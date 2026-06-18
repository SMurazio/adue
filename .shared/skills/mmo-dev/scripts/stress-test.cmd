@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stress-test.ps1" %*
