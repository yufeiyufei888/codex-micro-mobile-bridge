package com.codexmicro.mobile.ui

import android.app.Application
import android.content.Intent
import androidx.core.content.ContextCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.codexmicro.mobile.AppContainer
import com.codexmicro.mobile.data.settings.SettingsSnapshot
import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalDecision
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.domain.ConnectionStatus
import com.codexmicro.mobile.domain.PairingInfo
import com.codexmicro.mobile.domain.ModelOption
import com.codexmicro.mobile.domain.ProjectOption
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import com.codexmicro.mobile.network.DiscoveredHost
import com.codexmicro.mobile.network.PairingPayload
import com.codexmicro.mobile.security.SpkiPinningTrustManager
import com.codexmicro.mobile.service.ConnectionForegroundService
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

enum class Destination { TASKS, APPROVALS, SETTINGS, PAIRING, TASK_DETAIL, CONVERSATION_HISTORY }

data class MobileUiState(
    val destination: Destination = Destination.TASKS,
    val tasks: List<TaskItem> = emptyList(),
    val approvals: List<ApprovalRequest> = emptyList(),
    val connection: ConnectionStatus = ConnectionStatus.Disconnected,
    val settings: SettingsSnapshot = SettingsSnapshot(),
    val discoveredHosts: List<DiscoveredHost> = emptyList(),
    val discoveryRunning: Boolean = false,
    val selectedTaskId: String? = null,
    val selectedMessages: List<TaskMessage> = emptyList(),
    val message: String? = null,
    val models: List<ModelOption> = emptyList(),
    val projects: List<ProjectOption> = emptyList(),
    val busy: Boolean = false,
) {
    val selectedTask: TaskItem? get() = tasks.firstOrNull { it.id == selectedTaskId }
    val pendingApprovals: Int get() = approvals.count { it.status == ApprovalStatus.PENDING }
}

sealed interface MobileAction {
    data class Navigate(val destination: Destination) : MobileAction
    data class OpenTask(val id: String) : MobileAction
    data object OpenConversationHistory : MobileAction
    data object Back : MobileAction
    data object OpenPairing : MobileAction
    data class PairFromCode(val raw: String) : MobileAction
    data class PairManually(
        val deviceName: String,
        val host: String,
        val port: String,
        val pin: String,
        val pairingCode: String,
    ) : MobileAction
    data object ToggleDiscovery : MobileAction
    data class ResolveApproval(val id: String, val decision: ApprovalDecision) : MobileAction
    data class RespondUserInput(val id: String, val answers: Map<String, String>) : MobileAction
    data class SendTaskMessage(
        val taskId: String,
        val message: String,
        val model: String?,
        val reasoningEffort: String?,
    ) : MobileAction
    data class CreateTask(
        val projectId: String,
        val title: String,
        val prompt: String,
        val model: String?,
        val reasoningEffort: String?,
        val slot: Int?,
    ) : MobileAction
    data class InterruptTask(val taskId: String) : MobileAction
    data class ForkTask(val taskId: String) : MobileAction
    data class AssignSlot(val taskId: String, val slot: Int) : MobileAction
    data class ClearSlot(val slot: Int) : MobileAction
    data class TogglePinned(val taskId: String, val pinned: Boolean) : MobileAction
    data object OpenApprovals : MobileAction
    data class SetKeepConnected(val enabled: Boolean) : MobileAction
    data object Unpair : MobileAction
    data object DismissMessage : MobileAction
}

class CodexMicroViewModel(
    private val application: Application,
    private val container: AppContainer,
) : ViewModel() {
    private data class Core(
        val tasks: List<TaskItem>,
        val approvals: List<ApprovalRequest>,
        val connection: ConnectionStatus,
        val settings: SettingsSnapshot,
    )
    private data class Navigation(val destination: Destination, val taskId: String?)

    private val navigation = MutableStateFlow(Navigation(Destination.TASKS, null))
    private val message = MutableStateFlow<String?>(null)
    private val busy = MutableStateFlow(false)
    private val selectedMessages = MutableStateFlow<List<TaskMessage>>(emptyList())
    private var messageObservation: Job? = null
    private val core = combine(
        container.taskRepository.tasks,
        container.taskRepository.approvals,
        container.connectionRepository.status,
        container.settingsStore.settings,
    ) { tasks, approvals, connection, settings -> Core(tasks, approvals, connection, settings) }
    private val catalog = combine(
        container.connectionRepository.modelCatalog,
        container.connectionRepository.projects,
    ) { models, projects -> models to projects }
    private data class Feedback(val notice: String?, val working: Boolean, val messages: List<TaskMessage>)
    private val feedback = combine(message, busy, selectedMessages) { notice, working, messages ->
        Feedback(notice, working, messages)
    }
    private val discovery = combine(
        container.nsdDiscovery.hosts,
        container.nsdDiscovery.running,
    ) { hosts, running -> hosts to running }

    val uiState: StateFlow<MobileUiState> = combine(core, catalog, discovery, navigation, feedback) {
            coreState, catalogState, discoveryState, nav, feedbackState ->
        MobileUiState(
            destination = nav.destination,
            tasks = coreState.tasks,
            approvals = coreState.approvals,
            connection = coreState.connection,
            settings = coreState.settings,
            discoveredHosts = discoveryState.first,
            discoveryRunning = discoveryState.second,
            selectedTaskId = nav.taskId,
            selectedMessages = feedbackState.messages,
            message = feedbackState.notice,
            busy = feedbackState.working,
            models = catalogState.first,
            projects = catalogState.second,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), MobileUiState())

    fun onAction(action: MobileAction) {
        when (action) {
            is MobileAction.Navigate -> {
                container.nsdDiscovery.stop()
                navigation.value = Navigation(action.destination, null)
                messageObservation?.cancel()
                selectedMessages.value = emptyList()
            }
            is MobileAction.OpenTask -> {
                navigation.value = Navigation(Destination.TASK_DETAIL, action.id)
                observeMessages(action.id)
                if (container.connectionRepository.status.value is ConnectionStatus.Online) {
                    viewModelScope.launch { container.connectionRepository.readTask(action.id) }
                }
            }
            MobileAction.OpenConversationHistory -> {
                val taskId = navigation.value.taskId ?: return
                navigation.value = Navigation(Destination.CONVERSATION_HISTORY, taskId)
                observeMessages(taskId)
            }
            MobileAction.Back -> {
                container.nsdDiscovery.stop()
                val current = navigation.value
                if (current.destination == Destination.CONVERSATION_HISTORY && current.taskId != null) {
                    navigation.value = Navigation(Destination.TASK_DETAIL, current.taskId)
                } else {
                    navigation.value = Navigation(Destination.TASKS, null)
                    messageObservation?.cancel()
                    selectedMessages.value = emptyList()
                }
            }
            MobileAction.OpenPairing -> navigation.value = Navigation(Destination.PAIRING, null)
            is MobileAction.PairFromCode -> pairFromCode(action.raw)
            is MobileAction.PairManually -> pairManually(action)
            MobileAction.ToggleDiscovery -> if (container.nsdDiscovery.running.value) {
                container.nsdDiscovery.stop()
            } else container.nsdDiscovery.start()
            is MobileAction.ResolveApproval -> resolveApproval(action.id, action.decision)
            is MobileAction.RespondUserInput -> taskOperation("回答已提交") {
                container.connectionRepository.respondUserInput(action.id, action.answers)
            }
            is MobileAction.SendTaskMessage -> taskOperation("已发送到电脑端当前 Codex 对话") {
                container.connectionRepository.sendTaskMessage(
                    action.taskId,
                    action.message,
                    action.model,
                    action.reasoningEffort,
                ).map { Unit }
            }
            is MobileAction.CreateTask -> taskOperation("任务创建请求已发送") {
                container.connectionRepository.createTask(
                    action.projectId,
                    action.title,
                    action.prompt,
                    action.model,
                    action.reasoningEffort,
                    action.slot,
                ).map { Unit }
            }
            is MobileAction.InterruptTask -> taskOperation("已发送停止请求") {
                container.connectionRepository.interruptTask(action.taskId)
            }
            is MobileAction.ForkTask -> taskOperation("已创建续作任务") {
                container.connectionRepository.forkTask(action.taskId).map { Unit }
            }
            is MobileAction.AssignSlot -> taskOperation("已更新槽位 ${action.slot}") {
                container.connectionRepository.assignSlot(action.taskId, action.slot)
            }
            is MobileAction.ClearSlot -> taskOperation("已清空槽位 ${action.slot}") {
                container.connectionRepository.assignSlot(null, action.slot)
            }
            is MobileAction.TogglePinned -> taskOperation(if (action.pinned) "已固定任务" else "已取消固定") {
                runCatching {
                    check(container.taskRepository.setPinned(action.taskId, action.pinned)) { "任务已不存在" }
                }
            }
            MobileAction.OpenApprovals -> {
                navigation.value = Navigation(Destination.APPROVALS, null)
                messageObservation?.cancel()
                selectedMessages.value = emptyList()
            }
            is MobileAction.SetKeepConnected -> setKeepConnected(action.enabled)
            MobileAction.Unpair -> viewModelScope.launch {
                stopForegroundConnection()
                container.connectionRepository.disconnect()
                container.settingsStore.clearPairing()
                container.notifications.cancelAllApprovals()
                message.value = "已解除配对"
            }
            MobileAction.DismissMessage -> message.value = null
        }
    }

    fun openApproval(id: String?) {
        navigation.value = Navigation(Destination.APPROVALS, null)
        messageObservation?.cancel()
        selectedMessages.value = emptyList()
        if (id != null) message.value = "已打开审批请求"
    }

    private fun observeMessages(taskId: String) {
        messageObservation?.cancel()
        messageObservation = viewModelScope.launch {
            container.taskRepository.messages(taskId).collectLatest { selectedMessages.value = it }
        }
    }

    private fun pairFromCode(raw: String) {
        PairingPayload.parse(raw, container.wireJson)
            .onSuccess(::finishPairing)
            .onFailure { message.value = it.message ?: "配对码无法识别" }
    }

    private fun pairManually(action: MobileAction.PairManually) {
        val candidate = runCatching {
            PairingInfo(
                hostId = "manual-${UUID.nameUUIDFromBytes(action.host.trim().toByteArray()).toString().take(12)}",
                deviceName = action.deviceName.trim().ifBlank { "Codex Micro" },
                host = action.host.trim(),
                port = action.port.toInt(),
                path = "/v1/mobile",
                spkiSha256 = action.pin.trim().also { SpkiPinningTrustManager.canonicalPin(it) },
                pairingCode = action.pairingCode.trim(),
                pairingExpiresAtEpochMs = System.currentTimeMillis() + MANUAL_PAIRING_WINDOW_MS,
            ).also {
                require(it.host.isNotBlank() && !it.host.contains("://")) { "请输入主机名或 IP，不要包含协议" }
                require(it.port in 1..65535) { "端口无效" }
                require(it.pairingCode?.matches(Regex("^[0-9]{6}$")) == true) { "配对码必须是 6 位数字" }
            }
        }
        candidate.onSuccess(::finishPairing)
            .onFailure { message.value = it.message ?: "配对信息无效" }
    }

    private fun finishPairing(pairing: PairingInfo) {
        viewModelScope.launch {
            container.nsdDiscovery.stop()
            container.connectionRepository.pairAndConnect(pairing)
            navigation.value = Navigation(Destination.TASKS, null)
            message.value = "正在验证设备身份；成功后才会保存配对"
        }
    }

    private fun resolveApproval(id: String, decision: ApprovalDecision) {
        val successMessage = when (decision) {
            ApprovalDecision.APPROVE_ONCE -> "已批准本次操作"
            ApprovalDecision.APPROVE_SESSION -> "已批准本次会话"
            ApprovalDecision.DECLINE -> "已拒绝"
            ApprovalDecision.CANCEL -> "已取消"
        }
        taskOperation(successMessage) { container.connectionRepository.resolveApproval(id, decision) }
    }

    private fun taskOperation(successMessage: String, block: suspend () -> Result<Unit>) {
        if (busy.value) return
        viewModelScope.launch {
            busy.value = true
            try {
                block().onSuccess { message.value = successMessage }
                    .onFailure { message.value = operationFailureMessage(it) }
            } finally {
                busy.value = false
            }
        }
    }

    private fun setKeepConnected(enabled: Boolean) {
        viewModelScope.launch {
            container.settingsStore.setKeepConnected(enabled)
            if (enabled) {
                ContextCompat.startForegroundService(
                    application,
                    Intent(application, ConnectionForegroundService::class.java),
                )
            } else stopForegroundConnection()
        }
    }

    fun ensureContinuousConnection() {
        if (uiState.value.settings.keepConnected && uiState.value.settings.pairing != null) {
            ContextCompat.startForegroundService(
                application,
                Intent(application, ConnectionForegroundService::class.java),
            )
        }
    }

    private fun stopForegroundConnection() {
        application.stopService(Intent(application, ConnectionForegroundService::class.java))
    }

    override fun onCleared() {
        container.nsdDiscovery.stop()
        super.onCleared()
    }

    companion object {
        private const val MANUAL_PAIRING_WINDOW_MS = 60_000L

        fun factory(application: Application, container: AppContainer): ViewModelProvider.Factory =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>): T =
                    CodexMicroViewModel(application, container) as T
            }
    }
}

internal fun operationFailureMessage(error: Throwable): String =
    if (error is CancellationException) {
        "连接正在同步状态，请以桌面执行状态为准；若未执行再重试"
    } else {
        error.message ?: "操作未完成，请重试"
    }
