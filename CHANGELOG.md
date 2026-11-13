# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## Unreleased

### Added
- **Xbox 360 packages:** `LibXisoKit.Stfs`. `StfsHeader` reads CON, LIVE and PIRS headers (content type, title ID, media ID, version, names, STFS or SVOD). `StfsPackage` lists, reads and extracts the files of an STFS package (XBLA games, DLC, title updates), with one or two copies of each hash table and up to three table levels, and `Verify` checks the whole hash tree. Games on Demand (SVOD) packages open for their header only.

### Changed (breaking)
- **Names:** the repository is now `Nickel42/lib-xiso-kit` (was `xbox-iso-library`). The packages are renamed to `lib-xiso-kit` and `lib-xiso-kit.Cli`, the library assembly to `lib-xiso-kit`, and release archives to `lib-xiso-kit-cli-*`. The namespace `XboxIsoLibrary` is now `LibXisoKit` (with `LibXisoKit.Cli` and `LibXisoKit.Tests`), the entry-point class `XboxIsoShrinker` is now `XisoShrinker`, and the command-line tool `xboxiso` is now `xiso-kit` (`dotnet tool` command and `xiso-kit` binaries).
- **Optimized-image tag:** rebuilt XISOs carry `in!xiso!lib-xiso-kit` (space-padded to the 16-character field), so those header bytes differ from rc.2.
- **Sidecar:** `.shrink.json` sidecars name `lib-xiso-kit` as the tool.

## 1.0.0-rc.2

The library is now a standalone package for any .NET app.

### Changed (breaking)
- **Names:** package and assembly `xbox-iso-library`, namespace `XboxIsoLibrary`. The command-line tool keeps the `xboxiso` command and is packaged as `xbox-iso-library.Cli`.
- **Targets:** `net8.0` and `net10.0` (previously `net8.0` and `net9.0`; .NET 9 support ended in May 2026).
- **Optimized-image tag:** rebuilt XISOs carry `in!xiso!xbox-iso-library`; set `XisoBuildOptions.TagVersion` to change it. This changes 16 bytes of the header compared with rc.1; everything else in the output is unchanged.
- **Sidecar:** `.shrink.json` sidecars name `xbox-iso-library` as the tool.

### Added
- **Packaging:** NuGet packages with source link and symbol packages; the CLI as a `dotnet tool`.
- **Releases:** standalone `xboxiso` binaries for Windows, Linux and macOS (x64 and Arm64) with each release.
- **CI:** tests on .NET 8 and .NET 10 plus the full self-test on Windows, Linux and macOS, and a trimmed Android app build.
- **Samples:** `QuickStart` (the README's example) and `AndroidSmoke`.

### Performance
- Reading CCI/CSO is about 7× faster (sectors fetched in runs and decoded in parallel).
- Compressed writes overlap reading, compressing and writing.
- Dedupe hashes only files that share a size with another file.
- Source hashing runs CRC32, MD5 and SHA-1 in parallel, with a faster CRC32.
- Output is byte-identical to before these changes.

## 1.0.0-rc.1

First release candidate.
