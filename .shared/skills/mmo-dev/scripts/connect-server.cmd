@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0connect-server.ps1" %*
