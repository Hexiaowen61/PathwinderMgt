using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            _cfg = cfg;
            _service = service;

            CmbArch.Items.Add(ProgramEntry.X64);
            CmbArch.Items.Add(ProgramEntry.X86);
            if (p.Architecture == PeDetector.Arm64) CmbArch.Items.Add(PeDetector.Arm64);

            LoadEntry(p);
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
            _loading = false;

            _dirty = false;
            RebuildRulesPanel();
            RefreshManagedState();
        }

        private void RebuildRulesPanel()
        {
            var panel = new RulesPanel(_p.OverrideRules);
            panel.Changed += (_, _) =>
            {
                ConfigManager.Save(_cfg);
                _dirty = true;
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
                TxtInstruction.Text = "（未添加指令）";
                return;
            }
            try
            {
                var lnk = ShortcutManager.Read(_p.OriginalShortcut);
                TxtInstruction.Text =
                    $"目标：{lnk.TargetPath}{Environment.NewLine}" +
                    $"参数：{lnk.Arguments}{Environment.NewLine}" +
                    $"工作目录：{lnk.WorkingDirectory}";
            }
            catch
            {
                TxtInstruction.Text = "（已受管，但读取快捷方式失败）";
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
                MessageBox.Show("更新 Hookshot 日志设置失败：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>标记为"不管理"：主列表仅保留详情按钮，批量操作自动跳过。已受管时提示已有指令保持不变。</summary>
        private void ChkExcluded_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool excluded = ChkExcluded.IsChecked == true;
            if (excluded && IsCurrentlyManaged() && MessageBox.Show(
                    "该程序当前已添加指令，标记为“不管理”后已有指令保持不变；\n" +
                    "如需恢复原快捷方式，可先取消标记再移除指令。\n\n确认标记？",
                    "标记为不管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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
            if (empty && MessageBox.Show(
                    "当前程序没有可用的生效规则（或规则路径为空），添加后启动时不会产生重定向。\n\n是否仍要继续？",
                    "规则未配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                _service.ApplyInstruction(_p, allowEmptyRules: true);
                ConfigManager.Save(_cfg);
                _dirty = false;
                RefreshManagedState();
                MessageBox.Show("指令已添加，快捷方式已改写并加上前缀标记。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show("指令已更新，工作区与快捷方式已按最新规则重新生成。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认移除该程序的 Pathwinder 指令？原快捷方式将从备份恢复。",
                    "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
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
                MessageBox.Show("检测到目标程序已在运行，Hookshot 无法注入已运行的进程。\n\n仍要通过快捷方式启动？",
                    "程序已在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try { _service.Launch(_p); }
            catch (PwException ex) { HandleError(ex); }
            catch (Exception ex) { HandleGeneric(ex); }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认删除该程序的全部记录？\n\n若当前仍受管，会先恢复原快捷方式并清理工作区。",
                    "删除记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
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
            MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);

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
            ShowError($"操作失败：{ex.Message}\n\n详细信息已写入 logs\\manager.log。");
        }
    }
}
