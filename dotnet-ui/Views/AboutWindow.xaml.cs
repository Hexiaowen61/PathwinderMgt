using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
            Title = ver != null
                ? $"关于 Pathwinder 管理器 v{ver.Major}.{ver.Minor}.{ver.Build}"
                : "关于 Pathwinder 管理器 v1.0.0";
            TxtChangelog.Document = MiniMarkdown.ToFlowDocument(_changelog, MarkdownTheme.FromTheme(ThemeManager.IsDark));
            // 主题切换后更新记录需按新配色重新渲染（FlowDocument 里的颜色是渲染时写死的）
            ThemeManager.ThemeChanged += RenderChangelog;
            Closed += (_, _) => ThemeManager.ThemeChanged -= RenderChangelog;
        }

        private readonly string _changelog = LoadChangelog();

        /// <summary>按当前主题重新渲染更新记录。</summary>
        private void RenderChangelog()
        {
            TxtChangelog.Document = MiniMarkdown.ToFlowDocument(_changelog, MarkdownTheme.FromTheme(ThemeManager.IsDark));
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

        private const string ReleasesApiUrl =
            "https://api.github.com/repos/Hexiaowen61/PathwinderMgt/releases/latest";
        private const string ReleasesLatestUrl =
            "https://github.com/Hexiaowen61/PathwinderMgt/releases/latest";
        private const string ReleasesPageUrl =
            "https://github.com/Hexiaowen61/PathwinderMgt/releases";

        private sealed class ReleaseInfo
        {
            public string Tag { get; init; }
            public string Notes { get; init; }
            public string PageUrl { get; init; }
        }

        /// <summary>点击「检查更新」：查询 GitHub 最新 Release，有新版本时询问是否打开浏览器下载。</summary>
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = "检查中…";
            CheckUpdateProgress.Visibility = Visibility.Visible;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PathwinderMgt");

                var info = await FetchLatestReleaseAsync(http);
                if (info == null)
                {
                    MessageBox.Show("暂未发布任何版本，可前往下载页查看：" + Environment.NewLine + ReleasesPageUrl,
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Version latest = ParseReleaseVersion(info.Tag);
                if (latest == null)
                {
                    MessageBox.Show($"未能识别最新版本号（{info.Tag}）。" + Environment.NewLine +
                                    "可前往下载页查看：" + ReleasesPageUrl,
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                var currentShort = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

                if (latest > currentShort)
                {
                    string notes = string.IsNullOrWhiteSpace(info.Notes) ? "（该版本未提供更新说明）" : info.Notes.Trim();
                    if (notes.Length > 800) notes = notes.Substring(0, 800) + "…";
                    var result = MessageBox.Show(
                        $"发现新版本 {info.Tag}（当前 {currentShort}）。" + Environment.NewLine + Environment.NewLine +
                        notes + Environment.NewLine + Environment.NewLine +
                        "点击「是」打开浏览器下载，点击「否」取消。",
                        "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show($"当前已是最新版本（{currentShort}）。",
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception)
            {
                MessageBox.Show("检查更新失败，请检查网络连接后重试。" + Environment.NewLine +
                                "也可以直接打开下载页查看最新版本：" + ReleasesPageUrl,
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                CheckUpdateProgress.Visibility = Visibility.Collapsed;
                BtnCheckUpdate.Content = "检查更新";
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        /// <summary>先走 GitHub API（可获得更新说明）；接口限流或不可用时，回退到 releases/latest 的重定向地址。</summary>
        private static async Task<ReleaseInfo> FetchLatestReleaseAsync(HttpClient http)
        {
            try
            {
                using var response = await http.GetAsync(ReleasesApiUrl);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    string tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
                    string body = root.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() ?? string.Empty : string.Empty;
                    string pageUrl = root.TryGetProperty("html_url", out var urlNode) && !string.IsNullOrWhiteSpace(urlNode.GetString())
                        ? urlNode.GetString() : ReleasesPageUrl;
                    if (!string.IsNullOrWhiteSpace(tag))
                        return new ReleaseInfo { Tag = tag, Notes = body, PageUrl = pageUrl };
                }
            }
            catch
            {
                // API 不可用（限流、被墙等），继续尝试下面的回退方案
            }

            // 回退：/releases/latest 会重定向到 /releases/tag/<tag>，不消耗 API 配额
            using var pageResponse = await http.GetAsync(ReleasesLatestUrl);
            string finalUrl = pageResponse.RequestMessage?.RequestUri?.AbsoluteUri;
            if (!string.IsNullOrEmpty(finalUrl))
            {
                var match = System.Text.RegularExpressions.Regex.Match(finalUrl, @"/releases/tag/([^/?#]+)");
                if (match.Success)
                {
                    return new ReleaseInfo
                    {
                        Tag = Uri.UnescapeDataString(match.Groups[1].Value),
                        Notes = string.Empty,
                        PageUrl = finalUrl
                    };
                }
            }

            return null;
        }

        /// <summary>把 "v1.0.3" 之类的 Release tag 解析成 Version；无法解析时返回 null。</summary>
        private static Version ParseReleaseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(tag, @"(\d+)\s*\.\s*(\d+)(?:\s*\.\s*(\d+))?");
            if (!m.Success) return null;
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return new Version(major, minor, build);
        }

        /// <summary>打开日志目录（原先在「设置」的日志卡片里）。</summary>
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
            sb.AppendLine("下载地址：https://github.com/Hexiaowen61/PathwinderMgt/releases");

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
                Foreground = PathwinderMgt.Services.ThemeManager.Brush("Success")
            };
            await Task.Delay(800);
            btn.Content = original;
            btn.IsEnabled = originalEnabled;
        }
    }
}
