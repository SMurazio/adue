@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0godot-run.ps1" %*
