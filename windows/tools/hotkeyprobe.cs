// hotkeyprobe - guided capture of what the lighting hotkeys emit on this laptop.
// Shows one instruction at a time; you press the key(s), then click whether the
// lighting changed. Logs (letters masked) to hotkeyprobe.log next to the exe:
//   - WH_KEYBOARD_LL events (vk, scan, flags incl. injected)
//   - Raw Input keyboard + consumer/system-control events with the source device
//   - HP BIOS WMI events (root\wmi HPBIOS_BIOSEvent, hpqBEvnt)
//   - input reports on the RGB MCU's vendor interfaces (1FC9:2238 MI_02/MI_03)
//
// Build: csc /target:winexe /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Drawing.dll hotkeyprobe.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

static class Probe
{
    static readonly string[] Steps =
    {
        "Press Fn+1 (hold Fn, tap 1)",
        "Press Fn+2",
        "Press Fn+3",
        "Press Fn+9",
        "Press the BACKLIGHT key (F9) once, on its own",
        "Press the BACKLIGHT key (F9) again",
        "Press the BACKLIGHT key (F9) a third time",
        "Press Fn + BACKLIGHT key (Fn+F9)",
        "Press Fn+F4",
        "Press any other key you think controls lighting (or click Skip)",
    };

    static StreamWriter _log;
    static readonly object _lk = new object();
    static readonly Stopwatch _sw = Stopwatch.StartNew();
    static readonly Dictionary<IntPtr, string> _devNames = new Dictionary<IntPtr, string>();

    public static void Log(string s)
    {
        lock (_lk) { _log.WriteLine("{0,8:F2}s  {1}", _sw.Elapsed.TotalSeconds, s); _log.Flush(); }
    }

    static string Vk(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return "letter";            // mask typing
        return "0x" + vk.ToString("X2") + " " + (Keys)vk;
    }

    // ---- low-level hook ----
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr h, uint t);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [StructLayout(LayoutKind.Sequential)] struct KBLL { public uint vk, scan, flags, time; public IntPtr extra; }
    static HookProc _proc;

    static IntPtr Hook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = (KBLL)Marshal.PtrToStructure(l, typeof(KBLL));
            int m = w.ToInt32();
            string kind = m == 0x100 ? "down" : m == 0x101 ? "up" : m == 0x104 ? "sysdown" : "sysup";
            Log(string.Format("LLHOOK {0,-7} vk={1} scan=0x{2:X2} flags=0x{3:X2}{4}{5}", kind, Vk(k.vk), k.scan, k.flags,
                (k.flags & 0x10) != 0 ? " INJECTED" : "", (k.flags & 0x01) != 0 ? " EXT" : ""));
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    // ---- raw input ----
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, byte[] data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, System.Text.StringBuilder data, ref uint size);

    static string DevName(IntPtr h)
    {
        string n;
        if (_devNames.TryGetValue(h, out n)) return n;
        uint size = 512; var sb = new System.Text.StringBuilder(512);
        GetRawInputDeviceInfo(h, 0x20000007 /*RIDI_DEVICENAME*/, sb, ref size);
        n = sb.ToString();
        int a = n.IndexOf('#'), b = n.IndexOf('#', a + 1);
        if (a >= 0 && b > a) n = n.Substring(a + 1, b - a - 1);
        if (h == IntPtr.Zero) n = "(injected/no device)";
        _devNames[h] = n;
        return n;
    }

    public static void Register(IntPtr hwnd)
    {
        const uint INPUTSINK = 0x100;
        var d = new[]
        {
            new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = INPUTSINK, Target = hwnd },   // keyboard
            new RAWINPUTDEVICE { Page = 0x0C, Usage = 0x01, Flags = INPUTSINK, Target = hwnd },   // consumer control
            new RAWINPUTDEVICE { Page = 0x01, Usage = 0x80, Flags = INPUTSINK, Target = hwnd },   // system control
            new RAWINPUTDEVICE { Page = 0x01, Usage = 0x0C, Flags = INPUTSINK, Target = hwnd },   // wireless radio ctrl (HPQ6001)
        };
        if (!RegisterRawInputDevices(d, (uint)d.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            Log("RegisterRawInputDevices failed " + Marshal.GetLastWin32Error());
    }

    public static void OnRawInput(IntPtr lParam)
    {
        uint hdr = (uint)(8 + 2 * IntPtr.Size), size = 0;
        GetRawInputData(lParam, 0x10000003, null, ref size, hdr);
        var buf = new byte[size];
        if (GetRawInputData(lParam, 0x10000003, buf, ref size, hdr) != size) return;
        uint type = BitConverter.ToUInt32(buf, 0);
        IntPtr dev = IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(buf, 8)) : new IntPtr(BitConverter.ToInt32(buf, 8));
        int o = (int)hdr;
        if (type == 1) // keyboard
        {
            ushort make = BitConverter.ToUInt16(buf, o), flags = BitConverter.ToUInt16(buf, o + 2), vk = BitConverter.ToUInt16(buf, o + 6);
            uint msg = BitConverter.ToUInt32(buf, o + 8);
            Log(string.Format("RAWKBD  {0} make=0x{1:X2} flags=0x{2:X} vk={3} msg=0x{4:X3}  dev={5}",
                (flags & 1) != 0 ? "up  " : "down", make, flags, Vk(vk), msg, DevName(dev)));
        }
        else if (type == 2) // HID
        {
            int sz = BitConverter.ToInt32(buf, o), cnt = BitConverter.ToInt32(buf, o + 4);
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(sz * cnt, 32); i++) parts.Add(buf[o + 8 + i].ToString("X2"));
            Log("RAWHID  " + string.Join(" ", parts) + "  dev=" + DevName(dev));
        }
    }

    // ---- WMI ----
    static void Wmi(string cls)
    {
        try
        {
            var w = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM " + cls));
            w.EventArrived += delegate(object s, EventArrivedEventArgs e)
            {
                var parts = new List<string>();
                foreach (var p in e.NewEvent.Properties)
                    if (p.Name != "SECURITY_DESCRIPTOR" && p.Name != "TIME_CREATED") parts.Add(p.Name + "=" + p.Value);
                Log("WMI " + cls + "  " + string.Join("  ", parts));
            };
            w.Start();
            Log("WMI " + cls + ": watching");
        }
        catch (Exception e) { Log("WMI " + cls + ": " + e.Message); }
    }

    // ---- MCU vendor interfaces ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

    static void Mcu(string mi)
    {
        string path = null;
        foreach (var p in HpKbdPaths()) if (p.Contains("mi_" + mi)) path = p;
        if (path == null) { Log("MI_" + mi + ": not found"); return; }
        var h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h.IsInvalid) { Log("MI_" + mi + ": open failed " + Marshal.GetLastWin32Error()); return; }
        var fs = new FileStream(h, FileAccess.Read, 65, true);
        Log("MI_" + mi + ": listening");
        var t = new Thread(delegate()
        {
            var buf = new byte[65];
            while (true)
            {
                int n;
                try { n = fs.Read(buf, 0, buf.Length); } catch (Exception e) { Log("MI_" + mi + " error " + e.Message); return; }
                var parts = new List<string>();
                for (int i = 0; i < Math.Min(n, 24); i++) parts.Add(buf[i].ToString("X2"));
                Log("MCU MI_" + mi + "  " + string.Join(" ", parts));
            }
        });
        t.IsBackground = true; t.Start();
    }

    [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
    [StructLayout(LayoutKind.Sequential)] struct DID { public int cb; public Guid g; public int f; public IntPtr r; }
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref DID did);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref DID did, IntPtr det, int size, out int req, IntPtr di);

    static List<string> HpKbdPaths()
    {
        var l = new List<string>(); var g = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        var set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        for (int i = 0; ; i++)
        {
            var d = new DID(); d.cb = Marshal.SizeOf(d);
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
            int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
            var b = Marshal.AllocHGlobal(req); Marshal.WriteInt32(b, IntPtr.Size == 8 ? 8 : 6);
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, b, req, out req, IntPtr.Zero))
            { var p = Marshal.PtrToStringUni(new IntPtr(b.ToInt64() + 4)).ToLowerInvariant(); if (p.Contains("vid_1fc9&pid_2238")) l.Add(p); }
            Marshal.FreeHGlobal(b);
        }
        return l;
    }

    class Win : Form
    {
        int _step = -1;
        readonly Label _lbl = new Label();
        readonly Button _yes = new Button(), _no = new Button(), _skip = new Button();

        public Win()
        {
            Text = "hpkbd hotkey probe"; TopMost = true; Width = 620; Height = 260; FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false;
            _lbl.SetBounds(20, 20, 570, 110); _lbl.Font = new Font("Segoe UI", 15f);
            _yes.SetBounds(20, 150, 180, 45); _yes.Text = "Lighting CHANGED";
            _no.SetBounds(215, 150, 180, 45); _no.Text = "Nothing changed";
            _skip.SetBounds(410, 150, 180, 45); _skip.Text = "Skip / not available";
            foreach (var b in new[] { _yes, _no, _skip }) { b.Font = new Font("Segoe UI", 10f); b.TabStop = false; Controls.Add(b); }
            Controls.Add(_lbl);
            _yes.Click += delegate { Next("lighting CHANGED"); };
            _no.Click += delegate { Next("no visible change"); };
            _skip.Click += delegate { Next("skipped"); };
            Shown += delegate { Next(null); };
        }

        void Next(string result)
        {
            if (result != null) Log("=== STEP " + (_step + 1) + " RESULT: " + result);
            _step++;
            if (_step >= Steps.Length)
            {
                Log("=== done");
                _lbl.Text = "Done, thanks! You can close this window.";
                _yes.Enabled = _no.Enabled = _skip.Enabled = false;
                return;
            }
            Log("=== STEP " + (_step + 1) + ": " + Steps[_step]);
            _lbl.Text = "Step " + (_step + 1) + "/" + Steps.Length + ":\n" + Steps[_step] + "\n\nthen click what the keyboard lighting did.";
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Register(Handle); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x00FF) OnRawInput(m.LParam); base.WndProc(ref m); }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkeyprobe.log");
        _log = new StreamWriter(path, false);
        Log("OMEN background running: " + (Process.GetProcessesByName("OmenCommandCenterBackground").Length > 0));
        _proc = Hook;
        SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
        Wmi("HPBIOS_BIOSEvent");
        Wmi("hpqBEvnt");
        Mcu("02"); Mcu("03");
        Application.EnableVisualStyles();
        Application.Run(new Win());
        _log.Close();
    }
}
