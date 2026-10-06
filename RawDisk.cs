using Microsoft.Win32.SafeHandles;

namespace Reclaim;

/// <summary>One physical drive, opened read-only. All reads are absolute-offset,
/// tolerant of bad sectors: a failed big read is retried in sector-sized pieces
/// and the unreadable ranges are recorded instead of aborting the scan.</summary>
public sealed class RawDisk : IDisposable
{
    public const int SectorSize = 512;

    private readonly SafeFileHandle _h;
    public int Number { get; }
    public string Path { get; }
    public long Size { get; }
    public string Label { get; set; }
    public List<(long Off, long Len)> BadRanges { get; } = new();

    private RawDisk(SafeFileHandle h, int number, long size, string label)
    {
        _h = h;
        Number = number;
        Path = $@"\\.\PhysicalDrive{number}";
        Size = size;
        Label = label;
    }

    /// <summary>Probe PhysicalDrive0..15, return the ones that exist.</summary>
    public static List<RawDisk> Enumerate()
    {
        var list = new List<RawDisk>();
        for (int i = 0; i < 16; i++)
        {
            var h = Native.CreateFile($@"\\.\PhysicalDrive{i}",
                Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            var buf = new byte[16];
            long size = 0;
            if (Native.DeviceIoControl(h, Native.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
                size = BitConverter.ToInt64(buf, 0);
            list.Add(new RawDisk(h, i, size, $"Disk {i} — {size / 1e9:0.#} GB"));
        }
        return list;
    }

    /// <summary>Which physical disk hosts volume letter (e.g. "C"). -1 unknown.</summary>
    public static int DiskNumberForVolume(char letter)
    {
        var h = Native.CreateFile($@"\\.\{letter}:",
            0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return -1; }
        var buf = new byte[16];
        int n = -1;
        if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                IntPtr.Zero, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
            n = BitConverter.ToInt32(buf, 4); // STORAGE_DEVICE_NUMBER.DeviceNumber
        h.Dispose();
        return n;
    }

    /// <summary>Read `length` bytes at `offset`. Returns bytes actually read;
    /// on failure retries per-sector and fills gaps with zeros, logging the range.</summary>
    public byte[] ReadAt(long offset, int length)
    {
        var whole = TryRead(offset, length);
        if (whole != null) return whole;

        // degraded read - the drive has bad sectors here
        var data = new byte[length];
        int firstBad = -1;
        for (int pos = 0; pos < length; pos += SectorSize)
        {
            var s = TryRead(offset + pos, SectorSize);
            if (s != null)
            {
                if (firstBad >= 0)
                {
                    BadRanges.Add((offset + firstBad, pos - firstBad));
                    firstBad = -1;
                }
                Array.Copy(s, 0, data, pos, Math.Min(SectorSize, length - pos));
            }
            else if (firstBad < 0)
            {
                firstBad = pos;
            }
        }
        if (firstBad >= 0)
            BadRanges.Add((offset + firstBad, length - firstBad));
        return data;
    }

    private byte[]? TryRead(long offset, int length)
    {
        if (!Native.SetFilePointerEx(_h, offset, out _, Native.FILE_BEGIN))
            return null;
        var buf = new byte[length];
        if (!Native.ReadFile(_h, buf, (uint)length, out uint n, IntPtr.Zero) || n == 0)
            return null;
        if (n < length)
            Array.Resize(ref buf, (int)n);
        return buf;
    }

    public override string ToString() => Label;

    public void Dispose() => _h.Dispose();
}
