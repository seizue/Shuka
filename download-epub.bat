@echo off
title 52shuku EPUB Downloader
color 0B
cd /d "%~dp0"

echo.
echo ========================================
echo    52shuku.net  -^>  EPUB (English)
echo ========================================
echo.
echo Paste the novel index URL from 52shuku.net
echo Example: https://www.52shuku.net/bl/09_b/bkd7d.html
echo.
set /p NOVEL_URL="Novel URL: "

if "%NOVEL_URL%"=="" (
    echo No URL entered. Exiting.
    pause
    exit /b
)

echo.
echo Cover image URL - leave blank to auto-generate a cover
echo Example: https://i7-static.jjwxc.net/tmp/.../cover.jpg
echo.
set /p COVER_URL="Cover URL (or press Enter to skip): "

echo.
echo How many pages? Press Enter for ALL pages, or type 3 to test first
echo.
set /p PAGES="Pages: "
if "%PAGES%"=="" set PAGES=0

echo.
echo Downloading and translating... please wait.
echo (EPUB will be saved to your Downloads folder)
echo.

if "%COVER_URL%"=="" (
    "%~dp0ShukuEpub.exe" "%NOVEL_URL%" "%PAGES%"
) else (
    "%~dp0ShukuEpub.exe" "%NOVEL_URL%" "%PAGES%" "" "%COVER_URL%"
)

echo.
if %ERRORLEVEL%==0 (
    color 0A
    echo Done! Your .epub file is in your Downloads folder.
) else (
    color 0C
    echo Something went wrong. See the error above.
)

echo.
pause
