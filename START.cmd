@echo off
setlocal
pushd "%~dp0"
echo VeliShell - Bauen und starten
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1" -Run
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo.
  echo Der Vorgang wurde nicht abgeschlossen. Bitte die Meldung oben beachten.
  pause
)
popd
exit /b %RESULT%
