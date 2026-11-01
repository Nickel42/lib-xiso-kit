# NoIntrovert.XboxIso

A dependency-free .NET 8/9 library for shrinking original Xbox disc images, built for the NoIntrovert Avalonia app. It has no NuGet dependencies and no native binaries, and it is trim- and AOT-friendly.

**Status: 1.0.0-rc.1.** This code was written without access to a .NET SDK, so the first `dotnet build` happens on your machine. The on-disc format logic (directory-table layout, LZ4, CCI and CSO) was validated with a reference prototype. That prototype's output was cross-checked against `extract-xiso` 2.7.1 built from source and against the system `liblz4`.

## What it does

| Capability | Notes |
|---|---|
| Open anything | Redump ISO (XGD1, plus Xbox 360 XGD2/XGD3/hybrid layouts), XISO, split `.1.iso/.2.iso`, CCI, CSO |
| Analyze | Title ID, name, region and version from `default.xbe`; used, unused and video-partition bytes; demo/`$SystemUpdate` detection; duplicate files |
| Plan | Reviewable list of removals from built-in rules, your rules database, heuristics, and caller globs. Toggle `PlannedRemoval.Remove` in the UI |
| **Rebuild** (default) | New minimal XISO laid out like extract-xiso. Supports removals, deduplication (Qwix-style cross-linking), and the XBE media patch |
| **Trim-only** | Copies the game partition with the original sector layout, drops the video partition and tail, and zero-fills Redump filler. Cannot remove files |
| Output | ISO, CCI (Cerbios), CSO (CISO v2, Project Stellar), optionally split below the FATX 4 GiB limit |
| Verify | Re-opens the output, walks the filesystem, parses `default.xbe`, and compares the SHA-1 of the whole logical image with what was written |
| Identity | CRC32/MD5/SHA-1 of the source, Redump DAT (Logiqx XML) matching, and a `<output>.shrink.json` sidecar that preserves the original identity |
| Extras | Pack a folder to an image, extract an image, convert between containers losslessly, deterministic output |

No demo list is built in. Only `$SystemUpdate` removal is enabled by default. Folders that look like demos are flagged for review. Real per-title removals come from your rules file (`rules/xbox-shrink-rules.json`), keyed by title ID and optionally version.

## Build and smoke test

```bash
dotnet build -c Release
dotnet run --project src/NoIntrovert.XboxIso.Cli -c Release -- selftest
dotnet test
```

`selftest` needs no game. It builds a synthetic disc containing a fake 387 MiB Redump video partition, demos, a dashboard update, and duplicate files. It then exercises rebuild, trim, every output format, splitting, deduplication, determinism and conversion, and compares every file byte-for-byte. It needs about 400 MB of free temp space.

## CLI test harness (`xboxiso`)

```bash
xboxiso info    game.iso
xboxiso analyze game.iso --dupes --rules rules/xbox-shrink-rules.json
xboxiso shrink  game.iso out/game.iso --dedupe --dry-run      # show the plan only
xboxiso shrink  game.iso out/game.cci --format cci --split --dedupe --hash
xboxiso shrink  game.iso out/game.iso --mode trim             # most conservative
xboxiso verify  out/game.cci
xboxiso convert out/game.iso out/game.cso --format cso
xboxiso extract game.iso extracted/
xboxiso hash    game.iso --dat "Microsoft - Xbox (Redump).dat"
```

Run `xboxiso help` for every option.

## Using it from NoIntrovert

Reference `src/NoIntrovert.XboxIso/NoIntrovert.XboxIso.csproj`, or copy the folder into your solution.

```csharp
using NoIntrovert.XboxIso;
using NoIntrovert.XboxIso.Analysis;
using NoIntrovert.XboxIso.IO;
using NoIntrovert.XboxIso.Shrinking;
using NoIntrovert.XboxIso.Xdvdfs;

// Create Progress<T> on the UI thread: it captures Avalonia's SynchronizationContext,
// so reports arrive on the UI thread. The library already throttles reports to ~10/s.
var progress = new Progress<OperationProgress>(p =>
{
    Stage = p.Stage.ToString();
    Percent = (p.Fraction ?? 0) * 100;
});

using var shrinker = await XboxIsoShrinker.OpenAsync(path, ct);
var analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions { FindDuplicates = true }, progress, ct);

var rules = ShrinkRuleSet.Load(rulesPath);                 // optional
var plan = shrinker.CreatePlan(analysis, new ShrinkOptions
{
    Format = OutputFormat.Cci,
    SplitSize = XdvdfsConstants.FatxSplitSize,
    Deduplicate = true,
    HashSource = true,                                     // record Redump identity in the sidecar
}, rules);

// Bind plan.Items to a DataGrid with a checkbox on PlannedRemoval.Remove.
// Show plan.Warnings, plan.RemovedBytes, plan.EstimatedOutputBytes.

var result = await shrinker.ExecuteAsync(plan, outputPath, progress, ct);
// result.SavedBytes, result.Verification, result.SidecarPath, result.OutputFiles
```

The one-shot form is `XboxIsoShrinker.ShrinkAsync(input, output, options, rules, progress, ct)`.

Things to know for the UI:
- Cancellation is honoured throughout. A cancelled or failed write deletes its partial output, and a failed verification deletes the output too.
- Existing outputs are never overwritten unless `Overwrite = true`.
- `DeleteSourceAfterVerify` only deletes the source after a successful verification. Offer it as an explicit choice, because the original is the only thing that still matches Redump.
- `PlannedRemoval.Remove` is a plain property. Wrap it in your view model if you need `INotifyPropertyChanged`.
- `XboxIsoShrinker` keeps the source open and is not thread-safe. Use one instance per job; separate jobs can run in parallel.

## Choosing options

| Goal | Settings |
|---|---|
| Safest shrink, identical files and sectors | `Mode = TrimOnly`, `MediaPatch = false` |
| Smallest playable ISO (xemu, Cerbios, HDD) | defaults + `Deduplicate = true` |
| Smallest file on Cerbios | above + `Format = Cci`, `SplitSize = FatxSplitSize` |
| Project Stellar | `Format = Cso`, `SplitSize = FatxSplitSize` |
| Reproducible output (verifiable by hash later) | `Timestamp = Deterministic` |

`MediaPatch` (default on) applies extract-xiso's XBE media-check patch. It makes the output match what extract-xiso and Repackinator users expect. Turn it off to keep executables byte-identical.

## Rules database

See `rules/xbox-shrink-rules.json`. A rule is applied automatically only when it is marked `"verified": true`. Unverified rules show up in the plan unselected unless `ApplyUnverifiedRules` is set. Build the database from titles you have actually tested: shrink the game, boot it, and open every menu that might link to the removed content.

## Not included

- **Attacher `default.xbe` generation** (Repackinator/Cerbios loaders for HDD folders). The known attachers are GPL binaries. Generate them with Repackinator or XBMC4Gamers, or add your own later.
- **Title images and artwork**, beyond what `XbeInfo` exposes.
- Xbox 360 images can be read and rebuilt, but the target is original Xbox and 360 output hasn't been tested on hardware.

## Release-candidate test checklist

1. `dotnet build` warning/error review, then `selftest` and `dotnet test`.
2. On 3–5 real Redump dumps:
   - `xboxiso info` and `analyze --dupes`: check the title, region and findings make sense.
   - Shrink with the defaults to `.iso`, then boot it in xemu.
   - Shrink with `--format cci --split --dedupe` and boot on Cerbios, if available.
   - Run `--mode trim`. The result should be byte-identical to `dd skip=387M` of the same dump up to the used length, except for the zeroed filler.
   - Cross-check one output with `extract-xiso -l` and with xdvdfs.
3. Interrupt a long write (Ctrl+C) and confirm no partial files remain.
4. Large titles (>4 GiB XISO): confirm split parts are each under 4 GiB and boot.

## Layout

```
src/NoIntrovert.XboxIso/          library
  IO/            ImageSource, ImageOpener, VirtualImage, ImageWriter, split handling
  Compression/   LZ4 block codec, CCI and CSO readers
  Xdvdfs/        filesystem reader, XISO builder, partition trimmer
  Xbe/           XBE header/certificate parser, media patch
  Analysis/      analyzer, rules database, glob matcher
  Identity/      CRC32, file hashes, Redump DAT matching
  Shrinking/     XboxIsoShrinker (entry point), options, plan, result, sidecar
src/NoIntrovert.XboxIso.Cli/      xboxiso test harness + selftest
tests/NoIntrovert.XboxIso.Tests/  xunit tests
rules/                            rules database template
```

Formats implemented from public specifications and from reading extract-xiso (BSD-style license), xdvdfs and ciso (MIT), and XboxToolkit (GPL-3.0; read for format details only). No code was copied. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

[MIT](LICENSE) © 2026 Nickel42
