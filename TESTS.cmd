@echo off
setlocal
pushd "%~dp0"
echo VeliShell 0.7.0 - Kerntests
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1" -TestsOnly
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo.
  echo Der Vorgang wurde nicht abgeschlossen. Bitte die Meldung oben beachten.
  pause
)
if "%RESULT%"=="0" pause
popd
exit /b %RESULT%
