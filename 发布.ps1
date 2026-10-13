# BiliDesk 发布脚本
#
# 一次产出两套:
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#   2) 安装器载荷: 框架依赖(只含 app + LibVLC) —— 配 Publish\installer.iss 打 Inno 安装包,
#      .NET 桌面运行时(8+)由安装器检测、全无时引导下载 .NET 10。
#
# ★★ 2026-10-13 两处改动, 都是为了"不会把旧文件混进新包":
#
# ① **版本号从工程里读**, 不再是脚本自己的默认值。
#    以前这里写死 param([string]$Version = "1.2.6"), 而 App.AppVersion / csproj 的
#    <Version> / installer.iss 的 MyAppVersion 是另外三处 —— 要改四处, 漏一处的后果是
#    "文件名写着 1.2.7、程序里显示 1.2.6", 或者安装器装的和发布的对不上。
#    现在只改 csproj 一处, 脚本自己读; 显式传参仍然优先(留给 CI 指定别的号)。
#
# ② **每轮发布用带版本号的独立输出目录**, 目录非空时先清空。
#    以前固定输出到 Publish\BiliDesk-便携版\ 与 Publish\BiliDesk-安装器-files\,
#    而脚本"只建目录不清理"(因为 Publish 下还放着 installer.iss, 任何 Publish\* 的
#    Remove-Item 都会连带删掉它)。于是**上一版残留的 dll 会被压进这一版的 zip** ——
#    用户装到的可能混着两个版本的程序集, 症状是"改了代码但行为没变"(加载了旧 dll)。
#    现在输出目录带版本号且每轮清空, 目录不同就不会有残留; installer.iss 也在带版本的
#    目录里, 彻底不与输出目录混放。
#
# 产物:
#   Publish\BiliDesk-<ver>-portable\        (自包含单文件, 中间产物)
#   Publish\BiliDesk-<ver>-portable.zip
#   Publish\BiliDesk-<ver>-installer\installer.iss
#   Publish\BiliDesk-<ver>-installer\files\   (安装器载荷)
#   Publish\BiliDesk-<ver>-installer\BiliDesk-<ver>-setup.exe   (本脚本自动编译)
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 -Version 1.2.8
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Out = Join-Path $Root "Publish"

# ---- 版本号: 显式参数 > csproj 的 <Version>(工程里的唯一真相源)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $proj = Join-Path $Root "BiliDesk\BiliDesk.csproj"
    $m = Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $m) { throw "在 $proj 里找不到 <Version>…</Version>, 无法确定版本号" }
    $Version = $m.Matches[0].Groups[1].Value.Trim()
}
Write-Host "== 发布版本 $Version(读自 BiliDesk.csproj) =="

# ---- 每轮独立的输出目录(见文件头 ②: 防止上一版残留混进新包)
$PortableDir = Join-Path $Out "BiliDesk-$Version-portable"
$InstallerDir = Join-Path $Out "BiliDesk-$Version-installer"
# 载荷放在安装器目录的 files\ 子目录里 —— 因为 installer.iss 就在这个目录, 而 .iss 里的
# Source 路径是**相对自身**写的。让载荷与 .iss 平级的话, 那个路径就得跟着版本号变,
# 每次升版本都要手改一次 .iss, 而"忘了改"的后果是装出一个空壳安装器。
$InstallerFiles = Join-Path $InstallerDir "files"

if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }
# 清空这两个目录(它们是本脚本自己的产物目录, 不含任何手写文件, 所以可以放心删)
foreach ($d in @($PortableDir, $InstallerDir, $InstallerFiles)) {
    if (Test-Path $d) {
        Write-Host "   清空旧输出 $d"
        Remove-Item $d -Recurse -Force
    }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

Write-Host "== 1/4 便携版(自包含 + 压缩) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $PortableDir
if ($LASTEXITCODE -ne 0) { throw "便携版发布失败" }

Write-Host "== 2/4 安装器载荷(依赖 .NET 8+ 桌面运行时) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $InstallerFiles
if ($LASTEXITCODE -ne 0) { throw "安装器载荷发布失败" }

Write-Host "== 3/4 打包便携版 =="
# 文件名用纯 ASCII: GitHub 会把非 ASCII 的资产名截掉(1.2.0 的 zip 在下载页显示成 "BiliDesk-1.2.0-.zip")
$zip = Join-Path $Out "BiliDesk-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip

# installer.iss 复制到安装器目录(源文件在 Publish 下)。复制而不是改原来的:
# 原来那份给"手工打开 Inno Setup"用, 这份保证路径与本轮目录结构一致。
Copy-Item (Join-Path $Out "installer.iss") (Join-Path $InstallerDir "installer.iss") -Force

Write-Host ""
Write-Host "完成:"
Write-Host "  便携版目录 : $PortableDir"
Write-Host "  便携版 zip : $zip"
Write-Host "  安装器目录 : $InstallerDir  (含 installer.iss, 载荷在 files\)"

# ---- 顺带把安装器也编出来(能编就编, 编不了不致命: 便携版已经可用)
# ★ 为什么在这一步编而不是让用户手动开 Inno Setup:
#   上一轮 1.2.5 / 1.2.6 的 setup.exe 都是这么出来的, 手工那一步很容易被跳过 ——
#   于是"zip 有了、setup.exe 还是上一个版本的"这种错配就是这么发生的。
#   编不出来(没装 Inno Setup)只警告不失败: 便携版仍然是完整可用的交付物。
$iscc = @(
    "D:\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    Write-Host ""
    Write-Host "== 4/4 编译安装器 =="
    # ★ 在安装器目录里执行: .iss 里的相对路径(SetupIconFile / Source)是**相对 .iss 自身**解析的,
    #   编译时的工作目录会影响它们, 所以切过去再跑。
    Push-Location $InstallerDir
    try {
        & $iscc "installer.iss"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败 (exit $LASTEXITCODE)" }
    } finally { Pop-Location }
    $setup = Join-Path $InstallerDir "BiliDesk-$Version-setup.exe"
    Write-Host "  安装器     : $setup"
} else {
    Write-Host ""
    Write-Host "!! 没找到 ISCC.exe, 跳过安装器编译(便携版不受影响)"
    Write-Host "   装了 Inno Setup 6 之后重跑本脚本即可, 或手动编译 $InstallerDir\installer.iss"
}
