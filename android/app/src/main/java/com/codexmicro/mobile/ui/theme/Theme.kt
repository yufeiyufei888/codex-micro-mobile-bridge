package com.codexmicro.mobile.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val CodexLightColors = lightColorScheme(
    primary = Emerald600,
    onPrimary = White,
    primaryContainer = EmeraldContainer,
    onPrimaryContainer = Color(0xFF064E3B),
    secondary = Blue300,
    onSecondary = White,
    secondaryContainer = BlueContainer,
    onSecondaryContainer = Color(0xFF1E3A8A),
    background = Canvas,
    onBackground = Ink900,
    surface = White,
    onSurface = Ink900,
    surfaceVariant = SoftSurface,
    onSurfaceVariant = Ink700,
    outline = Outline,
    outlineVariant = Color(0xFFE2E8F0),
    error = Rose300,
    onError = White,
    errorContainer = RoseContainer,
    onErrorContainer = Color(0xFF881337),
    surfaceTint = Emerald600,
)

@Composable
fun CodexMicroTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = CodexLightColors, content = content)
}
