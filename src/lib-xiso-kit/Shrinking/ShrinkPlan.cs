using LibXisoKit.Analysis;
using LibXisoKit.Xdvdfs;

namespace LibXisoKit.Shrinking;

/// <summary>Where a proposed removal came from.</summary>
public enum RemovalSource
{
    /// <summary>Built-in safe rule ($SystemUpdate).</summary>
    BuiltIn,
    /// <summary>A rules database entry marked verified.</summary>
    VerifiedRule,
    /// <summary>A rules database entry not yet verified.</summary>
    UnverifiedRule,
    /// <summary>Folder-name heuristics.</summary>
    Heuristic,
    /// <summary>Requested by the user or caller.</summary>
    User,
}

/// <summary>A file or directory that may be removed. Toggle <see cref="Remove"/> before executing the plan.</summary>
public sealed class PlannedRemoval
{
    internal PlannedRemoval(string path, bool isDirectory, long bytes, int fileCount, RemovalSource source, Confidence confidence, string reason, bool remove)
    {
        Path = path;
        IsDirectory = isDirectory;
        Bytes = bytes;
        FileCount = fileCount;
        Source = source;
        Confidence = confidence;
        Reason = reason;
        Remove = remove;
    }

    public string Path { get; }
    public bool IsDirectory { get; }

    /// <summary>File bytes under this entry.</summary>
    public long Bytes { get; }

    public int FileCount { get; }
    public RemovalSource Source { get; }
    public Confidence Confidence { get; }
    public string Reason { get; }

    /// <summary>Whether this entry will be left out of the output.</summary>
    public bool Remove { get; set; }
}

/// <summary>
/// A reviewable description of what a shrink will do. Created by <see cref="XisoShrinker.CreatePlan"/>;
/// bind <see cref="Items"/> to a UI, let the user toggle <see cref="PlannedRemoval.Remove"/>, then execute.
/// </summary>
public sealed class ShrinkPlan
{
    private readonly List<PlannedRemoval> _items;
    private readonly XdvdfsVolume _volume;

    internal ShrinkPlan(XdvdfsVolume volume, IsoAnalysis analysis, ShrinkOptions options, List<PlannedRemoval> items, List<string> warnings)
    {
        _volume = volume;
        Analysis = analysis;
        Options = options;
        _items = items;
        Warnings = warnings;
    }

    public IsoAnalysis Analysis { get; }

    public ShrinkOptions Options { get; set; }

    public IReadOnlyList<PlannedRemoval> Items => _items;

    public IReadOnlyList<string> Warnings { get; }

    internal XdvdfsVolume Volume => _volume;

    /// <summary>Adds (or re-enables) a user-requested removal. Returns null if the path does not exist.</summary>
    public PlannedRemoval? AddRemoval(string path, string? reason = null)
    {
        var entry = _volume.Find(path);
        if (entry is null || entry == _volume.Root)
        {
            return null;
        }
        var existing = _items.FirstOrDefault(i => string.Equals(i.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Remove = true;
            return existing;
        }
        var (bytes, files) = Measure(entry);
        var item = new PlannedRemoval(entry.Path, entry.IsDirectory, bytes, files, RemovalSource.User, Confidence.Review, reason ?? "Requested by user.", true);
        _items.Add(item);
        return item;
    }

    /// <summary>Paths that will be left out (directories imply everything below them).</summary>
    public IReadOnlyList<string> SelectedPaths => _items.Where(i => i.Remove).Select(i => i.Path).ToList();

    /// <summary>File bytes that will be removed (overlapping selections counted once).</summary>
    public long RemovedBytes => SelectedEntries().Where(e => !e.IsDirectory).Sum(e => (long)e.Size);

    /// <summary>Number of files that will be removed.</summary>
    public int RemovedFileCount => SelectedEntries().Count(e => !e.IsDirectory);

    /// <summary>True when the plan needs <see cref="ShrinkMode.Rebuild"/>.</summary>
    public bool RequiresRebuild => _items.Any(i => i.Remove) || Options.Deduplicate;

    /// <summary>Approximate output size in bytes before compression.</summary>
    public long EstimatedOutputBytes
    {
        get
        {
            if (Options.Mode == ShrinkMode.TrimOnly)
            {
                return RoundUp(Analysis.UsedBytes);
            }
            var removed = new HashSet<XdvdfsEntry>(SelectedEntries());
            long sectors = XdvdfsConstants.RootDirectorySector;
            var seen = new HashSet<uint>();
            foreach (var e in _volume.EnumerateEntries())
            {
                if (removed.Contains(e))
                {
                    continue;
                }
                if (e.IsDirectory)
                {
                    sectors += Math.Max(1, e.SectorCount);
                }
                else if (e.Size > 0 && (!Options.Deduplicate || seen.Add(e.StartSector)))
                {
                    sectors += e.SectorCount;
                }
            }
            sectors += Math.Max(1, XdvdfsConstants.SectorsFor(_volume.RootSize)); // root table
            long dupSavings = Options.Deduplicate ? Analysis.DuplicateBytes : 0;
            return RoundUp(Math.Max(0, sectors * XdvdfsConstants.SectorSize - dupSavings));
        }
    }

    /// <summary>Every entry removed by the current selection, including descendants of selected directories.</summary>
    internal IEnumerable<XdvdfsEntry> SelectedEntries()
    {
        var selected = new HashSet<string>(SelectedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var e in _volume.EnumerateEntries())
        {
            if (IsSelected(e, selected))
            {
                yield return e;
            }
        }
    }

    internal static bool IsSelected(XdvdfsEntry e, HashSet<string> selected)
    {
        for (XdvdfsEntry? p = e; p is not null && p.Parent is not null; p = p.Parent)
        {
            if (selected.Contains(p.Path))
            {
                return true;
            }
        }
        return false;
    }

    internal static (long Bytes, int Files) Measure(XdvdfsEntry entry)
    {
        if (!entry.IsDirectory)
        {
            return (entry.Size, 1);
        }
        long bytes = 0;
        int files = 0;
        var stack = new Stack<XdvdfsEntry>(entry.Children);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.IsDirectory)
            {
                foreach (var c in e.Children)
                {
                    stack.Push(c);
                }
            }
            else
            {
                bytes += e.Size;
                files++;
            }
        }
        return (bytes, files);
    }

    private static long RoundUp(long bytes) =>
        (bytes + XdvdfsConstants.FileModulus - 1) / XdvdfsConstants.FileModulus * XdvdfsConstants.FileModulus;
}
