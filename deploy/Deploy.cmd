@echo off
rem Double-click to put the latest version of the site live. See deploy\README.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
echo.
pause
