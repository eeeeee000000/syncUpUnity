@echo off
chcp 65001 >nul
title syncUp-servern
cd /d "%~dp0server"

where node >nul 2>nul
if errorlevel 1 (
  echo Node.js saknas. Ladda ner det fran https://nodejs.org och forsok igen.
  pause
  exit /b 1
)

echo Startar syncUp... Chatten och adminpanelen oppnas i webblasaren.
echo Stang det har fonstret for att stoppa servern.
echo.
set OPEN_BROWSER=1
node server.js
echo.
echo Servern stoppades.
pause
