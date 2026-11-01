# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/).

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
