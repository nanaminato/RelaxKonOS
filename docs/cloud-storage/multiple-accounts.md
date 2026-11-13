# Multiple cloud accounts

A user may add several Baidu Netdisk connections. Authorize each account separately and give each connection a recognizable name. For Baidu, select the intended Baidu account on the provider's verification page before approving each device code.

Authorization from **Add connection** creates a new connection even when the provider and display name are identical. Authorization from **Replace credentials** updates the explicitly selected connection by ID. The provider account is chosen on the verification page.

## Identity and isolation

- The immutable connection ID, not provider or display name, identifies directory, file, and upload requests.
- Connections and authorization sessions belong to the initiating RelaxKonOS user. Unknown and foreign connection IDs return not found; foreign connections cannot be deleted.
- Refresh and access tokens are isolated by connection ID and credential revision. Replacing credentials invalidates that connection's cache, not other accounts.
- Refresh-token rotation updates only the connection whose refresh request produced it. Persisted credentials remain encrypted; list responses never expose tokens.
- Token refresh is serialized within bounded connection stripes rather than a global provider lock. Different stripes can refresh concurrently; hash collisions can serialize unrelated accounts but cannot mix credentials.
- Deleting a connection removes local access. It does not revoke the provider grant or delete provider files. Remove provider access separately if revocation is intended.

## Automated verification

Run `dotnet run --project Tests/Server/RelaxKonOS.CloudMultiAccount.Tests` and `dotnet run --project Tests/Server/RelaxKonOS.BaiduAuthorizationSessions.Tests`.

These tests use mocked providers and synthetic credentials. They cover same-provider/same-name account creation, distinct device authorization sessions, owner isolation, token routing/caching/replacement, refresh rotation, and sibling preservation after deletion. Live provider authorization and UI account-switching still require device acceptance tests; no real accounts are needed by these suites.
