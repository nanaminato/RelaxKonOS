# RelaxKonOS Developer CLI

`relaxkonos-dev` publishes a RelaxKonOS application project and creates its `.roapp` package without a project-specific shell script.

```bash
relaxkonos-dev pack ./MyApp --configuration Release
relaxkonos-dev pack ./MyApp --configuration Debug --no-build
relaxkonos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
relaxkonos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` writes `artifacts/<entry-assembly>.roapp` by default. It requires a `manifest.json` beside the `.csproj`; use `--manifest` and `--output` to override those paths. By default it recompiles the selected `Debug` or `Release` configuration with `dotnet publish`; pass `--no-build` to use `dotnet publish --no-build` and package that configuration's existing build output. It packages the complete publish output beneath the target framework directory declared by `manifest.json`'s `entryAssembly`, including private dependencies and native runtime assets. When the manifest declares `iconPath`, the CLI also safely copies that relative icon asset.

Set `RELAXKONOS_DEV_TOKEN` (or pass `--token`) for commands that contact a running RelaxKonOS Shell: `--install`, `watch`, `apps`, `install`, `update`, `launch`, and `uninstall`. Use `export RELAXKONOS_DEV_TOKEN="<pairing-token>"` in a POSIX shell or `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"` in Windows PowerShell. Use `watch --no-install` to build packages without a Shell.

Run `relaxkonos-dev` with no arguments to see the complete command reference. The RelaxKonOS repository's Developer Mode guide describes the package format and compatibility contract.

## Remote settings service

`settings` calls the remote Server independently of the Settings window and local Developer Bridge. Supply an explicit HTTPS origin with `--server`. The host JWT comes from `RELAXKONOS_HOST_ACCESS_TOKEN`; a developer pairing token grants no host access. Keep tokens out of scripts and source control.

```sh
relaxkonos-dev settings --server https://remote.example:5001 catalog
relaxkonos-dev settings --server https://remote.example:5001 time
relaxkonos-dev settings --server https://remote.example:5001 preview-time --revision <snapshot-revision> --idempotency-key <unique-key> --zone <remote-zone-id>
relaxkonos-dev settings --server https://remote.example:5001 apply-time --id <reviewed-plan-id>
relaxkonos-dev settings --server https://remote.example:5001 operation --id <plan-id>
relaxkonos-dev settings --server https://remote.example:5001 rollback --id <plan-id> --revision <observed-revision>
```

Use a time zone ID enumerated by the remote host. Review the plan's differences, target, impact and expiry before applying its ID. The same host JWT needs an existing short-lived `HostTimeChange` grant for `host/time`; missing authorization returns the Server's structured 428 response without opening a password dialog. Output is Server JSON; HTTP failures exit 1. Transport failures never replay writes: query the original plan ID before deciding on another write. Rollback checks authorization and the observed revision again. Hostname and DNS commands are still pending.


## Environment variables

Select the remote scope `hostUser` or `hostMachine`. Discover the server-bound authorization target with `environment-target`. The same JWT needs an existing `HostEnvironmentRead` grant for that target, including preview and rollback readback. Apply/rollback also require `HostEnvironmentChange`; `--reveal` additionally requires `HostEnvironmentReveal`. The CLI never requests or expands grants automatically. Reads return masked values by default; explicit reveal writes raw values to standard output.

```sh
relaxkonos-dev settings --server https://remote.example:5001 environment-target --scope hostUser
relaxkonos-dev settings --server https://remote.example:5001 environment --scope hostUser
relaxkonos-dev settings --server https://remote.example:5001 environment --scope hostUser --reveal
relaxkonos-dev settings --server https://remote.example:5001 preview-environment --scope hostUser --revision <snapshot-revision> --idempotency-key <unique-key> --changes changes.json
relaxkonos-dev settings --server https://remote.example:5001 apply-environment --id <reviewed-plan-id>
relaxkonos-dev settings --server https://remote.example:5001 operation --id <plan-id>
relaxkonos-dev settings --server https://remote.example:5001 rollback --id <plan-id> --revision <observed-revision>
```

`changes.json` uses the shared `EnvironmentChangeSet` contract; `--changes -` reads UTF-8 JSON from standard input. Input is bounded to 2 MiB; the Server also enforces 128 changes, 256 KiB of data, and remote platform semantics. Keep values out of command-line arguments.

```json
{
  "changes": [
    { "name": "JAVA_HOME", "operation": "Set", "value": "/opt/jdk", "valueKind": "String" },
    { "name": "EMPTY", "operation": "Set", "value": "" },
    { "name": "OLD_VALUE", "operation": "Delete" }
  ],
  "confirmHighImpact": false
}
```

An empty string is a stored value; deletion requires `Delete` without a value. Review high-impact PATH/runtime changes before explicitly setting `confirmHighImpact: true`. Plans remain redacted: review the local change file together with the plan target, impact and expiry before applying its ID. Editing the file does not change an existing plan. CLI commands are implemented; the Linux provider and real cross-platform write verification remain pending.
