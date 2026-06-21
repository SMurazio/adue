@echo off
title MMO Server
cd /d "%~dp0..\..\..\.."
set "DOTNET=.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" run --no-build --no-restore --project "src\Mmo.Server\Mmo.Server.csproj"
