@echo off
rem Registers Romestead Cheat Menu in the game. What it does is described in setup.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" install %*
pause
