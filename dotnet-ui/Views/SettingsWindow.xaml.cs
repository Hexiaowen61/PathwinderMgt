using System.Diagnostics;
using System.IO;
using System.Windows;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>设置窗口：快捷方式前缀、快捷方式恢复工具、全局默认路径。原先位于「说明」窗口中，现独立出来。</summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>用户确认后的前缀结果（已校验），供调用方读取。</summary>
        public string PrefixResult { get; private set; }

        private readonly AppConfig _cfg;
        private readonly Rule _builtInRule; // 内置全局规则（全局默认路径即其 OriginDirectory）

        public SettingsWindow(string currentPrefix, AppConfig cfg)
        {
            InitializeComponent();

            _cfg = cfg;
            _builtInRule = cfg?.GlobalRules?.Find(r => r.BuiltIn);

            PrefixBox.Text = currentPrefix;
            PrefixBox.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            GlobalOriginBox.Text = CurrentGlobalOrigin();
        }

        /// <summary>当前已保存的全局默认路径（内置规则源目录；无配置时显示默认值）。</summary>
        private string CurrentGlobalOrigin()
            => _builtInRule?.OriginDirectory is string s && s.Length > 0
                ? s
                : ConfigManager.DefaultOriginDirectory();

        /// <summary>打开快捷方式恢复窗口（独立工具，使用已保存的前缀）。
        /// 保存后关闭本窗口：WPF 模态窗口存活期间，同线程其他窗口的 OLE 拖放目标会返回 None（禁止光标），
        /// 恢复窗口必须脱离模态环境才能正常接收拖入。</summary>
        private void BtnRestoreShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (!TrySaveGlobalOrigin()) return;

            PrefixResult = ConfigManager.ValidatePrefix(PrefixBox.Text);

            var win = new RestoreWindow(PrefixResult);
            win.Show();
            win.Activate();
            DialogResult = true;
        }

        /// <summary>打开日志目录（原先主窗口标题栏的「日志」按钮）。</summary>
        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开日志目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------- 全局默认路径 ----------------

        /// <summary>浏览文件夹选择全局默认路径。</summary>
        private void BtnBrowseGlobalOrigin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择全局默认路径" };
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
            SetHint("已恢复为已保存的路径。");
        }

        /// <summary>校验并保存全局默认路径。返回 true 表示成功（或无需保存），false 表示校验失败。</summary>
        private bool TrySaveGlobalOrigin()
        {
            if (_builtInRule == null || _cfg == null)
            {
                MessageBox.Show(this, "未找到内置全局规则，无法保存。", "保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 规范化：去首尾空白与多余的反斜杠（盘符根除外）
            string path = GlobalOriginBox.Text.Trim();
            if (path.Length > 3 && path.EndsWith("\\")) path = path.TrimEnd('\\');

            string err = ValidateGlobalOrigin(path);
            if (err == null && !Directory.Exists(path))
                err = "所选目录不存在。";
            if (err != null)
            {
                SetHint(err);
                return false;
            }

            if (string.Equals(path.TrimEnd('\\'), _builtInRule.OriginDirectory?.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                SetHint("路径未变化。");
                return true; // 没变化也算成功，允许关闭
            }

            _builtInRule.OriginDirectory = path;
            ConfigManager.Save(_cfg);
            GlobalOriginBox.Text = path;
            SetHint("已保存，新登记的程序将默认使用该路径。");
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
            if (string.IsNullOrWhiteSpace(path)) return "全局默认路径不能为空。";
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "路径包含非法字符。";
            if (!Path.IsPathRooted(path)) return "必须是绝对路径（如 C:\\Users）。";
            string root = Path.GetPathRoot(path)?.TrimEnd('\\');
            if (path.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase))
                return "不能设置为盘符根目录（如 C:\\）。";
            // 源目录覆盖管理器安装目录会把自身生成物（目标目录 <管理器>\AppData）一并卷入重定向
            if (RuleValidator.IsNested(AppPaths.AppDir, path))
                return "不能设置为管理器安装目录或其上级目录。";
            return null;
        }

        private void UpdatePreview()
        {
            PrefixPreview.Text = $"预览：{PrefixBox.Text}微信";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. 校验并保存全局默认路径（失败则不关闭窗口）
            if (!TrySaveGlobalOrigin()) return;

            // 2. 保存前缀
            PrefixResult = ConfigManager.ValidatePrefix(PrefixBox.Text);
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
