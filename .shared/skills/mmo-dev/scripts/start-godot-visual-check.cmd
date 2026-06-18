@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0start-godot-visual-check.ps1" %*
