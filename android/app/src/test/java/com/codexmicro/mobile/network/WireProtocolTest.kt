package com.codexmicro.mobile.network

import kotlinx.serialization.SerializationException
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class WireProtocolTest {
    private val json = Json { ignoreUnknownKeys = false; explicitNulls = true }

    @Test
    fun decodesCanonicalTaskEventAndRejectsUnknownDataField() {
        val raw = taskStateEvent()
        val event = json.decodeFromString<ProtocolEvent>(raw)
        val data = json.decodeFromJsonElement<TaskStateData>(event.data)
        assertEquals("thread-1", data.task.threadId)
        assertEquals("project-1", data.task.projectId)
        assertEquals("step-1", data.task.plan.single().stepId)

        val withUnknown = raw.replace("\"task\":{", "\"unexpected\":true,\"task\":{")
        val unknownEvent = json.decodeFromString<ProtocolEvent>(withUnknown)
        assertThrows(SerializationException::class.java) {
            json.decodeFromJsonElement<TaskStateData>(unknownEvent.data)
        }
    }

    @Test
    fun strictResponseRejectsUnknownTopLevelField() {
        assertThrows(SerializationException::class.java) {
            json.decodeFromString<ProtocolResponse>(
                """{"v":1,"id":"request-1","result":{"accepted":true},"extra":1}""",
            )
        }
    }

    @Test
    fun extractsThreadIdFromNestedCreateAndForkResult() {
        val nested = json.parseToJsonElement(
            """{"task":{"threadId":"thread-new","projectId":null,"title":"New","status":"idle","activeTurnId":null,"attention":false,"progress":{"kind":"unknown"},"plan":[],"lastMessagePreview":null,"updatedAt":"2026-08-10T00:00:00Z"}}""",
        ).jsonObject
        assertEquals("thread-new", nested.taskResultThreadId())
        assertNull(json.parseToJsonElement("{}").jsonObject.taskResultThreadId())
    }

    @Test
    fun websocketFrameLimitFitsTheLargestSingleCanonicalMessage() {
        assertEquals(1_048_576L, MAX_WSS_FRAME_BYTES)
        assertTrue(MAX_WSS_FRAME_BYTES > 200_000L)
    }

    private fun taskStateEvent() =
        """{"v":1,"epoch":"epoch-123456789012","seq":2,"event":"task.state","data":{"task":{"threadId":"thread-1","projectId":"project-1","title":"Build Android","status":"running","activeTurnId":"turn-1","attention":false,"progress":{"kind":"plan_steps","completedSteps":0,"totalSteps":1},"plan":[{"stepId":"step-1","text":"Compile","status":"in_progress"}],"lastMessagePreview":"Working","updatedAt":"2026-08-10T00:00:00Z"}}}"""
}
