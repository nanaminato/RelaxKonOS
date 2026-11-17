# PostgreSQL bootstrap checks

Run the focused checks with:

```powershell
dotnet run --project Tests/PostgreSql/RelaxKonOS.PostgreSql.Tests.csproj
```

The suite uses the public Ubuntu 24.04 amd64 APT outputs captured on 2026-11-16. It verifies the exact artifact/dependency freeze, source and platform refusals, unknown inventory, external installation protection and initialization inputs. Fixture contents are historical evidence, not a production version catalog or trust store. Signature/download evidence and platform limits are recorded in [DB-01](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB01.md).

The native smoke test requires a **disposable** Ubuntu 24.04 container labelled `relaxkonos.task=db01`, with no mounts or published ports, and PostgreSQL 16 packages already installed. Never label an existing application container for this purpose. It creates a fixed private test instance, exercises the generated initialization arguments, performs authenticated native checks and stops the instance. The stopped data stays in the disposable container until that container is removed. Re-running against the same data is deliberately refused.

```powershell
dotnet Tests/PostgreSql/bin/Debug/net10.0/RelaxKonOS.PostgreSql.Tests.dll initialization-json > .codex-scratch/db02-init.json
python Tests/PostgreSql/native-smoke.py <disposable-container> .codex-scratch/db02-init.json
```

Credentials are generated per run, passed through stdin, kept in a mode-0600 file inside the disposable container, and never echoed. The fixture account setup is not a DB-03 implementation. Container validation does not exercise systemd, Helper authorization, the component installation ledger, interrupted installation, product credential storage or desktop UI. DB-02 adds separate product-path checks below; this older smoke does not supply their evidence.

## DB-02 product-path checks

The product implementation and evidence are recorded in [DB-02](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB02.md). The older `native-smoke.py` above tests initialization arguments only. The new `native-helper-smoke.py` executes the actual Helper dispatcher and durable native transaction in separate processes with real systemd and PostgreSQL.

Focused suites (also registered in `Tools/tests.json`):

```powershell
dotnet run --project Tests/Server/RelaxKonOS.Server.Tests -- --postgresql-only
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -- --database-only
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -- --component-installs-only
```

The first suite covers installation validation, original receipts, response loss, controller/elevation mapping and the shared coordinator/store. The desktop suite covers states, cancellation, permission/session changes, exact-body retries and three-language rendering. The component suite verifies frozen request submission through the desktop HTTP installer. These are focused tests, not a claim of a complete remote GUI deployment.

For the real native test, create a **new disposable fixture** with no bind mounts or published ports. systemd needs an isolated privileged cgroup environment. Never point this script at a production host: it installs packages and creates a test database table, and its final scenario deliberately injects an uncertain checkpoint into the fixture ledger.

```powershell
dotnet publish Tests/PostgreSql/RelaxKonOS.PostgreSql.Tests.csproj -r linux-x64 --self-contained true -o .codex-scratch/db02-linux
docker build -f Tests/PostgreSql/systemd.Dockerfile -t relaxkonos-db02-systemd:test Tests/PostgreSql
docker run --name relaxkonos-db02-test --label relaxkonos.task=db02 -d --privileged --cgroupns=private --tmpfs /run --tmpfs /run/lock relaxkonos-db02-systemd:test
docker cp .codex-scratch/db02-linux relaxkonos-db02-test:/opt/acceptance
docker cp Tests/PostgreSql/native-helper-smoke.py relaxkonos-db02-test:/opt/native-helper-smoke.py
docker exec relaxkonos-db02-test sh -c 'printf DB-02 > /etc/relaxkonos-db02-disposable; chmod +x /opt/acceptance/RelaxKonOS.PostgreSql.Tests'
docker exec relaxkonos-db02-test python3 /opt/native-helper-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests
```

Expected checks include signed frozen installation, default-cluster suppression/cleanup, idempotent installation, initialized native authentication, start/stop, restart data persistence, same-key conflict, revision rejection, safe-checkpoint recovery, configuration drift, and refusal to replay an uncertain dispatch. `--existing` skips installation/initialization only and exercises a previously initialized disposable fixture. Each Helper invocation is a fresh process; checkpoint injection models uncertainty but is not an actual power-loss test.

After collecting evidence, remove only the named test container. No test credentials are used. The application credential-management workflow remains DB-03.

## DB-03 accounts and normal password rotation

See [DB-03 implementation and acceptance](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB03.md). After initializing a **fresh fixture using the current build** with the DB-02 product-path commands above, run:

```powershell
docker cp Tests/PostgreSql/native-accounts-smoke.py relaxkonos-db02-test:/opt/native-accounts-smoke.py
docker exec relaxkonos-db02-test python3 /opt/native-accounts-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests
```

The script requires the same disposable marker and executes the real Helper through stdin. It creates uniquely named owned databases/accounts, validates existing and future privileges and cross-database denial, rotates a password while retaining an existing session, and injects a lost-response checkpoint after a real committed SQL transaction. Expected: 44 checks. It is not a physical power-loss test. Never run it against an existing application host. The server `--postgresql-only` and desktop `--database-only` suites now include DB-03 credential/authorization and account workspace checks respectively. The old DB-02 development ledger is intentionally not migrated; use a fresh disposable fixture. Remove only the verified named fixture after collecting results.

## DB-04 administrator identity recovery

See [DB-04 scope and evidence](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB04.md). Use the same fresh disposable systemd fixture, current binary and DB-02 initialization. The script intentionally breaks the fixture administrator and terminates a real Helper process; never use an existing host.

```powershell
docker cp Tests/PostgreSql/native-admin-recovery-smoke.py relaxkonos-db02-test:/opt/native-admin-recovery-smoke.py
docker exec relaxkonos-db02-test python3 /opt/native-admin-recovery-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests --normal
docker exec relaxkonos-db02-test python3 /opt/native-admin-recovery-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests --prepare-restart
docker restart relaxkonos-db02-test
docker exec relaxkonos-db02-test python3 /opt/native-admin-recovery-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests --after-restart
```

Normal/failure checks: 17, or 24 when DB-03 accounts already exist; add one if an initial explicit start is needed. Running the DB-03 accounts smoke first additionally exercises cleanup after failed account verification. Restart checks: 11. The interruption phase checks the durable mask and closed IPv4/IPv6 listeners, and can also perform an initial explicit start. `--normal` leaves the instance running only to support following test phases; the recovery operation itself always leaves it stopped. Mask persistence and restart are real; this is not physical power-loss acceptance. The server and desktop focused suites include administrator recovery authorization and UI checks.

## DB-05 scoped remote TLS

[DB-05](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB05.md) owns the implementation boundaries and evidence. Create a fresh systemd fixture named `relaxkonos-db05` with label `relaxkonos.task=db05`, no bind mounts or public ports, current Helper binaries, and the same DB-02 initialization marker and smoke. Install `nftables openssl iproute2` **inside the fixture only**. Create two fresh client containers from `postgres:18-bookworm` with entrypoint `sleep infinity`, names `relaxkonos-db05-allowed` / `relaxkonos-db05-denied`, label `relaxkonos.task=db05-client`, no binds or port mappings. The client image creates an anonymous volume; remove it with these disposable containers.

```powershell
python Tests/PostgreSql/native-remote-smoke.py relaxkonos-db05 relaxkonos-db05-allowed relaxkonos-db05-denied --fault
```

For IPv6, create a dedicated labelled bridge `rk-db05-net` with IPv4 subnet `172.30.5.0/24` and IPv6 subnet `fd00:db05::/64`, attach only these fixtures, and run the same script with `--ipv6`. Do not reuse an existing network with other workloads. Source addresses are read from Docker inventory, not hardcoded into product code. The smoke creates owned test databases/accounts and temporary self-signed TLS material; passwords and keys stay off argv and test output. It verifies actual TLS login from one container, rejection from another, plaintext/password/name/database failures, firewall counters, guard reload, unrelated rule preservation, and session termination on close. `--fault` introduces a real private-key permission failure and resumes the original native transaction. It ends with PostgreSQL stopped. Remove only the labelled fixtures (including their anonymous volumes) and dedicated network after verification.

For a normal external host, use `Tools/postgresql-remote-probe.py` with a JSON request through stdin. See its module documentation and DB-05 for request fields and trust requirements. Server success alone is not external reachability evidence.

## DB-06 patch maintenance, backups and removal

See [DB-06 scope and evidence](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB06.md). Use a fresh labelled Ubuntu 24.04 systemd fixture with no bind mounts or published ports, the current published test executor at `/opt/acceptance`, the DB-02 marker, and the completed DB-02 native baseline. Copy `native-maintenance-smoke.py` and `native-accounts-smoke.py` to `/opt`.

```powershell
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --old-fixture
docker exec relaxkonos-db06 python3 /opt/native-accounts-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --update
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --prepare-restart
docker restart relaxkonos-db06
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --after-restart
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --credential-gate --lifecycle
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --failed-backup
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --path-gates --lifecycle
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --prepare-restore-restart
docker restart relaxkonos-db06
docker exec relaxkonos-db06 python3 /opt/native-maintenance-smoke.py --after-restart
```

`--old-fixture` is deliberately destructive **fixture preparation**: it removes the baseline disposable cluster, installs signed Ubuntu 16.2 packages, resets only the disposable ownership record and invokes real product initialization under 16.2. It is not a production downgrade or import API. `--update` then invokes the actual frozen 16.15 maintenance plan. `--lifecycle` corrupts the disposable executable and ultimately deletes the owned data, backups and configuration before testing explicit new initialization. Never run these scripts against a real application host. The failure phase repairs only its deliberately broken administrator offline before resuming the original transaction. Remove only the labelled fixture after collecting evidence. No physical power-loss claim is made.


## DB-07A automatic offline recovery points and retention

See [DB-07A scope and evidence](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB07.md). Create a fresh disposable fixture named `relaxkonos-db07`, labelled `relaxkonos.task=db07`, using the DB-02 systemd image, current published executor, no bind mounts and no published ports. Set the same DB-02 disposable marker and complete the native DB-02 baseline. Copy `native-backup-automation-smoke.py` and `native-accounts-smoke.py` into `/opt`. These tests stop the fixture database, inject faults, restore data and ultimately uninstall/delete the fixture instance; never use a real application host.

```powershell
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --normal
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --prepare-restart
docker restart relaxkonos-db07
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --after-restart
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --prepare-cleanup-restart
docker restart relaxkonos-db07
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --after-cleanup-restart
docker exec relaxkonos-db07 python3 /opt/native-accounts-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests
docker exec relaxkonos-db07 /opt/acceptance/RelaxKonOS.PostgreSql.Tests scheduler-native
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --restore-automatic
docker exec relaxkonos-db07 python3 /opt/native-backup-automation-smoke.py --lifecycle
```

Wait until systemd is ready after each restart before continuing. Fault context lives in the fixture's persistent `/var/lib/relaxkonos` tree, not `/tmp`. The scheduler harness runs the actual Core background worker/coordinator/service with an in-process transport to the actual Helper; it does not exercise HTTP/PAM or a full deployed server. Run it **after** the accounts fixture so the latest automatic copy contains the current identity/grant state required by restore gates. Tests verify running-instance opt-in, retention/manual protection, same-slot receipts, persistent isolation/deletion responsibility, restored service/data and policy revocation. Inspect the task label, mounts and port mappings before removing only this disposable container. No physical power-loss claim is made.


## DB-07B explicit major migration

See [DB-07B scope and evidence](../../docs/applications/RelaxKonOS.DatabaseManager.PostgreSql.DB07B.md). Publish the current test executor and create a **fresh disposable** Ubuntu 24.04 amd64 systemd container with task label `relaxkonos.task=db07b`, no bind mounts or public ports. Use the DB-02 marker and native baseline above. Install `ca-certificates` from the signed Ubuntu repository **inside the fixture** as an HTTPS prerequisite; the product does not install it through a read-only plan. Run the DB-03 accounts smoke first to include real managed roles and permissions. Copy `native-major-migration-smoke.py` to `/opt`.

Normal path on one fresh fixture:

```powershell
docker exec relaxkonos-db07b python3 /opt/native-major-migration-smoke.py --normal
docker exec relaxkonos-db07b python3 /opt/native-major-migration-smoke.py --receipt-window
docker exec relaxkonos-db07b python3 /opt/native-major-migration-smoke.py --ca-precondition
docker exec relaxkonos-db07b python3 /opt/native-major-migration-smoke.py --post-migration
```

Real interruption on a **separate fresh source fixture**, never on the already migrated instance:

```powershell
docker exec relaxkonos-db07b-interrupt python3 /opt/native-major-migration-smoke.py --prepare-restart
docker restart relaxkonos-db07b-interrupt
docker exec relaxkonos-db07b-interrupt python3 /opt/native-major-migration-smoke.py --after-restart
```

Wait for systemd readiness after restart. The script terminates the actual Helper at `MajorUpgraded`, after native copy completion and before activation; both ordinary services must remain persistently masked with closed IPv4/IPv6 TCP. Context is stored under `/var/lib/relaxkonos`. `--receipt-window` deliberately injects a verified checkpoint before receipt commit and must not be reported as a physical crash. `--post-migration` ends by uninstalling and deleting the owned fixture data, configuration, backups and schedule. `--ca-precondition` temporarily moves and restores only the fixture's CA bundle. Native initialization/copy interrupted before a completion checkpoint remains isolated and is not automatically replayed. Inspect task labels, mounts and port bindings before removing only the named disposable fixtures.


## DB-07C two managed instances

Use a fresh Ubuntu 24.04 amd64 systemd fixture named `relaxkonos-db07c` with label `relaxkonos.task=db07c`, no host mounts or published ports. Publish the current test executor and run the DB-02 native baseline above. Set `/etc/relaxkonos-db07c-disposable` to `DB-07C` and copy `native-multi-instance-smoke.py` to `/opt`. The script creates a second instance and exercises account creation and physical restore through the actual Helper.

```powershell
docker exec relaxkonos-db07c python3 /opt/native-multi-instance-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests
docker restart relaxkonos-db07c
docker exec relaxkonos-db07c python3 /opt/native-multi-instance-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests --after-restart
```

Wait for systemd to become ready before the last command. Verify the fixture label, mounts and port bindings before removing the container.


Run the independent secondary deletion scenario after the two-instance smoke, while the fixture still contains both clusters. It uses the real installation queue and an in-process Helper transport to verify encrypted credential cleanup, probes the primary throughout deletion and performs a real shared package repair afterward.

```powershell
docker exec relaxkonos-db07c python3 /opt/native-multi-instance-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests --delete-second
```
