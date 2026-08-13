package com.codexmicro.mobile.data.settings

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.codexmicro.mobile.domain.PairingInfo
import java.io.IOException
import java.security.SecureRandom
import java.util.Base64
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.map

private val Context.settingsDataStore by preferencesDataStore(name = "settings")

data class SettingsSnapshot(
    val keepConnected: Boolean = false,
    val pairing: PairingInfo? = null,
    val clientDeviceId: String = "",
    val clientDisplayName: String = "Codex Micro Android",
)

class SettingsStore(private val context: Context) {
    val settings: Flow<SettingsSnapshot> = context.settingsDataStore.data
        .catch { error ->
            if (error is IOException) emit(androidx.datastore.preferences.core.emptyPreferences())
            else throw error
        }
        .map { values ->
            val host = values[Keys.host]
            val pin = values[Keys.pin]
            val pairing = if (!host.isNullOrBlank() && !pin.isNullOrBlank()) {
                PairingInfo(
                    hostId = values[Keys.hostId].orEmpty(),
                    deviceName = values[Keys.deviceName] ?: "Codex Micro",
                    host = host,
                    port = values[Keys.port] ?: 47127,
                    path = values[Keys.path] ?: "/v1/mobile",
                    spkiSha256 = pin,
                )
            } else null
            SettingsSnapshot(
                // Existing paired installations are upgraded to the reliable LAN default.
                // The user can still turn continuous monitoring off explicitly in Settings.
                keepConnected = values[Keys.keepConnected] ?: (pairing != null),
                pairing = pairing,
                clientDeviceId = values[Keys.clientDeviceId].orEmpty(),
                clientDisplayName = values[Keys.clientDisplayName] ?: "Codex Micro Android",
            )
        }

    suspend fun savePairing(pairing: PairingInfo) {
        context.settingsDataStore.edit { values ->
            values[Keys.hostId] = pairing.hostId
            values[Keys.deviceName] = pairing.deviceName
            values[Keys.host] = pairing.host
            values[Keys.port] = pairing.port
            values[Keys.path] = pairing.path
            values[Keys.pin] = pairing.spkiSha256
            values.remove(Keys.pairingCode)
            values.remove(Keys.serverNonce)
            values.remove(Keys.pairingExpiresAt)
            values.remove(Keys.obsoleteModeKey)
            values[Keys.keepConnected] = true
        }
    }

    suspend fun clearPairing() {
        context.settingsDataStore.edit { values ->
            values.remove(Keys.hostId)
            values.remove(Keys.deviceName)
            values.remove(Keys.host)
            values.remove(Keys.port)
            values.remove(Keys.path)
            values.remove(Keys.pin)
            values.remove(Keys.pairingCode)
            values.remove(Keys.serverNonce)
            values.remove(Keys.pairingExpiresAt)
            values[Keys.keepConnected] = false
        }
    }

    suspend fun setKeepConnected(enabled: Boolean) {
        context.settingsDataStore.edit { it[Keys.keepConnected] = enabled }
    }

    suspend fun ensureClientIdentity(): Pair<String, String> {
        var deviceId = ""
        var displayName = "Codex Micro Android"
        context.settingsDataStore.edit { values ->
            // V2 no longer exposes the old local showcase mode.
            values.remove(Keys.obsoleteModeKey)
            deviceId = values[Keys.clientDeviceId] ?: buildDeviceId().also {
                values[Keys.clientDeviceId] = it
            }
            displayName = values[Keys.clientDisplayName] ?: "Codex Micro Android".also {
                values[Keys.clientDisplayName] = it
            }
        }
        return deviceId to displayName
    }

    suspend fun clearPairingSecrets() {
        context.settingsDataStore.edit { values ->
            values.remove(Keys.pairingCode)
            values.remove(Keys.serverNonce)
            values.remove(Keys.pairingExpiresAt)
        }
    }

    private fun buildDeviceId(): String {
        val random = ByteArray(18).also(SecureRandom()::nextBytes)
        return "android-${Base64.getUrlEncoder().withoutPadding().encodeToString(random)}"
    }

    private object Keys {
        val obsoleteModeKey = booleanPreferencesKey("demo_enabled")
        val keepConnected = booleanPreferencesKey("keep_connected")
        val hostId = stringPreferencesKey("paired_host_id")
        val deviceName = stringPreferencesKey("paired_device_name")
        val host = stringPreferencesKey("paired_host")
        val port = intPreferencesKey("paired_port")
        val path = stringPreferencesKey("paired_path")
        val pin = stringPreferencesKey("paired_spki_sha256")
        val pairingCode = stringPreferencesKey("pairing_code")
        val serverNonce = stringPreferencesKey("pairing_server_nonce")
        val pairingExpiresAt = androidx.datastore.preferences.core.longPreferencesKey("pairing_expires_at")
        val clientDeviceId = stringPreferencesKey("client_device_id")
        val clientDisplayName = stringPreferencesKey("client_display_name")
    }
}
