Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

& docker compose --env-file .env down
if ($LASTEXITCODE -ne 0) {
    throw 'Could not stop the application container.'
}

Write-Host 'KeyfeTakhir stopped.' -ForegroundColor Green
