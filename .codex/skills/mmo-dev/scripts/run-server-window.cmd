@echo off
title MMO Server
cd /d "%~dp0..\..\..\.."
".tools\dotnet\dotnet.exe" run --no-build --no-restore --project "src\Mmo.Server\Mmo.Server.csproj"
