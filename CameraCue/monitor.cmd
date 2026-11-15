@echo off
setlocal
set "PIO=pio"
where pio >nul 2>nul
if errorlevel 1 (
  set "PIO=%USERPROFILE%\.platformio\penv\Scripts\platformio.exe"
  if not exist "%PIO%" (
    echo PlatformIO CLI wurde nicht gefunden.
    pause
    exit /b 1
  )
)
"%PIO%" device monitor -b 115200
set ERR=%ERRORLEVEL%
pause
exit /b %ERR%
