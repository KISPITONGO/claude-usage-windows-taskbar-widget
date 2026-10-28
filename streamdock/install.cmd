@echo off
chcp 65001 >nul
setlocal
set "SRC=%~dp0com.tokenbars.streamdock.sdPlugin"
set "ROOT=%APPDATA%\HotSpot\StreamDock"
set "DEST=%ROOT%\plugins\com.tokenbars.streamdock.sdPlugin"

if not exist "%SRC%\manifest.json" (
  echo No encuentro la carpeta com.tokenbars.streamdock.sdPlugin junto a este archivo.
  pause & exit /b 1
)
if not exist "%ROOT%" (
  echo No encuentro el software Stream Dock en %ROOT%.
  echo Instala o abre una vez el programa Stream Dock de Ajazz/Mirabox y vuelve a intentarlo.
  pause & exit /b 1
)

echo Cerrando Stream Dock para instalar el plugin...
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "(Get-Process StreamDock -ErrorAction SilentlyContinue | Select-Object -First 1).Path"`) do set "SDEXE=%%P"
taskkill /f /im StreamDock.exe >nul 2>&1
taskkill /f /im TokenBarsDock.exe >nul 2>&1
timeout /t 2 /nobreak >nul

robocopy "%SRC%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Error al copiar el plugin a %DEST%
  pause & exit /b 1
)
echo Plugin instalado en %DEST%

if defined SDEXE (
  echo Abriendo Stream Dock otra vez...
  start "" "%SDEXE%"
) else (
  echo Abre el programa Stream Dock: el plugin aparece en la categoria TokenBars.
)
pause
