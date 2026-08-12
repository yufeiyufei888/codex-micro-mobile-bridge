package com.codexmicro.mobile.ui.screens

import com.codexmicro.mobile.domain.TaskStatus

/**
 * The status card is intentionally compact for every state. Conversation text
 * belongs to the recent-response card and history page, never the status card.
 */
internal fun TaskStatus.showsDetailStatusBody(): Boolean = false
