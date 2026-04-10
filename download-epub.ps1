# 52shuku EPUB Downloader

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   52shuku.net  ->  EPUB (English)      " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Paste the novel index URL from 52shuku.net" -ForegroundColor Yellow
Write-Host "Example: https://www.52shuku.net/bl/09_b/bkd7d.html" -ForegroundColor DarkGray
Write-Host ""
$url = Read-Host "Novel URL"

if ([string]::IsNullOrWhiteSpace($url)) {
    Write-Host "No URL entered. Exiting." -ForegroundColor Red
    pause; exit
}

Write-Host ""
Write-Host "Cover image URL (optional — leave blank to auto-generate)" -ForegroundColor Yellow
Write-Host "Example: https://i7-static.jjwxc.net/tmp/backend/.../cover.jpg" -ForegroundColor DarkGray
$coverUrl = Read-Host "Cover URL"

Write-Host ""
Write-Host "How many pages? (0 or blank = ALL pages, enter 3 to test first)" -ForegroundColor Yellow
$pagesInput = Read-Host "Pages"
$pages = 0
if (![string]::IsNullOrWhiteSpace($pagesInput)) { $pages = [int]$pagesInput }

Write-Host ""
Write-Host "Starting download..." -ForegroundColor Green
Write-Host ""

if ([string]::IsNullOrWhiteSpace($coverUrl)) {
    dotnet run --project "$scriptDir" -c Release -- "$url" "$pages"
} else {
    dotnet run --project "$scriptDir" -c Release -- "$url" "$pages" "" "$coverUrl"
}

Write-Host ""
if ($LASTEXITCODE -eq 0) {
    Write-Host "Done! Check your Downloads folder for the .epub file." -ForegroundColor Green
} else {
    Write-Host "Something went wrong. Check the output above." -ForegroundColor Red
}

Write-Host ""
Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host "  1. Download another novel" -ForegroundColor White
Write-Host "  2. Exit" -ForegroundColor White
Write-Host "----------------------------------------" -ForegroundColor Cyan
$choice = Read-Host "Choose (1 or 2)"

if ($choice -eq "1") {
    & $MyInvocation.MyCommand.Path
    exit
}

Write-Host ""
Write-Host "Goodbye!" -ForegroundColor DarkGray
Start-Sleep -Seconds 1
