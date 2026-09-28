# Windows workstation owner-device keys

This feature applies only when the host is Windows 10 or Windows 11 workstation edition. Linux, macOS, and every Windows Server edition retain password-backed host-administrator elevation.

## Trust model

Each controller owns a distinct P-256 ECDSA key pair. The private key remains in the platform keystore: DPAPI for the Windows desktop client and Android Keystore for the mobile client. RelaxKonOS Server stores only DER SubjectPublicKeyInfo public-key bytes.

The local Windows device is enrolled from a loopback-only Negotiate endpoint while the same local Windows administrator that runs Server is signed in. It is unavailable over a LAN address, tunnel, or reverse proxy. That route may also replace a lost local key for the same device; it never accepts a remote request. An enrolled controller creates a one-time, ten-minute pairing invitation for another controller. The invitation carries no private key and may be represented as a QR payload containing the server origin and invitation token.

The current desktop flow serializes the QR payload as base64-encoded UTF-8 JSON:

```json
{ "version": 1, "serverUrl": "https://host.example/", "token": "…", "expiresAt": "…" }
```

Android must accept this exact payload from a live QR scan, a user-selected local image, or manual paste; these are input alternatives, never distinct wire formats. Live scanning uses the permissionless Google Code Scanner when available, while local-image recognition uses the bundled ML Kit QR reader so it works without a model download. Before creating its Android Keystore key, the app shows the resolved HTTP(S) Server origin and expiry for explicit confirmation, and rejects expired or unsupported-protocol origins.

To sign in, a controller requests a 32-byte, two-minute nonce and returns an ECDSA SHA-256 signature. The Server consumes each nonce once, then issues the usual short-lived access and refresh tokens with `amr=owner-device-key`.

## Host elevation

Only an active owner-device-key session on the supported Windows workstation platform may obtain an elevation grant without a Windows administrator password. Grants retain the existing capability, target, token-JTI, expiry, and audit boundaries. Pairing never gives a generic LocalSystem shell or an unrestricted administrator token.

Owner devices can list and revoke other owner devices. The final active device cannot revoke itself. A recovery-code flow will be introduced with the pairing UI; it must be a high-entropy one-time recovery credential, never a shared day-to-day password.

## Mobile flow

1. Generate a P-256 Android Keystore key with user authentication enabled when available.
2. Scan a pairing QR code, select a local QR image, or paste the code created by an enrolled controller.
3. Show the scanned Server origin and expiry, then submit the invitation token and SPKI public key; request and sign a nonce to obtain the normal login session.
4. On each remote sign-in, repeat only nonce signing; never transmit an administrator or Microsoft-account password.
