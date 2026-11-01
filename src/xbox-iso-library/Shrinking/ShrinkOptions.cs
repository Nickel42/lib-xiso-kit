using XboxIsoLibrary.IO;
using XboxIsoLibrary.Xdvdfs;

namespace XboxIsoLibrary.Shrinking;

/// <summary>How the output image is produced.</summary>
public enum ShrinkMode
{
    /// <summary>
    /// Repack the filesystem into a new minimal XISO. Required for removing files or deduplicating.
    /// Equivalent to extract-xiso / xdvdfs "pack" / Repackinator rebuild.
    /// </summary>
    Rebuild,

    /// <summary>
    /// Copy the game partition as-is (files keep their original sectors), dropping the video partition and
    /// the unused tail. Cannot remove files. The most conservative option.
    /// </summary>
    TrimOnly,
}

/// <summary>Volume timestamp written to the output.</summary>
public enum TimestampMode
{
    /// <summary>Keep the source image's timestamp.</summary>
    PreserveSource,
    /// <summary>Write 0 (1601-01-01) so the same input and options always give byte-identical output.</summary>
    Deterministic,
    /// <summary>Use the current time.</summary>
    Now,
}

/// <summary>Everything that controls a shrink operation. Defaults are the safe, lossless choices.</summary>
public sealed record ShrinkOptions
{
    public ShrinkMode Mode { get; init; } = ShrinkMode.Rebuild;

    public OutputFormat Format { get; init; } = OutputFormat.Iso;

    /// <summary>Split the output into parts of at most this many bytes. Use <see cref="XdvdfsConstants.FatxSplitSize"/> for Xbox hard drives.</summary>
    public long? SplitSize { get; init; }

    /// <summary>Remove bundled dashboard updates (<c>$SystemUpdate</c>). Safe for modded consoles and emulators.</summary>
    public bool RemoveSystemUpdate { get; init; } = true;

    /// <summary>Apply rules from the rules database that are not marked verified.</summary>
    public bool ApplyUnverifiedRules { get; init; }

    /// <summary>Pre-select heuristic "likely demo" folders for removal (they are always listed in the plan).</summary>
    public bool RemoveLikelyDemos { get; init; }

    /// <summary>Extra glob patterns to remove (see <see cref="Analysis.GlobMatcher"/>).</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Share sectors between byte-identical files (like Qwix "cross-link identical files"). Rebuild mode only.</summary>
    public bool Deduplicate { get; init; }

    /// <summary>Apply extract-xiso's XBE media-check patch. Helps some BIOSes run games from hard disk.</summary>
    public bool MediaPatch { get; init; } = true;

    /// <summary>TrimOnly mode: zero-fill unused sectors so the result compresses well.</summary>
    public bool Scrub { get; init; } = true;

    public TimestampMode Timestamp { get; init; } = TimestampMode.PreserveSource;

    /// <summary>Re-read the written output and confirm it matches what was intended.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Write a JSON sidecar recording the source identity and what was changed.</summary>
    public bool WriteSidecar { get; init; } = true;

    /// <summary>Hash the source file(s) (CRC32/MD5/SHA-1) before shrinking, for the sidecar and DAT matching.</summary>
    public bool HashSource { get; init; }

    /// <summary>Optional Redump DAT used to identify the source (implies <see cref="HashSource"/>).</summary>
    public Identity.DatFile? Dat { get; init; }

    /// <summary>Replace existing output files.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Delete the source file(s) after a successful, verified shrink.</summary>
    public bool DeleteSourceAfterVerify { get; init; }
}
