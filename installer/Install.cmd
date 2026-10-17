@echo off
rem Double-click to install WatchLattice. It asks for administrator rights,
rem then asks a few first-run questions. See install.ps1 for the options.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 pause
