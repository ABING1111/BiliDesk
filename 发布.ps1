# BiliDesk 发布脚本
#
# 一次产出两套(2026-09-26 定):
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#   2) 安装器载荷: 框架依赖(只含 app + LibVLC, ~67MB) —— 配 Publish\installer.iss 打 Inno 安装包,
#      .NET 8 桌面运行时由安装器检测、缺失时引导下载(见 installer.iss 的 [Code])。
#
# 产物统一落在仓库根的 Publish\ (installer.iss 就在那里, 它的相对路径按同级目录写):
#   Publish\BiliDesk-便携版\            Publish\BiliDesk-安装器-files\
#   Publish\BiliDesk-<ver>-portable.zip
# (2026-10-03 由 "Pulish" 更正为 "Publish" —— 这是拼写错误, 目录、脚本、.gitignore、.iss 一起改。)
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 [-Version 1.2.4]
#
# 注意: 这里的默认版本号要跟 App.AppVersion / csproj 的 <Version> / installer.iss 的
# MyAppVersion 一起改 —— 它只决定 zip 的文件名, 不参与程序内部显示。
param([string]$Version = "1.2.4")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Out = Join-Path $Root "Publish"
$PortableDir = Join-Path $Out "BiliDesk-便携版"
$InstallerDir = Join-Path $Out "BiliDesk-安装器-files"

# 只建目录, 不清理: Publish 下还放着 installer.iss, 任何 Remove-Item Publish\* 都会连带删掉它。
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }

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
Write-Host "  安装器载荷 : $InstallerDir"
Write-Host "  下一步     : 用 Inno Setup 编译 Publish\installer.iss (载荷目录与它同级)"
