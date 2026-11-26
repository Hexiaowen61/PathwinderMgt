using System;
using System.Collections.Generic;
using System.Windows;

namespace PathwinderMgt.Services
{
    /// <summary>
    /// 中英文切换：所有界面文案集中在本类的 Map 里，XAML 通过 {DynamicResource L_xxx} 引用、
    /// 代码通过 Loc.T("L_xxx") 引用。切换语言时把生成的资源字典整体替换进 Application.Resources，
    /// XAML 绑定会即时刷新；代码里动态赋值的文本由各窗口订阅 LanguageChanged 后重设。
    /// </summary>
    public static class Loc
    {
        public const string Zh = "zh-CN";
        public const string En = "en-US";

        /// <summary>当前生效的语言（默认简体中文）。</summary>
        public static string Current { get; private set; } = Zh;

        public static bool IsEnglish =>
            string.Equals(Current, En, StringComparison.OrdinalIgnoreCase);

        /// <summary>语言切换后触发：供代码里直接赋值文本的界面重新取文案。</summary>
        public static event Action LanguageChanged;

        /// <summary>把任意配置值规范化为 zh-CN / en-US，非法值按中文处理。</summary>
        public static string Normalize(string lang) =>
            string.Equals(lang, En, StringComparison.OrdinalIgnoreCase) ? En : Zh;

        /// <summary>取文案。缺 key 时返回 key 本身，便于开发期发现遗漏。</summary>
        public static string T(string key)
        {
            if (Map.TryGetValue(key, out var v)) return IsEnglish ? v.En : v.Zh;
            return key;
        }

        /// <summary>取带占位符的文案，例如 T("L_Main_Count", 3)。</summary>
        public static string T(string key, params object[] args)
        {
            string fmt = T(key);
            try { return string.Format(fmt, args); }
            catch (FormatException) { return fmt; }
        }

        /// <summary>应用语言（可重复调用）。只替换语言字典，主题字典保持不变。</summary>
        public static void Apply(string lang)
        {
            string name = Normalize(lang);
            var app = Application.Current;
            if (app == null) { Current = name; return; }

            var dict = new ResourceDictionary();
            foreach (var kv in Map)
                dict[kv.Key] = name == En ? kv.Value.En : kv.Value.Zh;

            var merged = app.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Contains(MarkerKey))
                    merged.RemoveAt(i);
            }
            merged.Add(dict);

            Current = name;
            LanguageChanged?.Invoke();
        }

        /// <summary>语言字典的判定键（不能用 Source 匹配，C# 构造的字典没有 Source）。</summary>
        private const string MarkerKey = "L_Main_Title";

        /// <summary>
        /// 全部文案。键名规则：L_窗口_语义。中英文必须一一对应。
        /// </summary>
        private static readonly Dictionary<string, (string Zh, string En)> Map = new()
        {
            // ---------------- 通用 ----------------
            ["L_Common_Ok"] = ("确定", "OK"),
            ["L_Common_Cancel"] = ("取消", "Cancel"),
            ["L_Common_Save"] = ("保存", "Save"),
            ["L_Common_Close"] = ("关闭", "Close"),
            ["L_Common_Browse"] = ("浏览...", "Browse..."),
            ["L_Common_Yes"] = ("是", "Yes"),
            ["L_Common_No"] = ("否", "No"),
            ["L_Common_Warning"] = ("提示", "Notice"),

            // ---------------- 主窗口 ----------------
            ["L_Main_Title"] = ("Pathwinder 管理器", "Pathwinder Manager"),
            ["L_Main_Subtitle"] = ("导入 .lnk 快捷方式或 .exe，即可添加 Pathwinder 重定向能力",
                                   "Import a .lnk shortcut or .exe to add Pathwinder redirection"),
            ["L_Main_Admin"] = ("当前权限：管理员", "Running as: Administrator"),
            ["L_Main_About"] = ("关于", "About"),
            ["L_Main_Settings"] = ("设置", "Settings"),
            ["L_Main_Refresh"] = ("刷新列表", "Refresh"),
            ["L_Main_Import"] = ("导入", "Import"),
            ["L_Main_AddAll"] = ("全部添加", "Add All"),
            ["L_Main_RemoveAll"] = ("全部移除", "Remove All"),
            ["L_Main_DeleteAll"] = ("全部删除", "Delete All"),

            // 列表列头
            ["L_Main_ColOp"] = ("操作", "Actions"),
            ["L_Main_ColStatus"] = ("状态", "Status"),
            ["L_Main_ColDisplay"] = ("显示名", "Display Name"),
            ["L_Main_ColArch"] = ("架构", "Arch"),
            ["L_Main_ColPath"] = ("原 exe 路径", "Source exe"),

            // 行内按钮
            ["L_Main_Detail"] = ("详情", "Details"),
            ["L_Main_AddCmd"] = ("添加指令", "Add Command"),
            ["L_Main_RemoveCmd"] = ("移除指令", "Remove Command"),

            // 状态标签
            ["L_Status_Managed"] = ("已受管", "Managed"),
            ["L_Status_NotManaged"] = ("未受管", "Not managed"),
            ["L_Status_Missing"] = ("文件缺失", "Missing file"),
            ["L_Status_NoPermission"] = ("无权限", "No permission"),
            ["L_Status_Excluded"] = ("不管理", "Ignored"),
            ["L_Main_StatusSummary"] = ("共 {0} 个程序，其中已受管 {1} 个", "{0} programs, {1} managed"),
            ["L_Main_StatusExcluded"] = ("，不管理 {0} 个", ", {0} ignored"),
            ["L_Main_LastCleanup"] = ("上次清理：{0}", "Last cleanup: {0}"),
            ["L_Main_LastCleanupFull"] = ("上次清理：{0}（重建 {1}，删除孤儿 {2}）",
                                          "Last cleanup: {0} (rebuilt {1}, removed {2} orphans)"),
            ["L_Main_NeverCleanup"] = ("尚未执行清理", "No cleanup yet"),

            // 主窗口补充文案
            ["L_Main_AdminElevated"] = ("当前权限：管理员", "Running as: Administrator"),
            ["L_Main_AdminNormal"] = ("当前权限：普通用户", "Running as: Standard user"),
            ["L_Main_AdminTipToNormal"] = ("点击切换为普通用户权限（会重启管理器）",
                                           "Click to switch to standard user (restarts the manager)"),
            ["L_Main_AdminTipToAdmin"] = ("点击切换为管理员权限（会重启管理器）",
                                          "Click to switch to administrator (restarts the manager)"),
            ["L_Main_StartupTitle"] = ("启动提示", "Startup notice"),
            ["L_Main_ChildAuthAdded"] = ("检测到 {0} 个子进程缺少 Hookshot 授权，已自动补建授权文件。请完全退出目标程序后，重新通过受管快捷方式启动，重定向才能覆盖全部子进程。",
                                         "{0} child processes were missing Hookshot authorization; the files were recreated. Fully exit the target program and launch it again from the managed shortcut so redirection covers all child processes."),
            ["L_Main_ChildAuthFailed"] = ("有 {0} 个子进程位于受保护目录，授权文件未能自动创建，详见 logs\\manager.log。",
                                          "{0} child processes are in protected folders and their authorization files could not be created. See logs\\manager.log."),
            ["L_Main_ImportDlgTitle"] = ("选择一个或多个快捷方式（.lnk）或可执行文件（.exe）",
                                         "Select one or more shortcuts (.lnk) or executables (.exe)"),
            ["L_Main_ImportDlgFilter"] = ("快捷方式和可执行文件 (*.lnk;*.exe)|*.lnk;*.exe|快捷方式 (*.lnk)|*.lnk|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                                          "Shortcuts and executables (*.lnk;*.exe)|*.lnk;*.exe|Shortcuts (*.lnk)|*.lnk|Executables (*.exe)|*.exe|All files (*.*)|*.*"),
            ["L_Main_AddResultTitle"] = ("添加结果", "Add result"),
            ["L_Main_Arm64Warn"] = ("「{0}」检测为 ARM64，Hookshot 无 ARM64 版本，请在详情中手动选择 x86/x64",
                                    "\"{0}\" was detected as ARM64; Hookshot has no ARM64 build. Choose x86/x64 in Details."),
            ["L_Main_OpFailed"] = ("操作失败", "Operation failed"),
            ["L_Main_OpFailedDetail"] = ("操作失败：{0}\n\n详细信息已写入 logs\\manager.log。",
                                         "Operation failed: {0}\n\nSee logs\\manager.log for details."),
            ["L_Main_RulesEmptyTitle"] = ("规则未配置", "No rules configured"),
            ["L_Main_RulesEmptyMsg"] = ("部分程序没有可用的生效规则（或规则路径为空），添加后启动时不会产生重定向。\n\n是否仍要继续？",
                                        "Some programs have no usable rules (or rule paths are empty); no redirection will occur at startup.\n\nContinue anyway?"),
            ["L_Main_NoAddTargets"] = ("没有需要添加指令的程序。", "No programs need a command."),
            ["L_Main_NoRemoveTargets"] = ("没有需要移除指令的程序。", "No programs have a command to remove."),
            ["L_Main_ConfirmRemoveAll"] = ("确认移除 {0} 个程序的指令？原快捷方式将从备份恢复。",
                                           "Remove commands from {0} programs? Original shortcuts will be restored."),
            ["L_Main_Confirm"] = ("确认", "Confirm"),
            ["L_Main_OpAddAll"] = ("全部添加", "Add All"),
            ["L_Main_OpRemoveAll"] = ("全部移除", "Remove All"),
            ["L_Main_BatchDone"] = ("{0}完成：成功 {1}/{2}", "{0} finished: {1}/{2} succeeded"),
            ["L_Main_BatchPartial"] = ("成功 {0} 个，失败 {1} 个：\n\n{2}", "{0} succeeded, {1} failed:\n\n{2}"),
            ["L_Main_BatchResultTitle"] = ("{0}结果", "{0} result"),
            ["L_Main_DeleteAllNoneExcluded"] = ("所有程序均已标记为“不管理”，没有可删除的记录。",
                                                "All programs are marked as ignored; nothing to delete."),
            ["L_Main_DeleteAllNone"] = ("当前没有任何记录可删除。", "There are no records to delete."),
            ["L_Main_DeleteAllConfirm"] = ("确定要删除所有 {0} 条记录吗？此操作不可撤销。\n\n受管程序的快捷方式将从备份恢复，工作区与备份文件将被清理。",
                                           "Delete all {0} records? This cannot be undone.\n\nManaged shortcuts will be restored and workspaces will be cleaned up."),
            ["L_Main_DeleteAllSkipped"] = ("\n\n已标记为“不管理”的 {0} 个程序将被跳过。",
                                           "\n\n{0} programs marked as ignored will be skipped."),
            ["L_Main_Deleting"] = ("删除中...", "Deleting..."),
            ["L_Main_DeletingAllStatus"] = ("正在删除全部记录...", "Deleting all records..."),
            ["L_Main_DeleteDoneStatus"] = ("已删除全部 {0} 条记录", "Deleted all {0} records"),
            ["L_Main_DeleteDoneMsg"] = ("已成功删除全部 {0} 条记录。", "Deleted all {0} records."),
            ["L_Main_DeleteCompleteTitle"] = ("删除完成", "Delete complete"),
            ["L_Main_DeletePartialStatus"] = ("删除完成：成功 {0}/{1}", "Delete finished: {0}/{1} succeeded"),
            ["L_Main_DeletePartialMsg"] = ("成功删除 {0} 条，失败 {1} 条：\n\n{2}",
                                           "Deleted {0}, failed {1}:\n\n{2}"),
            ["L_Main_DeleteResultTitle"] = ("删除结果", "Delete result"),
            ["L_Main_PrefixSaved"] = ("快捷方式前缀已保存，已有快捷方式将在下次启动时迁移为新前缀。",
                                      "Shortcut prefix saved. Existing shortcuts migrate to the new prefix on next launch."),
            ["L_Main_RefreshFailed"] = ("刷新失败", "Refresh failed"),

            // 主窗口提示
            ["L_Main_ImportTitle"] = ("导入快捷方式", "Import shortcuts"),
            ["L_Main_ImportFilter"] = ("快捷方式或程序 (*.lnk;*.exe)|*.lnk;*.exe|所有文件 (*.*)|*.*",
                                       "Shortcuts or programs (*.lnk;*.exe)|*.lnk;*.exe|All files (*.*)|*.*"),
            ["L_Main_DeleteAllTitle"] = ("确认删除全部记录", "Delete all records"),
            ["L_Main_DeleteAllMsg"] = ("将删除全部 {0} 条程序记录，受管快捷方式会被恢复。确定继续？",
                                       "This will delete all {0} records and restore managed shortcuts. Continue?"),
            ["L_Main_DeleteAllDone"] = ("已删除全部记录。", "All records deleted."),
            ["L_Main_AddAllDone"] = ("已为 {0} 个程序添加指令。", "Command added for {0} programs."),
            ["L_Main_RemoveAllDone"] = ("已移除 {0} 个程序的指令。", "Command removed for {0} programs."),
            ["L_Main_NoSelection"] = ("请先选择程序。", "Select a program first."),
            ["L_Main_AdminRestart"] = ("需要以管理员身份重启才能继续。", "Restarting as administrator is required."),
            ["L_Main_CleanupRunning"] = ("正在清理…", "Cleaning up..."),

            // ---------------- 设置窗口 ----------------
            ["L_Settings_Title"] = ("设置", "Settings"),
            ["L_Settings_Language"] = ("语言", "Language"),
            ["L_Settings_LanguageDesc"] = ("切换管理器的界面语言，点击后立即生效。点「保存」保存选择；点「取消」恢复为打开设置前的语言。",
                                           "Change the interface language. It applies immediately; click Save to keep it or Cancel to revert."),
            ["L_Settings_LangZh"] = ("简体中文", "简体中文"),
            ["L_Settings_LangEn"] = ("English", "English"),
            ["L_Settings_CurrentLang"] = ("当前语言：{0}", "Current language: {0}"),
            ["L_Settings_Appearance"] = ("外观", "Appearance"),
            ["L_Settings_AppearanceDesc"] = ("切换管理器的深色 / 浅色主题，点击后立即生效。点「保存」保存选择；点「取消」恢复为打开设置前的主题。",
                                             "Switch between dark and light themes. It applies immediately; click Save to keep it or Cancel to revert."),
            ["L_Settings_ThemeLight"] = ("当前主题：浅色", "Theme: Light"),
            ["L_Settings_ThemeDark"] = ("当前主题：深色", "Theme: Dark"),
            ["L_Settings_ThemeTipLight"] = ("点击切换为深色主题", "Click to switch to dark theme"),
            ["L_Settings_ThemeTipDark"] = ("点击切换为浅色主题", "Click to switch to light theme"),
            ["L_Settings_GlobalOrigin"] = ("全局默认路径", "Default Origin Path"),
            ["L_Settings_GlobalOriginDesc"] = ("新登记程序的默认重定向源目录（初始值 C:\\Users）。已登记程序的私有规则不受影响；选中目录后不会立即生效，点下方「保存」才写入配置并生效，重启后保持。点击右侧「浏览...」按钮选择目录。",
                                               "Default redirection source for newly registered programs (initially C:\\Users). Existing programs are unaffected. The change takes effect after clicking Save and persists across restarts."),
            ["L_Settings_RestoreDefault"] = ("恢复默认", "Reset"),
            ["L_Settings_GlobalOriginTip"] = ("全局默认路径（点击右侧浏览按钮选择目录）",
                                              "Default origin path (use Browse to pick a folder)"),
            ["L_Settings_Prefix"] = ("快捷方式前缀", "Shortcut Prefix"),
            ["L_Settings_PrefixLabel"] = ("前缀", "Prefix"),
            ["L_Settings_PrefixPreview"] = ("预览：{0}微信", "Preview: {0}WeChat"),
            ["L_Settings_PrefixDesc"] = ("最长 4 字符，不可包含文件系统非法符号（< > : \" / \\ | ? *）。留空恢复默认 ●",
                                          "Up to 4 characters; cannot contain < > : \" / \\ | ? *. Leave empty for the default ●"),
            ["L_Settings_RestoreTool"] = ("快捷方式恢复", "Shortcut Restore"),
            ["L_Settings_RestoreDesc"] = ("在打开的窗口中拖入带前缀的受管快捷方式，自动还原为普通快捷方式输出到桌面，并删除原文件。独立工具，不影响主列表与程序配置；失败原因会显示在窗口日志中。",
                                          "Drag managed shortcuts with the prefix into the opened window to restore them as normal shortcuts on the desktop. Independent tool; it does not affect the main list or program configuration."),
            ["L_Settings_OpenRestore"] = ("打开恢复窗口", "Open Restore Window"),
            ["L_Settings_Runtime"] = ("运行库提示", "Runtime Notice"),
            ["L_Settings_RuntimeDesc"] = ("Pathwinder 依赖 Microsoft Visual C++ 2015-2022 运行库（x64 / x86）。缺少时启动会弹窗提示并提供官方下载地址；勾选下方选项可不再提示。",
                                          "Pathwinder requires the Microsoft Visual C++ 2015-2022 runtime (x64 / x86). If missing, a prompt with the official download link appears at startup."),
            ["L_Settings_SuppressRuntime"] = ("启动时不再提示缺少运行库", "Don't remind me again"),
            ["L_Settings_SaveTip"] = ("设置项没有改动时不可保存", "Save is disabled when nothing has changed"),
            ["L_Settings_SavedHint"] = ("已恢复为已保存的路径。", "Restored the saved path."),
            ["L_Settings_NoBuiltinRule"] = ("未找到内置全局规则，无法保存。", "Built-in global rule not found; cannot save."),
            ["L_Settings_PathNotExist"] = ("所选目录不存在。", "The selected folder does not exist."),
            ["L_Settings_PathUnchanged"] = ("路径未变化。", "Path unchanged."),
            ["L_Settings_PathSaved"] = ("已保存，新登记的程序将默认使用该路径。",
                                         "Saved. New programs will use this path by default."),
            ["L_Settings_PathEmpty"] = ("全局默认路径不能为空。", "The default origin path cannot be empty."),
            ["L_Settings_PathInvalidChars"] = ("路径包含非法字符。", "The path contains invalid characters."),
            ["L_Settings_PathNotAbsolute"] = ("必须是绝对路径（如 C:\\Users）。", "Must be an absolute path (e.g. C:\\Users)."),
            ["L_Settings_PathRoot"] = ("不能设置为盘符根目录（如 C:\\）。", "Cannot be a drive root (e.g. C:\\)."),
            ["L_Settings_PathNested"] = ("不能设置为管理器安装目录或其上级目录。",
                                          "Cannot be the manager install folder or one of its parents."),
            ["L_Settings_PickOrigin"] = ("选择全局默认路径", "Choose default origin path"),
            ["L_Settings_SaveFailed"] = ("保存失败", "Save failed"),

            // ---------------- 程序详情窗口 ----------------
            ["L_Prog_Title"] = ("程序详情", "Program Details"),
            ["L_Prog_Copy"] = ("拷贝", "Duplicate"),
            ["L_Prog_Name"] = ("名称", "Name"),
            ["L_Prog_Id"] = ("程序 ID", "Program ID"),
            ["L_Prog_Arch"] = ("位数", "Architecture"),
            ["L_Prog_ArchWarn"] = ("ARM64 不受支持，请手动选择 x86 或 x64",
                                   "ARM64 is not supported; please choose x86 or x64"),
            ["L_Prog_Mode"] = ("模式切换", "Redirect Mode"),
            ["L_Prog_ModeSimple"] = ("Simple（严格隔离，默认）", "Simple (strict isolation, default)"),
            ["L_Prog_ModeSimplePro"] = ("SimplePro（读回退，不隔离）", "SimplePro (read fallback, no isolation)"),
            ["L_Prog_ModeOverlay"] = ("Overlay（合并目录视图）", "Overlay (merged directory view)"),
            ["L_Prog_ModeCustom"] = ("自定义", "Custom"),
            ["L_Prog_Target"] = ("目标 exe", "Target exe"),
            ["L_Prog_Shortcut"] = ("快捷方式", "Shortcut"),
            ["L_Prog_Command"] = ("指令内容", "Command"),
            ["L_Prog_NoCommand"] = ("（未添加指令）", "(no command added)"),
            ["L_Prog_HookshotLog"] = ("Hookshot 日志", "Hookshot Log"),
            ["L_Prog_HookshotLogContent"] = ("启用 Hookshot 诊断日志（排查注入问题时开启，桌面会产生日志，管理器自动清理）",
                                             "Enable Hookshot diagnostic log (for injection troubleshooting; logs appear on the desktop and are cleaned up automatically)"),
            ["L_Prog_ManageMark"] = ("管理标记", "Manage Flag"),
            ["L_Prog_ManageMarkContent"] = ("标记为“不管理”（主列表仅保留“详情”按钮，批量操作将自动跳过该程序）",
                                            "Mark as ignored (only Details remains; bulk actions skip this program)"),
            ["L_Prog_Apply"] = ("添加指令", "Add Command"),
            ["L_Prog_Update"] = ("更新指令", "Update Command"),
            ["L_Prog_Remove"] = ("移除指令", "Remove Command"),
            ["L_Prog_Launch"] = ("启动", "Launch"),
            ["L_Prog_Delete"] = ("删除记录", "Delete Record"),
            ["L_Prog_DeleteTitle"] = ("删除记录", "Delete Record"),
            ["L_Prog_DeleteMsg"] = ("确定删除该程序记录？受管快捷方式会被恢复。",
                                    "Delete this program record? Managed shortcuts will be restored."),
            ["L_Prog_CopySuffix"] = ("（副本）", " (copy)"),

            // 程序详情窗口补充文案
            ["L_Prog_InstrTarget"] = ("目标：{0}", "Target: {0}"),
            ["L_Prog_InstrArgs"] = ("参数：{0}", "Arguments: {0}"),
            ["L_Prog_InstrWorkDir"] = ("工作目录：{0}", "Working directory: {0}"),
            ["L_Prog_InstrReadFail"] = ("（已受管，但读取快捷方式失败）", "(Managed, but reading the shortcut failed)"),
            ["L_Prog_SyncFailedTitle"] = ("同步失败", "Sync failed"),
            ["L_Prog_SyncFailedMsg"] = ("规则已保存，但同步到工作区失败：{0}\n可点击「更新指令」重试。",
                                        "Rules saved, but syncing to the workspace failed: {0}\nClick \"Update Command\" to retry."),
            ["L_Prog_ModeUpdateFailed"] = ("更新重定向模式失败：{0}", "Failed to update redirect mode: {0}"),
            ["L_Prog_LogUpdateFailed"] = ("更新 Hookshot 日志设置失败：{0}", "Failed to update the Hookshot log setting: {0}"),
            ["L_Prog_ExcludedTitle"] = ("标记为不管理", "Mark as ignored"),
            ["L_Prog_ExcludedMsg"] = ("该程序当前已添加指令，标记为“不管理”后已有指令保持不变；\n如需恢复原快捷方式，可先取消标记再移除指令。\n\n确认标记？",
                                      "This program already has a command. Marking it as ignored keeps the command intact;\nto restore the original shortcut, unmark it and remove the command first.\n\nMark it?"),
            ["L_Prog_ModeCustomConfirm"] = ("将清除各规则的独立模式，统一为「{0}」，是否继续？",
                                            "This clears per-rule modes and unifies them to \"{0}\". Continue?"),
            ["L_Prog_AddedMsg"] = ("指令已添加，快捷方式已改写并加上前缀标记。",
                                   "Command added; the shortcut was rewritten with the prefix."),
            ["L_Prog_UpdatedMsg"] = ("指令已更新，工作区与快捷方式已按最新规则重新生成。",
                                     "Command updated; workspace and shortcut regenerated with the latest rules."),
            ["L_Prog_Done"] = ("完成", "Done"),
            ["L_Prog_RemoveConfirm"] = ("确认移除该程序的 Pathwinder 指令？原快捷方式将从备份恢复。",
                                        "Remove the Pathwinder command? The original shortcut will be restored."),
            ["L_Prog_Confirm"] = ("确认", "Confirm"),
            ["L_Prog_RunningTitle"] = ("程序已在运行", "Program is running"),
            ["L_Prog_RunningMsg"] = ("检测到目标程序已在运行，Hookshot 无法注入已运行的进程。\n\n仍要通过快捷方式启动？",
                                     "The target program is already running; Hookshot cannot inject into a running process.\n\nLaunch through the shortcut anyway?"),
            ["L_Prog_DeleteConfirm"] = ("确认删除该程序的全部记录？\n\n若当前仍受管，会先恢复原快捷方式并清理工作区。",
                                        "Delete all records for this program?\n\nIf still managed, the original shortcut will be restored and the workspace cleaned up."),

            // ---------------- 规则面板 ----------------
            ["L_Rules_Add"] = ("新建规则", "New Rule"),
            ["L_Rules_Delete"] = ("删除选中", "Delete Selected"),
            ["L_Rules_ColName"] = ("规则名", "Rule Name"),
            ["L_Rules_ColOrigin"] = ("原始路径", "Origin Path"),
            ["L_Rules_ColTarget"] = ("目标路径", "Target Path"),
            ["L_Rules_Name"] = ("规则名", "Rule Name"),
            ["L_Rules_Mode"] = ("模式", "Mode"),
            ["L_Rules_ModeTip"] = ("规则级重定向模式（仅「模式切换」为自定义时可编辑；否则跟随程序级设置）",
                                   "Per-rule redirection mode (editable only when the program mode is Custom)"),
            ["L_Rules_Origin"] = ("原始路径", "Origin Path"),
            ["L_Rules_Target"] = ("目标路径", "Target Path"),
            ["L_Rules_Save"] = ("保存", "Save"),

            // 规则面板补充文案
            ["L_Rules_BuiltInSuffix"] = ("（内置）", " (built-in)"),
            ["L_Rules_NewRule"] = ("新规则", "New rule"),
            ["L_Rules_PickFirst"] = ("请先在列表中选中一条规则。", "Select a rule in the list first."),
            ["L_Rules_BuiltInNoDelete"] = ("该规则是内置规则，可编辑但不可删除。",
                                           "This is a built-in rule; it can be edited but not deleted."),
            ["L_Rules_SaveEmpty"] = ("请先在上方列表选中一条规则；或填写规则内容后点“保存”新建。",
                                     "Select a rule above, or fill in the fields and click Save to create one."),
            ["L_Rules_Validate"] = ("规则校验", "Rule validation"),

            // ---------------- 快捷方式恢复窗口 ----------------
            ["L_Restore_Title"] = ("快捷方式恢复", "Restore Shortcuts"),
            ["L_Restore_Hint"] = ("请拖入需要恢复的快捷方式（支持一次拖入多个），恢复结果输出到桌面",
                                  "Drag the shortcuts to restore here (multiple allowed); restored shortcuts are saved to the desktop"),
            ["L_Restore_DragHint"] = ("请拖入需要恢复的快捷方式", "Drag shortcuts to restore here"),
            ["L_Restore_Busy"] = ("正在处理上一批，请稍候…", "Still processing the previous batch, please wait..."),
            ["L_Restore_SkipNonLnk"] = ("跳过（非 .lnk 文件）：{0}", "Skipped (not a .lnk file): {0}"),
            ["L_Restore_Start"] = ("开始处理 {0} 个快捷方式…", "Processing {0} shortcuts..."),
            ["L_Restore_Ok"] = ("已恢复：{0} → {1}", "Restored: {0} → {1}"),
            ["L_Restore_Warn"] = ("（警告：{0}）", " (warning: {0})"),
            ["L_Restore_Fail"] = ("恢复失败：{0} — {1}", "Restore failed: {0} — {1}"),
            ["L_Restore_Done"] = ("本批完成：成功 {0}，失败 {1}", "Batch finished: {0} succeeded, {1} failed"),

            // ---------------- 关于窗口 ----------------
            ["L_About_Title"] = ("关于 Pathwinder 管理器", "About Pathwinder Manager"),
            ["L_About_ClickToCopy"] = ("点击复制", "Click to copy"),
            ["L_About_About"] = ("基本信息", "Basic Information"),
            ["L_About_Group"] = ("群 301802646", "Group 301802646"),
            ["L_About_Coolapk"] = ("酷安 Hexiaowen", "Coolapk Hexiaowen"),
            ["L_About_Homepage"] = (" — 个人主页", " — Homepage"),
            ["L_About_Video"] = (" — 视频教学", " — Video tutorial"),
            ["L_About_Releases"] = ("发布地址", "Releases"),
            ["L_About_Share"] = ("分享给好友", "Share"),
            ["L_About_OpenLogs"] = ("打开日志目录", "Open Log Folder"),
            ["L_About_CheckUpdate"] = ("检查更新", "Check for Updates"),
            ["L_About_Intro"] = ("应用介绍", "Overview"),
            ["L_About_Intro1"] = ("Pathwinder 管理器是一款 Windows 桌面工具，让你通过可视化界面，为任意已安装程序添加文件路径重定向能力——无需修改程序代码，即可改变程序的文件读写路径。",
                                  "Pathwinder Manager is a Windows desktop tool that adds file path redirection to any installed program through a visual interface."),
            ["L_About_Intro2"] = ("举个例子：大多数程序会在 “C:\\Users\\用户名\\AppData” 下创建数据文件，而本工具能将这些文件重定向到指定目录，且不影响程序正常运行。",
                                  "For example, most programs create data files under C:\\Users\\<name>\\AppData. This tool redirects those files elsewhere without affecting the program."),
            ["L_About_Intro3"] = ("简单来说，如果程序在某个目录（如 “C:\\ddd\\eee\\fff”）下创建了数据文件，而你不想让这些文件写进该目录，就可以用本工具把它们重定向到别处。",
                                  "In short, if a program writes data into a folder you don't want it to use, redirect those writes elsewhere."),
            ["L_About_Intro4"] = ("担心涉及的文件夹太多？你可以添加多条重定向规则，或直接指定一个上层目录（如 “C:\\ddd”）一并覆盖。",
                                  "Too many folders involved? Add multiple rules or cover them with a parent folder."),
            ["L_About_Intro5"] = ("拖入程序的快捷方式（.lnk）或可执行文件（.exe），配置重定向规则后，一键改写快捷方式，让目标程序在启动时自动应用路径重定向规则。",
                                  "Drop a .lnk or .exe, configure rules, then rewrite the shortcut so the program applies redirection at startup."),
            ["L_About_Notes"] = ("使用注意事项", "Notes"),
            ["L_About_Note1"] = ("• 许多程序在安装时会默认把程序文件装进用户目录（典型路径 “C:\\Users\\用户名\\AppData\\Local\\Programs”）。这会让本工具内置的“C:\\Users”重定向规则与程序启动路径重叠冲突，导致程序启动失败。凡是安装在 “C:\\Users” 下的程序，都需要手动配置重定向规则才能正常启动。",
                                 "• Many programs install into the user folder (e.g. C:\\Users\\<name>\\AppData\\Local\\Programs), which conflicts with the built-in C:\\Users rule. Programs installed there need custom rules."),
            ["L_About_Note2"] = ("解决办法有两种：", "Two solutions:"),
            ["L_About_Note2a"] = ("1. 手动把程序复制到非 “C:\\Users” 目录。", "1. Copy the program to a folder outside C:\\Users."),
            ["L_About_Note2b"] = ("2. 在重定向规则中移除 “C:\\Users” 目录，改为手动定义重定向规则。",
                                  "2. Remove the C:\\Users rule and define rules manually."),
            ["L_About_Note3"] = ("• 加载器只能拉起新进程，目标程序必须先关闭，再通过受管快捷方式冷启动。",
                                 "• The loader can only start new processes; close the program and launch it from the managed shortcut."),
            ["L_About_Note4"] = ("• 盘符根目录（如 C:\\）不能作为重定向规则的起点或目标。",
                                 "• A drive root (e.g. C:\\) cannot be an origin or target."),
            ["L_About_Note5"] = ("• 若目标程序需要管理员权限运行，受管快捷方式也需以管理员身份运行，否则注入失败。",
                                 "• If the program needs admin rights, the managed shortcut must run as administrator too."),
            ["L_About_Note6"] = ("• 修改规则后即时写入工作区配置；已运行的程序需重启后按新规则重定向。",
                                 "• Rule changes are written immediately; running programs apply them after restart."),
            ["L_About_Note7"] = ("• 请勿将本工具安装到需要管理员权限的目录（如 Program Files），推荐安装到用户目录或 D 盘。",
                                 "• Do not install into a protected folder (e.g. Program Files); prefer a user folder or drive D."),
            ["L_About_OpenSource"] = ("开源地址", "Open Source"),
            ["L_About_RepoGui"] = (" — 本项目（图形界面与管理器）", " — This project (GUI and manager)"),
            ["L_About_RepoInject"] = (" — 通用 DLL 注入与函数调用拦截库", " — Generic DLL injection and hooking library"),
            ["L_About_RepoRedirect"] = (" — 文件、目录及注册表路径重定向", " — File, directory and registry redirection"),
            ["L_About_Thanks"] = ("本工具基于 .NET 8 + WPF 开发，集成上述两个开源项目，感谢开源作者的贡献，欢迎关注。",
                                  "Built with .NET 8 + WPF. Thanks to the open-source authors of the projects above."),
            ["L_About_Changelog"] = ("更新记录", "Changelog"),

            // 关于窗口补充文案
            ["L_About_TitleVersion"] = ("关于 Pathwinder 管理器 v{0}", "About Pathwinder Manager v{0}"),
            ["L_About_Checking"] = ("检查中…", "Checking..."),
            ["L_About_NoRelease"] = ("暂未发布任何版本，可前往下载页查看：\n{0}",
                                     "No releases yet. Visit the releases page:\n{0}"),
            ["L_About_BadTag"] = ("未能识别最新版本号（{0}）。\n可前往下载页查看：{1}",
                                  "Could not parse the latest version ({0}).\nVisit the releases page: {1}"),
            ["L_About_NoNotes"] = ("（该版本未提供更新说明）", "(No release notes for this version)"),
            ["L_About_NewVersion"] = ("发现新版本 {0}（当前 {1}）。\n\n{2}\n\n点击「是」打开浏览器下载，点击「否」取消。",
                                      "New version {0} available (current {1}).\n\n{2}\n\nClick Yes to open the download page, or No to cancel."),
            ["L_About_UpToDate"] = ("当前已是最新版本（{0}）。", "You are up to date ({0})."),
            ["L_About_CheckFailed"] = ("检查更新失败，请检查网络连接后重试。\n也可以直接打开下载页查看最新版本：{0}",
                                       "Update check failed. Check your network and try again.\nYou can also open the releases page: {0}"),
            ["L_About_OpenLogsFailed"] = ("无法打开日志目录", "Cannot open log folder"),
            ["L_About_CopiedShare"] = ("已复制到剪贴板 ✓", "Copied to clipboard ✓"),
            ["L_About_Copied"] = ("已复制 ✓", "Copied ✓"),
            ["L_About_NoChangelog"] = ("（未找到更新记录文件 resources\\changelog.txt）",
                                       "(resources\\changelog.txt not found)"),
            ["L_About_DownloadLink"] = ("下载地址：{0}", "Download: {0}"),

            // ---------------- 错误码文案（PW001~PW010）----------------
            ["L_Err_WorkspaceLocked"] = ("无法删除工作区，请先关闭目标程序", "Cannot remove the workspace; close the target program first"),
            ["L_Err_HardLinkFailed"] = ("创建硬链接失败，请检查磁盘和权限", "Failed to create a hard link; check the disk and permissions"),
            ["L_Err_ShortcutRewrite"] = ("快捷方式改写失败，原快捷方式已备份", "Failed to rewrite the shortcut; the original was backed up"),
            ["L_Err_IniWrite"] = ("规则写入失败，请检查工作区权限", "Failed to write rules; check workspace permissions"),
            ["L_Err_BackupRestore"] = ("原快捷方式路径不存在，请手动处理", "The original shortcut path no longer exists; fix it manually"),
            ["L_Err_NoPermission"] = ("无权限：目标程序位于受保护目录，当前用户无法写入 Hookshot 授权文件，请以管理员身份运行管理器后重试",
                                       "No permission: the target is in a protected folder and the Hookshot authorization file cannot be written. Run the manager as administrator and try again"),
            ["L_Err_PeDetectFailed"] = ("无法检测目标程序位数，请手动选择", "Cannot detect the target's architecture; choose it manually"),
            ["L_Err_Arm64Unsupported"] = ("Hookshot 无 ARM64 版本，不支持该程序", "Hookshot has no ARM64 build; this program is not supported"),
            ["L_Err_RuleInvalid"] = ("规则不合法", "Invalid rule"),
            ["L_Err_ConfigCorrupted"] = ("配置文件损坏，已备份为 config.json.bak", "The config file was corrupted and has been backed up as config.json.bak"),
            ["L_Err_Unknown"] = ("未知错误", "Unknown error"),
            ["L_Err_WithDetail"] = ("{0}（{1}）", "{0} ({1})"),

            // ---------------- 规则校验 ----------------
            ["L_Rule_NameRequired"] = ("规则名不能为空", "Rule name must not be empty"),
            ["L_Rule_OriginRequired"] = ("规则「{0}」的源目录不能为空", "Rule \"{0}\": origin directory must not be empty"),
            ["L_Rule_TargetRequired"] = ("规则「{0}」的目标目录不能为空", "Rule \"{0}\": target directory must not be empty"),
            ["L_Rule_OriginAbsolute"] = ("规则「{0}」的源目录必须是绝对路径", "Rule \"{0}\": origin directory must be an absolute path"),
            ["L_Rule_TargetAbsolute"] = ("规则「{0}」的目标目录必须是绝对路径", "Rule \"{0}\": target directory must be an absolute path"),
            ["L_Rule_OriginRoot"] = ("规则「{0}」的源目录不能为盘符根目录", "Rule \"{0}\": origin directory must not be a drive root"),
            ["L_Rule_TargetRoot"] = ("规则「{0}」的目标目录不能为盘符根目录", "Rule \"{0}\": target directory must not be a drive root"),
            ["L_Rule_PatternNoSlash"] = ("规则「{0}」的文件通配「{1}」不能含反斜杠", "Rule \"{0}\": file pattern \"{1}\" must not contain a backslash"),
            ["L_Rule_DuplicateName"] = ("规则名重复：{0}（规则名大小写敏感）", "Duplicate rule name: {0} (names are case-sensitive)"),
            ["L_Rule_DuplicateOrigin"] = ("规则「{0}」的源目录与其他规则重复", "Rule \"{0}\": origin directory duplicates another rule"),
            ["L_Rule_NestedTarget"] = ("规则「{0}」的目标目录与其他规则的目标目录存在祖先/后代关系，必须完全不相关",
                                       "Rule \"{0}\": target directory overlaps another rule's target directory (ancestor/descendant); they must be unrelated"),
            ["L_Rule_TargetIsOrigin"] = ("规则「{0}」的目标目录不能等于任何规则的源目录",
                                         "Rule \"{0}\": target directory must not equal any rule's origin directory"),
            ["L_Rule_NoEffectiveRules"] = ("没有任何生效规则", "No effective rules"),

            // ---------------- 应用级提示 ----------------
            ["L_App_Name"] = ("Pathwinder 管理器", "Pathwinder Manager"),
            ["L_App_StartupFailed"] = ("管理器启动失败：{0}\n\n详细信息已写入 logs\\manager.log。程序将退出。",
                                       "Manager failed to start: {0}\n\nSee logs\\manager.log for details. The program will exit."),
            ["L_App_NeedRuntimeTitle"] = ("缺少系统运行库", "Missing system runtime"),
            ["L_App_NeedRuntimeMsg"] = ("检测到系统缺少 Microsoft Visual C++ 2015-2022 运行库（{0}）。\n\n缺少运行库时，Pathwinder 无法注入目标程序，路径重定向不会生效。\n\n「是」：打开官方下载地址（安装后请重新启动本管理器）\n「否」：不再提示（可在「设置」中重新开启）\n「取消」：本次跳过，下次启动仍会提示",
                                        "The Microsoft Visual C++ 2015-2022 runtime is missing ({0}).\n\nWithout it Pathwinder cannot inject into target programs and redirection will not work.\n\nYes: open the official download page (restart the manager after installing)\nNo: don't remind me again (can be re-enabled in Settings)\nCancel: skip this time, ask again next launch"),
            ["L_App_UacOff"] = ("系统已关闭 UAC，且没有可用的提权中转进程。\n\n请关闭管理器后，从桌面快捷方式重新启动。",
                                "UAC is disabled and no elevation helper is available.\n\nClose the manager and start it again from the desktop shortcut."),
            ["L_App_ElevateFailed"] = ("无法以管理员身份重启：{0}\n\n请关闭管理器后，右键选择「以管理员身份运行」手动重试。",
                                       "Cannot restart as administrator: {0}\n\nClose the manager, right-click the shortcut and choose \"Run as administrator\"."),
            ["L_App_StandardFailed"] = ("无法以普通权限重启：{0}\n\n请关闭管理器后，从桌面快捷方式重新启动（不要从管理员终端启动）。",
                                        "Cannot restart with standard permissions: {0}\n\nClose the manager and start it from the desktop shortcut (not from an elevated terminal)."),
            ["L_App_NeedAdminTitle"] = ("需要管理员权限", "Administrator required"),
            ["L_App_NeedAdminMsg"] = ("目标程序位于受保护目录（如 Program Files），当前权限无法写入 Hookshot 授权文件。\n\n是否立即以管理员身份重启管理器？\n（重启后请重新执行该操作）",
                                      "The target program is in a protected folder (e.g. Program Files), so the Hookshot authorization file cannot be written.\n\nRestart the manager as administrator now?\n(Repeat the action after restarting)"),
            ["L_App_Unexpected"] = ("发生未预期的错误，操作已中止（程序不会退出）：\n\n{0}",
                                    "An unexpected error occurred and the action was aborted (the program stays open):\n\n{0}"),
        };
    }
}
