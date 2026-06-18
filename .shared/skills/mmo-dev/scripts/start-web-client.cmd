@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0start-web-client.ps1" %*
