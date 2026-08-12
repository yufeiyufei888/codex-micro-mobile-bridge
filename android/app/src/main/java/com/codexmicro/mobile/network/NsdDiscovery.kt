package com.codexmicro.mobile.network

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class DiscoveredHost(val name: String, val host: String, val port: Int)

class NsdDiscovery(context: Context) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
    private val _hosts = MutableStateFlow<List<DiscoveredHost>>(emptyList())
    val hosts: StateFlow<List<DiscoveredHost>> = _hosts.asStateFlow()
    private val _running = MutableStateFlow(false)
    val running: StateFlow<Boolean> = _running.asStateFlow()

    private var listener: NsdManager.DiscoveryListener? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    @Synchronized
    fun start() {
        if (listener != null) return
        _hosts.value = emptyList()
        multicastLock = wifi?.createMulticastLock("codexmicro_nsd")?.apply {
            setReferenceCounted(false)
            acquire()
        }
        val next = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) { _running.value = true }
            override fun onDiscoveryStopped(serviceType: String) { finish() }
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { finish() }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) { finish() }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                _hosts.value = _hosts.value.filterNot { it.name == serviceInfo.serviceName }
            }
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (!serviceInfo.serviceType.startsWith(SERVICE_TYPE)) return
                @Suppress("DEPRECATION")
                nsd.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit
                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        @Suppress("DEPRECATION")
                        val address = serviceInfo.host?.hostAddress ?: return
                        val item = DiscoveredHost(serviceInfo.serviceName, address, serviceInfo.port)
                        _hosts.value = (_hosts.value.filterNot { it.name == item.name } + item)
                            .sortedBy { it.name.lowercase() }
                    }
                })
            }
        }
        listener = next
        runCatching { nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, next) }
            .onFailure { finish() }
    }

    @Synchronized
    fun stop() {
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        finish()
    }

    @Synchronized
    private fun finish() {
        listener = null
        _running.value = false
        multicastLock?.let { if (it.isHeld) it.release() }
        multicastLock = null
    }

    companion object { const val SERVICE_TYPE = "_codexmicro._tcp." }
}
