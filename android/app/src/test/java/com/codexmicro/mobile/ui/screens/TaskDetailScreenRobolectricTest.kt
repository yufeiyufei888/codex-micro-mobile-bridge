package com.codexmicro.mobile.ui.screens

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.junit4.StateRestorationTester
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind
import com.codexmicro.mobile.ui.theme.CodexMicroTheme
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TaskDetailScreenRobolectricTest {
    @get:Rule
    val compose = createComposeRule()

    @Test
    fun completeReplyAppearsOnceAndHistoryCardIsActionable() {
        var historyOpened = false
        val reply = "这是唯一应显示在最近回复卡片中的完整正文。"

        compose.setContent {
            CodexMicroTheme {
                screen(task(reply), historyCount = 4, onOpenHistory = { historyOpened = true })
            }
        }

        compose.onAllNodesWithText(reply, useUnmergedTree = true).assertCountEquals(1)
        compose.onNodeWithText("查看对话（4 条）").performClick()
        compose.runOnIdle { assertTrue(historyOpened) }
    }

    @Test
    fun draftMessageSurvivesSavedStateRestoration() {
        val restoration = StateRestorationTester(compose)
        restoration.setContent {
            CodexMicroTheme { screen(task(""), historyCount = 0) }
        }

        compose.onNode(hasSetTextAction()).performTextInput("恢复后仍应保留的草稿")
        restoration.emulateSavedInstanceStateRestore()

        compose.onNode(hasSetTextAction()).assertTextContains("恢复后仍应保留的草稿")
    }

    @androidx.compose.runtime.Composable
    private fun screen(
        task: TaskItem,
        historyCount: Int,
        onOpenHistory: () -> Unit = {},
    ) = TaskDetailScreen(
        task = task,
        models = emptyList(),
        online = true,
        busy = false,
        onSend = { _, _, _ -> },
        onInterrupt = {},
        onFork = {},
        onOpenApprovals = {},
        historyCount = historyCount,
        onOpenHistory = onOpenHistory,
        onAssignSlot = {},
        onClearSlot = {},
        onBack = {},
    )

    private fun task(lastResponse: String) = TaskItem(
        id = "thread-1",
        title = "当前桌面对话",
        workspace = "",
        summary = "",
        status = TaskStatus.SUCCEEDED,
        plan = emptyList(),
        transport = TransportKind.LAN_WSS,
        updatedAtEpochMs = 1,
        lastResponse = lastResponse,
    )
}
