- # Pathwinder Manager — 开发文档
## 1. 项目概述
### 1.1 目标
开发一个 Windows 桌面工具 `Pathwinder Manager`，让用户无需手动接触 Pathwinder/Hookshot 的 DLL 和配置文件，即可通过可视化界面完成：
1. 将任意已安装程序的快捷方式或 exe 拖入工具，一键为其添加上 Pathwinder 重定向能力。
2. 为每个程序配置“原始路径 → 目标路径”的重定向规则（全局默认规则 + 单程序覆盖规则）。
3. 工具自身集成 Hookshot 与 Pathwinder 的二进制文件，通过**硬链接**方式在管理器目录内的工作区加载，避免在每个程序目录里散落 DLL。
### 1.2 项目命名
- **项目根目录**：`C:\PathwinderMgt\`
- 主程序名：`PathwinderManager.exe`
- 命名空间：`PathwinderMgt`
### 1.3 核心约束（来自官方源码与 Wiki）
- Pathwinder 必须以 **Hookshot** 加载器启动才能生效；不能注入已在运行的进程。
- Pathwinder 依赖同目录下的 `Pathwinder.HookModule.{32|64}.dll` 和 `Pathwinder.ini`。
- 盘符根目录（`C:\`）不能作为 `OriginDirectory` 或 `TargetDirectory`。
- 父目录规则会自动覆盖其下所有子目录。
- 规则名必须唯一。
- 所有 `TargetDirectory` 之间不能有祖先/后代关系，必须完全不相关。
- 同一规则内 `FilePattern` 可多行，支持 `*` 和 `?`，非正则，不能含反斜杠。
- 路径和 FilePattern 大小写不敏感；规则名大小写敏感。
### 1.4 已确认的技术决策
| 项           | 选择                                      |
| :----------- | :---------------------------------------- |
| 技术栈       | C# + WPF (.NET 8)                         |
| 启动方式     | 快捷方式直接指向 `Hookshot.{32|64}.exe`   |
| 工作区       | 管理器目录内 `runtime\<程序ID>\` + 硬链接 |
| 快捷方式改写 | 覆盖原快捷方式 + 备份 + 改名标记          |
| 规则合并     | 单程序规则完全替换全局规则                |
| 写入探测     | 第一版不做，留接口                        |
| 位数检测     | 自动检测 PE 头 + 手动兜底                 |
| 二进制目录   | `payload\`（不叫 `bin\`）                 |
| 程序 ID      | 原 exe 路径的哈希                         |
| 迁移支持     | 支持整目录拷贝，启动时检测断链并重建      |
------
## 2. 非目标（Non-Goals）
以下内容明确**不在本工具范围内**：
1. 不注入已在运行的进程。Hookshot 只能拉起新进程，目标程序必须先关闭。
2. 第一版不做写入行为探测（ETW 或 Minifilter），只留接口。
3. 不修改目标程序目录里的任何文件。
4. 不提供 Hookshot/Pathwinder 的编译能力，只集成官方发布版二进制。
5. 不支持 ARM64（Hookshot 无 ARM64 版本）。
6. 不做规则导入/导出（后续可扩展）。
7. 不做图标叠加标记（第一版用 `P-` 前缀代替）。
8. 不处理注册表重定向（Pathwinder 目前只做文件系统）。
------
## 3. 插件集成（resources 目录）
### 3.1 resources 目录结构
```text
C:\PathwinderMgt\resources\
├── Hookshot-*.zip
├── Pathwinder-*.zip
├── Hookshot-<版本>\                ← 解压后，目录名由实际版本决定
│   ├── LICENSE
│   ├── README.md
│   ├── SDK\
│   │   ├── Include\Hookshot\*.h
│   │   └── Lib\Win32\Hookshot.32.lib、Lib\x64\Hookshot.64.lib
│   ├── ThirdParty\
│   ├── Win32\
│   │   ├── Hookshot.32.dll
│   │   ├── Hookshot.32.exe          ← 32 位加载器
│   │   └── HookshotLauncher.32.exe
│   └── x64\
│       ├── Hookshot.64.dll
│       ├── Hookshot.64.exe          ← 64 位加载器
│       └── HookshotLauncher.64.exe
└── Pathwinder-<版本>\
    ├── LICENSE
    ├── README.md
    ├── ThirdParty\
    ├── Win32\
    │   └── Pathwinder.HookModule.32.dll
    └── x64\
        └── Pathwinder.HookModule.64.dll
```
> 版本号不能写死当前使用的版本，使用通配符，只有两个压缩包，避免 MSBuild `Include` 匹配失败。
### 3.2 构建时的处理
项目里**不移动、不改名** resources 下的任何文件。构建后需要的二进制由 MSBuild 的 `Copy` 任务从 resources 复制到输出目录的 `payload\` 下。
`.csproj` 里的复制规则：
```xml
<ItemGroup>
  <!-- Hookshot x64 -->
  <None Include="resources\Hookshot-*\x64\Hookshot.64.exe">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Hookshot.64.exe</Link>
  </None>
  <None Include="resources\Hookshot-*\x64\Hookshot.64.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Hookshot.64.dll</Link>
  </None>
  <!-- Hookshot x86 -->
  <None Include="resources\Hookshot-*\Win32\Hookshot.32.exe">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Hookshot.32.exe</Link>
  </None>
  <None Include="resources\Hookshot-*\Win32\Hookshot.32.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Hookshot.32.dll</Link>
  </None>
  <!-- Pathwinder x64 / x86 -->
  <None Include="resources\Pathwinder-*\x64\Pathwinder.HookModule.64.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Pathwinder.HookModule.64.dll</Link>
  </None>
  <None Include="resources\Pathwinder-*\Win32\Pathwinder.HookModule.32.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>payload\Pathwinder.HookModule.32.dll</Link>
  </None>
</ItemGroup>
```
**不要**把 `HookshotLauncher.*.exe` 复制进 `payload\`——它是给开发者做 GUI 启动用的，命令行场景用 `Hookshot.XX.exe` 就够了。
### 3.3 发布后的最终目录布局
```text
C:\PathwinderMgt\
├── PathwinderManager.exe
├── PathwinderManager.dll
├── ...（WPF 运行时依赖）
├── payload\                              ← 从 resources 复制来的真身
│   ├── Hookshot.32.exe
│   ├── Hookshot.32.dll
│   ├── Hookshot.64.exe
│   ├── Hookshot.64.dll
│   ├── Pathwinder.HookModule.32.dll
│   ├── Pathwinder.HookModule.64.dll
│   └── version.json                      ← 记录当前 Hookshot/Pathwinder 版本
├── runtime\                              ← 每个程序的工作目录
│   └── <hash>\                           ← hash = 原 exe 路径的哈希
│       ├── Hookshot.64.exe                → 硬链接到 payload\Hookshot.64.exe
│       ├── Hookshot.64.dll                → 硬链接
│       ├── Pathwinder.HookModule.64.dll   → 硬链接
│       └── Pathwinder.ini                 ← 该程序独有
├── backups\
│   └── <hash>.lnk                        ← 原快捷方式备份
├── logs\
│   ├── manager.log
│   └── cleanup.log
└── config.json
```
------
## 4. 启动机制
### 4.1 快捷方式的实际指向
**不自己写 Launcher**。快捷方式直接指向 `payload\Hookshot.{32|64}.exe`。
但 Hookshot 的加载机制要求：**它从自身所在目录或目标 exe 所在目录寻找 hook module（即 `Pathwinder.HookModule.XX.dll`）和配置**。所以不能直接指向 `payload\Hookshot.64.exe`，否则所有程序共用一份 INI，无法区分。
因此采用**“按程序分工作区 + 硬链接”**的方式：
- 每个程序有独立的 `runtime\<hash>\`。
- 把 `payload\` 下的 `Hookshot.64.exe`、`Hookshot.64.dll`、`Pathwinder.HookModule.64.dll` 用硬链接放到该工作区。
- 在该工作区写入该程序独有的 `Pathwinder.ini`。
- 快捷方式指向 `runtime\<hash>\Hookshot.64.exe`。
### 4.2 快捷方式的参数格式
Hookshot 的命令行格式已确认：
```text
Hookshot.64.exe "C:\Program Files\SomeApp\SomeApp.exe" --original-arg
```
- 目标 exe 路径始终加引号。
- 原参数原样拼接，不做额外转义。
- Hookshot 自动匹配 32/64 位，不需要手动指定。
改写后的快捷方式：
```text
TargetPath  = C:\PathwinderMgt\runtime\<hash>\Hookshot.64.exe
Arguments   = "C:\Program Files\SomeApp\SomeApp.exe" --original-arg
WorkingDir  = C:\Program Files\SomeApp
```
### 4.3 位数选择
- 自动检测目标 exe 的 PE 头，`Machine` 字段 `0x014c` = x86，`0x8664` = x64。
- 按检测结果决定工作区里放 32 位还是 64 位的三个文件。
- **Hookshot 自身会自动匹配目标 exe 位数**，我们只需确保工作区里对应位数的 DLL 存在。
- 检测到 ARM64 时，提示“Hookshot 无 ARM64 版本，不支持”，并让用户手动选 x86/x64 兜底。
- 检测失败时 UI 提供手动下拉覆盖。
------
## 5. 功能模块设计
### 5.1 快捷方式管理模块
#### 5.1.1 拖入检测
- 用户拖入 `.lnk`，解析 `TargetPath`、`Arguments`、`WorkingDirectory`。
- 用户拖入 `.exe`，自动在桌面生成 `P-` 快捷方式，再走正常添加流程。
- 判断是否已带指令：`TargetPath` 是否位于 `C:\PathwinderMgt\runtime\` 下且文件名是 `Hookshot.XX.exe`。
#### 5.1.2 一键添加指令
1. **计算程序 ID**：`hash = SHA256(原 exe 完整路径)` 取前 16 位十六进制。
2. **检查残留**：若 `runtime\<hash>\` 已存在，先尝试删除；删不掉则报错，提示用户先关闭程序。
3. **备份**：复制原 `.lnk` 到 `backups\<hash>.lnk`。
4. **准备工作区**：
   - 创建 `runtime\<hash>\`。
   - 用 `CreateHardLinkW` 把 `payload\` 下对应的三个文件链接进来。
   - 生成 `Pathwinder.ini`（内容见 5.2.3）。
5. **改写快捷方式**：
   - `TargetPath` = `C:\PathwinderMgt\runtime\<hash>\Hookshot.XX.exe`
   - `Arguments` = `"<原程序路径>"`（+ 原参数）
   - `WorkingDirectory` = 原工作目录；若为空，填目标 exe 所在目录
   - `IconLocation` = 沿用原图标
6. **加视觉标记**：名称前缀 `P-`。
7. **写入 config.json**。
#### 5.1.3 一键移除指令
1. 从 `backups\<hash>.lnk` 恢复原快捷方式。
   - 若原路径已不存在，直接报错，让用户自己处理。
2. 从 `config.json` 移除记录。
3. 删除 `runtime\<hash>\`（若被占用则记入 `pendingCleanup`，启动时优先清）。
#### 5.1.4 单独操作 / 批量操作
- 列表每行提供“添加 / 移除”按钮。
- 顶部提供“全部添加 / 全部移除”。
#### 5.1.5 回显
- 读 `config.json` 得到被管理列表。
- 逐个校验快捷方式当前实际状态，以实际为准刷新 UI。
- 不一致时提示用户并询问是否重新同步。
- 快捷方式被改名/移动后不反查，以 config.json 为准，UI 提示“文件缺失”。
### 5.2 规则配置中心
#### 5.2.1 全局默认规则
在设置页维护一组全局规则。
#### 5.2.2 单程序覆盖规则
- `useGlobalRules = true` → 用全局规则。
- `useGlobalRules = false` → 只用 `overrideRules`，全局规则完全不参与。
- 提供“从全局规则复制一份”按钮。
- 全局规则改动时不弹窗提示，由用户自己清楚哪些程序是 `false`。
#### 5.2.3 生成的 INI 格式
`runtime\<hash>\Pathwinder.ini` 内容示例：
```ini
[FilesystemRule:AppData]
OriginDirectory = C:\Users\39313\AppData
TargetDirectory = D:\RedirectedData\AppData
[FilesystemRule:ProgramData]
OriginDirectory = C:\ProgramData
TargetDirectory = D:\RedirectedData\ProgramData
FilePattern = *.dat
FilePattern = *.log
```
每条 `overrideRules`（或全局规则）生成一个 `[FilesystemRule:名称]` 块。若规则含 `filePatterns`，每个 pattern 生成一行 `FilePattern = ...`。
#### 5.2.4 规则校验
- 绝对路径。
- 不是盘符根目录。
- 规则名必须唯一。
- 同一 INI 内 `OriginDirectory` 不重复。
- 所有 `TargetDirectory` 之间不能有祖先/后代关系，必须完全不相关。
- 所有 `TargetDirectory` 不能是任何规则的 `OriginDirectory`。
- `FilePattern` 不能含反斜杠。
### 5.3 清理与维护模块
#### 5.3.1 启动时清理
管理器每次启动时执行：
1. 读 `config.json`，收集所有有效程序 ID。
2. 处理 `pendingCleanup` 列表，优先删除这些 ID 对应的 `runtime\<hash>\`。
3. 扫描 `runtime\` 下所有子目录：
   - 子目录名不在有效 ID 列表 → 整个目录删除。
   - 子目录名有效 → 用 `GetFileInformationByHandle` 比较 VolumeSerialNumber + FileIndex，判断硬链接是否与 `payload\` 下真身一致；不一致则删了重建。
4. 扫描 `backups\`：对应 ID 不在配置中则删除。
#### 5.3.2 错误处理
- 目录被占用无法删除 → 记 `logs\cleanup.log`，跳过，并加入 `pendingCleanup`。
- 不因单个失败中断整体流程。
### 5.4 版本管理与升级
- `payload\version.json` 记录当前 Hookshot/Pathwinder 版本。
- 启动时与 `resources\` 下版本对比，提示用户升级。
- 升级后重建所有 `runtime\<hash>\` 下的硬链接。
------
## 6. 配置存储
`config.json`：
```json
{
  "globalRules": [
    {
      "name": "Global_AppData",
      "originDirectory": "C:\\Users\\39313\\AppData",
      "targetDirectory": "D:\\RedirectedData\\AppData",
      "filePatterns": []
    }
  ],
  "programs": [
    {
      "id": "a1b2c3d4e5f6a7b8",
      "displayName": "某程序",
      "originalShortcut": "C:\\Users\\39313\\Desktop\\某程序.lnk",
      "originalTarget": "C:\\Program Files\\SomeApp\\SomeApp.exe",
      "originalArguments": "",
      "originalWorkingDir": "C:\\Program Files\\SomeApp",
      "useGlobalRules": false,
      "overrideRules": [
        {
          "name": "AppData",
          "originDirectory": "C:\\Users\\39313\\AppData",
          "targetDirectory": "D:\\RedirectedData\\AppData",
          "filePatterns": []
        }
      ],
      "architecture": "x64"
    }
  ],
  "pendingCleanup": [],
  "settings": {}
}
```
> 路径不写进 config.json，运行时按 exe 所在目录推导。
------
## 7. 技术栈落地
| 需求              | 方案                                         |
| :---------------- | :------------------------------------------- |
| 框架              | .NET 8 + WPF                                 |
| 快捷方式解析      | 自写 `IShellLinkW` + `IPersistFile` 互操作   |
| 快捷方式创建/修改 | 同上                                         |
| JSON              | `System.Text.Json`                           |
| 硬链接            | P/Invoke `CreateHardLinkW`                   |
| 硬链接一致性检测  | P/Invoke `GetFileInformationByHandle`        |
| PE 位数检测       | 读 PE 头 `Machine` 字段                      |
| 并发保护          | 单实例 Mutex + 临时文件替换                  |
| 打包              | `dotnet publish -r win-x64 --self-contained` |
**不引入第三方 NuGet 包**（除 .NET 8 内置）。
------
## 8. UI 字段清单
### 8.1 主界面
| 区域   | 字段                                                         |
| :----- | :----------------------------------------------------------- |
| 顶部   | “全部添加指令”按钮、“全部移除指令”按钮、“打开日志目录”按钮   |
| 列表列 | 显示名、原 exe 路径、架构、状态（已受管/未受管/文件缺失）、操作（添加/移除只显示一个按钮互斥，详情按钮） |
| 底部   | 状态栏：受管程序数、上次清理时间                             |
## 9. 日志规范
- 目录：`logs\`
- 文件：
  - `manager.log`：日常操作（添加、移除、改写快捷方式、配置变更）
  - `cleanup.log`：启动清理记录
- 格式：`[时间] [级别] [模块] 消息`
- 级别：INFO / WARN / ERROR
- 轮转：单文件超过 5 MB 时重命名为 `.1`，保留最近 3 份。
------
## 10. 错误码与错误处理规范
| 错误码 | 含义             | UI 文案                                  |
| :----- | :--------------- | :--------------------------------------- |
| PW001  | 工作区目录被占用 | “无法删除工作区，请先关闭目标程序”       |
| PW002  | 硬链接创建失败   | “创建硬链接失败，请检查磁盘和权限”       |
| PW003  | 快捷方式改写失败 | “快捷方式改写失败，原快捷方式已备份”     |
| PW004  | INI 写入失败     | “规则写入失败，请检查工作区权限”         |
| PW005  | 备份恢复失败     | “原快捷方式路径不存在，请手动处理”       |
| PW006  | 权限不足         | “当前用户无权限写入 runtime 目录”        |
| PW007  | 位数检测失败     | “无法检测目标程序位数，请手动选择”       |
| PW008  | ARM64 不支持     | “Hookshot 无 ARM64 版本，不支持该程序”   |
| PW009  | 规则校验失败     | “规则不合法：<具体原因>”                 |
| PW010  | config.json 损坏 | “配置文件损坏，已备份为 config.json.bak” |
------
## 11. 开发路线图与验收标准
| 阶段 | 内容                                   | 验收标准                                           |
| :--- | :------------------------------------- | :------------------------------------------------- |
| 1    | resources 解压与 MSBuild 复制规则      | `payload\` 下出现 6 个二进制 + version.json        |
| 2    | 快捷方式拖入、解析、状态检测、位数检测 | 能正确识别已带指令的快捷方式；能检测 x86/x64/ARM64 |
| 3    | 快捷方式添加/移除指令 + 备份 + 改名    | 一键改写 `.lnk`，回显正确，备份文件存在            |
| 4    | 工作区准备：硬链接 + INI 生成          | `runtime\<hash>\` 就绪，硬链接与 payload 一致      |
| 5    | 规则配置 UI + config.json 读写         | 全局/单程序规则可配，校验生效，config 原子写       |
| 6    | 清理与维护模块                         | 启动时自动清理孤儿目录，断链重建                   |
| 7    | 联调测试                               | Process Monitor 中看到目标程序读取重定向后的路径   |
| 8    | 打包与文档                             | 发布版 + 用户手册                                  |
------
## 12. 风险与注意事项
1. **32/64 位必须匹配**：Hookshot 与 Pathwinder 分 32/64 位，必须与目标程序一致。
2. **Hookshot 只能拉起新进程**：目标程序已在运行时需提示用户先关闭。
3. **Hookshot 从自身目录或目标 exe 目录找 hook module 和配置**：按程序分工作区的原因。
4. **Pathwinder 配置错误**：会在桌面生成日志，UI 引导用户查看。
5. **硬链接同盘要求**：`payload\` 和 `runtime\` 都在 `C:\PathwinderMgt\` 下，天然同盘。
6. **权限**：管理器装在受保护目录时 `runtime\` 可能不可写，启动时检测并提示。
7. **Pathwinder 限制**：盘符根目录不能作为规则起点，UI 阻止。
8. **按用户账户动态解析路径**。
9. **整目录迁移**：支持，启动时检测断链并全部重建。
10. **ARM64**：Hookshot 无 ARM64 版本，检测到后提示不支持，允许手动选 x86/x64 兜底。
------
## 13. 后续可扩展方向
- 集成写入行为探测（ETW 或简化版 Minifilter）。
- 规则导入/导出。
- 图标叠加标记替代改名前缀。
- ARM64 支持（等 Hookshot 官方发布）。
### bug清单
1，操作列过短，默认长度，应该能直接显示，添加指令，详情，   按钮才对
2，软件窗口存在默认值，不能再缩小了
3，详情里面的，保存未生效，且文件统配是啥意思，没实际用处就删掉
4，全局规则，应存在一条不可删除，可编辑的：
```
 "originDirectory": "C:\\Users\\39313\\AppData",--用户名要系统获取
  "targetDirectory": "D:\\app\\PathwinderMgt\\AppData",
```
------
## 14. 问题与修复

### 14.1 快捷方式恢复窗口拖入显示禁止标志（已修复）

**现象**：从「说明」窗口打开「快捷方式恢复」窗口后，从资源管理器拖入快捷方式时光标显示禁止标志，无法拖入。主窗口（拖入登记）拖入正常。

**根因（两个独立问题叠加）**：

1. **TextBox 内部 TextEditor 截胡拖拽事件**：恢复窗口日志区使用 TextBox（LogBox），其内部 TextEditor 组件会在**冒泡阶段**接管拖拽事件并置 `Handled = true`，导致窗口级 `Drop` 事件永不触发，OLE 拖放目标返回 `None`（禁止光标）。主窗口无 TextBox 覆盖，冒泡事件（`DragOver`/`Drop`）可正常工作，因此不受影响。
2. **模态窗口干扰 OLE 拖放**：「说明」窗口以 `ShowDialog()` 模态打开时，其存活期间同线程其他窗口的 OLE 拖放目标返回 `None`，即使恢复窗口已用隧道事件正确处理也仍被拒绝。

**修复方案**：

1. 恢复窗口改用**隧道事件**处理拖拽，在隧道阶段标记 `Handled = true` 阻止 TextEditor 插手，并向 OLE 返回 `Copy` 效果（可放置光标）：
   - 修改位置：`Views/RestoreWindow.xaml` — `AllowDrop="True" PreviewDragOver="Window_PreviewDragOver" PreviewDrop="Window_PreviewDrop"`
   - 修改位置：`Views/RestoreWindow.xaml.cs` — 新增 `Window_PreviewDragOver` / `Window_PreviewDrop`，校验 `DataFormats.FileDrop` 后置 `e.Effects = Copy`、`e.Handled = true`
2. 打开恢复窗口时关闭模态「说明」窗口，使恢复窗口脱离模态环境：
   - 修改位置：`Views/AboutWindow.xaml.cs` — `BtnRestoreShortcut_Click` 中 `win.Show(); win.Activate();` 后追加 `Close();`
3. 管理员运行时系统 UIPI 会拦截资源管理器（中等完整性）的拖放消息，需显式放行：
   - 修改位置：`Views/RestoreWindow.xaml.cs` — `SourceInitialized` 中检测管理员身份后调用 `ChangeWindowMessageFilter` 放行 `WM_DROPFILES`（0x0233）、`WM_COPYDATA`（0x004A）、`WM_COPYGLOBALDATA`（0x0049）

**验证**：通过 UIA + 模拟拖拽自动化测试完整走通：打开恢复窗口 → 拖入受管快捷方式 → 日志输出「已恢复 → 桌面」→ 旧文件删除，`成功 1，失败 0`。

### 14.2 拷贝记录的 data 目标目录未按新显示名创建（已修复）

**现象**：拷贝「微信」得到「微信 1」后，`data` 目录下没有出现 `微信 1`，通过受管快捷方式运行也不创建；拷贝记录与源记录实际共用同一个 `data\微信` 目录。

**根因**：`Manager.NumberTargetDir` 的执行顺序错误。原代码先整段替换基础名（`微信` → `微信 1`），再用正则 `(?:\s\d+)+$` 去掉「旧编号链尾巴」——但替换后的新显示名 `微信 1` 末尾的 ` 1` 同样匹配该正则，被当作旧编号链误删，目标目录退回 `微信`。去编号链的正则本来是为了处理历史雪球命名（如 `微信 3 2`），却在正常路径上吞掉了新编号。

**修复**：交换顺序——先在旧字符串上去掉旧编号链，再整段替换基础名，新显示名的编号得以保留。

- 修改位置：`Services/Manager.cs` — `NumberTargetDir` 方法，`last = Regex.Replace(last, @"(?:\s\d+)+$", "").Replace(baseName, newName)`

**修复后行为**：

| 源目标目录末段 | 拷贝结果 |
| :--- | :--- |
| `微信` | `微信 1` |
| `微信 3 2`（历史雪球） | `微信 1` |
| 从「微信 1」再拷贝 | `微信 2` |

**注意**：修复前已生成的拷贝记录，其目标目录已错误指向源记录目录（如 `data\微信`），不受本修复影响，需删除拷贝记录重新拷贝，或在详情页手动修正目标路径。