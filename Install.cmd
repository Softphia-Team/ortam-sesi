@echo off
rem Installs Ortam Sesi for the current user. Just double-click. No admin rights needed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
