@echo off
setlocal
cd /d "%~dp0"

rem Debug launcher: same as start-mmo.cmd but turns on the on-screen
rem frame-timing + GC overlay so we can diagnose the movement stutter.
set MMO_DEBUG_MOVEMENT=1
set MMO_GODOT_FRAME_HITCH_MS=18
rem Big-world test: 1000x1000 tiles (verified server-side; ZoneInfo ships the full ~4000-tile border).
rem Bump both to 2048 to go bigger (untested: ~8200-tile ZoneInfo at login may stress that one message).
set MMO_WORLD_WIDTH_TILES=1000
set MMO_WORLD_HEIGHT_TILES=1000
rem (Diagnostic file logs MMO_DEBUG_FRAME_LOG / MMO_DEBUG_CADENCE_LOG were used to find the
rem  movement-cadence freeze; left off now. Re-add either flag if you need a fresh trace.)

echo Starting MMO in DEBUG mode (frame timing + GC overlay enabled)...
echo.
echo While moving, read the TOP-LEFT overlay FRAME line:
echo   - frame ms / max ms
echo   - gc0 / gc1 / gc2 counts
echo   - interpolation queueDepth
echo.

call ".shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd"
if errorlevel 1 (
    echo.
    echo Failed to start. If it says "Godot executable not found", set MMO_GODOT:
    echo   setx MMO_GODOT "D:\Tools\Godot\Godot_v4.6.3-stable_mono_win64.exe"
    echo then open a new window and retry.
    pause
    exit /b 1
)

echo.
echo MMO started in debug mode.
echo Stop with: .shared\skills\mmo-dev\scripts\stop-mmo.cmd
pause
