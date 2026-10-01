# BiliDesk 发布脚本
#
# 一次产出两套(2026-09-26 定):
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#   2) 安装器载荷: 框架依赖(只含 app + LibVLC, ~67MB) —— 配 installer.iss 打 Inno 安装包,
#      .NET 8 桌面运行时由安装器检测、缺失时引导下载(见 installer.iss 的 [Code])。
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 [-Version 1.2.0]
#
# 注意: 这里的默认版本号要跟 App.AppVersion / csproj 的 <Version> / installer.iss 的
# MyAppVersion 一起改 —— 它只决定 zip 的文件名, 不参与程序内部显示。
param([string]$Version = "1.2.0")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Out = Join-Path $Root "发布包"
$PortableDir = Join-Path $Out "BiliDesk-便携版"
$InstallerDir = Join-Path $Out "BiliDesk-安装器-files"

Write-Host "== 1/3 便携版(自包含 + 压缩) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -o $PortableDir
if ($LASTEXITCODE -ne 0) { throw "便携版发布失败" }

Write-Host "== 2/3 安装器载荷(依赖 .NET 8 桌面运行时) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=false `
    -o $InstallerDir
if ($LASTEXITCODE -ne 0) { throw "安装器载荷发布失败" }

Write-Host "== 3/3 打包便携版 =="
# 文件名用纯 ASCII: GitHub 会把非 ASCII 的资产名截掉(1.2.0 的 zip 在下载页显示成 "BiliDesk-1.2.0-.zip")
$zip = Join-Path $Out "BiliDesk-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip

Write-Host ""
Write-Host "完成:"
Write-Host "  便携版目录 : $PortableDir"
Write-Host "  便携版 zip : $zip"
Write-Host "  安装器载荷 : $InstallerDir  (用 Inno Setup 编译 发布包\installer.iss)"
