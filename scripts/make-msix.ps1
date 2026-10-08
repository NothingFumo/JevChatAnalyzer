# JevChatAnalyzer MSIX 打包：自签证书 + 单项目 MSIX
# 前置：dotnet publish 支持 GenerateAppxPackageOnBuild（WindowsAppSDK 单项目打包）
# 用法：powershell -ExecutionPolicy Bypass -File scripts\make-msix.ps1
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$certThumbFile = Join-Path $env:LOCALAPPDATA "JevChatAnalyzer\packaging\cert-thumb.txt"

# 1) 自签代码签名证书（已存在则复用）
$existing = if (Test-Path $certThumbFile) { Get-Content $certThumbFile -Raw } else { $null }
$cert = if ($existing) { Get-Item "Cert:\CurrentUser\My\$($existing.Trim())" -ErrorAction SilentlyContinue } else { $null }
if (-not $cert) {
    Write-Host "创建自签代码签名证书 …"
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=JevChatAnalyzer, O=JevChatAnalyzer, C=CN" `
        -KeyUsage DigitalSignature -FriendlyName "JevChatAnalyzer 打包证书" `
        -CertStoreLocation "Cert:\CurrentUser\My" -HashAlgorithm SHA256 `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    New-Item -Force -ItemType Directory (Split-Path -Parent $certThumbFile) | Out-Null
    Set-Content $certThumbFile $cert.Thumbprint
    # 导出 .pfx 供打包器签名
    $password = ConvertTo-SecureString -String "JevChatAnalyzer" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath (Join-Path $root "src\JevChatAnalyzer.App\JevChatAnalyzer.pfx") -Password $password | Out-Null
    # 提示用户安装到受信任人存储（MSIX 安装前提）
    Write-Host "正在把证书安装到 本地计算机\受信任的人（需要管理员确认 UAC）…"
    $tempCer = Join-Path $env:TEMP "JevChatAnalyzer.cer"
    Export-Certificate -Cert $cert -FilePath $tempCer | Out-Null
    Import-Certificate -FilePath $tempCer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
}

# 2) 发布并生成 MSIX
dotnet publish (Join-Path $root "src\JevChatAnalyzer.App\JevChatAnalyzer.App.csproj") `
    -c $Configuration -p:Platform=x64 -p:PlatformName=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:PackageCertificateKeyFile=JevChatAnalyzer.pfx `
    -p:PackageCertificatePassword=JevChatAnalyzer
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nMSIX 输出目录：src\JevChatAnalyzer.App\AppPackages\"
Write-Host "安装：双击 .msix（证书已装到 TrustedPeople），或 Add-AppxPackage <路径>"
