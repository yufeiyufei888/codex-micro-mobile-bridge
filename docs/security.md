# Security model

## Assets and threats

Protected assets include task text, local paths, commands, file changes,
approval/input responses, pairing keys, and control of the managed Codex
process. The design assumes a hostile/shared LAN, replay attempts, stale taps,
and a lost phone. It does not claim protection after the paired Windows account
or unlocked Android device itself is compromised.

## TLS identity, pairing, and connection authentication

V1 has no plaintext LAN mode. Device authentication uses P-256 signatures.

1. On first run the companion creates an ECDSA P-256 TLS keypair and random
   `serverId`. Its private key is protected with Windows DPAPI `CurrentUser`;
   plaintext private-key files are not retained.
2. "Pair phone" creates a cryptographically random QR nonce and an independent
   six-digit one-time code. Both expire after 60 seconds and are single-use.
   The QR contains the private WSS address ending in `/v1/mobile`, `serverId`,
   protocol version, QR nonce, the six-digit pairing code, and the certificate's
   SHA-256 SPKI pin. The same code may also be entered manually.
3. Android generates a non-exportable P-256 signing key in Android Keystore,
   opens the same `/v1/mobile` WSS, and verifies the leaf certificate's exact
   SHA-256 SPKI pin before sending pairing data. Public-CA validation does not
   replace pinning for this local identity.
4. Inside that WSS, the companion sends a fresh random challenge as a transport
   extension frame. Android returns the QR nonce, six-digit code, device public
   key, and a signature covering the challenge and pairing context. The
   companion verifies the unexpired nonce/code and signature, consumes the
   pairing session atomically, and stores only the device public key and minimal
   device metadata.
5. On every later `/v1/mobile` connection the companion sends a fresh auth
   challenge. Android signs it with its Keystore private key; the companion
   verifies the stored public key before accepting any business frame. After
   authentication, the first business frame is the canonical `snapshot` event.

Pair/auth challenge frames are transport extensions and are not accepted by
`shared/protocol-v1/schema.json`. Challenges are unpredictable and never
reused. Because a six-digit code has low entropy, source/nonce/device rate
limiting is a production release gate; a build that has not implemented it must
say so in diagnostics and release notes. TLS 1.2 or later is mandatory, TLS 1.3
is preferred, and TLS 0-RTT is disabled.

A pin mismatch is a hard `CERT_PIN_MISMATCH`; there is no "continue anyway."
Certificate rotation, desktop reinstall, or lost key material requires an
explicit new pairing from the desktop UI.

## Private-network exposure

The following are production hardening targets and must be verified by runtime
diagnostics/tests; they are not implied to exist merely because this document
specifies them:

- show every actual bind address and refuse release configuration that exposes
  the listener on an unintended public/VPN-exit interface;
- limit any user-created Windows Firewall rule to the executable, chosen port,
  and Private profile; the current implementation must not be described as
  creating or auditing that rule unless it actually does;
- keep `/v1/mobile` WSS as the only mobile application endpoint and never
  forward app-server stdio or loopback diagnostics to the LAN.

mDNS is discovery, not trust. Its TXT record may advertise `serverId`, port,
protocol version, and the SHA-256 SPKI pin. The advertised pin is public and the
TXT record is unauthenticated, so Android must not replace the QR-established
pin with an mDNS value. Pairing codes, nonces, challenges, signatures, and key
material are not advertised.

## Credential storage

### Android

- Generate the P-256 device signing key as non-exportable Android Keystore key
  material and allow it only for signing.
- Store the SHA-256 SPKI pin and paired-host metadata in app-private DataStore.
  These values identify the paired endpoint but are not authentication secrets;
  no desktop/ChatGPT credential is stored on the phone.
- V1 does not require user authentication for every key use because the
  explicitly enabled background-monitoring mode must be able to reconnect while
  the screen is locked. A future biometric/unlock policy must be user-selected
  and must clearly disable that background behavior.
- Clearing app data or invalidating the key removes local authority and
  requires pairing again.

### Windows companion

- Protect the TLS private key and encrypted recovery-state key with DPAPI
  `CurrentUser`.
- Store only Android public keys for connection authentication; the desktop
  never receives or reconstructs a phone private key.
- Apply an ACL granting only the installing user and SYSTEM access to companion
  state. Do not place long-lived credentials in command lines, environment
  variables, or machine-wide plaintext storage.

## Authorization and replay protection

- Pairing/revocation is per device; possession of one registered private key
  grants only the V1 operations exposed by the companion.
- Every write requires current `epoch` plus stable `clientCommandId`. Request
  `id` is only response correlation.
- The companion retains canonical operation hashes/results for at least 24
  hours and the newest 4096 IDs per device. Same ID/body returns the recorded
  result; same ID/different body fails with `IDEMPOTENCY_CONFLICT`.
- Only events consume sequence numbers. A gap forces reconnect and a new
  first-frame snapshot. Epoch rotation invalidates writes and approvals.
- Active `task.send` requires matching `expectedTurnId`; stale/missing values
  fail before text is forwarded. Active steering cannot change model/effort.

The phone never supplies a project path. `task.create.projectId` must resolve
through the companion's configured catalog. A path exposed in a snapshot is
read-only display metadata and cannot be echoed as authorization.

## Approval safety

Approval notification text is informational. The detail screen reloads the
pending record and submits the exact `approvalId`, `threadId`, `turnId`, `epoch`,
and `seq` binding.

- No "approve current request" endpoint exists.
- Command/file decisions must be in `details.allowedDecisions`.
- Permission `granted` IDs must be a subset of requested filesystem/network IDs
  and `scope` must be offered by `details.allowedScopes`.
- User-input answers target pending question IDs and include required answers.
- Session scope requires an additional Android confirmation.
- High/critical-risk approvals require the full detail view; lock-screen actions
  may decline/cancel but never approve.
- Resolved, expired, cleared, or old-epoch approvals fail closed.

## Privacy, revocation, and failure

- Lock-screen notifications default to task title/state; command, paths, diffs,
  answers, and approval details remain hidden.
- Production logs contain IDs/categories/sizes/result codes only. They exclude
  prompts, model output, commands, diffs, local paths, codes, nonces, challenges,
  signatures, private keys, SPKI pins, task titles, and answers.
- Diagnostic export is explicit, time-bounded, redacted, and previewed.
- ChatGPT/Codex authentication credentials never leave the desktop boundary.

Production revocation must delete the registered public key, close active
sockets, and make the next signature challenge fail. Pair/auth failure rate
limiting by source, pairing nonce, and device ID is also a production
requirement. If the current Bridge build has not implemented either control,
diagnostics and release notes must say so; tests must not report them as passed.
Logs never include submitted code/signature. If key storage, SPKI verification,
schema verification, or approval binding is uncertain, control stops; cached
state may remain visibly offline or `recovery_unknown`.
