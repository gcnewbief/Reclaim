namespace Reclaim;

/// <summary>Tier-0 disk triage: parse the partition table (MBR or GPT) and
/// probe each partition's boot sector to classify the filesystem and its
/// health. This is what decides which scan tier a volume needs.</summary>
public sealed class VolumeInfo
{
    public long Offset;
    public long Size;
    public string FsName = "unknown";
    public string Label = "";
    public string Verdict = "";

    // NTFS geometry (filled when FsName == "NTFS")
    public int BytesPerSector;
    public int ClusterSize;
    public long MftLcn;
    public long MftMirrLcn;
    public int MftRecordSize = 1024;
    public bool BootSectorOk;

    public string OffsetHex => $"0x{Offset:X}";
    public string SizeHuman => Size >= 1L << 30 ? $"{Size / (double)(1L << 30):0.#} GB"
                                               : $"{Size / (double)(1L << 20):0.#} MB";
}

public static class Volumes
{
    /// <summary>Detect filesystem from a boot sector. Returns name or null.</summary>
    public static string? DetectFs(byte[] s)
    {
        if (s.Length < 512) return null;
        if (s[510] == 0x55 && s[511] == 0xAA)
        {
            if (Ascii(s, 3, 8) == "NTFS    ") return "NTFS";
            if (Ascii(s, 3, 8) == "EXFAT   ") return "exFAT";
            if (Ascii(s, 82, 5) == "FAT32") return "FAT32";
            if (Ascii(s, 54, 5) is "FAT16" or "FAT12" or "FAT  ") return "FAT16";
            if (Ascii(s, 3, 8) == "MSDOS5.0") return "FAT";
            if (Ascii(s, 3, 8) == "MSWIN4.1") return "FAT";
        }
        if (Ascii(s, 3, 8) == "-FVE-FS-") return "BitLocker";
        if (Ascii(s, 0, 8) == "EFI PART") return "GPT header";
        return null;
    }

    private static string Ascii(byte[] b, int off, int len) =>
        off + len <= b.Length ? System.Text.Encoding.ASCII.GetString(b, off, len) : "";

    /// <summary>Parse NTFS BPB from a boot sector.</summary>
    public static void ParseNtfsBoot(VolumeInfo v, byte[] s)
    {
        v.BytesPerSector = BitConverter.ToUInt16(s, 11);
        int spc = s[13];
        v.ClusterSize = v.BytesPerSector * spc;
        v.MftLcn = BitConverter.ToInt64(s, 48);
        v.MftMirrLcn = BitConverter.ToInt64(s, 56);
        sbyte rec = (sbyte)s[64];          // negative => 2^-n bytes
        v.MftRecordSize = rec < 0 ? 1 << -rec : rec * v.ClusterSize;
        v.BootSectorOk = v.BytesPerSector is 512 or 4096 && spc > 0;
    }

    /// <summary>Tier 0: enumerate volumes on the disk. Handles MBR, GPT, and
    /// "no partition table at all" (falls back to hunting for boot sectors).</summary>
    public static List<VolumeInfo> Triage(RawDisk disk, List<string> log)
    {
        var vols = new List<VolumeInfo>();
        var s0 = disk.ReadAt(0, 512);

        bool mbr = s0[510] == 0x55 && s0[511] == 0xAA;
        var s1 = disk.ReadAt(512, 512);
        bool gpt = Ascii(s1, 0, 8) == "EFI PART";

        if (mbr && !gpt)
        {
            log.Add("Partition table: MBR signature present");
            for (int i = 0; i < 4; i++)
            {
                int o = 446 + i * 16;
                byte ptype = s0[o + 4];
                int lba = BitConverter.ToInt32(s0, o + 8);
                int cnt = BitConverter.ToInt32(s0, o + 12);
                if (ptype == 0 || cnt <= 0) continue;
                var v = ProbeVolume(disk, (long)lba * 512, (long)cnt * 512, log, $"MBR type 0x{ptype:X2}");
                vols.Add(v);
            }
        }
        else if (gpt)
        {
            log.Add("Partition table: GPT");
            // GPT entry array at LBA2+, 128 bytes each
            uint nEnt = BitConverter.ToUInt32(s1, 80);
            uint entSz = BitConverter.ToUInt32(s1, 84);
            ulong entLba = BitConverter.ToUInt64(s1, 72);
            int want = (int)Math.Min(nEnt * entSz, 128 * 1024);
            var entries = disk.ReadAt((long)entLba * 512, want);
            for (int i = 0; i + 128 <= entries.Length; i += (int)Math.Max(entSz, 128))
            {
                ulong lo = BitConverter.ToUInt64(entries, i + 32);
                ulong hi = BitConverter.ToUInt64(entries, i + 40);
                if (lo == 0 || hi <= lo) continue;
                var v = ProbeVolume(disk, (long)lo * 512, (long)(hi - lo + 1) * 512, log, "GPT");
                vols.Add(v);
            }
        }
        else
        {
            log.Add("Partition table: ABSENT/CORRUPT — hunting for filesystem signatures");
            // hunt for a boot sector in the usual places + scan first 16GB
            var spots = new List<long> { 0x100000, 0x200000, 0x400000, 0x800000 };  // 1,2,4,8 MB
            for (long o = 0; o < Math.Min(disk.Size, 16L << 30); o += 4L << 20)
                spots.Add(o);
            foreach (var o in spots.OrderBy(x => x))
            {
                var s = disk.ReadAt(o, 4096);    // covers 4K-sector boot too
                for (int sub = 0; sub <= 4096 - 512; sub += 512)
                {
                    var fs = DetectFs(s.AsSpan(sub, 512).ToArray());
                    if (fs == "NTFS")
                    {
                        var v = new VolumeInfo { Offset = o + sub, FsName = "NTFS" };
                        ParseNtfsBoot(v, s.AsSpan(sub, 512).ToArray());
                        v.Size = disk.Size - v.Offset;   // unknown end
                        v.Verdict = "NTFS boot sector found by hunt";
                        vols.Add(v);
                        log.Add($"  NTFS boot sector @ 0x{v.Offset:X} (hunted)");
                        return vols;
                    }
                    if (fs != null)
                        log.Add($"  {fs} signature @ 0x{o + sub:X}");
                }
            }
            log.Add("  no filesystem signature found in scanned region");
        }
        return vols;
    }

    private static VolumeInfo ProbeVolume(RawDisk disk, long off, long size,
                                          List<string> log, string via)
    {
        var v = new VolumeInfo { Offset = off, Size = size };
        var s = disk.ReadAt(off, 4096);
        var fs = DetectFs(s.AsSpan(0, 512).ToArray());
        v.FsName = fs ?? "unrecognized";
        if (fs == "NTFS")
        {
            ParseNtfsBoot(v, s.AsSpan(0, 512).ToArray());
            v.Verdict = v.BootSectorOk
                ? $"healthy NTFS boot — MFT at LCN {v.MftLcn:N0} ({v.ClusterSize}B clusters)"
                : "NTFS signature but suspicious BPB";
        }
        else if (fs == "BitLocker")
            v.Verdict = "BitLocker volume — needs recovery key, carving will find only ciphertext";
        else
            v.Verdict = fs is null
                ? "no filesystem signature at partition start — may be shifted/wiped"
                : $"{fs} detected";
        log.Add($"  volume @ 0x{off:X} size {size / 1e9:0.#} GB [{via}]: {v.FsName} — {v.Verdict}");
        return v;
    }
}
