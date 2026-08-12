package com.codexmicro.mobile.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.BorderStroke
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.ProgressKind

@Composable
@OptIn(ExperimentalFoundationApi::class)
fun TaskCard(
    task: TaskItem,
    slot: Int,
    offline: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val visual = task.status.visual()
    Card(
        modifier = modifier
            .heightIn(min = 188.dp)
            .graphicsLayer { alpha = if (offline) 0.58f else 1f }
            .combinedClickable(
                onClickLabel = "打开任务",
                onLongClickLabel = "管理槽位和固定",
                onClick = onClick,
                onLongClick = onLongClick,
            )
            .semantics {
                role = Role.Button
                stateDescription = when (val progress = task.progressKind) {
                    is ProgressKind.PlanSteps -> "${visual.label}，计划完成 ${progress.completed}/${progress.total}"
                    is ProgressKind.Indeterminate -> "${visual.label}，${progress.label}"
                    ProgressKind.Unknown -> "${visual.label}，暂无可验证计划"
                }
            },
        colors = CardDefaults.cardColors(containerColor = visual.containerColor),
        elevation = CardDefaults.cardElevation(defaultElevation = 1.dp, pressedElevation = 0.dp),
        border = BorderStroke(
            1.dp,
            if (offline) MaterialTheme.colorScheme.outline else visual.color.copy(alpha = 0.48f),
        ),
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
            Surface(
                color = MaterialTheme.colorScheme.surface.copy(alpha = 0.72f),
                contentColor = visual.color,
                shape = MaterialTheme.shapes.small,
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 5.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(5.dp),
                ) {
                    Icon(visual.icon, contentDescription = null, modifier = Modifier.size(15.dp))
                    Text(visual.label, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold)
                }
            }
                Text(
                    "槽位 $slot${if (task.pinned) " · 已固定" else ""}",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                task.title,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                task.currentStep,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.weight(1f, fill = true))
            val progressModifier = Modifier
                .fillMaxWidth()
                .height(5.dp)
                .clip(MaterialTheme.shapes.extraSmall)
            task.progress?.let {
                LinearProgressIndicator(
                    progress = { it },
                    modifier = progressModifier,
                    color = visual.color,
                    trackColor = visual.color.copy(alpha = 0.16f),
                )
            } ?: LinearProgressIndicator(
                modifier = progressModifier,
                color = visual.color,
                trackColor = visual.color.copy(alpha = 0.16f),
            )
            Text(
                when (val progress = task.progressKind) {
                    is ProgressKind.PlanSteps -> "${progress.completed}/${progress.total} 步"
                    is ProgressKind.Indeterminate -> progress.label
                    ProgressKind.Unknown -> "暂无可验证计划"
                },
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (offline) {
                val minutes = ((System.currentTimeMillis() - task.updatedAtEpochMs).coerceAtLeast(0L) / 60_000L)
                Text(
                    "离线快照 · ${if (minutes < 1) "刚刚" else "${minutes}分钟前"}",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}
