@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0movement-debug-trace.ps1" %*
