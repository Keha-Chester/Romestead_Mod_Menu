@echo off
rem Removes Romestead Cheat Menu's registration from the game. What it does is described in setup.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" uninstall %*
pause
