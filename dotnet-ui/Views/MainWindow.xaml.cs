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
using System.Windows.Media.Animation;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    public partial class MainWindow : Window
    {
        internal enum RowState { Managed, Unmanaged, Missing, PermissionDenied, Excluded }

        public class RowVM : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            public ProgramEntry P { get; internal set; }
            internal RowState State { get; private set; }
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
                ThemeManager.Color(P.Architecture == PeDetector.Arm64 ? "Danger" : "TextBody");

            /// <summary>详情窗口保存后会替换成新的 ProgramEntry 实例，就地换绑定，避免重建整表。</summary>
            internal void SetProgram(ProgramEntry p)
            {
                P = p;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(OriginalTarget)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Architecture)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ArchitectureFg)));
            }

            internal void SetState(RowState state)
            {
                State = state;
                switch (state)
                {
                    case RowState.Managed:
                        Status = Loc.T("L_Status_Managed"); StatusBg = ThemeManager.Color("StatusOkBg"); StatusFg = ThemeManager.Color("StatusOkFg");
                        CanAdd = false; CanRemove = true;
                        break;
                    case RowState.Unmanaged:
                        Status = Loc.T("L_Status_NotManaged"); StatusBg = ThemeManager.Color("StatusWarnBg"); StatusFg = ThemeManager.Color("StatusWarnFg");
                        CanAdd = true; CanRemove = false;
                        break;
                    case RowState.PermissionDenied:
                        Status = Loc.T("L_Status_NoPermission"); StatusBg = ThemeManager.Color("StatusErrorBg"); StatusFg = ThemeManager.Color("StatusErrorFg");
                        CanAdd = false; CanRemove = false;
                        break;
                    case RowState.Excluded:
                        Status = Loc.T("L_Status_Excluded"); StatusBg = ThemeManager.Color("StatusIdleBg"); StatusFg = ThemeManager.Color("StatusIdleFg");
                        CanAdd = false; CanRemove = false;
                        break;
                    default:
                        Status = Loc.T("L_Status_Missing"); StatusBg = ThemeManager.Color("StatusErrorBg"); StatusFg = ThemeManager.Color("StatusErrorFg");
                        CanAdd = false; CanRemove = false;
                        break;
                }
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusBg)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusFg)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CanAdd)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CanRemove)));
            }
        }

        private AppConfig _cfg;
        private ProgramService _service;
        public ObservableCollection<RowVM> Rows { get; } = new();
        private bool _warningsShown;
        private DateTime? _cleanupTime;   // 上次清理时间与统计，供语言切换时重绘状态栏文案
        private int _cleanupRebuilt;
        private int _cleanupOrphans;
        private bool? _nameSortAsc; // 显示名排序方向：null=未排序，true=升序，false=降序
        private bool? _statusSortAsc; // 状态排序方向：null=未排序，true=升序，false=降序
        private readonly TextBlock _statusHeaderBlock = new();
        private readonly TextBlock _displayHeaderBlock = new();

        public MainWindow()
        {
            InitializeComponent();
            ColStatus.Header = _statusHeaderBlock;
            ColDisplay.Header = _displayHeaderBlock;
            UseWidestHeaderPlaceholder(); // 首次测宽按最宽符号，箭头才不会被裁
            UpdateTitle();
            LvPrograms.ItemsSource = Rows;
            Loaded += MainWindow_Loaded;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) =>
            {
                ThemeManager.ThemeChanged -= OnThemeChanged;
                Loc.LanguageChanged -= OnLanguageChanged;
            };
        }

        /// <summary>主题切换后重建行：状态标签与架构列的颜色是构造时取的快照，需重新取色。</summary>
        private void OnThemeChanged()
        {
            if (_cfg != null) RefreshRows();
        }

        /// <summary>语言切换后：重设代码里直接赋值的文案，并按新语言表头重测列宽。</summary>
        private void OnLanguageChanged()
        {
            UpdateTitle();
            ApplyPermissionButton();
            UpdateCleanupText();
            if (_cfg == null) return;
            RefreshRows();                // 状态标签等绑定文案随语言重新生成
            UseWidestHeaderPlaceholder(); // 再按最宽符号占位后测宽，箭头不会被裁
            RecalculateAutoColumns();
            UpdateHeaderArrows();
        }

        /// <summary>窗口标题：应用名 + 版本号（版本来自 csproj 的 &lt;Version&gt;，与「说明」窗口一致）。</summary>
        private void UpdateTitle()
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            Title = version == null
                ? Loc.T("L_Main_Title")
                : $"{Loc.T("L_Main_Title")} v{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>权限按钮文案：显示当前实际权限。</summary>
        private void ApplyPermissionButton()
        {
            bool elevated = App.IsElevated();
            BtnRunAsAdmin.Content = Loc.T(elevated ? "L_Main_AdminElevated" : "L_Main_AdminNormal");
            BtnRunAsAdmin.ToolTip = Loc.T(elevated ? "L_Main_AdminTipToNormal" : "L_Main_AdminTipToAdmin");
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 运行库缺失会导致注入失败：优先提示，避免用户误以为重定向本身有问题
            App.CheckVcRuntime();

            var startup = App.Bootstrap;
            _cfg = startup.Config;
            _service = startup.Programs;
            RefreshRows();

            // 权限按钮：显示当前实际权限，点击切换到另一种权限
            ApplyPermissionButton();
            BtnRunAsAdmin.IsEnabled = true;

            var c = startup.Cleanup;
            _cleanupTime = _cfg.Settings.LastCleanupTime;
            _cleanupRebuilt = c.WorkspacesRebuilt;
            _cleanupOrphans = c.OrphanDirsRemoved;
            UpdateCleanupText();

            var notices = new List<string>(startup.Warnings);
            if (startup.ChildAuthAdded > 0)
                notices.Add(Loc.T("L_Main_ChildAuthAdded", startup.ChildAuthAdded));
            if (startup.ChildAuthFailed > 0)
                notices.Add(Loc.T("L_Main_ChildAuthFailed", startup.ChildAuthFailed));

            if (!_warningsShown && notices.Count > 0)
            {
                _warningsShown = true;
                MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, notices),
                    Loc.T("L_Main_StartupTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>状态栏右侧清理统计文案（语言切换时也走这里重绘）。</summary>
        private void UpdateCleanupText()
        {
            CleanupText.Text = _cleanupTime is DateTime t
                ? Loc.T("L_Main_LastCleanupFull", t.ToString("yyyy-MM-dd HH:mm:ss"), _cleanupRebuilt, _cleanupOrphans)
                : Loc.T("L_Main_NeverCleanup");
        }

        /// <summary>逐个校验快捷方式实际状态，以实际为准刷新 UI（文档 5.1.5）。</summary>
        private void RefreshRows()
        {
            // 程序集合没变化时就地更新每行，不重建集合——
            // 重建会让虚拟化列表跳回顶部，滚动位置丢失。
            // 注意：详情窗口保存时会替换成新的 ProgramEntry 实例，所以这里用 Id 比对而不是引用比对。
            bool sameShape = Rows.Count == _cfg.Programs.Count &&
                             Rows.Select(r => r.P.Id).OrderBy(x => x, StringComparer.Ordinal)
                                 .SequenceEqual(_cfg.Programs.Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal));
            if (sameShape)
            {
                foreach (var row in Rows)
                {
                    var p = _cfg.Programs.FirstOrDefault(x => x.Id == row.P.Id);
                    if (p == null) continue;
                    row.SetProgram(p); // 实例可能已被详情窗口替换，同时刷新主题相关颜色
                    row.SetState(ComputeState(p));
                }
                UpdateStatusText();
                UseWidestHeaderPlaceholder();
                LvPrograms.UpdateLayout(); // 空表/行数不变时也按最宽符号测一次列宽
                UpdateHeaderArrows();      // 再显示实际的 ●/▲/▼
                return;
            }

            Rows.Clear();
            foreach (var p in _cfg.Programs)
            {
                var row = new RowVM { P = p };
                row.SetState(ComputeState(p));
                Rows.Add(row);
            }
            UpdateStatusText();
            // 测宽前先把表头换成最宽的符号 ▲，否则按较窄的 ● 测出来的列宽会裁掉箭头。
            UseWidestHeaderPlaceholder();
            RecalculateAutoColumns();
            ApplyActiveSort(); // 列宽测完后再恢复 ●/▲/▼ 与行顺序
        }

        /// <summary>
        /// 强制 GridView 丢掉 Auto 列宽缓存并按当前内容重新测量。
        /// 不能只把 Width 赋成 NaN——列宽本来就是 Auto(NaN) 时这次赋值不产生变化，
        /// WPF 认为属性没变，不会重算，于是新增/删除行后列宽仍是上一次的旧值。
        /// 先撑到极大宽度再回 Auto 可以可靠触发重测；
        /// 也不能反过来用 0 撑——那会让内容在 0 宽下被测量出裁剪后的错误宽度。
        /// </summary>
        private void RecalculateAutoColumns()
        {
            var cols = new[] { ColOp, ColStatus, ColDisplay, ColArch, ColPath };
            foreach (var col in cols) col.Width = 9999;
            LvPrograms.UpdateLayout();
            foreach (var col in cols) col.Width = double.NaN;
            LvPrograms.UpdateLayout();
        }

        /// <summary>计算单行状态：不管理 → 文件缺失 → 已受管/未受管 → 无权限。</summary>
        private RowState ComputeState(ProgramEntry p)
        {
            // 标记为"不管理"的程序：状态固定展示，不再校验快捷方式/权限
            if (p.Excluded) return RowState.Excluded;
            if (!ProgramService.IsShortcutAvailable(p)) return RowState.Missing;

            bool managed = File.Exists(p.OriginalShortcut) &&
                           ShortcutManager.IsManaged(p.OriginalShortcut, AppPaths.RuntimeRoot, out var hash) &&
                           string.Equals(hash, p.Id, StringComparison.OrdinalIgnoreCase);
            var state = managed ? RowState.Managed : RowState.Unmanaged;

            // 目标 exe 目录不可写（如 Program Files）或快捷方式所在位置不可写（如公共桌面/只读）
            // → 无权限，禁止添加/移除指令
            if (!_service.IsTargetWritable(p) || !_service.IsShortcutWritable(p))
                state = RowState.PermissionDenied;
            return state;
        }

        /// <summary>底部统计文案。</summary>
        private void UpdateStatusText()
        {
            int excludedCount = Rows.Count(r => r.State == RowState.Excluded);
            int managedCount = Rows.Count(r => r.State == RowState.Managed);
            StatusText.Text = Loc.T("L_Main_StatusSummary", _cfg.Programs.Count, managedCount) +
                              (excludedCount > 0 ? Loc.T("L_Main_StatusExcluded", excludedCount) : "");
        }

        // ---------------- 排序 ----------------

        /// <summary>点击表头切换排序：显示名、状态各支持升序/降序，二者互斥。</summary>
        private void ColHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader hdr) return;

            if (ReferenceEquals(hdr.Column, ColDisplay))
            {
                _statusSortAsc = null;
                _nameSortAsc = _nameSortAsc != true;
            }
            else if (ReferenceEquals(hdr.Column, ColStatus))
            {
                _nameSortAsc = null;
                _statusSortAsc = _statusSortAsc != true;
            }
            else return;

            ApplySortOrder();   // 只重排行顺序
            UpdateHeaderArrows(); // 只换符号，字符宽度不变 → 列宽无需重测
        }

        /// <summary>按当前生效的排序方式重排（默认不排序）。</summary>
        private void ApplyActiveSort()
        {
            ApplySortOrder();
            UpdateHeaderArrows();
        }

        /// <summary>只重排行顺序，不动表头文字。</summary>
        private void ApplySortOrder()
        {
            if (_statusSortAsc != null) ApplyStatusSort();
            else ApplyNameSort();
        }

        /// <summary>
        /// 刷新两列表头文字。始终保留一个符号位（● / ▲ / ▼）——
        /// 这样切换排序时表头宽度一致，Auto 列宽不会因文字变宽而重新分配。
        /// 注意 ● 比 ▲/▼ 窄 5px，所以测量列宽时必须用最宽的符号（见 UseWidestHeaderPlaceholder）。
        /// </summary>
        private void UpdateHeaderArrows()
        {
            _displayHeaderBlock.Text = Loc.T("L_Main_ColDisplay") + " " + (_nameSortAsc == null ? "●" : _nameSortAsc == true ? "▲" : "▼");
            _statusHeaderBlock.Text = Loc.T("L_Main_ColStatus") + " " + (_statusSortAsc == null ? "●" : _statusSortAsc == true ? "▲" : "▼");
        }

        /// <summary>
        /// 测量列宽前把表头都设成最宽的符号 ▲。
        /// WPF 的 Auto 列宽只在下一次布局时算一次并缓存，若按较窄的 ● 测量，
        /// 之后切到 ▲/▼ 就会比列宽多出约 5px，箭头被裁掉一截。
        /// </summary>
        private void UseWidestHeaderPlaceholder()
        {
            _displayHeaderBlock.Text = Loc.T("L_Main_ColDisplay") + " ▲";
            _statusHeaderBlock.Text = Loc.T("L_Main_ColStatus") + " ▲";
        }

        /// <summary>按显示名重排当前行集合。</summary>
        private void ApplyNameSort()
        {
            if (_nameSortAsc == null || Rows.Count < 2) return;
            var sorted = _nameSortAsc == true
                ? Rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList()
                : Rows.OrderByDescending(r => r.DisplayName, StringComparer.CurrentCulture).ToList();
            MoveRows(sorted);
        }

        /// <summary>按状态重排：升序为 已受管→未受管→无权限→文件缺失→不管理，同状态按显示名。</summary>
        private void ApplyStatusSort()
        {
            if (_statusSortAsc == null || Rows.Count < 2) return;
            var sorted = _statusSortAsc == true
                ? Rows.OrderBy(r => StatusRank(r.State)).ThenBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList()
                : Rows.OrderByDescending(r => StatusRank(r.State)).ThenBy(r => r.DisplayName, StringComparer.CurrentCulture).ToList();
            MoveRows(sorted);
        }

        private static int StatusRank(RowState state) => state switch
        {
            RowState.Managed => 0,
            RowState.Unmanaged => 1,
            RowState.PermissionDenied => 2,
            RowState.Missing => 3,
            RowState.Excluded => 4,
            _ => 5
        };

        /// <summary>把行集合移动成目标顺序（ObservableCollection 无法直接重排）。</summary>
        private void MoveRows(IReadOnlyList<RowVM> sorted)
        {
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
                Title = Loc.T("L_Main_ImportDlgTitle"),
                Filter = Loc.T("L_Main_ImportDlgFilter"),
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
                        errors.Add(Loc.T("L_Main_Arm64Warn", entry.DisplayName));
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
                MessageBox.Show(string.Join(Environment.NewLine, errors), Loc.T("L_Main_AddResultTitle"),
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
                MessageBox.Show(ex.UserMessage, Loc.T("L_Main_OpFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                // 非预期异常绝不允许闪退：记录后友好提示
                _service?.Log.Error("单个操作未处理异常：" + ex);
                MessageBox.Show(Loc.T("L_Main_OpFailedDetail", ex.Message),
                    Loc.T("L_Main_OpFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                Loc.T("L_Main_RulesEmptyMsg"),
                Loc.T("L_Main_RulesEmptyTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void BtnAddAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = Rows.Where(r => r.CanAdd).ToList();
            if (targets.Count == 0) { MessageBox.Show(Loc.T("L_Main_NoAddTargets")); return; }
            if (ConfirmEmptyRules(targets) != true) return;

            var errors = RunBatch(targets, p => _service.ApplyInstruction(p, allowEmptyRules: true), out bool denied);
            ConfigManager.Save(_cfg);
            RefreshRows();
            ReportBatch(Loc.T("L_Main_OpAddAll"), targets.Count, errors);
            if (denied) App.AskRestartElevated();
        }

        private void BtnRemoveAll_Click(object sender, RoutedEventArgs e)
        {
            var targets = Rows.Where(r => r.CanRemove).ToList();
            if (targets.Count == 0) { MessageBox.Show(Loc.T("L_Main_NoRemoveTargets")); return; }
            if (MessageBox.Show(Loc.T("L_Main_ConfirmRemoveAll", targets.Count),
                    Loc.T("L_Main_Confirm"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            var errors = RunBatch(targets, p => _service.RemoveInstruction(p), out _);
            ConfigManager.Save(_cfg);
            RefreshRows();
            ReportBatch(Loc.T("L_Main_OpRemoveAll"), targets.Count, errors);
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
            StatusText.Text = Loc.T("L_Main_BatchDone", op, total - errors.Count, total);
            if (errors.Count > 0)
                MessageBox.Show(Loc.T("L_Main_BatchPartial", total - errors.Count, errors.Count,
                                    string.Join(Environment.NewLine, errors)),
                    Loc.T("L_Main_BatchResultTitle", op), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        ? Loc.T("L_Main_DeleteAllNoneExcluded")
                        : Loc.T("L_Main_DeleteAllNone"),
                    Loc.T("L_Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int skipped = _cfg.Programs.Count - count;

            var result = MessageBox.Show(
                Loc.T("L_Main_DeleteAllConfirm", count) +
                (skipped > 0 ? Loc.T("L_Main_DeleteAllSkipped", skipped) : ""),
                Loc.T("L_Main_DeleteAllTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            var btn = sender as Button;
            object originalContent = btn?.Content;
            try
            {
                // 加载状态：禁用按钮、显示"删除中..."、等待光标、状态栏提示
                if (btn != null)
                {
                    btn.IsEnabled = false;
                    btn.Content = Loc.T("L_Main_Deleting");
                }
                Cursor = Cursors.Wait;
                StatusText.Text = Loc.T("L_Main_DeletingAllStatus");
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
                    StatusText.Text = Loc.T("L_Main_DeleteDoneStatus", count);
                    MessageBox.Show(Loc.T("L_Main_DeleteDoneMsg", count), Loc.T("L_Main_DeleteCompleteTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    int ok = count - errors.Count;
                    StatusText.Text = Loc.T("L_Main_DeletePartialStatus", ok, count);
                    MessageBox.Show(Loc.T("L_Main_DeletePartialMsg", ok, errors.Count,
                                        string.Join(Environment.NewLine, errors)),
                        Loc.T("L_Main_DeleteResultTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // 只刷新受影响的那一行。详情窗口保存时会用拷贝替换成新的 ProgramEntry 实例，
            // 若走 RefreshRows() 会因引用不等而整表 Clear()+Add()，把滚动位置冲回顶部。
            RefreshRowAfterDetail(p.Id);
        }

        /// <summary>按 Id 就地更新详情窗口改动的那一行；记录数变化（新增/删除）才整表刷新。</summary>
        private void RefreshRowAfterDetail(string id)
        {
            if (Rows.Count != _cfg.Programs.Count) { RefreshRows(); return; }

            var updated = _cfg.Programs.FirstOrDefault(x => x.Id == id);
            var row = Rows.FirstOrDefault(r => r.P.Id == id);
            if (updated == null || row == null) { RefreshRows(); return; }

            row.SetProgram(updated);
            row.SetState(ComputeState(updated));
            UpdateStatusText();
        }

        // ---------------- 其他工具栏 ----------------

        private void BtnRunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (App.IsElevated()) App.RestartAsStandardUser();
            else App.RestartElevated();
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        /// <summary>设置窗口：快捷方式前缀、快捷方式恢复、全局默认路径（原在「说明」窗口中）。</summary>
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_cfg.Settings.ShortcutPrefix, _cfg) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (dlg.PrefixResult != null && dlg.PrefixResult != _cfg.Settings.ShortcutPrefix)
            {
                _cfg.Settings.ShortcutPrefix = dlg.PrefixResult;
                ConfigManager.Save(_cfg);
                MessageBox.Show(Loc.T("L_Main_PrefixSaved"), Loc.T("L_Common_Warning"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            StartSpin();
            try
            {
                // 清理/扫描放到后台线程，UI 线程保持响应，图标才能持续旋转
                var result = await Task.Run(() => CleanupManager.Run(_cfg));
                ConfigManager.Save(_cfg);
                RefreshRows();
                // RefreshRows 在行集合没变化时不会重建容器，此时若某行内容（显示名/路径）变过，
                // Auto 列宽仍是旧值。刷新是用户表达"按当前数据重排"的意图，这里兜底重算一次。
                RecalculateAutoColumns();
                if (_cfg.Settings.LastCleanupTime is DateTime t)
                {
                    _cleanupTime = t;
                    _cleanupRebuilt = result.WorkspacesRebuilt;
                    _cleanupOrphans = result.OrphanDirsRemoved;
                }
                UpdateCleanupText();
                AnimateRowsIn();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("L_Main_RefreshFailed")); }
            finally { StopSpin(); } // 列表刷新结束才解除禁用
        }

        /// <summary>开始刷新：图标持续旋转 + 按钮禁用（刷新期间不可重复点击）。</summary>
        private void StartSpin()
        {
            if (RefreshIcon.RenderTransform is not RotateTransform rt)
            {
                rt = new RotateTransform();
                RefreshIcon.RenderTransform = rt;
            }
            BtnRefresh.IsEnabled = false;
            // 转一整圈约 0.7s 并循环，等刷新真正结束再停
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rt.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        /// <summary>刷新结束：停转、角度复位、恢复可点击。</summary>
        private void StopSpin()
        {
            if (RefreshIcon.RenderTransform is RotateTransform rt)
            {
                // 清掉动画时钟再复位角度，避免下一圈从当前角度接着算
                rt.BeginAnimation(RotateTransform.AngleProperty, null);
                rt.Angle = 0;
            }
            BtnRefresh.IsEnabled = true;
        }

        /// <summary>刷新完成后让列表行依次淡入，用列表动画替代左下角的“已刷新”文字提示。</summary>
        private void AnimateRowsIn()
        {
            UpdateLayout(); // 先完成布局，确保当前可见行已生成容器
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            int index = 0;
            foreach (var row in Rows)
            {
                if (LvPrograms.ItemContainerGenerator.ContainerFromItem(row) is not ListViewItem container) continue;
                var fade = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(240))
                {
                    BeginTime = TimeSpan.FromMilliseconds(Math.Min(index, 10) * 28),
                    EasingFunction = ease
                };
                container.BeginAnimation(UIElement.OpacityProperty, fade);
                if (++index >= 14) break; // 只对前 14 行做交错动画，避免长列表刷新时拖慢
            }
        }
    }

    /// <summary>
    /// 强制 WPF 重新测量所有 Auto（NaN）列宽。
    /// 原理：Auto 列宽只在 ItemsSource 初始化时测一次；ObservableCollection.Move()
    /// 不会触发重测。方法是先把 Auto 列临时设为 0，UpdateLayout 让 WPF 应用，
    /// 再设回 NaN，WPF 就必须按新内容重新测量。
    /// </summary>
    internal static class GridViewExtensions
    {
        public static void RefreshAutoColumns(this System.Windows.Controls.GridView gridView,
                                              System.Windows.Controls.ListView listView)
        {
            if (gridView == null || listView == null) return;

            // 收集所有 Auto 列（Width == NaN）
            var autoCols = gridView.Columns.Where(c => double.IsNaN(c.Width)).ToList();
            if (autoCols.Count == 0) return;

            // 第一步：强制改变列宽 → 触发 WPF 认为需要重新测量
            foreach (var col in autoCols) col.Width = 0;
            listView.UpdateLayout();

            // 第二步：回到 Auto（NaN） → WPF 必须按当前内容重新测量
            foreach (var col in autoCols) col.Width = double.NaN;
            listView.UpdateLayout();
        }
    }
}
