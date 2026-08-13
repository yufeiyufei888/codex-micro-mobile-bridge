# Codex Micro Desktop Sync Bridge (Windows)

V1.0.6 is a Windows companion for the Android Codex Micro controller. It exposes a pinned-TLS, device-authenticated LAN WSS endpoint and maps the phone to the currently active Codex desktop conversation. Active rollout metadata is opened with Windows writer-compatible sharing, so a file that Codex is currently appending is not rejected as a non-root session. The existing verified binding also remains live while an exact phone prompt is being associated.

## Desktop sync boundary

- Text is written through Windows UI Automation to the verified Codex ProseMirror editor and sent only after the Codex process, foreground window, editor value, and focus are rechecked.
- Stop invokes the current Codex Stop control.
- Approval discovery requires an approval context and a supported visible action. A phone decision is applied only after the current approval fingerprint still matches; the bridge invokes the action, or uses focused Enter as a verified fallback.
- The bridge reads the local Codex session record to return the latest assistant reply to the phone.
- It does not use fixed coordinates, inject unconditional global keystrokes, expose credentials, or attach through an undocumented network API.

The public App Server documentation describes starting/resuming App Server-owned threads and server-initiated approvals, but does not promise concurrent takeover of an already active desktop UI conversation. Desktop sync therefore uses a deliberately narrow, fail-closed UI Automation adapter.

## Runtime

Open and sign in to Codex Desktop first, select the conversation to control, then run `CodexMicroBridge.exe`. Closing the WPF window hides it to the notification area. The app is single-instance per Windows user.

Pairing opens from the local WPF UI for 60 seconds. The QR contains the WSS address, SPKI pin, nonce, expiry, and one-time code, but no OpenAI credential. Returning devices authenticate with a fresh signed challenge. The phone must verify hostname, certificate validity, and the pinned SPKI value before sending credentials.

The WSS endpoint is `/v1/mobile`; complete text envelopes are limited to 1 MiB. Pairing state, TLS material, idempotency records, and cached messages remain under `%LOCALAPPDATA%\CodexMicroBridge`, with sensitive persisted fields protected by DPAPI CurrentUser.

The bridge binds a selected RFC1918 Wi-Fi/Ethernet address on port 47127 and advertises `_codexmicro._tcp` as an untrusted discovery hint. It does not elevate itself or modify Windows Firewall automatically.

## Build and test

```powershell
work\dotnet10\dotnet.exe build bridge\CodexMicroBridge.sln -c Release --no-restore
work\dotnet10\dotnet.exe test bridge\CodexMicroBridge.sln -c Release --no-restore
work\dotnet10\dotnet.exe publish bridge\src\CodexMicroBridge.App\CodexMicroBridge.App.csproj -c Release -r win-x64 --self-contained true --no-restore
```

The bridge targets `net10.0-windows`. Automated tests cover protocol validation, pairing and authentication, TLS/persistence boundaries, idempotency, reducer behavior, session-response extraction, and the stable virtual desktop task. Actual Codex Desktop input, approval controls, and Windows-version accessibility behavior require interactive end-to-end verification.

Legacy App Server adapter and schema projects remain in the repository for compatibility tests and historical reference, but the V1.0.6 runtime does not start an App Server child process.
