# JevChatAnalyzer 构建脚本：x64 Debug/Release
# 用法：powershell -ExecutionPolicy Bypass -File scripts\build.ps1 [-Configuration Release]
param(
    [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root "JevChatAnalyzer.slnx") -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test (Join-Path $root "JevChatAnalyzer.slnx") -c $Configuration --no-build --filter "FullyQualifiedName!~JevChatAnalyzer.App"
exit $LASTEXITCODE
