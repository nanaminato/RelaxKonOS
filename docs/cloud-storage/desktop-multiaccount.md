# Desktop cloud accounts

The built-in Explorer cloud entry and sidebar expose every connection owned by
the signed-in RelaxKonOS user. Multiple Baidu Netdisk accounts
can remain connected at the same time. No provider-wide “active account” is used
for file requests: listing, pagination, download, upload and folder creation all
carry the selected connection ID.

## Adding and selecting accounts

Use **Add connection**, choose the provider and give each account a recognizable
name, then authorize in the client computer's browser. Choose the intended
provider account there. **Add connection** creates a separate connection.
**Replace credentials** opens a non-modal configuration window for the selected
connection. Saving new credentials or completing browser authorization from
that window updates the existing connection ID. Choose the intended provider
account when authorizing again, especially before retrying a file transfer.

The account selector displays name, provider and connection ID, so duplicate
names remain distinguishable. Sidebar entries display provider and expose the
ID in their tooltip. Switching in the selector restores that connection's last
successfully listed folder; selecting its sidebar root explicitly opens root.
The previous account's entries and pagination cursor are cleared before loading
the next account. Navigation snapshots are keyed by connection ID, not name or
provider, removed when a connection disappears, and cleared on disconnect.

Directory requests show a loading message and can be cancelled. Switching
accounts or returning to host files cancels the previous listing. A listing
times out after 30 seconds. Failed navigation keeps the last loaded entries
for the same account, if available.

Explorer caches the first page of each folder in memory for five minutes,
separately by connection ID and folder reference. The refresh button reloads
the current folder from the provider. Successful uploads and folder creation
invalidate the affected folder. Credential changes and account removal clear
the corresponding cache, and disconnect clears all cached listings.

Right-click a folder to **Open** it or a file to **Download** it. The
**Upload** and **Download** buttons use the same server file picker as before.

Uploads and downloads run in the operation center. If the provider rejects the
connection credentials, Explorer opens the connection's configuration window
and waits for a retry decision on the transfer. Ordinary permissions and
network failures report their own errors. Upload and folder controls follow
each connection's write capability.

## Verification

The Explorer executable test suite exercises two Baidu navigation
snapshots, duplicate folder names, snapshot isolation, revocation and disconnect.
Desktop compilation validates the XAML bindings. These checks do not replace a
live provider sign-in/upload/download acceptance test; no real account credentials
are required or accessed by the automated tests.
