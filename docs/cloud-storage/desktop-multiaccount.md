# Desktop cloud accounts

The built-in Explorer cloud entry and sidebar expose every connection owned by
the signed-in RelaxKonOS user. Multiple Google Drive and Baidu Netdisk accounts
can remain connected at the same time. No provider-wide “active account” is used
for file requests: listing, pagination, download, upload and folder creation all
carry the selected connection ID.

## Adding and selecting accounts

Use **Add connection**, choose the provider and give each account a recognizable
name, then authorize in the client computer's browser. Choose the intended
provider account there. Browser authorization creates a separate connection;
it does not replace another account, even when the connection form was opened
from **Replace credentials**. Manual credential saves on that form do replace
the selected connection. Names are labels, not identity keys.

The account selector displays name, provider and connection ID, so duplicate
names remain distinguishable. Sidebar entries display provider and expose the
ID in their tooltip. Switching in the selector restores that connection's last
successfully listed folder; selecting its sidebar root explicitly opens root.
The previous account's entries and pagination cursor are cleared before loading
the next account. Navigation snapshots are keyed by connection ID, not name or
provider, removed when a connection disappears, and cleared on disconnect.

File operations serialize within this view and retain the selected connection
for the duration of the operation. Upload/folder controls follow each connection's
write capability. This is not simultaneous split-pane browsing or cross-account
copy/move support. Failed navigation reports an error without displaying another
account's entries.

## Verification

The Explorer executable test suite exercises two Google and two Baidu navigation
snapshots, duplicate folder names, snapshot isolation, revocation and disconnect.
Desktop compilation validates the XAML bindings. These checks do not replace a
live provider sign-in/upload/download acceptance test; no real account credentials
are required or accessed by the automated tests.
