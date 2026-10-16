@echo off
cd /d "%~dp0"
if exist "%~dp0VeliShell.exe" (
  start "" "%~dp0VeliShell.exe" --restore-native-prototype
  exit /b 0
)
echo VeliShell.exe fehlt. Bitte das gesamte ZIP entpacken.
echo VeliShell.exe is missing. Please extract the complete ZIP.
pause
exit /b 1
