package com.codexmicro.mobile.ui.components

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Approval
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.Error
import androidx.compose.material.icons.rounded.HourglassTop
import androidx.compose.material.icons.rounded.PauseCircle
import androidx.compose.material.icons.rounded.PlayCircle
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import com.codexmicro.mobile.domain.ConnectionStatus
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.ui.theme.Amber300
import com.codexmicro.mobile.ui.theme.AmberContainer
import com.codexmicro.mobile.ui.theme.Blue300
import com.codexmicro.mobile.ui.theme.BlueContainer
import com.codexmicro.mobile.ui.theme.Emerald300
import com.codexmicro.mobile.ui.theme.EmeraldContainer
import com.codexmicro.mobile.ui.theme.Rose300
import com.codexmicro.mobile.ui.theme.RoseContainer
import com.codexmicro.mobile.ui.theme.Slate300
import com.codexmicro.mobile.ui.theme.SlateContainer

data class StatusVisual(
    val label: String,
    val color: Color,
    val containerColor: Color,
    val icon: ImageVector,
)

@Composable
fun TaskStatus.visual(): StatusVisual = when (this) {
    TaskStatus.UNASSIGNED -> StatusVisual("空槽位", Slate300, SlateContainer, Icons.Rounded.HourglassTop)
    TaskStatus.QUEUED -> StatusVisual("排队中", Slate300, SlateContainer, Icons.Rounded.HourglassTop)
    TaskStatus.IDLE -> StatusVisual("空闲", Slate300, SlateContainer, Icons.Rounded.PauseCircle)
    TaskStatus.WORKING -> StatusVisual("执行中", Blue300, BlueContainer, Icons.Rounded.PlayCircle)
    TaskStatus.WAITING_APPROVAL -> StatusVisual("待审批", Amber300, AmberContainer, Icons.Rounded.Approval)
    TaskStatus.WAITING_REPLY -> StatusVisual("待回复", Amber300, AmberContainer, Icons.Rounded.Approval)
    TaskStatus.COMPLETED_UNREAD -> StatusVisual("完成 · 未读", Emerald300, EmeraldContainer, Icons.Rounded.CheckCircle)
    TaskStatus.SUCCEEDED -> StatusVisual("已完成", Emerald300, EmeraldContainer, Icons.Rounded.CheckCircle)
    TaskStatus.INTERRUPTED -> StatusVisual("已中断", Slate300, SlateContainer, Icons.Rounded.PauseCircle)
    TaskStatus.FAILED -> StatusVisual("失败", Rose300, RoseContainer, Icons.Rounded.Error)
    TaskStatus.RECOVERY_UNKNOWN -> StatusVisual("状态待恢复", Slate300, SlateContainer, Icons.Rounded.Refresh)
    TaskStatus.PAUSED -> StatusVisual("已暂停", Amber300, AmberContainer, Icons.Rounded.PauseCircle)
}

@Composable
fun ConnectionStatus.visual(): StatusVisual = when (this) {
    ConnectionStatus.Disconnected -> StatusVisual("未连接", Slate300, SlateContainer, Icons.Rounded.CloudOff)
    ConnectionStatus.Discovering -> StatusVisual("发现设备", Blue300, BlueContainer, Icons.Rounded.Sync)
    ConnectionStatus.Connecting -> StatusVisual("正在连接", Blue300, BlueContainer, Icons.Rounded.Sync)
    is ConnectionStatus.Online -> StatusVisual("已连接 · $deviceName", Emerald300, EmeraldContainer, Icons.Rounded.CloudDone)
    is ConnectionStatus.Degraded -> StatusVisual("电脑服务降级", Amber300, AmberContainer, Icons.Rounded.Error)
    is ConnectionStatus.RecoveryUnknown -> StatusVisual("连接状态待恢复", Rose300, RoseContainer, Icons.Rounded.Error)
    is ConnectionStatus.RemoteOffline -> StatusVisual("电脑服务离线", Slate300, SlateContainer, Icons.Rounded.CloudOff)
    is ConnectionStatus.Reconnecting -> StatusVisual("重连中 · 第 $attempt 次", Amber300, AmberContainer, Icons.Rounded.Refresh)
    is ConnectionStatus.Blocked -> StatusVisual("连接已阻断", Rose300, RoseContainer, Icons.Rounded.Error)
    is ConnectionStatus.Error -> StatusVisual("连接异常", Rose300, RoseContainer, Icons.Rounded.Error)
}
