using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using PathwinderMgt.Services;

namespace PathwinderMgt
{
    public partial class App : Application
    {
        // 主窗口标题，用于定位已有实例的窗口句柄
        private const string MainWindowTitle = "Pathwinder 管理器";

        // Win32 API：激活已有窗口到前台
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private const int SW_RESTORE = 9;

        /// <summary>
        /// 将已有实例的主窗口激活到前台（恢复最小化 + 置顶）。
        /// 不弹任何提示框，静默激活后当前进程直接退出。
        /// </summary>
        private static void ActivateExistingInstance()
        {
            IntPtr hwnd = FindWindow(null, MainWindowTitle);
            if (hwnd == IntPtr.Zero) return;

            // 恢复最小化窗口
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);

            // SetForegroundWindow 受系统限制：仅前台线程或最近输入的进程可调用。
            // 通过 AttachThreadInput 附加到前台线程，绕过限制。
            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint curThread = GetCurrentThreadId();
            if (foreThread != curThread && foreThread != 0)
            {
                AttachThreadInput(foreThread, curThread, true);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                AttachThreadInput(foreThread, curThread, false);
            }
            else
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
        }
        // 单实例互斥量：优先 Global\（跨会话，管理员/普通用户互斥），失败降级 Local\（同会话内互斥）
        private static readonly Mutex _mutex = CreateMutex();

        private static Mutex CreateMutex()
        {
            try { return new Mutex(false, "Global\\PathwinderManager_SingleInstance"); }
            catch (UnauthorizedAccessException)
            {
                // 部分环境下 Global\ 命名空间创建权限不足（如某些 UAC 配置），降级到 Local\
                try { return new Mutex(false, "Local\\PathwinderManager_SingleInstance"); }
                catch { return new Mutex(false, "PathwinderManager_SingleInstance"); }
            }
            catch { return new Mutex(false, "PathwinderManager_SingleInstance"); }
        }

        public static StartupResult Bootstrap { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局未处理异常兜底：任何 UI 线程上的意外异常都只提示、不闪退
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            try
            {
                if (!_mutex.WaitOne(0, false))
                {
                    // 已有实例在运行：静默激活其窗口到前台，不弹任何提示框，然后立即退出。
                    // 必须用 Environment.Exit 而非 Shutdown——此时 StartupUri 的主窗口
                    // 尚未创建，Shutdown 无法阻止框架继续加载 MainWindow。
                    try { ActivateExistingInstance(); } catch { }
                    Environment.Exit(0);
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例异常退出留下的废弃互斥量，当前实例已获得所有权，继续启动
            }

            try
            {
                Bootstrap = StartupCoordinator.Initialize();
            }
            catch (Exception ex)
            {
                // 启动失败必须释放互斥量并退出，否则进程会残留在后台持有互斥量，
                // 导致后续启动全部误报"已在运行"
                try { _mutex.ReleaseMutex(); } catch { }
                try
                {
                    new FileLogger(AppPaths.ManagerLogPath, "Startup")
                        .Error("启动初始化致命异常：" + ex);
                }
                catch { }
                MessageBox.Show("管理器启动失败：" + ex.Message +
                                "\n\n详细信息已写入 logs\\manager.log。程序将退出。",
                    "Pathwinder 管理器", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(0);
                return;
            }

            if (Bootstrap.PermissionFatal)
            {
                MessageBox.Show(ErrorMessages.Text(PwCode.NoPermission), "Pathwinder 管理器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                try { _mutex.ReleaseMutex(); } catch { }
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
        }

        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// 以管理员身份重启管理器（不询问）。
        /// 必须先释放单实例互斥量，否则新进程会因互斥量尚未释放而误报"已在运行"。
        /// </summary>
        public static void RestartElevated()
        {
            try
            {
                // 释放单实例互斥量，让新进程能正常获取
                try { _mutex.ReleaseMutex(); }
                catch { /* 未持有或已释放，忽略 */ }

                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法以管理员身份重启：" + ex.Message +
                                "\n\n请关闭管理器后，右键选择「以管理员身份运行」手动重试。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 无权限时引导用户以管理员身份重启管理器。
        /// 返回 true 表示已发起重启（调用方应中止当前操作）；false 表示用户放弃或重启失败。
        /// </summary>
        public static bool AskRestartElevated()
        {
            var choice = MessageBox.Show(
                "目标程序位于受保护目录（如 Program Files），当前权限无法写入 Hookshot 授权文件。\n\n" +
                "是否立即以管理员身份重启管理器？\n（重启后请重新执行该操作）",
                "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return false;
            RestartElevated();
            return true;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                new FileLogger(AppPaths.ManagerLogPath, "Unhandled")
                    .Error("UI 线程未处理异常：" + e.Exception);
            }
            catch { /* 日志失败不再弹窗递归 */ }

            MessageBox.Show("发生未预期的错误，操作已中止（程序不会退出）：\n\n" + e.Exception.Message,
                "Pathwinder 管理器", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                new FileLogger(AppPaths.ManagerLogPath, "Unhandled")
                    .Error("非 UI 线程致命未处理异常（IsTerminating=" + e.IsTerminating + "）：" + e.ExceptionObject);
            }
            catch { }
        }
    }
}
