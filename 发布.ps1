# BiliDesk 发布脚本
#
# 一次产出两套(2026-09-26 定):
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#   2) 安装器载荷: 框架依赖(只含 app + LibVLC, ~67MB) —— 配 Publish\installer.iss 打 Inno 安装包,
#      .NET 桌面运行时(8+)由安装器检测、全无时引导下载 .NET 10(见 installer.iss 的 [Code];
#      程序在 csproj 配了 RollForward=Major, 只有 .NET 10+ 也能跑)。
#
# 产物统一落在仓库根的 Publish\ (installer.iss 就在那里, 它的相对路径按同级目录写):
#   Publish\BiliDesk-便携版\            Publish\BiliDesk-安装器-files\
#   Publish\BiliDesk-<ver>-portable.zip
# (2026-10-03 由 "Pulish" 更正为 "Publish" —— 这是拼写错误, 目录、脚本、.gitignore、.iss 一起改。)
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 [-Version 1.6.3]
#
# 注意: 这里的默认版本号要跟 App.AppVersion / csproj 的 <Version> / installer.iss 的
# MyAppVersion 一起改 —— 它只决定 zip 的文件名, 不参与程序内部显示。
param([string]$Version = "1.7.1", [switch]$FixIntegrity)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Out = Join-Path $Root "Publish"
$PortableDir = Join-Path $Out "BiliDesk-便携版"
$InstallerDir = Join-Path $Out "BiliDesk-安装器-files"

# Publish 根目录只建不清理: 里面还放着 installer.iss, 任何 Remove-Item Publish\* 都会连带删掉它。
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }

# ============================================================
# ★★★ 两个**产物目录**必须先清空 —— 2026-11-02 的事故就是漏了这一步
#
# dotnet publish -o 不是"干净输出": 它在原有目录上增量复制, 判据是"源文件更新才覆盖"。
# .NET 8 → .NET 10 升级后, 目录里留着 .NET 8 的 WPF 原生库
# (wpfgfx_cor3.dll / PresentationNative_cor3.dll / PenImc_cor3.dll, 文件版本 8.0.x),
# 它们的修改时间恰好比 .NET 10 运行时包里那几份**更晚** ⇒ 复制一直被跳过, 于是一直留在原地。
# 而单文件发布**不打包 WPF 原生库**(它们就是散在 exe 旁边的), 于是 .NET 10 的托管代码去调
# .NET 8 的 PresentationNative_cor3.dll 里根本不存在的导出 LsDisableSpecialCharacterLigature:
#     System.EntryPointNotFoundException: Entry point was not found.
#     at MS.Internal.WindowsBase.NativeMethodsSetLastError.LsDisableSpecialCharacterLigature(...)
#     at System.Windows.Interop.HwndTarget..ctor(...)   ← 首个窗口一显示就炸
# 1.5.1 便携版就是这么发出去的(1.5.0 同样中招)。清目录, 再配合下面第 3 步的版本校验。
# ============================================================
foreach ($dir in @($PortableDir, $InstallerDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

Write-Host "== 1/5 便携版(自包含 + 压缩) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -o $PortableDir
if ($LASTEXITCODE -ne 0) { throw "便携版发布失败" }

Write-Host "== 2/5 安装器载荷(依赖 .NET 10+ 桌面运行时) =="
dotnet publish (Join-Path $Root "BiliDesk\BiliDesk.csproj") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=false `
    -o $InstallerDir
if ($LASTEXITCODE -ne 0) { throw "安装器载荷发布失败" }

Write-Host "== 3/5 校验 WPF 原生库版本(防旧 .NET 残留混进包) =="
# 只查 WPF 那三个 dll: 它们的文件版本大版本号必须等于 app 目标框架的大版本(net10.0 → 10)。
# D3DCompiler_47_cor3.dll 是 Windows SDK 的、vcruntime140_cor3.dll 是 VC 运行时的,
# 版本号与 .NET 无关, 排除在外。
$tfm = ([xml](Get-Content (Join-Path $Root "BiliDesk\BiliDesk.csproj") -Raw)).Project.PropertyGroup |
       ForEach-Object { $_.TargetFramework } | Where-Object { $_ } | Select-Object -First 1
if (-not ($tfm -match '^net(\d+)\..*$')) { throw "无法从 csproj 读出 TargetFramework: $tfm" }
$expectedMajor = [int]$Matches[1]
$wpfNative = @("wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "PenImc_cor3.dll")
foreach ($dll in Get-ChildItem (Join-Path $PortableDir "*_cor3.dll") | Where-Object { $_.Name -in $wpfNative }) {
    $major = 0
    if ($dll.VersionInfo.FileVersion -match '^\s*(\d+)') { $major = [int]$Matches[1] }
    if ($major -ne $expectedMajor) {
        $msg = "便携版里 $($dll.Name) 是 $($dll.VersionInfo.FileVersion), 与 $tfm 不符 —— " +
               "输出目录混进了旧 .NET 的残留, 这种包发出去会启动即报 EntryPointNotFoundException。"
        throw $msg
    }
    Write-Host ("  {0,-28} {1}" -f $dll.Name, $dll.VersionInfo.FileVersion)
}

# ============================================================
# ★★★ 4/5 把产物从 Low 完整性提回 Medium —— **不做这一步, 发出去的包全是坏的**
#
# 为什么需要: 本机(DSH 沙箱)会给**工作区内**新建的文件打 Low 完整性标签
# (`Mandatory Label\Low Mandatory Level`)。带这个标签的 exe 一旦被跑起来,
# 进程就以**低完整性**运行, 于是:
#   ① 读不了机器级证书存储 ⇒ VLC 的 GnuTLS 无法校验 B 站证书 ⇒ **视频一帧都放不出**
#      (表现:"视频播放出错, 请检查网络或尝试其他清晰度")。
#      ★ 这正是"bin\Debug 下播放正常、便携版却放不出"的原因 —— 那个目录是 Medium
#        (2026-10-11 修过一次, 见长期记忆)。
#   ② Inno 安装包更直接: 它以低完整性运行, 连在 %TEMP% 下建自己的工作目录都被拒
#      ⇒ 报 `Setup was unable to create the directory "...\Temp\is-XXXX.tmp". Error 5: 拒绝访问。`
#
# 少了它, 本地怎么测都是好的, 只有用户拿到包才会炸 —— 2026-10-17 一次性踩掉
# "便携版放不出视频 + 安装包打不开"两件事, 就是这个原因。
#
# ★ 必须对**目录**递归设(-T), 不能只设文件: 目录本身带 Low 时, 之后新写进去的文件
#   (紧接着 ISCC 生成的 setup.exe)会**继承 Low**, 于是又白干了。
# ★ 顺序必须在"打包 zip / 编译安装器"**之前**, 否则后一步产出的文件又变成 Low。
# ★ 这条在 CI 或别的机器上跑是无害的空操作(那些文件本来就不是 Low)。
# ============================================================
Write-Host "== 4/5 提回 Medium 完整性(修 Low 标签导致的放不出视频 / 安装包打不开) =="
icacls $Out /setintegritylevel Medium /T /C | Out-Null

# ★★★ 注意: 这一步**管不到**稍后由 ISCC 生成的 setup.exe。
#   ISCC 在 Publish\ 下新建的 exe 没有显式完整性标签(只继承目录的 ACE),
#   而 `icacls <文件>` 查不到标签行时看起来像"没问题", 实际要靠父目录兜底。
#   2026-11-07 连续两次发布都发现 setup.exe 缺标签, 所以这里在脚本末尾**再补一次**
#   (见文件最后的 "6/6")。宁可多跑一条无害的命令, 也不要发一个可能打不开的安装包。

Write-Host "== 5/5 打包便携版 =="
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
Write-Host ""
Write-Host "★ 编译完安装器后, 记得再跑一次(ISCC 新建的 setup.exe 不带完整性标签):"
Write-Host "    powershell -ExecutionPolicy Bypass -File 发布.ps1 -FixIntegrity"
Write-Host ""

# ============================================================
# 独立开关: 只给 Publish\ 下**所有文件**补一次 Medium 完整性。
#
# 为什么需要单独一个入口: setup.exe 是 ISCC 在发布脚本**跑完之后**才生成的,
# 上面 4/5 那一步根本覆盖不到它。2026-11-07 连续两次发布都发现 setup.exe
# 缺显式标签(靠父目录兜底才没出事), 所以留一个可以随时补跑的命令。
# ============================================================
if ($FixIntegrity) {
    Write-Host "== 补完整性标签(Publish\ 下全部文件) =="
    icacls $Out /setintegritylevel Medium /T /C | Out-Null
    foreach ($f in Get-ChildItem $Out -Filter *.exe) {
        icacls $f.FullName /setintegritylevel Medium | Out-Null
        Write-Host ("  {0}" -f $f.Name)
    }
    Write-Host "完成。"
}
