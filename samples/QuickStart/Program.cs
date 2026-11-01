// The README's example as a program CI compiles on every change.
// usage: QuickStart <image> <output.cci> [rules.json]
using LibXisoKit;
using LibXisoKit.Analysis;
using LibXisoKit.IO;
using LibXisoKit.Shrinking;
using LibXisoKit.Xdvdfs;

if (args.Length < 2)
{
    Console.WriteLine("usage: QuickStart <image> <output.cci> [rules.json]");
    return 1;
}
string input = args[0], output = args[1];
string? rulesPath = args.Length > 2 ? args[2] : null;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var progress = new Progress<OperationProgress>(p => Console.Write($"\r{p.Stage}: {(p.Fraction ?? 0):P0}   "));

// Open any supported image: Redump ISO, XISO, split .1.iso/.2.iso, CCI or CSO.
using var shrinker = await XisoShrinker.OpenAsync(input, cts.Token);

// Analyze: title from default.xbe, used/unused space, video partition, demos, duplicates.
IsoAnalysis analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions { FindDuplicates = true }, progress, cts.Token);
Console.WriteLine($"\n{analysis.DefaultXbe?.TitleName} [{analysis.DefaultXbe?.TitleIdHex}] {analysis.DefaultXbe?.RegionLabel}, "
                  + $"{analysis.FileCount} files, {analysis.UsedBytes / 1048576.0:F0} MiB used of {analysis.ImageLength / 1048576.0:F0} MiB");

// Plan: a reviewable list of removals. Toggle PlannedRemoval.Remove in a UI before executing.
ShrinkRuleSet? rules = rulesPath is null ? null : ShrinkRuleSet.Load(rulesPath);
ShrinkPlan plan = shrinker.CreatePlan(analysis, new ShrinkOptions
{
    Format = OutputFormat.Cci,
    SplitSize = XdvdfsConstants.FatxSplitSize, // parts below the FATX 4 GiB limit
    Deduplicate = true,
}, rules);
foreach (var w in plan.Warnings) Console.WriteLine($"  note: {w}");

// Execute: writes the output, then re-opens and verifies it. A failed or cancelled run leaves no files.
ShrinkResult result = await shrinker.ExecuteAsync(plan, output, progress, cts.Token);
Console.WriteLine($"\nWrote {string.Join(", ", result.OutputFiles.Select(Path.GetFileName))}: "
                  + $"{result.SourceBytes / 1048576.0:F0} MiB -> {result.OutputBytes / 1048576.0:F0} MiB, "
                  + $"verified: {result.Verification?.Success}, logical SHA-1 {result.LogicalSha1}");
return 0;
