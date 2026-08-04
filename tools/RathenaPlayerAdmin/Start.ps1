$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ' rAthena 玩家資料管理工具' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan

function Test-DotNet8Sdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $false }
    try {
        $sdks = & dotnet --list-sdks 2>$null
        return [bool]($sdks | Where-Object { $_ -match '^8\.' })
    } catch { return $false }
}

$localDotNet = Join-Path $PSScriptRoot '.dotnet'
$localDotNetExe = Join-Path $localDotNet 'dotnet.exe'

if (-not (Test-DotNet8Sdk)) {
    if (Test-Path $localDotNetExe) {
        $env:DOTNET_ROOT = $localDotNet
        $env:PATH = "$localDotNet;$env:PATH"
    }
}

if (-not (Test-DotNet8Sdk)) {
    Write-Host ''
    Write-Host '未偵測到 .NET 8 SDK，正在自動下載並安裝到本工具資料夾……' -ForegroundColor Yellow
    Write-Host '不需要系統管理員權限，請保持網路連線。'

    New-Item -ItemType Directory -Force -Path $localDotNet | Out-Null
    $installScript = Join-Path $env:TEMP 'dotnet-install-rathena.ps1'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel 8.0 -Quality GA -InstallDir $localDotNet -NoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localDotNetExe)) {
        throw '.NET 8 SDK 自動安裝失敗。請確認網路、TLS 或防毒軟體是否阻擋下載。'
    }
    $env:DOTNET_ROOT = $localDotNet
    $env:PATH = "$localDotNet;$env:PATH"
    Write-Host '.NET 8 SDK 安裝完成。' -ForegroundColor Green
}

if (-not (Test-DotNet8Sdk)) {
    throw '已執行安裝，但仍無法偵測 .NET 8 SDK。'
}

$configPath = Join-Path $PSScriptRoot 'local-settings.json'

if (-not (Test-Path $configPath)) {
    Write-Host ''
    Write-Host '第一次啟動，請輸入 rAthena 資料庫資料。' -ForegroundColor Yellow
    $server = Read-Host 'MySQL 位址（預設 127.0.0.1）'
    if ([string]::IsNullOrWhiteSpace($server)) { $server = '127.0.0.1' }
    $port = Read-Host 'MySQL Port（預設 3306）'
    if ([string]::IsNullOrWhiteSpace($port)) { $port = '3306' }
    $database = Read-Host '資料庫名稱（預設 ragnarok）'
    if ([string]::IsNullOrWhiteSpace($database)) { $database = 'ragnarok' }
    $user = Read-Host '資料庫帳號'
    $securePassword = Read-Host '資料庫密碼' -AsSecureString
    $password = [System.Net.NetworkCredential]::new('', $securePassword).Password
    $config = [ordered]@{ Server=$server; Port=$port; Database=$database; User=$user; Password=$password; Url='http://127.0.0.1:5080' }
    $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "設定已儲存：$configPath" -ForegroundColor Green
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$env:ConnectionStrings__Rathena = "Server=$($config.Server);Port=$($config.Port);Database=$($config.Database);User ID=$($config.User);Password=$($config.Password);Allow User Variables=true;"

Write-Host ''
Write-Host '正在還原套件並啟動管理工具……' -ForegroundColor Green
Write-Host "管理網址：$($config.Url)"
Write-Host '關閉此視窗即可停止服務。'

Start-Job -ScriptBlock { param($url); Start-Sleep -Seconds 5; Start-Process $url } -ArgumentList $config.Url | Out-Null
& dotnet run --urls $config.Url
if ($LASTEXITCODE -ne 0) { throw "程式結束，錯誤代碼：$LASTEXITCODE" }
