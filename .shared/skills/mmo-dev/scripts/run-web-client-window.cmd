@echo off
title MMO Web Client
cd /d "%~dp0..\..\..\.."
set "DOTNET=.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" run --no-build --no-restore --project "src\Mmo.Client.Web\Mmo.Client.Web.csproj"
