# Testing

## 1. Unit tests and the self-test

```bash
dotnet build
dotnet test
dotnet run --project tools/lib-xiso-kit.Cli -c Release -- selftest
```

The tests run against both library targets (net8.0 and net10.0). CI runs them and the self-test on Windows, Linux and macOS, and builds the Android sample.

`selftest` needs no game: it builds a synthetic disc and checks rebuild, trim, every output format, splitting, deduplication, determinism and conversion byte for byte.

## 2. Real-dump checklist

1. On 3–5 real Redump dumps:
   - `xiso-kit info` and `analyze --dupes`: check the title, region and findings make sense.
   - Shrink with the defaults to `.iso`, then boot it in xemu.
   - Shrink with `--format cci --split --dedupe` and boot on Cerbios, if available.
   - Run `--mode trim`. The result should be byte-identical to `dd skip=387M` of the same dump up to the used length, except for the zeroed filler.
   - Cross-check one output with `extract-xiso -l` and with xdvdfs.
2. Interrupt a long write (Ctrl+C) and confirm no partial files remain.
3. Large titles (>4 GiB XISO): confirm split parts are each under 4 GiB and boot.

### What has been checked on real dumps

On Ninja Gaiden Black (an existing XISO) and three Redump dumps (Jade Empire, Marvel vs. Capcom 2, Panzer Dragoon Orta):

- `info` and `analyze` identify each title correctly, including the 387 MiB video partition on the Redump dumps.
- Rebuild to ISO, split CCI and split CSO all verify, and every file inside matches the source by SHA-1.
- Trim output equals the source from 387 MiB on, except for zeroed filler.
- Split parts stay below the FATX limit.
- CCI/CSO → ISO conversion is lossless.
- Cancelling mid-write or mid-verify leaves no files.
- The Linux build of `xiso-kit` gives the same output as Windows.

Not yet checked: booting in xemu or on Cerbios/Project Stellar, matching against a Redump DAT, and a cross-check with `extract-xiso -l` or xdvdfs.
