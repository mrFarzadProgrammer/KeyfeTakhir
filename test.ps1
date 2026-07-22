Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host '1/5 - Container status' -ForegroundColor Cyan
& docker compose --env-file .env ps
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the container status.'
}

Write-Host "`n2/5 - Health endpoint" -ForegroundColor Cyan
$response = Invoke-WebRequest -Uri 'http://localhost:8080/health' -UseBasicParsing -TimeoutSec 5
if ($response.StatusCode -ne 200) {
    throw "Health endpoint returned HTTP $($response.StatusCode)."
}
Write-Host 'HTTP 200 OK' -ForegroundColor Green

Write-Host "`n3/5 - Admin UI and logo" -ForegroundColor Cyan
$adminPage = Invoke-WebRequest -Uri 'http://localhost:8080/admin/' -UseBasicParsing -TimeoutSec 5
if ($adminPage.StatusCode -ne 200 -or
    $adminPage.Content -notmatch '/admin/assets/logo.svg' -or
    $adminPage.Content -notmatch '<title>') {
    throw 'The admin page or its branding did not load correctly.'
}

$logo = Invoke-WebRequest -Uri 'http://localhost:8080/admin/assets/logo.svg' -UseBasicParsing -TimeoutSec 5
if ($logo.StatusCode -ne 200 -or $logo.Content -notmatch '<svg') {
    throw 'The admin logo did not load correctly.'
}
Write-Host 'Admin page and logo: OK' -ForegroundColor Green

Write-Host "`n4/5 - Authentication boundary" -ForegroundColor Cyan
$unauthorized = $false
try {
    Invoke-WebRequest -Uri 'http://localhost:8080/api/admin/session' -UseBasicParsing -TimeoutSec 5 | Out-Null
}
catch {
    $statusCode = $null
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $statusCode = [int]$_.Exception.Response.StatusCode
    }
    if ($statusCode -eq 401) {
        $unauthorized = $true
    }
}

if (-not $unauthorized) {
    throw 'The unauthenticated admin API should return HTTP 401.'
}
Write-Host 'Unauthenticated API returns 401: OK' -ForegroundColor Green

Write-Host "`n5/5 - Recent known errors" -ForegroundColor Cyan
$logs = & docker compose --env-file .env logs --since 5m late-fee-box 2>&1
$patterns = @(
    'Unhandled exception',
    'Bale:BotToken is empty',
    'scheme is not supported',
    'HttpClient.Timeout of 45 seconds',
    'NU1301',
    'SSL connection could not be established'
)
$problems = $logs | Select-String -Pattern $patterns
if ($problems) {
    $problems
    throw 'A known fatal error was found in the application logs.'
}

Write-Host 'No known fatal error was found.' -ForegroundColor Green
Write-Host "`nBale tests: private /bill; group /addmember, /debtors, /fund, /commands." -ForegroundColor Yellow
