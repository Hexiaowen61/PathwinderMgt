using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;

using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>关于窗口：展示应用介绍、使用注意事项、开源地址。设置项（前缀/快捷方式恢复/全局默认路径）已移至「设置」窗口。</summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver != null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "版本 1.0.0";
            TxtChangelog.Text = LoadChangelog();
        }

        /// <summary>读取随程序分发的更新记录文件（resources\changelog.txt）。</summary>
        private static string LoadChangelog()
        {
            try
            {
                string path = Path.Combine(AppPaths.AppDir, "resources", "changelog.txt");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch
            {
                // 读取失败时退回提示文本，不影响关于窗口其他内容
            }
            return "（未找到更新记录文件 resources\\changelog.txt）";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
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
