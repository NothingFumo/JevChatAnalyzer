# JevChatAnalyzer 构建与验证：停实例 → 构建 Core/App/测试 → 跑测试
# 用法：powershell -ExecutionPolicy Bypass -File scripts\build.ps1 [-Configuration Release] [-SkipTests]
param(
    [string]$Configuration = "Debug",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    Write-Host "==> $Description"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "失败：$Description" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# 运行中的应用会按住 Core.dll（MSB3027），先停掉
Get-Process -Name JevChatAnalyzer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 解决方案本身没有 Debug|x64 配置（只有 App 项目映射了 x64），所以逐个项目构建
Invoke-Step "构建 Core" {
    dotnet build (Join-Path $root "src\JevChatAnalyzer.Core\JevChatAnalyzer.Core.csproj") `
        -c $Configuration -p:Platform=x64 --nologo -v q
}
Invoke-Step "构建 App" {
    dotnet build (Join-Path $root "src\JevChatAnalyzer.App\JevChatAnalyzer.App.csproj") `
        -c $Configuration -p:Platform=x64 --nologo -v q
}

if ($SkipTests) {
    Write-Host "`n构建通过（已跳过测试）。" -ForegroundColor Green
    exit 0
}

Invoke-Step "构建测试工程" {
    dotnet build (Join-Path $root "tests\JevChatAnalyzer.Core.Tests\JevChatAnalyzer.Core.Tests.csproj") `
        -c $Configuration -p:Platform=x64 --nologo -v q
}
Invoke-Step "运行测试" {
    dotnet test (Join-Path $root "tests\JevChatAnalyzer.Core.Tests\JevChatAnalyzer.Core.Tests.csproj") `
        -c $Configuration -p:Platform=x64 --no-build --nologo -v q
}

Write-Host "`n构建与测试通过。" -ForegroundColor Green
