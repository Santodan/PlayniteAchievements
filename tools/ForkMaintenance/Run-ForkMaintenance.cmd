@echo off
title Playnite Achievements - ForkMaintenance
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-ForkMaintenance.ps1" %*
exit /b %errorlevel%
