@echo off
rem Double-click to put the previous version back (the newest backup). See deploy\README.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" -Rollback
echo.
pause
