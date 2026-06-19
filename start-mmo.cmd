@echo off
setlocal

cd /d "%~dp0"

rem Big-world test: 1000x1000 tiles. Bump both to 2048 to go bigger
rem (untested: ~8200-tile ZoneInfo at login may stress that one message).
set MMO_WORLD_WIDTH_TILES=1000
set MMO_WORLD_HEIGHT_TILES=1000

echo Starting MMO server...
call ".shared\skills\mmo-dev\scripts\start-godot-visual-check.cmd"
if errorlevel 1 (
    echo.
    echo Failed to start the MMO server and Godot clients.
    pause
    exit /b 1
)

echo.
echo MMO server and Godot clients have been started.
echo Use .shared\skills\mmo-dev\scripts\stop-mmo.cmd to stop them.
echo.
pause
