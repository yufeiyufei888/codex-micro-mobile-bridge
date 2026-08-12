# Codex Micro Mobile protocol v1

This folder is the language-neutral business contract between Android and the
desktop companion. It is intentionally smaller than the Codex app-server
protocol. The companion owns the Codex process and translates the pinned
app-server schema into this stable surface.

Pairing, SHA-256 SPKI pinning, and P-256 challenge-signature authentication
occur before business messages on `/v1/mobile`. They are transport extensions
and are not valid messages under `schema.json`.

## Canonical frames

Protocol v1 has only three public frame shapes:

```json
{ "v": 1, "id": "req_123", "op": "task.read", "params": { "threadId": "thr_demo" } }
{ "v": 1, "id": "req_123", "result": {} }
{ "v": 1, "id": "req_123", "error": { "code": "...", "message": "...", "retryable": false } }
{ "v": 1, "epoch": "...", "seq": 1, "event": "snapshot", "data": {} }
```

The first line is a request. The next two lines are the mutually exclusive
success and error response forms. The last line is an ordered server event.
`id` correlates a response to one request and does not provide idempotency.

The public operations are exactly:

1. `tasks.list`
2. `task.create`
3. `task.read`
4. `task.send`
5. `task.interrupt`
6. `task.fork`
7. `task.read_ack`
8. `approval.respond`
9. `slot.assign`

The public events are exactly:

1. `snapshot`
2. `bridge.status`
3. `task.state`
4. `task.message.delta`
5. `task.message.completed`
6. `task.plan.updated`
7. `approval.requested`
8. `approval.resolved`
9. `task.error`

Unknown operations, events, top-level shapes, and closed-object fields are
rejected as `INVALID_MESSAGE`. Voice, push-to-talk, foreground-open controls,
feature negotiation, and BLE transport are not part of V1. V1 uses private-LAN
WSS only.

## Operation semantics

| Operation | Read/write | Meaning |
| --- | --- | --- |
| `tasks.list` | Read | Return six slot mappings, real companion tasks, catalogs, and a read watermark |
| `task.create` | Write | Create a companion-owned thread from a configured `projectId` and start its first turn |
| `task.read` | Read | Return one companion-owned task, completed messages, and pending approvals |
| `task.send` | Write | Start an idle task turn or steer the exact expected active turn |
| `task.interrupt` | Write | Interrupt the exact active turn |
| `task.fork` | Write | Fork a companion-owned thread at the requested supported point |
| `task.read_ack` | Write | Mark messages through the supplied message ID as read in companion state |
| `approval.respond` | Write | Resolve one still-pending, exactly bound approval/input request |
| `slot.assign` | Write | Assign a managed thread or `null` to clear one of six local slots |

Every write has `params.clientCommandId` and `params.epoch`. Read operations do
not. `task.read_ack` is a write because it mutates attention/read state.

`task.create` sends `projectId`, never `cwd` or another phone-provided path.
The project catalog is configured on the companion and may expose its path only
as read-only snapshot metadata. A non-null `slot.assign.threadId` must already
belong to the companion; `null` explicitly clears the slot.

`snapshot.slots` and `tasks.list.result.slots` always contain exactly six
ordered entries for slots 1 through 6. `threadId: null` means empty. `tasks`
contains only real companion-owned tasks and never a fabricated `unassigned`
task. Android joins the arrays and derives its empty/unassigned cards.

Every task carries three detail fields in every snapshot, `task.state`, and
read/list result:

- `projectId`: the configured project identity, or `null` when no authoritative
  association exists;
- `lastMessagePreview`: a plain-text preview of the latest completed message,
  or `null` before one exists;
- `plan`: the complete latest authoritative app-server plan-step array, which
  may be empty.

Android replaces these fields from authoritative task objects; it does not
merge stale local previews or invent a project from a path/title.

`snapshot` and `tasks.list` also carry `modelCatalog`. `task.create` and an idle
`task.send` may select a listed `model` and supported `effort`. When a task has
an active turn, `task.send.expectedTurnId` is mandatory and must match; otherwise
the Bridge returns `STALE_TURN`. Active steering rejects model/effort overrides.
Reasoning effort is shown by its upstream capability name and is never branded
as an official "Fast Mode."

A successful write response means the operation was accepted or its durable
result was found by `clientCommandId`. It is not evidence that a turn finished.
Only an authoritative lifecycle event or later snapshot can prove completion.

## Ordering and recovery

- `epoch` is an opaque random token with at least 128 bits of entropy. The
  companion rotates it whenever the companion generation or managed app-server
  child is replaced.
- `seq` applies only to events. It starts at `1` and increases by exactly one
  for every event in an epoch. Responses are correlated by `id` and never
  consume an event sequence number.
- The first business frame on every authenticated WSS session is `snapshot`.
  That snapshot replaces the Android reducer state and establishes its current
  `(epoch, seq)` baseline.
- Android applies later events only when both the epoch matches and sequence is
  contiguous. On a gap it stops reducing events, closes the session, reconnects,
  and waits for a new authoritative snapshot. It never guesses missing state.
- A write using an older epoch fails as `STALE_EPOCH` before any upstream call.

`tasks.list` and `task.read` include an epoch/sequence read watermark, but they
do not repair a broken event stream. Only the connection-opening `snapshot`
resets event continuity.

## Idempotency

For every write, Android creates one stable `clientCommandId` before the first
send and reuses it across timeout/reconnect retries. The companion stores the
canonical operation hash and final response for at least 24 hours and the
latest 4096 command IDs per paired device.

The canonical hash includes `op` plus all `params` except `clientCommandId`;
the transport request `id` is excluded. Repeating the same command ID and body
returns the recorded response without repeating the action. Reusing it with a
different body returns `IDEMPOTENCY_CONFLICT` and performs no new action.

## Approval and input binding

`approval.requested` normalizes exactly four upstream families while retaining
type-specific `details`:

1. `command`: usable command text, `cwd`, reason, and offered decisions;
2. `file_change`: item ID, grant root, offered decisions, and paths only when
   authoritative item data actually provides them (`null` otherwise);
3. `permission`: `cwd`, requested filesystem entries, `network.enabled`, and
   only network targets actually supplied or reliably correlated; missing
   target/port is displayed as unavailable, never inferred;
4. `user_input`: questions, required flags, and options.

The immutable approval binding is exactly `approvalId`, `threadId`, `turnId`,
`epoch`, and `seq`. For `approval.requested`, the nested approval epoch and seq
must equal the enclosing event epoch and seq. Android sends all five fields in
`approval.respond`; the companion compares them with the still-pending
registry entry and confirms the decision was offered.

`approval.respond.response` is a tagged union, not a boolean: command and file
change use a normalized decision; permission uses `granted` permission IDs plus
`scope`; user input uses answers keyed by question ID. Granted IDs must be a
subset of the still-pending request, and every required question must be
answered.

Mismatch, expiry, upstream cleanup, duplicate resolution, or epoch rotation
fails closed. There is no endpoint that resolves "the current approval."

## Honest progress

There is deliberately no percentage field. V1 permits:

- `unknown`: no trustworthy progress exists;
- `indeterminate`: authoritative activity exists but no total is known. Its
  source may be app-server state or the verified current Codex desktop UI
  (`desktop_ui_status`);
- `plan_steps`: completed/total counts derived from explicit app-server plan
  step states.

When progress is `plan_steps`, `totalSteps` equals `task.plan.length` and
`completedSteps` equals the number of plan entries whose status is `completed`.
With `unknown` or `indeterminate`, the plan may be empty or retain the latest
real steps (for example during `recovery_unknown`), but the UI must not derive
a percentage or terminal result from the retained plan.

Elapsed time, token count, output volume, animation duration, and process
presence are never converted into a progress percentage or completion claim.

Task `status` and the boolean `attention` are orthogonal. A task waiting for a
user answer is `waiting_input`; an unread finished task is `completed` with
`attention: true`. V1 does not define `waiting_reply` or `completed_unread`.

## Compatibility fixtures

Android and the companion validate every JSON fixture against `schema.json`
and replay the semantic cases in `manifest.json`. The dependency-free guard is:

```text
node shared/protocol-v1/validate-fixtures.mjs
```

That guard checks canonical drift and shared semantics; it is not a replacement
for a Draft 2020-12 validator in either production implementation.
