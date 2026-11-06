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
AppVersion=1.0.2
; 关键：设置 Setup.exe 自身的文件版本（鼠标悬停看到的版本号）
; 不加此项时默认 0.0.0.0
VersionInfoVersion=1.0.2.0
VersionInfoProductVersion=1.0.2.0
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

; 关闭 Inno 自带的 Restart Manager 自动关闭（默认 yes）。
; 它会把“仍加载 {app}\payload 下 DLL 的程序”（被注入的各应用）视为占用文件，
; 逐个尝试关闭，失败时弹出“安装程序无法自动关闭所有应用程序”的三按钮对话框。
; 但 payload 是通过删除硬链接名更新的，已加载的映像不受影响，这些程序无需关闭。
; 管理器自身由 [Code] 静默 taskkill；确实关不掉时，改在向导页内列出进程供手动关闭。
CloseApplications=no

; 安装/卸载时检测管理器是否正在运行，检测逻辑见文件末尾 [Code] 段
; （安装：InitializeSetup 静默关闭，失败则在向导页列出；卸载：询问后重试。）

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
; 删除历史版本的核心组件包：旧安装残留的 zip 不会被覆盖，目录里可能出现多个版本。
; 清掉它们，保证安装后 resources 里只剩本次随包发布的版本。
Type: files; Name: "{app}\resources\Hookshot-*.zip"
Type: files; Name: "{app}\resources\Pathwinder-*.zip"

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

var
  BlockedPage: TWizardPage;
  BlockedHint: TNewStaticText;
  BlockedList: TNewListBox;

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

// 取 CSV 行中第 Index 个字段（去掉引号），用于解析 tasklist 的输出
function CsvField(const Line: String; Index: Integer): String;
var
  I, Count, Start: Integer;
begin
  Result := '';
  Start := 1;
  Count := 0;
  for I := 1 to Length(Line) do
  begin
    if Line[I] = ',' then
    begin
      if Count = Index then
      begin
        Result := Copy(Line, Start, I - Start);
        StringChangeEx(Result, '"', '', True);
        Exit;
      end;
      Inc(Count);
      Start := I + 1;
    end;
  end;
  if Count = Index then
  begin
    Result := Copy(Line, Start, Length(Line) - Start + 1);
    StringChangeEx(Result, '"', '', True);
  end;
end;

// 列出仍在运行的管理器进程（tasklist 的 CSV 行）。没有任何实例时返回空数组。
// 用进程列表而不是互斥量判断：既决定是否显示提示页，也用来展示进程名与 PID。
function ListRunningManager(): TArrayOfString;
var
  ResultCode: Integer;
  OutFile: String;
  Raw: TArrayOfString;
  I: Integer;
  Line: String;
begin
  SetArrayLength(Result, 0);

  OutFile := ExpandConstant('{tmp}\pw_running_manager.txt');
  DeleteFile(OutFile);
  Exec(ExpandConstant('{cmd}'),
       '/C tasklist /fi "imagename eq ' + AppExeName + '" /fo csv /nh > "' + OutFile + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(OutFile) then Exit;
  if not LoadStringsFromFile(OutFile, Raw) then Exit;

  // 匹配的行以引号开头，形如 "PathwinderManager.exe","1234","Console","1","12,345 K"；
  // 没有匹配时 tasklist 输出提示文字，不以引号开头，会被过滤掉
  for I := 0 to GetArrayLength(Raw) - 1 do
  begin
    Line := Trim(Raw[I]);
    if (Length(Line) > 0) and (Line[1] = '"') then
    begin
      SetArrayLength(Result, GetArrayLength(Result) + 1);
      Result[GetArrayLength(Result) - 1] := Line;
    end;
  end;

  DeleteFile(OutFile);
end;

// 用当前仍在运行的进程刷新提示页列表
procedure RefreshBlockedList();
var
  Rows: TArrayOfString;
  I: Integer;
begin
  BlockedList.Items.Clear;
  Rows := ListRunningManager();
  for I := 0 to GetArrayLength(Rows) - 1 do
    BlockedList.Items.Add(CsvField(Rows[I], 0) + '    PID ' + CsvField(Rows[I], 1));
end;

procedure InitializeWizard();
begin
  BlockedPage := CreateCustomPage(wpReady, '关闭正在运行的程序',
    '自动关闭未能成功，安装程序需要先关闭下列程序才能继续。');
  BlockedHint := TNewStaticText.Create(BlockedPage);
  BlockedHint.Parent := BlockedPage.Surface;
  BlockedHint.Caption := '请手动关闭以下程序，然后点击“下一步”继续安装：';
  BlockedList := TNewListBox.Create(BlockedPage);
  BlockedList.Parent := BlockedPage.Surface;
  BlockedList.Left := 0;
  BlockedList.Top := BlockedHint.Top + BlockedHint.Height + ScaleY(8);
  BlockedList.Width := BlockedPage.SurfaceWidth;
  BlockedList.Height := BlockedPage.SurfaceHeight - BlockedList.Top;
  BlockedList.TabStop := False;
end;

// 安装前检测：检测到运行则静默强制关闭；关不掉时不在此时弹框，
// 交给向导页 CreateCustomPage(wpReady) 内联展示，由用户手动关闭后继续。
function InitializeSetup(): Boolean;
begin
  Result := True;
  if CheckForMutexes(AppMutexNames) then
    TerminateApp();
end;

// 没有正在运行的管理器时跳过该提示页
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = BlockedPage.ID then
    Result := GetArrayLength(ListRunningManager()) = 0;
end;

// 进入提示页时刷新列表（例如用户从“上一步”返回）
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = BlockedPage.ID then
    RefreshBlockedList();
end;

// 在提示页点“下一步”：再尝试一次静默关闭；仍有关不掉的实例则留在本页并刷新列表
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = BlockedPage.ID then
  begin
    if CheckForMutexes(AppMutexNames) then
      TerminateApp();
    if GetArrayLength(ListRunningManager()) > 0 then
    begin
      RefreshBlockedList();
      Result := False;
    end;
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
