@echo off
cd /d "%~dp0"
if exist "%~dp0VeliShell.exe" (
  start "" "%~dp0VeliShell.exe" --native-frame-demo
  exit /b 0
)
echo Bitte das komplette ZIP entpacken. / Please extract the complete ZIP.
pause
exit /b 1
