@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0review-stress.ps1" %*
