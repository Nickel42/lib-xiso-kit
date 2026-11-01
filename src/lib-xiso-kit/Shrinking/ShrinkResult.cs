using System.Text.Json;
using System.Text.Json.Serialization;
using LibXisoKit.Identity;
using LibXisoKit.IO;
using LibXisoKit.Xbe;

namespace LibXisoKit.Shrinking;

/// <summary>Outcome of verifying an image.</summary>
public sealed record VerifyResult(
    bool Success,
    string? LogicalSha1,
    int FileCount,
    XbeInfo? DefaultXbe,
    IReadOnlyList<string> Problems);

/// <summary>Outcome of <see cref="XisoShrinker.Execute"/>.</summary>
public sealed record ShrinkResult
{
    public required IReadOnlyList<string> OutputFiles { get; init; }
    public required OutputFormat Format { get; init; }
    public required ShrinkMode Mode { get; init; }

    /// <summary>Size of the source file(s) on disk.</summary>
    public required long SourceBytes { get; init; }

    /// <summary>Size of the output file(s) on disk.</summary>
    public required long OutputBytes { get; init; }

    /// <summary>Uncompressed size of the output image.</summary>
    public required long LogicalBytes { get; init; }

    public required string LogicalSha1 { get; init; }
    public required IReadOnlyList<string> RemovedPaths { get; init; }
    public required int RemovedFileCount { get; init; }
    public required long RemovedBytes { get; init; }
    public required int DeduplicatedFiles { get; init; }
    public required long DeduplicatedBytes { get; init; }
    public required int PatchedExecutables { get; init; }
    public required long ScrubbedBytes { get; init; }
    public required VerifyResult? Verification { get; init; }
    public required FileHashes? SourceHashes { get; init; }
    public required DatEntry? DatMatch { get; init; }
    public required string? SidecarPath { get; init; }
    public required bool SourceDeleted { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public long SavedBytes => SourceBytes - OutputBytes;

    public double SavedFraction => SourceBytes > 0 ? (double)SavedBytes / SourceBytes : 0;
}

/// <summary>
/// JSON written next to the output so the original disc identity survives modification
/// (a trimmed image can never match a Redump checksum again).
/// </summary>
public sealed class ShrinkSidecar
{
    public string Tool { get; set; } = "lib-xiso-kit";
    public string ToolVersion { get; set; } = typeof(ShrinkSidecar).Assembly.GetName().Version?.ToString() ?? "unknown";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public SidecarSource Source { get; set; } = new();
    public SidecarTitle? Title { get; set; }
    public SidecarChanges Changes { get; set; } = new();
    public SidecarOutput Output { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, SidecarJsonContext.Default.ShrinkSidecar);

    public static ShrinkSidecar Parse(string json) =>
        JsonSerializer.Deserialize(json, SidecarJsonContext.Default.ShrinkSidecar) ?? throw new InvalidDataException("Empty sidecar.");

    /// <summary>Conventional sidecar path for an output image.</summary>
    public static string PathFor(string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        return Path.Combine(dir, Path.GetFileName(outputPath) + ".shrink.json");
    }
}

public sealed class SidecarSource
{
    public List<string> Files { get; set; } = new();
    public long Size { get; set; }
    public string? Container { get; set; }
    public string? Layout { get; set; }
    public string? Crc32 { get; set; }
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? DatName { get; set; }
    public string? DatGame { get; set; }
    public string? DatRom { get; set; }
}

public sealed class SidecarTitle
{
    public string? TitleId { get; set; }
    public string? TitleIdDisplay { get; set; }
    public string? Name { get; set; }
    public string? Region { get; set; }
    public uint Version { get; set; }
    public uint DiscNumber { get; set; }
}

public sealed class SidecarChanges
{
    public string? Mode { get; set; }
    public List<string> RemovedPaths { get; set; } = new();
    public int RemovedFiles { get; set; }
    public long RemovedBytes { get; set; }
    public int DeduplicatedFiles { get; set; }
    public long DeduplicatedBytes { get; set; }
    public int MediaPatchedExecutables { get; set; }
    public long ScrubbedBytes { get; set; }
    public string? Timestamp { get; set; }
}

public sealed class SidecarOutput
{
    public List<string> Files { get; set; } = new();
    public string? Format { get; set; }
    public long SizeOnDisk { get; set; }
    public long LogicalSize { get; set; }
    public string? LogicalSha1 { get; set; }
    public bool? Verified { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShrinkSidecar))]
internal sealed partial class SidecarJsonContext : JsonSerializerContext
{
}
