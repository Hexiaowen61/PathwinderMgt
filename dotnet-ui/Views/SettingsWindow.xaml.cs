using System;
using System.IO;
using System.Windows;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>设置窗口：语言、主题、快捷方式前缀、快捷方式恢复工具、全局默认路径。</summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>用户确认后的前缀结果（已校验），供调用方读取。</summary>
        public string PrefixResult { get; private set; }

        private readonly AppConfig _cfg;
        private readonly Rule _builtInRule; // 内置全局规则（全局默认路径即其 OriginDirectory）
        private readonly string _initialTheme; // 打开设置前的主题，点「取消」时恢复
        private readonly string _initialLanguage; // 打开设置前的语言，点「取消」时恢复
        private readonly string _initialPrefix; // 以下四个是「打开时」的快照，只有改动过才允许保存
        private readonly string _initialGlobalOrigin;
        private readonly bool _initialSuppressVcRuntime;

        public SettingsWindow(string currentPrefix, AppConfig cfg)
        {
            InitializeComponent();

            _cfg = cfg;
            _builtInRule = cfg?.GlobalRules?.Find(r => r.BuiltIn);

            // 先记录初始值，再填充控件（填充过程的 TextChanged 也能正确算出「未改动」）
            _initialTheme = ThemeManager.Normalize(ThemeManager.Current);
            _initialLanguage = Loc.Normalize(Loc.Current);
            _initialGlobalOrigin = CurrentGlobalOrigin();
            _initialSuppressVcRuntime = _cfg?.Settings?.SuppressVcRuntimePrompt == true;
            _initialPrefix = currentPrefix ?? "";

            PrefixBox.Text = _initialPrefix;
            GlobalOriginBox.Text = _initialGlobalOrigin;
            ChkSuppressVcRuntime.IsChecked = _initialSuppressVcRuntime;

            PrefixBox.TextChanged += (_, _) => { UpdatePreview(); UpdateDirty(); };
            GlobalOriginBox.TextChanged += (_, _) => UpdateDirty();
            ChkSuppressVcRuntime.Checked += (_, _) => UpdateDirty();
            ChkSuppressVcRuntime.Unchecked += (_, _) => UpdateDirty();

            UpdatePreview();
            UpdateThemeButton();
            UpdateLanguageButton();
            UpdateDirty();

            // 语言切换后，代码赋值的按钮文案要跟着变
            Loc.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            UpdateThemeButton();
            UpdateLanguageButton();
            UpdatePreview();
        }

        /// <summary>「保存」按钮只在设置项相对打开时有改动才可用。</summary>
        private void UpdateDirty()
        {
            bool dirty =
                !string.Equals(PrefixBox.Text ?? "", _initialPrefix, StringComparison.Ordinal) ||
                !string.Equals(NormalizePath(GlobalOriginBox.Text), NormalizePath(_initialGlobalOrigin),
                    StringComparison.OrdinalIgnoreCase) ||
                (ChkSuppressVcRuntime.IsChecked == true) != _initialSuppressVcRuntime ||
                !string.Equals(ThemeManager.Current, _initialTheme, StringComparison.Ordinal) ||
                !string.Equals(Loc.Normalize(Loc.Current), _initialLanguage, StringComparison.Ordinal);
            BtnSave.IsEnabled = dirty;
        }

        private static string NormalizePath(string path) => (path ?? "").Trim().TrimEnd('\\');

        /// <summary>语言按钮文案始终显示当前实际生效的语言。</summary>
        private void UpdateLanguageButton()
        {
            BtnLanguage.Content = Loc.T("L_Settings_CurrentLang",
                Loc.IsEnglish ? Loc.T("L_Settings_LangEn") : Loc.T("L_Settings_LangZh"));
        }

        /// <summary>点击切换语言：立即全局生效，确定时写入配置。</summary>
        private void BtnLanguage_Click(object sender, RoutedEventArgs e)
        {
            Loc.Apply(Loc.IsEnglish ? Loc.Zh : Loc.En);
            UpdateDirty();
        }

        /// <summary>把当前语言写入 config.json（无变化时不写盘）。</summary>
        private void PersistLanguage()
        {
            if (_cfg == null) return;
            string lang = Loc.Normalize(Loc.Current);
            if (_cfg.Settings.Language == lang) return;
            _cfg.Settings.Language = lang;
            ConfigManager.Save(_cfg);
        }

        /// <summary>主题按钮文案始终显示当前实际生效的主题。</summary>
        private void UpdateThemeButton()
        {
            BtnTheme.Content = ThemeManager.IsDark ? Loc.T("L_Settings_ThemeDark") : Loc.T("L_Settings_ThemeLight");
            BtnTheme.ToolTip = ThemeManager.IsDark ? Loc.T("L_Settings_ThemeTipDark") : Loc.T("L_Settings_ThemeTipLight");
        }

        /// <summary>点击切换主题：立即生效，确定时写入配置。</summary>
        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Apply(ThemeManager.IsDark ? ThemeManager.Light : ThemeManager.Dark);
            UpdateThemeButton();
            UpdateDirty();
        }

        /// <summary>把当前主题写入 config.json（无变化时不写盘）。</summary>
        private void PersistTheme()
        {
            if (_cfg == null || _cfg.Settings.Theme == ThemeManager.Current) return;
            _cfg.Settings.Theme = ThemeManager.Current;
            ConfigManager.Save(_cfg);
        }

        /// <summary>当前已保存的全局默认路径（内置规则源目录；无配置时显示默认值）。</summary>
        private string CurrentGlobalOrigin()
            => _builtInRule?.OriginDirectory is string s && s.Length > 0
                ? s
                : ConfigManager.DefaultOriginDirectory();

        /// <summary>打开快捷方式恢复窗口（独立工具，使用已保存的前缀）。
        /// 保存后关闭本窗口：WPF 模态窗口存活期间，同线程其他窗口的 OLE 拖放目标会返回 None（禁止光标），
        /// 恢复窗口必须脱离模态环境才能正常接收拖入。
        /// 注意：这里不落盘任何设置——全局默认路径等改动只有点「保存」才生效。</summary>
        private void BtnRestoreShortcut_Click(object sender, RoutedEventArgs e)
        {
            PrefixResult = ConfigManager.ValidatePrefix(PrefixBox.Text);

            var win = new RestoreWindow(PrefixResult);
            win.Show();
            win.Activate();
            PersistTheme();    // 主题是即时预览项，本窗口关闭后保持当前外观
            PersistLanguage(); // 语言同理
            DialogResult = true;
        }

        // ---------------- 全局默认路径 ----------------

        /// <summary>浏览文件夹选择全局默认路径。</summary>
        private void BtnBrowseGlobalOrigin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("L_Settings_PickOrigin") };
            string current = GlobalOriginBox.Text.Trim();
            if (current.Length > 0 && Directory.Exists(current))
                dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
                GlobalOriginBox.Text = dlg.FolderName;
        }

        /// <summary>恢复默认：把输入框恢复为已保存的值。</summary>
        private void BtnCancelGlobalOrigin_Click(object sender, RoutedEventArgs e)
        {
            GlobalOriginBox.Text = CurrentGlobalOrigin();
            SetHint(Loc.T("L_Settings_SavedHint"));
        }

        /// <summary>校验并保存全局默认路径。返回 true 表示成功（或无需保存），false 表示校验失败。</summary>
        private bool TrySaveGlobalOrigin()
        {
            if (_builtInRule == null || _cfg == null)
            {
                MessageBox.Show(this, Loc.T("L_Settings_NoBuiltinRule"), Loc.T("L_Settings_SaveFailed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 规范化：去首尾空白与多余的反斜杠（盘符根除外）
            string path = GlobalOriginBox.Text.Trim();
            if (path.Length > 3 && path.EndsWith("\\")) path = path.TrimEnd('\\');

            string err = ValidateGlobalOrigin(path);
            if (err == null && !Directory.Exists(path))
                err = Loc.T("L_Settings_PathNotExist");
            if (err != null)
            {
                SetHint(err);
                return false;
            }

            if (string.Equals(path.TrimEnd('\\'), _builtInRule.OriginDirectory?.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                SetHint(Loc.T("L_Settings_PathUnchanged"));
                return true; // 没变化也算成功，允许关闭
            }

            _builtInRule.OriginDirectory = path;
            ConfigManager.Save(_cfg);
            GlobalOriginBox.Text = path;
            SetHint(Loc.T("L_Settings_PathSaved"));
            return true;
        }

        /// <summary>显示卡片内的提示信息；无内容时折叠，避免卡片底部多出空白。</summary>
        private void SetHint(string text)
        {
            TxtGlobalOriginHint.Text = text ?? string.Empty;
            TxtGlobalOriginHint.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>全局默认路径校验：非空、绝对路径、非盘符根、不含非法字符、不指向管理器安装目录及上级。</summary>
        private static string ValidateGlobalOrigin(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Loc.T("L_Settings_PathEmpty");
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return Loc.T("L_Settings_PathInvalidChars");
            if (!Path.IsPathRooted(path)) return Loc.T("L_Settings_PathNotAbsolute");
            string root = Path.GetPathRoot(path)?.TrimEnd('\\');
            if (path.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase))
                return Loc.T("L_Settings_PathRoot");
            // 源目录覆盖管理器安装目录会把自身生成物（目标目录 <管理器>\AppData）一并卷入重定向
            if (RuleValidator.IsNested(AppPaths.AppDir, path))
                return Loc.T("L_Settings_PathNested");
            return null;
        }

        private void UpdatePreview()
        {
            PrefixPreview.Text = Loc.T("L_Settings_PrefixPreview", PrefixBox.Text);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. 校验并保存全局默认路径（失败则不关闭窗口）
            if (!TrySaveGlobalOrigin()) return;

            // 2. 保存前缀
            PrefixResult = ConfigManager.ValidatePrefix(PrefixBox.Text);

            // 3. 保存「缺少运行库时不再提示」开关
            bool suppress = ChkSuppressVcRuntime.IsChecked == true;
            if (_cfg != null && _cfg.Settings.SuppressVcRuntimePrompt != suppress)
            {
                _cfg.Settings.SuppressVcRuntimePrompt = suppress;
                ConfigManager.Save(_cfg);
            }

            // 4. 保存主题与语言（预览阶段已应用，这里只落盘）
            PersistTheme();
            PersistLanguage();

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 取消：撤销预览阶段的主题与语言切换
            ThemeManager.Apply(_initialTheme);
            Loc.Apply(_initialLanguage);
            DialogResult = false;
        }
    }
}
