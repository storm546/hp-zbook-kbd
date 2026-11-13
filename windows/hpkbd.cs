// hpkbd - device and protocol layer for the HP ZBook Studio G8 (board 8873,
// "Modena") per-key RGB keyboard.
//
// The LEDs are driven by an internal USB MCU (NXP, 1FC9:2238). Its interface
// MI_02 is a vendor HID collection with 64-byte reports that speaks HP's
// "McuSDK2 / General" command set (what OMEN Gaming Hub's Mailroom +
// StarmadeKbLightingControl use for "ModenaPerKeyRGB"). See ../PROTOCOL.md.
//
// C# 5 only (that is what the in-box csc supports). Build with build.cmd.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HpKbd
{
    static class Native
    {
        public static readonly Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA did);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);
    }

    sealed class Device : IDisposable
    {
        public const int PacketSize = 64;
        public string Path;
        public int OutLen, InLen;
        SafeFileHandle _h;
        FileStream _fs;

        public static List<string> Enumerate(string match)
        {
            var list = new List<string>();
            Guid g = Native.HidGuid;
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            try
            {
                for (int i = 0; ; i++)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(did);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                    int req;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                            if (p.ToLowerInvariant().Contains(match)) list.Add(p);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static Device Open(string mi)
        {
            var paths = Enumerate("vid_1fc9&pid_2238&mi_" + mi);
            if (paths.Count == 0) throw new Exception("Keyboard MCU (1FC9:2238 MI_" + mi + ") not found.");
            var d = new Device();
            d.Path = paths[0];
            d._h = Native.CreateFile(d.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (d._h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open " + d.Path);

            IntPtr pp;
            if (!Native.HidD_GetPreparsedData(d._h, out pp)) throw new Exception("HidD_GetPreparsedData failed");
            try
            {
                Native.HIDP_CAPS caps;
                Native.HidP_GetCaps(pp, out caps);
                d.OutLen = caps.OutputReportByteLength;
                d.InLen = caps.InputReportByteLength;
            }
            finally { Native.HidD_FreePreparsedData(pp); }
            if (d.OutLen != PacketSize + 1) throw new Exception("Unexpected output report length " + d.OutLen);
            d._fs = new FileStream(d._h, FileAccess.ReadWrite, d.OutLen, true);
            return d;
        }

        public void Flush() { Native.HidD_FlushQueue(_h); }

        public void Write(byte[] packet)
        {
            var buf = new byte[OutLen];            // buf[0] = report id 0 (unnumbered)
            Array.Copy(packet, 0, buf, 1, Math.Min(packet.Length, PacketSize));
            _fs.Write(buf, 0, buf.Length);
        }

        // Returns the 64-byte payload or null on timeout.
        public byte[] Read(int timeoutMs)
        {
            var buf = new byte[InLen];
            var ar = _fs.BeginRead(buf, 0, buf.Length, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;   // pending read is cancelled on Dispose
            int n = _fs.EndRead(ar);
            if (n <= 1) return null;
            var r = new byte[n - 1];
            Array.Copy(buf, 1, r, 0, n - 1);
            return r;
        }

        public void Dispose()
        {
            if (_fs != null) _fs.Dispose();
            if (_h != null) _h.Dispose();
        }
    }

    // Packet builders mirroring Hp.Bridge.Client.SDKs.McuSDK2.General.Commands.GeneralCommandHelper.
    // Packet: [cmd, index, len_lo, len_hi, payload...] zero padded to 64.
    // Reply:  [cmd, index, .., .., 0xEC, status]  status 0xAC = ok, 0xFA = fail, 0xBD = auto report.
    static class Proto
    {
        public const byte CMD_SET_LIGHTING_EFFECT = 0x03;
        public const byte CMD_KEY_R = 0x05, CMD_KEY_G = 0x06, CMD_KEY_B = 0x07;
        public const byte CMD_LIGHTING_ON_OFF = 0x09;
        public const byte CMD_STORE_LIGHTING_TO_FLASH = 0x0A;   // not used: we re-apply instead of wearing flash
        public const byte CMD_GET_DEVICE_INFO = 0x80;
        public const int KEY_COUNT = 144;                       // what OMEN sends for Modena (0x90)
        public const int PAGE = 60;

        static byte[] Pkt(byte cmd, byte index, int len)
        {
            var p = new byte[Device.PacketSize];
            p[0] = cmd; p[1] = index; p[2] = (byte)(len & 0xFF); p[3] = (byte)(len >> 8);
            return p;
        }

        public static byte[] GetDeviceInfo() { return Pkt(CMD_GET_DEVICE_INFO, 1, 0); }

        // CommonToggleEnum: OFF = 0, START = 1 (STOP = 0xFF is not "on"!)
        public static byte[] LightingOn() { var p = Pkt(CMD_LIGHTING_ON_OFF, 0, 1); p[4] = 0x01; return p; }
        public static byte[] LightingOff() { var p = Pkt(CMD_LIGHTING_ON_OFF, 0, 1); p[4] = 0x00; return p; }

        // GeneralCommandHelper.CreateStaticCMD: the length field is left 0; 60 + 60 + rest per channel.
        public static List<byte[]> StaticMaps(byte[] r, byte[] g, byte[] b)
        {
            var l = new List<byte[]>();
            byte[][] maps = { r, g, b };
            for (int c = 0; c < 3; c++)
                for (int page = 0; page < 3; page++)
                {
                    int start = page * PAGE;
                    int len = Math.Min(PAGE, maps[c].Length - start);
                    var p = Pkt((byte)(CMD_KEY_R + c), (byte)page, 0);
                    Array.Copy(maps[c], start, p, 4, len);
                    l.Add(p);
                }
            return l;
        }

        // GeneralCommandHelper.SetLightingEffect(LightingSetting, target).
        public static byte[] Effect(byte effect, byte showMode, byte colorNumber, byte speed, byte brightnessLv,
                                    byte direction, byte rippleSize, byte raindrop, IList<byte[]> colors)
        {
            var p = Pkt(CMD_SET_LIGHTING_EFFECT, 0 /*ALL_LED_AREA*/, 0x24);
            p[4] = effect; p[5] = showMode; p[6] = colorNumber; p[7] = speed; p[8] = brightnessLv;
            p[9] = direction; p[10] = rippleSize; p[11] = raindrop;
            if (colors != null)
                for (int i = 0; i < Math.Min(6, colors.Count); i++) Array.Copy(colors[i], 0, p, 28 + 3 * i, 3);
            return p;
        }
    }

    sealed class Kbd : IDisposable
    {
        readonly Device _d;
        readonly Mutex _omenMutex;
        public bool Verbose;

        public Kbd(string mi)
        {
            _d = Device.Open(mi);
            // Share OMEN's legacy McuSDK I/O mutex so we never interleave a sequence with it.
            Mutex m;
            if (Mutex.TryOpenExisting("WoodstockIoMutex", out m)) _omenMutex = m;
        }

        public Device Dev { get { return _d; } }

        T Locked<T>(Func<T> f)
        {
            bool held = false;
            try
            {
                if (_omenMutex != null)
                {
                    try { held = _omenMutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                }
                _d.Flush();
                return f();
            }
            finally { if (held) _omenMutex.ReleaseMutex(); }
        }

        byte[] Exec(byte[] pkt)
        {
            if (Verbose) Console.Error.WriteLine(">> " + Hex(pkt, 4 + Math.Max((int)pkt[2], 8)));
            _d.Write(pkt);
            // Skip unrelated input (e.g. auto reports) until the echo of our command arrives.
            var deadline = DateTime.UtcNow.AddMilliseconds(1000);
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) { if (Verbose) Console.Error.WriteLine("<< (timeout)"); return null; }
                var r = _d.Read(left);
                if (r == null) { if (Verbose) Console.Error.WriteLine("<< (timeout)"); return null; }
                if (Verbose) Console.Error.WriteLine("<< " + Hex(r, 12));
                if (r[0] == pkt[0] && r[1] == pkt[1]) return r;
            }
        }

        void Step(byte[] pkt)
        {
            var r = Exec(pkt);
            if (r == null) throw new Exception("MCU did not reply to command 0x" + pkt[0].ToString("X2"));
            if (r[4] != 0xEC || r[5] != 0xAC)
                throw new Exception("MCU rejected command 0x" + pkt[0].ToString("X2") + ": " + Hex(r, 8));
        }

        public byte[] Info() { return Locked(delegate { return Exec(Proto.GetDeviceInfo()); }); }
        public void On() { Locked(delegate { Step(Proto.LightingOn()); return 0; }); }
        public void Off() { Locked(delegate { Step(Proto.LightingOff()); return 0; }); }

        // StarmadeKbLightingControl.ResetLighting (static mode): on, then the 9 colour pages.
        public void SetKeys(byte[] r, byte[] g, byte[] b)
        {
            Locked(delegate
            {
                Step(Proto.LightingOn());
                foreach (var p in Proto.StaticMaps(r, g, b)) Step(p);
                return 0;
            });
        }

        public void SetEffect(byte[] effectPacket)
        {
            Locked(delegate
            {
                Step(Proto.LightingOn());
                Step(effectPacket);
                return 0;
            });
        }

        public byte[] Raw(byte[] pkt) { return Locked(delegate { return Exec(pkt); }); }

        public static string Hex(byte[] b, int n)
        {
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(n, b.Length); i++) parts.Add(b[i].ToString("X2"));
            return string.Join(" ", parts);
        }

        public void Dispose() { _d.Dispose(); if (_omenMutex != null) _omenMutex.Dispose(); }
    }
}
