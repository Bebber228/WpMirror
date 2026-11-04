// WpMirror: screen mirror for Windows Phone 8.1 (Lumia) over USB.
//
// Frame message (phone -> PC, bulk IN 0x84 of USB interface 2):
//   +0  uint32 magic 0x44555608     +4  uint32 type (2 = frame)     +8  uint32 total size
//   +24 uint32 bits per pixel (16)  +28 uint32 row stride in bytes
//   +32 uint16 phone screen width,  +34 uint16 phone screen height
//   +36 uint16 frame width,         +38 uint16 frame height
//   pixels start at (total size - stride * frame height), RGB565 little-endian when bpp = 16
// Touch message (PC -> phone): class request 0x03 + 192 bytes on bulk OUT 0x03.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WpMirror
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MirrorForm());
        }
    }

    // Draws the frame scaled to fit, with a selectable interpolation mode.
    internal sealed class FrameView : Control
    {
        public string Message = "";
        public InterpolationMode Interpolation = InterpolationMode.NearestNeighbor;
        private Image _image;

        public FrameView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            ForeColor = Color.Gainsboro;
        }

        public Image Image
        {
            get { return _image; }
            set { _image = value; Invalidate(); }
        }

        public RectangleF GetImageRect(Image img)
        {
            float s = Math.Min((float)ClientSize.Width / img.Width, (float)ClientSize.Height / img.Height);
            float w = img.Width * s;
            float h = img.Height * s;
            return new RectangleF((ClientSize.Width - w) / 2f, (ClientSize.Height - h) / 2f, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Image img = _image;
            if (img != null)
            {
                if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
                g.InterpolationMode = Interpolation;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, GetImageRect(img));
            }
            else if (!string.IsNullOrEmpty(Message))
            {
                using (var br = new SolidBrush(ForeColor))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(Message, Font, br, new RectangleF(12, 12, Math.Max(1, Width - 24), Math.Max(1, Height - 24)), sf);
                }
            }
        }
    }

    internal sealed class MirrorForm : Form
    {
        private const uint Magic = 0x44555608;
        private const int HeaderSize = 512;
        private const string Hint = "Choose your phone in the Device menu.";
        private const string WaitText = "Waiting for the first frame...\r\nIf the phone asks to allow screen projection, tap Yes.";

        // Pause between two connections to the phone, and the minimum time a connection stays open.
        // Opening and closing the projection too quickly can upset the phone.
        private const int CooldownMs = 3000;
        private const int MinSessionMs = 2500;

        // Touch message templates (stroke start, move, end).
        private static readonly byte[] TplDown = Hex("0856554403000000c0000000e204100084000000010000000000000001000000010010000200000000000000fe01c00320000000906e7704806e7704000000000000000001000001e20410000000000000008e4074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c000000403f00004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770446020000fe010000c003000058010000");
        private static readonly byte[] TplMove = Hex("0856554403000000c0000000e204100084000000010000000000000001000000010010000200000002000000fe019c0340000000906e7704806e7704000000000000000001000001e20410000000005006e78c4074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c00c2f9383f00004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770445020000fe0100009c03000058010000");
        private static readonly byte[] TplUp = Hex("0856554403000000c0000000e2041000840000000100000000000000010000000100100000000000020000004002000040000000906e7704806e7704000000000000000001000001e2041000000000000000000074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c000000000000004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770445020000400200000000000069010000");

        // One connection attempt to one phone. A new session never starts before the previous one has ended.
        private sealed class Session
        {
            public volatile bool Stop;
            public volatile bool GotFrame;
            public Thread Thread;
            public DeviceInfo Device;
            public long Bytes;
            public int Messages;
            public bool WarnedNoData;
            public bool WarnedDecode;
            public int Rejects;
        }

        private readonly FrameView _view;
        private readonly MenuStrip _menu;
        private readonly ToolStripMenuItem _mDevice;
        private readonly ToolStripMenuItem[] _miQuality = new ToolStripMenuItem[3];
        private readonly ToolStripMenuItem _miTop;
        private readonly ToolStripMenuItem _miTouch;
        private readonly ToolStripMenuItem _miAck;

        private readonly object _portLock = new object();
        private Port _port;
        private Session _session;   // the session the user currently wants (null = disconnected)
        private Session _last;      // the most recently started session (may still be shutting down)
        private DeviceInfo _current;
        private long _lastCloseTicks;

        private volatile string _statusText = "";
        private string _note = "";
        private DateTime _noteUntil = DateTime.MinValue;
        private int _fpsCount;
        private DateTime _fpsStart = DateTime.UtcNow;
        private double _fps;
        private int _pending;
        private volatile int _quality = 2;
        private int _logged;

        private volatile bool _touch;
        private volatile bool _ack;
        private int _rotate;
        private int _landscape = -1;   // orientation of the last shown picture: -1 unknown, 0 portrait, 1 landscape
        private long _lastKey;         // last seen frame format (to log changes)
        private volatile int _screenW = 720;
        private volatile int _screenH = 1280;

        private bool _down;
        private int _stroke;
        private DateTime _lastSend = DateTime.MinValue;
        private int _lastPx;
        private int _lastPy;

        private bool _sized;
        private Rectangle _savedBounds = Rectangle.Empty;

        private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory;

        public MirrorForm()
        {
            Text = "WpMirror";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(340, 200);

            _view = new FrameView();
            _view.Dock = DockStyle.Fill;
            _view.Message = Hint;

            _menu = new MenuStrip();

            // File
            var mFile = new ToolStripMenuItem("File");
            mFile.DropDownItems.Add(Item("Screenshot", Keys.Control | Keys.S, delegate { Screenshot(); }));
            mFile.DropDownItems.Add(Item("Copy frame to clipboard", Keys.Control | Keys.C, delegate { CopyFrame(); }));
            mFile.DropDownItems.Add(Item("Open screenshots folder", Keys.None, delegate { OpenFolder(); }));
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(Item("Exit", Keys.Control | Keys.Q, delegate { Close(); }));

            // Device (the list is rebuilt every time the menu opens)
            _mDevice = new ToolStripMenuItem("Device");
            _mDevice.DropDownItems.Add(new ToolStripMenuItem("Scanning..."));
            _mDevice.DropDownOpening += delegate { RebuildDeviceMenu(); };

            // View
            var mView = new ToolStripMenuItem("View");
            mView.DropDownItems.Add(Item("Rotate 90 degrees", Keys.Control | Keys.R, delegate { Rotate(); }));
            mView.DropDownItems.Add(Item("Fit window to frame", Keys.Control | Keys.D1, delegate { FitWindow(); }));
            _miTop = Item("Always on top", Keys.Control | Keys.T, delegate { ToggleTop(); });
            mView.DropDownItems.Add(_miTop);

            // Quality
            var mQuality = new ToolStripMenuItem("Quality");
            _miQuality[0] = Item("Low (fastest, soft picture)", Keys.None, delegate { ApplyQuality(0); });
            _miQuality[1] = Item("Medium (balanced)", Keys.None, delegate { ApplyQuality(1); });
            _miQuality[2] = Item("High (sharpest)", Keys.None, delegate { ApplyQuality(2); });
            foreach (ToolStripMenuItem mi in _miQuality) mQuality.DropDownItems.Add(mi);

            // Control
            var mCtl = new ToolStripMenuItem("Control");
            _miTouch = Item("Mouse touch (experimental)", Keys.F7, delegate { ToggleTouch(); });
            _miAck = Item("Acknowledge frames (experimental)", Keys.F8, delegate { ToggleAck(); });
            mCtl.DropDownItems.Add(_miTouch);
            mCtl.DropDownItems.Add(_miAck);

            // Help
            var mHelp = new ToolStripMenuItem("Help");
            mHelp.DropDownItems.Add(Item("How to use", Keys.F1, delegate { ShowHelp(); }));
            mHelp.DropDownItems.Add(Item("Save USB report (troubleshooting)", Keys.None, delegate { SaveReport(); }));

            _menu.Items.Add(mFile);
            _menu.Items.Add(_mDevice);
            _menu.Items.Add(mView);
            _menu.Items.Add(mQuality);
            _menu.Items.Add(mCtl);
            _menu.Items.Add(mHelp);

            // Add the fill control first, then the menu, so docking works as expected.
            Controls.Add(_view);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            _view.MouseDown += OnMouseDown;
            _view.MouseMove += OnMouseMove;
            _view.MouseUp += OnMouseUp;

            LoadSettings();
            ApplyQuality(_quality);
            FormClosing += delegate { SaveSettings(); Shutdown(); };
        }

        private static ToolStripMenuItem Item(string text, Keys keys, EventHandler handler)
        {
            var mi = new ToolStripMenuItem(text);
            if (keys != Keys.None) mi.ShortcutKeys = keys;
            mi.Click += handler;
            return mi;
        }

        // ---------- settings ----------
        private string IniPath { get { return Path.Combine(_baseDir, "wpmirror.ini"); } }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                int l = 0, t = 0, w = 0, h = 0;
                foreach (string line in File.ReadAllLines(IniPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    switch (k)
                    {
                        case "left": l = n; break;
                        case "top": t = n; break;
                        case "width": w = n; break;
                        case "height": h = n; break;
                        case "topmost": TopMost = n != 0; _miTop.Checked = TopMost; break;
                        case "rotate": _rotate = ((n % 4) + 4) % 4; break;
                        case "quality2": _quality = Math.Max(0, Math.Min(2, n)); break;
                    }
                }
                if (w > 100 && h > 100) _savedBounds = new Rectangle(l, t, w, h);
            }
            catch (Exception ex) { Log("settings: " + ex.Message); }
        }

        private void SaveSettings()
        {
            try
            {
                Rectangle b = _savedBounds;
                if (_sized) b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var sb = new StringBuilder();
                sb.AppendLine("left=" + b.Left);
                sb.AppendLine("top=" + b.Top);
                sb.AppendLine("width=" + b.Width);
                sb.AppendLine("height=" + b.Height);
                sb.AppendLine("topmost=" + (TopMost ? 1 : 0));
                sb.AppendLine("rotate=" + _rotate);
                sb.AppendLine("quality2=" + _quality);
                File.WriteAllText(IniPath, sb.ToString());
            }
            catch (Exception) { }
        }

        private void Log(string s)
        {
            if (_logged++ > 400) return;
            try { File.AppendAllText(Path.Combine(_baseDir, "wpmirror.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); }
            catch (IOException) { }
        }

        // ---------- device menu ----------
        private void RebuildDeviceMenu()
        {
            _mDevice.DropDownItems.Clear();
            List<DeviceInfo> list;
            try { list = Port.ListDevices(); }
            catch (Exception) { list = new List<DeviceInfo>(); }

            Session cur = _session;
            bool active = cur != null && !cur.Stop;

            if (list.Count == 0)
            {
                var none = new ToolStripMenuItem("(no Windows Phone connected)");
                none.Enabled = false;
                _mDevice.DropDownItems.Add(none);
            }
            foreach (DeviceInfo d in list)
            {
                DeviceInfo dev = d;
                var mi = new ToolStripMenuItem(dev.Name);
                mi.Checked = active && cur.Device.Instance == dev.Instance;
                mi.Click += delegate { SelectDevice(dev); };
                _mDevice.DropDownItems.Add(mi);
            }
            List<PendingDevice> pending;
            try { pending = Port.FindUnregistered(); }
            catch (Exception) { pending = new List<PendingDevice>(); }
            foreach (PendingDevice pd in pending)
            {
                PendingDevice dev2 = pd;
                var mi = new ToolStripMenuItem("Set up " + dev2.Name + " (one time, administrator)");
                mi.Click += delegate { SetupDevice(dev2); };
                _mDevice.DropDownItems.Add(mi);
            }
            _mDevice.DropDownItems.Add(new ToolStripSeparator());
            var disc = new ToolStripMenuItem("Disconnect");
            disc.Enabled = active;
            disc.Click += delegate { Disconnect(); };
            _mDevice.DropDownItems.Add(disc);
        }

        // Some phones are connected but Windows has not created an interface that programs can open.
        // This registers it once; Windows remembers the setting.
        private void SetupDevice(PendingDevice pd)
        {
            DialogResult r = MessageBox.Show(this,
                pd.Name + " is connected, but Windows has not created the screen projection interface for it.\r\n\r\n" +
                "WpMirror can register it now (one time, needs administrator rights). The phone's USB interface will restart for a moment.\r\n\r\nContinue?",
                "WpMirror", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            string err = Port.RegisterInterface(pd);
            Log("setup " + pd.InstanceId + " -> " + (err ?? "ok"));
            if (err != null) MessageBox.Show(this, err, "WpMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else MessageBox.Show(this, "Done. Wait a few seconds, then open the Device menu again and choose the phone.\r\nIf it is still missing, unplug and plug in the phone.", "WpMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SelectDevice(DeviceInfo dev)
        {
            Session cur = _session;
            // Choosing the phone that is already connecting or connected does nothing.
            if (cur != null && !cur.Stop && cur.Device.Instance == dev.Instance) return;

            Session prev = _last;
            if (cur != null) cur.Stop = true;

            ClearImage();
            _landscape = -1;
            var s = new Session();
            s.Device = dev;
            _session = s;
            _last = s;
            _current = dev;
            _fps = 0;
            _fpsCount = 0;
            _fpsStart = DateTime.UtcNow;
            EnsureStreamSize();

            bool closing = prev != null && prev.Thread != null && prev.Thread.IsAlive;
            _view.Message = closing ? "Closing the previous connection, please wait..." : WaitText;
            _statusText = "connecting";
            _view.Invalidate();
            UpdateTitle();

            s.Thread = new Thread(new ThreadStart(delegate { RunSession(s, prev); }));
            s.Thread.IsBackground = true;
            s.Thread.Start();
        }

        private void Disconnect()
        {
            Session cur = _session;
            if (cur != null) cur.Stop = true;
            _session = null;
            _current = null;
            _statusText = "";
            _landscape = -1;
            ClearImage();
            _view.Message = Hint;
            _view.Invalidate();
            UpdateTitle();
        }

        // Called when the program closes: ends the session and waits until the phone interface is released.
        private void Shutdown()
        {
            Session s = _last;
            if (s == null) return;
            s.Stop = true;
            if (s.Thread != null) s.Thread.Join(6000);
        }

        private void ClearImage()
        {
            Image old = _view.Image;
            _view.Image = null;
            if (old != null) old.Dispose();
        }

        private void EnsureStreamSize()
        {
            if (_sized) return;
            _sized = true;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            bool saved = false;
            if (_savedBounds.Width > 100 && _savedBounds.Height > 100)
            {
                foreach (Screen sc in Screen.AllScreens)
                {
                    if (sc.WorkingArea.IntersectsWith(_savedBounds)) { saved = true; break; }
                }
            }
            if (saved) Bounds = _savedBounds;
            else ClientSize = new Size(360, Math.Min(700, wa.Height - 100));

            Rectangle b = Bounds;
            if (b.Bottom > wa.Bottom) b.Y = Math.Max(wa.Top, wa.Bottom - b.Height);
            if (b.Right > wa.Right) b.X = Math.Max(wa.Left, wa.Right - b.Width);
            Bounds = b;
        }

        // ---------- status / title ----------
        private void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // Shows a status. If inView is true, the last frame is cleared and the text is drawn in the window.
        private void ShowStatus(Session s, string text, bool inView)
        {
            Post(delegate
            {
                if (s != _session) return;
                _statusText = text;
                if (inView)
                {
                    ClearImage();
                    _view.Message = text;
                    _view.Invalidate();
                }
                UpdateTitle();
            });
        }

        private void SetNote(string text)
        {
            _note = text;
            _noteUntil = DateTime.UtcNow.AddSeconds(6);
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            var sb = new StringBuilder("WpMirror");
            if (_current != null) sb.Append(" - ").Append(_current.Name);
            if (DateTime.UtcNow < _noteUntil) sb.Append(" - ").Append(_note);
            else
            {
                string st = _statusText;
                if (st.Length > 0 && st.IndexOf('\n') < 0) sb.Append(" - ").Append(st);
                if (_fps > 0 && _current != null) sb.Append(" - ").Append(_fps.ToString("0.0", CultureInfo.InvariantCulture)).Append(" fps");
            }
            if (_touch) sb.Append(" [touch]");
            if (_ack) sb.Append(" [ack]");
            Text = sb.ToString();
        }

        // ---------- session thread ----------
        private void RunSession(Session s, Session prev)
        {
            // Wait until the previous connection has been fully closed.
            if (prev != null && prev.Thread != null && prev.Thread != Thread.CurrentThread) prev.Thread.Join();

            // Pause between connections.
            while (!s.Stop)
            {
                long since = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastCloseTicks)) / TimeSpan.TicksPerMillisecond;
                if (since >= CooldownMs) break;
                Thread.Sleep(100);
            }

            if (!s.Stop)
            {
                ShowStatus(s, WaitText, true);
                ReadLoop(s);
            }
            Interlocked.Exchange(ref _lastCloseTicks, DateTime.UtcNow.Ticks);
        }

        private void SleepStop(Session s, int ms)
        {
            int waited = 0;
            while (!s.Stop && waited < ms) { Thread.Sleep(100); waited += 100; }
        }

        private void ReadLoop(Session s)
        {
            string path = s.Device.Path;
            while (!s.Stop)
            {
                Port p = Port.OpenPath(path);
                if (!p.IsOpen)
                {
                    ShowStatus(s, p.Error + "\r\nRetrying...", true);
                    Log(p.Error);
                    p.Dispose();
                    SleepStop(s, 2000);
                    // The device path can change after re-plugging: look it up again by instance id.
                    try
                    {
                        foreach (DeviceInfo d in Port.ListDevices())
                        {
                            if (d.Instance == s.Device.Instance) { path = d.Path; break; }
                        }
                    }
                    catch (Exception) { }
                    continue;
                }

                lock (_portLock) { _port = p; }
                p.SetTimeout(p.PipeIn, 250);
                p.SetTimeout(p.PipeOut, 200);
                DateTime opened = DateTime.UtcNow;
                bool started = p.ClassRequest(0x02);
                Log("interface " + p.InterfaceNumber + ", pipes in 0x" + p.PipeIn.ToString("x2") + " out 0x" + p.PipeOut.ToString("x2") + ", class request 0x02 -> " + started);

                ReadFrames(s, p);

                lock (_portLock) { _port = null; }

                // Never close the interface right after opening it.
                int left = MinSessionMs - (int)(DateTime.UtcNow - opened).TotalMilliseconds;
                if (left > 0) Thread.Sleep(left);
                p.AbortPipes(p.PipeIn, p.PipeOut);
                p.Dispose();
                Interlocked.Exchange(ref _lastCloseTicks, DateTime.UtcNow.Ticks);

                if (!s.Stop)
                {
                    ShowStatus(s, "Connection lost.\r\nReconnecting...", true);
                    SleepStop(s, CooldownMs);
                }
            }
        }

        private void CheckSilence(Session s, DateTime t0)
        {
            if (s.GotFrame) return;
            double el = (DateTime.UtcNow - t0).TotalSeconds;
            if (s.Bytes == 0 && el > 8 && !s.WarnedNoData)
            {
                s.WarnedNoData = true;
                Log("no data from the phone after 8 s");
                ShowStatus(s, "No data from the phone yet.\r\nUnlock the phone and tap Yes if it asks to allow screen projection.", true);
            }
            else if (s.Bytes > 0 && el > 6 && !s.WarnedDecode)
            {
                s.WarnedDecode = true;
                Log("data received but no frame could be decoded");
                ShowStatus(s, "The phone sends data that this version cannot show yet.\r\nDetails are in wpmirror.log.", true);
            }
        }

        private void ReadFrames(Session s, Port p)
        {
            var chunk = new byte[65536];
            var acc = new byte[8 * 1024 * 1024];
            int len = 0;
            DateTime t0 = DateTime.UtcNow;

            while (!s.Stop)
            {
                uint n;
                bool ok = Native.WinUsb_ReadPipe(p.Iface, p.PipeIn, chunk, (uint)chunk.Length, out n, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 121) { CheckSilence(s, t0); continue; } // timeout: no data yet
                    Log("read error " + err);
                    return;
                }
                if (n == 0) { CheckSilence(s, t0); continue; }
                s.Bytes += n;
                if (len + (int)n > acc.Length) len = 0;
                Buffer.BlockCopy(chunk, 0, acc, len, (int)n);
                len += (int)n;

                int off = 0;
                while (true)
                {
                    if (len - off < 16) break;
                    if (BitConverter.ToUInt32(acc, off) != Magic)
                    {
                        int j = off + 1;
                        bool found = false;
                        for (; j <= len - 4; j++)
                        {
                            if (BitConverter.ToUInt32(acc, j) == Magic) { found = true; break; }
                        }
                        if (!found) { off = Math.Max(off, len - 3); break; }
                        off = j;
                        continue;
                    }
                    uint total = BitConverter.ToUInt32(acc, off + 8);
                    if (total < 32 || total > acc.Length) { off += 1; continue; }
                    if (len - off < (int)total) break;
                    HandleMessage(s, acc, off, (int)total);
                    off += (int)total;
                }
                if (off > 0)
                {
                    Buffer.BlockCopy(acc, off, acc, 0, len - off);
                    len -= off;
                }
                CheckSilence(s, t0);
            }
        }

        private static string HexDump(byte[] b, int off, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && off + i < b.Length; i++) sb.Append(b[off + i].ToString("x2"));
            return sb.ToString();
        }

        private void Reject(Session s, string why)
        {
            if (s.Rejects++ < 3) Log("frame rejected: " + why);
        }

        private void HandleMessage(Session s, byte[] buf, int off, int total)
        {
            uint type = BitConverter.ToUInt32(buf, off + 4);
            if (s.Messages < 12)
            {
                s.Messages++;
                Log("message type " + type + " size " + total + " head " + HexDump(buf, off, 64));
            }
            if (type != 2) return;
            if (total < 40) return;

            uint bpp = BitConverter.ToUInt32(buf, off + 24);
            int stride = (int)BitConverter.ToUInt32(buf, off + 28);
            int sw = BitConverter.ToUInt16(buf, off + 32);
            int sh = BitConverter.ToUInt16(buf, off + 34);
            int w = BitConverter.ToUInt16(buf, off + 36);
            int h = BitConverter.ToUInt16(buf, off + 38);
            long key = ((long)sw << 48) | ((long)sh << 32) | ((long)w << 16) | (long)h;
            if (key != _lastKey)
            {
                _lastKey = key;
                Log("frame format: screen " + sw + "x" + sh + " frame " + w + "x" + h + " head " + HexDump(buf, off, 64));
            }
            if ((bpp != 16 && bpp != 24 && bpp != 32) || w <= 0 || h <= 0)
            {
                Reject(s, "unsupported format: bpp " + bpp + " size " + w + "x" + h);
                return;
            }
            int bytesPP = (int)bpp / 8;
            // Some phones (for example the Lumia 532) send 0 here: use a tightly packed row then.
            if (stride < w * bytesPP) stride = w * bytesPP;
            long dataOff = (long)total - (long)stride * h;
            if (dataOff < 32 || dataOff > 4096)
            {
                Reject(s, "unexpected layout: total " + total + " stride " + stride + " height " + h);
                return;
            }

            if (sw > 0 && sh > 0) { _screenW = sw; _screenH = sh; }

            if (_ack)
            {
                lock (_portLock)
                {
                    if (_port != null && _port.IsOpen) _port.ClassRequest(0x03);
                }
            }

            // Drop frames when the window cannot keep up.
            if (Interlocked.CompareExchange(ref _pending, 0, 0) >= 2) return;

            // Low quality decodes every second pixel and row: much less work, softer picture.
            int step = _quality == 0 ? 2 : 1;
            int ow = Math.Max(1, w / step);
            int oh = Math.Max(1, h / step);

            Bitmap bmp = new Bitmap(ow, oh, PixelFormat.Format32bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new int[ow];
                int start = off + (int)dataOff;
                for (int y = 0; y < oh; y++)
                {
                    int rowStart = start + (y * step) * stride;
                    for (int x = 0; x < ow; x++)
                    {
                        int q = rowStart + x * step * bytesPP;
                        if (bpp == 16)
                        {
                            int v = buf[q] | (buf[q + 1] << 8);
                            int r = (v >> 11) & 31;
                            int g = (v >> 5) & 63;
                            int b = v & 31;
                            row[x] = unchecked((int)0xFF000000) | (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
                        }
                        else
                        {
                            row[x] = unchecked((int)0xFF000000) | (buf[q + 2] << 16) | (buf[q + 1] << 8) | buf[q];
                        }
                    }
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), ow);
                }
            }
            finally { bmp.UnlockBits(bd); }

            switch (_rotate)
            {
                case 1: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 2: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 3: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }

            if (IsDisposed || !IsHandleCreated) { bmp.Dispose(); return; }
            Interlocked.Increment(ref _pending);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (s != _session) { bmp.Dispose(); return; }
                        Image old = _view.Image;
                        _view.Image = bmp;
                        if (old != null) old.Dispose();
                        int land = bmp.Width > bmp.Height ? 1 : 0;
                        if (land != _landscape)
                        {
                            _landscape = land;
                            Log("orientation: " + (land == 1 ? "landscape" : "portrait") + ", picture " + bmp.Width + "x" + bmp.Height);
                            AdaptWindow(bmp);
                        }
                        s.GotFrame = true;
                        _statusText = "streaming";
                        _fpsCount++;
                        double el = (DateTime.UtcNow - _fpsStart).TotalSeconds;
                        if (el >= 1.0)
                        {
                            _fps = _fpsCount / el;
                            _fpsCount = 0;
                            _fpsStart = DateTime.UtcNow;
                        }
                        UpdateTitle();
                    }
                    finally { Interlocked.Decrement(ref _pending); }
                }));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pending);
                bmp.Dispose();
            }
        }

        // ---------- menu actions ----------
        private void ApplyQuality(int q)
        {
            _quality = q;
            switch (q)
            {
                case 0: _view.Interpolation = InterpolationMode.Bilinear; break;
                case 1: _view.Interpolation = InterpolationMode.HighQualityBilinear; break;
                default: _view.Interpolation = InterpolationMode.NearestNeighbor; break;
            }
            for (int i = 0; i < _miQuality.Length; i++) _miQuality[i].Checked = (i == q);
            _view.Invalidate();
        }

        // Turns the window to match the picture: portrait <-> landscape.
        private void AdaptWindow(Image img)
        {
            if (WindowState != FormWindowState.Normal) return;
            int menuH = _menu.Height;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height - menuH;
            bool imgLand = img.Width > img.Height;
            if ((cw > ch) == imgLand) return;

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            double a = (double)img.Width / img.Height;
            int longSide = Math.Max(cw, ch);
            int w, h;
            if (imgLand) { w = longSide; h = (int)(w / a); }
            else { h = longSide; w = (int)(h * a); }

            int maxW = wa.Width - 40;
            int maxH = wa.Height - 80 - menuH;
            double sc = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
            w = Math.Max(160, (int)(w * sc));
            h = Math.Max(160, (int)(h * sc));
            ClientSize = new Size(w, h + menuH);

            Rectangle b = Bounds;
            if (b.Right > wa.Right) b.X = Math.Max(wa.Left, wa.Right - b.Width);
            if (b.Bottom > wa.Bottom) b.Y = Math.Max(wa.Top, wa.Bottom - b.Height);
            if (b.X < wa.Left) b.X = wa.Left;
            if (b.Y < wa.Top) b.Y = wa.Top;
            Bounds = b;
        }

        private void ToggleTop() { TopMost = !TopMost; _miTop.Checked = TopMost; }

        private void ToggleTouch()
        {
            _touch = !_touch;
            _miTouch.Checked = _touch;
            if (!_touch) _down = false;
            UpdateTitle();
        }

        private void ToggleAck() { _ack = !_ack; _miAck.Checked = _ack; UpdateTitle(); }

        private void Rotate() { _rotate = (_rotate + 1) % 4; }

        private void FitWindow()
        {
            Image img = _view.Image;
            if (img == null) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(img.Width, wa.Width - 40);
            int h = Math.Min(img.Height + _menu.Height, wa.Height - 80);
            ClientSize = new Size(w, h);
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. Unlock the phone and pick it in the Device menu.\r\n" +
                "2. If the phone asks to allow screen projection, tap Yes.\r\n\r\n" +
                "Ctrl+S  screenshot\r\n" +
                "Ctrl+C  copy frame\r\n" +
                "Ctrl+R  rotate 90 degrees\r\n" +
                "Ctrl+1  fit window to frame\r\n" +
                "Ctrl+T  always on top\r\n" +
                "F7      mouse touch (experimental)\r\n" +
                "F8      acknowledge frames (experimental)\r\n\r\n" +
                "Quality presets change only how the picture is processed and scaled on the PC. " +
                "The phone always sends the same stream.\r\n" +
                "Screenshots are saved to Pictures\\WpMirror.",
                "WpMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveReport()
        {
            try
            {
                string path = Path.Combine(_baseDir, "usbreport.txt");
                File.WriteAllText(path, Port.Report());
                SetNote("saved " + path);
                MessageBox.Show(this, "Report saved to:\r\n" + path, "WpMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { SetNote("cannot save report: " + ex.Message); }
        }

        private string PicturesDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WpMirror");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void OpenFolder()
        {
            try { Process.Start("explorer.exe", PicturesDir()); }
            catch (Exception ex) { SetNote("cannot open folder: " + ex.Message); }
        }

        private void Screenshot()
        {
            Image img = _view.Image;
            if (img == null) { SetNote("no frame yet"); return; }
            try
            {
                string path = Path.Combine(PicturesDir(), "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                using (Bitmap copy = new Bitmap(img)) copy.Save(path, ImageFormat.Png);
                SetNote("saved " + path);
            }
            catch (Exception ex) { SetNote("cannot save: " + ex.Message); }
        }

        private void CopyFrame()
        {
            Image img = _view.Image;
            if (img == null) { SetNote("no frame yet"); return; }
            try { Clipboard.SetImage(new Bitmap(img)); SetNote("frame copied to clipboard"); }
            catch (Exception ex) { SetNote("cannot copy: " + ex.Message); }
        }

        // ---------- mouse touch (experimental) ----------
        private bool MapMouse(Point m, out int px, out int py)
        {
            px = 0; py = 0;
            Image img = _view.Image;
            if (img == null) return false;
            int iw = img.Width, ih = img.Height;
            RectangleF r = _view.GetImageRect(img);
            double scale = r.Width / iw;
            if (scale <= 0) return false;
            double ix = (m.X - r.X) / scale;
            double iy = (m.Y - r.Y) / scale;
            ix = Math.Max(0, Math.Min(iw - 1, ix));
            iy = Math.Max(0, Math.Min(ih - 1, iy));

            // Size of the picture before manual rotation.
            int w = (_rotate % 2 == 0) ? iw : ih;
            int h = (_rotate % 2 == 0) ? ih : iw;
            double fx, fy;
            switch (_rotate)
            {
                case 1: fx = iy; fy = h - 1 - ix; break;
                case 2: fx = w - 1 - ix; fy = h - 1 - iy; break;
                case 3: fx = w - 1 - iy; fy = ix; break;
                default: fx = ix; fy = iy; break;
            }
            px = (int)Math.Round(fx * _screenW / w);
            py = (int)Math.Round(fy * _screenH / h);
            return true;
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (!_touch || e.Button != MouseButtons.Left) return;
            int px, py;
            if (!MapMouse(e.Location, out px, out py)) return;
            _stroke++;
            _down = true;
            _lastPx = px; _lastPy = py;
            SendTouch(0, px, py);
            _lastSend = DateTime.UtcNow;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_touch || !_down) return;
            if ((DateTime.UtcNow - _lastSend).TotalMilliseconds < 15) return;
            int px, py;
            if (!MapMouse(e.Location, out px, out py)) return;
            if (px == _lastPx && py == _lastPy) return;
            _lastPx = px; _lastPy = py;
            SendTouch(1, px, py);
            _lastSend = DateTime.UtcNow;
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_touch || !_down || e.Button != MouseButtons.Left) return;
            _down = false;
            int px, py;
            if (!MapMouse(e.Location, out px, out py)) { px = _lastPx; py = _lastPy; }
            SendTouch(2, px, py);
        }

        private void SendTouch(int kind, int x, int y)
        {
            x = Math.Max(0, Math.Min(65535, x));
            y = Math.Max(0, Math.Min(65535, y));
            byte[] t = (byte[])(kind == 0 ? TplDown : (kind == 2 ? TplUp : TplMove)).Clone();
            Put(t, 32, 0x00100000u | (uint)(_stroke & 0xFFFF));
            Put(t, 36, kind == 2 ? 0u : 2u);
            Put(t, 40, kind == 0 ? 0u : 2u);
            Put(t, 44, ((uint)y << 16) | (uint)x);
            Put(t, 180, (uint)x);
            Put(t, 184, (uint)y);
            Put(t, 188, (uint)(344 + (int)Math.Round((x - 510) / 3.9)));
            lock (_portLock)
            {
                if (_port == null || !_port.IsOpen) return;
                _port.ClassRequest(0x03);
                bool ok = _port.Write(_port.PipeOut, t);
                if (!ok) Log("touch write failed, error " + Marshal.GetLastWin32Error());
                else Log("touch kind " + kind + " x " + x + " y " + y);
            }
        }

        private static void Put(byte[] b, int off, uint v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }

        private static byte[] Hex(string s)
        {
            var r = new byte[s.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return r;
        }
    }
}
