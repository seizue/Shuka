@echo off
title Shuka EPUB Downloader
color 0B
cd /d "%~dp0"

:MENU
cls
echo.
echo =================================================
echo    52shuku.net / czbooks.net  -^>  EPUB (English)
echo =================================================
echo.
echo   1. Download single novel
echo   2. Batch download (multiple novels)
echo   3. Exit
echo.
set /p MODE="Choose (1, 2 or 3): "

if "%MODE%"=="1" goto SINGLE
if "%MODE%"=="2" goto BATCH
if "%MODE%"=="3" goto END
goto MENU

:: ─────────────────────────────────────────────────────────────────────────────
:SINGLE
cls
echo.
echo ========================================
echo    Chinese Novel  -^>  EPUB (English)
echo ========================================
echo.
set /p NOVEL_URL="Novel URL: "
if "%NOVEL_URL%"=="" ( echo No URL entered. & pause & goto MENU )

echo.
echo Cover URL (leave blank to auto-detect or generate)
set /p COVER_URL="Cover URL: "

echo.
echo How many pages? (Enter = ALL, or type a number to test)
set /p PAGES="Pages: "
if "%PAGES%"=="" set PAGES=0

echo.
echo Downloading... (EPUB saved to Downloads folder)
echo.

if "%COVER_URL%"=="" (
    "%~dp0Shuka.exe" "%NOVEL_URL%" "%PAGES%"
) else (
    "%~dp0Shuka.exe" "%NOVEL_URL%" "%PAGES%" "" "%COVER_URL%"
)

echo.
if %ERRORLEVEL%==0 ( color 0A & echo Done! Check your Downloads folder. ) else ( color 0C & echo Something went wrong. )
echo.
pause
goto MENU

:: ─────────────────────────────────────────────────────────────────────────────
:BATCH
cls
echo.
echo ========================================
echo    Batch Download  -^>  EPUB (English)
echo ========================================
echo.
echo Add novels one by one. Each will download after you choose to start.
echo.
setlocal enabledelayedexpansion
set BATCH_COUNT=0
set BATCH_PENDING=0

:BATCH_ADD
set /a BATCH_COUNT+=1
echo --- Novel #!BATCH_COUNT! ---
set /p B_URL="Novel URL: "

if "!B_URL!"=="" (
    set /a BATCH_COUNT-=1
    echo Skipped.
) else (
    echo Cover URL (leave blank to auto-detect or generate)
    set /p B_COVER="Cover URL: "
    set /a BATCH_PENDING+=1
    set NOVEL_URL_!BATCH_PENDING!=!B_URL!
    set NOVEL_COVER_!BATCH_PENDING!=!B_COVER!
    echo Novel #!BATCH_COUNT! added.
)
echo.

echo   1. Add another novel
echo   2. Start downloading (!BATCH_PENDING! queued)
echo   3. Cancel
echo.
set /p BATCH_CHOICE="Choose: "

if "!BATCH_CHOICE!"=="1" goto BATCH_ADD
if "!BATCH_CHOICE!"=="3" ( endlocal & goto MENU )

if !BATCH_PENDING!==0 (
    echo Nothing queued.
    endlocal
    pause
    goto MENU
)

echo.
echo Starting batch download of !BATCH_PENDING! novel(s)...
echo.

for /l %%I in (1,1,!BATCH_PENDING!) do (
    echo.
    echo [%%I/!BATCH_PENDING!] !NOVEL_URL_%%I!
    if "!NOVEL_COVER_%%I!"=="" (
        "%~dp0Shuka.exe" "!NOVEL_URL_%%I!" "0"
    ) else (
        "%~dp0Shuka.exe" "!NOVEL_URL_%%I!" "0" "" "!NOVEL_COVER_%%I!"
    )
)

endlocal
echo.
color 0A
echo Batch complete! Check your Downloads folder.
echo.
pause
goto MENU

:: ─────────────────────────────────────────────────────────────────────────────
:END
echo.
echo Goodbye!
timeout /t 2 /nobreak >nul
