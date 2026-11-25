using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PathwinderMgt.Services;

namespace PathwinderMgt
{
    public partial class App : Application
    {
        // 主窗口标题，用于定位已有实例的窗口句柄
        /// <summary>主窗口标题候选（界面语言可为中文或英文），用于定位已有实例窗口。</summary>
        private static readonly string[] MainWindowTitles = { "Pathwinder 管理器", "Pathwinder Manager" };

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
            IntPtr hwnd = IntPtr.Zero;
            foreach (string title in MainWindowTitles)
            {
                hwnd = FindWindow(null, title);
                if (hwnd != IntPtr.Zero) break;
            }
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
                    Loc.T("L_App_Name"), MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(0);
                return;
            }

            if (Bootstrap.PermissionFatal)
            {
                MessageBox.Show(ErrorMessages.Text(PwCode.NoPermission), Loc.T("L_App_Name"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                try { _mutex.ReleaseMutex(); } catch { }
                Shutdown(0);
                return;
            }

            // 应用已保存的主题：必须在主窗口创建前生效（StartupUri 在 OnStartup 之后加载），
            // 否则窗口会先以浅色渲染再跳到深色
            ThemeManager.Apply(Bootstrap?.Config?.Settings?.Theme);

            // 应用已保存的界面语言：同样要在主窗口创建前生效
            Loc.Apply(Bootstrap?.Config?.Settings?.Language);

            // 之后打开的每个窗口（含对话框）加载时同步标题栏，避免深色内容配白色标题栏
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => ThemeManager.ApplyTitleBar(sender as Window)));

            base.OnStartup(e);
        }

        private const string VcRedistX64Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        private const string VcRedistX86Url = "https://aka.ms/vs/17/release/vc_redist.x86.exe";

        /// <summary>
        /// 检测 Microsoft Visual C++ 2015-2022 运行库（x64 / x86）。缺失时提示用户，
        /// 点击「确定」打开对应位数的官方下载地址。缺少运行库会导致 Hookshot 注入失败。
        /// </summary>
        public static void CheckVcRuntime()
        {
            var cfg = Bootstrap?.Config;
            if (cfg?.Settings?.SuppressVcRuntimePrompt == true) return;

            var missing = new List<string>();
            if (!IsVcRuntimeInstalled("x64")) missing.Add("x64");
            if (!IsVcRuntimeInstalled("x86")) missing.Add("x86");
            if (missing.Count == 0) return;

            string archText = string.Join(Loc.IsEnglish ? " and " : " 和 ", missing);
            var choice = MessageBox.Show(
                Loc.T("L_App_NeedRuntimeMsg", archText),
                Loc.T("L_App_NeedRuntimeTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (choice == MessageBoxResult.No)
            {
                if (cfg != null)
                {
                    cfg.Settings.SuppressVcRuntimePrompt = true;
                    try { ConfigManager.Save(cfg); } catch { /* 保存失败时下次仍提示 */ }
                }
                return;
            }
            if (choice != MessageBoxResult.Yes) return;

            foreach (string arch in missing)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = arch == "x64" ? VcRedistX64Url : VcRedistX86Url,
                        UseShellExecute = true
                    });
                }
                catch { /* 浏览器启动失败不影响管理器主流程 */ }
            }
        }

        private static bool IsVcRuntimeInstalled(string arch)
        {
            foreach (string root in new[]
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch
            })
            {
                try
                {
                    object value = Registry.GetValue(root, "Installed", null);
                    if (value is int i && i == 1) return true;
                }
                catch { /* 注册表不可读时按未安装处理，交由提示引导用户 */ }
            }
            return false;
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

                // UAC 关闭时 runas 不会真正提权：ShellExecute 会沿用当前（受限）令牌。
                // 此时通知仍然存活的管理员中转进程，由它用管理员令牌启动新实例。
                if (!IsUacEnabled())
                {
                    try
                    {
                        using var request = EventWaitHandle.OpenExisting(ElevateEventName);
                        request.Set();
                        Current.Shutdown();
                        return;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // 没有新进程接手，重新持有互斥量，避免出现两个管理器窗口
                        TryReacquireMutex();
                        MessageBox.Show(Loc.T("L_App_UacOff"),
                            Loc.T("L_Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

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
                // 提权被取消或启动失败：当前实例仍在运行，需重新持有互斥量
                TryReacquireMutex();
                MessageBox.Show(Loc.T("L_App_ElevateFailed", ex.Message),
                    Loc.T("L_Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>启动新实例失败后重新持有单实例互斥量，保持单实例语义。</summary>
        private static void TryReacquireMutex()
        {
            try { _mutex.WaitOne(0, false); } catch { }
        }

        /// <summary>UAC 是否启用（EnableLUA=1 视为启用）。</summary>
        private static bool IsUacEnabled()
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var value = key?.GetValue("EnableLUA");
            return value is int i ? i != 0 : true;
        }

        /// <summary>
        /// 以普通用户权限重启管理器。
        /// Windows 不支持对运行中进程直接降权，所以先用 SAFER 生成普通用户令牌，
        /// 再用该令牌另起一个进程，最后退出当前（管理员）进程。
        /// </summary>
        public static void RestartAsStandardUser()
        {
            try
            {
                try { _mutex.ReleaseMutex(); }
                catch { /* 未持有或已释放，忽略 */ }

                IntPtr childHandle = IntPtr.Zero;
                if (!SaferCreateLevel(SAFER_SCOPEID_USER, SAFER_LEVELID_NORMALUSER, 0, out var level, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                try
                {
                    if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out var saferToken, 0, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    try
                    {
                        // SAFER 只剥离管理员组，完整性级别仍是 High；复制后降到 Medium，
                        // 才等价于“非提权的管理员账户”（右键「以普通权限运行」的效果）。
                        if (!DuplicateTokenEx(saferToken, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1, out var token))
                            throw new Win32Exception(Marshal.GetLastWin32Error());

                        try
                        {
                            LowerToMediumIntegrity(token);

                            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                            if (!CreateProcessWithTokenW(token, LOGON_WITH_PROFILE, Environment.ProcessPath, null,
                                    CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, AppPaths.AppDir, ref si, out var pi))
                                throw new Win32Exception(Marshal.GetLastWin32Error());

                            childHandle = pi.hProcess;
                            CloseHandle(pi.hThread);
                        }
                        finally
                        {
                            CloseHandle(token);
                        }
                    }
                    finally
                    {
                        CloseHandle(saferToken);
                    }
                }
                finally
                {
                    SaferCloseLevel(level);
                }

                // UAC 关闭时，普通权限实例无法自行提权，需要本进程作为提权中转继续存活
                if (!IsUacEnabled() && childHandle != IntPtr.Zero)
                {
                    StartElevationBroker(childHandle);
                    return;
                }

                if (childHandle != IntPtr.Zero) CloseHandle(childHandle);
                Current.Shutdown();
            }
            catch (Exception ex)
            {
                TryReacquireMutex();
                MessageBox.Show(Loc.T("L_App_StandardFailed", ex.Message),
                    Loc.T("L_Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private const uint SAFER_SCOPEID_USER = 1;
        private const string ElevateEventName = @"Local\PathwinderManager.ElevateRequest";

        /// <summary>
        /// 提权中转：隐藏当前窗口并保留管理员进程，等待普通权限实例发出提权请求。
        /// 收到请求后用本进程的管理员令牌启动新实例；普通权限实例退出时自动结束。
        /// 仅用于系统关闭 UAC（EnableLUA=0）的情况。
        /// </summary>
        private static void StartElevationBroker(IntPtr childProcessHandle)
        {
            var request = new EventWaitHandle(false, EventResetMode.AutoReset, ElevateEventName);

            foreach (Window window in Current.Windows)
                window.Hide();

            var thread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        if (request.WaitOne(300))
                        {
                            Current.Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo
                                    {
                                        FileName = Environment.ProcessPath,
                                        UseShellExecute = false,
                                        WorkingDirectory = AppPaths.AppDir
                                    });
                                }
                                finally
                                {
                                    Current.Shutdown();
                                }
                            });
                            return;
                        }

                        // 普通权限实例已退出：中转任务结束
                        if (WaitForSingleObject(childProcessHandle, 0) == 0)
                        {
                            CloseHandle(childProcessHandle);
                            Current.Dispatcher.Invoke(() => Current.Shutdown());
                            return;
                        }
                    }
                }
                catch
                {
                    Current.Dispatcher.Invoke(() => Current.Shutdown());
                }
            })
            { IsBackground = true, Name = "ElevationBroker" };

            thread.Start();
        }

        private const uint SAFER_LEVELID_NORMALUSER = 0x20000;
        private const uint LOGON_WITH_PROFILE = 0x00000001;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const int TokenIntegrityLevel = 25;
        private const uint SE_GROUP_INTEGRITY = 0x00000020;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }

        /// <summary>把令牌完整性级别降为 Medium（S-1-16-8192）。</summary>
        private static void LowerToMediumIntegrity(IntPtr token)
        {
            if (!ConvertStringSidToSid("S-1-16-8192", out var sid))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var label = new TOKEN_MANDATORY_LABEL
                {
                    Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = SE_GROUP_INTEGRITY }
                };

                int structSize = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>();
                int sidLength = GetLengthSid(sid);
                IntPtr buffer = Marshal.AllocHGlobal(structSize + sidLength);
                try
                {
                    Marshal.StructureToPtr(label, buffer, false);
                    CopyMemory(IntPtr.Add(buffer, structSize), sid, (IntPtr)sidLength);
                    if (!SetTokenInformation(token, TokenIntegrityLevel, buffer, structSize + sidLength))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                LocalFree(sid);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags,
            out IntPtr levelHandle, IntPtr reserved);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SaferComputeTokenFromLevel(IntPtr levelHandle, IntPtr inToken,
            out IntPtr outToken, uint flags, IntPtr reserved);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SaferCloseLevel(IntPtr levelHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
            IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr token, int tokenInformationClass,
            IntPtr tokenInformation, int tokenInformationLength);

        [DllImport("advapi32.dll")]
        private static extern int GetLengthSid(IntPtr sid);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, IntPtr length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string applicationName,
            string commandLine, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        /// <summary>
        /// 无权限时引导用户以管理员身份重启管理器。
        /// 返回 true 表示已发起重启（调用方应中止当前操作）；false 表示用户放弃或重启失败。
        /// </summary>
        public static bool AskRestartElevated()
        {
            var choice = MessageBox.Show(
                Loc.T("L_App_NeedAdminMsg"),
                Loc.T("L_App_NeedAdminTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
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

            MessageBox.Show(Loc.T("L_App_Unexpected", e.Exception.Message),
                Loc.T("L_App_Name"), MessageBoxButton.OK, MessageBoxImage.Error);
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
