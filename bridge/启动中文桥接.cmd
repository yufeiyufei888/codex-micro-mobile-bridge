@echo off
setlocal
set "BRIDGE_DIR=%~dp0"
set "CODEX_PATH=%CODEX_BRIDGE_CODEX_PATH%"

if not defined CODEX_PATH if exist "%BRIDGE_DIR%codex.exe" set "CODEX_PATH=%BRIDGE_DIR%codex.exe"
if not defined CODEX_PATH if exist "%BRIDGE_DIR%..\..\work\codex.exe" set "CODEX_PATH=%BRIDGE_DIR%..\..\work\codex.exe"

if defined CODEX_PATH (
  "%BRIDGE_DIR%CodexMicroBridge.exe" --codex-executable "%CODEX_PATH%"
) else (
  "%BRIDGE_DIR%CodexMicroBridge.exe"
)

endlocal
