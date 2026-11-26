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
            UpdateTitle();
            TxtChangelog.Document = MiniMarkdown.ToFlowDocument(ChangelogText, MarkdownTheme.FromTheme(ThemeManager.IsDark));
            // 主题切换后更新记录需按新配色重新渲染（FlowDocument 里的颜色是渲染时写死的）
            ThemeManager.ThemeChanged += RenderChangelog;
            Loc.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) =>
            {
                ThemeManager.ThemeChanged -= RenderChangelog;
                Loc.LanguageChanged -= OnLanguageChanged;
            };
        }

        private void UpdateTitle()
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Title = ver != null
                ? Loc.T("L_About_TitleVersion", $"{ver.Major}.{ver.Minor}.{ver.Build}")
                : Loc.T("L_About_TitleVersion", "1.0.0");
        }

        /// <summary>语言切换：重设窗口标题与更新记录（缺失提示文案随语言变化）。</summary>
        private void OnLanguageChanged()
        {
            UpdateTitle();
            RenderChangelog();
        }

        private readonly string _changelogFile = LoadChangelog();

        /// <summary>更新记录正文；文件缺失时按当前语言显示提示。</summary>
        private string ChangelogText => _changelogFile ?? Loc.T("L_About_NoChangelog");

        /// <summary>按当前主题重新渲染更新记录。</summary>
        private void RenderChangelog()
        {
            TxtChangelog.Document = MiniMarkdown.ToFlowDocument(ChangelogText, MarkdownTheme.FromTheme(ThemeManager.IsDark));
        }

        /// <summary>读取随程序分发的更新记录文件（resources\changelog.txt）；不存在时返回 null。</summary>
        private static string LoadChangelog()
        {
            try
            {
                string path = Path.Combine(AppPaths.AppDir, "resources", "changelog.txt");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch
            {
                // 读取失败时按缺失处理，不影响关于窗口其他内容
            }
            return null;
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

        /// <summary>点击「检查更新」：查询 GitHub 最新 Release，有新版本时询问是否打开浏览器下载。</summary>
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = Loc.T("L_About_Checking");
            CheckUpdateProgress.Visibility = Visibility.Visible;
            try
            {
                var info = await UpdateChecker.FetchLatestAsync();
                if (info == null)
                {
                    MessageBox.Show(Loc.T("L_About_NoRelease", UpdateChecker.ReleasesPageUrl),
                        Loc.T("L_About_CheckUpdate"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (info.Version == null)
                {
                    MessageBox.Show(Loc.T("L_About_BadTag", info.Tag, UpdateChecker.ReleasesPageUrl),
                        Loc.T("L_About_CheckUpdate"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var currentShort = UpdateChecker.CurrentVersion;

                if (info.Version > currentShort)
                {
                    string notes = string.IsNullOrWhiteSpace(info.Notes) ? Loc.T("L_About_NoNotes") : info.Notes.Trim();
                    if (notes.Length > 800) notes = notes.Substring(0, 800) + "…";
                    var result = MessageBox.Show(
                        Loc.T("L_About_NewVersion", info.Tag, currentShort, notes),
                        Loc.T("L_About_CheckUpdate"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(Loc.T("L_About_UpToDate", currentShort),
                        Loc.T("L_About_CheckUpdate"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception)
            {
                MessageBox.Show(Loc.T("L_About_CheckFailed", UpdateChecker.ReleasesPageUrl),
                    Loc.T("L_About_CheckUpdate"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                CheckUpdateProgress.Visibility = Visibility.Collapsed;
                BtnCheckUpdate.Content = Loc.T("L_About_CheckUpdate");
                BtnCheckUpdate.IsEnabled = true;
            }
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
                MessageBox.Show(this, ex.Message, Loc.T("L_About_OpenLogsFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();

            sb.AppendLine(Loc.T("L_About_Intro1"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("L_About_Intro2"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("L_About_Intro3"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("L_About_Intro4"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("L_About_DownloadLink", "https://github.com/Hexiaowen61/PathwinderMgt/releases"));

            Clipboard.SetText(sb.ToString());
            await ShowCopiedHint(ShareButton, Loc.T("L_About_CopiedShare"));
        }

        /// <summary>临时将按钮内容替换为提示文本，800ms 后恢复。</summary>
        private static async Task ShowCopiedHint(Button btn, string hint = null)
        {
            hint ??= Loc.T("L_About_Copied");
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
