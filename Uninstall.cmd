@echo off
rem Completely removes Ortam Sesi (program, autostart, shortcut, settings).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1"
