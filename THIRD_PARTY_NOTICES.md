# Bundled third-party components

Thor's Kist retains Brian Miller's MIT license in `LICENSE`.

The release ZIP additionally redistributes these unmodified runtime components:

| Component | Version | License | Copyright / source |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite.Core | 8.0.25 | MIT (`licenses/Microsoft.Data.Sqlite.txt`) | .NET Foundation and Contributors; https://github.com/dotnet/efcore |
| SQLitePCLRaw.core | 2.1.14 | Apache-2.0 (`licenses/Apache-2.0.txt`) | Copyright 2014-2024 SourceGear, LLC; https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.14 | Apache-2.0 | SourceGear, LLC; same source and license as above |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.14 | Apache-2.0 package; SQLite code is public domain | SourceGear, LLC; same source and license as above; https://sqlite.org/copyright.html |

SQLitePCLRaw's upstream notices, including its SQLite and Microsoft Open
Technologies notices, are preserved in `licenses/SQLitePCLRaw-NOTICE.txt`.
The Linux native SQLite library is taken from the pinned NuGet package, without
modification. Windows `e_sqlite3.dll` is built from the unmodified official SQLite
3.54.0 amalgamation with MSVC, optimization, a static C runtime and full matching
debug symbols in `e_sqlite3.pdb`. SQLite itself is public domain.
`plugins/sqlite-build.json` records the source URL, source archive checksum,
compiler options and matching DLL/PDB identifiers. Rebuild with
`scripts/build_sqlite_windows.py` and pass its output to
`scripts/prepare_hexium.py --sqlite-windows-build BUILD_DIRECTORY`.
`plugins/journal-runtime.json` records file hashes and architectures.

BepInEx, HarmonyX, Valheim, Unity and framework assemblies are supplied by the
game/mod loader and are not included in this ZIP.
