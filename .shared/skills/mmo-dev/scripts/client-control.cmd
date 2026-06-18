@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0client-control.ps1" %*
