package com.codexmicro.mobile

import android.app.Application
import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import androidx.room.Room
import com.codexmicro.mobile.data.ConnectionRepository
import com.codexmicro.mobile.data.RoomTaskRepository
import com.codexmicro.mobile.data.TaskRepository
import com.codexmicro.mobile.data.local.CodexMicroDatabase
import com.codexmicro.mobile.data.local.MIGRATION_1_2
import com.codexmicro.mobile.data.local.MIGRATION_2_3
import com.codexmicro.mobile.data.local.MIGRATION_3_4
import com.codexmicro.mobile.data.settings.SettingsStore
import com.codexmicro.mobile.network.NsdDiscovery
import com.codexmicro.mobile.notifications.ApprovalNotificationManager
import com.codexmicro.mobile.security.PairingKeyStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json

class CodexMicroApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
        container.notifications.createChannels()
        val connectivity = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        connectivity.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                container.appScope.launch {
                    val settings = container.settingsStore.settings.first()
                    if (settings.keepConnected) {
                        settings.pairing?.let(container.connectionRepository::reconnectNow)
                    }
                }
            }
        })
        container.appScope.launch {
            container.settingsStore.ensureClientIdentity()
            val settings = container.settingsStore.settings.first()
            settings.pairing?.let(container.connectionRepository::ensureConnected)
                ?: container.connectionRepository.disconnect()
        }
    }
}

class AppContainer(application: Application) {
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val wireJson = Json {
        ignoreUnknownKeys = false
        explicitNulls = true
        encodeDefaults = true
    }
    private val localJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val database = Room.databaseBuilder(
        application,
        CodexMicroDatabase::class.java,
        "codex-micro.db",
    ).addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4).build()
    val settingsStore = SettingsStore(application)
    val taskRepository: TaskRepository = RoomTaskRepository(database, localJson)
    val notifications = ApprovalNotificationManager(application)
    val keyStore = PairingKeyStore()
    val nsdDiscovery = NsdDiscovery(application)
    val connectionRepository = ConnectionRepository(
        settingsStore,
        taskRepository,
        keyStore,
        notifications,
        wireJson,
        appScope,
    )
}
