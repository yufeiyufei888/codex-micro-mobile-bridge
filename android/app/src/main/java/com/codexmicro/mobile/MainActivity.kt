package com.codexmicro.mobile

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.codexmicro.mobile.notifications.ApprovalNotificationManager
import com.codexmicro.mobile.ui.CodexMicroApp
import com.codexmicro.mobile.ui.CodexMicroViewModel
import com.codexmicro.mobile.ui.MobileAction
import com.codexmicro.mobile.ui.theme.CodexMicroTheme

class MainActivity : ComponentActivity() {
    private val viewModel: CodexMicroViewModel by viewModels {
        val app = application as CodexMicroApplication
        CodexMicroViewModel.factory(app, app.container)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = androidx.activity.SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
            navigationBarStyle = androidx.activity.SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
        )
        handleApprovalIntent(intent)
        setContent {
            val state by viewModel.uiState.collectAsStateWithLifecycle()
            val lifecycleOwner = LocalLifecycleOwner.current
            var cameraGranted by remember { mutableStateOf(hasPermission(Manifest.permission.CAMERA)) }
            var notificationsGranted by remember { mutableStateOf(hasNotificationPermission()) }
            var pendingKeepConnection by remember { mutableStateOf(false) }
            val cameraLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
                cameraGranted = it
            }
            val notificationLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
                notificationsGranted = it
                if (it && pendingKeepConnection) viewModel.onAction(MobileAction.SetKeepConnected(true))
                pendingKeepConnection = false
            }

            LaunchedEffect(state.settings.keepConnected, state.settings.pairing) {
                if (state.settings.keepConnected && state.settings.pairing != null) {
                    viewModel.ensureContinuousConnection()
                }
            }

            DisposableEffect(lifecycleOwner) {
                val observer = LifecycleEventObserver { _, event ->
                    if (event == Lifecycle.Event.ON_RESUME) {
                        cameraGranted = hasPermission(Manifest.permission.CAMERA)
                        notificationsGranted = hasNotificationPermission()
                    }
                }
                lifecycleOwner.lifecycle.addObserver(observer)
                onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
            }

            CodexMicroTheme {
                CodexMicroApp(
                    state = state,
                    hasCameraPermission = cameraGranted,
                    hasNotificationPermission = notificationsGranted,
                    onAction = viewModel::onAction,
                    onRequestCamera = { cameraLauncher.launch(Manifest.permission.CAMERA) },
                    onRequestNotifications = {
                        if (Build.VERSION.SDK_INT >= 33) {
                            notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                        } else notificationsGranted = true
                    },
                    onSetKeepConnected = { enabled ->
                        if (enabled && Build.VERSION.SDK_INT >= 33 && !notificationsGranted) {
                            pendingKeepConnection = true
                            notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                        } else viewModel.onAction(MobileAction.SetKeepConnected(enabled))
                    },
                    onOpenSystemSettings = {
                        startActivity(
                            Intent(
                                Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                                Uri.fromParts("package", packageName, null),
                            ),
                        )
                    },
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleApprovalIntent(intent)
    }

    private fun handleApprovalIntent(intent: Intent?) {
        intent?.getStringExtra(ApprovalNotificationManager.EXTRA_APPROVAL_ID)?.let(viewModel::openApproval)
    }

    private fun hasPermission(permission: String): Boolean =
        ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED

    private fun hasNotificationPermission(): Boolean =
        Build.VERSION.SDK_INT < 33 || hasPermission(Manifest.permission.POST_NOTIFICATIONS)
}
