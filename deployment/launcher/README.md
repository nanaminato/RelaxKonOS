# Deployment launcher sources

The canonical sources are the ordered fragments in `src/windows/` and `src/linux/`.
`src/windows.txt` and `src/linux.txt` define their concatenation order. The fragments
share one script scope; they are build inputs, not independently executable modules.
The Linux request fragment includes its Python heredoc intact.

| Fragment | Responsibility |
| --- | --- |
| `preamble` | Invocation options, encoding, staging paths and initial state |
| `journal` | JSON Lines events, operation records and diagnostics |
| `json-reader` (Windows) | Strict JSON parsing |
| `request` | Request schema, options and package validation helpers |
| `host-facts` | Installation state, platform facts and health checks |
| `locking` | Write lock and idempotency |
| `packages` (Windows) | Package download, transfer progress and validation |
| `engine` | Fixed deployment engine invocation |
| `preflight` | Installation identity and prerequisites |
| `actions` | Probe, status, install, upgrade, repair, rollback and uninstall |
| `entry` | Dispatch and final failure boundary |

Desktop MSBuild and Android Gradle compose the fragments into their build directories.
Release packaging generates the same two standalone scripts into its launcher output.
No source fragment or build tool is uploaded to a target host. Generated scripts use
UTF-8 and LF line endings on every platform. The Windows artifact retains its BOM
so Windows PowerShell 5.1 correctly reads non-ASCII messages; the Linux artifact
has no BOM so its shebang remains valid.

The two top-level scripts are checked-in generated artifacts for manual use and
existing deployment tests. Never edit them directly. After editing fragments, run:

```powershell
./deployment/launcher/Build-Launchers.ps1
./deployment/launcher/Build-Launchers.ps1 -Check
```

Or on a Python host:

```sh
python3 deployment/launcher/build_launchers.py
python3 deployment/launcher/build_launchers.py --check
```

Both commands accept an output directory (`-OutputDirectory` / `--output`).
Run `Tests/Deployment/LauncherCompositionChecks.ps1` to check generated freshness,
PowerShell syntax, generator parity and embedded desktop resource generation.
Existing deployment regression checks continue to execute the standalone artifacts.
