using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Threading;

// Reads wireless-device battery over HID and prints a single integer percentage.
// Prints -1 when the device is absent or does not answer. Exits immediately.
static class BatteryRead
{
    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(IntPtr h, byte[] b, int len);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr en, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr devInfo, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, ref uint req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static string FindPath(string mustContain)
    {
        Guid hid; HidD_GetHidGuid(out hid);
        IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref did); i++)
            {
                uint need = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref need, IntPtr.Zero);
                if (need == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: 8 on x64, 6 on x86
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    uint req = 0;
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, ref req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    if (path != null && path.ToLowerInvariant().Contains(mustContain)) return path;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    // LAMZU Aurora protocol: 65-byte feature report, reply carries state at [7], level at [8].
    static int ReadLamzu()
    {
        string path = FindPath("vid_373e&pid_001e&mi_02");
        if (path == null) return -1;
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return -1;
        try
        {
            var req = new byte[65];
            req[0] = 0x00; req[3] = 0x02; req[4] = 0x02; req[6] = 0x83;
            if (!HidD_SetFeature(h, req, req.Length)) return -1;
            System.Threading.Thread.Sleep(60);
            var resp = new byte[65];
            resp[0] = 0x00;
            if (!HidD_GetFeature(h, resp, resp.Length)) return -1;
            if (resp[1] == 0xA1 && resp[4] == 0x02 && resp[6] == 0x83)
            {
                int level = resp[8];
                if (level >= 0 && level <= 100) return level;
            }
            return -1;
        }
        finally { CloseHandle(h); }
    }


    // ---------------------------------------------------------------------
    // Turtle Beach Atlas Air.
    // The headset speaks an undocumented vendor protocol, but Swarm II is
    // already running and logs the level continuously. Read that instead of
    // reverse engineering the device. Values older than STALE_MINUTES are
    // returned as stale (see ReadTurtleBeach) so the bar can show them dimmed.
    // ---------------------------------------------------------------------
    const int STALE_MINUTES = 10;

    static string NewestFile(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string best = null; DateTime bestTime = DateTime.MinValue;
        foreach (string f in Directory.GetFiles(dir, "*.txt"))
        {
            DateTime t = File.GetLastWriteTime(f);
            if (t > bestTime) { bestTime = t; best = f; }
        }
        return best;
    }

    static string[] SafeReadLines(string path)
    {
        // Swarm II holds these logs open, so share read AND write.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            var list = new System.Collections.Generic.List<string>();
            string line;
            while ((line = sr.ReadLine()) != null) list.Add(line);
            return list.ToArray();
        }
    }

    static bool Fresh(DateTime stamp)
    {
        return (DateTime.Now - stamp).TotalMinutes <= STALE_MINUTES;
    }

    // Newest battery reading in a log folder, with the timestamp of that line.
    // Freshness is decided by the caller so a stale value can still be shown.
    static bool LastReading(string dir, Regex rx, string tsFormat, int tsLen,
                            out int value, out DateTime stamp)
    {
        value = -1; stamp = DateTime.MinValue;
        string path = NewestFile(dir);
        if (path == null) return false;
        string[] lines;
        try { lines = SafeReadLines(path); } catch { return false; }
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            Match m = rx.Match(lines[i]);
            if (!m.Success) continue;
            int v = int.Parse(m.Groups[1].Value);
            if (v < 0 || v > 100) continue;
            DateTime ts;
            if (lines[i].Length >= tsLen &&
                DateTime.TryParseExact(lines[i].Substring(0, tsLen), tsFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                stamp = ts;
            value = v;
            return true;
        }
        return false;
    }

    // Encoded so the bar can always show a number:
    //   0..100       live reading
    //   value - 1000 last known reading, but Swarm II has not heard from the
    //                headset for STALE_MINUTES (the skin shows it dimmed)
    //   -1           no reading at all
    static int ReadTurtleBeach()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Turtle Beach", "Swarm II", "Log");

        int mv, tv; DateTime mts, tts;
        bool gotM = LastReading(Path.Combine(root, "MOSIT"),
                        new Regex(@"""240""\s*:\s*""(\d{1,3})"""), "yyyy-MM-dd HH:mm:ss.fff", 23, out mv, out mts);
        bool gotT = LastReading(Path.Combine(root, "BatteryTrayIcon"),
                        new Regex(@"f_battery_level:(\d{1,3})"), "yyyyMMdd_HH:mm:ss:fff", 21, out tv, out tts);

        // The two logs update independently - trust whichever spoke last.
        int value; DateTime stamp;
        if (gotM && gotT)   { if (tts > mts) { value = tv; stamp = tts; } else { value = mv; stamp = mts; } }
        else if (gotM)      { value = mv; stamp = mts; }
        else if (gotT)      { value = tv; stamp = tts; }
        else                { return -1; }

        return Fresh(stamp) ? value : value - 1000;
    }


    // ---------------------------------------------------------------------
    // Secondary readouts shown on the info line: mouse polling rate and
    // headset audio format. These change rarely, so the skin polls them
    // on a slower timer than the battery.
    // ---------------------------------------------------------------------
    static readonly string SEP = ((char)92).ToString();

    static string Hz(int hz)
    {
        if (hz <= 0) return "";
        return (hz >= 1000 && hz % 1000 == 0) ? (hz / 1000) + "K" : hz.ToString();
    }

    // LAMZU Aurora "get polling rate". Profile 1, as lamzu-ctl does.
    static int LamzuRateHz()
    {
        string path = FindPath("vid_373e&pid_001e&mi_02");
        if (path == null) return -1;
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return -1;
        try
        {
            var req = new byte[65];
            req[0] = 0x00; req[3] = 0x02; req[4] = 0x02; req[5] = 0x01; req[6] = 0x80; req[7] = 0x01;
            if (!HidD_SetFeature(h, req, req.Length)) return -1;
            System.Threading.Thread.Sleep(60);
            var resp = new byte[65];
            resp[0] = 0x00;
            if (!HidD_GetFeature(h, resp, resp.Length)) return -1;
            if (resp[1] < 0xA0 || resp[1] > 0xAF) return -1;
            int code = (resp[6] == 0x80) ? resp[8] : resp[7];
            switch (code)
            {
                // Bit-coded. Low nibble descends from 1000; high nibble ascends
                // from 2000. Confirmed on this 8K Maya X: profile 1 reads 0x40
                // and the mouse is set to 8000 Hz. lamzu-ctl's table maps 0x40
                // to 4000 because it merges the Compx and Aurora code sets and
                // predates 8K hardware -- do not trust it here.
                case 0x01: return 1000;
                case 0x02: return 500;
                case 0x04: return 250;
                case 0x08: return 125;
                case 0x10: return 2000;
                case 0x20: return 4000;
                case 0x40: return 8000;
                default:   return -1;
            }
        }
        finally { CloseHandle(h); }
    }

    // Turtle Beach audio format, from the Windows audio endpoint.
    // The registry blob is an 8-byte PROPVARIANT header then WAVEFORMATEXTENSIBLE.
    static string TurtleBeachAudio()
    {
        string render = string.Join(SEP, new string[] {
            "SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "MMDevices", "Audio", "Render" });
        string fmtName = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";
        string friendly = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(render))
        {
            if (root == null) return "";
            foreach (string sub in root.GetSubKeyNames())
            {
                using (RegistryKey dev = root.OpenSubKey(sub))
                {
                    if (dev == null) continue;
                    object state = dev.GetValue("DeviceState");
                    if (state == null || Convert.ToInt32(state) != 1) continue;   // active only
                    using (RegistryKey props = dev.OpenSubKey("Properties"))
                    {
                        if (props == null) continue;
                        string name = props.GetValue(friendly) as string;
                        if (name == null || name.IndexOf("Atlas", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        byte[] b = props.GetValue(fmtName) as byte[];
                        if (b == null || b.Length < 24) return "";
                        int avg = BitConverter.ToInt32(b, 16);      // nAvgBytesPerSec at blob+8+8
                        if (avg <= 0) return "";
                        double kbps = avg * 8.0 / 1000.0;
                        return kbps >= 1000
                            ? (kbps / 1000.0).ToString("0.#") + "M"
                            : Math.Round(kbps).ToString() + "k";
                    }
                }
            }
        }
        return "";
    }


    // Two BatteryRead processes can be launched at once (battery + rate).
    // Concurrent opens of the same HID handle make the exchange fail, so
    // serialise access across processes and retry a transient failure.
    const string LAMZU_MUTEX = "BatteryRead_Lamzu_HID";

    static int WithLamzuLock(Func<int> body)
    {
        using (var mtx = new Mutex(false, LAMZU_MUTEX))
        {
            bool held = false;
            try { held = mtx.WaitOne(3000); }
            catch (AbandonedMutexException) { held = true; }
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int r = body();
                    if (r >= 0) return r;
                    Thread.Sleep(80);
                }
                return -1;
            }
            finally { if (held) mtx.ReleaseMutex(); }
        }
    }


    const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    struct OVERLAPPED { public IntPtr I; public IntPtr IH; public uint O; public uint OH; public IntPtr hEvent; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, uint n, IntPtr read, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, uint n, IntPtr written, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEvent(IntPtr attr, bool manual, bool init, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED ov, out uint n, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIo(IntPtr h);

    // ---------------------------------------------------------------------
    // Lofree Hyzen67. Protocol decoded from the Lofree Hub web configurator.
    // Report id 4, 63-byte frames:
    //   [0]=00 [1]=00 [2]=cmd [3]=len [4]=offLo [5]=offHi [6]=00 [7..]=payload
    // Handshake: 3-byte report id 4, [00,00,01]=START and [00,00,02]=END.
    // Battery is command 0x1A with an empty payload; percent = payload[0].
    // Buffers here carry the report id in byte 0, so every index is +1
    // relative to the WebHID code this came from.
    // ---------------------------------------------------------------------
    const int LOFREE_REPORT_ID = 4;
    const int LOFREE_FRAME = 63;
    const string LOFREE_MUTEX = "BatteryRead_Lofree_HID";

    // Windows WriteFile on a HID device requires exactly OutputReportByteLength
    // bytes; unlike hidapi it does not pad short buffers. Always send a full frame.
    static bool LofreeWrite(IntPtr h, byte[] head)
    {
        var buf = new byte[LOFREE_FRAME + 1];
        Array.Copy(head, buf, Math.Min(head.Length, buf.Length));
        var ov = new OVERLAPPED();
        ov.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (ov.hEvent == IntPtr.Zero) return false;
        try
        {
            if (!WriteFile(h, buf, (uint)buf.Length, IntPtr.Zero, ref ov))
            {
                if (Marshal.GetLastWin32Error() != 997) return false;      // ERROR_IO_PENDING
                if (WaitForSingleObject(ov.hEvent, 1000) != 0) { CancelIo(h); return false; }
            }
            uint n;
            return GetOverlappedResult(h, ref ov, out n, false);
        }
        finally { CloseHandle(ov.hEvent); }
    }

    static byte[] LofreeRead(IntPtr h, int timeoutMs)
    {
        var buf = new byte[LOFREE_FRAME + 1];
        var ov = new OVERLAPPED();
        ov.hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (ov.hEvent == IntPtr.Zero) return null;
        try
        {
            if (!ReadFile(h, buf, (uint)buf.Length, IntPtr.Zero, ref ov))
            {
                if (Marshal.GetLastWin32Error() != 997) return null;   // ERROR_IO_PENDING
                if (WaitForSingleObject(ov.hEvent, (uint)timeoutMs) != 0)
                {
                    CancelIo(h);
                    return null;
                }
            }
            uint got;
            if (!GetOverlappedResult(h, ref ov, out got, false) || got == 0) return null;
            return buf;
        }
        finally { CloseHandle(ov.hEvent); }
    }

    static void LofreeDrain(IntPtr h, int ms)
    {
        while (LofreeRead(h, ms) != null) { }
    }

    static bool DBG = Environment.GetEnvironmentVariable("BR_DEBUG") == "1";
    static void D(string m) { if (DBG) Console.Error.WriteLine("[dbg] " + m); }

    static int LofreeQuery(IntPtr h, byte cmd)
    {
        LofreeDrain(h, 40);
        if (!LofreeWrite(h, new byte[] { LOFREE_REPORT_ID, 0x00, 0x00, 0x01 }))
        { D("START write failed err=" + Marshal.GetLastWin32Error()); return -1; }
        byte[] ack = LofreeRead(h, 300);
        D("ack=" + (ack == null ? "null" : string.Join(",", ack, 0, 8)));
        if (ack == null || ack[3] != 0x01) return -1;

        var frame = new byte[LOFREE_FRAME + 1];
        frame[0] = LOFREE_REPORT_ID;
        frame[3] = cmd;               // [2] in WebHID terms
        if (!LofreeWrite(h, frame)) return -1;

        int value = -1;
        for (int i = 0; i < 6; i++)
        {
            byte[] r = LofreeRead(h, 300);
            D("resp=" + (r == null ? "null" : string.Join(",", r, 0, 10)));
            if (r == null) break;
            if (r[3] == cmd) { value = r[8]; break; }   // payload[0]
        }
        LofreeWrite(h, new byte[] { LOFREE_REPORT_ID, 0x00, 0x00, 0x02 });  // END
        LofreeDrain(h, 60);
        return value;
    }

    static int ReadLofreeCore()
    {
        string path = FindPath("vid_388d&pid_0025&mi_01&col04");
        D("path=" + (path ?? "NULL"));
        if (path == null) return -1;
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h == new IntPtr(-1)) { D("CreateFile failed err=" + Marshal.GetLastWin32Error()); return -1; }
        try
        {
            // One query instead of two: the 0xAA status probe doubled the cost
            // of this reader (measured 365ms -> 216ms) and a battery of exactly 0 serves
            // the same purpose. A connected keyboard never reports 0 -- it would
            // be dead -- so treat 0 as offline/unknown and show a dash.
            int v = LofreeQuery(h, 0x1A);
            D("battery=" + v);
            return (v >= 1 && v <= 100) ? v : -1;
        }
        finally { CloseHandle(h); }
    }

    static int ReadLofree()
    {
        using (var mtx = new Mutex(false, LOFREE_MUTEX))
        {
            bool held = false;
            try { held = mtx.WaitOne(3000); }
            catch (AbandonedMutexException) { held = true; }
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int r = ReadLofreeCore();
                    if (r >= 0) return r;
                    Thread.Sleep(100);
                }
                return -1;
            }
            finally { if (held) mtx.ReleaseMutex(); }
        }
    }

    static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0].ToLowerInvariant() : "lamzu";
        int v = -1;
        try {
            if (which == "lamzu") v = WithLamzuLock(ReadLamzu);
            else if (which == "tbair") v = ReadTurtleBeach();
            else if (which == "lofree") v = ReadLofree();
            else if (which == "lamzu-rate") { int hz = WithLamzuLock(LamzuRateHz); Console.Out.Write(hz > 0 ? Hz(hz) : ""); return 0; }
            else if (which == "tbair-rate") { Console.Out.Write(TurtleBeachAudio()); return 0; }
        } catch { v = -1; }
        Console.Out.Write(v);
        return 0;
    }
}
