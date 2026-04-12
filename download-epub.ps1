# 52shuku EPUB Downloader

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Show-Header {
    Clear-Host
    Write-Host ""
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host "     Shuka -> Chinese to English (EPUB)    " -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host ""
}

function Invoke-Shuka {
    param([string[]]$arguments)
    $exe = Join-Path $scriptDir "Shuka.exe"
    if (Test-Path $exe) {
        & $exe @arguments
    } else {
        dotnet run --project "$scriptDir" -c Release -- @arguments
    }
}

function Ask-Novel {
    param([int]$index = 0, [bool]$askPages = $true)

    if ($index -gt 0) {
        Write-Host "--- Novel #$index ---" -ForegroundColor Yellow
    }

    $url = Read-Host "Novel URL"
    if ([string]::IsNullOrWhiteSpace($url)) { return $null }

    Write-Host ""
    Write-Host "Cover URL (leave blank to auto-detect or generate)" -ForegroundColor DarkGray
    $cover = Read-Host "Cover URL"

    $pages = 0
    if ($askPages) {
        Write-Host ""
        Write-Host "How many pages? (0 or blank = ALL, enter 3 to test first)" -ForegroundColor DarkGray
        $pagesInput = Read-Host "Pages"
        if (![string]::IsNullOrWhiteSpace($pagesInput)) { $pages = [int]$pagesInput }
    }

    return [PSCustomObject]@{ Url = $url; Cover = $cover; Pages = $pages }
}

function Download-Single {
    Show-Header
    Write-Host "  [ Single Novel ]" -ForegroundColor Yellow
    Write-Host ""

    $novel = Ask-Novel -askPages $true
    if ($null -eq $novel) { Write-Host "No URL entered." -ForegroundColor Red; return }

    Write-Host ""
    Write-Host "Downloading..." -ForegroundColor Green
    Write-Host ""

    if ([string]::IsNullOrWhiteSpace($novel.Cover)) {
        Invoke-Shuka @($novel.Url, "$($novel.Pages)")
    } else {
        Invoke-Shuka @($novel.Url, "$($novel.Pages)", "", $novel.Cover)
    }

    Write-Host ""
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Done! Check your Downloads folder." -ForegroundColor Green
    } else {
        Write-Host "Something went wrong. Check the output above." -ForegroundColor Red
    }
}

function Download-Batch {
    Show-Header
    Write-Host "  [ Batch Download ]" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Add novels one by one. All pages will be downloaded for each." -ForegroundColor DarkGray
    Write-Host ""

    $queue = [System.Collections.Generic.List[object]]::new()

    while ($true) {
        $novel = Ask-Novel -index ($queue.Count + 1) -askPages $false
        if ($null -ne $novel) {
            $queue.Add($novel)
            Write-Host ""
            Write-Host "Novel #$($queue.Count) added." -ForegroundColor Green
        } else {
            Write-Host "Skipped." -ForegroundColor DarkGray
        }

        Write-Host ""
        Write-Host "  1. Add another novel" -ForegroundColor White
        Write-Host "  2. Start downloading ($($queue.Count) queued)" -ForegroundColor White
        Write-Host "  3. Cancel" -ForegroundColor White
        Write-Host ""
        $choice = Read-Host "Choose"

        if ($choice -eq "2") { break }
        if ($choice -eq "3") { return }
    }

    if ($queue.Count -eq 0) {
        Write-Host "Nothing queued." -ForegroundColor Red
        return
    }

    Write-Host ""
    Write-Host "Starting batch download of $($queue.Count) novel(s)..." -ForegroundColor Green
    Write-Host ""

    $i = 0
    foreach ($novel in $queue) {
        $i++
        Write-Host ""
        Write-Host "[$i/$($queue.Count)] $($novel.Url)" -ForegroundColor Cyan

        if ([string]::IsNullOrWhiteSpace($novel.Cover)) {
            Invoke-Shuka @($novel.Url, "0")
        } else {
            Invoke-Shuka @($novel.Url, "0", "", $novel.Cover)
        }
    }

    Write-Host ""
    Write-Host "Batch complete! Check your Downloads folder." -ForegroundColor Green
}

# ── Main loop ──────────────────────────────────────────────────────────────────
while ($true) {
    Show-Header
    Write-Host "  1. Download single novel" -ForegroundColor White
    Write-Host "  2. Batch download (multiple novels)" -ForegroundColor White
    Write-Host "  3. Exit" -ForegroundColor White
    Write-Host ""
    $choice = Read-Host "Choose (1, 2 or 3)"

    switch ($choice) {
        "1" { Download-Single }
        "2" { Download-Batch }
        "3" { Write-Host ""; Write-Host "Goodbye!" -ForegroundColor DarkGray; Start-Sleep -Seconds 1; exit }
        default { Write-Host "Invalid choice." -ForegroundColor Red }
    }

    Write-Host ""
    Read-Host "Press Enter to return to menu"
}
