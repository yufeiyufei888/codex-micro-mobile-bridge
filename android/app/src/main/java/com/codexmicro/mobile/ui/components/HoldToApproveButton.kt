package com.codexmicro.mobile.ui.components

import android.animation.ValueAnimator
import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.min

@Composable
fun HoldToApproveButton(
    enabled: Boolean,
    onApprove: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var progress by remember { mutableFloatStateOf(0f) }
    val haptic = LocalHapticFeedback.current
    var pressed by remember { mutableStateOf(false) }
    var accessibilityArmedAt by remember { mutableLongStateOf(0L) }
    val showDecorativeProgress = ValueAnimator.areAnimatorsEnabled()
    val gesture = if (enabled) {
        Modifier.pointerInput(onApprove, showDecorativeProgress) {
            coroutineScope countdownScope@{
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    pressed = true
                    progress = 0f
                    var completed = false
                    val countdown = this@countdownScope.launch {
                        val startedAt = SystemClock.elapsedRealtime()
                        do {
                            val elapsed = SystemClock.elapsedRealtime() - startedAt
                            progress = if (showDecorativeProgress) {
                                (elapsed.toFloat() / HOLD_MILLIS).coerceIn(0f, 1f)
                            } else 0f
                            if (elapsed >= HOLD_MILLIS) break
                            delay(min(16L, HOLD_MILLIS - elapsed))
                        } while (pressed)
                        if (!pressed) return@launch
                        completed = true
                        accessibilityArmedAt = 0L
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        onApprove()
                    }
                    waitForUpOrCancellation()
                    pressed = false
                    countdown.cancel()
                    if (!completed) progress = 0f
                }
            }
        }
    } else Modifier

    Box(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(
                if (enabled) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.surfaceVariant,
            )
            .then(gesture)
            .semantics {
                role = Role.Button
                stateDescription = if (accessibilityArmedAt == 0L) {
                    "需要持续按住 600 毫秒批准；辅助功能可执行两步确认"
                } else {
                    "辅助功能批准已开始，等待 600 毫秒后再次执行确认"
                }
                if (!enabled) disabled()
                customActions = listOf(
                    CustomAccessibilityAction(
                        label = if (accessibilityArmedAt == 0L) "开始 600 毫秒批准确认" else "确认批准",
                    ) {
                        if (!enabled) return@CustomAccessibilityAction false
                        val now = SystemClock.elapsedRealtime()
                        val elapsed = now - accessibilityArmedAt
                        when {
                            accessibilityArmedAt == 0L || elapsed > ACCESSIBILITY_CONFIRM_WINDOW_MILLIS -> {
                                accessibilityArmedAt = now
                                true
                            }
                            elapsed >= HOLD_MILLIS -> {
                                accessibilityArmedAt = 0L
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                onApprove()
                                true
                            }
                            else -> false
                        }
                    },
                )
            },
    ) {
        Box(
            Modifier
                .fillMaxHeight()
                .fillMaxWidth(progress)
                .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.18f)),
        )
        Text(
            if (pressed && showDecorativeProgress) "继续按住 · ${(progress * 100).toInt()}%"
            else if (pressed) "继续按住" else "按住 0.6 秒批准",
            modifier = Modifier.align(Alignment.Center),
            color = if (enabled) MaterialTheme.colorScheme.onPrimary
            else MaterialTheme.colorScheme.onSurfaceVariant,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.Bold,
        )
    }
}

private const val HOLD_MILLIS = 600
private const val ACCESSIBILITY_CONFIRM_WINDOW_MILLIS = 10_000
