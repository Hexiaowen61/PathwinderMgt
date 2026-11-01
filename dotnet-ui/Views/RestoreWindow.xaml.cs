using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using PathwinderMgt.Models;
using PathwinderMgt.Services;

namespace PathwinderMgt.Views
{
    /// <summary>
    /// 快捷方式恢复窗口：拖入受管快捷方式，后台还原为普通快捷方式输出到桌面并删除旧文件。
    /// 独立工具，不涉及主列表与配置；结果与失败原因以日志方式展示。
    /// </summary>
    public partial class RestoreWindow : Window
    {
        private readonly string _prefix;
        private bool _busy; // 上一批尚未处理完时忽略新拖入，避免并发写桌面文件

        // 管理员运行时系统 UIPI 会拦截资源管理器（中等完整性）的拖放消息，需显式放行
        private const uint WM_DROPFILES = 0x0233;
        private const uint WM_COPYDATA = 0x004A;
        private const uint WM_COPYGLOBALDATA = 0x0049;

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilter(uint message, uint flags);

        public RestoreWindow(string prefix)
        {
            InitializeComponent();
            _prefix = string.IsNullOrEmpty(prefix) ? Settings.DefaultShortcutPrefix : prefix;
            AppendLog("请拖入需要恢复的快捷方式");
            SourceInitialized += (_, _) =>
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent())
                        .IsInRole(WindowsBuiltInRole.Administrator))
                {
                    foreach (uint msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                        ChangeWindowMessageFilter(msg, 1); // MSGFLT_ADD：允许低完整性进程投递
                }
            };
        }

        /// <summary>
        /// 必须用隧道事件（Preview*）处理拖拽：窗口内含 TextBox（LogBox），其内部 TextEditor 会在冒泡阶段
        /// 截胡拖拽事件并置 Handled，导致窗口级 Drop 永远不触发、光标显示为禁止。
        /// 在隧道阶段标记 Handled 可阻止 TextEditor 插手，并向 OLE 返回 Copy 效果（可放置光标）。
        /// </summary>
        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            OnFilesDropped(e.Data);
        }

        private async void OnFilesDropped(IDataObject data)
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            if (_busy) { AppendLog("正在处理上一批，请稍候…"); return; }
            _busy = true;
            try
            {
                var lnks = files.Where(f => string.Equals(Path.GetExtension(f), ".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string skipped in files.Except(lnks))
                    AppendLog($"跳过（非 .lnk 文件）：{Path.GetFileName(skipped)}");
                if (lnks.Count == 0) return;

                AppendLog($"开始处理 {lnks.Count} 个快捷方式…");
                var results = await Task.Run(() =>
                    lnks.Select(f => (File: f, R: ShortcutRestorer.Restore(f, _prefix))).ToList());

                foreach (var (file, r) in results)
                {
                    string name = Path.GetFileName(file);
                    if (r.Ok)
                        AppendLog($"已恢复：{name} → {r.NewPath}" +
                                  (string.IsNullOrEmpty(r.Warning) ? "" : $"（警告：{r.Warning}）"));
                    else
                        AppendLog($"恢复失败：{name} — {r.Error}");
                }
                AppendLog($"本批完成：成功 {results.Count(x => x.R.Ok)}，失败 {results.Count(x => !x.R.Ok)}");
            }
            finally
            {
                _busy = false;
            }
        }

        private void AppendLog(string line)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
            LogBox.ScrollToEnd();
        }
    }
}
