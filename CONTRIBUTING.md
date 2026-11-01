# Contributing

Issues and pull requests are welcome.

## Building and testing

```bash
dotnet build
dotnet test
dotnet run --project tools/xbox-iso-library.Cli -c Release -- selftest
```

You need the .NET 10 SDK. Install the .NET 8 runtime too, to run the net8.0 tests. To build the Android sample:

```bash
dotnet workload install android
dotnet build samples/AndroidSmoke -c Release
```

## Guidelines

- **No copyrighted files.** Never commit disc images, executables, firmware or other Microsoft material, not even in tests. Tests and the self-test build synthetic images.
- **Formats:** changes to an on-disc format should come with a test, and keep the self-test passing.
- **Rules:** additions to `rules/xbox-shrink-rules.json` should be marked `"verified": true` only after the shrunk game has been booted and its menus checked.
- **Warnings:** keep the build free of warnings, including the trim and AOT analyzers.

## Releasing

1. Update `CHANGELOG.md`, and `VersionPrefix` / `VersionSuffix` in `Directory.Build.props`.
2. Tag the commit and push the tag:
   ```bash
   git tag v1.0.0-rc.2
   git push origin v1.0.0-rc.2
   ```
3. The **Release** workflow tests and packs everything with that version, builds the standalone `xboxiso` binaries, and creates a GitHub release with all of them attached.
4. If the repository has a `NUGET_API_KEY` secret (an API key from nuget.org scoped to the `xbox-iso-library*` packages), the workflow also pushes the packages to nuget.org.
