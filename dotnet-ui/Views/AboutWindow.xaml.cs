using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>关于窗口：展示应用介绍、使用注意事项、开源地址，并提供前缀与全局默认路径设置。</summary>
    public partial class AboutWindow : Window
    {
        /// <summary>用户确认后的前缀结果（已校验），供调用方读取。</summary>
        public string PrefixResult { get; private set; }

        private readonly AppConfig _cfg;
        private readonly Rule _builtInRule; // 内置全局规则（全局默认路径即其 OriginDirectory）

        public AboutWindow() : this(Settings.DefaultShortcutPrefix, null) { }

        public AboutWindow(string currentPrefix, AppConfig cfg)
        {
            InitializeComponent();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver != null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "版本 1.0.0";

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

        /// <summary>打开快捷方式恢复窗口（独立工具，使用已保存的前缀，不影响本窗口的前缀编辑）。
        /// 打开后关闭本窗口：WPF 模态窗口存活期间，同线程其他窗口的 OLE 拖放目标会返回 None（禁止光标），
        /// 恢复窗口必须脱离模态环境才能正常接收拖入。</summary>
        private void BtnRestoreShortcut_Click(object sender, RoutedEventArgs e)
        {
            string prefix = _cfg?.Settings.ShortcutPrefix ?? Settings.DefaultShortcutPrefix;
            var win = new RestoreWindow(prefix);
            win.Show();
            win.Activate();
            Close();
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
            TxtGlobalOriginHint.Text = "已恢复为已保存的路径。";
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
                TxtGlobalOriginHint.Text = err;
                return false;
            }

            if (string.Equals(path.TrimEnd('\\'), _builtInRule.OriginDirectory?.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                TxtGlobalOriginHint.Text = "路径未变化。";
                return true; // 没变化也算成功，允许关闭
            }

            _builtInRule.OriginDirectory = path;
            ConfigManager.Save(_cfg);
            GlobalOriginBox.Text = path;
            TxtGlobalOriginHint.Text = "已保存，新登记的程序将默认使用该路径。";
            return true;
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
            Close();
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private async void CopyChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string text) return;
            Clipboard.SetText(text);
            await ShowCopiedHint(btn);
        }

        private async void ShareButton_Click(object sender, RoutedEventArgs e)
        {

            
            var sb = new StringBuilder();

            sb.AppendLine("Pathwinder 管理器是一款 Windows 桌面工具，让你通过可视化界面，为任意已安装程序添加文件路径重定向能力——无需修改程序代码，即可改变程序的文件读写路径。");
            sb.AppendLine();
            sb.AppendLine("举个例子：大多数程序会在 “C:\\Users\\用户名\\AppData” 下创建数据文件，而本工具能将这些文件重定向到指定目录，且不影响程序正常运行。");
            sb.AppendLine();
            sb.AppendLine("简单来说，如果程序在某个目录（如 “C:\\ddd\\eee\\fff”）下创建了数据文件，而你不想让这些文件写进该目录，就可以用本工具把它们重定向到别处。");
            sb.AppendLine();
            sb.AppendLine("担心涉及的文件夹太多？你可以添加多条重定向规则，或直接指定一个上层目录（如 “C:\\ddd”）一并覆盖。");
            sb.AppendLine();
            sb.AppendLine("下载地址：https://hexiaowen.lanzouw.com/b00g46ub");
            
            Clipboard.SetText(sb.ToString());
            await ShowCopiedHint(ShareButton, "已复制到剪贴板 ✓");
        }

        /// <summary>临时将按钮内容替换为提示文本，800ms 后恢复。</summary>
        private static async Task ShowCopiedHint(Button btn, string hint = "已复制 ✓")
        {
            var original = btn.Content;
            var originalEnabled = btn.IsEnabled;
            btn.IsEnabled = false;
            btn.Content = new TextBlock
            {
                Text = hint,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"))
            };
            await Task.Delay(800);
            btn.Content = original;
            btn.IsEnabled = originalEnabled;
        }
    }
}
