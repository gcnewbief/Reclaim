namespace Reclaim;

/// <summary>Signature-based carver: scans raw disk for magic bytes and cuts
/// header->footer (or max length). Used for the deep scan - works even when
/// the filesystem metadata is gone.</summary>
public sealed class Carver
{
    private readonly RawDisk _disk;

    public sealed record Signature(
        string Name, string Ext, byte[] Header, byte[]? Footer, long MaxLen);

    public static readonly Signature[] Default =
    {
        new("JPEG",  ".jpg",  new byte[]{0xFF,0xD8,0xFF}, new byte[]{0xFF,0xD9}, 32L<<20),
        new("PNG",   ".png",  new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A}, new byte[]{0x49,0x45,0x4E,0x44,0xAE,0x42,0x60,0x82}, 64L<<20),
        new("GIF",   ".gif",  "GIF8"u8.ToArray(), new byte[]{0x00,0x3B}, 32L<<20),
        new("PDF",   ".pdf",  "%PDF-"u8.ToArray(), "%%EOF"u8.ToArray(), 256L<<20),
        new("ZIP",   ".zip",  new byte[]{0x50,0x4B,0x03,0x04}, null, 512L<<20),   // covers docx/xlsx too
        new("RAR",   ".rar",  "Rar!\x1A\x07"u8.ToArray(), null, 512L<<20),
        new("7z",    ".7z",   new byte[]{0x37,0x7A,0xBC,0xAF,0x27,0x1C}, null, 512L<<20),
        new("gzip",  ".gz",   new byte[]{0x1F,0x8B,0x08}, null, 256L<<20),
        new("BMP",   ".bmp",  "BM"u8.ToArray(), null, 256L<<20),
        new("RIFF",  ".avi",  "RIFF"u8.ToArray(), null, 32L<<30),    // avi/wav - size in hdr
        new("SQLite",".db",   "SQLite format 3\0"u8.ToArray(), null, 512L<<20),
        new("MP4",   ".mp4",  "ftyp"u8.ToArray(), null, 32L<<30),    // generic ftyp, box size validated
        new("WMV",   ".wmv",  new byte[]{0x30,0x26,0xB2,0x75,0x8E,0x66,0xCF,0x11,0xA6,0xD9,0x00,0xAA,0x00,0x62,0xCE,0x6C}, null, 32L<<30), // ASF header GUID
        new("MPEG-PS",".mpg", new byte[]{0x00,0x00,0x01,0xBA}, null, 32L<<30),
        new("TS",    ".ts",   new byte[]{0x47}, null, 32L<<30),      // transport stream - sync-verified
        new("PE",    ".exe",  "MZ"u8.ToArray(), null, 64L<<20),
        new("ELF",   ".elf",  new byte[]{0x7F,0x45,0x4C,0x46}, null, 64L<<20),
        new("TIFF",  ".tif",  new byte[]{0x49,0x49,0x2A,0x00}, null, 256L<<20),
        new("TIFF2", ".tif",  new byte[]{0x4D,0x4D,0x00,0x2A}, null, 256L<<20),
        new("PSD",   ".psd",  "8BPS"u8.ToArray(), null, 512L<<20),
        new("FLAC",  ".flac", "fLaC"u8.ToArray(), null, 128L<<20),
        new("MKV",   ".mkv",  new byte[]{0x1A,0x45,0xDF,0xA3}, null, 32L<<30),
        new("ISO",   ".iso",  new byte[]{0x43,0x44,0x30,0x30,0x31}, null, 0), // at sector 0x8001
    };

    public Carver(RawDisk disk) => _disk = disk;

    /// <summary>Sequential scan; yields carved hits as RecoveredEntry.</summary>
    public IEnumerable<RecoveredEntry> Scan(
        IProgress<ScanProgress> prog, CancellationToken ct, long maxLen = 0)
    {
        const int CHUNK = 32 * 1024 * 1024;
        const int OVERLAP = 64 * 1024;
        long limit = maxLen > 0 ? Math.Min(maxLen, _disk.Size) : _disk.Size;
        int found = 0;
        long off = 0;
        while (off < limit)
        {
            if (ct.IsCancellationRequested) yield break;
            int want = (int)Math.Min(CHUNK + OVERLAP, limit - off);
            var data = _disk.ReadAt(off, want);
            int scanLen = Math.Min(data.Length, CHUNK);   // overlap not scanned twice

            foreach (var sig in Default)
            {
                int i = 0;
                while (i < scanLen && (i = data.AsSpan(i, scanLen - i).IndexOf(sig.Header)) >= 0)
                {
                    long abs = off + i;
                    long len = sig.MaxLen;
                    // format-specific validation / length extraction
                    if (sig.Name is "MP4")
                    {
                        // 'ftyp' must be preceded by a plausible box size (16..64, multiple of 8)
                        if (i < 4) { i += sig.Header.Length; continue; }
                        uint box = ((uint)data[i - 4] << 24) | ((uint)data[i - 3] << 16) |
                                   ((uint)data[i - 2] << 8) | data[i - 1];
                        if (box < 16 || box > 64 || box % 8 != 0) { i += sig.Header.Length; continue; }
                        abs -= 4;   // file starts at the box size field
                    }
                    else if (sig.Name is "TS")
                    {
                        // 0x47 sync byte must repeat every 188 bytes
                        if (i + 188 * 2 >= data.Length || data[i + 188] != 0x47 || data[i + 376] != 0x47)
                        { i += sig.Header.Length; continue; }
                    }
                    if (sig.Name is "RIFF" && i + 8 <= data.Length)
                    {
                        uint rl = BitConverter.ToUInt32(data, i + 4);
                        if (rl > 16 && rl < sig.MaxLen) len = rl + 8;
                    }
                    else if (sig.Name is "BMP" && i + 6 <= data.Length)
                    {
                        uint bl = BitConverter.ToUInt32(data, i + 2);
                        if (bl > 54 && bl < sig.MaxLen) len = bl;
                    }
                    else if (sig.Footer != null)
                    {
                        int f = data.AsSpan(i + sig.Header.Length,
                                Math.Min(data.Length - i - sig.Header.Length, (int)Math.Min(sig.MaxLen, 8L << 20)))
                                .IndexOf(sig.Footer);
                        if (f >= 0) len = sig.Header.Length + f + sig.Footer.Length;
                    }
                    yield return new RecoveredEntry
                    {
                        Name = $"carved_{found:D6}_{abs >> 12:X}{sig.Ext}",
                        Ext = sig.Ext, Size = len, State = EntryState.Deleted,
                        Source = EntrySource.Carved, Health = Health.Unknown,
                        CarveOffset = abs, CarveLen = len,
                        FolderPath = $"[carved {sig.Name}]",
                    };
                    found++;
                    i += sig.Header.Length;
                }
            }
            prog?.Report(new ScanProgress
            {
                Offset = off + want, Total = limit, CarvedFound = found,
                Phase = "Deep scan (signatures)"
            });
            off += CHUNK;
        }
    }
}
