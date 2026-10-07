using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    // folder tree (left panel)
    private string? _folderFilter;                       // null = "all results"
    private Dictionary<string, FolderNode> _nodeByPath = new();
    private FolderNode? _treeRoot;                       // "(volume root)"
    private FolderNode? _allNode;
    private bool _suppressTreeCount;

    public MainWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _entries;
        RecoveredEntry.CheckedChanged = OnEntryChecked;
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        AdminNote.Text = admin ? "" : "  ⚠ not elevated — relaunch as Administrator";
        Log(admin ? "Running elevated — raw disk access available."
                  : "NOT elevated — drive list may be empty.");
        var v = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        VersionLbl.Text = $"v{v}";
        Title = $"Reclaim — data recovery  ·  v{v}";
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

    // ------------------------------------------------------------- drive health light

    private int _lightGen;
    private Smart.Report? _lastSmart;
    private int _lastSmartDisk = -1;

    private void SetLight(Smart.Light l, string tip)
    {
        DriveLight.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(l switch
            {
                Smart.Light.Green   => "#35e05a",
                Smart.Light.Amber   => "#ffb000",
                Smart.Light.Red     => "#ff5252",
                _                   => "#3a4a3a",
            }));
        DriveLightLbl.Text = $"drive health: {l.ToString().ToLowerInvariant()}";
        DriveLightBox.ToolTip = tip;
    }

    private async void DriveList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _lastSmart = null; _lastSmartDisk = -1;
        if (DriveList.SelectedItem is not RawDisk disk)
        { SetLight(Smart.Light.Unknown, "no drive selected"); return; }
        int gen = ++_lightGen;
        SetLight(Smart.Light.Unknown, $"Disk {disk.Number}: reading SMART…");
        var rep = await Task.Run(() => Smart.Read(disk.Number));
        if (gen != _lightGen) return;                    // selection changed meanwhile
        _lastSmart = rep; _lastSmartDisk = disk.Number;
        SetLight(rep.TrafficLight, rep.Summary);
        Log($"SMART: {rep.Summary} — {rep.TrafficLight}");
    }

    private RawDisk? PickDisk()
    {
        if (DriveList.SelectedItem is RawDisk d) return d;
        Status("Pick a drive first.");
        return null;
    }

    private void SetBusy(bool busy)
    {
        ScanBtn.IsEnabled = DeepOnlyBtn.IsEnabled = RefreshBtn.IsEnabled =
            SmartBtn.IsEnabled = !busy;
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
        await RebuildTree();
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
        await RebuildTree();
        ApplyFilter();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { _cts?.Cancel(); Log("cancel requested"); }

    private async void Smart_Click(object s, RoutedEventArgs e)
    {
        var disk = PickDisk();
        if (disk is null) return;
        SmartBtn.IsEnabled = false;
        Smart.Report rep;
        if (_lastSmart is { } cached && _lastSmartDisk == disk.Number)
            rep = cached;                            // auto-read on selection already ran
        else
        {
            Log($"reading SMART for Disk {disk.Number}…");
            rep = await Task.Run(() => Smart.Read(disk.Number));
            _lastSmart = rep; _lastSmartDisk = disk.Number;
            SetLight(rep.TrafficLight, rep.Summary);
            Log($"SMART: {rep.Summary}");
        }
        SmartBtn.IsEnabled = true;
        try { ShowReport($"Disk {disk.Number} SMART", Smart.Format(rep)); }
        catch (Exception ex) { Log($"report window failed: {ex.Message}"); }
    }

    private void ShowReport(string title, string text)
    {
        var w = new Window
        {
            Title = title, Width = 820, Height = 560, Owner = this,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0a0e0a")),
        };
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#69f0ae")),
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        w.Content = box;
        w.Show();
        Log($"  report window '{title}' shown; windows={System.Windows.Application.Current.Windows.Count}");
    }

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
            if (_folderFilter != null &&
                !string.Equals(r.FolderPath, _folderFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (DeletedOnly.IsChecked == true && r.State != EntryState.Deleted) return false;
            if (_filter.Length > 0 && !r.DisplayPath.Contains(_filter,
                    StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        };
        RefreshCheckState();                        // syncs counts, header box, recover button
        if (_entries.Count == 0) { HealthLbl.Text = ""; return; }
        var g = _entries.GroupBy(e => e.Health)
            .ToDictionary(x => x.Key, x => x.Count());
        HealthLbl.Text =
            $"health: ■ {g.GetValueOrDefault(Health.Good):N0} good " +
            $"■ {g.GetValueOrDefault(Health.Partial):N0} partial " +
            $"■ {g.GetValueOrDefault(Health.Poor):N0} poor " +
            $"■ {g.GetValueOrDefault(Health.Unknown):N0} unknown";
    }

    private void UpdateCounts()
    {
        int shown = System.Windows.Data.CollectionViewSource
            .GetDefaultView(_entries).Cast<RecoveredEntry>().Count();
        int chk = _entries.Count(e => e.Checked);
        CountLbl.Text = $"{shown:N0} shown / {_entries.Count:N0} found" +
                        (chk > 0 ? $"  ·  {chk:N0} checked" : "");
    }

    // ------------------------------------------------------------- checkboxes

    private bool _bulk;

    private System.Collections.Generic.IEnumerable<RecoveredEntry> Filtered() =>
        System.Windows.Data.CollectionViewSource
            .GetDefaultView(_entries).Cast<RecoveredEntry>();

    private void CheckAll_Changed(object s, RoutedEventArgs e)
    {
        if (_bulk) return;
        bool val = CheckAll.IsChecked == true;
        _suppressTreeCount = true;
        foreach (var en in Filtered()) en.Checked = val;
        _suppressTreeCount = false;
        RecountTree();
        RefreshCheckState();
    }

    /// <summary>RecoveredEntry.CheckedChanged hook — single source of truth for
    /// every check toggle (grid click, UIA TogglePattern, tree apply is
    /// suppressed and recounted in bulk instead).</summary>
    private void OnEntryChecked(RecoveredEntry en, bool nowChecked)
    {
        if (_suppressTreeCount) return;
        if (_nodeByPath.TryGetValue(en.FolderPath, out var node))
            for (var p = node; p != null; p = p.Parent)
            { p.DescChecked += nowChecked ? 1 : -1; p.RefreshCheck(); }
        SyncAllNode();
        RefreshCheckState();
    }

    // ------------------------------------------------------------- folder tree

    private async Task RebuildTree()
    {
        _folderFilter = null;
        FolderTree.ItemsSource = null;
        _treeRoot = _allNode = null;
        _nodeByPath = new();
        if (_entries.Count == 0) return;
        var snap = _entries.ToList();              // scan is done; snapshot for the bg pass
        var (roots, all, root, byPath) =
            await Task.Run(() => FolderTreeBuilder.Build(snap));
        _nodeByPath = byPath;
        _treeRoot = root;
        _allNode = all;
        FolderTree.ItemsSource = roots;
        Log($"folder tree: {byPath.Count - 1:N0} folders from {_entries.Count:N0} entries");
    }

    private void SyncAllNode()
    {
        if (_allNode is null || _treeRoot is null) return;
        _allNode.DescTotal = _treeRoot.DescTotal;
        _allNode.DescChecked = _treeRoot.DescChecked;
        _allNode.RefreshCheck();
    }

    /// <summary>Full recount of DescChecked — used after bulk ops that ran
    /// with the per-entry hook suppressed. O(entries + nodes).</summary>
    private void RecountTree()
    {
        if (_treeRoot is null) return;
        static int Sum(FolderNode n)
        {
            int c = n.Files.Count(f => f.Checked);
            foreach (var ch in n.Children) c += Sum(ch);
            n.DescChecked = c;
            n.RefreshCheck();
            return c;
        }
        Sum(_treeRoot);
        SyncAllNode();
    }

    private void ToggleFolder(FolderNode node)
    {
        var n = node.IsAll ? _treeRoot : node;     // "all results" toggles the whole tree
        if (n is null) return;
        bool v = n.DescChecked < n.DescTotal;      // partial/empty -> check all under it
        int before = n.DescChecked;
        _suppressTreeCount = true;
        ApplyTreeCheck(n, v);
        _suppressTreeCount = false;
        int delta = n.DescChecked - before;
        for (var p = n.Parent; p != null; p = p.Parent)
        { p.DescChecked += delta; p.RefreshCheck(); }
        SyncAllNode();
        RefreshCheckState();
        Log($"folder {(n.FullPath is { Length: > 0 } fp ? fp : n.Name)}: " +
            $"{(v ? "checked" : "cleared")} — {Math.Abs(delta):N0} entries");
    }

    private static void ApplyTreeCheck(FolderNode n, bool v)
    {
        foreach (var f in n.Files) f.Checked = v;
        foreach (var c in n.Children) ApplyTreeCheck(c, v);
        n.DescChecked = v ? n.DescTotal : 0;
        n.RefreshCheck();
    }

    // mouse: intercept before the CheckBox cycles its tri-state so we decide
    private void FolderCheck_Preview(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FolderNode n) ToggleFolder(n);
        e.Handled = true;
    }

    // keyboard (space) still lands here — same deterministic rule
    private void FolderCheck_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FolderNode n) ToggleFolder(n);
    }

    private void FolderTree_Selected(object s, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode n) return;
        _folderFilter = n.FullPath;                // null on "all results" = no folder limit
        ApplyFilter();
        DetailLbl.Text = n.IsAll
            ? $"all results — {n.DescTotal:N0} entries · {n.DescChecked:N0} checked"
            : $"{(n.FullPath!.Length > 0 ? n.FullPath + "\\" : "(volume root)")} — " +
              $"{n.Files.Count:N0} item(s) here · {n.DescTotal:N0} incl. subfolders · " +
              $"{n.DescChecked:N0} checked";
    }

    private void RefreshCheckState()
    {
        if (!IsInitialized) return;
        int chk = _entries.Count(e => e.Checked);
        RecoverBtn.IsEnabled = chk > 0 || Grid.SelectedItems.Count > 0;
        _bulk = true;
        int shown = Filtered().Count();
        CheckAll.IsChecked = chk == 0 ? false : chk >= shown && shown > 0 ? true : null;
        _bulk = false;
        UpdateCounts();
    }

    private void Grid_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        RecoverBtn.IsEnabled = Grid.SelectedItems.Count > 0 || _entries.Any(x => x.Checked);
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
        var sel = _entries.Where(x => x.Checked).ToList();   // checkbox set wins
        if (sel.Count == 0)
            sel = Grid.SelectedItems.Cast<RecoveredEntry>().ToList();
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
        int done = 0, failed = 0, idx = 0;

        var hud = new RecoveryHud(this, sel.Count, _cts);
        IProgress<(int idx, int done, int fail, string name, long fb, long ft)> prog =
            new Progress<(int, int, int, string, long, long)>(
                t => hud.Update(t.Item1, t.Item2, t.Item3, t.Item4, t.Item5, t.Item6));

        try
        {
            await Task.Run(() =>
            {
                foreach (var en in sel)
                {
                    if (_cts.IsCancellationRequested) break;
                    idx++;
                    prog.Report((idx, done, failed, en.Name, 0, en.Size));
                    try
                    {
                        string rel = en.FolderPath.StartsWith("[") ? "" : en.FolderPath;
                        string dir = Path.Combine(outDir, rel);
                        Directory.CreateDirectory(dir);
                        string dest = Path.Combine(dir,
                            string.Concat(en.Name.Split(Path.GetInvalidFileNameChars())));
                        if (en.IsDir) { Directory.CreateDirectory(dest); done++; continue; }
                        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
                        WriteData(disk, scanner, en, fs,
                            b => prog.Report((idx, done, failed, en.Name, b, en.Size)));
                        done++;
                    }
                    catch { failed++; }
                }
            }, _cts.Token);
            Log($"Recovered {done} file(s) to {outDir}" + (failed > 0 ? $" — {failed} failed" : "") +
                (idx < sel.Count ? $" — cancelled after {idx}" : ""));
            Status($"Recovered {done} file(s).");
        }
        finally { SetBusy(false); hud.Close(); }
    }

    /// <summary>Small dark progress window while Recover_Click copies data.
    /// Built in code — window resources/styles don't flow across windows, so
    /// colors are set explicitly. Closing it cancels the recovery.</summary>
    private sealed class RecoveryHud
    {
        private readonly Window _win;
        private readonly System.Windows.Controls.ProgressBar _files, _fileBytes;
        private readonly TextBlock _line1, _line2;

        private static System.Windows.Media.Brush B(string hex) =>
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        public RecoveryHud(Window owner, int totalFiles, CancellationTokenSource cts)
        {
            var mono = new System.Windows.Media.FontFamily("Consolas");
            _line1 = new TextBlock
            {
                Foreground = B("#d9ffe4"), FontFamily = mono, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _line2 = new TextBlock
            {
                Foreground = B("#6f8f76"), FontFamily = mono, FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            };
            _files = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0, Maximum = totalFiles, Height = 12,
                Foreground = B("#00e676"), Background = B("#0c110c"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            _fileBytes = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0, Maximum = 1000, Height = 5,
                Foreground = B("#69f0ae"), Background = B("#0c110c"),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var cancel = new System.Windows.Controls.Button
            {
                Content = "✕ CANCEL",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 5, 12, 5),
                Background = B("#1a2b1a"), Foreground = B("#69f0ae"),
                BorderBrush = B("#1d2b1d"),
            };
            cancel.Click += (_, _) => cts.Cancel();

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(_line1);
            panel.Children.Add(_files);
            panel.Children.Add(_fileBytes);
            panel.Children.Add(_line2);
            panel.Children.Add(cancel);

            _win = new Window
            {
                Title = "Reclaim — recovering", Width = 560, SizeToContent = SizeToContent.Height,
                Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = B("#0a0e0a"),
                Icon = owner.Icon, Content = panel, Topmost = false,
            };
            _win.Closing += (_, _) => cts.Cancel();
            _win.Show();
        }

        public void Update(int idx, int done, int fail, string name, long fb, long ft)
        {
            _files.Value = idx;
            _line1.Text = $"file {idx} — {name}";
            _fileBytes.Value = ft > 0 ? 1000.0 * fb / ft : 0;
            _line2.Text = ft > 0
                ? $"{fb / 1e6:0.#} / {ft / 1e6:0.#} MB   ·   ok {done} · failed {fail}"
                : $"ok {done} · failed {fail}";
        }

        public void Close() => _win.Close();
    }

    private static void WriteData(RawDisk disk, NtfsScanner? scanner,
                                  RecoveredEntry en, Stream out_, Action<long>? onBytes = null)
    {
        if (en.Source == EntrySource.Carved)
        {
            CopyRegion(disk, out_, en.CarveOffset, en.CarveLen, onBytes);
        }
        else if (en.ResidentData != null)
        {
            int n = (int)Math.Min(en.ResidentData.Length, en.Size);
            out_.Write(en.ResidentData, 0, n);
            onBytes?.Invoke(n);
        }
        else if (scanner != null && en.Runs.Count > 0)
        {
            long remain = en.Size, wrote = 0;
            foreach (var (vcn, lcn, clusters) in en.Runs)
            {
                long want = Math.Min(clusters * scanner.ClusterSize, remain);
                if (want <= 0) break;
                long base_ = wrote;
                CopyRegion(disk, out_, scanner.LcnToOffset(lcn), want,
                    b => onBytes?.Invoke(base_ + b));
                wrote += want;
                remain -= want;
            }
        }
    }

    private static void CopyRegion(RawDisk disk, Stream out_, long off, long len,
                                   Action<long>? onBytes = null)
    {
        const int CH = 4 * 1024 * 1024;
        for (long pos = 0; pos < len; pos += CH)
        {
            int want = (int)Math.Min(CH, len - pos);
            var d = disk.ReadAt(off + pos, want);
            out_.Write(d, 0, d.Length);
            onBytes?.Invoke(pos + d.Length);
        }
    }
}
