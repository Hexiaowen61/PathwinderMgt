using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>程序详情页（文档 8.2 / 5.2.2 / 4.3）。</summary>
    public partial class ProgramWindow : Window
    {
        private readonly AppConfig _cfg;
        private ProgramEntry _p; // 拷贝后切换为新记录，去掉 readonly（拷贝功能文档第 6 节）
        private readonly ProgramService _service;
        private bool _loading;
        private bool _dirty; // 规则等数据已变更，已受管时需显示“更新指令”

        public ProgramWindow(AppConfig cfg, ProgramEntry p, ProgramService service)
        {
            InitializeComponent();
            // Esc 等同右上角叉叉：用冒泡阶段的 KeyDown，控件已处理的 Esc（如收起 ComboBox 下拉）不关窗口
            KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
            _cfg = cfg;
            _service = service;

            CmbArch.Items.Add(ProgramEntry.X64);
            CmbArch.Items.Add(ProgramEntry.X86);
            if (p.Architecture == PeDetector.Arm64) CmbArch.Items.Add(PeDetector.Arm64);

            // 语言切换后指令内容等代码赋值的文本需重设
            Loc.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;

            LoadEntry(p);
        }

        private void OnLanguageChanged()
        {
            if (_p != null) RefreshManagedState();
        }

        /// <summary>用指定程序记录填充全部界面字段（拷贝功能文档第 6 节；拷贝成功后切换到新记录）。</summary>
        private void LoadEntry(ProgramEntry p)
        {
            _p = p;

            _loading = true;
            TxtName.Text = p.DisplayName;
            TxtId.Text = p.Id;
            TxtTarget.Text = p.OriginalTarget;
            TxtShortcut.Text = p.OriginalShortcut;

            CmbArch.SelectedItem = p.Architecture;
            TxtArchWarn.Visibility = p.Architecture == PeDetector.Arm64 ? Visibility.Visible : Visibility.Collapsed;

            ChkHookshotLog.IsChecked = p.HookshotLogEnabled;
            ChkExcluded.IsChecked = p.Excluded;

            // 自定义状态：任一规则带独立模式。自定义时跳过 ini 程序级回填
            //（各节模式可能不同，回填会把程序级错改成首节模式，且不应触发补写）
            bool custom = p.OverrideRules.Any(r => r.RedirectMode != null);
            if (!custom)
            {
                // 模式切换：以工作区 ini 为准；ini 已存在但未配置 RedirectMode 时按严格 Simple 显示
                //（内核缺省即 Simple）。这里不再补写 Simple Pro，避免已有指令被悄悄切成读回退、
                // 失去数据隔离；未添加指令时沿用记录值。
                string iniMode = WorkspaceManager.ReadPathwinderRedirectMode(p);
                if (iniMode != null)
                    p.RedirectMode = iniMode;
                else if (File.Exists(Path.Combine(AppPaths.WorkDir(p.Id), "Pathwinder.ini")))
                    p.RedirectMode = ProgramEntry.RedirectModeSimple;
            }
            CmbMode.SelectedIndex = custom ? 3 : p.RedirectMode switch
            {
                ProgramEntry.RedirectModeSimple => 0,
                ProgramEntry.RedirectModeOverlay => 2,
                _ => 1
            };
            _loading = false;

            _dirty = false;
            RebuildRulesPanel();
            RefreshManagedState();
        }

        private RulesPanel _rulesPanel;

        private void RebuildRulesPanel()
        {
            bool custom = _p.OverrideRules.Any(r => r.RedirectMode != null);
            var panel = new RulesPanel(_p.OverrideRules, custom, () => _p.RedirectMode,
                ruleName => ConfigManager.ComputeDefaultTargetDir(_p.DisplayName, _p.Id, ruleName));
            _rulesPanel = panel;
            panel.Changed += (_, _) =>
            {
                ConfigManager.Save(_cfg);
                // 已受管：规则立即写入工作区 Pathwinder.ini（与模式/日志开关一致，改完即时生效，
                // 不再依赖“更新指令”）。Prepare 幂等：硬链接已指向 payload 时跳过，仅重写 ini。
                if (IsCurrentlyManaged())
                {
                    try
                    {
                        var pending = WorkspaceManager.Prepare(_p, _p.OverrideRules);
                        if (pending.Count > 0)
                            _service.Log.Warn($"规则已写入 ini，但工作区有文件被运行中的程序占用：" +
                                              $"{string.Join("、", pending)}");
                        _dirty = false;
                    }
                    catch (Exception ex)
                    {
                        // ini 写入失败：回退为显示“更新指令”，用户可手动重试
                        _service.Log.Error($"规则变更同步 ini 失败：{ex.Message}");
                        MessageBox.Show(Loc.T("L_Prog_SyncFailedMsg", ex.Message), Loc.T("L_Prog_SyncFailedTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        _dirty = true;
                    }
                }
                RefreshManagedState();
            };
            PanelHost.Content = panel;
        }

        /// <summary>按快捷方式实际状态与数据变更状态，互斥显示"添加/更新/移除指令"，并刷新指令内容。</summary>
        private void RefreshManagedState()
        {
            TxtShortcut.Text = _p.OriginalShortcut;
            bool available = ProgramService.IsShortcutAvailable(_p);
            bool managed = IsCurrentlyManaged();
            // 标记为"不管理"：隐藏添加/更新/移除指令，主列表也只保留详情按钮
            bool excluded = _p.Excluded;

            // 三按钮互斥：未受管→"添加指令"；已受管且数据已变更→"更新指令"；已受管且未变更→"移除指令"
            // 目标目录或快捷方式所在位置不可写（受保护目录/公共桌面/只读）时，操作均需管理员权限，统一禁用
            bool writable = _service.IsTargetWritable(_p) && _service.IsShortcutWritable(_p);

            BtnApply.Visibility = managed || excluded ? Visibility.Collapsed : Visibility.Visible;
            BtnApply.IsEnabled = available && !managed && !excluded && _p.Architecture != PeDetector.Arm64 && writable;

            BtnUpdate.Visibility = !excluded && managed && _dirty ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdate.IsEnabled = writable && !excluded;

            BtnRemove.Visibility = !excluded && managed && !_dirty ? Visibility.Visible : Visibility.Collapsed;
            BtnRemove.IsEnabled = writable && !excluded;

            RefreshInstructionText(managed);
        }

        /// <summary>快捷方式当前是否已改写为本程序的受管快捷方式。</summary>
        private bool IsCurrentlyManaged()
            => File.Exists(_p.OriginalShortcut) &&
               ShortcutManager.IsManaged(_p.OriginalShortcut, AppPaths.RuntimeRoot, out var hash) &&
               string.Equals(hash, _p.Id, StringComparison.OrdinalIgnoreCase);

        /// <summary>已添加指令时显示快捷方式的完整参数（目标/参数/工作目录），否则显示占位。</summary>
        private void RefreshInstructionText(bool managed)
        {
            if (!managed)
            {
                TxtInstruction.Text = Loc.T("L_Prog_NoCommand");
                return;
            }
            try
            {
                var lnk = ShortcutManager.Read(_p.OriginalShortcut);
                TxtInstruction.Text =
                    Loc.T("L_Prog_InstrTarget", lnk.TargetPath) + Environment.NewLine +
                    Loc.T("L_Prog_InstrArgs", lnk.Arguments) + Environment.NewLine +
                    Loc.T("L_Prog_InstrWorkDir", lnk.WorkingDirectory);
            }
            catch
            {
                TxtInstruction.Text = Loc.T("L_Prog_InstrReadFail");
            }
        }

        // ---------------- 位数手动兜底（文档 4.3）----------------

        private void CmbArch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbArch.SelectedItem is not string arch) return;
            if (arch == PeDetector.Arm64) return; // 只是展示项，不允许主动选成 arm64
            try
            {
                _service.ChangeArchitecture(_p, arch);
                ConfigManager.Save(_cfg);
                _dirty = false;
                TxtArchWarn.Visibility = Visibility.Collapsed;
                if (CmbArch.Items.Contains(PeDetector.Arm64)) CmbArch.Items.Remove(PeDetector.Arm64);
                RefreshManagedState();
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        // ---------------- 模式切换（立即重写 ini；自定义→统一模式需确认）----------------

        /// <summary>自定义状态：任一规则带独立模式（详情页“自定义”选项为非持久化的 UI 推导）。</summary>
        private bool HasCustomRuleModes => _p.OverrideRules.Any(r => r.RedirectMode != null);

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            // 切到「自定义」：把程序级当前值物化到每条规则，规则级下拉变为可编辑；ini 内容等价不变
            if (CmbMode.SelectedIndex == 3)
            {
                if (!HasCustomRuleModes)
                {
                    foreach (var r in _p.OverrideRules)
                        r.RedirectMode = _p.RedirectMode;
                    ConfigManager.Save(_cfg);
                }
                _rulesPanel?.SetCustomMode(true);
                return;
            }

            string mode = CmbMode.SelectedIndex switch
            {
                0 => ProgramEntry.RedirectModeSimple,
                2 => ProgramEntry.RedirectModeOverlay,
                _ => ProgramEntry.RedirectModeSimplePro
            };

            // 自定义 → 非自定义：覆盖前确认（清除各规则独立模式不可逆）
            if (HasCustomRuleModes)
            {
                string display = CmbMode.SelectedIndex switch { 0 => "Simple", 2 => "Overlay", _ => "SimplePro" };
                if (MessageBox.Show(Loc.T("L_Prog_ModeCustomConfirm", display),
                        Loc.T("L_Prog_Mode"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    _loading = true;
                    CmbMode.SelectedIndex = 3; // 回退到自定义
                    _loading = false;
                    return;
                }
                foreach (var r in _p.OverrideRules)
                    r.RedirectMode = null;
            }

            _p.RedirectMode = mode;
            try
            {
                // 统一模式时也必须重写 ini：自定义期间各规则可能写入过独立的 RedirectMode 行
                WorkspaceManager.SetPathwinderRedirectMode(_p); // 未添加指令时为空操作，Prepare 时按记录写入
                ConfigManager.Save(_cfg);
                _rulesPanel?.SetCustomMode(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("L_Prog_ModeUpdateFailed", ex.Message), Loc.T("L_Common_Warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ChkHookshotLog_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool enabled = ChkHookshotLog.IsChecked == true;
            _p.HookshotLogEnabled = enabled;
            try
            {
                WorkspaceManager.SetHookshotLogLevel(_p);
                ConfigManager.Save(_cfg);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("L_Prog_LogUpdateFailed", ex.Message), Loc.T("L_Common_Warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>标记为"不管理"：主列表仅保留详情按钮，批量操作自动跳过。已受管时提示已有指令保持不变。</summary>
        private void ChkExcluded_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool excluded = ChkExcluded.IsChecked == true;
            if (excluded && IsCurrentlyManaged() && MessageBox.Show(
                    Loc.T("L_Prog_ExcludedMsg"),
                    Loc.T("L_Prog_ExcludedTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                _loading = true;
                ChkExcluded.IsChecked = false;
                _loading = false;
                return;
            }
            _p.Excluded = excluded;
            ConfigManager.Save(_cfg);
            RefreshManagedState();
        }

        // ---------------- 添加 / 移除 / 启动 / 删除 ----------------

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            var rules = _p.OverrideRules;
            bool empty = rules.Count == 0 ||
                         rules.Any(r => string.IsNullOrWhiteSpace(r.OriginDirectory) ||
                                        string.IsNullOrWhiteSpace(r.TargetDirectory));
            if (empty && QuietMessageBox.Show(
                    Loc.T("L_Main_RulesEmptyMsg"),
                    Loc.T("L_Main_RulesEmptyTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            try
            {
                _service.ApplyInstruction(_p, allowEmptyRules: true);
                ConfigManager.Save(_cfg);
                _dirty = false;
                RefreshManagedState();
                QuietMessageBox.Show(Loc.T("L_Prog_AddedMsg"), Loc.T("L_Prog_Done"));
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 使用最新数据（规则等）重新生成工作区并重写快捷方式
                _service.ApplyInstruction(_p, allowEmptyRules: true);
                ConfigManager.Save(_cfg);
                _dirty = false;
                RefreshManagedState();
                QuietMessageBox.Show(Loc.T("L_Prog_UpdatedMsg"), Loc.T("L_Prog_Done"));
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (QuietMessageBox.Show(Loc.T("L_Prog_RemoveConfirm"),
                    Loc.T("L_Prog_Confirm"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                _service.RemoveInstruction(_p);
                ConfigManager.Save(_cfg);
                _dirty = false;
                RefreshManagedState();
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (ProgramService.IsTargetRunning(_p) &&
                QuietMessageBox.Show(Loc.T("L_Prog_RunningMsg"),
                    Loc.T("L_Prog_RunningTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            try { _service.Launch(_p); }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("L_Prog_DeleteConfirm"),
                    Loc.T("L_Prog_DeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                _service.DeleteEntry(_p);
                ConfigManager.Save(_cfg);
                Close();
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>拷贝当前记录为新记录（显示名/目标路径自动编号），详情页数据切换到新记录（拷贝功能文档第 5/6 节）。</summary>
        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var copy = _service.CopyProgram(_p);
                ConfigManager.Save(_cfg);
                LoadEntry(copy); // 当前详情页立即切换为拷贝出的新记录；主列表在窗口关闭后由既有 RefreshRows 刷新
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void ShowError(string message) =>
            MessageBox.Show(message, Loc.T("L_Main_OpFailed"), MessageBoxButton.OK, MessageBoxImage.Error);

        /// <summary>业务异常：无权限时引导管理员重启，其余正常报错。</summary>
        private void HandleError(PwException ex)
        {
            if (ex.Code == PwCode.NoPermission && App.AskRestartElevated()) return;
            ShowError(ex.UserMessage);
        }

        /// <summary>非预期异常：记录日志并友好提示，杜绝闪退。</summary>
        private void HandleGeneric(Exception ex)
        {
            _service.Log.Error("详情页操作未处理异常：" + ex);
            ShowError(Loc.T("L_Main_OpFailedDetail", ex.Message));
        }
    }
}
