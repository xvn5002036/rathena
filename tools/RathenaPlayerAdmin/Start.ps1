$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ' rAthena Player Admin' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan

function Test-DotNet8Sdk {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        return $false
    }

    try {
        $sdks = & dotnet --list-sdks 2>$null
        return [bool]($sdks | Where-Object { $_ -match '^8\.' })
    }
    catch {
        return $false
    }
}

function Get-FileWithFallback {
    param(
        [string[]]$Urls,
        [string]$Destination
    )

    $lastError = $null
    foreach ($url in $Urls) {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Write-Host "Downloading from $url (attempt $attempt of 3)..."
                Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Destination -TimeoutSec 60
                if ((Test-Path -LiteralPath $Destination) -and ((Get-Item -LiteralPath $Destination).Length -gt 0)) {
                    return
                }
            }
            catch {
                $lastError = $_
                Write-Host "Download attempt failed: $($_.Exception.Message)" -ForegroundColor Yellow
                Start-Sleep -Seconds 2
            }
        }
    }

    throw "Unable to download the .NET installer from Microsoft or GitHub. Check DNS, firewall, or proxy settings. Last error: $($lastError.Exception.Message)"
}

$localDotNet = Join-Path $PSScriptRoot '.dotnet'
$localDotNetExe = Join-Path $localDotNet 'dotnet.exe'

if (-not (Test-DotNet8Sdk) -and (Test-Path -LiteralPath $localDotNetExe)) {
    $env:DOTNET_ROOT = $localDotNet
    $env:PATH = "$localDotNet;$env:PATH"
}

if (-not (Test-DotNet8Sdk)) {
    Write-Host ''
    Write-Host '.NET 8 SDK was not found. Downloading a private copy...' -ForegroundColor Yellow
    Write-Host 'This does not require administrator permission.'

    New-Item -ItemType Directory -Force -Path $localDotNet | Out-Null
    $installScript = Join-Path $env:TEMP 'dotnet-install-rathena.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Get-FileWithFallback -Urls @(
        'https://dot.net/v1/dotnet-install.ps1',
        'https://raw.githubusercontent.com/dotnet/install-scripts/main/src/dotnet-install.ps1'
    ) -Destination $installScript
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel 8.0 -Quality GA -InstallDir $localDotNet -NoPath

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $localDotNetExe)) {
        throw '.NET 8 SDK installation failed. Check the network connection and try again.'
    }

    $env:DOTNET_ROOT = $localDotNet
    $env:PATH = "$localDotNet;$env:PATH"
    Write-Host '.NET 8 SDK installation completed.' -ForegroundColor Green
}

if (-not (Test-DotNet8Sdk)) {
    throw '.NET 8 SDK is still unavailable after installation.'
}

$configPath = Join-Path $PSScriptRoot 'local-settings.json'

if (-not (Test-Path -LiteralPath $configPath)) {
    Write-Host ''
    Write-Host 'First-time setup: enter the rAthena database connection.' -ForegroundColor Yellow

    $server = Read-Host 'MySQL server (default: 127.0.0.1)'
    if ([string]::IsNullOrWhiteSpace($server)) { $server = '127.0.0.1' }

    $port = Read-Host 'MySQL port (default: 3306)'
    if ([string]::IsNullOrWhiteSpace($port)) { $port = '3306' }

    $database = Read-Host 'Database name (default: ragnarok)'
    if ([string]::IsNullOrWhiteSpace($database)) { $database = 'ragnarok' }

    $user = Read-Host 'Database user'
    $securePassword = Read-Host 'Database password' -AsSecureString
    $password = [System.Net.NetworkCredential]::new('', $securePassword).Password

    $config = [ordered]@{
        Server   = $server
        Port     = $port
        Database = $database
        User     = $user
        Password = $password
        Url      = 'http://127.0.0.1:5080'
    }
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
    Write-Host "Settings saved to $configPath" -ForegroundColor Green
}

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$env:ConnectionStrings__Rathena = "Server=$($config.Server);Port=$($config.Port);Database=$($config.Database);User ID=$($config.User);Password=$($config.Password);Allow User Variables=true;"

Write-Host ''
Write-Host 'Starting rAthena Player Admin...' -ForegroundColor Green
Write-Host "Address: $($config.Url)"
Write-Host 'Keep this window open while using the application.'

Start-Job -ScriptBlock {
    param($url)
    Start-Sleep -Seconds 5
    Start-Process $url
} -ArgumentList $config.Url | Out-Null

& dotnet run --urls $config.Url
if ($LASTEXITCODE -ne 0) {
    throw "Application exited with code $LASTEXITCODE."
}
