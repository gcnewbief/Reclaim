// Headless harness: exercises the same engine path as MainWindow.ScanBtn_Click.
// Usage: reclaim-cli [diskNumber] [--carve]
using Reclaim;

// catch-everything logging to file so silent deaths are visible
var logPath = Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\..\\cli_log.txt");
void Flog(string m) => File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Flog($"UNHANDLED {e.ExceptionObject}");
AppDomain.CurrentDomain.ProcessExit += (_, _) => Flog("ProcessExit");
TaskScheduler.UnobservedTaskException += (_, e) =>
    Flog($"UNOBSERVED {e.Exception}");
Flog($"=== start pid={Environment.ProcessId} ===");

int diskNum = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 1;
bool carve = args.Contains("--carve");

if (args.Contains("--smart"))
{
    var rep = Smart.Read(diskNum);
    Console.WriteLine(Smart.Format(rep));
    return rep.Ok ? 0 : 1;
}

var disks = RawDisk.Enumerate();
Console.WriteLine($"== enumerated {disks.Count} drive(s) ==");
foreach (var d in disks) Console.WriteLine($"  {d.Label}");

var disk = disks.FirstOrDefault(d => d.Number == diskNum);
if (disk is null) { Console.WriteLine($"disk {diskNum} not found"); return 2; }

Console.WriteLine($"\n== tier 0: triage disk {diskNum} ==");
var tlog = new List<string>();
List<VolumeInfo> vols;
try
{
    vols = Volumes.Triage(disk, tlog);
}
catch (Exception ex)
{
    Console.WriteLine($"TRIAGE THREW: {ex}");
    return 1;
}
foreach (var l in tlog) Console.WriteLine($"  {l}");
foreach (var v in vols)
    Console.WriteLine($"  vol: off=0x{v.Offset:X} size={v.Size / 1e9:0.#}GB fs={v.FsName} " +
                      $"bootok={v.BootSectorOk} verdict={v.Verdict}");

var scanner = new NtfsScanner(disk);
var prog = new Progress<ScanProgress>(p =>
{
    var line = $"  [{p.Offset / 1e9:0.00}/{p.Total / 1e9:0.0} GB] {p.Phase} " +
               $"rec={p.RecordsFound} carved={p.CarvedFound}";
    Console.WriteLine(line);
    Flog($"{line}  mem={GC.GetTotalMemory(false) / 1e6:0}MB");
});

var ntfs = vols.FirstOrDefault(v => v is { FsName: "NTFS", BootSectorOk: true });
Console.WriteLine(ntfs != null
    ? $"\n== tier 1: quick scan NTFS @ 0x{ntfs.Offset:X} =="
    : "\n== tier 2: raw FILE record scan ==");

List<MftRecord> records;
try
{
    if (ntfs != null)
        records = scanner.QuickScan(ntfs.Offset, ntfs.ClusterSize, ntfs.MftLcn,
                                    ntfs.MftRecordSize, prog, CancellationToken.None).ToList();
    else
        records = scanner.ScanSmart(prog, CancellationToken.None).ToList();
}
catch (Exception ex)
{
    Console.WriteLine($"SCAN THREW: {ex}");
    return 1;
}
Console.WriteLine($"records: {records.Count:N0}  badRegions: {scanner.BadRegionCount}");

bool located = scanner.VolumeOffset >= 0 || scanner.LocateVolume();
if (!located && ntfs != null) { scanner.ForceVolume(ntfs.Offset, ntfs.ClusterSize); located = true; }
Console.WriteLine($"volume offset: {(located ? $"0x{scanner.VolumeOffset:X}" : "UNKNOWN")} " +
                  $"cluster={scanner.ClusterSize}");

if (located)
{
    var bitmap = scanner.LoadBitmap();
    if (bitmap != null)
    {
        long free = 0;
        foreach (var b in bitmap) free += 8 - System.Numerics.BitOperations.PopCount(b);
        Console.WriteLine($"$Bitmap: {free:N0}/{bitmap.Length * 8L:N0} clusters free");
    }
    else Console.WriteLine("$Bitmap unavailable");
}

int del = 0, resident = 0;
foreach (var r in records.Take(20))
    Console.WriteLine($"  rec {r.RecordNumber,-6} {(r.InUse ? "live " : "DEL  ")} " +
                      $"{(r.IsDir ? "<dir>" : "     ")} size={r.RealSize,-12} name={r.Name}");
foreach (var r in records) if (!r.InUse) { del++; if (r.ResidentData != null) resident++; }
Console.WriteLine($"deleted: {del:N0} ({resident:N0} resident)");
Flog($"done. records={records.Count} deleted={del}");

if (args.Contains("--tree"))
{
    // build RecoveredEntry rows + the folder tree exactly like the UI does,
    // then dump top-level structure and timing
    Console.WriteLine("\n== folder tree ==");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var entries = records.Select(r => new RecoveredEntry
    {
        Name = r.Name is "" ? $"record_{r.RecordNumber}" : r.Name,
        FolderPath = scanner.BuildPath(r),
        Size = r.RealSize,
        State = r.InUse ? EntryState.Live : EntryState.Deleted,
        Source = EntrySource.Mft,
        IsDir = r.IsDir,
        RecordNum = r.RecordNumber,
    }).ToList();
    Console.WriteLine($"  {entries.Count:N0} entries in {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    var (roots, all, root, byPath) = FolderTreeBuilder.Build(entries);
    Console.WriteLine($"  tree: {byPath.Count - 1:N0} folders in {sw.ElapsedMilliseconds} ms");
    Flog($"tree: {byPath.Count - 1} folders, build {sw.ElapsedMilliseconds} ms");

    int noPath = entries.Count(e => e.FolderPath is "");
    int bracketed = entries.Count(e => e.FolderPath.StartsWith('['));
    Console.WriteLine($"  entries with no folder path: {noPath:N0} · carved/other []: {bracketed:N0}");
    foreach (var c in root.Children.OrderByDescending(c => c.DescTotal).Take(25))
        Console.WriteLine($"  {c.DescTotal,10:N0}  {c.FullPath}");
    Console.WriteLine($"  ... ({root.Children.Count:N0} top-level folders total)");
}

if (carve)
{
    Console.WriteLine("\n== signature carve ==");
    foreach (var h in new Carver(disk).Scan(prog, CancellationToken.None))
        Console.WriteLine($"  {h.Name} @ 0x{h.CarveOffset:X} ({h.CarveLen:N0} B)");
}
return 0;
