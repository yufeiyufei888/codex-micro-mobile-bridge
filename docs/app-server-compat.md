# Codex app-server compatibility contract

The upstream reference is the official [Codex App Server documentation](https://learn.chatgpt.com/docs/app-server).
The generated schema from the pinned CLI is authoritative whenever prose and a
runtime build differ.

## Local compatibility spike (2026-08-10)

A read-only Windows spike against Codex CLI `0.147.0-alpha.6.5` established the
minimum integration path used by this design:

- `generate-json-schema` completed and produced the checked-in versioned bundle;
- stdio `initialize` followed by `initialized` succeeded, and initialization
  identified the runtime platform as Windows;
- `model/list` succeeded and returned model reasoning-effort capabilities;
- `thread/list` filtered to source kind `appServer` succeeded and returned an
  empty list in the clean spike environment;
- `account/read` returned `account: null` with `requiresOpenaiAuth: true`, which
  places authentication at the desktop companion/App Server boundary, not on
  the phone.

This is feasibility evidence, not release acceptance. Release artifacts still
need the exact CLI version and generated-schema directory digest. No user,
account, or installation identifier belongs in this document or fixtures.

## Process and transport

The companion spawns exactly one managed child:

```text
<absolute-pinned-codex-path> app-server
```

stdin and stdout are newline-delimited JSON messages. Each stdout line must
parse as one complete message; partial lines are buffered and malformed lines
are protocol failures. stderr is captured separately for redacted diagnostics.

The companion does not expose app-server's experimental WebSocket listener.
Private-LAN WSS terminates at the companion and carries protocol-v1 only.

After spawn, the adapter sends `initialize`, waits for success, then sends
`initialized`. No thread method is sent before that handshake completes.

## Fixed CLI and generated schemas

The checked-in JSON bundle and version lock are under
`shared/app-server-schema/`. They were generated with:

```text
codex app-server generate-json-schema --out ./shared/app-server-schema/codex-<version>
```

The release job uses `shared/app-server-compat/generate-schema.ps1` against the
selected CLI and records exact `codex --version`, generated-schema directory
SHA-256, and adapter revision. An upgrade regenerates into a new
version directory, reviews the schema diff, updates adapters/fixtures, and runs
all compatibility tests. Generated files are never hand-edited.

At runtime an exact-version or reviewed-schema-digest mismatch returns
`APP_SERVER_SCHEMA_MISMATCH`; the adapter does not fall back to field guessing.

## Canonical V1 operation mapping

| Protocol operation | App-server/local operation | Completion evidence |
| --- | --- | --- |
| `tasks.list` | Filtered `thread/list`; local slots/projects; cached `model/list` | Point-in-time result and read watermark |
| `task.create` | Resolve configured `projectId`, then `thread/start` and `turn/start` | Created thread plus lifecycle events |
| `task.read` | `thread/read` for a managed thread plus local pending approvals | Point-in-time result and read watermark |
| `task.send` on idle task | `turn/start` with optional catalog model/effort | `turn/completed` |
| `task.send` on active task | `turn/steer` only when `expectedTurnId` matches | Accepted turn ID, then lifecycle events |
| `task.interrupt` | `turn/interrupt` for the exact active turn | Interrupted terminal lifecycle |
| `task.fork` | `thread/fork` for a managed thread | Returned forked thread |
| `task.read_ack` | Companion-local read/attention state | Durable local acknowledgement |
| `approval.respond` | Type-specific response to exact server request | `serverRequest/resolved`, then item lifecycle |
| `slot.assign` | Companion-local six-slot registry | Durable mapping; null clears |

Threads are created/resumed by this companion. List/read/fork/interrupt/steer
operations are restricted to its managed thread registry; unrelated ChatGPT
Desktop tasks are not controllable.

`task.create` accepts `projectId`, never phone-provided `cwd`. A non-null slot
assignment must name a managed thread. Every snapshot has six slot mappings 1
through 6; null means empty and the tasks array contains no fabricated task.

The adapter builds `modelCatalog` from `model/list`, preserving ID, display
name, supported reasoning efforts, and default marker. Model/effort selection
is applied only on create or an idle `turn/start`. Active steering requires the
matching `expectedTurnId` and rejects model/effort overrides. Reasoning effort
is not presented as an official "Fast Mode."

## Four server-request families

| Upstream method | Protocol details | Type-specific response |
| --- | --- | --- |
| `item/commandExecution/requestApproval` | `command`: command text, cwd, reason, offered decisions | Normalized command decision |
| `item/fileChange/requestApproval` | `file_change`: item ID, grant root, nullable authoritative paths, offered decisions | Normalized file decision |
| `item/permissions/requestApproval` | `permission`: cwd, filesystem entries, network enabled plus only available targets, scopes | Requested-ID subset plus scope |
| `item/tool/requestUserInput` | `user_input`: questions, required flags, options | Answers keyed by question ID |

For each upstream request the companion allocates `approvalId`, stores the
upstream JSON-RPC request ID, and emits `approval.requested`. The immutable
binding is exactly `approvalId`, `threadId`, `turnId`, `epoch`, `seq`.
`approval.respond.response` is a tagged union, not a boolean. A response is sent
upstream only if the entry is pending, all five binding fields match, its type
matches, and decisions/grants/answers are valid for the stored request.
`serverRequest/resolved` clears the registry even when resolution happened
elsewhere.

The pinned schema is authoritative for exact method spelling. Unknown server
requests, MCP elicitation, dynamic tool calls, attestation, and other shapes are
V2 or later: they are never auto-approved or compressed into a boolean.

## State reduction and honest progress

- `turn/started` produces `task.state` with `running`.
- A live approval/user input produces `approval.requested` and a waiting state.
- `item/*Delta` produces display-only `task.message.delta`.
- Completed messages/items produce `task.message.completed` as applicable.
- A completed message updates `lastMessagePreview`; deltas never do.
- The companion registry supplies nullable `projectId`; it is never inferred
  from cwd or title.
- App-server plan updates replace the authoritative task `plan`. When progress
  is `plan_steps`, total equals plan length and completed equals the completed
  entry count.
- `turn/completed` is the authoritative completed/interrupted/error evidence.
- Without a real plan, progress is unknown or indeterminate; never a percentage.

Pinned FileChange approval params guarantee `grantRoot` and `itemId`, not a
direct path list. Protocol `details.paths` is therefore `null` unless paths are
available from authoritative item data; the adapter never fabricates them.
Pinned Permissions params expose filesystem entries and `network.enabled`.
Network host/protocol may be available from a real command network context and
port may still be absent. The UI displays only targets actually supplied or
reliably correlated and otherwise says the target is unavailable.

## Child failure and recovery

Unexpected EOF, malformed stdout, nonzero exit, or initialization failure is an
app-server crash. The supervisor:

1. marks affected in-flight tasks `recovery_unknown`;
2. invalidates pending approvals and rotates the protocol epoch;
3. persists uncertain thread/turn IDs;
4. restarts with bounded exponential backoff and jitter;
5. initializes and resumes only companion-owned threads;
6. replaces unknown state only when authoritative reads/events prove it.

Process presence or a resumed thread does not prove the previous turn
completed. If its terminal event cannot be reconstructed, it remains
`recovery_unknown`.
