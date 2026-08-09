@echo off
REM ADUE duo front door: visible server + TWO Godot clients (GodotA + GodotB) for the two-player
REM feel-test / merge gate. Thin wrapper over start-godot-visual-check.ps1 -Clients 2; every flag
REM of that script passes through (e.g. -LogToFile). For a SOLO client use start-godot-visual-check.cmd.
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0start-godot-visual-check.ps1" -Clients 2 %*
