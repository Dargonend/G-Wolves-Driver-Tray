@echo off
rem ===========================================================================
rem  DPI diagnosis - finds out where the web driver really stores the DPI.
rem
rem  Pure ASCII + CRLF on purpose: cmd.exe re-reads a running batch file from
rem  disk and assumes CRLF, so a bare LF or a non-ASCII byte makes it resume
rem  mid-line. Chinese text lives in the .txt notes and is printed with "type"
rem  after "chcp 65001".
rem
rem  Layout-agnostic: works both in the development tree (exe in bin\) and in
rem  the packaged release (exe at the package root, this script in tools\).
rem ===========================================================================

setlocal EnableExtensions
chcp 65001 >nul 2>&1
title G-Wolves-Driver-Tray - DPI diagnosis
cd /d "%~dp0"

set "HERE=%~dp0"
set "EXE="
if exist "%HERE%G-Wolves-Driver-Tray.exe" set "EXE=%HERE%G-Wolves-Driver-Tray.exe"
if not defined EXE if exist "%HERE%bin\G-Wolves-Driver-Tray.exe" set "EXE=%HERE%bin\G-Wolves-Driver-Tray.exe"
if not defined EXE if exist "%HERE%..\G-Wolves-Driver-Tray.exe" set "EXE=%HERE%..\G-Wolves-Driver-Tray.exe"
if not defined EXE if exist "%HERE%..\bin\G-Wolves-Driver-Tray.exe" set "EXE=%HERE%..\bin\G-Wolves-Driver-Tray.exe"

set "NOTE=%HERE%finddpinote.txt"
if not exist "%NOTE%" if exist "%HERE%..\finddpinote.txt" set "NOTE=%HERE%..\finddpinote.txt"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

cls
if exist "%NOTE%" type "%NOTE%"
echo.
echo   ------------------------------------------------------------
echo.

if not defined EXE (
    echo   [ERROR] G-Wolves-Driver-Tray.exe was not found next to this script.
    echo.
    pause
    exit /b 1
)

pause

rem No -Out: the script locates its own folder (%~dp0 always ends in a
rem backslash, and passing that quoted into -Out is asking for trouble).
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%HERE%finddpi.ps1"

echo.
echo   ============================================================
echo.
if exist "%HERE%dpidiff.txt" (
    type "%HERE%dpidiff.txt"
) else (
    echo   [WARN] dpidiff.txt was not produced.
)
echo.
pause
endlocal
exit /b 0
