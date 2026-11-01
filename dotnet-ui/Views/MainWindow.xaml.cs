using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    public partial class MainWindow : Window
    {
        internal enum RowState { Managed, Unmanaged, Missing, PermissionDenied, Excluded }

        public class RowVM
        {
            public ProgramEntry P { get; init; }
            public string Status { get; private set; }
            public string StatusBg { get; private set; }
            public string StatusFg { get; private set; }
            public bool CanAdd { get; private set; }
            public bool CanRemove { get; private set; }
            public string DisplayName => P.DisplayName;
            public string OriginalTarget => P.OriginalTarget;
            public string Architecture =>
                P.Architecture == PeDetector.Arm64 ? "ARM64" : P.Architecture.ToUpperInvariant();
            public string ArchitectureFg =>
                P.Architecture == PeDetector.Arm64 ? "#DC2626" : "#1F2937";

            internal void SetState(RowState state)
            {
                switch (state)
                {
                    case RowState.Managed:
                        Status = "已受管"; StatusBg = "#DCFCE7"; StatusFg = "#166534";
                        CanAdd = false; CanRemove = true;
                        break;
                    case RowState.Unmanaged:
                        Status = "未受管"; StatusBg = "#FEF3C7"; StatusFg = "#92400E";
                        CanAdd = true; CanRemove = false;
                        break;
                    case RowState.PermissionDenied:
                        Status = "无权限"; StatusBg = "#FEE2E2"; StatusFg = "#991B1B";
                        CanAdd = false; CanRemove = false;
                        break;
                    case RowState.Excluded:
                        Status = "不管理"; StatusBg = "#E5E7EB"; StatusFg = "#4B5563";
                        CanAdd = false; CanRemove = false;
                        break;
                    default:
                        Status = "文件缺失"; StatusBg = "#FEE2E2"; StatusFg = "#991B1B";
                        CanAdd = false; CanRemove = false;
                        break;
                }
            }
        }

        private AppConfig _cfg;
        private ProgramService _service;
        public ObservableCollection<RowVM> Rows { get; } = new();
        private bool _warningsShown;
        private bool? _nameSortAsc; // 显示名排序方向：null=未排序，true=升序，false=降序

        public MainWindow()
        {
            InitializeComponent();
            LvPrograms.ItemsSource = Rows;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var startup = App.Bootstrap;
            _cfg = startup.Config;
            _service = startup.Programs;
            RefreshRows();

            // 管理员启动按钮：已提权则置灰，否则可点击重启为管理员
            BtnRunAsAdmin.IsEnabled = !App.IsElevated();

            var c = startup.Cleanup;
            CleanupText.Text = _cfg.Settings.LastCleanupTime is DateTime t
                ? $"上次清理：{t:yyyy-MM-dd HH:mm:ss}（重建 {c.WorkspacesRebuilt}，删除孤儿 {c.OrphanDirsRemoved}）"
                : "尚未执行清理";

            var notices = new List<string>(startup.Warnings);
            if (startup.ChildAuthAdded > 0)
                notices.Add($"检测到 {startup.ChildAuthAdded} 个子进程缺少 Hookshot 授权，已自动补建授权文件。" +
                            "请完全退出目标程序后，重新通过受管快捷方式启动，重定向才能覆盖全部子进程。");
            if (startup.ChildAuthFailed > 0)
                notices.Add($"有 {startup.ChildAuthFailed} 个子进程位于受保护目录，授权文件未能自动创建，详见 logs\\manager.log。");

            if (!_warningsShown && notices.Count > 0)
            {
                _warningsShown = true;
                MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, notices),
                    "启动提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>逐个校验快捷方式实际状态，以实际为准刷新 UI（文档 5.1.5）。</summary>
        private void RefreshRows()
        {
            Rows.Clear();
            foreach (var p in _cfg.Programs)
            {
                var row = new RowVM { P = p };
                RowState state;
                // 标记为"不管理"的程序：状态固定展示，不再校验快捷方式/权限
                if (p.Excluded)
                    state = RowState.Excluded;
                else if (!ProgramService.IsShortcutAvailable(p))
                    state = RowState.Missing;
                else if (File.Exists(p.OriginalShortcut) &&
                         ShortcutManager.IsManaged(p.OriginalShortcut, AppPaths.RuntimeRoot, out var hash) &&
                         string.Equals(hash, p.Id, StringComparison.OrdinalIgnoreCase))
                    state = RowState.Managed;
                else
                    state = RowState.Unmanaged;

                // 目标 exe 目录不可写（如 Program Files）或快捷方式所在位置不可写（如公共桌面/只读）
                // → 无权限，禁止添加/移除指令
                if (state != RowState.Missing && state != RowState.Excluded &&
                    (!_service.IsTargetWritable(p) || !_service.IsShortcutWritable(p)))
                    state = RowState.PermissionDenied;

                row.SetState(state);
                Rows.Add(row);
            }
            if (_cfg != null)
            {
                int excludedCount = Rows.Count(r => r.Status == "不管理");
                StatusText.Text = $"共 {_cfg.Programs.Count} 个程序，其中已受管 {Rows.Count(r => r.Status == "已受管")} 个" +
                                  (excludedCount > 0 ? $"，不管理 {excludedCount} 个" : "");
            }
            // 空列表时 Auto 列宽只剩表头宽，改用固定合理宽度；有数据时恢复自适应（NaN = Auto）
            bool empty = Rows.Count == 0;
            ColOp.Width = empty ? 230 : double.NaN;
            ColStatus.Width = empty ? 80 : double.NaN;
            ColDisplay.Width = empty ? 150 : double.NaN;
            ColArch.Width = empty ? 70 : double.NaN;
            ColPath.Width = empty ? 300 : double.NaN;
            // 非空时强制 GridView 按新内容重新测量列宽（Auto 列宽不会随 ObservableCollection 变化自动重算）
            if (!empty && LvPrograms.View is GridView gv)
            {
                LvPrograms.UpdateLayout();
                gv.RefreshAutoColumns();
            }
            ApplyNameSort(); // 保持显示名排序状态（RefreshRows 重建行后重新应用）
        }

        // ---------------- 显示名排序 ----------------

        /// <summary>点击"显示名"表头：切换升序/降序（其余表头不响应）。</summary>
        private void ColHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader hdr || !ReferenceEquals(hdr.Column, ColDisplay)) return;
            _nameSortAsc = _nameSortAsc != true; // 未排序/降序 → 升序；升序 → 降序
            ApplyNameSort();
        }

        /// <summary>按显示名重排当前行集合并更新表头箭头。</summary>
        private void ApplyNameSort()
        {
            ColDisplay.Header = _nameSortAsc == null ? "显示名"
                : _nameSortAsc == true ? "显示名 ▲" : "显示名 ▼";
            if (_nameSortAsc == null || Rows.Count < 2) return;
            var sorted = _nameSortAsc == true
                ? Rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList()
                : Rows.OrderByDescending(r => r.DisplayName, StringComparer.CurrentCulture).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int idx = Rows.IndexOf(sorted[i]);
                if (idx != i) Rows.Move(idx, i);
            }
        }

        // ---------------- 拖入（文档 5.1.1）----------------

        private void Window_DragOver(object sender, DragEventArgs e)
            => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            RegisterFiles(files);
        }

        /// <summary>"添加程序"按钮：打开文件对话框批量选择 .lnk / .exe。</summary>
        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择一个或多个快捷方式（.lnk）或可执行文件（.exe）",
                Filter = "快捷方式和可执行文件 (*.lnk;*.exe)|*.lnk;*.exe|快捷方式 (*.lnk)|*.lnk|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
                RegisterFiles(dlg.FileNames);
        }

        /// <summary>登记一批 .lnk/.exe（拖入与"添加程序"按钮共用）。</summary>
        private void RegisterFiles(IEnumerable<string> files)
        {
            int added = 0;
            var errors = new List<string>();

            foreach (var f in files)
            {
                bool isLnk = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                bool isExe = f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (!isLnk && !isExe) continue;
                try
                {
                    var entry = isLnk ? _service.RegisterShortcut(f) : _service.RegisterExe(f);
                    added++;
                    if (entry.Architecture == PeDetector.Arm64)
                        errors.Add($"「{entry.DisplayName}」检测为 ARM64，Hookshot 无 ARM64 版本，请在详情中手动选择 x86/x64");
                }
                catch (PwException ex) { errors.Add($"{Path.GetFileName(f)}：{ex.UserMessage}"); }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}：{ex.Message}"); }
            }

            if (added > 0)
            {
                ConfigManager.Save(_cfg);
                RefreshRows();
            }
            if (errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, errors), "添加结果",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------------- 单个添加 / 移除 ----------------

        private void BtnAddOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not RowVM row) return;
            if (ConfirmEmptyRules(new[] { row }) != true) return;
            if (RunOne(row.P, p => _service.ApplyInstruction(p)))
            {
                ConfigManager.Save(_cfg);
                RefreshRows();
            }
        }

        private void BtnRemoveOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not RowVM row) return;
            if (RunOne(row.P, p => _service.RemoveInstruction(p)))
            {
                ConfigManager.Save(_cfg);
                RefreshRows();
            }
        }

        private bool RunOne(ProgramEntry p, Action<ProgramEntry> action)
        {
            try
            {
                action(p);
                return true;
            }
            catch (PwException ex)
            {
                if (ex.Code == PwCode.NoPermission && App.AskRestartElevated()) return false;
                MessageBox.Show(ex.UserMessage, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                // 非预期异常绝不允许闪退：记录后友好提示
                _service?.Log.Error("单个操作未处理异常：" + ex);
                MessageBox.Show($"操作失败：{ex.Message}\n\n详细信息已写入 logs\\manager.log。",
                    "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ---------------- 批量 ----------------

        /// <summary>生效规则为空时统一确认一次。返回 null 表示无需确认（继续），true/false 为用户选择。</summary>
        private bool? ConfirmEmptyRules(IEnumerable<RowVM> targets)
        {
            bool anyEmpty = targets.Select(r => r.P).Any(p =>
            {
                var rules = p.OverrideRules;
                return rules.Count == 0 ||
                       rules.Any(r => string.IsNullOrWhiteSpace(r.OriginDirectory) ||
                                      string.IsNullOrWhiteSpace(r.TargetDirectory));
            });
            if (!anyEmpty) return true;
            return MessageBox.Show(
                "部分程序没有可用的生效规则（或规则路径为空），添加后启动时不会产生重定向。\n\n是否仍要继续？",
                "规则未配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void BtnAddAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = Rows.Where(r => r.CanAdd).ToList();
            if (targets.Count == 0) { MessageBox.Show("没有需要添加指令的程序。"); return; }
            if (ConfirmEmptyRules(targets) != true) return;

            var errors = RunBatch(targets, p => _service.ApplyInstruction(p, allowEmptyRules: true), out bool denied);
            ConfigManager.Save(_cfg);
            RefreshRows();
            ReportBatch("全部添加", targets.Count, errors);
            if (denied) App.AskRestartElevated();
        }

        private void BtnRemoveAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = Rows.Where(r => r.CanRemove).ToList();
            if (targets.Count == 0) { MessageBox.Show("没有需要移除指令的程序。"); return; }
            if (MessageBox.Show($"确认移除 {targets.Count} 个程序的指令？原快捷方式将从备份恢复。",
                    "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            var errors = RunBatch(targets, p => _service.RemoveInstruction(p), out _);
            ConfigManager.Save(_cfg);
            RefreshRows();
            ReportBatch("全部移除", targets.Count, errors);
        }

        private List<string> RunBatch(List<RowVM> targets, Action<ProgramEntry> action,
            out bool permissionDenied)
        {
            var errors = new List<string>();
            permissionDenied = false;
            foreach (var row in targets)
            {
                try { action(row.P); }
                catch (PwException ex)
                {
                    errors.Add($"「{row.P.DisplayName}」：{ex.UserMessage}");
                    if (ex.Code == PwCode.NoPermission) permissionDenied = true;
                }
                catch (Exception ex)
                {
                    _service?.Log.Error("批量操作未处理异常：" + ex);
                    errors.Add($"「{row.P.DisplayName}」：{ex.Message}");
                }
            }
            return errors;
        }

        private void ReportBatch(string op, int total, List<string> errors)
        {
            StatusText.Text = $"{op}完成：成功 {total - errors.Count}/{total}";
            if (errors.Count > 0)
                MessageBox.Show($"成功 {total - errors.Count} 个，失败 {errors.Count} 个：\n\n" +
                                string.Join(Environment.NewLine, errors),
                    $"{op}结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------------- 全部删除记录 ----------------

        /// <summary>删除全部程序记录：二次确认 → 加载状态 → 批量 DeleteEntry → 成功提示。标记为"不管理"的程序自动跳过。</summary>
        private async void BtnDeleteAll_Click(object sender, RoutedEventArgs e)
        {
            // 标记为"不管理"的程序不参与批量操作
            int count = _cfg.Programs.Count(p => !p.Excluded);
            if (count == 0)
            {
                MessageBox.Show(
                    _cfg.Programs.Count > 0
                        ? "所有程序均已标记为“不管理”，没有可删除的记录。"
                        : "当前没有任何记录可删除。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int skipped = _cfg.Programs.Count - count;

            var result = MessageBox.Show(
                $"确定要删除所有 {count} 条记录吗？此操作不可撤销。\n\n" +
                "受管程序的快捷方式将从备份恢复，工作区与备份文件将被清理。" +
                (skipped > 0 ? $"\n\n已标记为“不管理”的 {skipped} 个程序将被跳过。" : ""),
                "确认删除全部记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            var btn = sender as Button;
            object originalContent = btn?.Content;
            try
            {
                // 加载状态：禁用按钮、显示"删除中..."、等待光标、状态栏提示
                if (btn != null)
                {
                    btn.IsEnabled = false;
                    btn.Content = "删除中...";
                }
                Cursor = Cursors.Wait;
                StatusText.Text = "正在删除全部记录...";
                await Task.Delay(50); // 让 UI 渲染加载状态

                var errors = new List<string>();
                // 复制一份再遍历，DeleteEntry 会从 _cfg.Programs 移除项
                var programs = _cfg.Programs.Where(p => !p.Excluded).ToList();
                foreach (var p in programs)
                {
                    try
                    {
                        _service.DeleteEntry(p);
                    }
                    catch (Exception ex)
                    {
                        _service?.Log.Error($"删除记录失败 {p.DisplayName}：{ex}");
                        errors.Add($"「{p.DisplayName}」：{ex.Message}");
                    }
                }

                ConfigManager.Save(_cfg);
                RefreshRows();

                if (errors.Count == 0)
                {
                    StatusText.Text = $"已删除全部 {count} 条记录";
                    MessageBox.Show($"已成功删除全部 {count} 条记录。", "删除完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    int ok = count - errors.Count;
                    StatusText.Text = $"删除完成：成功 {ok}/{count}";
                    MessageBox.Show($"成功删除 {ok} 条，失败 {errors.Count} 条：\n\n" +
                                    string.Join(Environment.NewLine, errors),
                        "删除结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                // 恢复按钮状态
                if (btn != null)
                {
                    btn.IsEnabled = true;
                    btn.Content = originalContent;
                }
                Cursor = Cursors.Arrow;
            }
        }

        // ---------------- 详情 / 全局规则 ----------------

        private void BtnDetail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is RowVM row) OpenDetail(row.P);
        }

        private void LvPrograms_MouseDoubleClick(object sender, RoutedEventArgs e)
        {
            if (LvPrograms.SelectedItem is RowVM row) OpenDetail(row.P);
        }

        private void OpenDetail(ProgramEntry p)
        {
            var dlg = new ProgramWindow(_cfg, p, _service) { Owner = this };
            dlg.ShowDialog();
            ConfigManager.Save(_cfg);
            RefreshRows();
        }

        // ---------------- 其他工具栏 ----------------

        private void BtnRunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            App.RestartElevated();
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开日志目录"); }
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AboutWindow(_cfg.Settings.ShortcutPrefix, _cfg) { Owner = this };
            dlg.ShowDialog();
            if (dlg.PrefixResult != null && dlg.PrefixResult != _cfg.Settings.ShortcutPrefix)
            {
                _cfg.Settings.ShortcutPrefix = dlg.PrefixResult;
                ConfigManager.Save(_cfg);
                MessageBox.Show("快捷方式前缀已保存，已有快捷方式将在下次启动时迁移为新前缀。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = CleanupManager.Run(_cfg);
                ConfigManager.Save(_cfg);
                RefreshRows();
                CleanupText.Text = _cfg.Settings.LastCleanupTime is DateTime t
                    ? $"上次清理：{t:yyyy-MM-dd HH:mm:ss}（重建 {result.WorkspacesRebuilt}，删除孤儿 {result.OrphanDirsRemoved}）"
                    : CleanupText.Text;
                StatusText.Text = "已刷新";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "刷新失败"); }
        }
    }

    /// <summary>
    /// GridView 列宽工具：WPF 的 Auto 列宽在 ObservableCollection 增删改后不会自动重算，
    /// 需要先 UpdateLayout 让 ActualWidth 反映新内容，再锁 ActualWidth 后设回 NaN 触发重新测量。
    /// </summary>
    internal static class GridViewExtensions
    {
        public static void RefreshAutoColumns(this System.Windows.Controls.GridView gridView)
        {
            if (gridView == null) return;
            foreach (var col in gridView.Columns)
            {
                if (col.ActualWidth <= 0) continue;
                col.Width = col.ActualWidth;
                col.Width = double.NaN;
            }
        }
    }
}
