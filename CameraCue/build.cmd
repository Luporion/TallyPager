@echo off
setlocal
set "PIO=pio"
where pio >nul 2>nul
if errorlevel 1 (
  set "PIO=%USERPROFILE%\.platformio\penv\Scripts\platformio.exe"
  if not exist "%PIO%" (
    echo PlatformIO CLI wurde nicht gefunden.
    echo Erwarteter Pfad: %USERPROFILE%\.platformio\penv\Scripts\platformio.exe
    echo Alternativ in VS Code: PlatformIO - Build verwenden.
    pause
    exit /b 1
  )
)
"%PIO%" run
set ERR=%ERRORLEVEL%
pause
exit /b %ERR%
