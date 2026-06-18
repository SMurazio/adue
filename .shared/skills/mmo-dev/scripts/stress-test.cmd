@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0stress-test.ps1" %*
