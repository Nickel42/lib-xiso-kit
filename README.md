# xbox-iso-library

A dependency-free C# library for original Xbox disc images, usable from any .NET app. It opens Redump ISOs, XISOs, split ISOs, CCI and CSO images, and analyzes the title and wasted space. It then rebuilds or trims them to a smaller XISO and writes ISO, CCI or CSO, optionally split below the FATX 4 GiB limit, and verifies the result. No native code, no package dependencies.

[![CI](https://github.com/Nickel42/xbox-iso-library/actions/workflows/ci.yml/badge.svg)](https://github.com/Nickel42/xbox-iso-library/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/Nickel42/xbox-iso-library/blob/main/LICENSE)

| Package | What it is |
|---|---|
| **xbox-iso-library** | The library (namespace `XboxIsoLibrary`) |
| **xbox-iso-library.Cli** | The `xboxiso` command-line tool, as a `dotnet tool` or a standalone binary |

## Platforms

The library is plain managed code, so the same package works everywhere .NET runs. It targets `net8.0` and `net10.0` and is marked trim- and AOT-compatible.

| Platform | Status |
|---|---|
| Windows (x64, Arm64) | Tests and the full self-test run in CI on every change |
| Linux (x64, Arm64) | Tests and the full self-test run in CI on every change |
| macOS (Apple silicon, Intel) | Tests and the full self-test run in CI on every change |
| Android | An Android app using the library is built (trimmed) in CI on every change |
| iOS, Mac Catalyst, Native AOT | Expected to work (trim/AOT analyzers report no warnings); not tested in CI |

Any app on .NET 8 or later can use it, including `net8.0-android`, `net9.0-ios` and so on. .NET Framework and .NET Standard are not supported.

## Install

```bash
dotnet add package xbox-iso-library
```

The command-line tool:

```bash
dotnet tool install -g xbox-iso-library.Cli
```

Standalone `xboxiso` binaries for Windows, Linux and macOS (no .NET install needed) are attached to each [GitHub release](https://github.com/Nickel42/xbox-iso-library/releases). Until a release reaches nuget.org, reference `src/xbox-iso-library` directly or use the packages attached to a release.

## What it does

| Capability | Notes |
|---|---|
| Open anything | Redump ISO (XGD1, plus Xbox 360 XGD2/XGD3/hybrid layouts), XISO, split `.1.iso/.2.iso`, CCI, CSO |
| Analyze | Title ID, name, region and version from `default.xbe`; used, unused and video-partition bytes; demo/`$SystemUpdate` detection; duplicate files |
| Plan | Reviewable list of removals from built-in rules, your rules database, heuristics, and caller globs. Toggle `PlannedRemoval.Remove` before executing |
| **Rebuild** (default) | New minimal XISO laid out like extract-xiso. Supports removals, deduplication (Qwix-style cross-linking), and the XBE media patch |
| **Trim-only** | Copies the game partition with the original sector layout, drops the video partition and tail, and zero-fills Redump filler. Cannot remove files |
| Output | ISO, CCI (Cerbios), CSO (CISO v2, Project Stellar), optionally split below the FATX 4 GiB limit |
| Verify | Re-opens the output, walks the filesystem, parses `default.xbe`, and compares the SHA-1 of the whole logical image with what was written |
| Identity | CRC32/MD5/SHA-1 of the source, Redump DAT (Logiqx XML) matching, and a `<output>.shrink.json` sidecar that preserves the original identity |
| Extras | Pack a folder to an image, extract an image, convert between containers losslessly, deterministic output |

No demo list is built in. Only `$SystemUpdate` removal is enabled by default. Folders that look like demos are flagged for review. Real per-title removals come from your rules file (`rules/xbox-shrink-rules.json`), keyed by title ID and optionally version.

## Quick start

This is [`samples/QuickStart`](https://github.com/Nickel42/xbox-iso-library/tree/main/samples/QuickStart), which CI compiles on every change.

```csharp
using XboxIsoLibrary;
using XboxIsoLibrary.Analysis;
using XboxIsoLibrary.IO;
using XboxIsoLibrary.Shrinking;
using XboxIsoLibrary.Xdvdfs;

var progress = new Progress<OperationProgress>(p => Console.Write($"\r{p.Stage}: {(p.Fraction ?? 0):P0}"));

// Open any supported image and analyze it.
using var shrinker = await XboxIsoShrinker.OpenAsync(path, ct);
IsoAnalysis analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions { FindDuplicates = true }, progress, ct);
Console.WriteLine($"{analysis.DefaultXbe?.TitleName} [{analysis.DefaultXbe?.TitleIdHex}] {analysis.DefaultXbe?.RegionLabel}");

// Plan: a reviewable list of removals (rules are optional).
var rules = ShrinkRuleSet.Load(rulesPath);
ShrinkPlan plan = shrinker.CreatePlan(analysis, new ShrinkOptions
{
    Format = OutputFormat.Cci,
    SplitSize = XdvdfsConstants.FatxSplitSize, // parts below the FATX 4 GiB limit
    Deduplicate = true,
    HashSource = true,                         // record the source's identity in the sidecar
}, rules);

// Execute: writes, then re-opens and verifies the output.
ShrinkResult result = await shrinker.ExecuteAsync(plan, outputPath, progress, ct);
// result.OutputFiles, result.OutputBytes, result.Verification, result.SidecarPath
```

The one-shot form is `XboxIsoShrinker.ShrinkAsync(input, output, options, rules, progress, ct)`.

## Using it in an app

- **Threads:** the async methods run on the thread pool. In WPF, WinForms, Avalonia or MAUI, await them from the UI thread and create `Progress<T>` there, so progress reports arrive on the UI thread. The library throttles reports to about 10 per second.
- **Showing the plan:** bind `plan.Items` to a list with a checkbox on `PlannedRemoval.Remove`, and show `plan.Warnings`, `plan.RemovedBytes` and `plan.EstimatedOutputBytes`. `Remove` is a plain property; wrap it in your view model if you need `INotifyPropertyChanged`.
- **Safe output:** cancellation is honoured throughout. A cancelled or failed write deletes its partial output, and a failed verification deletes the output too. Existing outputs are never overwritten unless `Overwrite = true`.
- **Deleting the source:** `DeleteSourceAfterVerify` only deletes the source after a successful verification. Offer it as an explicit choice, because the original is the only file that still matches Redump.
- **Concurrency:** `XboxIsoShrinker` keeps the source open and is not thread-safe. Use one instance per job; separate jobs can run in parallel.
- **Android and iOS:** the APIs take file paths. Files picked through the Storage Access Framework (Android) or document picker (iOS) arrive as streams or content URIs; copy them into your app's storage first. Disc images are large, so check the free space before you copy.

## Choosing options

| Goal | Settings |
|---|---|
| Safest shrink, identical files and sectors | `Mode = TrimOnly`, `MediaPatch = false` |
| Smallest playable ISO (xemu, Cerbios, HDD) | defaults + `Deduplicate = true` |
| Smallest file on Cerbios | above + `Format = Cci`, `SplitSize = FatxSplitSize` |
| Project Stellar | `Format = Cso`, `SplitSize = FatxSplitSize` |
| Reproducible output (verifiable by hash later) | `Timestamp = Deterministic` |

`MediaPatch` (default on) applies extract-xiso's XBE media-check patch. It makes the output match what extract-xiso and Repackinator users expect. Turn it off to keep executables byte-identical.

Rebuilt images carry the optimized-image tag `in!xiso!xbox-iso-library`, the way extract-xiso writes its own name there. Set `XisoBuildOptions.TagVersion` (16 characters) to write something else.

## Rules database

See [`rules/xbox-shrink-rules.json`](https://github.com/Nickel42/xbox-iso-library/blob/main/rules/xbox-shrink-rules.json). A rule is applied automatically only when it is marked `"verified": true`. Unverified rules show up in the plan unselected unless `ApplyUnverifiedRules` is set. Build the database from titles you have actually tested: shrink the game, boot it, and open every menu that might link to the removed content.

## Command line (`xboxiso`)

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
xboxiso selftest                                              # no game needed
```

Run `xboxiso help` for every option. `selftest` builds a synthetic disc with a fake 387 MiB Redump video partition, demos, a dashboard update and duplicate files. It then exercises rebuild, trim, every output format, splitting, deduplication, determinism and conversion, comparing every file byte for byte. It needs about 400 MB of free temp space.

## Not included

- **Attacher `default.xbe` generation** (Repackinator/Cerbios loaders for HDD folders). The known attachers are GPL binaries. Generate them with Repackinator or XBMC4Gamers.
- **Title images and artwork**, beyond what `XbeInfo` exposes.
- Xbox 360 images can be read and rebuilt, but the target is original Xbox and 360 output hasn't been tested on hardware.

## Building from source

Needs the .NET 10 SDK (the tests also run on .NET 8 if it is installed).

```bash
dotnet build
dotnet test
dotnet run --project tools/xbox-iso-library.Cli -c Release -- selftest
dotnet pack -c Release -o artifacts
```

See [docs/TESTING.md](https://github.com/Nickel42/xbox-iso-library/blob/main/docs/TESTING.md) for testing with real dumps, and [CONTRIBUTING.md](https://github.com/Nickel42/xbox-iso-library/blob/main/CONTRIBUTING.md) for how releases work.

```
src/xbox-iso-library/            the library
  IO/            ImageSource, ImageOpener, VirtualImage, ImageWriter, split handling
  Compression/   LZ4 block codec, CCI and CSO readers
  Xdvdfs/        filesystem reader, XISO builder, partition trimmer
  Xbe/           XBE header/certificate parser, media patch
  Analysis/      analyzer, rules database, glob matcher
  Identity/      CRC32, file hashes, Redump DAT matching
  Shrinking/     XboxIsoShrinker (entry point), options, plan, result, sidecar
tools/xbox-iso-library.Cli/      xboxiso command-line tool + selftest
tests/xbox-iso-library.Tests/    xunit tests
samples/                         QuickStart and an Android smoke test
rules/                           rules database template
```

## Legal

This library is for working with disc images you dumped from games you own. It contains no Microsoft keys, firmware, executables or other copyrighted Microsoft material; test images are synthetic. Formats were implemented from public specifications and from reading extract-xiso (BSD-style license), xdvdfs and ciso (MIT), and XboxToolkit (GPL-3.0; read for format details only). No code was copied; see [THIRD_PARTY_NOTICES.md](https://github.com/Nickel42/xbox-iso-library/blob/main/THIRD_PARTY_NOTICES.md). "Xbox" is a trademark of Microsoft; this project is not affiliated with or endorsed by Microsoft.

## License

[MIT](https://github.com/Nickel42/xbox-iso-library/blob/main/LICENSE) © 2026 Nickel42
