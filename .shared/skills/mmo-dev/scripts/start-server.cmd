@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0start-server.ps1" %*
