namespace Reclaim;

public enum EntryState { Live, Deleted }
public enum EntrySource { Mft, Carved }
public enum Health { Good, Partial, Poor, Unknown }

/// <summary>One recoverable file/dir found on the disk.</summary>
public sealed class RecoveredEntry : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly System.ComponentModel.PropertyChangedEventArgs CheckedArgs =
        new(nameof(Checked));
    private bool _checked;

    /// <summary>Checkbox state in the results grid — marks the file for recovery.</summary>
    public bool Checked
    {
        get => _checked;
        set { _checked = value; PropertyChanged?.Invoke(this, CheckedArgs); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "";
    public string FolderPath { get; set; } = "";   // reconstructed parent chain
    public long Size { get; set; }
    public EntryState State;
    public EntrySource Source;
    public Health Health = Health.Unknown;
    public bool IsDir;

    // location of the data
    public long RecordNum = -1;                    // MFT record (Mft source)
    public byte[]? ResidentData;                   // small files stored in-record
    public List<(long Vcn, long Lcn, long Clusters)> Runs = new(); // non-resident
    public long CarveOffset;                       // Carved source
    public long CarveLen;
    public string Ext = "";                        // carved files get ext from sig

    public string DisplayPath => FolderPath is "" ? Name : FolderPath + "\\" + Name;
}

public sealed class ScanProgress
{
    public long Offset;
    public long Total;
    public int RecordsFound;
    public int DeletedFound;
    public int CarvedFound;
    public string Phase = "";
}
