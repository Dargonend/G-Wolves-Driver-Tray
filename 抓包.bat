@echo off
rem ===========================================================================
rem  Sniffer - listens to the mouse for 90 seconds and dumps every frame.
rem
rem  READ ONLY: this never sends a command to the device.
rem
rem  Pure ASCII + CRLF on purpose (see the note in the other .bat files).
rem  Layout-agnostic: works from the development tree and from the packaged
rem  release (tools\ folder with the exe one level up).
rem ===========================================================================

setlocal EnableExtensions
chcp 65001 >nul 2>&1
title GWMouseBattery - sniff
cd /d "%~dp0"

set "HERE=%~dp0"
set "EXE="
if exist "%HERE%GWMouseBattery.exe" set "EXE=%HERE%GWMouseBattery.exe"
if not defined EXE if exist "%HERE%bin\GWMouseBattery.exe" set "EXE=%HERE%bin\GWMouseBattery.exe"
if not defined EXE if exist "%HERE%..\GWMouseBattery.exe" set "EXE=%HERE%..\GWMouseBattery.exe"
if not defined EXE if exist "%HERE%..\bin\GWMouseBattery.exe" set "EXE=%HERE%..\bin\GWMouseBattery.exe"

set "NOTE=%HERE%sniffnote.txt"
if not exist "%NOTE%" if exist "%HERE%..\sniffnote.txt" set "NOTE=%HERE%..\sniffnote.txt"

set "OUT=%HERE%sniff.txt"

cls
if exist "%NOTE%" type "%NOTE%"
echo.
echo   ------------------------------------------------------------
echo.

if not defined EXE (
    echo   [ERROR] GWMouseBattery.exe was not found next to this script.
    echo.
    pause
    exit /b 1
)

pause

start /wait "" "%EXE%" --sniff 90 --out "%OUT%"

echo.
echo   ------------------------------------------------------------
if exist "%OUT%" (
    echo   DONE.  sniff.txt has been written:
    echo          %OUT%
) else (
    echo   [WARN] no output was produced - the mouse may be unreachable.
)
echo.
pause
endlocal
exit /b 0
