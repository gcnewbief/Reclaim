using System.Collections.Concurrent;
using System.Text;

namespace Reclaim;

/// <summary>One parsed MFT FILE record.</summary>
public sealed class MftRecord
{
    public uint RecordNumber;
    public bool InUse;
    public bool IsDir;
    public ushort Sequence;
    public long ParentRef;        // low 48 bits = parent record number
    public string Name = "";
    public long RealSize;
    public byte[]? ResidentData;
    public List<(long Vcn, long Lcn, long Clusters)> DataRuns = new();
    public long DiskOffset;       // where the record was found
    public bool HasAttrList;      // attributes continue in other records
}

public static class MftRecordParser
{
    /// <summary>Parse a candidate 1KB record. Returns null if not a valid FILE record.</summary>
    public static MftRecord? Parse(byte[] rec, long diskOffset)
    {
        if (rec.Length < 1024 || rec[0] != 'F' || rec[1] != 'I' || rec[2] != 'L' || rec[3] != 'E')
            return null;
        ushort usaOff = BitConverter.ToUInt16(rec, 4);
        ushort usaCnt = BitConverter.ToUInt16(rec, 6);
        int recSize = (usaCnt - 1) * 512;
        if (usaOff < 0x28 || usaOff + usaCnt * 2 > rec.Length || recSize < 512 || recSize > rec.Length)
            return null;

        // update-sequence fixup: last 2 bytes of each 512B sector are replaced
        // by the usa entries; validate signature byte first
        var r = (byte[])rec.Clone();
        ushort usn = BitConverter.ToUInt16(r, usaOff);
        for (int i = 1; i < usaCnt; i++)
        {
            int tail = i * 512 - 2;
            if (BitConverter.ToUInt16(r, tail) != usn) return null;  // not a record
            r[tail] = r[usaOff + i * 2];
            r[tail + 1] = r[usaOff + i * 2 + 1];
        }

        ushort flags = BitConverter.ToUInt16(r, 22);
        ushort attrOff = BitConverter.ToUInt16(r, 20);
        var m = new MftRecord
        {
            RecordNumber = BitConverter.ToUInt32(r, 44),
            InUse = (flags & 1) != 0,
            IsDir = (flags & 2) != 0,
            Sequence = BitConverter.ToUInt16(r, 16),
            DiskOffset = diskOffset,
        };

        int pos = attrOff;
        for (int guard = 0; guard < 200 && pos + 8 <= recSize; guard++)
        {
            uint atype = BitConverter.ToUInt32(r, pos);
            if (atype == 0xFFFFFFFF) break;
            int alen = BitConverter.ToInt32(r, pos + 4);
            if (alen < 16 || pos + alen > recSize) break;
            bool nonRes = r[pos + 8] != 0;

            if (atype == 0x30)        // $FILE_NAME (always resident)
            {
                int coff = BitConverter.ToUInt16(r, pos + 20) + pos;
                if (coff + 66 <= pos + alen)
                {
                    m.ParentRef = BitConverter.ToInt64(r, coff) & 0x0000FFFFFFFFFFFF;
                    m.RealSize = BitConverter.ToInt64(r, coff + 48);
                    int nlen = r[coff + 64];
                    byte ns = r[coff + 65];
                    if (nlen > 0 && coff + 66 + nlen * 2 <= pos + alen)
                    {
                        string nm = Encoding.Unicode.GetString(r, coff + 66, nlen * 2);
                        // prefer non-DOS (namespace 3) name if both exist
                        if (m.Name is "" || ns != 2)
                            m.Name = nm;
                    }
                }
            }
            else if (atype == 0x80)   // $DATA (unnamed stream only for v1)
            {
                int nameLen = r[pos + 9];
                if (nameLen == 0)     // skip ADS for now
                {
                    if (!nonRes)
                    {
                        int csz = BitConverter.ToInt32(r, pos + 16);
                        int coff = BitConverter.ToUInt16(r, pos + 20) + pos;
                        if (coff + csz <= pos + alen && csz <= 1 << 22)
                        {
                            m.ResidentData = new byte[csz];
                            Array.Copy(r, coff, m.ResidentData, 0, csz);
                            if (m.RealSize == 0) m.RealSize = csz;
                        }
                    }
                    else
                    {
                        m.RealSize = BitConverter.ToInt64(r, pos + 48);
                        int roff = BitConverter.ToUInt16(r, pos + 32) + pos;
                        m.DataRuns = DecodeRuns(r, roff, pos + alen);
                    }
                }
            }
            else if (atype == 0x20)   // $ATTRIBUTE_LIST
            {
                m.HasAttrList = true;
            }
            pos += alen;
        }
        // a record that parses zero attributes is almost certainly noise
        if (m.Name is "" && m.ResidentData is null && m.DataRuns.Count == 0)
            return null;
        return m;
    }

    /// <summary>Records that own data runs for a named stream (e.g. "$MFT"
    /// record 0) let us locate the volume without a boot sector.</summary>

    /// <summary>Decode NTFS data runs: varint (len,lcnDelta) pairs, 0 terminator.</summary>
    public static List<(long Vcn, long Lcn, long Clusters)> DecodeRuns(byte[] r, int off, int end)
    {
        var runs = new List<(long, long, long)>();
        long lcn = 0, vcn = 0;
        while (off < end && r[off] != 0)
        {
            int hdr = r[off++];
            int lenSz = hdr & 0xF, offSz = hdr >> 4;
            if (off + lenSz + offSz > end || lenSz > 8 || offSz > 8) break;
            long len = 0, delta = 0;
            for (int i = 0; i < lenSz; i++) len |= (long)r[off++] << (8 * i);
            for (int i = 0; i < offSz; i++) delta |= (long)r[off++] << (8 * i);
            if (offSz > 0 && (r[off - 1] & 0x80) != 0)     // sign-extend
                delta |= -1L << (8 * offSz);
            if (offSz == 0) { vcn += len; continue; }       // sparse run
            lcn += delta;
            runs.Add((vcn, lcn, len));
            vcn += len;
        }
        return runs;
    }
}

/// <summary>Scans a RawDisk for MFT records, locates the NTFS volume offset
/// by voting on self-locating records, and yields deleted entries with
/// reconstructed paths.</summary>
public sealed class NtfsScanner
{
    private readonly RawDisk _disk;
    private readonly ConcurrentDictionary<uint, MftRecord> _records = new();
    private readonly List<MftRecord> _mftCopies = new();   // every record-0 copy
    public long VolumeOffset { get; private set; } = -1;
    public int ClusterSize { get; private set; } = 4096;
    public long RecordsScanned;

    public NtfsScanner(RawDisk disk) => _disk = disk;

    /// <summary>Set volume offset/cluster size explicitly (e.g. from a boot sector).</summary>
    public void ForceVolume(long volOff, int clusterSize)
    {
        VolumeOffset = volOff;
        ClusterSize = clusterSize;
    }

    /// <summary>Tier-1 quick scan: the volume offset and geometry are already
    /// known (intact boot sector or hunted NTFS signature), so read $MFT
    /// directly via record 0's data runs instead of scanning the whole disk.</summary>
    public IEnumerable<MftRecord> QuickScan(long volOff, int clusterSize, long mftLcn,
                                          int recSize, IProgress<ScanProgress> prog,
                                          CancellationToken ct)
    {
        VolumeOffset = volOff;
        ClusterSize = clusterSize;
        var results = new List<MftRecord>();
        long mftAbs = LcnToOffset(mftLcn);

        var rec0 = MftRecordParser.Parse(_disk.ReadAt(mftAbs, Math.Max(recSize, 1024)), mftAbs);
        if (rec0 is null)
        {
            prog?.Report(new ScanProgress { Phase = "MFT record 0 unreadable" });
            yield break;
        }
        _records.TryAdd(0, rec0);
        _mftCopies.Add(rec0);
        results.Add(rec0);

        // walk every extent of $MFT, recSize-aligned records within each
        const int CH = 16 * 1024 * 1024;
        foreach (var (_, lcn, clusters) in rec0.DataRuns)
        {
            long extStart = LcnToOffset(lcn);
            long extLen = clusters * clusterSize;
            for (long pos = 0; pos < extLen; pos += CH)
            {
                if (ct.IsCancellationRequested) yield break;
                int want = (int)Math.Min(CH, extLen - pos);
                var data = _disk.ReadAt(extStart + pos, want);
                for (int i = 0; i + recSize <= data.Length; i += recSize)
                {
                    var rec = MftRecordParser.Parse(
                        data.AsSpan(i, recSize).ToArray(), extStart + pos + i);
                    if (rec != null)
                    {
                        results.Add(rec);
                        if (rec.RecordNumber == 0) _mftCopies.Add(rec);
                        _records.TryAdd(rec.RecordNumber, rec);
                    }
                }
                RecordsScanned = extStart + pos + want;
                prog?.Report(new ScanProgress
                {
                    Offset = pos + want, Total = extLen,
                    RecordsFound = _records.Count, Phase = "Reading MFT"
                });
            }
        }
        foreach (var r in results) yield return r;
    }

    /// <summary>Tier-2 smart scan: sequential hunt, but as soon as enough records
    /// exist to infer where the MFT lives, verify it and read the extents directly.
    /// If inference never succeeds this degenerates to the full exhaustive scan.</summary>
    public IEnumerable<MftRecord> ScanSmart(IProgress<ScanProgress> prog, CancellationToken ct)
    {
        const int CHUNK = 64 * 1024 * 1024;
        const int MAXREC = 4096;
        var results = new List<MftRecord>();
        var seen = new HashSet<long>();
        byte[] tail = Array.Empty<byte>();
        long baseOff;
        long lastAttempt = -1;
        bool mftDone = false;

        for (long off = 0; off < _disk.Size && !mftDone; off += CHUNK)
        {
            if (ct.IsCancellationRequested) yield break;
            int want = (int)Math.Min(CHUNK, _disk.Size - off);
            byte[] data = _disk.ReadAt(off, want);
            baseOff = off - tail.Length;
            if (tail.Length > 0)
            {
                var joined = new byte[tail.Length + data.Length];
                tail.CopyTo(joined, 0);
                data.CopyTo(joined, tail.Length);
                data = joined;
            }

            int i = 0;
            while ((i = IndexOf(data, _fileSig, i)) >= 0)
            {
                long abs = baseOff + i;
                if (abs % 512 == 0 && seen.Add(abs))
                {
                    var rec = MftRecordParser.Parse(
                        data.AsSpan(i, Math.Min(MAXREC, data.Length - i)).ToArray(), abs);
                    if (rec != null)
                    {
                        results.Add(rec);
                        if (rec.RecordNumber == 0) _mftCopies.Add(rec);
                        _records.TryAdd(rec.RecordNumber, rec);
                    }
                }
                i += 4;
            }
            int keep = Math.Min(MAXREC, data.Length);
            tail = data.AsSpan(data.Length - keep, keep).ToArray();
            RecordsScanned = off + want;

            // once a decent sample of records exists, try to infer+read the MFT directly
            if (_records.Count >= 64 && off - lastAttempt > CHUNK)
            {
                lastAttempt = off;
                if (TryInferMft(out int recSize))
                {
                    prog?.Report(new ScanProgress
                    {
                        Offset = RecordsScanned, Total = _disk.Size,
                        RecordsFound = _records.Count,
                        Phase = $"MFT located @0x{MftStart:X} - reading extents directly"
                    });
                    foreach (var r in ReadMftExtents(recSize, prog, ct))
                        if (_records.TryAdd(r.RecordNumber, r)) results.Add(r);
                    mftDone = true;
                }
            }
            prog?.Report(new ScanProgress
            {
                Offset = RecordsScanned, Total = _disk.Size,
                RecordsFound = _records.Count, Phase = "Record hunt"
            });
        }
        foreach (var r in results) yield return r;
    }

    public long MftStart { get; private set; } = -1;
    private int _mftRecSize = 1024;

    /// <summary>Infer the MFT's location without a boot sector.
    /// Every record knows its number, so rec.DiskOffset - recNum*recSize votes
    /// for its extent's start. The dominant vote is extent 1 (records 0..k).
    /// Verified by reading the predicted position of record 0, then the volume
    /// offset is pinned down by checking a second MFT extent or $Bitmap data.</summary>
    public bool TryInferMft(out int recSize)
    {
        recSize = _mftRecSize;
        foreach (int rs in new[] { 1024, 4096 })
        {
            var votes = _records.Values
                .GroupBy(r => r.DiskOffset - (long)r.RecordNumber * rs)
                .Where(g => g.Key >= 0)
                .OrderByDescending(g => g.Count());
            foreach (var g in votes.Take(3))
            {
                long mftStart = g.Key;
                if (mftStart % 512 != 0) continue;
                var rec0 = MftRecordParser.Parse(_disk.ReadAt(mftStart, rs), mftStart);
                if (rec0 is null || rec0.DataRuns.Count == 0) continue;
                // if the predicted spot holds a different record number, this was
                // another extent - recompute using what we actually found
                if (rec0.RecordNumber != 0)
                {
                    mftStart -= (long)rec0.RecordNumber * rs;
                    if (mftStart < 0) continue;
                    rec0 = MftRecordParser.Parse(_disk.ReadAt(mftStart, rs), mftStart);
                    if (rec0 is not { RecordNumber: 0 } || rec0.DataRuns.Count == 0) continue;
                }
                foreach (int cs in new[] { 4096, 8192, 2048, 16384, 1024, 512, 32768, 65536 })
                {
                    long vstart = mftStart - rec0.DataRuns[0].Lcn * cs;
                    if (vstart < 0 || vstart % 512 != 0) continue;
                    if (!VerifyVolume(rec0, mftStart, vstart, cs, rs)) continue;
                    VolumeOffset = vstart;
                    ClusterSize = cs;
                    MftStart = mftStart;
                    _mftRecSize = recSize = rs;
                    _records.TryAdd(0, rec0);
                    _mftCopies.Add(rec0);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Cross-check a (volStart, clusterSize) guess against ground truth:
    /// prefer a second MFT extent (predicted record number must parse correctly);
    /// for single-extent MFTs, read $Bitmap (record 6) data at the predicted
    /// position - a real allocation bitmap is neither all-zero nor all-0xFF.</summary>
    private bool VerifyVolume(MftRecord rec0, long mftStart, long vstart, int cs, int recSize)
    {
        if (rec0.DataRuns.Count > 1)
        {
            var run = rec0.DataRuns[1];
            long expectedNum = run.Vcn * cs / recSize;
            var probe = MftRecordParser.Parse(
                _disk.ReadAt(vstart + run.Lcn * cs, recSize), vstart + run.Lcn * cs);
            if (probe != null && probe.RecordNumber == expectedNum)
                return true;
            return false;
        }
        // single-extent MFT - verify via $Bitmap (record 6)
        var r6 = MftRecordParser.Parse(_disk.ReadAt(mftStart + 6L * recSize, recSize),
                                       mftStart + 6L * recSize);
        if (r6 is { RecordNumber: 6 } && r6.DataRuns.Count > 0)
        {
            var d = _disk.ReadAt(vstart + r6.DataRuns[0].Lcn * cs, 4096);
            int real = d.Count(b => b != 0 && b != 0xFF);
            if (real > 64) return true;
        }
        // weak fallback: volume start on a 1MB boundary is typical for modern layouts
        return vstart % (1024 * 1024) == 0;
    }

    /// <summary>Read every MFT extent at recSize stride once volume geometry is known.</summary>
    public IEnumerable<MftRecord> ReadMftExtents(int recSize, IProgress<ScanProgress> prog,
                                               CancellationToken ct)
    {
        var results = new List<MftRecord>();
        if (!_records.TryGetValue(0, out var rec0) || rec0.DataRuns.Count == 0)
            return results;
        const int CH = 16 * 1024 * 1024;
        long total = rec0.DataRuns.Sum(r => r.Clusters * ClusterSize);
        long done = 0;
        foreach (var (_, lcn, clusters) in rec0.DataRuns)
        {
            long extStart = LcnToOffset(lcn);
            long extLen = clusters * ClusterSize;
            for (long pos = 0; pos < extLen; pos += CH)
            {
                if (ct.IsCancellationRequested) return results;
                int want = (int)Math.Min(CH, extLen - pos);
                var data = _disk.ReadAt(extStart + pos, want);
                for (int i = 0; i + recSize <= data.Length; i += recSize)
                {
                    if (data[i] != 'F') continue;   // fast reject before full sig check
                    var rec = MftRecordParser.Parse(
                        data.AsSpan(i, recSize).ToArray(), extStart + pos + i);
                    if (rec != null) results.Add(rec);
                }
                done += want;
                prog?.Report(new ScanProgress
                {
                    Offset = done, Total = total, RecordsFound = results.Count,
                    Phase = "Reading $MFT extents"
                });
            }
        }
        return results;
    }

    /// <summary>Tier-2: scan the whole disk at 1KB boundaries for FILE records.</summary>
    public IEnumerable<MftRecord> ScanRecords(IProgress<ScanProgress> prog, CancellationToken ct)
    {
        const int CHUNK = 64 * 1024 * 1024;
        const int MAXREC = 4096;
        var results = new List<MftRecord>();
        var seen = new HashSet<long>();       // dedupe hits re-scanned in the overlap
        byte[] tail = Array.Empty<byte>();
        long baseOff = 0;   // absolute offset of data[0] once tail is prepended

        for (long off = 0; off < _disk.Size; off += CHUNK)
        {
            if (ct.IsCancellationRequested) yield break;
            int want = (int)Math.Min(CHUNK, _disk.Size - off);
            byte[] data = _disk.ReadAt(off, want);
            baseOff = off - tail.Length;
            if (tail.Length > 0)
            {
                var joined = new byte[tail.Length + data.Length];
                tail.CopyTo(joined, 0);
                data.CopyTo(joined, tail.Length);
                data = joined;
            }

            // find "FILE" at 1024-aligned positions; pass up to 4KB so big records parse.
            // skip hits we've already processed in the previous chunk's tail
            int i = 0;
            while ((i = IndexOf(data, _fileSig, i)) >= 0)
            {
                long abs = baseOff + i;
                // records are sector-aligned (512) but a volume can start mid-1KB
                if (abs % 512 == 0 && seen.Add(abs))
                {
                    var rec = MftRecordParser.Parse(
                        data.AsSpan(i, Math.Min(MAXREC, data.Length - i)).ToArray(), abs);
                    if (rec != null)
                    {
                        results.Add(rec);
                        if (rec.RecordNumber == 0) _mftCopies.Add(rec);
                        _records.TryAdd(rec.RecordNumber, rec);
                    }
                }
                i += 4;
            }
            // keep the last MAXREC bytes so boundary-straddling records survive
            int keep = Math.Min(MAXREC, data.Length);
            tail = data.AsSpan(data.Length - keep, keep).ToArray();
            RecordsScanned = off + want;
            prog?.Report(new ScanProgress
            {
                Offset = RecordsScanned, Total = _disk.Size,
                RecordsFound = _records.Count, Phase = "Deep record scan"
            });
        }
        foreach (var r in results) yield return r;
    }

    public int BadRegionCount => _disk.BadRanges.Count;

    private static readonly byte[] _fileSig = "FILE"u8.ToArray();
    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        return hay.AsSpan(Math.Min(start, hay.Length)).IndexOf(needle) is int x && x >= 0
            ? x + Math.Min(start, hay.Length) : -1;
    }

    /// <summary>Work out the volume offset. Record 0 ($MFT) describes its own
    /// location, so volStart = P0 - firstLcn*cs — but a $MFTMirr duplicate of
    /// record 0 sits at the mirror position while pointing at the real MFT,
    /// giving a wrong (usually oversized) candidate. Try every record-0 copy x
    /// cluster size and reject candidates where volStart + volume size (from
    /// $Bitmap's cluster count) would run past the end of the disk.</summary>
    public bool LocateVolume()
    {
        // volume size in clusters ~ $Bitmap (record 6) real size * 8
        long volClusters = 0;
        if (_records.TryGetValue(6, out var bmp) && bmp.RealSize > 0)
            volClusters = bmp.RealSize * 8;

        var candidates = new List<(long vstart, int cs)>();
        foreach (var mft in _mftCopies)
        {
            if (mft.DataRuns.Count == 0) continue;
            long mftLcn = mft.DataRuns[0].Lcn;
            foreach (int cs in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
            {
                long v = mft.DiskOffset - mftLcn * cs;
                if (v < 0) continue;
                if (volClusters > 0 && v + volClusters * cs > _disk.Size + 4096L * cs)
                    continue;
                candidates.Add((v, cs));
            }
        }
        if (candidates.Count == 0) return false;
        // prefer the most common volStart, then 4K clusters, then MB-aligned
        var pick = candidates
            .GroupBy(c => c.vstart)
            .OrderByDescending(g => g.Count())
            .Select(g => g.OrderByDescending(c => c.cs == 4096)
                          .ThenBy(c => c.vstart % 1_048_576 == 0 ? 0 : 1)
                          .First())
            .First();
        VolumeOffset = pick.vstart;
        ClusterSize = pick.cs;
        return true;
    }

    /// <summary>Absolute disk offset of a file's data run.</summary>
    public long LcnToOffset(long lcn) => VolumeOffset + lcn * ClusterSize;

    /// <summary>Rebuild full path by walking parent refs. Root = record 5.</summary>
    public string BuildPath(MftRecord rec)
    {
        var parts = new List<string>();
        long cur = rec.ParentRef;
        for (int depth = 0; depth < 64 && cur > 5; depth++)
        {
            if (!_records.TryGetValue((uint)cur, out var parent)) break;
            if (parent.Name is not "") parts.Insert(0, parent.Name);
            cur = parent.ParentRef;
        }
        return string.Join("\\", parts);
    }

    /// <summary>Read the cluster allocation bitmap ($Bitmap, record 6) so we
    /// can mark deleted files whose clusters got reallocated as 'poor'.</summary>
    public byte[]? LoadBitmap()
    {
        if (VolumeOffset < 0 || !_records.TryGetValue(6, out var bmp)) return null;
        if (bmp.ResidentData != null) return bmp.ResidentData;
        if (bmp.DataRuns.Count == 0) return null;
        long total = bmp.RealSize;
        var buf = new byte[total];
        long written = 0;
        foreach (var (_, lcn, clusters) in bmp.DataRuns)
        {
            int want = (int)Math.Min(clusters * ClusterSize, total - written);
            if (want <= 0) break;
            var d = _disk.ReadAt(LcnToOffset(lcn), want);
            Array.Copy(d, 0, buf, written, d.Length);
            written += want;
        }
        return buf;
    }

    public static Health HealthFor(MftRecord rec, byte[]? bitmap, int cs)
    {
        if (rec.ResidentData != null) return Health.Good;
        if (rec.DataRuns.Count == 0) return Health.Unknown;
        if (bitmap is null) return Health.Unknown;
        long total = 0, free = 0;
        foreach (var (_, lcn, clusters) in rec.DataRuns)
            for (long c = lcn; c < lcn + clusters; c++)
            {
                long bit = c;
                if (bit / 8 >= bitmap.Length) break;
                total++;
                if ((bitmap[bit / 8] & (1 << (int)(bit % 8))) == 0) free++;
            }
        if (total == 0) return Health.Unknown;
        return free == total ? Health.Good : free > 0 ? Health.Partial : Health.Poor;
    }
}
