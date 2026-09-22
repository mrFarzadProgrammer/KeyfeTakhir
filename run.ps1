param(
    [switch]$Rebuild
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$ImageTag = 'latefeebox-stable:1.3.4'
$SdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0'
$RuntimeImage = 'mcr.microsoft.com/dotnet/aspnet:10.0'

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$Quiet
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) {
            & docker @Arguments *> $null
        }
        else {
            & docker @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        }
        return [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Read-DotEnv {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    foreach ($line in [System.IO.File]::ReadAllLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separatorIndex = $trimmed.IndexOf('=')
        if ($separatorIndex -lt 1) {
            continue
        }

        $key = $trimmed.Substring(0, $separatorIndex).Trim()
        $value = $trimmed.Substring($separatorIndex + 1).Trim()
        $values[$key] = $value
    }

    return $values
}

function Build-ApplicationImage {
    Write-Host 'Building application image...' -ForegroundColor Cyan

    $buildArguments = @(
        'build',
        '--pull=false',
        '--network=none',
        '--tag', $ImageTag,
        '--file', 'src/LateFeeBox.Web/Dockerfile',
        '.'
    )

    $buildExitCode = Invoke-Docker -Arguments $buildArguments
    if ($buildExitCode -eq 0) {
        return
    }

    Write-Host ''
    Write-Warning 'The first build attempt failed. Docker may not have the .NET base images locally.'
    Write-Host "Trying to download $SdkImage ..." -ForegroundColor Yellow
    $sdkPullExitCode = Invoke-Docker -Arguments @('pull', $SdkImage)

    Write-Host "Trying to download $RuntimeImage ..." -ForegroundColor Yellow
    $runtimePullExitCode = Invoke-Docker -Arguments @('pull', $RuntimeImage)

    if ($sdkPullExitCode -ne 0 -or $runtimePullExitCode -ne 0) {
        throw @"
The required .NET Docker images are not available and Docker could not download them.
This is a Docker Desktop network/proxy/TLS problem, not a NuGet package problem.

Run these commands manually after fixing Docker Desktop proxy settings:
  docker pull $SdkImage
  docker pull $RuntimeImage

Then run:
  .\run.ps1 -Rebuild
"@
    }

    Write-Host 'Base images are ready. Retrying the offline application build...' -ForegroundColor Cyan
    $secondBuildExitCode = Invoke-Docker -Arguments $buildArguments
    if ($secondBuildExitCode -ne 0) {
        throw 'Docker build failed after the base images were prepared. Review the build output above.'
    }
}

Write-Host '1/6 - Checking Docker Desktop...' -ForegroundColor Cyan
if ((Invoke-Docker -Arguments @('version') -Quiet) -ne 0) {
    throw 'Docker Desktop is not running. Start Docker Desktop and retry.'
}

if (-not (Test-Path -LiteralPath '.env')) {
    throw 'The .env file was not found. Copy .env.example to .env and fill its values.'
}

Write-Host '2/6 - Validating .env...' -ForegroundColor Cyan
$envValues = Read-DotEnv -Path '.env'
$requiredNames = @(
    'BALE_BOT_TOKEN',
    'BALE_BOT_USERNAME',
    'BALE_ADMIN_USER_ID',
    'BALE_GROUP_CHAT_ID',
    'ADMIN_PASSWORD'
)

foreach ($name in $requiredNames) {
    if (-not $envValues.ContainsKey($name) -or [string]::IsNullOrWhiteSpace([string]$envValues[$name])) {
        throw "Required value '$name' is empty in .env."
    }
}

$token = [string]$envValues['BALE_BOT_TOKEN']
if ($token.StartsWith('bot', [System.StringComparison]::OrdinalIgnoreCase) -or
    $token.Contains('://') -or
    -not $token.Contains(':')) {
    throw 'BALE_BOT_TOKEN is invalid. Put only the raw token returned by Bale BotFather.'
}

if ($envValues.ContainsKey('BALE_USE_LONG_POLLING') -and
    -not [string]::IsNullOrWhiteSpace([string]$envValues['BALE_USE_LONG_POLLING']) -and
    ([string]$envValues['BALE_USE_LONG_POLLING']).ToLowerInvariant() -ne 'true') {
    throw 'BALE_USE_LONG_POLLING must be true in this release.'
}

if ($Rebuild) {
    Write-Host '3/6 - Preparing and building the application...' -ForegroundColor Cyan
    Build-ApplicationImage
    Write-Host '4/6 - Application image built successfully.' -ForegroundColor Green
}
else {
    if ((Invoke-Docker -Arguments @('image', 'inspect', $ImageTag) -Quiet) -ne 0) {
        throw "The application image '$ImageTag' does not exist. Run .\run.ps1 -Rebuild first."
    }

    Write-Host '3/6 - Existing application image found.' -ForegroundColor Cyan
    Write-Host '4/6 - Build skipped.' -ForegroundColor DarkGray
}

Write-Host '5/6 - Starting application...' -ForegroundColor Cyan
# Remove a container left by an older extracted project. Docker volumes are not removed.
$null = Invoke-Docker -Arguments @('rm', '-f', 'late-fee-box') -Quiet

$startExitCode = Invoke-Docker -Arguments @(
    'compose',
    '--env-file', '.env',
    'up', '-d', '--force-recreate', '--no-build'
)
if ($startExitCode -ne 0) {
    throw 'Could not start the application container.'
}

Write-Host '6/6 - Waiting for health check...' -ForegroundColor Cyan
$healthy = $false
for ($attempt = 1; $attempt -le 60; $attempt++) {
    try {
        $response = Invoke-WebRequest `
            -Uri 'http://localhost:8080/health' `
            -UseBasicParsing `
            -TimeoutSec 2

        if ($response.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    }
    catch {
        # The application may still be starting.
    }

    Start-Sleep -Seconds 1
}

if (-not $healthy) {
    Invoke-Docker -Arguments @('compose', '--env-file', '.env', 'logs', '--tail', '150', 'late-fee-box') | Out-Null
    throw 'The health check did not become ready. Review the logs printed above.'
}

Write-Host ''
Write-Host 'KeyfeTakhir started successfully.' -ForegroundColor Green
Write-Host 'Admin panel: http://localhost:8080/admin/' -ForegroundColor Green
Write-Host 'Logs: docker compose --env-file .env logs -f --tail 100 late-fee-box' -ForegroundColor Yellow
Write-Host 'Bale tests: private /bill   group /debtors /fund /commands' -ForegroundColor Yellow
