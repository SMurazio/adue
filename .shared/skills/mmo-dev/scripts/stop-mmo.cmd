@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0stop-mmo.ps1" %*
