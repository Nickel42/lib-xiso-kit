using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoIntrovert.XboxIso.Analysis;

/// <summary>
/// A rules database describing which paths may be removed from which titles.
/// Load with <see cref="Load(string)"/> / <see cref="Parse"/>; merge several files with <see cref="Merge"/>.
/// </summary>
public sealed class ShrinkRuleSet
{
    /// <summary>Schema version; currently 1.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Rules applied to every title.</summary>
    public List<ShrinkRule> Global { get; set; } = new();

    /// <summary>Per-title rules.</summary>
    public List<TitleRules> Titles { get; set; } = new();

    /// <summary>The built-in, always-safe rules (dashboard updates only).</summary>
    public static ShrinkRuleSet BuiltIn => new()
    {
        Global =
        {
            new ShrinkRule
            {
                Remove = { "**/$SystemUpdate/**" },
                Reason = "Xbox dashboard update bundled on the disc; never needed on a modded console or emulator.",
                Verified = true,
            },
        },
    };

    public static ShrinkRuleSet Parse(string json) =>
        JsonSerializer.Deserialize(json, ShrinkRulesJsonContext.Default.ShrinkRuleSet)
        ?? throw new InvalidDataException("Rules file is empty.");

    public static ShrinkRuleSet Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, ShrinkRulesJsonContext.Default.ShrinkRuleSet);

    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Combines rule sets; later sets add to (they never remove) earlier ones.</summary>
    public static ShrinkRuleSet Merge(params ShrinkRuleSet[] sets)
    {
        var result = new ShrinkRuleSet();
        foreach (var s in sets)
        {
            result.Global.AddRange(s.Global);
            result.Titles.AddRange(s.Titles);
        }
        return result;
    }

    /// <summary>Rules that apply to a specific title/version (global rules included).</summary>
    public IEnumerable<(ShrinkRule Rule, TitleRules? Title)> RulesFor(uint? titleId, uint? version)
    {
        foreach (var g in Global)
        {
            yield return (g, null);
        }
        if (titleId is null)
        {
            yield break;
        }
        foreach (var t in Titles)
        {
            if (!t.Matches(titleId.Value, version))
            {
                continue;
            }
            foreach (var r in t.Rules)
            {
                yield return (r, t);
            }
        }
    }
}

/// <summary>Rules for one title, optionally restricted to specific XBE versions.</summary>
public sealed class TitleRules
{
    /// <summary>Title ID as 8 hex digits (e.g. "4D530004").</summary>
    public string TitleId { get; set; } = string.Empty;

    /// <summary>Human-readable name, informational only.</summary>
    public string? Name { get; set; }

    /// <summary>XBE versions these rules apply to (hex or decimal); empty means every version.</summary>
    public List<string> Versions { get; set; } = new();

    public List<ShrinkRule> Rules { get; set; } = new();

    public bool Matches(uint titleId, uint? version)
    {
        if (!uint.TryParse(TitleId, System.Globalization.NumberStyles.HexNumber, null, out uint id) || id != titleId)
        {
            return false;
        }
        if (Versions.Count == 0 || version is null)
        {
            return Versions.Count == 0;
        }
        foreach (var v in Versions)
        {
            string s = v.Trim();
            uint parsed;
            bool ok;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ok = uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out parsed);
            }
            else
            {
                ok = uint.TryParse(s, out parsed);
            }
            if (ok && parsed == version)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>A set of glob patterns that may be removed, with provenance.</summary>
public sealed class ShrinkRule
{
    /// <summary>Glob patterns (see <see cref="GlobMatcher"/>) of files/directories to remove.</summary>
    public List<string> Remove { get; set; } = new();

    /// <summary>Why the content can be removed (shown to the user).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// True when someone confirmed the trimmed image still boots and plays. Unverified rules are
    /// proposed in the plan but not applied unless <see cref="Shrinking.ShrinkOptions.ApplyUnverifiedRules"/> is set.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>Who/what verified the rule (emulator, hardware, BIOS), free text.</summary>
    public string? VerifiedOn { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShrinkRuleSet))]
internal sealed partial class ShrinkRulesJsonContext : JsonSerializerContext
{
}
