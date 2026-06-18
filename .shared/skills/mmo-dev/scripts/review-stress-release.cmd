@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0review-stress.ps1" -Configuration Release %*
