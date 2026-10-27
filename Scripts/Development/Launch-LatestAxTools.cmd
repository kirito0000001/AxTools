@echo off
rem Entry point for the development shortcut: hide the console and let PowerShell pick the newest AxTools.exe.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Launch-LatestAxTools.ps1"
