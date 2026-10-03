; ============================================================
; BiliDesk 安装器 (Inno Setup 6)
;
; 前提: 先跑 发布.ps1 生成 "BiliDesk-安装器-files" (框架依赖版, 不含 .NET)。
; 本脚本把它打包成安装器, 并在安装前检测 .NET 8 桌面运行时 —— 缺失则引导下载
; (所以安装器本体能保持 ~30MB, 而不是把 55MB 的运行时硬塞进去)。
;
; 用 Inno Setup 打开本文件编译即可。
; ============================================================
#define MyAppName "BiliDesk"
#define MyAppVersion "1.2.3"
#define MyAppPublisher "ABing"
#define MyAppExeName "BiliDesk.exe"
; 指向发布脚本生成的框架依赖目录(相对本 .iss 所在目录)
#define MySourceDir "BiliDesk-安装器-files"

[Setup]
; AppId 的字面 "{" 必须写成 "{{"(Inno 常量转义), 否则编译报 Unknown constant
AppId={{B3C9A1E4-2E6B-4A11-8B11-9F0D1C2E3A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
; 文件名用纯 ASCII: GitHub 会把非 ASCII 的资产名截掉(1.2.0 的 zip 在下载页显示成 "BiliDesk-1.2.0-.zip")
OutputBaseFilename=BiliDesk-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 写 x64os 而不是已弃用的 x64 —— Inno 6.3+ 对 x64 会打弃用警告并自动替换成 x64os,
; 两者语义完全一致(都要求 OS 为 x64), 直接写 x64os 让编译输出干净。
; 别改成 x64compatible: 那个会允许在 ARM64 上以模拟方式安装, 而本包内含 x64 原生
; LibVLC 插件, 没在 ARM64 上验证过。
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; 应用是 x64-only
PrivilegesRequired=admin
SetupIconFile=..\BiliDesk\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

; 安装向导语言。Inno 默认不带简体中文语言包 —— 想要中文向导的话：
; 从 https://jrsoftware.org/files/istrans/ 下载 ChineseSimplified.isl 放到
; "C:\Program Files (x86)\Inno Setup 6\Languages\" 后，把下面这行解开即可。
; [Languages]
; Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 框架依赖发布目录(不含 .NET 运行时、含 libvlc/win-x64)
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// ---- 检测 .NET 8 桌面运行时(Microsoft.WindowsDesktop.App 8.x) ----
// 运行时安装在 {pf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.x.y
function HasWindowsDesktop8(baseDir: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  if not DirExists(baseDir) then Exit;
  if FindFirst(baseDir + '\8.*', Rec) then
  begin
    Result := True;
    FindClose(Rec);
  end;
end;

function IsDotNet8DesktopInstalled(): Boolean;
begin
  Result := HasWindowsDesktop8(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App'))
         or HasWindowsDesktop8(ExpandConstant('{pf32}\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function InitializeSetup(): Boolean;
var
  Url: String;
  Code: Integer;
begin
  Result := True;
  if IsDotNet8DesktopInstalled() then Exit;

  Url := 'https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/runtime-desktop-8.0.x-windows-x64-installer';
  if MsgBox('检测到尚未安装 .NET 8 桌面运行时，它是本应用运行所必需的。' + #13#10 +
            '点击「是」将打开下载页面（约 55MB），下载安装后重新运行本安装程序。',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', Url, '', '', SW_SHOW, ewNoWait, Code);
  end;
  Result := False;   // 中止安装, 等运行时装好再来
end;
