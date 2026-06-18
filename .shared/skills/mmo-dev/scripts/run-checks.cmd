@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0run-checks.ps1" %*
