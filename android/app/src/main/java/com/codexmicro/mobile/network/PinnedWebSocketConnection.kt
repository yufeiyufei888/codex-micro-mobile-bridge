package com.codexmicro.mobile.network

import com.codexmicro.mobile.domain.PairingInfo
import com.codexmicro.mobile.security.SpkiPinningTrustManager
import io.ktor.client.HttpClient
import io.ktor.client.engine.cio.CIO
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.WebSockets
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.client.request.url
import io.ktor.http.URLProtocol
import io.ktor.http.encodedPath
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import io.ktor.websocket.send
import java.io.Closeable
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

internal const val MAX_WSS_FRAME_BYTES = 1_048_576L

class PinnedWebSocketConnection(
    private val pairing: PairingInfo,
    private val json: Json,
) : Closeable {
    private val client = HttpClient(CIO) {
        engine {
            https { trustManager = SpkiPinningTrustManager(pairing.spkiSha256, pairing.host) }
        }
        install(WebSockets) {
            pingIntervalMillis = 20_000
            maxFrameSize = MAX_WSS_FRAME_BYTES
        }
    }
    private val sendMutex = Mutex()
    private val pending = ConcurrentHashMap<String, CompletableDeferred<ProtocolResponse>>()
    @Volatile private var session: DefaultClientWebSocketSession? = null

    suspend fun receive(onOpen: suspend () -> Unit, onMessage: suspend (IncomingMessage) -> Unit) {
        val active = client.webSocketSession {
            url {
                protocol = URLProtocol.WSS
                host = pairing.host
                port = pairing.port
                encodedPath = pairing.path
            }
        }
        session = active
        try {
            coroutineScope {
                launch { onOpen() }
                for (frame in active.incoming) {
                    if (frame !is Frame.Text) continue
                    val root = json.parseToJsonElement(frame.readText()) as? JsonObject
                        ?: error("Protocol message must be a JSON object")
                    require(root["v"]?.toString() == PROTOCOL_VERSION.toString()) {
                        "Unsupported protocol version"
                    }
                    val message = if ("event" in root) {
                        IncomingMessage.Event(json.decodeFromJsonElement<ProtocolEvent>(root))
                    } else {
                        val response = json.decodeFromJsonElement<ProtocolResponse>(root)
                        require((response.result != null) xor (response.error != null)) {
                            "Protocol response must contain exactly one of result or error"
                        }
                        IncomingMessage.Response(response)
                    }
                    if (message is IncomingMessage.Response) {
                        pending.remove(message.value.id)?.complete(message.value) ?: onMessage(message)
                    } else onMessage(message)
                }
            }
        } finally {
            session = null
            runCatching { active.close() }
        }
    }

    suspend fun request(op: String, params: JsonObject, timeoutMillis: Long = 15_000): JsonElement? {
        val id = UUID.randomUUID().toString()
        val waiter = CompletableDeferred<ProtocolResponse>()
        pending[id] = waiter
        try {
            send(ProtocolRequest(id = id, op = op, params = params))
            val response = withTimeout(timeoutMillis) { waiter.await() }
            response.error?.let { throw RemoteProtocolException(it) }
            return response.result
        } finally {
            pending.remove(id)
        }
    }

    private suspend fun send(command: ProtocolRequest) = sendMutex.withLock {
        val active = session ?: error("Connection is not ready")
        active.send(Frame.Text(json.encodeToString(command)))
    }

    override fun close() {
        pending.values.forEach { it.cancel() }
        pending.clear()
        session = null
        client.close()
    }
}
