package com.codexmicro.mobile.ui.screens

import com.codexmicro.mobile.domain.TaskStatus
import org.junit.Assert.assertFalse
import org.junit.Test

class TaskDetailPresentationTest {
    @Test
    fun noStatusCardRepeatsConversationBody() {
        TaskStatus.entries.forEach { status -> assertFalse(status.showsDetailStatusBody()) }
    }
}
