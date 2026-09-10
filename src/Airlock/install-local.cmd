@echo off
setlocal
rem Build the tool from this working tree and install it globally as `airlock`.
rem PackageId is Airlock and PackageOutputPath is <repo>\artifacts\nupkg.

set REPO=%~dp0..\..

dotnet tool uninstall -g Airlock 2>nul

dotnet pack "%~dp0Airlock.csproj" -c Release || exit /b 1

dotnet tool install -g Airlock --source "%REPO%\artifacts\nupkg" || exit /b 1

airlock --help
