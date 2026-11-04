using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WpMirror
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle dev, out IntPtr iface);

        [DllImport("winusb.dll")]
        public static extern bool WinUsb_Free(IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipe, byte[] buf, uint len, out uint read, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_WritePipe(IntPtr iface, byte pipe, byte[] buf, uint len, out uint written, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ControlTransfer(IntPtr iface, WINUSB_SETUP_PACKET setup, byte[] buf, uint len, out uint transferred, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipe, uint policy, uint valueLen, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_AbortPipe(IntPtr iface, byte pipe);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte alt, out USB_INTERFACE_DESCRIPTOR desc);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Disable_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Enable_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(IntPtr iface, byte alt, byte index, out WINUSB_PIPE_INFORMATION info);
    }

    internal sealed class DeviceInfo
    {
        public string Name;
        public string Path;
        public string Instance;
    }

    // A connected phone whose display interface has no device interface registered yet.
    internal sealed class PendingDevice
    {
        public string Name;
        public string KeyPath;    // e.g. VID_0421&PID_06FC&MI_02\\6&378502b0&5&0002
        public string InstanceId; // e.g. USB\\VID_0421&PID_06FC&MI_02\\6&378502b0&5&0002
    }

    internal sealed class Port : IDisposable
    {
        // USB interface that carries the screen projection (MI_02 of the phone).
        public const int DisplayInterface = 2;

        private static readonly Guid DefaultInterfaceGuid = new Guid("31648E59-6FCB-4D81-9C97-D00999B0459A");

        public SafeFileHandle File;
        public IntPtr Iface;
        public string Error;

        // Taken from the interface descriptor when possible; these are the values used by the Lumia 830.
        public byte PipeIn = 0x84;
        public byte PipeOut = 0x03;
        public int InterfaceNumber = DisplayInterface;

        public bool IsOpen { get { return Iface != IntPtr.Zero; } }

        public static Port OpenPath(string path)
        {
            var port = new Port();
            if (string.IsNullOrEmpty(path))
            {
                port.Error = "Device interface not found.";
                return port;
            }
            SafeFileHandle h = Native.CreateFile(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
            if (h.IsInvalid)
            {
                port.Error = "Cannot open the device (error " + Marshal.GetLastWin32Error() + ").";
                return port;
            }
            IntPtr iface;
            if (!Native.WinUsb_Initialize(h, out iface))
            {
                port.Error = "WinUsb_Initialize failed (error " + Marshal.GetLastWin32Error() + ").";
                h.Dispose();
                return port;
            }
            port.File = h;
            port.Iface = iface;
            port.InterfaceNumber = ParseInterfaceNumber(path);
            port.DiscoverPipes();
            return port;
        }

        // "...&mi_02#..." -> 2
        private static int ParseInterfaceNumber(string path)
        {
            string low = path.ToLowerInvariant();
            int i = low.IndexOf("&mi_");
            int n;
            if (i >= 0 && low.Length >= i + 6 && int.TryParse(low.Substring(i + 4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out n)) return n;
            return 0;
        }

        // Finds the first bulk IN and bulk OUT endpoints, so different phone models can use different addresses.
        private void DiscoverPipes()
        {
            Native.USB_INTERFACE_DESCRIPTOR d;
            if (!Native.WinUsb_QueryInterfaceSettings(Iface, 0, out d)) return;
            bool haveIn = false, haveOut = false;
            for (byte i = 0; i < d.bNumEndpoints; i++)
            {
                Native.WINUSB_PIPE_INFORMATION pi;
                if (!Native.WinUsb_QueryPipe(Iface, 0, i, out pi)) continue;
                if (pi.PipeType != 2) continue; // 2 = bulk
                if ((pi.PipeId & 0x80) != 0)
                {
                    if (!haveIn) { PipeIn = pi.PipeId; haveIn = true; }
                }
                else if (!haveOut) { PipeOut = pi.PipeId; haveOut = true; }
            }
        }

        public bool ClassRequest(byte request)
        {
            var sp = new Native.WINUSB_SETUP_PACKET();
            sp.RequestType = 0x21;
            sp.Request = request;
            sp.Value = 0;
            sp.Index = (ushort)InterfaceNumber;
            sp.Length = 0;
            uint n;
            return Native.WinUsb_ControlTransfer(Iface, sp, new byte[0], 0, out n, IntPtr.Zero);
        }

        public bool Write(byte pipe, byte[] data)
        {
            uint n;
            return Native.WinUsb_WritePipe(Iface, pipe, data, (uint)data.Length, out n, IntPtr.Zero);
        }

        public void SetTimeout(byte pipe, uint ms)
        {
            Native.WinUsb_SetPipePolicy(Iface, pipe, 3, 4, ref ms);
        }

        // Cancels any transfer that is still pending, so the interface is released cleanly.
        public void AbortPipes(params byte[] pipes)
        {
            if (Iface == IntPtr.Zero) return;
            foreach (byte pipe in pipes) Native.WinUsb_AbortPipe(Iface, pipe);
        }

        public void Dispose()
        {
            if (Iface != IntPtr.Zero) { Native.WinUsb_Free(Iface); Iface = IntPtr.Zero; }
            if (File != null) File.Dispose();
        }

        // ---------- device discovery ----------

        // Lists the Windows Phone devices that are connected right now.
        public static List<DeviceInfo> ListDevices()
        {
            var all = new List<DeviceInfo>();
            var seen = new HashSet<string>();
            foreach (Guid g0 in CandidateGuids())
            {
                Guid g = g0;
                IntPtr set = Native.SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
                if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;
                try
                {
                    for (uint i = 0; ; i++)
                    {
                        var d = new Native.SP_DEVICE_INTERFACE_DATA();
                        d.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                        if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                        uint req;
                        Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                        IntPtr buf = Marshal.AllocHGlobal((int)req);
                        try
                        {
                            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                            string path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                            string low = path.ToLowerInvariant();
                            if (!low.Contains("vid_0421") && !low.Contains("vid_045e")) continue;
                            if (!seen.Add(low)) continue;
                            all.Add(MakeInfo(path));
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                finally { Native.SetupDiDestroyDeviceInfoList(set); }
            }
            // Also take every enabled interface that Windows has registered for the phone, whatever its GUID is.
            foreach (LinkedIf li in ScanDeviceClasses())
            {
                if (!IsConnectedPath(li.Path)) continue;
                if (!seen.Add(li.Path.ToLowerInvariant())) continue;
                all.Add(MakeInfo(li.Path));
            }
            // Normally the display interface is MI_02. If a phone has no such interface, offer what is there.
            var result = new List<DeviceInfo>();
            foreach (DeviceInfo d in all)
                if (d.Path.ToLowerInvariant().Contains("&mi_02")) result.Add(d);
            List<DeviceInfo> chosen = result.Count > 0 ? result : all;

            // One phone interface can be registered under several GUIDs: show each phone once,
            // and prefer the standard GUID.
            string std = DefaultInterfaceGuid.ToString().ToLowerInvariant();
            var unique = new List<DeviceInfo>();
            foreach (DeviceInfo d in chosen)
            {
                string key = d.Path.Split('#').Length > 2 ? (d.Path.Split('#')[1] + "#" + d.Instance).ToLowerInvariant() : d.Path.ToLowerInvariant();
                int idx = -1;
                for (int i = 0; i < unique.Count; i++)
                {
                    string[] up = unique[i].Path.Split('#');
                    string k2 = up.Length > 2 ? (up[1] + "#" + unique[i].Instance).ToLowerInvariant() : unique[i].Path.ToLowerInvariant();
                    if (k2 == key) { idx = i; break; }
                }
                if (idx < 0) unique.Add(d);
                else if (d.Path.ToLowerInvariant().Contains(std) && !unique[idx].Path.ToLowerInvariant().Contains(std)) unique[idx] = d;
            }
            return unique;
        }

        // \\?\USB#VID_0421&PID_06FC&MI_02#6&1f151618&0&0002#{guid} -> is that device node connected now?
        private static bool IsConnectedPath(string path)
        {
            string[] parts = path.Split('#');
            if (parts.Length < 3) return false;
            uint dn;
            return Native.CM_Locate_DevNodeW(out dn, "USB\\" + parts[1] + "\\" + parts[2], 0) == 0;
        }

        // Connected phones whose MI_02 uses the WinUSB driver but has no interface GUID,
        // so Windows has not created a device interface that programs can open.
        public static List<PendingDevice> FindUnregistered()
        {
            var list = new List<PendingDevice>();
            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb == null) return list;
                    foreach (string name in usb.GetSubKeyNames())
                    {
                        string up = name.ToUpperInvariant();
                        if (!(up.StartsWith("VID_0421&") || up.StartsWith("VID_045E&")) || !up.EndsWith("&MI_02")) continue;
                        using (RegistryKey k = usb.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            foreach (string inst in k.GetSubKeyNames())
                            {
                                string id = "USB\\" + name + "\\" + inst;
                                uint dn;
                                if (Native.CM_Locate_DevNodeW(out dn, id, 0) != 0) continue;
                                string friendly = null;
                                using (RegistryKey ik = k.OpenSubKey(inst))
                                {
                                    if (ik == null) continue;
                                    string svc = ik.GetValue("Service") as string;
                                    if (svc == null || !svc.Equals("winusb", StringComparison.OrdinalIgnoreCase)) continue;
                                    friendly = ik.GetValue("FriendlyName") as string;
                                }
                                using (RegistryKey dp = k.OpenSubKey(inst + @"\Device Parameters"))
                                {
                                    if (dp != null)
                                    {
                                        string one = dp.GetValue("DeviceInterfaceGUID") as string;
                                        if (!string.IsNullOrEmpty(one) || dp.GetValue("DeviceInterfaceGUIDs") != null) continue;
                                    }
                                }
                                var pd = new PendingDevice();
                                pd.Name = string.IsNullOrEmpty(friendly) ? "Windows Phone (" + name.Replace("&MI_02", "") + ")" : friendly;
                                pd.KeyPath = name + "\\" + inst;
                                pd.InstanceId = id;
                                list.Add(pd);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        // Writes the interface GUID for the device and restarts its MI_02 device node.
        // Returns null on success, otherwise an error text. Needs administrator rights.
        public static string RegisterInterface(PendingDevice d)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\" + d.KeyPath + @"\Device Parameters"))
                {
                    if (k == null) return "Cannot open the registry key for writing.";
                    k.SetValue("DeviceInterfaceGUID", "{" + DefaultInterfaceGuid.ToString().ToUpperInvariant() + "}", RegistryValueKind.String);
                }
            }
            catch (UnauthorizedAccessException) { return "Access denied. Start WpMirror as administrator (right-click, Run as administrator) and try again."; }
            catch (System.Security.SecurityException) { return "Access denied. Start WpMirror as administrator (right-click, Run as administrator) and try again."; }
            catch (Exception ex) { return ex.Message; }

            uint dn;
            if (Native.CM_Locate_DevNodeW(out dn, d.InstanceId, 0) == 0)
            {
                int r1 = Native.CM_Disable_DevNode(dn, 0);
                System.Threading.Thread.Sleep(700);
                int r2 = Native.CM_Enable_DevNode(dn, 0);
                if (r1 != 0 || r2 != 0) return "The setting was saved, but the device could not be restarted (codes " + r1 + "/" + r2 + "). Unplug and plug in the phone.";
            }
            return null;
        }

        private sealed class LinkedIf
        {
            public string Path;
            public Guid Guid;
            public bool Linked;
        }

        // Reads the interfaces Windows has registered for Nokia/Microsoft USB devices.
        // "Linked" is set while the interface is enabled, that is, the phone is connected.
        private static List<LinkedIf> ScanDeviceClasses()
        {
            var list = new List<LinkedIf>();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceClasses"))
                {
                    if (root == null) return list;
                    foreach (string gname in root.GetSubKeyNames())
                    {
                        Guid g;
                        if (!Guid.TryParse(gname, out g)) continue;
                        using (RegistryKey gk = root.OpenSubKey(gname))
                        {
                            if (gk == null) continue;
                            foreach (string iname in gk.GetSubKeyNames())
                            {
                                string low = iname.ToLowerInvariant();
                                if (!low.StartsWith("##?#usb#")) continue;
                                if (!low.Contains("#vid_0421&") && !low.Contains("#vid_045e&")) continue;
                                var li = new LinkedIf();
                                li.Guid = g;
                                li.Path = @"\\?\" + iname.Substring(4);
                                using (RegistryKey ck = gk.OpenSubKey(iname + @"\Control"))
                                {
                                    object v = ck == null ? null : ck.GetValue("Linked");
                                    li.Linked = v is int && (int)v != 0;
                                }
                                list.Add(li);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        private static string NodeStatus(string instanceId)
        {
            uint dn;
            if (Native.CM_Locate_DevNodeW(out dn, instanceId, 0) != 0) return "not connected";
            uint st, prob;
            if (Native.CM_Get_DevNode_Status(out st, out prob, dn, 0) != 0) return "connected";
            return "CONNECTED, problem code " + prob;
        }

        // Text report about the USB side of Nokia/Microsoft phones, for troubleshooting.
        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Candidate interface GUIDs:");
            foreach (Guid g in CandidateGuids()) sb.AppendLine("  " + g);
            sb.AppendLine();
            sb.AppendLine("Registry (Enum\\USB, vendors 0421 and 045E):");
            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb != null)
                    {
                        foreach (string name in usb.GetSubKeyNames())
                        {
                            string up = name.ToUpperInvariant();
                            if (!up.StartsWith("VID_0421&") && !up.StartsWith("VID_045E&")) continue;
                            using (RegistryKey k = usb.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                foreach (string inst in k.GetSubKeyNames())
                                {
                                    sb.AppendLine("  " + name + "\\" + inst + "   [" + NodeStatus("USB\\" + name + "\\" + inst) + "]");
                                    using (RegistryKey ik = k.OpenSubKey(inst))
                                    {
                                        if (ik == null) continue;
                                        sb.AppendLine("      Service=" + ik.GetValue("Service") + " Class=" + ik.GetValue("Class") + " Desc=" + ik.GetValue("DeviceDesc") + " Friendly=" + ik.GetValue("FriendlyName"));
                                        using (RegistryKey dp = ik.OpenSubKey("Device Parameters"))
                                        {
                                            if (dp != null)
                                            {
                                                object a = dp.GetValue("DeviceInterfaceGUID");
                                                object b = dp.GetValue("DeviceInterfaceGUIDs");
                                                sb.AppendLine("      DeviceInterfaceGUID=" + a + " DeviceInterfaceGUIDs=" + (b is string[] ? string.Join(",", (string[])b) : "" + b));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("  error: " + ex.Message); }
            sb.AppendLine();
            sb.AppendLine("Connected phones without a registered interface:");
            foreach (PendingDevice pd in FindUnregistered()) sb.AppendLine("  " + pd.Name + "  " + pd.InstanceId);
            sb.AppendLine();
            sb.AppendLine("Registered interfaces (Linked = enabled now):");
            foreach (LinkedIf li in ScanDeviceClasses()) sb.AppendLine("  linked=" + li.Linked + "  " + li.Path);
            sb.AppendLine();
            sb.AppendLine("Devices found by the Device menu:");
            try { foreach (DeviceInfo d in ListDevices()) sb.AppendLine("  " + d.Name + "  " + d.Path); }
            catch (Exception ex) { sb.AppendLine("  error: " + ex.Message); }
            return sb.ToString();
        }

        private static DeviceInfo MakeInfo(string path)
        {
            // Path looks like \\?\usb#vid_0421&pid_06fc&mi_02#6&1f151618&0&0002#{guid}
            string[] parts = path.Split('#');
            string hw = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
            string inst = parts.Length > 2 ? parts[2] : "";
            string name = null;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\" + hw + "\\" + inst))
                {
                    if (k != null) name = k.GetValue("FriendlyName") as string;
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(name)) name = "Windows Phone (" + hw.Replace("&MI_02", "") + ")";
            var info = new DeviceInfo();
            info.Name = name;
            info.Path = path;
            info.Instance = inst;
            return info;
        }

        private static List<Guid> CandidateGuids()
        {
            var list = new List<Guid>();
            list.Add(DefaultInterfaceGuid);
            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb == null) return list;
                    foreach (string name in usb.GetSubKeyNames())
                    {
                        string up = name.ToUpperInvariant();
                        bool vendor = up.StartsWith("VID_0421&") || up.StartsWith("VID_045E&");
                        if (!vendor) continue;
                        using (RegistryKey k = usb.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            foreach (string inst in k.GetSubKeyNames())
                            {
                                using (RegistryKey dp = k.OpenSubKey(inst + @"\Device Parameters"))
                                {
                                    if (dp == null) continue;
                                    AddGuids(list, dp.GetValue("DeviceInterfaceGUID"));
                                    AddGuids(list, dp.GetValue("DeviceInterfaceGUIDs"));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        private static void AddGuids(List<Guid> list, object value)
        {
            string[] arr = value as string[];
            if (arr == null && value is string) arr = new[] { (string)value };
            if (arr == null) return;
            foreach (string s in arr)
            {
                Guid g;
                if (Guid.TryParse(s, out g) && !list.Contains(g)) list.Add(g);
            }
        }
    }
}
