// WpMirror: screen mirror for Windows Phone 8.1 / Windows 10 Mobile (Lumia) over USB.
//
// Frame message (phone -> PC, bulk IN 0x84 of USB interface 2):
//   +0  uint32 magic 0x44555608     +4  uint32 type (2 = frame)     +8  uint32 total size
//   +24 uint32 bits per pixel (16)  +28 uint32 row stride in bytes
//   +32 uint16 phone screen width,  +34 uint16 phone screen height
//   +36 uint16 frame width,         +38 uint16 frame height
//   pixels start at (total size - stride * frame height), RGB565 little-endian when bpp = 16
//
// Input message (PC -> phone): 192 bytes on bulk OUT 0x03, type 3.
//   +24 uint32 input kind: 0 = touch, 1 = hardware button
//   touch:  +32 stroke id, +36/+40 contact state, +44 (y << 16 | x)
//   button: +28 uint32 button code (0 = Back, 1 = Start, 2 = Search), +32 byte 1 = pressed, 0 = released
// Class request 0x03 (interface 2): sent after every input message and every 2.5 s as keep-alive,
// exactly like Project My Screen does. 0x02 starts the projection, 0x01 stops it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
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

    // One decoded picture: 32-bit pixels, top-down rows.
    internal sealed class Frame
    {
        public int W;
        public int H;
        public int[] Px = new int[0];

        public void Ensure(int w, int h)
        {
            W = w; H = h;
            if (Px.Length < w * h) Px = new int[w * h];
        }

        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < H; y++)
                    Marshal.Copy(Px, y * W, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), W);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }

    // Draws the frame scaled to fit. Uses GDI StretchDIBits: much faster than GDI+ DrawImage.
    internal sealed class FrameView : Control
    {
        public string Message = "";
        public bool Smooth;          // true = HALFTONE (smooth), false = COLORONCOLOR (fast)
        public Frame Frame;          // touched only on the UI thread

        public FrameView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            BackColor = Color.Black;
            ForeColor = Color.Gainsboro;
        }

        public Rectangle GetImageRect(int iw, int ih)
        {
            if (iw <= 0 || ih <= 0) return Rectangle.Empty;
            double s = Math.Min((double)ClientSize.Width / iw, (double)ClientSize.Height / ih);
            int w = Math.Max(1, (int)Math.Round(iw * s));
            int h = Math.Max(1, (int)Math.Round(ih * s));
            return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Frame f = Frame;
            if (f == null || f.W <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                g.Clear(BackColor);
                if (!string.IsNullOrEmpty(Message))
                {
                    using (var br = new SolidBrush(ForeColor))
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(Message, Font, br, new RectangleF(12, 12, Math.Max(1, Width - 24), Math.Max(1, Height - 24)), sf);
                    }
                }
                return;
            }

            Rectangle r = GetImageRect(f.W, f.H);
            // Black bars only around the picture, so nothing flickers.
            using (var br = new SolidBrush(BackColor))
            {
                if (r.Top > 0) g.FillRectangle(br, 0, 0, ClientSize.Width, r.Top);
                if (r.Bottom < ClientSize.Height) g.FillRectangle(br, 0, r.Bottom, ClientSize.Width, ClientSize.Height - r.Bottom);
                if (r.Left > 0) g.FillRectangle(br, 0, r.Top, r.Left, r.Height);
                if (r.Right < ClientSize.Width) g.FillRectangle(br, r.Right, r.Top, ClientSize.Width - r.Right, r.Height);
            }

            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = 40;
            bi.biWidth = f.W;
            bi.biHeight = -f.H;   // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr hdc = g.GetHdc();
            try
            {
                Native.SetStretchBltMode(hdc, Smooth ? 4 : 3);
                if (Smooth) Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                Native.StretchDIBits(hdc, r.X, r.Y, r.Width, r.Height, 0, 0, f.W, f.H, f.Px, ref bi, 0, 0x00CC0020);
            }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    // Bar with the three phone buttons. Press = button down, release = button up (so long presses work).
    internal sealed class NavBar : Control
    {
        public event Action<int, bool> ButtonChanged;   // index 0 = Back, 1 = Start, 2 = Search
        private readonly Image[] _icons;
        private static readonly string[] Fallback = { "<", "Win", "Search" };
        private int _pressed = -1;

        public NavBar(Image[] icons)
        {
            _icons = icons;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            ForeColor = Color.White;
            Height = 60;
        }

        private Rectangle Slot(int i)
        {
            int x0 = i * Width / 3, x1 = (i + 1) * Width / 3;
            return new Rectangle(x0, 0, x1 - x0, Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            for (int i = 0; i < 3; i++)
            {
                Rectangle r = Slot(i);
                if (i == _pressed)
                {
                    using (var br = new SolidBrush(Color.FromArgb(60, 60, 60))) g.FillRectangle(br, r);
                }
                int sz = Math.Max(8, Math.Min(48, Height - 10));
                var ir = new Rectangle(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz);
                if (_icons[i] != null) g.DrawImage(_icons[i], ir);
                else
                {
                    using (var br = new SolidBrush(ForeColor))
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(Fallback[i], Font, br, r, sf);
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _pressed >= 0 || Width <= 0) return;
            _pressed = Math.Max(0, Math.Min(2, e.X * 3 / Width));
            Invalidate();
            if (ButtonChanged != null) ButtonChanged(_pressed, true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) Release();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) Release();
        }

        private void Release()
        {
            if (_pressed < 0) return;
            int i = _pressed;
            _pressed = -1;
            Invalidate();
            if (ButtonChanged != null) ButtonChanged(i, false);
        }
    }

    internal sealed class MirrorForm : Form
    {
        private const uint Magic = 0x44555608;
        private const string Hint = "Choose your phone in the Device menu.";
        private const string WaitText = "Waiting for the first frame...\r\nIf the phone asks to allow screen projection, tap Yes.";

        // Pause between two connections to the phone, and the minimum time a connection stays open.
        // Opening and closing the projection too quickly can upset the phone.
        private const int CooldownMs = 3000;
        private const int MinSessionMs = 2500;

        // Several read requests stay queued on the USB pipe, so the phone never waits for the PC.
        private const int ReadSlots = 6;
        private const int ReadSize = 256 * 1024;

        // While the left mouse button is held without moving, the contact is repeated this often,
        // so the phone sees a finger that stays on the screen (press and hold).
        private const int HoldRepeatMs = 30;

        // Touch message templates (stroke start, move, end).
        private static readonly byte[] TplDown = Hex("0856554403000000c0000000e204100084000000010000000000000001000000010010000200000000000000fe01c00320000000906e7704806e7704000000000000000001000001e20410000000000000008e4074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c000000403f00004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770446020000fe010000c003000058010000");
        private static readonly byte[] TplMove = Hex("0856554403000000c0000000e204100084000000010000000000000001000000010010000200000002000000fe019c0340000000906e7704806e7704000000000000000001000001e20410000000005006e78c4074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c00c2f9383f00004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770445020000fe0100009c03000058010000");
        private static readonly byte[] TplUp = Hex("0856554403000000c0000000e2041000840000000100000000000000010000000100100000000000020000004002000040000000906e7704806e7704000000000000000001000001e2041000000000000000000074fa2f01775b3c00906e7704580e770400000000a4fa2f01705a3c000000000000004044601077040000a044c0fa2f01806e7704d5000000190000009a01000061010000d0fa2f018e248b6ec8fa2f01c0fa2f0100000000806e770445020000400200000000000069010000");

        // Hardware button messages, taken byte for byte from Project My Screen (button pressed / released).
        private static readonly byte[] TplKeyDown = Hex("0856554403000000c0000000a0b1946e1a0b07001a0b07000100000001000000019385001a0b0700200000001a0b070001000102200000001a0b0700f9938500000000001a0b0700606db8030000000034024b0140000000706db803606db8030000000001000000010000011a0b07005cfa6f00435ae276d0531b0100000000f6c301d9706db803606db8030000000036010000760100003c104577000000000000000020fa6f009323496f1cfc6f0050f8e276f25286affeffffff88fa6f00");
        private static readonly byte[] TplKeyUp = Hex("0856554403000000c0000000d0531b01b0040000b01fe276010000000100000000fb6f00706db803606db8030000000040092f76009a19011a0b0700000000e863f093402cfa6f00775b8500706db803200fb803000000005cfa6f00705a850032387f3f000040442811b8030000a04478fa6f00606db803d5000000190000009a01000061010000706db80388fa6f00a524496f0000000000000000606db803470200007a010000fc0400003601000076010000a8fa6f00d827496f1a0b0700");

        private static readonly string[] KeyNames = { "Back", "Start", "Search" };

        // RGB565 -> 32-bit colour lookup table.
        private static readonly int[] Lut565 = BuildLut();

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
            public Frame Back = new Frame();   // owned by the reader thread
        }

        private readonly FrameView _view;
        private readonly NavBar _nav;
        private readonly MenuStrip _menu;
        private readonly ToolStripMenuItem _mDevice;
        private readonly ToolStripMenuItem[] _miQuality = new ToolStripMenuItem[3];
        private readonly ToolStripMenuItem _miTop;
        private readonly ToolStripMenuItem _miTouch;
        private readonly ToolStripMenuItem _miNav;
        private readonly System.Windows.Forms.Timer _holdTimer;

        private readonly object _portLock = new object();
        private Port _port;
        private Session _session;   // the session the user currently wants (null = disconnected)
        private Session _last;      // the most recently started session (may still be shutting down)
        private DeviceInfo _current;
        private long _lastCloseTicks;

        // Frame hand-over between the reader thread and the window (triple buffering).
        private readonly object _frameLock = new object();
        private Frame _ready;
        private bool _hasNew;
        private int _posted;
        private Frame _spare = new Frame();

        // Outgoing input messages (touch, buttons) are written by their own thread.
        private readonly object _outLock = new object();
        private readonly Queue<byte[]> _outQ = new Queue<byte[]>();
        private volatile bool _closing;

        private volatile string _statusText = "";
        private string _note = "";
        private DateTime _noteUntil = DateTime.MinValue;
        private int _fpsShown;
        private int _fpsRx;
        private DateTime _fpsStart = DateTime.UtcNow;
        private double _fps;
        private double _fpsPhone;
        private volatile int _quality = 2;
        private int _logged;

        private volatile bool _touch = true;
        private bool _navVisible = true;
        private bool _rawIo = true;
        private readonly int[] _keyCodes = { 0, 1, 2 };
        private readonly bool[] _keyDown = new bool[3];
        private volatile int _rotate;
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
            KeyPreview = true;
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (st != null) Icon = new Icon(st, 48, 48);
            }
            catch (Exception) { }

            _view = new FrameView();
            _view.Dock = DockStyle.Fill;
            _view.Message = Hint;

            _nav = new NavBar(new Image[] { LoadIcon("back.png"), LoadIcon("windows.png"), LoadIcon("search.png") });
            _nav.Dock = DockStyle.Bottom;
            _nav.ButtonChanged += delegate(int i, bool down) { SetKey(i, down); };

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
            _miQuality[0] = Item("Low (half resolution, least CPU)", Keys.None, delegate { ApplyQuality(0); });
            _miQuality[1] = Item("Medium (full resolution, fast scaling)", Keys.None, delegate { ApplyQuality(1); });
            _miQuality[2] = Item("High (full resolution, smooth scaling)", Keys.None, delegate { ApplyQuality(2); });
            foreach (ToolStripMenuItem mi in _miQuality) mQuality.DropDownItems.Add(mi);

            // Control
            var mCtl = new ToolStripMenuItem("Control");
            _miTouch = Item("Mouse touch", Keys.F7, delegate { ToggleTouch(); });
            _miNav = Item("Phone buttons", Keys.F6, delegate { ToggleNav(); });
            mCtl.DropDownItems.Add(_miTouch);
            mCtl.DropDownItems.Add(_miNav);
            mCtl.DropDownItems.Add(new ToolStripSeparator());
            mCtl.DropDownItems.Add(TapItem("Back", "Backspace", 0));
            mCtl.DropDownItems.Add(TapItem("Start", "Home", 1));
            mCtl.DropDownItems.Add(TapItem("Search", "F3", 2));

            // Help
            var mHelp = new ToolStripMenuItem("Help");
            mHelp.DropDownItems.Add(Item("Save USB report (troubleshooting)", Keys.None, delegate { SaveReport(); }));

            _menu.Items.Add(mFile);
            _menu.Items.Add(_mDevice);
            _menu.Items.Add(mView);
            _menu.Items.Add(mQuality);
            _menu.Items.Add(mCtl);

            // Fill control first, then the bottom bar, then the menu, so docking works as expected.
            Controls.Add(_view);
            Controls.Add(_nav);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            _view.MouseDown += OnMouseDown;
            _view.MouseMove += OnMouseMove;
            _view.MouseUp += OnMouseUp;
            _view.MouseCaptureChanged += delegate { if (_down && !_view.Capture) EndTouch(_lastPx, _lastPy); };

            _holdTimer = new System.Windows.Forms.Timer();
            _holdTimer.Interval = 15;
            _holdTimer.Tick += delegate { HoldTick(); };

            LoadSettings();
            ApplyQuality(_quality);
            _miTouch.Checked = _touch;
            _miNav.Checked = _navVisible;
            _nav.Visible = _navVisible;

            var writer = new Thread(new ThreadStart(WriterLoop));
            writer.IsBackground = true;
            writer.Start();

            FormClosing += delegate { SaveSettings(); Shutdown(); };
        }

        private static ToolStripMenuItem Item(string text, Keys keys, EventHandler handler)
        {
            var mi = new ToolStripMenuItem(text);
            if (keys != Keys.None) mi.ShortcutKeys = keys;
            mi.Click += handler;
            return mi;
        }

        // Menu item that taps a phone button. The key is only shown as text: the real key handling is in OnKeyDown,
        // so that holding the key holds the phone button.
        private ToolStripMenuItem TapItem(string text, string key, int index)
        {
            var mi = new ToolStripMenuItem(text);
            mi.ShortcutKeyDisplayString = key;
            mi.Click += delegate { TapKey(index); };
            return mi;
        }

        private Image LoadIcon(string name)
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (st != null) using (Image img = Image.FromStream(st)) return new Bitmap(img);
                }
            }
            catch (Exception) { }
            try
            {
                string path = Path.Combine(Path.Combine(_baseDir, "icons"), name);
                if (File.Exists(path)) using (Image img = Image.FromFile(path)) return new Bitmap(img);
            }
            catch (Exception) { }
            return null;
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
                        case "touch": _touch = n != 0; break;
                        case "buttons": _navVisible = n != 0; break;
                        case "rawio": _rawIo = n != 0; break;
                        case "key_back": _keyCodes[0] = n; break;
                        case "key_start": _keyCodes[1] = n; break;
                        case "key_search": _keyCodes[2] = n; break;
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
                sb.AppendLine("touch=" + (_touch ? 1 : 0));
                sb.AppendLine("buttons=" + (_navVisible ? 1 : 0));
                sb.AppendLine("rawio=" + (_rawIo ? 1 : 0));
                sb.AppendLine("key_back=" + _keyCodes[0]);
                sb.AppendLine("key_start=" + _keyCodes[1]);
                sb.AppendLine("key_search=" + _keyCodes[2]);
                File.WriteAllText(IniPath, sb.ToString());
            }
            catch (Exception) { }
        }

        private void Log(string s)
        {
            if (Interlocked.Increment(ref _logged) > 400) return;
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
            _fpsPhone = 0;
            _fpsShown = 0;
            Interlocked.Exchange(ref _fpsRx, 0);
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
            ReleaseAllInput();
            _closing = true;
            lock (_outLock) Monitor.PulseAll(_outLock);
            Session s = _last;
            if (s == null) return;
            s.Stop = true;
            if (s.Thread != null) s.Thread.Join(6000);
        }

        private void ClearImage()
        {
            _view.Frame = null;
            lock (_frameLock) { _hasNew = false; }
            _view.Invalidate();
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

        private int ChromeHeight { get { return _menu.Height + (_nav.Visible ? _nav.Height : 0); } }

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
                if (_current != null && (_fps > 0 || _fpsPhone > 0))
                {
                    sb.Append(" - ").Append(_fps.ToString("0.0", CultureInfo.InvariantCulture)).Append(" fps");
                    if (_fpsPhone - _fps > 1.0) sb.Append(" (phone ").Append(_fpsPhone.ToString("0.0", CultureInfo.InvariantCulture)).Append(")");
                }
            }
            if (_touch) sb.Append(" [touch]");
            string t = sb.ToString();
            if (Text != t) Text = t;
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

                bool rawOk;
                int readSize = p.ConfigureIn(_rawIo, ReadSize, out rawOk);
                p.SetTimeout(p.PipeOut, 200);
                DateTime opened = DateTime.UtcNow;
                bool started = p.ClassRequest(0x02);
                Log("interface " + p.InterfaceNumber + ", pipes in 0x" + p.PipeIn.ToString("x2") + " out 0x" + p.PipeOut.ToString("x2")
                    + ", packet " + p.InPacketSize + ", read " + readSize + " x " + ReadSlots + ", raw io " + rawOk
                    + ", class request 0x02 -> " + started);
                lock (_portLock) { _port = p; }

                ReadFrames(s, p, readSize);

                lock (_portLock) { _port = null; }

                // Never close the interface right after opening it.
                int left = MinSessionMs - (int)(DateTime.UtcNow - opened).TotalMilliseconds;
                if (left > 0) Thread.Sleep(left);
                if (s.Stop) p.ClassRequest(0x01);   // tell the phone to stop projecting, like Project My Screen
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

        // Keeps several read requests queued on the pipe. While the program works on one block,
        // the next ones are already being filled, so the phone never has to wait.
        private void ReadFrames(Session s, Port p, int size)
        {
            int ovSize = IntPtr.Size == 8 ? 32 : 20;
            int evOff = IntPtr.Size == 8 ? 24 : 16;
            var bufs = new IntPtr[ReadSlots];
            var ovs = new IntPtr[ReadSlots];
            var evs = new IntPtr[ReadSlots];
            var pending = new bool[ReadSlots];
            var acc = new byte[16 * 1024 * 1024];
            int len = 0;
            DateTime t0 = DateTime.UtcNow;

            try
            {
                for (int i = 0; i < ReadSlots; i++)
                {
                    bufs[i] = Marshal.AllocHGlobal(size);
                    ovs[i] = Marshal.AllocHGlobal(ovSize);
                    evs[i] = Native.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
                    if (evs[i] == IntPtr.Zero) { Log("CreateEvent failed"); return; }
                }
                for (int i = 0; i < ReadSlots; i++)
                {
                    if (!Submit(p, bufs[i], size, ovs[i], ovSize, evOff, evs[i])) return;
                    pending[i] = true;
                }

                int cur = 0;
                while (!s.Stop)
                {
                    uint w = Native.WaitForSingleObject(evs[cur], 250);
                    if (w == 0x102) { CheckSilence(s, t0); continue; }   // WAIT_TIMEOUT: nothing yet
                    if (w != 0) { Log("wait failed " + Marshal.GetLastWin32Error()); return; }

                    uint n;
                    bool ok = Native.WinUsb_GetOverlappedResult(p.Iface, ovs[cur], out n, false);
                    pending[cur] = false;
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 995) Log("read error " + err);
                        return;
                    }
                    if (n > 0)
                    {
                        s.Bytes += n;
                        if (len + (int)n > acc.Length) len = 0;
                        Marshal.Copy(bufs[cur], acc, len, (int)n);
                        len += (int)n;
                        len = Parse(s, acc, len);
                    }
                    CheckSilence(s, t0);

                    if (!Submit(p, bufs[cur], size, ovs[cur], ovSize, evOff, evs[cur])) return;
                    pending[cur] = true;
                    cur = (cur + 1) % ReadSlots;
                }
            }
            finally
            {
                // Cancel what is still queued and wait for it, before the memory is freed.
                p.AbortPipes(p.PipeIn);
                for (int i = 0; i < ReadSlots; i++)
                {
                    if (pending[i] && evs[i] != IntPtr.Zero && Native.WaitForSingleObject(evs[i], 2000) == 0) pending[i] = false;
                }
                for (int i = 0; i < ReadSlots; i++)
                {
                    if (pending[i]) continue;   // still in use by the driver: leak it rather than crash
                    if (bufs[i] != IntPtr.Zero) Marshal.FreeHGlobal(bufs[i]);
                    if (ovs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ovs[i]);
                    if (evs[i] != IntPtr.Zero) Native.CloseHandle(evs[i]);
                }
            }
        }

        private bool Submit(Port p, IntPtr buf, int size, IntPtr ov, int ovSize, int evOff, IntPtr ev)
        {
            Native.ResetEvent(ev);
            for (int i = 0; i < ovSize; i += 4) Marshal.WriteInt32(ov, i, 0);
            Marshal.WriteIntPtr(ov, evOff, ev);
            if (Native.WinUsb_ReadPipeAsync(p.Iface, p.PipeIn, buf, (uint)size, IntPtr.Zero, ov)) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == 997) return true;   // ERROR_IO_PENDING: queued
            Log("read request failed, error " + err);
            return false;
        }

        // Finds complete messages in the buffer. Returns how many bytes are left (moved to the start).
        private int Parse(Session s, byte[] acc, int len)
        {
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
                        if (acc[j] == 0x08 && BitConverter.ToUInt32(acc, j) == Magic) { found = true; break; }
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
            return len;
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
                Log("frame format: screen " + sw + "x" + sh + " frame " + w + "x" + h + " bpp " + bpp + " stride " + stride + " head " + HexDump(buf, off, 64));
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
            Interlocked.Increment(ref _fpsRx);

            int step = _quality == 0 ? 2 : 1;
            Decode(buf, off + (int)dataOff, (int)bpp, stride, w, h, step, _rotate, s.Back);

            // Hand the picture to the window. If the window has not shown the previous one yet, it is replaced.
            lock (_frameLock)
            {
                Frame t = _ready;
                _ready = s.Back;
                s.Back = t ?? _spare;
                if (t == null) _spare = new Frame();
                _hasNew = true;
            }
            if (Interlocked.Exchange(ref _posted, 1) == 0)
            {
                if (IsDisposed || !IsHandleCreated) { _posted = 0; return; }
                try { BeginInvoke(new Action(delegate { ShowNewFrame(s); })); }
                catch (InvalidOperationException) { _posted = 0; }
            }
        }

        private static int[] BuildLut()
        {
            var t = new int[65536];
            for (int v = 0; v < 65536; v++)
            {
                int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
                t[v] = unchecked((int)0xFF000000) | (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
            }
            return t;
        }

        // Converts the phone pixels to 32-bit and applies the manual rotation in the same pass.
        private static unsafe void Decode(byte[] buf, int start, int bpp, int stride, int w, int h, int step, int rot, Frame dst)
        {
            int ow = Math.Max(1, w / step);
            int oh = Math.Max(1, h / step);
            bool swap = (rot & 1) == 1;
            int dw = swap ? oh : ow;
            int dh = swap ? ow : oh;
            dst.Ensure(dw, dh);
            int bytesPP = bpp / 8;

            fixed (byte* src0 = buf)
            fixed (int* d0 = dst.Px)
            fixed (int* lut = Lut565)
            {
                for (int y = 0; y < oh; y++)
                {
                    byte* row = src0 + start + (long)(y * step) * stride;
                    int* p;
                    int inc;
                    switch (rot)
                    {
                        case 1: p = d0 + (oh - 1 - y); inc = dw; break;                  // 90 clockwise
                        case 2: p = d0 + (long)(oh - 1 - y) * dw + (ow - 1); inc = -1; break;
                        case 3: p = d0 + (long)(ow - 1) * dw + y; inc = -dw; break;       // 270 clockwise
                        default: p = d0 + (long)y * dw; inc = 1; break;
                    }
                    if (bpp == 16)
                    {
                        ushort* sp = (ushort*)row;
                        if (step == 1 && inc == 1)
                        {
                            for (int x = 0; x < ow; x++) p[x] = lut[sp[x]];
                        }
                        else
                        {
                            for (int x = 0; x < ow; x++) { *p = lut[sp[x * step]]; p += inc; }
                        }
                    }
                    else
                    {
                        byte* sp = row;
                        int adv = step * bytesPP;
                        for (int x = 0; x < ow; x++)
                        {
                            *p = unchecked((int)0xFF000000) | (sp[2] << 16) | (sp[1] << 8) | sp[0];
                            p += inc;
                            sp += adv;
                        }
                    }
                }
            }
        }

        // UI thread: take the newest picture and draw it.
        private void ShowNewFrame(Session s)
        {
            _posted = 0;
            if (s != _session) return;
            Frame f;
            lock (_frameLock)
            {
                if (!_hasNew) return;
                f = _ready;
                _ready = _view.Frame;   // the old front picture becomes free for the reader
                _hasNew = false;
            }
            _view.Frame = f;
            _view.Invalidate();

            int land = f.W > f.H ? 1 : 0;
            if (land != _landscape)
            {
                _landscape = land;
                Log("orientation: " + (land == 1 ? "landscape" : "portrait") + ", picture " + f.W + "x" + f.H);
                AdaptWindow(f.W, f.H);
            }
            if (!s.GotFrame)
            {
                s.GotFrame = true;
                _statusText = "streaming";
                UpdateTitle();
            }
            _fpsShown++;
            double el = (DateTime.UtcNow - _fpsStart).TotalSeconds;
            if (el >= 1.0)
            {
                _fps = _fpsShown / el;
                _fpsPhone = Interlocked.Exchange(ref _fpsRx, 0) / el;
                _fpsShown = 0;
                _fpsStart = DateTime.UtcNow;
                UpdateTitle();
            }
        }

        // ---------- menu actions ----------
        private void ApplyQuality(int q)
        {
            _quality = q;
            _view.Smooth = q == 2;
            for (int i = 0; i < _miQuality.Length; i++) _miQuality[i].Checked = (i == q);
            _view.Invalidate();
        }

        // Turns the window to match the picture: portrait <-> landscape.
        private void AdaptWindow(int iw, int ih)
        {
            if (WindowState != FormWindowState.Normal) return;
            int chrome = ChromeHeight;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height - chrome;
            bool imgLand = iw > ih;
            if ((cw > ch) == imgLand) return;

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            double a = (double)iw / ih;
            int longSide = Math.Max(cw, ch);
            int w, h;
            if (imgLand) { w = longSide; h = (int)(w / a); }
            else { h = longSide; w = (int)(h * a); }

            int maxW = wa.Width - 40;
            int maxH = wa.Height - 80 - chrome;
            double sc = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
            w = Math.Max(160, (int)(w * sc));
            h = Math.Max(160, (int)(h * sc));
            ClientSize = new Size(w, h + chrome);

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
            if (!_touch && _down) EndTouch(_lastPx, _lastPy);
            UpdateTitle();
        }

        // Shows or hides the button bar; the picture area keeps its size.
        private void ToggleNav()
        {
            _navVisible = !_navVisible;
            _miNav.Checked = _navVisible;
            if (!_navVisible) ReleaseAllInput();
            bool normal = WindowState == FormWindowState.Normal;
            Size cs = ClientSize;
            _nav.Visible = _navVisible;
            if (normal) ClientSize = new Size(cs.Width, cs.Height + (_navVisible ? _nav.Height : -_nav.Height));
        }

        private void Rotate() { _rotate = (_rotate + 1) % 4; }

        private void FitWindow()
        {
            Frame f = _view.Frame;
            if (f == null) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(f.W, wa.Width - 40);
            int h = Math.Min(f.H + ChromeHeight, wa.Height - 80);
            ClientSize = new Size(w, h);
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
            Frame f = _view.Frame;
            if (f == null) { SetNote("no frame yet"); return; }
            try
            {
                string path = Path.Combine(PicturesDir(), "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                using (Bitmap copy = f.ToBitmap()) copy.Save(path, ImageFormat.Png);
                SetNote("saved " + path);
            }
            catch (Exception ex) { SetNote("cannot save: " + ex.Message); }
        }

        private void CopyFrame()
        {
            Frame f = _view.Frame;
            if (f == null) { SetNote("no frame yet"); return; }
            try
            {
                using (Bitmap copy = f.ToBitmap()) Clipboard.SetImage(copy);
                SetNote("frame copied to clipboard");
            }
            catch (Exception ex) { SetNote("cannot copy: " + ex.Message); }
        }

        // ---------- output to the phone ----------
        private void Enqueue(byte[] msg)
        {
            lock (_outLock)
            {
                while (_outQ.Count > 100) _outQ.Dequeue();
                _outQ.Enqueue(msg);
                Monitor.Pulse(_outLock);
            }
        }

        // Writes input messages and sends the keep-alive request, off the UI thread.
        private void WriterLoop()
        {
            DateTime lastCtl = DateTime.UtcNow;
            while (!_closing)
            {
                byte[] item = null;
                lock (_outLock)
                {
                    if (_outQ.Count == 0) Monitor.Wait(_outLock, 500);
                    if (_outQ.Count > 0) item = _outQ.Dequeue();
                }
                lock (_portLock)
                {
                    Port p = _port;
                    if (p == null || !p.IsOpen) continue;   // not connected: the message is dropped
                    DateTime now = DateTime.UtcNow;
                    if (item != null)
                    {
                        bool ok = p.Write(p.PipeOut, item);
                        if (!ok) Log("input write failed, error " + Marshal.GetLastWin32Error());
                        p.ClassRequest(0x03);
                        p.ClassRequest(0x03);
                        lastCtl = now;
                    }
                    else if ((now - lastCtl).TotalMilliseconds >= 2500)
                    {
                        p.ClassRequest(0x03);
                        p.ClassRequest(0x03);
                        lastCtl = now;
                    }
                }
            }
        }

        // ---------- phone buttons ----------
        private void SetKey(int index, bool down)
        {
            if (index < 0 || index > 2) return;
            if (_keyDown[index] == down) return;   // ignore keyboard auto-repeat
            _keyDown[index] = down;
            byte[] t = (byte[])(down ? TplKeyDown : TplKeyUp).Clone();
            Put(t, 28, (uint)_keyCodes[index]);
            t[32] = (byte)(down ? 1 : 0);
            Enqueue(t);
        }

        private void TapKey(int index)
        {
            SetKey(index, true);
            SetKey(index, false);
        }

        private void ReleaseAllInput()
        {
            for (int i = 0; i < 3; i++) SetKey(i, false);
            if (_down) EndTouch(_lastPx, _lastPy);
        }

        private int KeyIndex(Keys k)
        {
            switch (k)
            {
                case Keys.Back: return 0;
                case Keys.Home: return 1;
                case Keys.F3: return 2;
            }
            return -1;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Modifiers != Keys.None) return;
            int i = KeyIndex(e.KeyCode);
            if (i < 0) return;
            SetKey(i, true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            int i = KeyIndex(e.KeyCode);
            if (i < 0) return;
            SetKey(i, false);
            e.Handled = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // A key released while another window has focus would never reach us.
            for (int i = 0; i < 3; i++) SetKey(i, false);
        }

        // ---------- mouse touch ----------
        private bool MapMouse(Point m, out int px, out int py)
        {
            px = 0; py = 0;
            Frame f = _view.Frame;
            if (f == null) return false;
            int iw = f.W, ih = f.H;
            Rectangle r = _view.GetImageRect(iw, ih);
            double scale = (double)r.Width / iw;
            if (scale <= 0) return false;
            double ix = (m.X - r.X) / scale;
            double iy = (m.Y - r.Y) / scale;
            ix = Math.Max(0, Math.Min(iw - 1, ix));
            iy = Math.Max(0, Math.Min(ih - 1, iy));

            // Size of the picture before manual rotation.
            int rot = _rotate;
            int w = (rot % 2 == 0) ? iw : ih;
            int h = (rot % 2 == 0) ? ih : iw;
            double fx, fy;
            switch (rot)
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
            if (!_touch || e.Button != MouseButtons.Left || _down) return;
            int px, py;
            if (!MapMouse(e.Location, out px, out py)) return;
            _stroke++;
            _down = true;
            _lastPx = px; _lastPy = py;
            SendTouch(0, px, py);
            _holdTimer.Start();
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
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_down || e.Button != MouseButtons.Left) return;
            int px, py;
            if (!MapMouse(e.Location, out px, out py)) { px = _lastPx; py = _lastPy; }
            EndTouch(px, py);
        }

        private void EndTouch(int px, int py)
        {
            if (!_down) return;
            _down = false;
            _holdTimer.Stop();
            SendTouch(2, px, py);
        }

        // The finger stays on the screen: repeat the contact so the phone keeps it (press and hold, drag and wait).
        private void HoldTick()
        {
            if (!_down) { _holdTimer.Stop(); return; }
            if ((DateTime.UtcNow - _lastSend).TotalMilliseconds < HoldRepeatMs) return;
            SendTouch(1, _lastPx, _lastPy);
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
            _lastSend = DateTime.UtcNow;
            Enqueue(t);
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
