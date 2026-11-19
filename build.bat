@echo off
cd /d "%~dp0"
dotnet publish -c Release
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)
echo.
echo DONE: %~dp0dist\StalzoneServerSelector.exe
explorer "%~dp0dist"
pause
