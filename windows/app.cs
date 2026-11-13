// CLI, persistent state and the hotkey daemon for hpkbd. Build with build.cmd.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HpKbd
{
    sealed class EffectDef
    {
        public byte Id; public string Preset; public string Color; public int Speed;
        public EffectDef(byte id, string preset, string color, int speed) { Id = id; Preset = preset; Color = color; Speed = speed; }
    }

    static class Effects
    {
        // McuSDK2 LightingEffectType ids OMEN uses for Starmade keyboards
        // (StarmadeKbLightingEffectCommandHelper.EffectCommandTable), with OMEN's defaults
        // from KbAnimationDefaultSetting.json. Not every effect accepts every preset:
        // e.g. breathing with a preset does nothing; with a custom colour it works.
        public static readonly Dictionary<string, EffectDef> Defs = new Dictionary<string, EffectDef>
        {
            { "colorloop", new EffectDef(4,  "volcano", null,     1) },
            { "sparkle",   new EffectDef(7,  "ocean",   null,     1) },
            { "breathing", new EffectDef(2,  null,      "EA002A", 0) },
            { "ghosting",  new EffectDef(8,  "jungle",  null,     1) },   // reacts to typing
            { "ripple",    new EffectDef(9,  "ocean",   null,     1) },   // reacts to typing
            { "wave",      new EffectDef(10, "galaxy",  null,     1) },
            { "omenx",     new EffectDef(13, "volcano", null,     1) },
            { "raindrop",  new EffectDef(12, null,      "0FFA36", 2) },
            { "confetti",  new EffectDef(15, "galaxy",  null,     1) },   // in OMEN's table, no Modena default
            { "sun",       new EffectDef(16, "galaxy",  null,     1) },   // "
            { "swipe",     new EffectDef(17, "galaxy",  null,     1) },   // "
        };

        // PresetType (sent as ShowMode with ColorNumber = 4)
        public static readonly Dictionary<string, byte> Presets = new Dictionary<string, byte>
        {
            { "volcano", 2 }, { "jungle", 3 }, { "ocean", 4 }, { "galaxy", 5 },
        };

        public static readonly string[] Speeds = { "slow", "medium", "fast" };
        public static readonly string[] Directions = { "inward", "outward", "rtl", "ltr", "up", "down" };

        // Backlight key + digit. 0 is always the static colour.
        public static readonly string[] DefaultSlots = { "static", "wave", "breathing", "colorloop", "sparkle", "raindrop", "ripple", "ghosting", "omenx", "confetti" };

        public static IEnumerable<string> Names { get { return Defs.Keys; } }

        // No hardware brightness on this MCU (0x0C is rejected), so effects get the
        // LightingBrightnessLv field (LV_01..LV_04 = 0..3) and static colours are scaled.
        public static byte Level(int pct) { return (byte)(pct >= 100 ? 3 : pct >= 75 ? 2 : pct >= 50 ? 1 : 0); }
    }

    // Lighting state shared by the CLI and the daemon (%APPDATA%\hpkbd\state.txt).
    sealed class State
    {
        public bool On = true;
        public string Mode = "static";            // static | effect
        public byte R = 255, G = 255, B = 255;
        public int Brightness = 100;              // percent
        public string Effect = "wave";
        public List<byte[]> Colors = new List<byte[]>();   // empty = preset
        public string Preset = "galaxy";
        public int Speed = 1, Direction = 3;
        public string[] Slots = (string[])Effects.DefaultSlots.Clone();

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hpkbd"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "state.txt"); } }

        public static State Load()
        {
            var s = new State();
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                try
                {
                    if (k.StartsWith("slot") && k.Length == 5) { s.Slots[k[4] - '0'] = v; continue; }
                    switch (k)
                    {
                        case "on": s.On = v == "1"; break;
                        case "mode": s.Mode = v; break;
                        case "color": { var c = Program.ParseColor(v); s.R = c[0]; s.G = c[1]; s.B = c[2]; break; }
                        case "brightness": s.Brightness = Math.Max(0, Math.Min(100, int.Parse(v))); break;
                        case "effect": s.Effect = v; break;
                        case "colors": s.Colors.Clear(); foreach (var c in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) s.Colors.Add(Program.ParseColor(c)); break;
                        case "preset": s.Preset = v; break;
                        case "speed": s.Speed = int.Parse(v); break;
                        case "direction": s.Direction = int.Parse(v); break;
                    }
                }
                catch (FormatException) { }
                catch (IndexOutOfRangeException) { }
            }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>();
            lines.Add("on=" + (On ? "1" : "0"));
            lines.Add("mode=" + Mode);
            lines.Add("color=" + Hex3(new[] { R, G, B }));
            lines.Add("brightness=" + Brightness);
            lines.Add("effect=" + Effect);
            var cs = new List<string>();
            foreach (var c in Colors) cs.Add(Hex3(c));
            lines.Add("colors=" + string.Join(",", cs));
            lines.Add("preset=" + Preset);
            lines.Add("speed=" + Speed);
            lines.Add("direction=" + Direction);
            for (int i = 0; i < 10; i++) lines.Add("slot" + i + "=" + Slots[i]);
            File.WriteAllLines(FilePath, lines.ToArray());
        }

        static string Hex3(byte[] c) { return c[0].ToString("X2") + c[1].ToString("X2") + c[2].ToString("X2"); }

        public void SelectSlot(int n)
        {
            string name = Slots[n];
            if (name == "static") { Mode = "static"; return; }
            SelectEffect(name);
        }

        // Load OMEN's defaults for an effect (preset or colour, speed).
        public void SelectEffect(string name)
        {
            EffectDef d;
            if (!Effects.Defs.TryGetValue(name, out d)) throw new ArgumentException("Unknown effect " + name);
            Mode = "effect"; Effect = name;
            Colors.Clear();
            if (d.Color != null) Colors.Add(Program.ParseColor(d.Color));
            if (d.Preset != null) Preset = d.Preset;
            Speed = d.Speed;
            Direction = 3;
        }

        // Encoded like StarmadeKbLightingEffectCommandHelper.SetLightingEffect.
        public byte[] EffectPacket()
        {
            EffectDef d;
            if (!Effects.Defs.TryGetValue(Effect, out d)) throw new ArgumentException("Unknown effect " + Effect);
            byte showMode, colorNumber;
            if (Colors.Count == 0)
            {
                byte preset;
                if (!Effects.Presets.TryGetValue(Preset, out preset)) preset = 5;
                showMode = preset; colorNumber = 4;
            }
            else
            {
                showMode = (byte)(Colors.Count == 1 ? 0 : 1);     // single / multi colour
                colorNumber = (byte)(Colors.Count - 1);
            }
            // OMEN swaps the first two directions when encoding.
            byte dir = (byte)(Direction == 0 ? 1 : Direction == 1 ? 0 : Direction);
            return Proto.Effect(d.Id, showMode, colorNumber, (byte)Speed, Effects.Level(Brightness), dir,
                                1 /*size: medium*/, (byte)Speed /*raindrop frequency = speed*/, Colors);
        }

        public void Apply(Kbd k)
        {
            if (!On || Brightness == 0) { k.Off(); return; }
            if (Mode == "effect") { k.SetEffect(EffectPacket()); return; }
            k.SetKeys(Plane(R), Plane(G), Plane(B));
        }

        byte[] Plane(byte v)
        {
            var p = new byte[Proto.KEY_COUNT];
            byte s = (byte)(v * Brightness / 100);
            for (int i = 0; i < p.Length; i++) p[i] = s;
            return p;
        }

        public string Describe()
        {
            if (!On || Brightness == 0) return "off";
            string b = " @ " + Brightness + "%";
            if (Mode == "effect")
            {
                var cs = new List<string>();
                foreach (var c in Colors) cs.Add("#" + Hex3(c));
                return "effect " + Effect + " (" + (Colors.Count == 0 ? Preset : string.Join(" ", cs)) + ", " +
                       Effects.Speeds[Speed] + ", " + Effects.Directions[Direction] + ")" + b;
            }
            return "static #" + Hex3(new[] { R, G, B }) + b;
        }
    }

    // ---------------------------------------------------------------------------------
    // Hotkeys. On this ZBook the keyboard is the EC's PS/2 device (ACPI HPQ8002):
    //   * Fn+1..9 send the same scancodes as 1..9, so they cannot be told apart.
    //   * The backlight key (F9 action) sends only a *break* of scancode 0x43 with no
    //     make. A real F9 (Fn+F9) sends make+break. That's how we recognise it.
    // Scheme:  backlight key       -> static: 100% -> 50% -> off -> 100%;  effects: on/off
    //          Right Alt + 0..9    -> pick mode slot (the digit is swallowed)
    // ---------------------------------------------------------------------------------
    static class Daemon
    {
        const uint VK_RMENU = 0xA5, VK_MASK = 0xE8;   // 0xE8 is unassigned: used to "mask" the lone Alt release

        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public KEYBDINPUT Ki; public long Pad; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr h, uint t);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, byte[] data, ref uint size, uint hdr);

        static HookProc _proc;
        static readonly BlockingCollection<string> Queue = new BlockingCollection<string>();
        static bool _console;

        // UI-thread state (raw input and the LL hook both run on the UI thread)
        static readonly HashSet<ushort> _madeKeys = new HashSet<ushort>();
        static readonly HashSet<uint> _swallowUp = new HashSet<uint>();
        static bool _rAltDown;

        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            if (_console) Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(State.Dir);
                string p = Path.Combine(State.Dir, "daemon.log");
                if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
                File.AppendAllText(p, line + Environment.NewLine);
            }
            catch (IOException) { }
        }

        sealed class Sink : NativeWindow
        {
            public Sink() { CreateHandle(new CreateParams()); }   // hidden top-level window
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x00FF) OnRawInput(m.LParam);       // WM_INPUT
                base.WndProc(ref m);
            }
        }

        public static int Run(bool console)
        {
            _console = console;
            bool created;
            using (var single = new Mutex(true, @"Local\hpkbd-daemon", out created))
            {
                if (!created) { Log("another daemon is already running"); return 0; }
                if (Process.GetProcessesByName("OmenCommandCenterBackground").Length > 0)
                    Log("warning: OMEN Gaming Hub's background task is running and may re-apply its own lighting");

                var worker = new Thread(Worker);
                worker.IsBackground = true;
                worker.Start();

                SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
                {
                    if (e.Mode == PowerModes.Resume) Queue.Add("resume");
                };
                SystemEvents.SessionSwitch += delegate(object s, SessionSwitchEventArgs e)
                {
                    if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect) Queue.Add("reapply");
                };

                var sink = new Sink();
                var rid = new[] { new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = 0x100 /*INPUTSINK*/, Target = sink.Handle } };
                if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                    Log("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());

                Queue.Add("reapply");
                Log("daemon started");

                // Install the LL hook last, right before pumping messages, so it never times out.
                _proc = Hook;
                IntPtr hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _proc, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) Log("SetWindowsHookEx failed (" + Marshal.GetLastWin32Error() + "); Right Alt + digit won't work");
                Application.Run();
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            }
            return 0;
        }

        static void OnRawInput(IntPtr lParam)
        {
            uint hdr = (uint)(8 + 2 * IntPtr.Size), size = 0;
            GetRawInputData(lParam, 0x10000003, null, ref size, hdr);
            if (size == 0 || size > 1024) return;
            var buf = new byte[size];
            if (GetRawInputData(lParam, 0x10000003, buf, ref size, hdr) != size) return;
            if (BitConverter.ToUInt32(buf, 0) != 1) return;               // RIM_TYPEKEYBOARD
            int o = (int)hdr;
            ushort make = BitConverter.ToUInt16(buf, o), flags = BitConverter.ToUInt16(buf, o + 2);
            ushort key = (ushort)(make | ((flags & 0x2) != 0 ? 0xE000 : 0)); // RI_KEY_E0
            bool isBreak = (flags & 0x1) != 0;
            if (!isBreak) { _madeKeys.Add(key); return; }
            bool hadMake = _madeKeys.Remove(key);
            if (key == 0x43 && !hadMake) Queue.Add("cycle");               // backlight key
        }

        static void SendMaskKey()
        {
            var inputs = new INPUT[2];
            inputs[0].Type = inputs[1].Type = 1;                            // INPUT_KEYBOARD
            inputs[0].Ki.Vk = inputs[1].Ki.Vk = (ushort)VK_MASK;
            inputs[1].Ki.Flags = 2;                                         // KEYEVENTF_KEYUP
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        static IntPtr Hook(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
                int msg = w.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                bool injected = (k.flags & 0x10) != 0;

                if (k.vk == VK_RMENU && !injected) _rAltDown = down;

                // Number row only: numpad digits with Alt are Windows Alt-codes.
                int digit = k.vk >= 0x30 && k.vk <= 0x39 ? (int)(k.vk - 0x30) : -1;
                if (down && digit >= 0 && !injected && _rAltDown)
                {
                    if (_swallowUp.Add(k.vk))                                // ignore auto-repeat
                    {
                        Queue.Add("slot:" + digit);
                        SendMaskKey();      // so releasing Alt isn't seen as a lone Alt tap (menu bar)
                    }
                    return new IntPtr(1);
                }
                if (up && _swallowUp.Remove(k.vk)) return new IntPtr(1);
            }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        public static void Mutate(State s, string act)
        {
            if (act == "cycle")
            {
                // Firmware animations ignore brightness, so effects just toggle on/off.
                if (!s.On || s.Brightness == 0) { s.On = true; s.Brightness = 100; }
                else if (s.Mode == "static" && s.Brightness > 50) s.Brightness = 50;
                else s.On = false;
            }
            else if (act.StartsWith("slot:"))
            {
                s.On = true;
                if (s.Brightness == 0) s.Brightness = 100;
                s.SelectSlot(int.Parse(act.Substring(5)));
            }
        }

        static void Worker()
        {
            foreach (var act in Queue.GetConsumingEnumerable())
            {
                try
                {
                    if (act == "resume") Thread.Sleep(2500);   // let USB re-enumerate
                    var st = State.Load();
                    Mutate(st, act);
                    st.Save();
                    if (Queue.Count > 0) { Log(act + " -> " + st.Describe() + " (superseded)"); continue; }
                    using (var k = new Kbd(Program.Interface)) st.Apply(k);
                    Log(act + " -> " + st.Describe());
                }
                catch (Exception e) { Log(act + " failed: " + e.Message); }
            }
        }
    }

    static class DaemonEntry
    {
        [STAThread]
        static int Main(string[] args) { return Daemon.Run(false); }
    }

    static class Program
    {
        public static string Interface = "02";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static void Usage()
        {
            Console.WriteLine(@"hpkbd - HP ZBook Studio G8 keyboard backlight control

  hpkbd status                         show saved state
  hpkbd on | off | toggle              backlight on / off (restores the last mode)
  hpkbd cycle                          brightness 100 -> 50 -> off (what the backlight key does)
  hpkbd brightness 0-100               static colours are scaled; effects use 4 levels
  hpkbd color RRGGBB [-b 0-100]        all keys one colour (static mode)
  hpkbd effect NAME [RRGGBB ...] [-p preset] [-s speed] [-d direction]
        NAME:   " + string.Join(" ", new List<string>(Effects.Names).ToArray()) + @"
        no args = OMEN's default look for that effect; colours: up to 6;
        -p volcano|jungle|ocean|galaxy uses a preset palette (not all effects take presets)
        -s slow|medium|fast   -d inward|outward|rtl|ltr|up|down
  hpkbd slot [0-9 [NAME|static]]       list slots / select one / set what Right Alt + digit selects
  hpkbd apply                          re-send the saved state
  hpkbd install | uninstall            start hpkbd-daemon.exe at login / remove it
  hpkbd daemon                         run the hotkey daemon in this console (debug)
  hpkbd info | raw HEX                 MCU device info / send one raw packet (debug)
  -v  dump packets

Hotkeys (needs the daemon): backlight key cycles 100% / 50% / off (effects: on/off);
Right Alt + 0-9 (number row) picks a slot. Slot 0 = static colour.");
        }

        public static byte[] ParseColor(string s)
        {
            s = s.TrimStart('#');
            if (s.Length != 6) throw new ArgumentException("Color must be RRGGBB: " + s);
            int v = int.Parse(s, NumberStyles.HexNumber);
            return new byte[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        public static byte[] ParseHex(string s)
        {
            s = s.Replace(" ", "");
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(s.Substring(2 * i, 2), NumberStyles.HexNumber);
            return b;
        }

        static int IndexOf(string[] arr, string v, string what)
        {
            int i = Array.IndexOf(arr, v.ToLowerInvariant());
            if (i < 0) throw new ArgumentException("Unknown " + what + " '" + v + "' (use " + string.Join("|", arr) + ")");
            return i;
        }

        [STAThread]
        static int Main(string[] args)
        {
            var a = new List<string>(args);
            bool verbose = a.Remove("-v");
            int ii = a.IndexOf("-i");
            if (ii >= 0) { Interface = a[ii + 1]; a.RemoveRange(ii, 2); }
            if (a.Count == 0 || a[0] == "-h" || a[0] == "--help") { Usage(); return 0; }

            try
            {
                var st = State.Load();
                switch (a[0])
                {
                    case "status": Console.WriteLine(st.Describe()); return 0;
                    case "daemon": return Daemon.Run(true);
                    case "install": return Install();
                    case "uninstall": return Uninstall();
                    case "on": st.On = true; if (st.Brightness == 0) st.Brightness = 100; break;
                    case "off": st.On = false; break;
                    case "toggle": st.On = !st.On; if (st.On && st.Brightness == 0) st.Brightness = 100; break;
                    case "cycle": Daemon.Mutate(st, "cycle"); break;
                    case "apply": break;
                    case "brightness": st.Brightness = Math.Max(0, Math.Min(100, int.Parse(a[1]))); st.On = st.Brightness > 0; break;
                    case "slot":
                        {
                            if (a.Count == 1)
                            {
                                for (int i = 0; i < 10; i++) Console.WriteLine("  Right Alt + " + i + "  ->  " + st.Slots[i]);
                                return 0;
                            }
                            int n = int.Parse(a[1]);
                            if (a.Count == 2) { st.On = true; st.SelectSlot(n); break; }
                            string name = a[2].ToLowerInvariant();
                            if (name != "static" && !Effects.Defs.ContainsKey(name)) throw new ArgumentException("Unknown effect " + name);
                            st.Slots[n] = name;
                            st.Save();
                            Console.WriteLine("slot " + n + " = " + name);
                            return 0;
                        }
                    case "color":
                        {
                            var c = ParseColor(a[1]);
                            st.R = c[0]; st.G = c[1]; st.B = c[2];
                            int bi = a.IndexOf("-b");
                            if (bi > 0) st.Brightness = Math.Max(0, Math.Min(100, int.Parse(a[bi + 1])));
                            if (st.Brightness == 0) st.Brightness = 100;
                            st.Mode = "static"; st.On = true;
                            break;
                        }
                    case "effect":
                        {
                            st.SelectEffect(a[1].ToLowerInvariant());
                            bool customColors = false;
                            for (int i = 2; i < a.Count; i++)
                            {
                                if (a[i] == "-s") { st.Speed = IndexOf(Effects.Speeds, a[++i], "speed"); continue; }
                                if (a[i] == "-d") { st.Direction = IndexOf(Effects.Directions, a[++i], "direction"); continue; }
                                if (a[i] == "-p")
                                {
                                    string p = a[++i].ToLowerInvariant();
                                    if (!Effects.Presets.ContainsKey(p)) throw new ArgumentException("Unknown preset " + p);
                                    st.Preset = p; st.Colors.Clear(); continue;
                                }
                                if (!customColors) { st.Colors.Clear(); customColors = true; }
                                st.Colors.Add(ParseColor(a[i]));
                            }
                            if (st.Colors.Count > 6) throw new ArgumentException("At most 6 colours");
                            if (st.Brightness == 0) st.Brightness = 100;
                            st.Mode = "effect"; st.On = true;
                            break;
                        }
                    case "info":
                    case "raw":
                        using (var k = new Kbd(Interface))
                        {
                            k.Verbose = verbose;
                            byte[] r;
                            if (a[0] == "info") r = k.Info();
                            else { var p = new byte[Device.PacketSize]; var h = ParseHex(string.Join("", a.GetRange(1, a.Count - 1))); Array.Copy(h, p, Math.Min(h.Length, p.Length)); r = k.Raw(p); }
                            Console.WriteLine(r == null ? "(no reply)" : Kbd.Hex(r, a[0] == "info" ? 24 : 64));
                            return r == null ? 2 : 0;
                        }
                    default: Usage(); return 1;
                }

                using (var k = new Kbd(Interface))
                {
                    k.Verbose = verbose;
                    st.Apply(k);
                }
                st.Save();
                Console.WriteLine(st.Describe());
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static string DaemonPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "hpkbd-daemon.exe"); }
        }

        static int Install()
        {
            if (!File.Exists(DaemonPath)) { Console.Error.WriteLine("missing " + DaemonPath + " (run build.cmd)"); return 1; }
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue("hpkbd", "\"" + DaemonPath + "\"");
            if (Process.GetProcessesByName("hpkbd-daemon").Length == 0) Process.Start(DaemonPath);
            Console.WriteLine("Installed: hpkbd-daemon starts at login (HKCU Run) and is running now.");
            return 0;
        }

        static int Uninstall()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (key != null) key.DeleteValue("hpkbd", false);
            foreach (var p in Process.GetProcessesByName("hpkbd-daemon")) { try { p.Kill(); } catch (Exception) { } }
            Console.WriteLine("Removed login entry and stopped the daemon.");
            return 0;
        }
    }
}
