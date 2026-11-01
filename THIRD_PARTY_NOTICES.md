# Third-party notices

This code is original. No third-party source code is included, and the library has no package dependencies.

Format behaviour follows public specifications and was worked out by reading these open-source projects:

- **extract-xiso** (BSD-style license): the XISO directory-table layout that rebuilt images follow, and the XBE media-check patch. `MediaPatch` searches for and changes the same byte sequence extract-xiso does, so patched executables match extract-xiso and Repackinator output.
- **xdvdfs** (MIT License): the XDVDFS filesystem format.
- **ciso** (MIT License): the CSO (CISO v2) container format.
- **XboxToolkit** (GPL-3.0): read for format details only. No code was copied or adapted.

During development the on-disc format logic was validated with a reference prototype, whose output was cross-checked against extract-xiso 2.7.1 built from source and against the system `liblz4`.

No Microsoft keys, firmware, executables or other copyrighted Microsoft material are included. Test images are synthetic.
