#!/usr/bin/env node
// Dependency-free canonical-contract guard. Production Android and companion
// implementations must also run a Draft 2020-12 JSON Schema validator.

import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const fixturesRoot = join(root, "fixtures");
const load = async (path) => JSON.parse(await readFile(path, "utf8"));
const manifest = await load(join(fixturesRoot, "manifest.json"));
const schema = await load(join(root, "schema.json"));
const codes = await load(join(root, "status-codes.json"));

const fail = (message) => {
  throw new Error(message);
};
const assert = (condition, message) => {
  if (!condition) fail(message);
};
const exactKeys = (value, expected, label) => {
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  assert(JSON.stringify(actual) === JSON.stringify(wanted), `${label}: keys ${actual.join(",")}`);
};
const canonical = (value) => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
};
const withoutCommandId = ({ clientCommandId: _ignored, ...rest }) => rest;
const commandHash = (message) => createHash("sha256")
  .update(canonical({ op: message.op, params: withoutCommandId(message.params) }))
  .digest("hex");
const hasPropertyNamed = (value, forbidden) => {
  if (!value || typeof value !== "object") return false;
  if (Object.prototype.hasOwnProperty.call(value, forbidden)) return true;
  return Object.values(value).some((child) => hasPropertyNamed(child, forbidden));
};

const allowedOps = new Set([
  "tasks.list",
  "task.create",
  "task.read",
  "task.send",
  "task.interrupt",
  "task.fork",
  "task.read_ack",
  "approval.respond",
  "slot.assign",
]);
const writeOps = new Set([
  "task.create",
  "task.send",
  "task.interrupt",
  "task.fork",
  "task.read_ack",
  "approval.respond",
  "slot.assign",
]);
const allowedEvents = new Set([
  "snapshot",
  "bridge.status",
  "task.state",
  "task.message.delta",
  "task.message.completed",
  "task.plan.updated",
  "approval.requested",
  "approval.resolved",
  "task.error",
]);
const errorCodes = new Set(codes.errorCodes.map(({ code }) => code));
const taskStatuses = new Set(codes.taskStatuses.map(({ id }) => id));
const bindingKeys = ["approvalId", "threadId", "turnId", "epoch", "seq"];

assert(schema.$schema === "https://json-schema.org/draft/2020-12/schema", "schema draft changed");
assert(schema.$defs.request.oneOf.length === allowedOps.size, "schema must expose exactly nine operations");
assert(schema.$defs.event.oneOf.length === allowedEvents.size, "schema must expose exactly nine events");
assert(manifest.protocolVersion === 1, "fixture manifest version must be 1");
assert(codes.protocolVersion === 1, "status code version must be 1");
assert(!JSON.stringify(schema).includes("voice_text"), "V1 schema must not expose voice_text");
assert(!JSON.stringify(schema).includes("foreground_open"), "V1 schema must not expose foreground_open");

const validateProgress = (progress, label) => {
  assert(progress && typeof progress === "object", `${label}: progress`);
  assert(!Object.prototype.hasOwnProperty.call(progress, "percent"), `${label}: progress percent is forbidden`);
  assert(["unknown", "indeterminate", "plan_steps"].includes(progress.kind), `${label}: progress kind`);
  if (progress.kind === "plan_steps") {
    assert(progress.completedSteps <= progress.totalSteps, `${label}: invalid plan step counts`);
  }
};
const validateTask = (task, label) => {
  assert(taskStatuses.has(task.status), `${label}: task status ${task.status}`);
  assert(task.status !== "unassigned", `${label}: unassigned is a derived empty-slot state, not a task`);
  assert(Object.prototype.hasOwnProperty.call(task, "projectId") &&
    (task.projectId === null || typeof task.projectId === "string"), `${label}: projectId`);
  assert(Array.isArray(task.plan), `${label}: authoritative plan array`);
  assert(Object.prototype.hasOwnProperty.call(task, "lastMessagePreview") &&
    (task.lastMessagePreview === null ||
      (typeof task.lastMessagePreview === "string" && task.lastMessagePreview.length <= 500)),
  `${label}: lastMessagePreview`);
  validateProgress(task.progress, label);
  if (task.progress.kind === "plan_steps") {
    const completed = task.plan.filter(({ status }) => status === "completed").length;
    assert(task.progress.totalSteps === task.plan.length,
      `${label}: totalSteps must equal authoritative plan length`);
    assert(task.progress.completedSteps === completed,
      `${label}: completedSteps must equal completed plan entries`);
  }
};
const validateTasks = (tasks, label) => {
  assert(Array.isArray(tasks) && tasks.length <= 6, `${label}: tasks`);
  assert(new Set(tasks.map(({ threadId }) => threadId)).size === tasks.length, `${label}: duplicate thread`);
  tasks.forEach((task, index) => validateTask(task, `${label}.tasks[${index}]`));
};
const validateSlots = (slots, tasks, label) => {
  assert(Array.isArray(slots) && slots.length === 6, `${label}: exactly six slots required`);
  assert(slots.every(({ slot }, index) => slot === index + 1), `${label}: slots must be ordered 1..6`);
  const assigned = slots.map(({ threadId }) => threadId).filter((threadId) => threadId !== null);
  assert(new Set(assigned).size === assigned.length, `${label}: one task cannot occupy multiple slots`);
  const taskIds = new Set(tasks.map(({ threadId }) => threadId));
  assert(assigned.every((threadId) => taskIds.has(threadId)), `${label}: slot points to a non-snapshot task`);
};
const validateModelCatalog = (catalog, label) => {
  assert(Array.isArray(catalog), `${label}: modelCatalog`);
  assert(catalog.filter(({ default: isDefault }) => isDefault).length <= 1, `${label}: multiple default models`);
  for (const model of catalog) {
    assert(typeof model.id === "string" && typeof model.displayName === "string", `${label}: model identity`);
    assert(Array.isArray(model.supportedReasoningEfforts) && model.supportedReasoningEfforts.length > 0,
      `${label}: model efforts`);
  }
};
const validateProjectCatalog = (catalog, label) => {
  assert(Array.isArray(catalog), `${label}: projectCatalog`);
  assert(new Set(catalog.map(({ projectId }) => projectId)).size === catalog.length,
    `${label}: duplicate projectId`);
  for (const project of catalog) {
    assert(typeof project.projectId === "string" && typeof project.displayName === "string",
      `${label}: project identity`);
  }
};
const exactBinding = (left, right) => bindingKeys.every((key) => left?.[key] === right?.[key]);

const basicValidate = (message, name) => {
  assert(message.v === 1, `${name}: v`);
  assert(!hasPropertyNamed(message, "percent"), `${name}: percent is forbidden`);
  assert(!hasPropertyNamed(message, "voice_text"), `${name}: voice_text is forbidden`);

  if (Object.prototype.hasOwnProperty.call(message, "op")) {
    exactKeys(message, ["v", "id", "op", "params"], name);
    assert(allowedOps.has(message.op), `${name}: unknown op ${message.op}`);
    assert(message.params && typeof message.params === "object" && !Array.isArray(message.params), `${name}: params`);
    if (writeOps.has(message.op)) {
      assert(typeof message.params.clientCommandId === "string" && message.params.clientCommandId.length >= 16,
        `${name}: write operation requires clientCommandId`);
      assert(typeof message.params.epoch === "string" && message.params.epoch.length >= 16,
        `${name}: write operation requires epoch`);
    } else {
      assert(!Object.prototype.hasOwnProperty.call(message.params, "clientCommandId"),
        `${name}: read operation must not carry clientCommandId`);
    }
    if (message.op === "approval.respond") {
      assert(bindingKeys.every((key) => Object.prototype.hasOwnProperty.call(message.params, key)),
        `${name}: incomplete approval binding`);
      const response = message.params.response;
      assert(response && ["command", "file_change", "permission", "user_input"].includes(response.type),
        `${name}: tagged approval response`);
      if (response.type === "command" || response.type === "file_change") {
        assert(typeof response.decision === "string", `${name}: normalized decision`);
      } else if (response.type === "permission") {
        assert(Array.isArray(response.granted) && ["once", "session"].includes(response.scope),
          `${name}: permission grant and scope`);
      } else {
        assert(response.answers && typeof response.answers === "object", `${name}: user-input answers`);
      }
    }
    if (message.op === "task.create") {
      assert(typeof message.params.projectId === "string", `${name}: projectId`);
      assert(!Object.prototype.hasOwnProperty.call(message.params, "cwd"), `${name}: phone path is forbidden`);
    }
    if (message.op === "task.send" && Object.prototype.hasOwnProperty.call(message.params, "expectedTurnId")) {
      assert(!Object.prototype.hasOwnProperty.call(message.params, "model") &&
        !Object.prototype.hasOwnProperty.call(message.params, "effort"),
      `${name}: active steer cannot override model/effort`);
    }
  } else if (Object.prototype.hasOwnProperty.call(message, "event")) {
    exactKeys(message, ["v", "epoch", "seq", "event", "data"], name);
    assert(allowedEvents.has(message.event), `${name}: unknown event ${message.event}`);
    assert(typeof message.epoch === "string" && message.epoch.length >= 16, `${name}: epoch`);
    assert(Number.isInteger(message.seq) && message.seq >= 1, `${name}: seq`);
    if (message.event === "snapshot") {
      validateTasks(message.data.tasks, name);
      validateSlots(message.data.slots, message.data.tasks, name);
      validateProjectCatalog(message.data.projectCatalog, name);
      validateModelCatalog(message.data.modelCatalog, name);
    }
    if (message.event === "task.state") validateTask(message.data.task, name);
    if (message.event === "approval.requested") {
      const approval = message.data.approval;
      assert(approval.epoch === message.epoch && approval.seq === message.seq,
        `${name}: approval binding must match requesting event epoch/seq`);
      assert(approval.details?.type === approval.approvalType, `${name}: approval details type mismatch`);
      if (approval.approvalType === "command") {
        assert(typeof approval.details.command === "string" && typeof approval.details.cwd === "string" &&
          typeof approval.details.reason === "string", `${name}: command details`);
      } else if (approval.approvalType === "file_change") {
        assert(typeof approval.details.itemId === "string" &&
          (approval.details.paths === null || Array.isArray(approval.details.paths)) &&
          typeof approval.details.grantRoot === "string",
          `${name}: file-change details`);
      } else if (approval.approvalType === "permission") {
        assert(typeof approval.details.cwd === "string" && Array.isArray(approval.details.requested.filesystem) &&
          typeof approval.details.requested.network?.enabled === "boolean" &&
          Array.isArray(approval.details.requested.network?.targets), `${name}: permission details`);
        for (const network of approval.details.requested.network.targets) {
          assert(typeof network.host === "string" && typeof network.protocol === "string" &&
            (network.port === undefined || Number.isInteger(network.port)), `${name}: network display tuple`);
        }
      } else {
        assert(Array.isArray(approval.details.questions) &&
          approval.details.questions.every(({ options }) => Array.isArray(options)), `${name}: user-input questions/options`);
      }
    }
  } else if (Object.prototype.hasOwnProperty.call(message, "result")) {
    exactKeys(message, ["v", "id", "result"], name);
    assert(message.result && typeof message.result === "object" && !Array.isArray(message.result), `${name}: result`);
    if (Array.isArray(message.result.tasks)) {
      validateTasks(message.result.tasks, name);
      validateSlots(message.result.slots, message.result.tasks, name);
      validateProjectCatalog(message.result.projectCatalog, name);
      validateModelCatalog(message.result.modelCatalog, name);
    }
    if (message.result.task) validateTask(message.result.task, name);
  } else if (Object.prototype.hasOwnProperty.call(message, "error")) {
    exactKeys(message, ["v", "id", "error"], name);
    assert(errorCodes.has(message.error.code), `${name}: error code ${message.error.code}`);
  } else {
    fail(`${name}: not a canonical request, response, or event`);
  }
};

for (const testCase of manifest.cases) {
  const message = await load(join(fixturesRoot, testCase.file));
  basicValidate(message, testCase.file);
  let outcome = "ACCEPT";
  const messageEpoch = message.op === "approval.respond" ? message.params.epoch : message.epoch;
  if (testCase.context?.currentEpoch && messageEpoch !== testCase.context.currentEpoch) {
    outcome = "STALE_EPOCH";
  } else if (testCase.context?.lastServerSeq !== undefined && message.seq !== testCase.context.lastServerSeq + 1) {
    outcome = "SEQ_GAP";
  } else if (testCase.context?.pendingBinding && !exactBinding(message.params, testCase.context.pendingBinding)) {
    outcome = "APPROVAL_BINDING_MISMATCH";
  } else if (testCase.context?.activeTurnId && message.op === "task.send" &&
      message.params.expectedTurnId !== testCase.context.activeTurnId) {
    outcome = "STALE_TURN";
  } else if (testCase.context?.requestedPermissionIds && message.op === "approval.respond" &&
      message.params.response.type === "permission" &&
      !message.params.response.granted.every((id) => testCase.context.requestedPermissionIds.includes(id))) {
    outcome = "DECISION_NOT_ALLOWED";
  } else if (testCase.context?.managedThreadIds && message.op === "slot.assign" &&
      message.params.threadId !== null && !testCase.context.managedThreadIds.includes(message.params.threadId)) {
    outcome = "THREAD_NOT_FOUND";
  }
  assert(outcome === testCase.semanticOutcome, `${testCase.file}: expected ${testCase.semanticOutcome}, got ${outcome}`);
}

for (const pair of manifest.pairs) {
  const [left, right] = await Promise.all(pair.files.map((file) => load(join(fixturesRoot, file))));
  basicValidate(left, pair.files[0]);
  basicValidate(right, pair.files[1]);
  const sameCommandId = left.params.clientCommandId === right.params.clientCommandId;
  const outcome = sameCommandId && commandHash(left) !== commandHash(right)
    ? "IDEMPOTENCY_CONFLICT"
    : "ACCEPT";
  assert(outcome === pair.semanticOutcome, `${pair.files.join(" + ")}: expected ${pair.semanticOutcome}, got ${outcome}`);
}

console.log(`protocol-v1 canonical fixtures OK (${manifest.cases.length} cases, ${manifest.pairs.length} pair)`);
