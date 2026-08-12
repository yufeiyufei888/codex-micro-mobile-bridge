package com.codexmicro.mobile.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Approval
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.ViewModule
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import com.codexmicro.mobile.ui.screens.ApprovalCenterScreen
import com.codexmicro.mobile.ui.screens.ConversationHistoryScreen
import com.codexmicro.mobile.ui.screens.PairingScreen
import com.codexmicro.mobile.ui.screens.SettingsScreen
import com.codexmicro.mobile.ui.screens.TaskDetailScreen
import com.codexmicro.mobile.ui.screens.TaskGridScreen

@Composable
fun CodexMicroApp(
    state: MobileUiState,
    hasCameraPermission: Boolean,
    hasNotificationPermission: Boolean,
    onAction: (MobileAction) -> Unit,
    onRequestCamera: () -> Unit,
    onRequestNotifications: () -> Unit,
    onSetKeepConnected: (Boolean) -> Unit,
    onOpenSystemSettings: () -> Unit,
) {
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(state.message) {
        state.message?.let {
            snackbar.showSnackbar(it)
            onAction(MobileAction.DismissMessage)
        }
    }
    val topLevel = state.destination in setOf(Destination.TASKS, Destination.APPROVALS, Destination.SETTINGS)
    if (!topLevel) BackHandler { onAction(MobileAction.Back) }

    Scaffold(
        topBar = {
            if (state.busy) LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            if (topLevel) {
                NavigationBar {
                    NavigationBarItem(
                        selected = state.destination == Destination.TASKS,
                        onClick = { onAction(MobileAction.Navigate(Destination.TASKS)) },
                        icon = { Icon(Icons.Rounded.ViewModule, contentDescription = null) },
                        label = { Text("桌面") },
                    )
                    NavigationBarItem(
                        selected = state.destination == Destination.APPROVALS,
                        onClick = { onAction(MobileAction.Navigate(Destination.APPROVALS)) },
                        icon = {
                            BadgedBox(badge = {
                                if (state.pendingApprovals > 0) Badge { Text(state.pendingApprovals.toString()) }
                            }) { Icon(Icons.Rounded.Approval, contentDescription = null) }
                        },
                        label = { Text("确认") },
                    )
                    NavigationBarItem(
                        selected = state.destination == Destination.SETTINGS,
                        onClick = { onAction(MobileAction.Navigate(Destination.SETTINGS)) },
                        icon = { Icon(Icons.Rounded.Settings, contentDescription = null) },
                        label = { Text("设置") },
                    )
                }
            }
        },
    ) { innerPadding ->
        val modifier = Modifier.padding(innerPadding)
        when (state.destination) {
            Destination.TASKS -> TaskGridScreen(
                tasks = state.tasks,
                models = state.models,
                projects = state.projects,
                connection = state.connection,
                busy = state.busy,
                onOpenTask = { onAction(MobileAction.OpenTask(it)) },
                onCreateTask = { project, title, prompt, model, effort, slot ->
                    onAction(MobileAction.CreateTask(project, title, prompt, model, effort, slot))
                },
                onAssignSlot = { taskId, slot -> onAction(MobileAction.AssignSlot(taskId, slot)) },
                onClearSlot = { slot -> onAction(MobileAction.ClearSlot(slot)) },
                onTogglePinned = { taskId, pinned -> onAction(MobileAction.TogglePinned(taskId, pinned)) },
                onPair = { onAction(MobileAction.OpenPairing) },
                modifier = modifier,
            )
            Destination.APPROVALS -> ApprovalCenterScreen(
                approvals = state.approvals,
                busy = state.busy,
                onResolve = { id, decision -> onAction(MobileAction.ResolveApproval(id, decision)) },
                onRespondUserInput = { id, answers -> onAction(MobileAction.RespondUserInput(id, answers)) },
                modifier = modifier,
            )
            Destination.SETTINGS -> SettingsScreen(
                settings = state.settings,
                hasCameraPermission = hasCameraPermission,
                hasNotificationPermission = hasNotificationPermission,
                onSetDemo = { onAction(MobileAction.SetDemo(it)) },
                onSetKeepConnected = onSetKeepConnected,
                onOpenPairing = { onAction(MobileAction.OpenPairing) },
                onResetDemo = { onAction(MobileAction.ResetDemo) },
                onUnpair = { onAction(MobileAction.Unpair) },
                onRequestCamera = onRequestCamera,
                onRequestNotifications = onRequestNotifications,
                onOpenSystemSettings = onOpenSystemSettings,
                modifier = modifier,
            )
            Destination.PAIRING -> PairingScreen(
                hasCameraPermission = hasCameraPermission,
                discoveredHosts = state.discoveredHosts,
                discoveryRunning = state.discoveryRunning,
                onRequestCamera = onRequestCamera,
                onToggleDiscovery = { onAction(MobileAction.ToggleDiscovery) },
                onPairCode = { onAction(MobileAction.PairFromCode(it)) },
                onPairManual = { name, host, port, pin, code ->
                    onAction(MobileAction.PairManually(name, host, port, pin, code))
                },
                onBack = { onAction(MobileAction.Back) },
                modifier = modifier,
            )
            Destination.TASK_DETAIL -> TaskDetailScreen(
                task = state.selectedTask,
                models = state.models,
                online = state.connection is com.codexmicro.mobile.domain.ConnectionStatus.Online,
                busy = state.busy,
                onSend = { message, model, effort ->
                    state.selectedTaskId?.let {
                        onAction(MobileAction.SendTaskMessage(it, message, model, effort))
                    }
                },
                onInterrupt = {
                    state.selectedTaskId?.let { onAction(MobileAction.InterruptTask(it)) }
                },
                onFork = {
                    state.selectedTaskId?.let { onAction(MobileAction.ForkTask(it)) }
                },
                onOpenApprovals = { onAction(MobileAction.OpenApprovals) },
                historyCount = state.selectedMessages.size,
                onOpenHistory = { onAction(MobileAction.OpenConversationHistory) },
                onAssignSlot = { slot ->
                    state.selectedTaskId?.let { onAction(MobileAction.AssignSlot(it, slot)) }
                },
                onClearSlot = { slot -> onAction(MobileAction.ClearSlot(slot)) },
                onBack = { onAction(MobileAction.Back) },
                modifier = modifier,
            )
            Destination.CONVERSATION_HISTORY -> ConversationHistoryScreen(
                task = state.selectedTask,
                messages = state.selectedMessages,
                onBack = { onAction(MobileAction.Back) },
                modifier = modifier,
            )
        }
    }
}
