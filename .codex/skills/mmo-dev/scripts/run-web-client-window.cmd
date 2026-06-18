@echo off
title MMO Web Client
cd /d "%~dp0..\..\..\.."
".tools\dotnet\dotnet.exe" run --no-build --no-restore --project "src\Mmo.Client.Web\Mmo.Client.Web.csproj"
