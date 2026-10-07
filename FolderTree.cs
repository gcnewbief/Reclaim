using System.ComponentModel;

namespace Reclaim;

/// <summary>One node in the recovered-folder tree shown left of the results
/// grid. The model is built once per scan from RecoveredEntry.FolderPath;
/// the TreeView virtualizes visuals, so the whole tree can be materialized
/// as plain objects.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IsCheckedArgs = new(nameof(IsChecked));
    private static readonly PropertyChangedEventArgs CountTextArgs = new(nameof(CountText));

    // NOTE: everything the XAML binds to must be a property, not a field —
    // {Binding Children}/{Binding Name} silently produce nothing on fields.
    public string Name { get; set; } = "";
    public string? FullPath { get; set; }    // null = the "all results" pseudo-node
    public FolderNode? Parent { get; set; }
    public List<FolderNode> Children { get; } = new();
    public List<RecoveredEntry> Files { get; } = new();  // direct contents (files + dir entries)
    public bool IsAll { get; set; }

    public int DescTotal { get; set; }       // entries at/below this node
    public int DescChecked { get; set; }

    private bool? _isChecked;
    public bool? IsChecked
    {
        get => _isChecked;
        private set { _isChecked = value; PropertyChanged?.Invoke(this, IsCheckedArgs); }
    }

    public string CountText => DescChecked > 0
        ? $"✓{DescChecked:N0}/{DescTotal:N0}"
        : $"({DescTotal:N0})";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Recompute tri-state + label from DescChecked/DescTotal.</summary>
    public void RefreshCheck()
    {
        IsChecked = DescTotal > 0 && DescChecked >= DescTotal ? true
                  : DescChecked > 0 ? (bool?)null : false;
        PropertyChanged?.Invoke(this, CountTextArgs);
    }
}

public static class FolderTreeBuilder
{
    /// <summary>Group entries into a folder tree. Returns the ItemsSource roots
    /// (an "all results" pseudo-node plus the volume root), the two special
    /// nodes, and a path->node index used to bump counts on single toggles.</summary>
    public static (List<FolderNode> Roots, FolderNode All, FolderNode Root,
                   Dictionary<string, FolderNode> ByPath)
        Build(List<RecoveredEntry> entries)
    {
        var root = new FolderNode { Name = "(volume root)", FullPath = "" };
        var all = new FolderNode { Name = "≡ ALL RESULTS", IsAll = true, FullPath = null };
        var byPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase)
        { [""] = root };

        FolderNode NodeFor(string path)
        {
            if (byPath.TryGetValue(path, out var n)) return n;
            int cut = path.LastIndexOf('\\');
            var parent = NodeFor(cut < 0 ? "" : path[..cut]);
            n = new FolderNode
            {
                Name = cut < 0 ? path : path[(cut + 1)..],
                FullPath = path,
                Parent = parent,
            };
            parent.Children.Add(n);
            byPath[path] = n;
            return n;
        }

        foreach (var en in entries)
        {
            NodeFor(en.FolderPath).Files.Add(en);
            if (en.IsDir)   // ensure even empty dirs appear as nodes
                NodeFor(en.FolderPath.Length == 0 ? en.Name : en.FolderPath + "\\" + en.Name);
        }

        // sort children, then post-order rollup of totals + initial check counts
        static int Finish(FolderNode n)
        {
            n.Children.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            int total = n.Files.Count, chk = n.Files.Count(f => f.Checked);
            foreach (var c in n.Children) { total += Finish(c); chk += c.DescChecked; }
            n.DescTotal = total; n.DescChecked = chk;
            n.RefreshCheck();
            return total;
        }
        Finish(root);

        all.DescTotal = root.DescTotal;
        all.DescChecked = root.DescChecked;
        all.RefreshCheck();

        return (new List<FolderNode> { all, root }, all, root, byPath);
    }
}
