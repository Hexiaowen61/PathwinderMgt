using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        public string Name => Source.BuiltIn ? Source.Name + "（内置）" : Source.Name;
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

        /// <summary>规则内容变化（保存）后触发，宿主负责落盘。</summary>
        public event EventHandler Changed;

        /// <param name="customEnabled">是否处于“自定义”模式（允许逐规则编辑模式）。</param>
        /// <param name="getProgramMode">取程序级当前模式（跟随显示/物化新规则用）。</param>
        public RulesPanel(List<Rule> rules, bool customEnabled, Func<string> getProgramMode)
        {
            InitializeComponent();
            _rules = rules;
            _customEnabled = customEnabled;
            _getProgramMode = getProgramMode;
            CmbRuleMode.IsEnabled = customEnabled;
            CmbRuleMode.SelectedIndex = ModeToIndex(getProgramMode());
            LvRules.ItemsSource = Rows;
            foreach (var r in _rules) Rows.Add(new RuleVM(r));
            if (Rows.Count > 0) LvRules.SelectedIndex = 0;
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        private static int ModeToIndex(string mode) => mode switch
        {
            ProgramEntry.RedirectModeSimple => 0,
            ProgramEntry.RedirectModeOverlay => 2,
            _ => 1 // SimplePro（默认）
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
            _current = vm;
            TxtName.Text = vm.Source.Name;
            TxtOrigin.Text = vm.Source.OriginDirectory;
            TxtTarget.Text = vm.Source.TargetDirectory;
            // 非自定义：跟随显示程序级模式；自定义：显示该规则独立值
            CmbRuleMode.SelectedIndex = ModeToIndex(
                _customEnabled ? vm.Source.RedirectMode ?? _getProgramMode()
                               : _getProgramMode());
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            int n = _rules.Count + 1;
            string name = "新规则" + n;
            while (_rules.Any(r => r.Name == name)) name = "新规则" + (++n);

            var r = new Rule { Name = name };
            // 自定义状态下新规则物化为程序级当前值；非自定义保持 null（跟随程序级）
            if (_customEnabled) r.RedirectMode = _getProgramMode();
            _rules.Add(r);
            var vm = new RuleVM(r);
            Rows.Add(vm);
            LvRules.SelectedItem = vm;
            TxtName.Focus();
            RefreshRulesColumns();
            RaiseChanged();
        }

        private void BtnDel_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                MessageBox.Show("请先在列表中选中一条规则。", "删除选中",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_current.Source.BuiltIn)
            {
                MessageBox.Show("该规则是内置规则，可编辑但不可删除。", "删除选中",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int idx = Rows.IndexOf(_current);
            _rules.Remove(_current.Source);
            Rows.Remove(_current);
            if (Rows.Count > 0) LvRules.SelectedIndex = Math.Min(idx, Rows.Count - 1);
            else _current = null;
            RefreshRulesColumns();
            RaiseChanged();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 列表为空（未选中任何规则）时：把当前填写内容作为新规则保存，避免“点了没反应”
            bool isNew = _current == null;
            if (isNew && TxtName.Text.Trim().Length == 0 &&
                TxtOrigin.Text.Trim().Length == 0 && TxtTarget.Text.Trim().Length == 0)
            {
                MessageBox.Show("请先在上方列表选中一条规则；或填写规则内容后点“保存”新建。", "保存",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
                MessageBox.Show(err, "规则校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                Rows.Add(vm);
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
                MessageBox.Show(err, "规则校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            // 保存后强制规则列表按新内容重新测量列宽（Auto 列宽不会随 ObservableCollection 变化自动重算）
            RefreshRulesColumns();
            RaiseChanged();
        }

        /// <summary>强制规则列表按当前内容重新测量 Auto 列宽（适用：增删改规则后；规则路径变长后保存）。</summary>
        private void RefreshRulesColumns()
        {
            if (LvRules.View is not GridView gv) return;
            LvRules.UpdateLayout();
            gv.RefreshAutoColumns();
        }
    }
}
