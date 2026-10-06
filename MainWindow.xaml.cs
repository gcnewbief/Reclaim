using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;   // FolderBrowserDialog

namespace Reclaim;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RecoveredEntry> _entries = new();
    private List<RawDisk> _disks = new();
    private List<VolumeInfo> _vols = new();
    private RawDisk? _disk;
    private NtfsScanner? _scanner;
    private CancellationTokenSource? _cts;
    private string _filter = "";

    public MainWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _entries;
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        AdminNote.Text = admin ? "" : "  ⚠ not elevated — relaunch as Administrator";
        Log(admin ? "Running elevated — raw disk access available."
                  : "NOT elevated — drive list may be empty.");
        LoadDrives();
    }

    // ------------------------------------------------------------- setup

    private void Log(string msg)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        LogBox.ScrollToEnd();
    }

    private void Status(string t) => StatusLbl.Text = t;

    /// <summary>Enrich bare disk labels with model/serial/health via PowerShell.</summary>
    private static Dictionary<int, string> DriveDetails()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"Get-PhysicalDisk | " +
                "Select-Object DeviceId,FriendlyName,HealthStatus,BusType | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            var doc = JsonDocument.Parse(json).RootElement;
            var items = doc.ValueKind == JsonValueKind.Array
                ? doc.EnumerateArray().ToList() : new List<JsonElement> { doc };
            foreach (var d in items)
            {
                int id = d.GetProperty("DeviceId").GetInt32();
                string name = d.GetProperty("FriendlyName").GetString() ?? "";
                string health = d.GetProperty("HealthStatus").ToString() == "0" ? "Healthy"
                              : d.GetProperty("HealthStatus").ToString() == "1" ? "Warning" : "Unhealthy";
                string bus = d.TryGetProperty("BusType", out var b) ? b.ToString() : "";
                map[id] = $"{name} · {health} · {bus}";
            }
        }
        catch { /* enrichment is best-effort */ }
        return map;
    }

    private void LoadDrives()
    {
        DriveList.ItemsSource = null;
        foreach (var d in _disks) d.Dispose();
        _disks = RawDisk.Enumerate();
        var details = DriveDetails();
        foreach (var d in _disks)
            if (details.TryGetValue(d.Number, out var det))
                d.Label = $"Disk {d.Number} — {d.Size / 1e9:0.#} GB — {det}";
        DriveList.ItemsSource = _disks;
        if (_disks.Count > 0) DriveList.SelectedIndex = 0;
        Log($"Enumerated {_disks.Count} physical drive(s).");
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => LoadDrives();

    private RawDisk? PickDisk()
    {
        if (DriveList.SelectedItem is RawDisk d) return d;
        Status("Pick a drive first.");
        return null;
    }

    private void SetBusy(bool busy)
    {
        ScanBtn.IsEnabled = DeepOnlyBtn.IsEnabled = RefreshBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        DriveList.IsEnabled = !busy;
    }

    // ------------------------------------------------------------- triage (tier 0)

    /// <summary>Background-safe: collects log lines; caller emits them on the UI thread.</summary>
    private (List<VolumeInfo> vols, List<string> log) Triage(RawDisk disk)
    {
        var log = new List<string>();
        var vols = Volumes.Triage(disk, log);
        if (disk.BadRanges.Count > 0)
            log.Add($"  warn {disk.BadRanges.Count} unreadable region(s) already encountered");
        return (vols, log);
    }

    // ------------------------------------------------------------- scan

    private async void ScanBtn_Click(object s, RoutedEventArgs e)
    {
        var disk = PickDisk();
        if (disk is null) return;
        _disk = disk;
        _entries.Clear();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var prog = MakeProgress();

        try
        {
            // tier 0
            var (vols, tlog) = await Task.Run(() => Triage(disk));
            foreach (var l in tlog) Log(l);
            _vols = vols;
            VolList.ItemsSource = vols;

            var scanner = new NtfsScanner(disk);
            _scanner = scanner;
            var ntfs = vols.FirstOrDefault(v => v is { FsName: "NTFS", BootSectorOk: true });

            List<MftRecord> records;
            if (ntfs != null)
            {
                Log($"Tier 1: NTFS @ 0x{ntfs.Offset:X} — reading $MFT directly");
                records = await Task.Run(() => scanner.QuickScan(
                    ntfs.Offset, ntfs.ClusterSize, ntfs.MftLcn,
                    ntfs.MftRecordSize, prog, _cts.Token).ToList(), _cts.Token);
                if (records.Count < 16)    // MFT unreadable -> fall through
                {
                    Log($"Tier 1 yielded {records.Count} records — escalating to Tier 2 smart scan");
                    records = await Task.Run(() =>
                        scanner.ScanSmart(prog, _cts.Token).ToList(), _cts.Token);
                }
            }
            else
            {
                Log("Tier 1 skipped (no healthy NTFS boot sector) — Tier 2 smart scan " +
                    "(hunts for MFT, then reads extents directly)");
                records = await Task.Run(() =>
                    scanner.ScanSmart(prog, _cts.Token).ToList(), _cts.Token);
            }
            if (_cts.IsCancellationRequested) { Status("Cancelled."); return; }

            // locate volume (needed for LCN->offset on non-resident data)
            bool located = scanner.VolumeOffset >= 0 || scanner.LocateVolume();
            if (!located && ntfs != null) { scanner.ForceVolume(ntfs.Offset, ntfs.ClusterSize); located = true; }
            Log($"Volume offset: {(located ? $"0x{scanner.VolumeOffset:X}" : "UNKNOWN — non-resident recovery unavailable")}, " +
                $"cluster {scanner.ClusterSize} B");

            byte[]? bitmap = located ? scanner.LoadBitmap() : null;
            if (bitmap != null)
            {
                long free = 0;
                foreach (var b in bitmap) free += 8 - System.Numerics.BitOperations.PopCount(b);
                Log($"$Bitmap: {free:N0}/{bitmap.Length * 8L:N0} clusters free " +
                    $"({100.0 * free / (bitmap.Length * 8L):0.#}%)");
            }
            else Log("$Bitmap unavailable — health will be estimated as Unknown");

            int del = 0, resident = 0;
            foreach (var r in records)
            {
                _entries.Add(new RecoveredEntry
                {
                    Name = r.Name is "" ? $"record_{r.RecordNumber}" : r.Name,
                    FolderPath = scanner.BuildPath(r),
                    Size = r.RealSize,
                    State = r.InUse ? EntryState.Live : EntryState.Deleted,
                    Source = EntrySource.Mft,
                    IsDir = r.IsDir,
                    RecordNum = r.RecordNumber,
                    ResidentData = r.ResidentData,
                    Runs = r.DataRuns,
                    Health = r.InUse ? Health.Good
                                     : NtfsScanner.HealthFor(r, bitmap, scanner.ClusterSize),
                });
                if (!r.InUse) { del++; if (r.ResidentData != null) resident++; }
            }
            Log($"MFT pass complete: {records.Count:N0} records · {del:N0} deleted " +
                $"({resident:N0} resident—fully recoverable) · " +
                $"{scanner.BadRegionCount} unreadable regions");
            Status($"Scan complete — {_entries.Count:N0} entries");

            // deep carve as automatic second pass when MFT found few deleted
            if (del == 0)
            {
                Log("No deleted records in MFT — starting signature deep scan");
                await RunCarve(disk, prog);
            }
        }
        catch (Exception ex) { Status($"Scan failed: {ex.Message}"); Log($"ERROR: {ex.Message}"); }
        finally { SetBusy(false); }
        ApplyFilter();
    }

    private async Task RunCarve(RawDisk disk, IProgress<ScanProgress> prog)
    {
        try
        {
            var hits = await Task.Run(() =>
                new Carver(disk).Scan(prog, _cts!.Token).ToList(), _cts!.Token);
            foreach (var h in hits) _entries.Add(h);
            Log($"Deep scan: {hits.Count:N0} files carved by signature " +
                $"({disk.BadRanges.Count} unreadable regions total)");
        }
        catch (Exception ex) { Log($"Deep scan failed: {ex.Message}"); }
    }

    private async void DeepOnly_Click(object s, RoutedEventArgs e)
    {
        var disk = PickDisk();
        if (disk is null) return;
        _disk = disk;
        _entries.Clear();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var prog = MakeProgress();
        Log("Exhaustive deep scan — every sector for FILE records, then signature carve." +
            " Can take a long time on large drives.");
        try
        {
            var scanner = new NtfsScanner(disk);
            _scanner = scanner;
            var records = await Task.Run(() =>
                scanner.ScanRecords(prog, _cts.Token).ToList(), _cts.Token);
            scanner.LocateVolume();
            foreach (var r in records)
                _entries.Add(new RecoveredEntry
                {
                    Name = r.Name is "" ? $"record_{r.RecordNumber}" : r.Name,
                    FolderPath = scanner.BuildPath(r),
                    Size = r.RealSize,
                    State = r.InUse ? EntryState.Live : EntryState.Deleted,
                    Source = EntrySource.Mft,
                    IsDir = r.IsDir,
                    RecordNum = r.RecordNumber,
                    ResidentData = r.ResidentData,
                    Runs = r.DataRuns,
                    Health = Health.Unknown,
                });
            Log($"Exhaustive scan: {records.Count:N0} records · " +
                $"{scanner.BadRegionCount} unreadable regions");
            await RunCarve(disk, prog);
        }
        catch (Exception ex) { Status($"Deep scan failed: {ex.Message}"); Log($"ERROR: {ex.Message}"); }
        finally { SetBusy(false); }
        ApplyFilter();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { _cts?.Cancel(); Log("cancel requested"); }

    private IProgress<ScanProgress> MakeProgress() =>
        new Progress<ScanProgress>(p =>
        {
            if (p.Total > 0) Prog.Value = 1000.0 * p.Offset / p.Total;
            Status($"{p.Phase} — {p.Offset / 1e9:0.0}/{p.Total / 1e9:0.0} GB · " +
                   $"{p.RecordsFound:N0} records · {p.CarvedFound:N0} carved");
        });

    // ------------------------------------------------------------- filtering

    private void Filter_Changed(object s, RoutedEventArgs e) { _filter = FilterBox.Text; ApplyFilter(); }

    private void ApplyFilter()
    {
        if (!IsInitialized) return;   // events fire during XAML load before all controls exist
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        view.Filter = o =>
        {
            if (o is not RecoveredEntry r) return false;
            if (DeletedOnly.IsChecked == true && r.State != EntryState.Deleted) return false;
            if (_filter.Length > 0 && !r.DisplayPath.Contains(_filter,
                    StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        };
        CountLbl.Text = $"{view.Cast<RecoveredEntry>().Count():N0} shown / {_entries.Count:N0} found";
    }

    private void Grid_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        RecoverBtn.IsEnabled = Grid.SelectedItems.Count > 0;
        if (Grid.SelectedItem is RecoveredEntry en)
        {
            string loc = en.Source == EntrySource.Carved
                ? $"carved @ 0x{en.CarveOffset:X} len {en.CarveLen:N0} B"
                : en.ResidentData != null
                    ? $"resident in MFT record {en.RecordNum} ({en.ResidentData.Length} B) — data intact in metadata"
                    : $"MFT record {en.RecordNum} · {en.Runs.Count} run(s): " +
                      string.Join(", ", en.Runs.Take(4).Select(r => $"LCN {r.Lcn:N0}+{r.Clusters:N0}"))
                      + (en.Runs.Count > 4 ? " …" : "");
            DetailLbl.Text = $"{en.DisplayPath}   —   {loc}   —   health: {en.Health}";
        }
    }

    // ------------------------------------------------------------- recovery

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutDir.Text = dlg.SelectedPath;
    }

    private async void Recover_Click(object s, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<RecoveredEntry>().ToList();
        if (sel.Count == 0 || _disk is null) return;
        string outDir = OutDir.Text.Trim();
        if (outDir.Length == 0) { Status("Pick an output folder."); return; }

        // hard rule: never write back onto the source disk
        char letter = Path.GetPathRoot(Path.GetFullPath(outDir))?[0] ?? '\0';
        int srcVol = letter != '\0' ? RawDisk.DiskNumberForVolume(letter) : -1;
        if (srcVol == _disk.Number)
        {
            System.Windows.MessageBox.Show(this,
                $"The output folder is on the same physical disk (Disk {_disk.Number}).\n" +
                "Writing there could overwrite the data you're trying to recover.\n\n" +
                "Choose a folder on a different drive.", "Reclaim — unsafe target",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Directory.CreateDirectory(outDir);
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var scanner = _scanner;
        var disk = _disk;
        int done = 0, failed = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var en in sel)
                {
                    if (_cts.IsCancellationRequested) break;
                    try
                    {
                        string rel = en.FolderPath.StartsWith("[") ? "" : en.FolderPath;
                        string dir = Path.Combine(outDir, rel);
                        Directory.CreateDirectory(dir);
                        string dest = Path.Combine(dir,
                            string.Concat(en.Name.Split(Path.GetInvalidFileNameChars())));
                        if (en.IsDir) { Directory.CreateDirectory(dest); done++; continue; }
                        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
                        WriteData(disk, scanner, en, fs);
                        done++;
                    }
                    catch { failed++; }
                }
            }, _cts.Token);
            Log($"Recovered {done} file(s) to {outDir}" + (failed > 0 ? $" — {failed} failed" : ""));
            Status($"Recovered {done} file(s).");
        }
        finally { SetBusy(false); }
    }

    private static void WriteData(RawDisk disk, NtfsScanner? scanner,
                                  RecoveredEntry en, Stream out_)
    {
        if (en.Source == EntrySource.Carved)
        {
            CopyRegion(disk, out_, en.CarveOffset, en.CarveLen);
        }
        else if (en.ResidentData != null)
        {
            out_.Write(en.ResidentData, 0, (int)Math.Min(en.ResidentData.Length, en.Size));
        }
        else if (scanner != null && en.Runs.Count > 0)
        {
            long remain = en.Size;
            foreach (var (vcn, lcn, clusters) in en.Runs)
            {
                long want = Math.Min(clusters * scanner.ClusterSize, remain);
                if (want <= 0) break;
                CopyRegion(disk, out_, scanner.LcnToOffset(lcn), want);
                remain -= want;
            }
        }
    }

    private static void CopyRegion(RawDisk disk, Stream out_, long off, long len)
    {
        const int CH = 4 * 1024 * 1024;
        for (long pos = 0; pos < len; pos += CH)
        {
            int want = (int)Math.Min(CH, len - pos);
            var d = disk.ReadAt(off + pos, want);
            out_.Write(d, 0, d.Length);
        }
    }
}
