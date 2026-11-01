; Inno Setup 脚本 - Pathwinder 管理器（PathwinderManager）
; 打包步骤:
;   1) dotnet publish "PathwinderMgt.csproj" -c Release -o publish
;      或直接运行项目自带脚本 dotnet_publish.ps1
;   2) ISCC.exe "Inno Setup.iss"
; 输出: Output\PathwinderManager_<版本>.exe（版本号取自 PathwinderMgt.csproj）
;
; 权限策略（免管理员）：
;   PrivilegesRequired=lowest → 安装全程不弹 UAC，{autopf} 自动解析为
;   %LocalAppData%\Programs\PathwinderManager（当前用户可写目录）。
;   管理器以“exe 所在目录”为根写 config/runtime/data/logs，因此无需提权即可正常运行。

; 安装包文件名中的版本号：由 dotnet_publish.ps1 通过 /DManagerVersion= 传入。
; 直接用 ISCC 编译而未传参时回退为 "Setup"（输出 PathwinderManager_Setup.exe）。
#ifndef ManagerVersion
#define ManagerVersion "Setup"
#endif

[Setup]
AppName=Pathwinder管理器
AppVersion=1.0.1
; 关键：设置 Setup.exe 自身的文件版本（鼠标悬停看到的版本号）
; 不加此项时默认 0.0.0.0
VersionInfoVersion=1.0.1.0
VersionInfoProductVersion=1.0.1.0
VersionInfoCompany=Pathwinder
VersionInfoProductName=Pathwinder管理器
AppPublisher=Pathwinder
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
AppUpdatesURL=https://github.com/

; 默认安装目录：免管理员安装时 {autopf} = %LocalAppData%\Programs
DefaultDirName={autopf}\PathwinderManager
DefaultGroupName=Pathwinder管理器

; 安装包输出
OutputDir=Output
OutputBaseFilename=PathwinderManager_{#ManagerVersion}
; 安装包图标（路径相对于本 iss 文件所在目录）
SetupIconFile=app.ico
; 卸载时显示的图标
UninstallDisplayIcon={app}\PathwinderManager.exe

; 安装界面设置
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; 免管理员：按当前用户安装，不请求 UAC 提权（不提供任何提权切换入口，
; 避免装入 Program Files 等受保护目录后程序自身无法写 config/runtime/data）
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 安装/卸载时检测管理器是否正在运行，检测逻辑见文件末尾 [Code] 段
; （InitializeSetup / InitializeUninstall：检测到运行时询问是否自动关闭，
;  用 taskkill 终止进程后等待互斥量释放。免管理员安装无 Restart Manager
;  自动关闭能力，AppMutex 只弹标准提示框不会关程序，故不用。）

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
; 唯一附加选项：桌面快捷方式（开始菜单快捷方式始终创建）
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "附加选项:"; Flags: checkedonce

[Files]
; 把 publish 整个目录打包到安装目录（含 PathwinderManager.exe、resources\*.zip）
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; 安装前删除旧 payload（payload 与 runtime 工作区是硬链接，删除链接名不受“文件已加载”影响），
; 程序首次运行时从 resources 的 zip 重新解压，因此每次安装都会把核心组件刷新到最新。
; 被注入的程序仍在运行时：本次不中断安装，其下一次启动即用上新组件。
Type: filesandordirs; Name: "{app}\payload"

[Icons]
; 开始菜单快捷方式（始终创建，普通权限，无任何提权标志）
Name: "{group}\Pathwinder管理器"; Filename: "{app}\PathwinderManager.exe"; IconFilename: "{app}\PathwinderManager.exe"
; 桌面快捷方式（取决于用户勾选）
Name: "{autodesktop}\Pathwinder管理器"; Filename: "{app}\PathwinderManager.exe"; Tasks: desktopicon; IconFilename: "{app}\PathwinderManager.exe"
; 卸载入口（开始菜单）
Name: "{group}\卸载 Pathwinder管理器"; Filename: "{uninstallexe}"

[Run]
; 安装完成后可选启动程序（普通权限直接启动）
Filename: "{app}\PathwinderManager.exe"; Description: "立即启动 Pathwinder管理器"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清理运行时生成的目录与配置
; （如需保留用户配置 config.json，可注释掉对应行）
Type: filesandordirs; Name: "{app}\config.json"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\backups"
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\payload"

[Code]
const
  // 与 App.xaml.cs 的 CreateMutex 保持一致：优先 Global\，降级 Local\，最后无前缀
  AppMutexNames = 'Global\PathwinderManager_SingleInstance,Local\PathwinderManager_SingleInstance,PathwinderManager_SingleInstance';
  AppExeName = 'PathwinderManager.exe';

// 静默强制关闭正在运行的管理器进程（不弹任何提示框）。
// 关闭策略：
//   1) 普通 taskkill /f —— 适用于非管理员权限运行的 PW，免管理员安装同样可终止；
//   2) 提权 taskkill /f —— 适用于管理员权限运行的 PW（非管理员进程无法 TerminateProcess
//      管理员进程，必须经 UAC 提权；UAC 是系统安全机制，非本程序的确认对话框）；
//   3) 均失败则返回 False，由调用方决定是否提示用户手动关闭。
function TerminateApp(): Boolean;
var
  ResultCode: Integer;
  i: Integer;
begin
  Result := False;

  // 阶段 1：普通 taskkill（隐藏窗口，不弹任何提示）
  for i := 1 to 2 do
  begin
    Exec(ExpandConstant('{cmd}'), '/C taskkill /im ' + AppExeName + ' /f', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1200);
    if not CheckForMutexes(AppMutexNames) then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 阶段 2：提权 taskkill（经 UAC 提权后可终止管理员模式运行的 PW）
  ShellExec('runas', 'taskkill', '/im ' + AppExeName + ' /f', '',
            SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  if not CheckForMutexes(AppMutexNames) then
  begin
    Result := True;
    Exit;
  end;

  // 阶段 3：仍未关闭，返回 False 让调用方提示用户
end;

// 安装前检测：检测到运行则静默强制关闭，无法关闭才提示用户手动关闭
function InitializeSetup(): Boolean;
begin
  Result := True;
  while CheckForMutexes(AppMutexNames) do
  begin
    // 静默强制关闭（不弹任何提示框）
    if TerminateApp() then Break;

    // 无法自动关闭，提示用户手动关闭后重试
    if MsgBox('安装程序无法自动关闭正在运行的 Pathwinder管理器。' + #13#10#13#10 +
              '请手动关闭程序后点击"重试"，或点击"取消"退出安装。',
              mbConfirmation, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    // 重试：继续循环重新检测
  end;
end;

// 卸载前检测：逻辑同安装——静默强制关闭，无法关闭才提示手动关闭
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes(AppMutexNames) do
  begin
    if TerminateApp() then Break;

    if MsgBox('卸载程序无法自动关闭正在运行的 Pathwinder管理器。' + #13#10#13#10 +
              '请手动关闭程序后点击"重试"，或点击"取消"退出卸载。',
              mbConfirmation, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
