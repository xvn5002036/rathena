$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ' rAthena 玩家資料管理工具' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host '找不到 .NET 8。請先安裝 .NET 8 SDK 或執行環境。' -ForegroundColor Red
    Write-Host '安裝完成後，再雙擊 Start.cmd。'
    exit 1
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

    $config = [ordered]@{
        Server = $server
        Port = $port
        Database = $database
        User = $user
        Password = $password
        Url = 'http://127.0.0.1:5080'
    }

    $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "設定已儲存：$configPath" -ForegroundColor Green
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$env:ConnectionStrings__Rathena = "Server=$($config.Server);Port=$($config.Port);Database=$($config.Database);User ID=$($config.User);Password=$($config.Password);Allow User Variables=true;"

Write-Host ''
Write-Host '正在啟動管理工具……' -ForegroundColor Green
Write-Host "管理網址：$($config.Url)"
Write-Host '關閉此視窗即可停止服務。'

Start-Job -ScriptBlock {
    param($url)
    Start-Sleep -Seconds 3
    Start-Process $url
} -ArgumentList $config.Url | Out-Null

dotnet run --urls $config.Url
