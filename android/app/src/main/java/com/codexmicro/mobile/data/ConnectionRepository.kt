package com.codexmicro.mobile.data

import com.codexmicro.mobile.data.settings.SettingsStore
import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalDecision
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.domain.ConnectionStatus
import com.codexmicro.mobile.domain.PairingInfo
import com.codexmicro.mobile.domain.ModelOption
import com.codexmicro.mobile.domain.ProjectOption
import com.codexmicro.mobile.domain.PlanStep
import com.codexmicro.mobile.domain.PlanStepState
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind
import com.codexmicro.mobile.domain.EventCursor
import com.codexmicro.mobile.domain.CursorDecision
import com.codexmicro.mobile.domain.mapCanonicalTaskStatus
import com.codexmicro.mobile.domain.approvalResolutionMatches
import com.codexmicro.mobile.domain.canApplyReadAcknowledgement
import com.codexmicro.mobile.network.IncomingMessage
import com.codexmicro.mobile.network.PinnedWebSocketConnection
import com.codexmicro.mobile.network.PROTOCOL_VERSION
import com.codexmicro.mobile.network.ProtocolEvent
import com.codexmicro.mobile.network.ProtocolEvents
import com.codexmicro.mobile.network.ProtocolOps
import com.codexmicro.mobile.network.RemoteProtocolException
import com.codexmicro.mobile.network.SnapshotData
import com.codexmicro.mobile.network.WireApproval
import com.codexmicro.mobile.network.WireTask
import com.codexmicro.mobile.network.WireMessage
import com.codexmicro.mobile.network.TaskStateData
import com.codexmicro.mobile.network.TaskMessageDeltaData
import com.codexmicro.mobile.network.TaskMessageCompletedData
import com.codexmicro.mobile.network.TaskPlanUpdatedData
import com.codexmicro.mobile.network.ApprovalRequestedData
import com.codexmicro.mobile.network.ApprovalResolvedData
import com.codexmicro.mobile.network.TaskErrorData
import com.codexmicro.mobile.network.BridgeStatusData
import com.codexmicro.mobile.network.WireBridgeStatus
import com.codexmicro.mobile.network.CommandApprovalDetails
import com.codexmicro.mobile.network.FileChangeApprovalDetails
import com.codexmicro.mobile.network.PermissionApprovalDetails
import com.codexmicro.mobile.network.UserInputApprovalDetails
import com.codexmicro.mobile.network.taskResultThreadId
import com.codexmicro.mobile.network.TaskResult
import com.codexmicro.mobile.network.TurnAcceptedResult
import com.codexmicro.mobile.network.ReadAckResult
import com.codexmicro.mobile.network.ApprovalRespondResult
import com.codexmicro.mobile.network.SlotAssignResult
import com.codexmicro.mobile.network.TaskReadResult
import com.codexmicro.mobile.network.MAX_WSS_FRAME_BYTES
import com.codexmicro.mobile.notifications.ApprovalNotificationManager
import com.codexmicro.mobile.security.PairingKeyStore
import com.codexmicro.mobile.security.SpkiPinningTrustManager
import java.security.SecureRandom
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.time.Instant
import java.util.Base64
import java.util.UUID
import kotlin.math.min
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

internal fun isBlockingAuthenticationError(code: String): Boolean = code in setOf(
    "AUTH_REQUIRED",
    "AUTH_FAILED",
    "CERT_PIN_MISMATCH",
    "UNSUPPORTED_PROTOCOL",
    "REPLAY_DETECTED",
)

internal fun preserveCompleteResponse(existingFull: String?, authoritativePreview: String?): String {
    val existing = existingFull.orEmpty()
    val preview = authoritativePreview.orEmpty()
    if (preview.isBlank()) return existing
    if (existing.length >= preview.length &&
        (existing.startsWith(preview) || existing.endsWith(preview))
    ) return existing
    return preview
}

internal fun isCanonicalProgressSource(source: String?): Boolean = source in setOf(
    "app_server_status",
    "app_server_item",
    "desktop_ui_status",
)

internal fun restoreAuthoritativeBridgeStatus(
    authoritative: WireBridgeStatus?,
    deviceName: String,
): ConnectionStatus = when (authoritative?.status) {
    null, "online" -> ConnectionStatus.Online(TransportKind.LAN_WSS, deviceName)
    "connecting" -> ConnectionStatus.Connecting
    "degraded" -> ConnectionStatus.Degraded(authoritative.reason)
    "recovery_unknown" -> ConnectionStatus.RecoveryUnknown(authoritative.reason)
    "offline" -> ConnectionStatus.RemoteOffline(authoritative.reason)
    else -> error("Unsupported bridge status: ${authoritative.status}")
}

class ConnectionRepository(
    private val settingsStore: SettingsStore,
    private val tasks: TaskRepository,
    private val keyStore: PairingKeyStore,
    private val notifications: ApprovalNotificationManager,
    private val json: Json,
    private val scope: CoroutineScope,
) {
    private val _status = MutableStateFlow<ConnectionStatus>(ConnectionStatus.Disconnected)
    val status: StateFlow<ConnectionStatus> = _status.asStateFlow()
    private val _modelCatalog = MutableStateFlow<List<ModelOption>>(emptyList())
    val modelCatalog: StateFlow<List<ModelOption>> = _modelCatalog.asStateFlow()
    private val _projects = MutableStateFlow<List<ProjectOption>>(emptyList())
    val projects: StateFlow<List<ProjectOption>> = _projects.asStateFlow()
    private var connectionJob: Job? = null
    @Volatile private var connection: PinnedWebSocketConnection? = null
    private var eventCursor = EventCursor()
    private var authenticated = false
    @Volatile private var pendingSnapshot: ProtocolEvent? = null
    private val reducerMutex = Mutex()
    private val writeMutex = Mutex()
    private var activeDeviceName = "Codex Micro"
    private val liveMessageDeltas = mutableMapOf<String, StringBuilder>()
    private var authoritativeBridgeStatus: WireBridgeStatus? = null
    @Volatile private var transientRecoveryPending = false
    @Volatile private var connectionGeneration = 0L

    suspend fun pairAndConnect(pairing: PairingInfo) {
        settingsStore.ensureClientIdentity()
        connect(pairing)
    }

    @Synchronized
    fun connect(pairing: PairingInfo) {
        val generation = ++connectionGeneration
        connectionJob?.cancel()
        activeDeviceName = pairing.deviceName
        resetProtocolState()
        connectionJob = scope.launch {
            var attempt = 0
            var connectionPairing = pairing
            while (currentCoroutineContext().isActive && generation == connectionGeneration) {
                _status.value = if (attempt == 0) ConnectionStatus.Connecting
                else ConnectionStatus.Reconnecting(attempt)
                val active = PinnedWebSocketConnection(connectionPairing, json)
                if (generation != connectionGeneration) {
                    active.close()
                    break
                }
                connection = active
                try {
                    active.receive(
                        onOpen = {
                            connectionPairing = authenticateConnection(active, connectionPairing, generation)
                        },
                        onMessage = { message ->
                            if (isCurrentConnection(generation, active)) {
                                handleMessage(message, generation, active)
                                if (_status.value is ConnectionStatus.Online) attempt = 0
                            }
                        },
                    )
                    error("Remote device closed the connection")
                } catch (error: Throwable) {
                    currentCoroutineContext().ensureActive()
                    if (!isCurrentConnection(generation, active)) break
                    if (error.hasCertificateFailure()) {
                        _status.value = ConnectionStatus.Blocked("设备证书与配对码不一致，请重新配对")
                        break
                    }
                    if (error.hasFrameLimitFailure()) {
                        _status.value = ConnectionStatus.Blocked(
                            "电脑响应超过 ${MAX_WSS_FRAME_BYTES / 1_048_576} MiB 协议上限，请缩短任务历史后重连",
                        )
                        break
                    }
                    if (error is AuthenticationRejectedException || error.hasProtocolFailure()) {
                        _status.value = ConnectionStatus.Blocked(error.message ?: "设备拒绝认证，请重新配对")
                        break
                    }
                    if (connectionPairing.pairingCode != null &&
                        connectionPairing.pairingExpiresAtEpochMs?.let { it <= System.currentTimeMillis() } != false
                    ) {
                        _status.value = ConnectionStatus.Blocked(
                            "首次配对窗口已过期。请确认电脑端桥接正在运行，然后重新打开 60 秒配对窗口扫码",
                        )
                        break
                    }
                    attempt += 1
                    resetProtocolState()
                    _status.value = ConnectionStatus.Reconnecting(attempt)
                    // Keep LAN recovery bounded while the process is runnable. Deep Doze can
                    // still defer this coroutine, so the UI continues to disclose that limit.
                    delay(min(5_000L, 1_000L shl min(attempt - 1, 3)))
                } finally {
                    if (connection === active) connection = null
                    active.close()
                }
            }
        }
    }

    @Synchronized
    fun ensureConnected(pairing: PairingInfo) {
        if (connectionJob?.isActive != true) connect(pairing)
    }

    fun reconnectNow(pairing: PairingInfo) {
        if (status.value is ConnectionStatus.Reconnecting ||
            status.value is ConnectionStatus.Disconnected ||
            status.value is ConnectionStatus.Degraded ||
            status.value is ConnectionStatus.RemoteOffline ||
            status.value is ConnectionStatus.RecoveryUnknown ||
            status.value is ConnectionStatus.Error
        ) connect(pairing)
    }

    @Synchronized
    fun disconnect(next: ConnectionStatus = ConnectionStatus.Disconnected) {
        connectionGeneration += 1
        connectionJob?.cancel()
        connectionJob = null
        connection?.close()
        connection = null
        resetProtocolState()
        _status.value = next
    }

    suspend fun resolveApproval(id: String, decision: ApprovalDecision): Result<Unit> = runCatching {
        val approved = decision in setOf(ApprovalDecision.APPROVE_ONCE, ApprovalDecision.APPROVE_SESSION)
        when (status.value) {
            is ConnectionStatus.Online -> {
                val approval = tasks.getApproval(id) ?: error("Approval no longer exists")
                val epoch = currentEpochOrThrow()
                require(approval.requestEpoch == epoch) { "Approval belongs to a stale connection epoch" }
                executeWrite(
                    ProtocolOps.APPROVAL_RESPOND,
                    "${approval.id}\u0000${approval.requestSeq}\u0000${decision.wireValue}",
                    validateResult = { result ->
                        val value = json.decodeFromJsonElement<ApprovalRespondResult>(
                            requireNotNull(result) { "Approval response result is missing" },
                        )
                        require(value.accepted && value.approvalId == approval.id) { "Approval response binding is invalid" }
                    },
                ) { writeEpoch, clientCommandId ->
                    buildJsonObject {
                        put("approvalId", approval.id)
                        put("threadId", approval.threadId)
                        put("turnId", approval.turnId)
                        put("epoch", writeEpoch)
                        put("seq", approval.requestSeq)
                        put("clientCommandId", clientCommandId)
                        put("response", buildApprovalResponse(approval, decision))
                    }
                }
            }
            else -> error("Device is not connected")
        }
        resolveApprovalLocallyIfPending(id, approved)
        notifications.cancelApproval(id)
    }

    suspend fun respondUserInput(id: String, answers: Map<String, String>): Result<Unit> = runCatching {
        val approval = tasks.getApproval(id) ?: error("Approval no longer exists")
        require(approval.approvalType == "user_input") { "Approval is not a user input request" }
        val details = json.parseToJsonElement(approval.detailsJson).jsonObject
        val questions = details["questions"]?.jsonArray.orEmpty()
        require(answers.values.any(String::isNotBlank)) { "请至少填写一项回答" }
        questions.forEach { item ->
            val question = item.jsonObject
            val questionId = question.string("questionId") ?: error("Question ID is missing")
            val required = question["required"]?.jsonPrimitive?.booleanOrNull == true
            if (required) require(!answers[questionId].isNullOrBlank()) { "请回答所有必填问题" }
        }
        val epoch = currentEpochOrThrow()
        require(approval.requestEpoch == epoch) { "Approval belongs to a stale connection epoch" }
        executeWrite(
            ProtocolOps.APPROVAL_RESPOND,
            "${approval.id}\u0000${approval.requestSeq}\u0000user_input\u0000${answers.toSortedMap()}",
            validateResult = { result ->
                val value = json.decodeFromJsonElement<ApprovalRespondResult>(
                    requireNotNull(result) { "Approval response result is missing" },
                )
                require(value.accepted && value.approvalId == approval.id) { "Approval response binding is invalid" }
            },
        ) { writeEpoch, clientCommandId ->
            buildJsonObject {
                put("clientCommandId", clientCommandId)
                put("approvalId", approval.id)
                put("threadId", approval.threadId)
                put("turnId", approval.turnId)
                put("epoch", writeEpoch)
                put("seq", approval.requestSeq)
                put("response", buildJsonObject {
                    put("type", "user_input")
                    put("answers", buildJsonObject {
                        answers.filterValues { it.isNotBlank() }.forEach { (key, value) -> put(key, value) }
                    })
                })
            }
        }
        resolveApprovalLocallyIfPending(id, true)
        notifications.cancelApproval(id)
    }

    suspend fun readTask(threadId: String, acknowledge: Boolean = true): Result<Unit> = runCatching {
        val active = requireOnlineConnection()
        val payload = json.decodeFromJsonElement<TaskReadResult>(
            requireNotNull(active.request(ProtocolOps.TASK_READ, buildJsonObject { put("threadId", threadId) })) {
                "Task read response is missing"
            },
        )
        require(payload.seq >= 1) { "Task read response sequence is invalid" }
        require(payload.task.threadId == threadId) { "Task read response binding is invalid" }
        require(payload.messages.all { it.threadId == threadId }) { "Task read messages belong to another task" }
        require(payload.approvals.all { it.threadId == threadId }) { "Task read approvals belong to another task" }
        val messages = payload.messages.map { it.toDomain() }
        var authoritativeApprovals: List<ApprovalRequest>? = null
        var authoritativeThroughMessageId: String? = null
        reducerMutex.withLock {
            require(payload.epoch == eventCursor.epoch) { "Task read response belongs to a stale epoch" }
            val existing = tasks.getTask(threadId)
            val validatedTask = payload.task.toDomain(existing)
            val decodedApprovals = payload.approvals.map { it.toDomain(validatedTask.title) }
            when {
                payload.seq == eventCursor.seq -> {
                    val lastResponse = messages.lastOrNull { it.role == "assistant" }?.text.orEmpty()
                    val task = validatedTask.copy(
                        lastResponse = lastResponse,
                        summary = lastResponse.takeIf(String::isNotBlank)?.take(MAX_MESSAGE_PREVIEW_CHARS)
                            ?: payload.task.progress.label
                            ?: existing?.summary.orEmpty(),
                    )
                    tasks.replaceTaskRead(task, messages, decodedApprovals)
                    authoritativeApprovals = decodedApprovals
                    authoritativeThroughMessageId = messages.lastOrNull()?.messageId
                }
                payload.seq < eventCursor.seq -> {
                    messages.forEach { tasks.upsertMessage(it) }
                    messages.lastOrNull { it.role == "assistant" }?.let { full ->
                        tasks.getTask(threadId)?.let { current ->
                            if (current.lastResponse.isBlank() ||
                                full.text.startsWith(current.lastResponse) ||
                                full.completedAtEpochMs >= current.updatedAtEpochMs
                            ) {
                                tasks.upsertTask(
                                    current.copy(
                                        lastResponse = full.text,
                                        summary = full.text.takeLast(MAX_MESSAGE_PREVIEW_CHARS),
                                        lastTurnId = full.turnId,
                                    ),
                                )
                            }
                        }
                    }
                }
                else -> error("Task read response sequence is ahead of the event reducer")
            }
        }
        authoritativeApprovals.orEmpty().filter { it.status == ApprovalStatus.PENDING }
            .forEach(notifications::showApproval)
        val throughMessageId = authoritativeThroughMessageId
        if (acknowledge && throughMessageId != null) {
            executeWrite(
                ProtocolOps.TASK_READ_ACK,
                "$threadId\u0000$throughMessageId",
                validateResult = { result ->
                    val value = json.decodeFromJsonElement<ReadAckResult>(
                        requireNotNull(result) { "Read acknowledgement result is missing" },
                    )
                    require(value.accepted && value.threadId == threadId && value.throughMessageId == throughMessageId) {
                        "Read acknowledgement binding is invalid"
                    }
                },
            ) { epoch, clientCommandId ->
                buildJsonObject {
                    put("threadId", threadId)
                    put("throughMessageId", throughMessageId)
                    put("epoch", epoch)
                    put("clientCommandId", clientCommandId)
                }
            }
            reducerMutex.withLock {
                if (!canApplyReadAcknowledgement(
                        readEpoch = payload.epoch,
                        readSeq = payload.seq,
                        throughMessageId = throughMessageId,
                        cursor = eventCursor,
                        latestMessageId = tasks.lastMessageId(threadId),
                    )
                ) return@withLock
                tasks.getTask(threadId)?.let { read ->
                    val stillNeedsAttention = read.status in setOf(
                        TaskStatus.WAITING_APPROVAL,
                        TaskStatus.WAITING_REPLY,
                        TaskStatus.FAILED,
                        TaskStatus.RECOVERY_UNKNOWN,
                    )
                    tasks.upsertTask(
                        read.copy(
                            status = if (read.status == TaskStatus.COMPLETED_UNREAD) TaskStatus.SUCCEEDED else read.status,
                            unread = false,
                            attention = stillNeedsAttention,
                        ),
                    )
                }
            }
        }
    }

    suspend fun sendTaskMessage(
        threadId: String,
        message: String,
        model: String?,
        reasoningEffort: String?,
    ): Result<Unit> = runCatching {
        require(message.isNotBlank()) { "Message cannot be empty" }
        val task = tasks.getTask(threadId) ?: error("Task no longer exists")
        executeWrite(
            ProtocolOps.TASK_SEND,
            listOf(threadId, task.activeTurnId.orEmpty(), message.trim(), model.orEmpty(), reasoningEffort.orEmpty())
                .joinToString("\u0000"),
            validateResult = { result ->
                val value = json.decodeFromJsonElement<TurnAcceptedResult>(
                    requireNotNull(result) { "Task send result is missing" },
                )
                require(value.accepted && value.threadId == threadId && value.turnId.isNotBlank()) {
                    "Task send response binding is invalid"
                }
                task.activeTurnId?.let { require(value.turnId == it) { "Steer response returned a different turn" } }
            },
        ) { epoch, clientCommandId ->
            buildJsonObject {
                put("threadId", threadId)
                put("text", message.trim())
                put("epoch", epoch)
                put("clientCommandId", clientCommandId)
                task.activeTurnId?.let { put("expectedTurnId", it) }
                if (task.activeTurnId == null) {
                    model?.takeIf(String::isNotBlank)?.let { put("model", it) }
                    reasoningEffort?.takeIf(String::isNotBlank)?.let { put("effort", it) }
                }
            }
        }
    }

    suspend fun interruptTask(threadId: String): Result<Unit> = runCatching {
        val turnId = tasks.getTask(threadId)?.activeTurnId ?: error("Task has no active turn")
        executeWrite(
            ProtocolOps.TASK_INTERRUPT,
            "$threadId\u0000$turnId",
            validateResult = { result ->
                val value = json.decodeFromJsonElement<TurnAcceptedResult>(
                    requireNotNull(result) { "Task interrupt result is missing" },
                )
                require(value.accepted && value.threadId == threadId && value.turnId == turnId) {
                    "Task interrupt response binding is invalid"
                }
            },
        ) { epoch, clientCommandId ->
            buildJsonObject {
                put("threadId", threadId)
                put("turnId", turnId)
                put("epoch", epoch)
                put("clientCommandId", clientCommandId)
            }
        }
    }

    suspend fun forkTask(threadId: String): Result<String?> = runCatching {
        val source = tasks.getTask(threadId) ?: error("Task no longer exists")
        val result = executeWrite(
            ProtocolOps.TASK_FORK,
            "$threadId\u0000${source.activeTurnId.orEmpty()}",
            validateResult = { result ->
                val value = json.decodeFromJsonElement<TaskResult>(
                    requireNotNull(result) { "Task fork result is missing" },
                )
                value.task.toDomain(null)
            },
        ) { epoch, clientCommandId ->
            buildJsonObject {
                put("threadId", threadId)
                put("clientCommandId", clientCommandId)
                source.activeTurnId?.let { put("turnId", it) }
                put("epoch", epoch)
            }
        }?.jsonObject
        result?.taskResultThreadId()
    }

    suspend fun createTask(
        projectId: String,
        title: String,
        prompt: String,
        model: String?,
        reasoningEffort: String?,
        slot: Int?,
    ): Result<String?> = runCatching {
        require(projectId.isNotBlank()) { "Project ID is required" }
        require(prompt.isNotBlank()) { "Initial prompt is required" }
        val result = executeWrite(
            ProtocolOps.TASK_CREATE,
            listOf(projectId.trim(), title.trim(), prompt.trim(), model.orEmpty(), reasoningEffort.orEmpty(), slot?.toString().orEmpty())
                .joinToString("\u0000"),
            validateResult = { result ->
                val value = json.decodeFromJsonElement<TaskResult>(
                    requireNotNull(result) { "Task create result is missing" },
                )
                value.task.toDomain(null)
            },
        ) { epoch, clientCommandId ->
            buildJsonObject {
                put("projectId", projectId.trim())
                put("epoch", epoch)
                put("clientCommandId", clientCommandId)
                title.takeIf(String::isNotBlank)?.let { put("title", it.trim()) }
                put("prompt", prompt.trim())
                model?.takeIf(String::isNotBlank)?.let { put("model", it) }
                reasoningEffort?.takeIf(String::isNotBlank)?.let { put("effort", it) }
                slot?.let { put("slot", it) }
            }
        }?.jsonObject
        result?.taskResultThreadId()
    }

    suspend fun assignSlot(threadId: String?, slot: Int): Result<Unit> = runCatching {
        require(slot in 1..6) { "Slot must be between 1 and 6" }
        executeWrite(
            ProtocolOps.SLOT_ASSIGN,
            "$slot\u0000${threadId.orEmpty()}",
            validateResult = { result ->
                val value = json.decodeFromJsonElement<SlotAssignResult>(
                    requireNotNull(result) { "Slot assignment result is missing" },
                )
                require(value.accepted && value.slot == slot && value.threadId == threadId) {
                    "Slot assignment response binding is invalid"
                }
            },
        ) { epoch, clientCommandId ->
            buildJsonObject {
                if (threadId == null) put("threadId", JsonNull) else put("threadId", threadId)
                put("slot", slot)
                put("epoch", epoch)
                put("clientCommandId", clientCommandId)
            }
        }
    }

    private suspend fun authenticateConnection(
        active: PinnedWebSocketConnection,
        pairing: PairingInfo,
        generation: Long,
    ): PairingInfo {
        try {
                val identity = settingsStore.ensureClientIdentity()
                var authenticatedPairing = pairing
                if (pairing.pairingCode != null) {
                    completePairing(active, pairing, identity.first, identity.second)
                    ensureCurrentConnection(generation, active)
                    authenticatedPairing = pairing.copy(
                        pairingCode = null,
                        serverNonce = null,
                        pairingExpiresAtEpochMs = null,
                    )
                    settingsStore.savePairing(authenticatedPairing)
                    settingsStore.setKeepConnected(true)
                } else {
                    authenticatePairedDevice(active, pairing, identity.first)
                }
                ensureCurrentConnection(generation, active)
                authenticated = true
                pendingSnapshot?.also { pendingSnapshot = null }?.let {
                    handleEvent(it, generation, active)
                }
                return authenticatedPairing
        } catch (error: RemoteProtocolException) {
            if (isBlockingAuthenticationError(error.remote.code)) {
                throw AuthenticationRejectedException(error.remote.message, error)
            }
            throw error
        }
    }

    private suspend fun completePairing(
        active: PinnedWebSocketConnection,
        pairing: PairingInfo,
        clientDeviceId: String,
        displayName: String,
    ) {
        pairing.pairingExpiresAtEpochMs?.let {
            if (it <= System.currentTimeMillis()) throw AuthenticationRejectedException("Pairing code expired")
        }
        val info = if (pairing.serverNonce == null) {
            active.request("pairing.info", JsonObject(emptyMap()))?.jsonObject
                ?: error("Pairing information is missing")
        } else null
        val pairingWindow = info?.elementIgnoringCase("pairing") as? JsonObject
        val serverNonceText = pairing.serverNonce
            ?: pairingWindow?.stringIgnoringCase("serverNonce")
            ?: error("Pairing nonce is missing")
        pairingWindow?.stringIgnoringCase("expiresAt")?.let { expiresAt ->
            if (Instant.parse(expiresAt).toEpochMilli() <= System.currentTimeMillis()) {
                throw AuthenticationRejectedException("Pairing window expired")
            }
        }
        val fingerprint = info?.stringIgnoringCase("certSpkiSha256") ?: pairing.spkiSha256
        if (SpkiPinningTrustManager.canonicalPin(fingerprint) !=
            SpkiPinningTrustManager.canonicalPin(pairing.spkiSha256)
        ) throw AuthenticationRejectedException("Pairing fingerprint changed")
        val serverNonce = decodeBase64Url(serverNonceText)
        val clientNonce = ByteArray(32).also(SecureRandom()::nextBytes)
        val payload = keyStore.createPairingPayload(clientDeviceId, serverNonce, clientNonce, fingerprint)
        val result = active.request(
            "pairing.complete",
            buildJsonObject {
                put("code", pairing.pairingCode ?: error("Pairing code is missing"))
                put("deviceId", clientDeviceId)
                put("displayName", displayName)
                put("clientPublicKeySpki", keyStore.publicKeySpkiBase64())
                put("clientNonce", Base64.getUrlEncoder().withoutPadding().encodeToString(clientNonce))
                put("signatureDer", keyStore.signDerBase64Url(payload))
            },
        )?.jsonObject ?: error("Pairing response is missing")
        if (result["authenticated"]?.jsonPrimitive?.booleanOrNull != true) {
            throw AuthenticationRejectedException("Pairing was not authenticated")
        }
    }

    private suspend fun authenticatePairedDevice(
        active: PinnedWebSocketConnection,
        pairing: PairingInfo,
        clientDeviceId: String,
    ) {
        val challenge = active.request(
            "auth.challenge",
            buildJsonObject { put("deviceId", clientDeviceId) },
        )?.jsonObject ?: error("Authentication challenge is missing")
        val challengeId = challenge.stringIgnoringCase("challengeId") ?: error("Challenge ID is missing")
        val serverNonce = decodeBase64Url(challenge.stringIgnoringCase("serverNonce") ?: error("Challenge nonce is missing"))
        val fingerprint = challenge.stringIgnoringCase("certificateFingerprint") ?: error("Challenge fingerprint is missing")
        if (SpkiPinningTrustManager.canonicalPin(fingerprint) !=
            SpkiPinningTrustManager.canonicalPin(pairing.spkiSha256)
        ) throw AuthenticationRejectedException("Authentication fingerprint changed")
        val payload = keyStore.createAuthenticationPayload(challengeId, clientDeviceId, serverNonce, fingerprint)
        val result = active.request(
            "auth.complete",
            buildJsonObject {
                put("challengeId", challengeId)
                put("signatureDer", keyStore.signDerBase64Url(payload))
            },
        )?.jsonObject ?: error("Authentication response is missing")
        if (result["authenticated"]?.jsonPrimitive?.booleanOrNull != true) {
            throw AuthenticationRejectedException("Authentication failed")
        }
    }

    private suspend fun handleMessage(
        message: IncomingMessage,
        generation: Long,
        active: PinnedWebSocketConnection,
    ) {
        if (message !is IncomingMessage.Event) return
        try {
            handleEvent(message.value, generation, active)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            if (!isCurrentConnection(generation, active)) return
            reducerMutex.withLock {
                if (!eventCursor.waitingForSnapshot &&
                    eventCursor.epoch == message.value.epoch &&
                    message.value.seq > eventCursor.seq
                ) {
                    eventCursor = eventCursor.copy(seq = message.value.seq)
                }
            }
            // A single malformed or forward-compatible business event must not tear down an
            // otherwise healthy pinned WSS connection.  Advance/recover from task.read instead
            // of reconnecting forever on the same desktop state.
            _status.value = ConnectionStatus.Degraded(
                "收到一条无法应用的状态更新，连接仍保持；正在自动同步完整状态",
            )
            transientRecoveryPending = true
            scheduleAuthoritativeRefresh(generation, active)
        }
    }

    private suspend fun handleEvent(
        envelope: ProtocolEvent,
        generation: Long,
        active: PinnedWebSocketConnection,
    ) = reducerMutex.withLock {
        if (!isCurrentConnection(generation, active)) return@withLock
        require(envelope.v == PROTOCOL_VERSION) { "Unsupported event protocol version" }
        require(envelope.event in SUPPORTED_EVENTS) { "Unsupported protocol event: ${envelope.event}" }
        if (envelope.event == ProtocolEvents.SNAPSHOT) {
            if (!authenticated) {
                pendingSnapshot = envelope
                return@withLock
            }
            val snapshot = json.decodeFromJsonElement<SnapshotData>(envelope.data)
            applySnapshot(snapshot, envelope.epoch, envelope.seq)
            eventCursor = eventCursor.acceptSnapshot(envelope.epoch, envelope.seq)
            tasks.deletePendingCommandsOutsideEpoch(envelope.epoch)
            authoritativeBridgeStatus = snapshot.bridge
            transientRecoveryPending = false
            _status.value = authoritativeBridgeStatus!!.toConnectionStatus()
            scheduleAuthoritativeRefresh(generation, active)
            return@withLock
        }
        if (!authenticated) return@withLock
        when (val decision = eventCursor.reduce(envelope.epoch, envelope.seq)) {
            CursorDecision.WaitForSnapshot -> return@withLock
            CursorDecision.EpochChanged -> {
                eventCursor = EventCursor()
                _status.value = ConnectionStatus.Reconnecting(1)
                connection?.close()
                return@withLock
            }
            CursorDecision.Ignore -> return@withLock
            is CursorDecision.Gap -> {
                eventCursor = decision.cursor
                _status.value = ConnectionStatus.Degraded(
                    "状态更新缺少 ${decision.missingCount} 条，连接仍保持；正在自动校准",
                )
                transientRecoveryPending = true
                scheduleAuthoritativeRefresh(generation, active)
            }
            is CursorDecision.Accept -> eventCursor = decision.cursor
        }
        when (envelope.event) {
            ProtocolEvents.BRIDGE_STATUS -> {
                val bridge = json.decodeFromJsonElement<BridgeStatusData>(envelope.data)
                require(bridge.status in CANONICAL_BRIDGE_STATUSES) { "Unsupported bridge status: ${bridge.status}" }
                authoritativeBridgeStatus = WireBridgeStatus(bridge.status, bridge.reason)
                transientRecoveryPending = false
                _status.value = authoritativeBridgeStatus!!.toConnectionStatus()
            }
            ProtocolEvents.TASK_STATE -> json.decodeFromJsonElement<TaskStateData>(envelope.data).task.let { wire ->
                val existing = tasks.getTask(wire.threadId)
                tasks.upsertTask(wire.toDomain(existing))
            }
            ProtocolEvents.TASK_PLAN_UPDATED -> json.decodeFromJsonElement<TaskPlanUpdatedData>(envelope.data).let { update ->
                val threadId = update.threadId
                val existing = tasks.getTask(threadId) ?: return@let
                if (existing.activeTurnId != update.turnId) return@let
                val steps = update.steps.map { it.toDomain() }
                tasks.upsertTask(
                    existing.copy(
                        plan = steps,
                        reportedProgress = if (steps.isEmpty()) {
                            com.codexmicro.mobile.domain.ProgressKind.Unknown
                        } else com.codexmicro.mobile.domain.ProgressKind.PlanSteps(
                            steps.count { it.state == PlanStepState.COMPLETED },
                            steps.size,
                        ),
                    ),
                )
            }
            ProtocolEvents.APPROVAL_REQUESTED -> {
                val wire = json.decodeFromJsonElement<ApprovalRequestedData>(envelope.data).approval
                require(wire.epoch == envelope.epoch && wire.seq == envelope.seq) {
                    "Approval request binding does not match its event envelope"
                }
                val approval = wire.toDomain()
                tasks.upsertApproval(approval)
                notifications.showApproval(approval)
            }
            ProtocolEvents.APPROVAL_RESOLVED -> json.decodeFromJsonElement<ApprovalResolvedData>(envelope.data).let { update ->
                val id = update.approvalId
                val pending = tasks.getApproval(id)
                require(
                    approvalResolutionMatches(
                        eventEpoch = envelope.epoch,
                        updateEpoch = update.epoch,
                        updateSeq = update.seq,
                        updateThreadId = update.threadId,
                        updateTurnId = update.turnId,
                        pending = pending,
                    ),
                ) {
                    "Approval resolution does not match the pending request"
                }
                val resolved = when (update.resolution) {
                    "approved" -> ApprovalStatus.APPROVED
                    "declined", "cancelled" -> ApprovalStatus.REJECTED
                    "expired" -> ApprovalStatus.EXPIRED
                    else -> ApprovalStatus.RESOLVED
                }
                pending?.let { tasks.upsertApproval(it.copy(status = resolved)) }
                notifications.cancelApproval(id)
            }
            ProtocolEvents.TASK_ERROR -> json.decodeFromJsonElement<TaskErrorData>(envelope.data).let { update ->
                val id = update.threadId
                tasks.getTask(id)?.let { task ->
                    tasks.upsertTask(
                        task.copy(
                            status = if (update.recoverable) {
                                TaskStatus.RECOVERY_UNKNOWN
                            } else TaskStatus.FAILED,
                            summary = update.message,
                            attention = true,
                        ),
                    )
                }
            }
            ProtocolEvents.TASK_MESSAGE_DELTA -> {
                val update = json.decodeFromJsonElement<TaskMessageDeltaData>(envelope.data)
                require(update.channel in CANONICAL_MESSAGE_CHANNELS) { "Unsupported message channel: ${update.channel}" }
                val messageId = update.messageId
                val threadId = update.threadId
                val delta = update.delta
                val preview = liveMessageDeltas.getOrPut(messageId, ::StringBuilder).append(delta)
                    .takeLast(MAX_MESSAGE_PREVIEW_CHARS).toString()
                tasks.getTask(threadId)?.let { task ->
                    tasks.upsertTask(task.copy(summary = preview))
                }
            }
            ProtocolEvents.TASK_MESSAGE_COMPLETED -> {
                val message = json.decodeFromJsonElement<TaskMessageCompletedData>(envelope.data).message.toDomain()
                liveMessageDeltas.remove(message.messageId)
                tasks.upsertMessage(message)
                if (message.role == "assistant") {
                    tasks.getTask(message.threadId)?.let { task ->
                        tasks.upsertTask(
                            task.copy(
                                summary = message.text.takeLast(MAX_MESSAGE_PREVIEW_CHARS),
                                lastResponse = message.text,
                                lastTurnId = message.turnId,
                                updatedAtEpochMs = message.completedAtEpochMs,
                            ),
                        )
                    }
                }
            }
        }
    }

    private fun scheduleAuthoritativeRefresh(
        generation: Long,
        active: PinnedWebSocketConnection,
    ) {
        scope.launch {
            // Let the reducer release its mutex before task.read applies its authoritative row.
            delay(100)
            if (!isCurrentConnection(generation, active)) return@launch
            tasks.tasks.first().map(TaskItem::id).forEach { threadId ->
                if (!isCurrentConnection(generation, active)) return@launch
                if (readTask(threadId, acknowledge = false).isFailure) return@launch
            }
            if (isCurrentConnection(generation, active) && transientRecoveryPending) {
                transientRecoveryPending = false
                _status.value = restoreAuthoritativeBridgeStatus(authoritativeBridgeStatus, activeDeviceName)
            }
        }
    }

    private suspend fun applySnapshot(snapshot: SnapshotData, epoch: String, seq: Long) {
        require(snapshot.slots.size == 6 && snapshot.slots.map { it.slot }.toSet() == (1..6).toSet()) {
            "Snapshot must contain each slot from 1 through 6 exactly once"
        }
        require(snapshot.slots.mapNotNull { it.threadId }.distinct().size == snapshot.slots.count { it.threadId != null }) {
            "A task cannot occupy more than one snapshot slot"
        }
        require(snapshot.tasks.size <= 6 && snapshot.tasks.map { it.threadId }.distinct().size == snapshot.tasks.size) {
            "Snapshot tasks must be unique and limited to six"
        }
        val taskIds = snapshot.tasks.map { it.threadId }.toSet()
        require(snapshot.slots.mapNotNull { it.threadId }.all { it in taskIds }) {
            "Snapshot slots may only refer to included tasks"
        }
        require(snapshot.approvals.all { approval ->
            approval.threadId in taskIds && approval.epoch == epoch && approval.seq in 1L..seq
        }) { "Snapshot approval binding is invalid" }
        require(snapshot.bridge.status in CANONICAL_BRIDGE_STATUSES) {
            "Unsupported snapshot bridge status: ${snapshot.bridge.status}"
        }
        _modelCatalog.value = snapshot.modelCatalog.map {
            ModelOption(
                it.id,
                it.displayName,
                it.supportedReasoningEfforts,
                null,
                it.default,
            )
        }
        _projects.value = snapshot.projectCatalog.map { ProjectOption(it.projectId, it.displayName, it.path.orEmpty()) }
        val slotByThread = snapshot.slots.mapNotNull { row -> row.threadId?.let { it to row.slot } }.toMap()
        val taskRows = snapshot.tasks.map { wire ->
            val existing = tasks.getTask(wire.threadId)
            wire.toDomain(existing).copy(slot = slotByThread[wire.threadId])
        }
        val approvalRows = mutableListOf<ApprovalRequest>()
        val titleByThread = taskRows.associate { it.id to it.title }
        snapshot.approvals.forEach { approvalRows += it.toDomain(titleByThread[it.threadId]) }
        notifications.cancelAllApprovals()
        tasks.replaceSnapshot(taskRows, approvalRows)
        approvalRows.forEach { approval ->
            if (approval.status == ApprovalStatus.PENDING) notifications.showApproval(approval)
        }
    }

    private fun WireTask.toDomain(existing: TaskItem? = null): TaskItem {
        require(threadId.isNotBlank() && title.isNotBlank()) { "Task identity is invalid" }
        require(status in CANONICAL_TASK_STATUSES) { "Unsupported task status: $status" }
        require(lastMessagePreview == null || lastMessagePreview.length <= 500) { "Task preview is too long" }
        require(plan.all { it.status in CANONICAL_PLAN_STATUSES }) { "Task plan contains an invalid status" }
        val mappedStatus = mapCanonicalTaskStatus(status, attention)
        val mappedPlan = plan.map { it.toDomain() }
        val mappedProgress = when (progress.kind) {
            "plan_steps" -> {
                require(mappedPlan.isNotEmpty()) { "Plan step progress requires at least one plan step" }
                val completed = mappedPlan.count { it.state == PlanStepState.COMPLETED }
                require(progress.completedSteps == completed && progress.totalSteps == mappedPlan.size) {
                    "Plan progress does not match authoritative plan steps"
                }
                require(progress.label == null && progress.source == null) { "Plan progress has unexpected fields" }
                com.codexmicro.mobile.domain.ProgressKind.PlanSteps(completed, mappedPlan.size)
            }
            "indeterminate" -> {
                require(!progress.label.isNullOrBlank() && isCanonicalProgressSource(progress.source)) {
                    "Indeterminate progress is missing its source or label"
                }
                require(progress.completedSteps == null && progress.totalSteps == null) {
                    "Indeterminate progress cannot contain step counts"
                }
                com.codexmicro.mobile.domain.ProgressKind.Indeterminate(progress.label)
            }
            "unknown" -> {
                require(
                    progress.label == null && progress.source == null &&
                        progress.completedSteps == null && progress.totalSteps == null,
                ) { "Unknown progress cannot contain derived progress fields" }
                com.codexmicro.mobile.domain.ProgressKind.Unknown
            }
            else -> error("Unsupported progress kind: ${progress.kind}")
        }
        return TaskItem(
            id = threadId,
            title = title.ifBlank { threadId },
            workspace = _projects.value.firstOrNull { it.id == projectId }?.path
                ?: existing?.workspace.orEmpty(),
            projectId = projectId,
            summary = lastMessagePreview ?: progress.label.orEmpty(),
            status = mappedStatus,
            plan = mappedPlan,
            transport = TransportKind.LAN_WSS,
            updatedAtEpochMs = Instant.parse(updatedAt).toEpochMilli(),
            activeTurnId = activeTurnId,
            lastTurnId = existing?.lastTurnId,
            slot = existing?.slot,
            unread = mappedStatus == TaskStatus.COMPLETED_UNREAD,
            attention = attention,
            pinned = existing?.pinned == true,
            reportedProgress = mappedProgress,
            lastResponse = preserveCompleteResponse(existing?.lastResponse, lastMessagePreview),
        )
    }

    private fun WireMessage.toDomain() = TaskMessage(
        messageId = messageId,
        threadId = threadId,
        turnId = turnId,
        itemId = itemId,
        role = role.also { require(it in CANONICAL_MESSAGE_ROLES) { "Unsupported message role: $it" } },
        text = text,
        completedAtEpochMs = Instant.parse(completedAt).toEpochMilli(),
    )

    private fun WireBridgeStatus.toConnectionStatus(): ConnectionStatus =
        when (status) {
            "online" -> ConnectionStatus.Online(TransportKind.LAN_WSS, activeDeviceName)
            "connecting" -> ConnectionStatus.Connecting
            "degraded" -> ConnectionStatus.Degraded(reason)
            "recovery_unknown" -> ConnectionStatus.RecoveryUnknown(reason)
            "offline" -> ConnectionStatus.RemoteOffline(reason)
            else -> error("Unsupported bridge status: $status")
        }

    private suspend fun WireApproval.toDomain(taskTitleOverride: String? = null): ApprovalRequest {
        validateApprovalDetails()
        val task = tasks.getTask(threadId)
        val reason = details.string("reason") ?: summary
        val preview = details.string("command")?.take(400)
            ?: details["paths"]?.toString()?.take(400)
            ?: ""
        return ApprovalRequest(
            id = approvalId,
            taskId = threadId,
            threadId = threadId,
            turnId = turnId,
            taskTitle = taskTitleOverride ?: task?.title ?: threadId,
            title = title,
            reason = reason,
            commandPreview = preview,
            status = ApprovalStatus.PENDING,
            requestedAtEpochMs = Instant.parse(requestedAt).toEpochMilli(),
            expiresAtEpochMs = System.currentTimeMillis() + 10 * 60_000L,
            requestEpoch = epoch,
            requestSeq = seq,
            approvalType = approvalType,
            detailsJson = details.toString(),
        )
    }

    private fun WireApproval.validateApprovalDetails() {
        require(seq >= 1 && approvalId.isNotBlank() && threadId.isNotBlank() && turnId.isNotBlank()) {
            "Approval binding is invalid"
        }
        when (approvalType) {
            "command" -> json.decodeFromJsonElement<CommandApprovalDetails>(details).also { value ->
                require(value.type == "command" && value.command.isNotBlank() && value.cwd.isNotBlank() && value.reason.isNotBlank()) {
                    "Command approval details are invalid"
                }
                validateDecisions(value.allowedDecisions)
            }
            "file_change" -> json.decodeFromJsonElement<FileChangeApprovalDetails>(details).also { value ->
                require(value.type == "file_change" && value.itemId.isNotBlank() && value.grantRoot.isNotBlank()) {
                    "File approval details are invalid"
                }
                require(value.paths == null || value.paths.isNotEmpty() && value.paths.distinct().size == value.paths.size) {
                    "File approval paths are invalid"
                }
                validateDecisions(value.allowedDecisions)
            }
            "permission" -> json.decodeFromJsonElement<PermissionApprovalDetails>(details).also { value ->
                require(value.type == "permission" && value.cwd.isNotBlank()) { "Permission approval details are invalid" }
                require(value.allowedScopes.isNotEmpty() && value.allowedScopes.distinct().size == value.allowedScopes.size &&
                    value.allowedScopes.all { it in setOf("once", "session") }) { "Permission scopes are invalid" }
                require(value.requested.filesystem.all { it.permissionId.isNotBlank() && it.path.isNotBlank() && it.access in setOf("read", "write") }) {
                    "Filesystem permissions are invalid"
                }
                require(value.requested.network.permissionId.isNotBlank()) { "Network permission ID is missing" }
                require(value.requested.network.targets.all {
                    it.host.isNotBlank() && it.protocol in setOf("tcp", "udp", "http", "https") &&
                        (it.port == null || it.port in 1..65535)
                }) { "Network permission targets are invalid" }
            }
            "user_input" -> json.decodeFromJsonElement<UserInputApprovalDetails>(details).also { value ->
                require(value.type == "user_input" && value.questions.size in 1..20) { "User input questions are invalid" }
                require(value.questions.map { it.questionId }.distinct().size == value.questions.size && value.questions.all {
                    it.questionId.isNotBlank() && it.prompt.isNotBlank() && it.options.size <= 20
                }) { "User input questions are invalid" }
            }
            else -> error("Unsupported approval type: $approvalType")
        }
    }

    private fun validateDecisions(decisions: List<String>) {
        require(decisions.isNotEmpty() && decisions.distinct().size == decisions.size &&
            decisions.all { it in CANONICAL_APPROVAL_DECISIONS }) { "Approval decisions are invalid" }
    }

    private fun com.codexmicro.mobile.network.WirePlanStep.toDomain() = PlanStep(
        id = stepId,
        title = text,
        state = when (status) {
            "completed" -> PlanStepState.COMPLETED
            "in_progress" -> PlanStepState.IN_PROGRESS
            else -> PlanStepState.PENDING
        },
    )

    private fun JsonObject.string(name: String): String? = this[name]?.jsonPrimitive?.contentOrNull
    private fun JsonObject.elementIgnoringCase(name: String): JsonElement? =
        entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value

    private fun JsonObject.stringIgnoringCase(name: String): String? =
        elementIgnoringCase(name)?.jsonPrimitive?.contentOrNull

    private suspend fun resolveApprovalLocallyIfPending(id: String, approved: Boolean) {
        if (tasks.getApproval(id)?.status == ApprovalStatus.PENDING) {
            tasks.resolveApproval(id, approved)
        }
    }

    private fun buildApprovalResponse(approval: ApprovalRequest, decision: ApprovalDecision): JsonObject {
        val details = json.parseToJsonElement(approval.detailsJson).jsonObject
        return when (approval.approvalType) {
            "command", "file_change" -> {
                val allowed = details["allowedDecisions"]?.jsonArray.orEmpty()
                    .mapNotNull { it.jsonPrimitive.contentOrNull }
                require(decision.wireValue in allowed) { "Requested decision is not offered by the approval" }
                buildJsonObject {
                    put("type", approval.approvalType)
                    put("decision", decision.wireValue)
                }
            }
            "permission" -> {
                val scopes = details["allowedScopes"]?.jsonArray.orEmpty()
                    .mapNotNull { it.jsonPrimitive.contentOrNull }
                val requestedScope = if (decision == ApprovalDecision.APPROVE_SESSION) "session" else "once"
                val scope = scopes.firstOrNull { it == requestedScope } ?: scopes.firstOrNull()
                    ?: error("Approval has no allowed permission scope")
                val requested = details["requested"]?.jsonObject
                val ids = if (decision in setOf(ApprovalDecision.APPROVE_ONCE, ApprovalDecision.APPROVE_SESSION)) {
                    val filesystem = requested?.get("filesystem")?.jsonArray.orEmpty().mapNotNull { item ->
                        item.jsonObject.string("permissionId")
                    }
                    val network = requested?.get("network")?.jsonObject?.let { value ->
                        if (value["enabled"]?.jsonPrimitive?.booleanOrNull == true) value.string("permissionId") else null
                    }
                    filesystem + listOfNotNull(network)
                } else emptyList()
                buildJsonObject {
                    put("type", "permission")
                    put("granted", JsonArray(ids.map(::JsonPrimitive)))
                    put("scope", scope)
                }
            }
            "user_input" -> error("User input requests must be answered in their question form")
            else -> error("Unsupported approval type")
        }
    }

    private fun decodeBase64Url(value: String): ByteArray {
        val padded = value + "=".repeat((4 - value.length % 4) % 4)
        return Base64.getUrlDecoder().decode(padded)
    }

    private suspend fun executeWrite(
        operation: String,
        semanticKey: String,
        validateResult: (JsonElement?) -> Unit = { requireNotNull(it) { "Write response result is missing" } },
        buildParams: (epoch: String, clientCommandId: String) -> JsonObject,
    ): JsonElement? = writeMutex.withLock {
        val active = requireOnlineConnection()
        val epoch = currentEpochOrThrow()
        val actionKey = commandActionKey(epoch, operation, semanticKey)
        val existing = tasks.getPendingCommand(actionKey)
        val pending = if (existing != null) existing else {
            val commandId = UUID.randomUUID().toString()
            val params = buildParams(epoch, commandId)
            PendingCommand(
                actionKey = actionKey,
                commandId = commandId,
                epoch = epoch,
                operation = operation,
                paramsJson = params.toString(),
                state = "pending",
                createdAtEpochMs = System.currentTimeMillis(),
            ).also { tasks.savePendingCommand(it) }
        }
        check(pending.epoch == epoch && pending.operation == operation) { "Pending command identity is inconsistent" }
        val params = json.parseToJsonElement(pending.paramsJson).jsonObject
        try {
            active.request(operation, params).also {
                validateResult(it)
                tasks.deletePendingCommand(actionKey)
            }
        } catch (error: Throwable) {
            if (error is RemoteProtocolException && !error.remote.retryable) {
                tasks.deletePendingCommand(actionKey)
            } else {
                tasks.markPendingCommandUncertain(actionKey)
            }
            throw error
        }
    }

    private fun commandActionKey(epoch: String, operation: String, semanticKey: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest("$epoch\u0000$operation\u0000$semanticKey".toByteArray(Charsets.UTF_8))
        return Base64.getUrlEncoder().withoutPadding().encodeToString(digest)
    }

    private fun requireOnlineConnection(): PinnedWebSocketConnection {
        check(status.value is ConnectionStatus.Online || status.value is ConnectionStatus.Degraded) {
            "Device is not connected"
        }
        return connection ?: error("Connection was interrupted")
    }

    private fun isCurrentConnection(generation: Long, active: PinnedWebSocketConnection): Boolean =
        generation == connectionGeneration && connection === active

    private fun ensureCurrentConnection(generation: Long, active: PinnedWebSocketConnection) {
        if (!isCurrentConnection(generation, active)) throw CancellationException("Connection attempt was superseded")
    }

    private fun currentEpochOrThrow(): String = eventCursor.epoch?.takeIf(String::isNotBlank)
        ?: error("No authoritative snapshot epoch is available")

    private fun resetProtocolState() {
        eventCursor = EventCursor()
        authenticated = false
        pendingSnapshot = null
        liveMessageDeltas.clear()
        authoritativeBridgeStatus = null
        transientRecoveryPending = false
    }

    private fun Throwable.hasCertificateFailure(): Boolean = generateSequence(this) { it.cause }
        .any { it is CertificateException || it.message?.contains("identity does not match") == true }

    private fun Throwable.hasProtocolFailure(): Boolean = generateSequence(this) { it.cause }.any {
        it is kotlinx.serialization.SerializationException ||
            it.message?.contains("Unsupported protocol", ignoreCase = true) == true ||
            it.message?.contains("Protocol response", ignoreCase = true) == true
    }

    private fun Throwable.hasFrameLimitFailure(): Boolean = generateSequence(this) { it.cause }.any {
        it.message?.contains("frame", ignoreCase = true) == true &&
            (it.message?.contains("too big", ignoreCase = true) == true ||
                it.message?.contains("max", ignoreCase = true) == true)
    }

    private companion object {
        const val MAX_MESSAGE_PREVIEW_CHARS = 400
        val SUPPORTED_EVENTS = setOf(
            ProtocolEvents.SNAPSHOT,
            ProtocolEvents.BRIDGE_STATUS,
            ProtocolEvents.TASK_STATE,
            ProtocolEvents.TASK_MESSAGE_DELTA,
            ProtocolEvents.TASK_MESSAGE_COMPLETED,
            ProtocolEvents.TASK_PLAN_UPDATED,
            ProtocolEvents.APPROVAL_REQUESTED,
            ProtocolEvents.APPROVAL_RESOLVED,
            ProtocolEvents.TASK_ERROR,
        )
        val CANONICAL_TASK_STATUSES = setOf(
            "idle", "running", "waiting_input", "waiting_approval", "completed", "error", "interrupted", "recovery_unknown",
        )
        val CANONICAL_PLAN_STATUSES = setOf("pending", "in_progress", "completed")
        val CANONICAL_MESSAGE_ROLES = setOf("user", "assistant", "system", "tool")
        val CANONICAL_MESSAGE_CHANNELS = setOf("assistant", "reasoning", "tool")
        val CANONICAL_APPROVAL_DECISIONS = setOf("approve_once", "approve_session", "decline", "cancel")
        val CANONICAL_BRIDGE_STATUSES = setOf("offline", "connecting", "online", "degraded", "recovery_unknown")
    }
}

private class AuthenticationRejectedException(message: String, cause: Throwable? = null) :
    SecurityException(message, cause)
