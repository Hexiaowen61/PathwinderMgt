using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    public class RuleVM
    {
        public Rule Source { get; }
        public string Name => Source.BuiltIn ? Source.Name + Loc.T("L_Rules_BuiltInSuffix") : Source.Name;
        public string OriginDirectory => Source.OriginDirectory;
        public string TargetDirectory => Source.TargetDirectory;

        public RuleVM(Rule r) => Source = r;
    }

    /// <summary>规则编辑面板：直接编辑传入的规则集合（单程序的规则，无新建/删除入口）。</summary>
    public partial class RulesPanel : UserControl
    {
        private readonly List<Rule> _rules;
        public ObservableCollection<RuleVM> Rows { get; } = new();
        private RuleVM _current;

        // 规则级模式编辑：仅详情页「模式切换」为自定义时启用；否则置灰并跟随显示程序级模式
        private bool _customEnabled;
        private readonly Func<string> _getProgramMode;

        /// <summary>按规则名生成默认目标路径（数据根\程序名\规则名），由宿主提供。</summary>
        private readonly Func<string, string> _getAutoTarget;

        // 自动填充状态：记录最近一次由原始路径生成的值，用户手工改过就不再覆盖
        private bool _loadingEdit;
        private string _autoName;
        private string _autoTargetValue;

        /// <summary>规则内容变化（保存）后触发，宿主负责落盘。</summary>
        public event EventHandler Changed;

        /// <param name="customEnabled">是否处于“自定义”模式（允许逐规则编辑模式）。</param>
        /// <param name="getProgramMode">取程序级当前模式（跟随显示/物化新规则用）。</param>
        /// <param name="getAutoTarget">按规则名生成默认目标路径（程序数据根\规则名）。</param>
        public RulesPanel(List<Rule> rules, bool customEnabled, Func<string> getProgramMode,
                          Func<string, string> getAutoTarget)
        {
            InitializeComponent();
            _rules = rules;
            _customEnabled = customEnabled;
            _getProgramMode = getProgramMode;
            _getAutoTarget = getAutoTarget;
            CmbRuleMode.IsEnabled = customEnabled;
            CmbRuleMode.SelectedIndex = ModeToIndex(getProgramMode());
            LvRules.ItemsSource = Rows;
            foreach (var r in _rules) Rows.Add(new RuleVM(r));
            if (Rows.Count > 0) LvRules.SelectedIndex = 0;

            // 语言切换后规则名里的「内置」等后缀需重新生成（RuleVM 未实现属性通知）
            Loc.LanguageChanged += OnLanguageChanged;
            Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            var selected = _current;
            var items = Rows.ToList();
            Rows.Clear();
            foreach (var vm in items) Rows.Add(vm);
            if (selected != null) LvRules.SelectedItem = selected;
            RefreshRulesColumns();
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        private static int ModeToIndex(string mode) => mode switch
        {
            ProgramEntry.RedirectModeSimple => 0,
            ProgramEntry.RedirectModeOverlay => 2,
            _ => 1 // SimplePro
        };

        private static string IndexToMode(int index) => index switch
        {
            0 => ProgramEntry.RedirectModeSimple,
            2 => ProgramEntry.RedirectModeOverlay,
            _ => ProgramEntry.RedirectModeSimplePro
        };

        /// <summary>主模式切换后由宿主调用：自定义→启用编辑并显示各规则值；非自定义→置灰并跟随显示主模式。</summary>
        public void SetCustomMode(bool enabled)
        {
            _customEnabled = enabled;
            CmbRuleMode.IsEnabled = enabled;
            CmbRuleMode.SelectedIndex = ModeToIndex(
                enabled && _current != null
                    ? _current.Source.RedirectMode ?? _getProgramMode()
                    : _getProgramMode());
        }

        private void LvRules_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvRules.SelectedItem is RuleVM vm) LoadEdit(vm);
        }

        private void LoadEdit(RuleVM vm)
        {
            _loadingEdit = true;
            _current = vm;
            TxtName.Text = vm.Source.Name;
            TxtOrigin.Text = vm.Source.OriginDirectory;
            TxtTarget.Text = vm.Source.TargetDirectory;
            // 非自定义：跟随显示程序级模式；自定义：显示该规则独立值
            CmbRuleMode.SelectedIndex = ModeToIndex(
                _customEnabled ? vm.Source.RedirectMode ?? _getProgramMode()
                               : _getProgramMode());
            _loadingEdit = false;
            // 已有规则的内容视为用户值，输入原始路径时不再自动覆盖
            _autoName = null;
            _autoTargetValue = null;
        }

        /// <summary>
        /// 原始路径优先输入时自动补规则名与目标路径：规则名取路径最后一级，
        /// 目标路径 = 程序数据根\规则名。仅当对应字段为空或仍是上次自动生成的值时覆盖，
        /// 避免冲掉用户手工修改。
        /// </summary>
        private void ApplyAutoFillFromOrigin()
        {
            string origin = TxtOrigin.Text.Trim();
            if (origin.Length == 0) return;

            string derivedName = ConfigManager.RuleNameFromOrigin(origin, null);
            if (string.IsNullOrEmpty(derivedName)) return;

            string currentName = TxtName.Text.Trim();
            if (currentName.Length == 0 || currentName == _autoName)
            {
                TxtName.Text = derivedName;
                _autoName = derivedName;
            }

            string autoTarget = _getAutoTarget?.Invoke(derivedName);
            if (string.IsNullOrEmpty(autoTarget)) return;

            string currentTarget = TxtTarget.Text.Trim();
            if (currentTarget.Length == 0 || currentTarget == _autoTargetValue)
            {
                TxtTarget.Text = autoTarget;
                _autoTargetValue = autoTarget;
            }
        }

        private void TxtOrigin_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingEdit) return;
            ApplyAutoFillFromOrigin();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            // 新建只是草稿：不加入 _rules、不触发 Changed，因此不会落盘，也不会同步到工作区 ini。
            // 点「保存」才正式写入；未保存就关闭窗口时草稿随面板一起丢弃。
            int n = Rows.Count + 1;
            string name = Loc.T("L_Rules_NewRule") + n;
            while (Rows.Any(v => v.Source.Name == name)) name = Loc.T("L_Rules_NewRule") + (++n);

            var r = new Rule { Name = name };
            // 自定义状态下新规则物化为程序级当前值；非自定义保持 null（跟随程序级）
            if (_customEnabled) r.RedirectMode = _getProgramMode();
            var vm = new RuleVM(r);
            Rows.Add(vm);
            LvRules.SelectedItem = vm;
            // 草稿的占位名视为自动值：随后输入原始路径时会被最后一级替换
            _autoName = r.Name;
            _autoTargetValue = "";
            TxtName.Focus();
            RefreshRulesColumns();
        }

        private void BtnDel_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                MessageBox.Show(Loc.T("L_Rules_PickFirst"), Loc.T("L_Rules_Delete"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_current.Source.BuiltIn)
            {
                MessageBox.Show(Loc.T("L_Rules_BuiltInNoDelete"), Loc.T("L_Rules_Delete"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int idx = Rows.IndexOf(_current);
            // 草稿（新建后未保存）不在 _rules 中：只从列表移除，无需落盘/同步 ini
            bool isDraft = !_rules.Contains(_current.Source);
            if (!isDraft) _rules.Remove(_current.Source);
            Rows.Remove(_current);
            if (Rows.Count > 0) LvRules.SelectedIndex = Math.Min(idx, Rows.Count - 1);
            else _current = null;
            RefreshRulesColumns();
            if (!isDraft) RaiseChanged();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 列表为空（未选中任何规则）时：把当前填写内容作为新规则保存，避免“点了没反应”
            // 「新建规则」产生的草稿不在 _rules 中，保存同样走新增分支
            bool isNew = _current == null || !_rules.Contains(_current.Source);
            if (isNew && TxtName.Text.Trim().Length == 0 &&
                TxtOrigin.Text.Trim().Length == 0 && TxtTarget.Text.Trim().Length == 0)
            {
                MessageBox.Show(Loc.T("L_Rules_SaveEmpty"), Loc.T("L_Rules_Save"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 兜底：只填了原始路径就点保存时，自动补齐规则名与目标路径
            ApplyAutoFillFromOrigin();

            var draft = new Rule
            {
                Name = TxtName.Text.Trim(),
                OriginDirectory = TxtOrigin.Text.Trim(),
                TargetDirectory = TxtTarget.Text.Trim(),
                // 文件通配已从界面移除；已有配置中的 FilePatterns 原样保留
                FilePatterns = isNew ? new List<string>() : _current.Source.FilePatterns ?? new List<string>(),
                BuiltIn = !isNew && _current.Source.BuiltIn
            };

            string err = RuleValidator.Validate(draft);
            if (err != null)
            {
                MessageBox.Show(err, Loc.T("L_Rules_Validate"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 自动填充只计算路径，这里在真正保存时再创建目标目录，避免输入过程中产生空目录
            try { Directory.CreateDirectory(draft.TargetDirectory); } catch { }

            if (isNew)
            {
                // 名称冲突时自动改名，保证唯一
                if (_rules.Any(r => r.Name == draft.Name))
                {
                    int n = _rules.Count + 1;
                    while (_rules.Any(r => r.Name == draft.Name + n)) n++;
                    draft.Name += n;
                }
                // 自定义状态下新规则物化为程序级当前值；非自定义保持 null（跟随程序级）
                if (_customEnabled) draft.RedirectMode = _getProgramMode();
                _rules.Add(draft);
                var vm = new RuleVM(draft);
                if (_current != null)
                {
                    // 草稿已显示在列表中：原位替换为正式规则，避免多出一行
                    Rows[Rows.IndexOf(_current)] = vm;
                }
                else Rows.Add(vm);
                _current = vm;
                LvRules.SelectedItem = vm;
            }
            else
            {
                _current.Source.Name = draft.Name;
                _current.Source.OriginDirectory = draft.OriginDirectory;
                _current.Source.TargetDirectory = draft.TargetDirectory;
                // 自定义状态下保存规则级独立模式；非自定义保持 null（跟随程序级）
                if (_customEnabled)
                    _current.Source.RedirectMode = IndexToMode(CmbRuleMode.SelectedIndex);

                err = RuleValidator.ValidateList(_rules);
                int idx = Rows.IndexOf(_current);
                var newVm = new RuleVM(_current.Source);
                Rows[idx] = newVm;
                _current = newVm;
                LvRules.SelectedItem = newVm;
            }

            if (err != null)
                MessageBox.Show(err, Loc.T("L_Rules_Validate"), MessageBoxButton.OK, MessageBoxImage.Warning);
            // 保存后强制规则列表按新内容重新测量列宽（Auto 列宽不会随 ObservableCollection 变化自动重算）
            RefreshRulesColumns();
            RaiseChanged();
        }

        /// <summary>强制规则列表按当前内容重新测量 Auto 列宽（适用：增删改规则后；规则路径变长后保存）。</summary>
        private void RefreshRulesColumns()
        {
            if (LvRules.View is not GridView gv) return;
            LvRules.UpdateLayout();
            gv.RefreshAutoColumns(LvRules);
        }
    }
}
