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
            AppendLog(Loc.T("L_Restore_DragHint"));
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
            if (_busy) { AppendLog(Loc.T("L_Restore_Busy")); return; }
            _busy = true;
            try
            {
                var lnks = files.Where(f => string.Equals(Path.GetExtension(f), ".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string skipped in files.Except(lnks))
                    AppendLog(Loc.T("L_Restore_SkipNonLnk", Path.GetFileName(skipped)));
                if (lnks.Count == 0) return;

                AppendLog(Loc.T("L_Restore_Start", lnks.Count));
                var results = await Task.Run(() =>
                    lnks.Select(f => (File: f, R: ShortcutRestorer.Restore(f, _prefix))).ToList());

                foreach (var (file, r) in results)
                {
                    string name = Path.GetFileName(file);
                    if (r.Ok)
                        AppendLog(Loc.T("L_Restore_Ok", name, r.NewPath) +
                                  (string.IsNullOrEmpty(r.Warning) ? "" : Loc.T("L_Restore_Warn", r.Warning)));
                    else
                        AppendLog(Loc.T("L_Restore_Fail", name, r.Error));
                }
                AppendLog(Loc.T("L_Restore_Done", results.Count(x => x.R.Ok), results.Count(x => !x.R.Ok)));
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
