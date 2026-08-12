package com.codexmicro.mobile.service

import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import com.codexmicro.mobile.CodexMicroApplication
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

class ConnectionForegroundService : Service() {
    private val container get() = (application as CodexMicroApplication).container

    override fun onCreate() {
        super.onCreate()
        container.notifications.createChannels()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            container.connectionRepository.disconnect()
            stopSelf()
            return START_NOT_STICKY
        }
        val notification = container.notifications.connectionNotification("保持局域网连接；深度省电时可能延迟")
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        container.appScope.launch {
            container.settingsStore.settings.first().pairing?.let(container.connectionRepository::ensureConnected)
        }
        return START_STICKY
    }

    override fun onDestroy() {
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val ACTION_STOP = "com.codexmicro.mobile.STOP_CONNECTION"
        const val NOTIFICATION_ID = 41
    }
}
