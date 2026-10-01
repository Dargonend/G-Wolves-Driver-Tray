@echo off
rem ===========================================================================
rem  GWMouseBattery launcher - plain ASCII + CRLF on purpose.
rem
rem  cmd.exe re-reads a running batch file from disk while it executes, and it
rem  assumes CRLF line endings. A non-ASCII byte or a bare LF makes it resume
rem  in the middle of a line and try to run word fragments as commands. The
rem  Chinese menu lives in menu.txt and is printed with "type" after chcp 65001.
rem
rem  The exe is a GUI-subsystem program, so cmd does not wait for it. Every
rem  command-line mode goes through "start /wait" and writes its text to a file
rem  with --out, which "type" then prints.
rem
rem  The EMPTY counter guards against a menu that spins forever: when stdin is
rem  at EOF, "set /p" returns immediately without defining the variable.
rem ===========================================================================

setlocal EnableExtensions
chcp 65001 >nul 2>&1
title G-Wolves Mouse Battery

set "HERE=%~dp0"
set "MENU=%HERE%menu.txt"
set "REPORT=%TEMP%\gwmouse-report.txt"

rem Locate the exe without caring about the layout: packaged releases keep it
rem right next to this script, the development tree keeps it in bin\.
set "EXE="
if exist "%HERE%GWMouseBattery.exe" set "EXE=%HERE%GWMouseBattery.exe"
if not defined EXE if exist "%HERE%bin\GWMouseBattery.exe" set "EXE=%HERE%bin\GWMouseBattery.exe"

if not exist "%EXE%" goto :missing

set "EMPTY=0"

:menu
cls
if exist "%MENU%" type "%MENU%"
echo.
set "PICK="
set /p "PICK=   # "
if not defined PICK goto :noinput
set "EMPTY=0"

if "%PICK%"=="1" goto :tray
if "%PICK%"=="2" goto :probe
if "%PICK%"=="3" goto :diag
if "%PICK%"=="4" goto :selftest
if "%PICK%"=="5" goto :shortcut
if "%PICK%"=="6" goto :stop
if "%PICK%"=="7" goto :sniff
if "%PICK%"=="0" goto :bye
goto :menu

:sniff
echo.
echo   Sniffing for 60 seconds. NO command is sent by this tool.
echo   During this window, go to the web driver and change the DPI once.
echo   Then come back here and copy everything below.
echo.
start /wait "" "%EXE%" --sniff 60 --out "%REPORT%" >nul 2>&1
if exist "%REPORT%" (
    type "%REPORT%"
    del "%REPORT%" >nul 2>&1
) else (
    echo   [WARN] no output was produced.
)
goto :again

:stop
echo.
taskkill /f /im GWMouseBattery.exe >nul 2>&1
if errorlevel 1 goto :stopnone
echo   Stopped the running instance.
ping -n 3 127.0.0.1 >nul
exit /b 0

:stopnone
echo   No running instance was found.
ping -n 3 127.0.0.1 >nul
exit /b 0

:noinput
set /a EMPTY+=1
if %EMPTY% GEQ 5 goto :bye
goto :menu

:tray
echo.
echo   Tray app started - look at the notification area.
start "" "%EXE%" --tray
ping -n 3 127.0.0.1 >nul
exit /b 0

:probe
call :run --probe
goto :again

:diag
call :run --diag
goto :again

:selftest
call :run --selftest
goto :again

:shortcut
call :run --shortcut
goto :again

:run
start /wait "" "%EXE%" %~1 --out "%REPORT%" >nul 2>&1
if exist "%REPORT%" goto :show
echo   [WARN] no output was produced by %~1
exit /b 0

:show
type "%REPORT%"
del "%REPORT%" >nul 2>&1
exit /b 0

:again
echo.
pause
goto :menu

:missing
echo.
echo   [ERROR] GWMouseBattery.exe was not found.
echo   Keep it next to this launcher (or in a bin\ subfolder).
echo.
pause
exit /b 1

:bye
endlocal
exit /b 0
