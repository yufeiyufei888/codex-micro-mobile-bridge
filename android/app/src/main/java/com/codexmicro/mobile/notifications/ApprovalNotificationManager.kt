package com.codexmicro.mobile.notifications

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import com.codexmicro.mobile.MainActivity
import com.codexmicro.mobile.R
import com.codexmicro.mobile.domain.ApprovalRequest

class ApprovalNotificationManager(private val context: Context) {
    private val manager = context.getSystemService(NotificationManager::class.java)

    fun createChannels() {
        manager.createNotificationChannels(
            listOf(
                NotificationChannel(CONNECTION_CHANNEL, "设备连接", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "Codex Micro 局域网连接状态"
                },
                NotificationChannel(APPROVAL_CHANNEL, "等待审批", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "需要你确认的 Codex 操作"
                    lockscreenVisibility = Notification.VISIBILITY_PRIVATE
                },
            ),
        )
    }

    fun showApproval(approval: ApprovalRequest) {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.POST_NOTIFICATIONS,
            ) != PackageManager.PERMISSION_GRANTED
        ) return

        val open = PendingIntent.getActivity(
            context,
            approval.id.hashCode(),
            Intent(context, MainActivity::class.java).putExtra(EXTRA_APPROVAL_ID, approval.id),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = Notification.Builder(context, APPROVAL_CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("等待审批 · ${approval.taskTitle}")
            .setContentText("有一项待处理请求，请在应用中查看详情")
            .setContentIntent(open)
            .setAutoCancel(true)
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .setCategory(Notification.CATEGORY_RECOMMENDATION)
        val notification = builder.build()
        runCatching { manager.notify(approval.id.hashCode(), notification) }
    }

    fun connectionNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            context,
            7,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(context, CONNECTION_CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Codex Micro")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .build()
    }

    fun cancelApproval(id: String) = manager.cancel(id.hashCode())

    fun cancelAllApprovals() {
        manager.activeNotifications
            .filter { it.notification.channelId == APPROVAL_CHANNEL }
            .forEach { manager.cancel(it.id) }
    }

    companion object {
        const val CONNECTION_CHANNEL = "connection"
        const val APPROVAL_CHANNEL = "approval"
        const val EXTRA_APPROVAL_ID = "approval_id"
    }
}
