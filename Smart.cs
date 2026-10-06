using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Reclaim;

/// <summary>SMART health readout. Tries real ATA SMART pass-through first
/// (SMART_RCV_DRIVE_DATA - gives the raw attribute table), falls back to
/// Storage reliability counters via PowerShell for NVMe/USB-bridge devices.</summary>
public static class Smart
{
    private const uint SMART_ENABLE_OPS   = 0x0007C084; // SMART_SEND_DRIVE_COMMAND
    private const uint SMART_RCV_DATA     = 0x0007C088; // SMART_RCV_DRIVE_DATA
    private const byte ATA_SMART          = 0xB0;
    private const byte SMART_READ_VALUES  = 0xD0;
    private const byte SMART_READ_THRESH  = 0xD1;
    private const byte SMART_ENABLE       = 0xD8;
    private const byte CYL_LO = 0x4F, CYL_HI = 0xC2;

    public sealed record Attr(byte Id, string Name, ushort Flags, byte Value,
                            byte Worst, byte[] Raw, byte? Thresh)
    {
        public long RawLong => Raw.Take(6).Select((b, i) => (long)b << (8 * i)).Sum();
        public string RawHex => BitConverter.ToString(Raw).Replace("-", " ");
        public bool Failing => Thresh.HasValue && Value <= Thresh.Value && Thresh.Value > 0;
    }

    public enum Light { Unknown, Green, Amber, Red }

    public sealed record Report(bool Ok, string Source, List<Attr> Attrs,
                                Dictionary<string, string> Extra, string? Error)
    {
        public string Summary =>
            !Ok ? $"SMART unavailable: {Error}"
                : Attrs.Count == 0 ? "SMART OK (no attribute table)"
                : $"{Attrs.Count} attributes, {Attrs.Count(a => a.Failing)} below threshold";

        /// <summary>Traffic-light verdict. Red = threshold breach / heavy reallocations
        /// / worn out; Amber = nonzero error counters, moderate wear, or hot;
        /// Unknown = SMART unreadable on this controller.</summary>
        public Light TrafficLight
        {
            get
            {
                if (!Ok) return Light.Unknown;
                if (Attrs.Count > 0)
                {
                    if (Attrs.Any(a => a.Failing)) return Light.Red;
                    long crit = Attrs.Where(a => a.Id is 5 or 187 or 196 or 197 or 198)
                                     .Sum(a => a.RawLong);
                    if (crit >= 10) return Light.Red;
                    long warn = crit + Attrs.Where(a => a.Id is 188 or 199)
                                            .Sum(a => a.RawLong);
                    if (warn > 0) return Light.Amber;
                    return Light.Green;
                }
                int wear = Num("Wear"), temp = Num("Temperature");
                long ucErr = Long("ReadErrorsUncorrected") + Long("WriteErrorsUncorrected");
                if (wear >= 90 || temp >= 75) return Light.Red;
                if (wear >= 50 || temp >= 60 || ucErr > 0) return Light.Amber;
                return Light.Green;
            }
        }

        private int Num(string k) =>
            Extra.TryGetValue(k, out var v) && int.TryParse(v, out int n) ? n : 0;
        private long Long(string k) =>
            Extra.TryGetValue(k, out var v) && long.TryParse(v, out long n) ? n : 0;
    }

    // well-known attribute ids
    private static readonly Dictionary<byte, string> Names = new()
    {
        [1] = "Raw Read Error Rate", [2] = "Throughput Performance",
        [3] = "Spin-Up Time", [4] = "Start/Stop Count", [5] = "Reallocated Sector Count",
        [7] = "Seek Error Rate", [8] = "Seek Time Performance", [9] = "Power-On Hours",
        [10] = "Spin Retry Count", [11] = "Calibration Retry Count", [12] = "Power Cycle Count",
        [13] = "Read Soft Error Rate",
        [160] = "Uncorrectable Errors", [161] = "Valid Spare Blocks",
        [163] = "Bad Block Count (Early)", [164] = "Bad Block Count (Late)",
        [165] = "Max Erase Count", [166] = "Min Erase Count", [167] = "Average Erase Count",
        [168] = "Max NAND Erase (Spec)", [169] = "Remaining Lifetime %",
        [170] = "Reserved Block Count", [171] = "Program Fail Count",
        [172] = "Erase Fail Count", [173] = "Wear Leveling Count",
        [174] = "Unexpected Power-Loss", [175] = "Program Fail (Worst Die)",
        [176] = "Unused Reserved Blocks", [177] = "Wear Range Delta",
        [178] = "Used Reserved Block (Worst)", [179] = "Used Reserved Block (Total)",
        [180] = "Unused Reserved (Total)", [181] = "Program Fail Total",
        [182] = "Erase Fail Total", [183] = "Runtime Bad Block", [184] = "End-to-End Error",
        [187] = "Reported Uncorrectable", [188] = "Command Timeout",
        [189] = "High Fly Writes", [190] = "Airflow Temperature",
        [191] = "G-Sense Errors", [192] = "Power-Off Retract Count",
        [193] = "Load/Unload Cycle Count", [194] = "Temperature (×0.1? raw)",
        [195] = "Hardware ECC Recovered", [196] = "Reallocation Event Count",
        [197] = "Current Pending Sectors", [198] = "Offline Uncorrectable",
        [199] = "UDMA CRC Error Count", [200] = "Multi-Zone Error Rate",
        [201] = "Soft Read Error Rate", [202] = "Data Address Mark Errors",
        [203] = "Run-Out Cancel", [204] = "Soft ECC Correction",
        [205] = "Thermal Asperity Rate", [206] = "Flying Height",
        [207] = "Spin High Current", [208] = "Spin Buzz", [209] = "Offline Seek Perf",
        [210] = "Vibration During Write", [211] = "Vibration During Read",
        [212] = "Shock During Write", [213] = "Shock During Read",
        [214] = "Shock Damage", [215] = "Shock Count",
        [216] = "Vibration Total", [217] = "Head Load Hours",
        [218] = "Head Retract Count", [219] = "Head Load",
        [220] = "Disk Shift", [221] = "G-Sense Error Rate",
        [222] = "Loaded Hours", [223] = "Load/Unload Retry",
        [224] = "Load Friction", [225] = "Load/Unload Cycle (Host)",
        [226] = "Load-in Time", [227] = "Torque Amplification",
        [228] = "Power-Off Retract",
        [230] = "Head Amplitude", [231] = "SSD Life Left",
        [232] = "Available Reserved Space", [233] = "Media Wearout Indicator",
        [234] = "Avg Erase/Max Erase", [235] = "Good Block/System Block",
        [240] = "Head Flying Hours", [241] = "Total LBAs Written",
        [242] = "Total LBAs Read", [243] = "Total LBAs Written (Ext)",
        [244] = "Total LBAs Read (Ext)", [245] = "NAND Writes (GiB?)",
        [246] = "SSD Total Erase Count", [247] = "SSD Program Count",
        [248] = "SSD Erase Count", [249] = "NAND Writes (1GiB)",
        [250] = "Read Error Retry Rate", [251] = "Min Spares Remaining",
        [252] = "Newly Added Bad Flash", [253] = "Free Fall Protection",
        [254] = "Free Fall Events", [255] = "NAND Read Retry",
    };

    /// <summary>Enable SMART then read the attribute + threshold tables.</summary>
    public static Report Read(int diskNum)
    {
        try
        {
            var h = Native.CreateFile($@"\\.\PhysicalDrive{diskNum}",
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                // retry read-only; some drivers accept SMART without write access
                h = Native.CreateFile($@"\\.\PhysicalDrive{diskNum}",
                    Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); return Fallback(diskNum, "cannot open drive"); }
            }
            using (h)
            {
                SmartCmd(h, SMART_ENABLE_OPS, SMART_ENABLE);         // enable ops
                var attrs = SmartCmd(h, SMART_RCV_DATA, SMART_READ_VALUES);
                var thresh = SmartCmd(h, SMART_RCV_DATA, SMART_READ_THRESH);
                if (attrs == null) return Fallback(diskNum, "SMART ioctl failed (NVMe/USB bridge?)");

                var map = new Dictionary<byte, byte>();
                if (thresh != null)
                    for (int i = 0; i < 30; i++)
                    {
                        int b = 2 + i * 12;
                        if (thresh[b] != 0) map[thresh[b]] = thresh[b + 10];
                    }

                var list = new List<Attr>();
                for (int i = 0; i < 30; i++)
                {
                    int b = 2 + i * 12;
                    byte id = attrs[b];
                    if (id == 0) continue;
                    var raw = new byte[6];
                    Array.Copy(attrs, b + 5, raw, 0, 6);
                    list.Add(new Attr(id,
                        Names.TryGetValue(id, out var n) ? n : $"Vendor #{id}",
                        BitConverter.ToUInt16(attrs, b + 1),
                        attrs[b + 3], attrs[b + 4], raw,
                        map.TryGetValue(id, out var t) ? t : null));
                }
                return new Report(true, "ATA pass-through", list, ReadExtra(diskNum), null);
            }
        }
        catch (Exception ex) { return Fallback(diskNum, ex.Message); }
    }

    /// <summary>Issue a SMART command and return the 512-byte data page.</summary>
    private static byte[]? SmartCmd(Microsoft.Win32.SafeHandles.SafeFileHandle h,
                                    uint ioctl, byte feature)
    {
        // SENDCMDINPARAMS: bufsize + 8 IDEREGS + driveNum + 3 resv + 4 resv dwords
        var inp = new byte[29];
        BitConverter.GetBytes((uint)512).CopyTo(inp, 0);
        inp[4] = feature;          // bFeaturesReg
        inp[5] = 0;                // bSectorCountReg
        inp[6] = 0;                // bSectorNumberReg
        inp[7] = CYL_LO;           // bCylLowReg
        inp[8] = CYL_HI;           // bCylHighReg
        inp[9] = 0xA0;             // bDriveHeadReg
        inp[10] = ATA_SMART;       // bCommandReg
        // SENDCMDOUTPARAMS: 4+16 driverstatus + 512 data
        var outp = new byte[4 + 16 + 512];
        if (!Native.DeviceIoControl(h, ioctl, inp, (uint)inp.Length,
                outp, (uint)outp.Length, out uint n, IntPtr.Zero))
            return null;
        var data = new byte[512];
        Array.Copy(outp, 20, data, 0, 512);
        return data;
    }

    /// <summary>Temperature/wear/power-on-hours via storage reliability counters
    /// (works for NVMe where ATA pass-through fails).</summary>
    private static Dictionary<string, string> ReadExtra(int diskNum)
    {
        var map = new Dictionary<string, string>();
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"" +
                $"Get-PhysicalDisk -DeviceNumber {diskNum} | Get-StorageReliabilityCounter | " +
                "Select-Object Temperature,PowerOnHours,Wear,ReadErrorsTotal,ReadErrorsUncorrected," +
                "WriteErrorsTotal,WriteErrorsUncorrected,ReadLatencyMax,WriteLatencyMax," +
                "FlushLatencyMax | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(json)) return map;
            var d = JsonDocument.Parse(json).RootElement;
            foreach (var prop in d.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    map[prop.Name] = prop.Value.ToString();
        }
        catch { }
        return map;
    }

    private static Report Fallback(int diskNum, string why)
    {
        var extra = ReadExtra(diskNum);
        return new Report(extra.Count > 0, "storage counters", new(), extra,
                          extra.Count == 0 ? why : null);
    }

    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SMART via {r.Source} — {r.Summary}");
        sb.AppendLine();
        foreach (var kv in r.Extra)
            sb.AppendLine($"  {kv.Key,-28} {kv.Value}");
        if (r.Attrs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {"ID",-4} {"Attribute",-30} {"Cur",4} {"Worst",6} {"Thr",4} {"Raw",-20} Verdict");
            foreach (var a in r.Attrs.OrderBy(a => a.Id))
                sb.AppendLine($"  {a.Id,-4} {a.Name,-30} {a.Value,4} {a.Worst,6} " +
                              $"{(a.Thresh?.ToString() ?? "-"),4} {a.RawHex,-20} " +
                              $"{(a.Failing ? "!! FAIL !!" : "ok")}");
        }
        return sb.ToString();
    }
}
