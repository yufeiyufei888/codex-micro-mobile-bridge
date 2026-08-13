# Verification strategy and acceptance matrix

## Android testing setup

The Android client is a single-platform, fully Jetpack Compose application with
Room, Ktor, DataStore, and a manually assembled `AppContainer`. The current
dependency graph already exposes repository and connection boundaries, so the
test setup intentionally keeps manual dependency injection instead of adding a
high-risk Hilt migration solely for tests.

### Local JVM tests

JUnit 4 tests under `android/app/src/test` cover protocol parsing, epoch/sequence
recovery, status mapping, pairing payloads, SPKI/SAN validation, user-facing
failure messages, reply presentation, and conversation-history de-duplication.

```powershell
./android/gradlew.bat -p ./android --no-daemon testDebugUnitTest
```

JaCoCo is enabled for measurement, not as a release percentage gate. Generate
the HTML and XML reports with:

```powershell
./android/gradlew.bat -p ./android --no-daemon jacocoDebugUnitTestReport
```

The report is written below
`android/app/build/reports/jacoco/jacocoDebugUnitTestReport/`. A low whole-app
percentage is expected while Android framework and transport orchestration
remain device-tested; coverage must not be inflated by excluding business
classes merely to reach a target number.

### Instrumented database and Compose tests

Tests under `android/app/src/androidTest` use AndroidJUnit4, an in-memory Room
database, and Compose testing APIs. They verify snapshot replacement, outbox
identity, message watermarks, one-copy reply presentation, history navigation,
and `rememberSaveable` draft restoration.

Compile the device test APK without claiming it ran on hardware:

```powershell
./android/gradlew.bat -p ./android --no-daemon assembleDebugAndroidTest
```

With an emulator or phone visible in `adb devices`, run:

```powershell
./android/gradlew.bat -p ./android --no-daemon connectedDebugAndroidTest
```

Room migration testing remains blocked until the historical version 1 and 2
Room schema JSON files are recovered. The checked-in version 3 schema is not
enough to prove `MIGRATION_1_2` and `MIGRATION_2_3` on SQLite.

### UI, screenshot, and end-to-end boundaries

- Compose behavior tests use semantic matchers first; `testTag` is reserved for
  controls that cannot be selected clearly with at most a few matchers.
- Screen behavior must be exercised at compact, medium, expanded, and 1.5x font
  configurations as the UI suite grows.
- Compose Preview Screenshot Testing was evaluated but not enabled in the
  current release line because the plugin is experimental and would add a new
  image-baseline workflow. Introduce it on an isolated branch before making it
  a release gate.
- System notification, CameraX, Wi-Fi switching, Xiaomi background policy,
  lock-screen survival, WSS pairing, and real Codex Computer Use approval are
  end-to-end behaviors. They require a physical Android device and Windows
  Bridge and must never be reported as passed from JVM or APK compilation alone.
- Android package name, monotonically increasing `versionCode`, and signer
  SHA-256 continuity are mandatory release checks. They are separate from app
  behavior tests and must be verified on the final APK before delivery.

## Test layers

1. **Schema tests:** validate every business frame with
   `shared/protocol-v1/schema.json` on Android and desktop.
2. **Shared semantic fixtures:** replay canonical op/event coverage plus epoch,
   sequence, turn, approval, permission-subset, and idempotency cases.
3. **Adapter contract tests:** run recorded messages from the exact pinned
   app-server schema through the reducer and type-specific approval mapper.
4. **Supervisor integration tests:** fake stdio partial lines, malformed JSON,
   stderr noise, exits, hangs, restarts, and uncertain recovery.
5. **Security tests:** exercise `/v1/mobile` pairing/auth challenges, pinning,
   Keystore signing, public-key revocation, replay, and log redaction.
6. **End-to-end tests:** real pinned Codex CLI on Windows plus a physical
   Android device over private Wi-Fi.

The dependency-free guard is:

```text
node shared/protocol-v1/validate-fixtures.mjs
```

It catches fixture/canonical semantic drift. It does not replace a production
Draft 2020-12 JSON Schema validator.

## Acceptance matrix

| ID | Scenario | Required result |
| --- | --- | --- |
| P-01 | Fresh authenticated `/v1/mobile` WSS | First business frame is `{v,epoch,seq,event:"snapshot",data}` |
| P-02 | Snapshot/list slot shape | Exactly six ordered mappings 1..6; `threadId` is managed ID or null |
| P-03 | Empty slot | No fake task; Android derives an unassigned card by joining slots and tasks |
| P-03A | Task detail replacement | Snapshot/state/read task always contains nullable project ID, nullable latest-completed preview, and plan array |
| P-04 | Event sequence gap | Client stops reducing, reconnects, and waits for snapshot; it never guesses |
| P-05 | Old-epoch write | `STALE_EPOCH`; no local or upstream mutation |
| P-06 | Same `clientCommandId` and body twice | Recorded response returned; action executes once |
| P-07 | Same `clientCommandId`, different body | `IDEMPOTENCY_CONFLICT`; second body is not executed |
| P-08 | Unknown op/event/field or legacy envelope | `INVALID_MESSAGE` schema rejection |
| P-09 | All seven write operations | Each requires exactly named `clientCommandId` and current `epoch` |
| P-10 | `slot.assign` with null | Slot clears; task itself is not deleted |
| P-11 | `slot.assign` with unmanaged thread | `THREAD_NOT_FOUND`; mapping unchanged |
| T-01 | `task.create` | Sends configured `projectId`; schema rejects phone-provided `cwd` |
| T-02 | Idle `task.send` | `expectedTurnId` optional; valid catalog model/effort may reach `turn/start` |
| T-03 | Active `task.send` with matching turn | `expectedTurnId` required; text reaches only that `turn/steer` |
| T-04 | Active `task.send` with missing/stale turn | `STALE_TURN`; text is not forwarded |
| T-05 | Active send with model/effort override | `ACTIVE_TURN_OVERRIDE_NOT_ALLOWED` or schema rejection |
| T-06 | Model/effort selection | Model is in catalog and effort in its supported list; no "official Fast Mode" label |
| S-01 | Valid QR nonce + six-digit code within 60s | SHA-256 SPKI checked; P-256 signature verified; desktop stores only public key |
| S-02 | Wrong TLS key | Hard `CERT_PIN_MISMATCH`; no bypass or pairing proof sent |
| S-03 | Reused/expired nonce or code | `AUTH_FAILED`; no device public key registered |
| S-04 | Revoked device | Public key removed, active socket closes, next challenge fails |
| S-05 | Public/wildcard listener attempt | Refused; no Public-profile firewall rule |
| S-06 | Log inspection | No codes, nonces, challenges, signatures, pins, prompts, commands, paths, diffs, answers, or model output |
| A-01 | Command approval | Details include command/cwd/reason; normalized offered decision forwarded once |
| A-02 | File-change approval | itemId/grantRoot shown; unavailable direct paths remain null, never guessed |
| A-03 | Permission approval | Filesystem and network-enabled shown; only real target host/protocol/optional port displayed; grants are requested subset with scope |
| A-04 | User input | Questions/options shown; all required answers tied to exact approval |
| A-05 | Resolved elsewhere | `APPROVAL_NOT_PENDING`; UI refreshes and no upstream duplicate is sent |
| A-06 | Any binding field changed | `APPROVAL_BINDING_MISMATCH`; no upstream response |
| A-07 | Approval from old epoch | `APPROVAL_STALE` or `STALE_EPOCH`; no upstream response |
| A-08 | Four response payloads | Tagged command/file/permission/user-input union is preserved; never boolean |
| C-01 | Pinned CLI/schema match | Child initializes over stdio and Bridge becomes online |
| C-02 | CLI version/hash mismatch | `APP_SERVER_SCHEMA_MISMATCH`; child is not trusted |
| C-03 | stdout split across reads | Lines buffer and each message is processed once |
| C-04 | JSON-looking stderr | Diagnostic only; never enters reducer |
| C-05 | Unknown upstream server request | Never auto-approved; affected task shows compatibility error |
| R-01 | Child exits during running turn | New epoch; task becomes `recovery_unknown`, never inferred complete |
| R-02 | Child exits with approval pending | Registry invalidated; old phone response fails |
| R-03 | Resume proves terminal state | Unknown changes only after authoritative read/event |
| R-04 | Resume cannot prove outcome | `recovery_unknown` persists with safe recovery action |
| U-01 | Running without plan | Unknown/indeterminate only; no percentage |
| U-02 | Authoritative plan steps | Completed/total matches app-server states exactly |
| U-02A | Task plan invariant | `totalSteps == plan.length`; completed count equals completed plan entries |
| U-02B | Recovery retains real plan | Unknown progress may show retained steps but derives no percentage/completion |
| U-03 | Long delta stream | Token/output volume never fabricates progress |
| E-01 | Six independent slots | Update affects only matching thread/slot join; color/text/icon agree |
| E-02 | Network loss/return | Cached state visibly offline; reconnect snapshot causes no duplicate write |
| E-03 | Ownership boundary | Unrelated ChatGPT Desktop task cannot be read, steered, interrupted, forked, slotted, or approved |
| V-01 | V1 capability boundary | No voice, PTT, BLE, or foreground-open business capability exists |

## Release gates

- Both platforms pass every schema and shared-fixture case.
- Runtime CLI exact version and generated-schema directory digest equal the
  reviewed compatibility lock.
- No failing/skipped P/T/S/A/C/R/U/E/V acceptance case is allowed for V1.
- End-to-end evidence records exact app, companion, CLI, Android, Windows, and
  network versions and whether the child was real or fake.
- Static/schema/unit success is reported honestly and is not called physical
  device, real-network, or real-Codex acceptance.
