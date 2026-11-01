using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PathwinderMgt.Models;

namespace PathwinderMgt.Services
{
    // ===================== 错误码（文档第 10 节）=====================

    public enum PwCode
    {
        WorkspaceLocked = 1,    // PW001 工作区目录被占用
        HardLinkFailed = 2,     // PW002 硬链接创建失败
        ShortcutRewrite = 3,    // PW003 快捷方式改写失败
        IniWrite = 4,           // PW004 INI 写入失败
        BackupRestore = 5,      // PW005 备份恢复失败
        NoPermission = 6,       // PW006 无权限
        PeDetectFailed = 7,     // PW007 位数检测失败
        Arm64Unsupported = 8,   // PW008 ARM64 不支持
        RuleInvalid = 9,        // PW009 规则校验失败
        ConfigCorrupted = 10    // PW010 config.json 损坏
    }

    public class PwException : Exception
    {
        public PwCode Code { get; }
        public PwException(PwCode code, string detail) : base(detail) => Code = code;

        public string UserMessage => ErrorMessages.Text(Code, Message);
    }

    public static class ErrorMessages
    {
        public static string Text(PwCode code, string detail = null)
        {
            string baseText = code switch
            {
                PwCode.WorkspaceLocked => "无法删除工作区，请先关闭目标程序",
                PwCode.HardLinkFailed => "创建硬链接失败，请检查磁盘和权限",
                PwCode.ShortcutRewrite => "快捷方式改写失败，原快捷方式已备份",
                PwCode.IniWrite => "规则写入失败，请检查工作区权限",
                PwCode.BackupRestore => "原快捷方式路径不存在，请手动处理",
                PwCode.NoPermission => "无权限：目标程序位于受保护目录，当前用户无法写入 Hookshot 授权文件，请以管理员身份运行管理器后重试",
                PwCode.PeDetectFailed => "无法检测目标程序位数，请手动选择",
                PwCode.Arm64Unsupported => "Hookshot 无 ARM64 版本，不支持该程序",
                PwCode.RuleInvalid => "规则不合法",
                PwCode.ConfigCorrupted => "配置文件损坏，已备份为 config.json.bak",
                _ => "未知错误"
            };
            return string.IsNullOrEmpty(detail) ? baseText : $"{baseText}（{detail}）";
        }
    }

    // ===================== Win32 互操作 =====================

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const long InvalidHandleValue = -1;
        public const uint FileReadAttributes = 0x0080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint FileShareDelete = 0x00000004;
        public const uint OpenExisting = 3;
        public const uint FileFlagBackupSemantics = 0x02000000;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>文件硬链接身份：卷序列号 + 文件索引（文档 5.3.1 断链检测）</summary>
    internal readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        public uint Volume { get; }
        public long Index { get; }

        public FileIdentity(uint volume, long index) { Volume = volume; Index = index; }

        public bool Equals(FileIdentity other) => Volume == other.Volume && Index == other.Index;
        public override bool Equals(object obj) => obj is FileIdentity other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Volume, Index);
    }

    // ===================== PE 位数检测（文档 4.3）=====================

    public static class PeDetector
    {
        public const string X86 = "x86";
        public const string X64 = "x64";
        public const string Arm64 = "arm64";
        public const string Unknown = "unknown";

        /// <summary>读 PE 头 Machine 字段：0x014c=x86，0x8664=x64，0xAA64=arm64；无法判定返回 unknown。</summary>
        public static string Detect(string exePath)
        {
            try
            {
                using var fs = File.OpenRead(exePath);
                using var br = new BinaryReader(fs);
                if (fs.Length < 0x40) return Unknown;
                fs.Position = 0x3C;
                int peOffset = br.ReadInt32();
                if (peOffset <= 0 || peOffset + 6 > fs.Length) return Unknown;
                fs.Position = peOffset + 4; // 跳过 "PE\0\0"
                ushort machine = br.ReadUInt16();
                return machine switch
                {
                    0x014C => X86,
                    0x8664 => X64,
                    0xAA64 => Arm64,
                    _ => Unknown
                };
            }
            catch
            {
                return Unknown;
            }
        }
    }

    // ===================== 硬链接（文档 4.1 / 5.3.1）=====================

    public static class HardLinkHelper
    {
        /// <summary>创建硬链接；失败抛 PW002（payload 与 runtime 天然同盘，不做复制兜底以免绕过断链检测）。</summary>
        public static void Create(string source, string target)
        {
            if (!File.Exists(source))
                throw new PwException(PwCode.HardLinkFailed, $"payload 中缺少源文件：{Path.GetFileName(source)}");
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (Exception ex)
                {
                    throw new PwException(PwCode.HardLinkFailed, $"无法覆盖已存在的文件：{ex.Message}");
                }
            }
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (!NativeMethods.CreateHardLinkW(target, source, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new PwException(PwCode.HardLinkFailed, $"Win32 错误 {err}：{Path.GetFileName(target)}");
            }
        }

        /// <summary>读取文件（卷序列号 + 文件索引）；文件不存在返回 null。</summary>
        internal static FileIdentity? ReadIdentity(string path)
        {
            IntPtr handle = NativeMethods.CreateFileW(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                IntPtr.Zero, NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics, IntPtr.Zero);
            if ((long)handle == NativeMethods.InvalidHandleValue) return null;
            try
            {
                if (!NativeMethods.GetFileInformationByHandle(handle, out var info)) return null;
                long index = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
                return new FileIdentity(info.VolumeSerialNumber, index);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        /// <summary>硬链接是否与真身一致（同一 FileIndex）。</summary>
        public static bool IsLinkedTo(string linkPath, string sourcePath)
        {
            var a = ReadIdentity(linkPath);
            var b = ReadIdentity(sourcePath);
            return a != null && b != null && a.Value.Equals(b.Value);
        }
    }

    // ===================== 快捷方式：自写 IShellLinkW + IPersistFile（文档第 7 节）=====================

    public static class ShortcutManager
    {
        public class ShortcutInfo
        {
            public string TargetPath = "";
            public string Arguments = "";
            public string WorkingDirectory = "";
            public string IconLocation = "";
        }

        /// <summary>
        /// 读取快捷方式（递归解析）。普通快捷方式直接取 TargetPath；
        /// 对 Shell API 无法解析的复合快捷方式（UWP 开始菜单宿主生成的"快捷方式的快捷方式"、
        /// 含虚拟 PIDL 的 MSI/UWP 链接），从 .lnk 二进制 UTF-16 内容中提取真实路径，
        /// 必要时沿 .lnk 链继续递归（最多 4 层）。
        /// </summary>
        public static ShortcutInfo Read(string lnkPath) => ReadResolved(lnkPath, 0);

        private const int MaxResolveDepth = 4;
        private static readonly HashSet<string> ExecutableExts =
            new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".cmd", ".com", ".msc" };

        private static ShortcutInfo ReadResolved(string lnkPath, int depth)
        {
            var info = ReadCom(lnkPath);
            if (!string.IsNullOrWhiteSpace(info.TargetPath) || depth >= MaxResolveDepth)
                return info;

            // Shell API 取不到 TargetPath（复合/虚拟 PIDL）：扫描二进制内嵌的 UTF-16 路径
            var candidates = ScanEmbeddedPaths(lnkPath);

            // 优先沿"快捷方式 → 快捷方式"链递归（可顺带取得真实工作目录/图标/参数）
            foreach (var candidate in candidates)
            {
                if (!candidate.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(candidate, lnkPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(candidate)) continue;
                var inner = ReadResolved(candidate, depth + 1);
                if (string.IsNullOrWhiteSpace(inner.TargetPath)) continue;
                info.TargetPath = inner.TargetPath;
                if (string.IsNullOrWhiteSpace(info.Arguments)) info.Arguments = inner.Arguments;
                if (string.IsNullOrWhiteSpace(info.WorkingDirectory)) info.WorkingDirectory = inner.WorkingDirectory;
                if (string.IsNullOrWhiteSpace(info.IconLocation)) info.IconLocation = inner.IconLocation;
                return info;
            }

            // 否则取内嵌的可执行文件路径
            foreach (var candidate in candidates)
            {
                string ext = Path.GetExtension(candidate);
                if (!ExecutableExts.Contains(ext) || !File.Exists(candidate)) continue;
                info.TargetPath = candidate;
                if (string.IsNullOrWhiteSpace(info.WorkingDirectory))
                    info.WorkingDirectory = Path.GetDirectoryName(candidate);
                return info;
            }

            return info;
        }

        /// <summary>
        /// 从 .lnk 二进制中提取 UTF-16LE 明文盘符路径（按出现顺序去重）。
        /// PIDL 段中的字符串可能落在奇数偏移，必须按奇/偶两种对齐各解码一次，否则会错位成乱码。
        /// </summary>
        private static List<string> ScanEmbeddedPaths(string lnkPath)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                byte[] bytes = File.ReadAllBytes(lnkPath);
                for (int offset = 0; offset <= 1; offset++)
                {
                    if (offset >= bytes.Length) continue;
                    string text = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
                    foreach (Match m in Regex.Matches(text, @"[A-Za-z]:\\[^\u0000-\u001F\*""<>|?]{2,260}"))
                    {
                        string v = m.Value.TrimEnd().TrimEnd(' ', '.');
                        if (v.Length < 4 || seen.Contains(v)) continue;
                        seen.Add(v);
                        results.Add(v);
                    }
                }
            }
            catch { /* 读取失败时返回空列表，由调用方按"无目标"处理 */ }
            return results;
        }

        private static ShortcutInfo ReadCom(string lnkPath)
        {
            var link = CreateLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                TryResolve(link);

                var pathSb = new StringBuilder(4096);
                var argsSb = new StringBuilder(4096);
                var dirSb = new StringBuilder(4096);
                var iconSb = new StringBuilder(4096);
                var ffd = new Win32FindData();

                ((IShellLinkW)link).GetPath(pathSb, pathSb.Capacity, ref ffd, 0);
                ((IShellLinkW)link).GetArguments(argsSb, argsSb.Capacity);
                ((IShellLinkW)link).GetWorkingDirectory(dirSb, dirSb.Capacity);
                ((IShellLinkW)link).GetIconLocation(iconSb, iconSb.Capacity, out _);

                return new ShortcutInfo
                {
                    TargetPath = pathSb.ToString(),
                    Arguments = argsSb.ToString(),
                    WorkingDirectory = dirSb.ToString(),
                    IconLocation = iconSb.ToString()
                };
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        public static void Write(string lnkPath, ShortcutInfo info)
        {
            var link = CreateLink();
            try
            {
                var sl = (IShellLinkW)link;
                sl.SetPath(info.TargetPath);
                sl.SetArguments(info.Arguments ?? "");
                sl.SetWorkingDirectory(info.WorkingDirectory ?? "");
                if (!string.IsNullOrEmpty(info.IconLocation))
                {
                    // IconLocation 形如 "C:\path\app.exe,0"
                    int comma = info.IconLocation.LastIndexOf(',');
                    string iconPath = comma > 0 ? info.IconLocation.Substring(0, comma) : info.IconLocation;
                    int iconIndex = comma > 0 && int.TryParse(info.IconLocation.Substring(comma + 1), out var idx) ? idx : 0;
                    sl.SetIconLocation(iconPath, iconIndex);
                }
                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch (PwException) { throw; }
            catch (Exception ex)
            {
                throw new PwException(PwCode.ShortcutRewrite, ex.Message);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        /// <summary>为 exe 创建快捷方式（拖入 exe 时在桌面生成，文档 5.1.1）</summary>
        public static void CreateFromExe(string lnkPath, string exePath, string arguments = "", string workingDir = "")
        {
            string dir = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Write(lnkPath, new ShortcutInfo
            {
                TargetPath = exePath,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(exePath) : workingDir
            });
        }

        /// <summary>
        /// 判断快捷方式是否已含 Pathwinder 指令：TargetPath 位于 runtime\ 下，文件名为 Hookshot.32/64.exe，
        /// 其父目录名即程序 ID（文档 5.1.1）。
        /// </summary>
        public static bool IsManaged(string lnkPath, string runtimeRoot, out string programId)
        {
            programId = null;
            try
            {
                var info = Read(lnkPath);
                if (string.IsNullOrEmpty(info.TargetPath) || !File.Exists(info.TargetPath)) return false; // 断链先不算受管
                string name = Path.GetFileName(info.TargetPath);
                if (!name.Equals("Hookshot.32.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Hookshot.64.exe", StringComparison.OrdinalIgnoreCase)) return false;

                string targetDir = Path.GetFullPath(Path.GetDirectoryName(info.TargetPath));
                string root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (!targetDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;

                string hash = Path.GetFileName(targetDir);
                if (string.IsNullOrEmpty(hash)) return false;
                programId = hash;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object CreateLink() => new ShellLinkCoclass();

        private static void TryResolve(object link)
        {
            try { ((IShellLinkW)link).Resolve(IntPtr.Zero, SlrNoUi); } catch { }
        }

        private const uint SlrNoUi = 0x00000001;

        // ---- COM 声明 ----

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLinkCoclass { }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        internal struct Win32FindData
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
                         ref Win32FindData pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
        internal interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    // ===================== 快捷方式恢复（独立工具，不涉及主列表与配置）=====================

    /// <summary>
    /// 把受管快捷方式还原为普通快捷方式：解析原程序路径与参数，在桌面生成去前缀的 .lnk 并删除旧文件。
    /// 识别规则：目标指向 runtime\Hookshot（IsManaged），或文件名以当前前缀/旧版 [PW] 前缀开头
    /// （后者覆盖 Hookshot 目标断链的场景）。纯文件操作，不读写 config.json。
    /// </summary>
    public static class ShortcutRestorer
    {
        public sealed class RestoreResult
        {
            public bool Ok { get; init; }
            public string NewPath { get; init; }
            public string Error { get; init; }
            public string Warning { get; init; }
        }

        public static RestoreResult Restore(string lnkPath, string prefix)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
                    return Fail("文件不存在");
                if (!string.Equals(Path.GetExtension(lnkPath), ".lnk", StringComparison.OrdinalIgnoreCase))
                    return Fail("不是快捷方式（.lnk）文件");

                var info = ShortcutManager.Read(lnkPath);
                string fileName = Path.GetFileNameWithoutExtension(lnkPath);
                bool managedByTarget = ShortcutManager.IsManaged(lnkPath, AppPaths.RuntimeRoot, out _);
                if (!managedByTarget && !FileNameHasPrefix(fileName, prefix))
                    return Fail("非受管快捷方式（目标未指向 Hookshot 且文件名无前缀）");

                // 受管参数格式为 "原exe" 原参数；仅文件名带前缀而内容未被改写时，目标本身就是原程序
                string exe = ExtractFirstQuoted(info.Arguments, out string restArgs);
                if (string.IsNullOrEmpty(exe))
                {
                    if (!managedByTarget && !string.IsNullOrEmpty(info.TargetPath))
                    {
                        exe = info.TargetPath;
                        restArgs = "";
                    }
                    else
                        return Fail("无法从快捷方式解析原程序路径");
                }

                // 输出到桌面（不覆盖同名文件，避免误伤无关快捷方式）
                string baseName = StripPrefix(fileName, prefix);
                string newPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), baseName + ".lnk");
                if (File.Exists(newPath))
                    return Fail($"桌面已存在同名快捷方式：{baseName}.lnk");

                ShortcutManager.Write(newPath, new ShortcutManager.ShortcutInfo
                {
                    TargetPath = exe,
                    Arguments = restArgs,
                    WorkingDirectory = info.WorkingDirectory,
                    IconLocation = info.IconLocation
                });

                string warning = null;
                try { File.Delete(lnkPath); }
                catch (Exception ex) { warning = $"旧文件删除失败（可手动删除）：{ex.Message}"; }

                return new RestoreResult { Ok = true, NewPath = newPath, Warning = warning };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static RestoreResult Fail(string error) =>
            new() { Ok = false, Error = error };

        private static bool FileNameHasPrefix(string name, string prefix) =>
            (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.Ordinal)) ||
            name.StartsWith(ConfigManager.LegacyPrefix, StringComparison.Ordinal);

        private static string StripPrefix(string name, string prefix)
        {
            if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.Ordinal))
                name = name.Substring(prefix.Length);
            else if (name.StartsWith(ConfigManager.LegacyPrefix, StringComparison.Ordinal))
                name = name.Substring(ConfigManager.LegacyPrefix.Length);
            return name.Trim();
        }

        /// <summary>提取参数首段引号内容（原 exe 路径），其余作为 rest 返回；无引号段返回空串。</summary>
        private static string ExtractFirstQuoted(string arguments, out string rest)
        {
            rest = "";
            if (string.IsNullOrWhiteSpace(arguments)) return "";
            arguments = arguments.Trim();
            if (arguments[0] != '"') return "";
            int end = arguments.IndexOf('"', 1);
            if (end < 0) return "";
            rest = arguments.Substring(end + 1).Trim();
            return arguments.Substring(1, end - 1);
        }
    }

    // ===================== 规则校验（文档 5.2.4 / 1.3）=====================

    public static class RuleValidator
    {
        public static string Validate(Rule r)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return "规则名不能为空";
            if (string.IsNullOrWhiteSpace(r.OriginDirectory)) return $"规则「{r.Name}」的 OriginDirectory 不能为空";
            if (string.IsNullOrWhiteSpace(r.TargetDirectory)) return $"规则「{r.Name}」的 TargetDirectory 不能为空";
            if (!Path.IsPathRooted(r.OriginDirectory)) return $"规则「{r.Name}」的 OriginDirectory 必须是绝对路径";
            if (!Path.IsPathRooted(r.TargetDirectory)) return $"规则「{r.Name}」的 TargetDirectory 必须是绝对路径";
            if (r.OriginDirectory.TrimEnd('\\').Equals(Path.GetPathRoot(r.OriginDirectory)?.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
                return $"规则「{r.Name}」的 OriginDirectory 不能为盘符根目录";
            if (r.TargetDirectory.TrimEnd('\\').Equals(Path.GetPathRoot(r.TargetDirectory)?.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
                return $"规则「{r.Name}」的 TargetDirectory 不能为盘符根目录";
            if (r.FilePatterns != null)
            {
                foreach (var p in r.FilePatterns)
                {
                    if (p != null && p.Contains('\\'))
                        return $"规则「{r.Name}」的 FilePattern「{p}」不能含反斜杠";
                }
            }
            return null;
        }

        /// <summary>校验同一 INI 内的规则集：名称唯一(大小写敏感)、Origin 不重复、Target 互不嵌套、Target≠任何 Origin。</summary>
        public static string ValidateList(List<Rule> rules)
        {
            var names = new HashSet<string>(StringComparer.Ordinal); // 规则名大小写敏感
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new List<string>();

            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string err = Validate(r);
                if (err != null) return err;

                if (!names.Add(r.Name.Trim())) return $"规则名重复：{r.Name}（规则名大小写敏感）";
                if (!origins.Add(r.OriginDirectory.TrimEnd('\\')))
                    return $"规则「{r.Name}」的 OriginDirectory 与其他规则重复";

                for (int j = 0; j < targets.Count; j++)
                {
                    if (IsNested(r.TargetDirectory, targets[j]))
                        return $"规则「{r.Name}」的 TargetDirectory 与其他规则的 TargetDirectory 存在祖先/后代关系，必须完全不相关";
                }
                foreach (var o in origins)
                {
                    if (r.TargetDirectory.TrimEnd('\\').Equals(o, StringComparison.OrdinalIgnoreCase))
                        return $"规则「{r.Name}」的 TargetDirectory 不能等于任何规则的 OriginDirectory";
                }
                targets.Add(r.TargetDirectory.TrimEnd('\\'));
            }
            return null;
        }

        /// <summary>b 是否为 a 本身或 a 的祖先目录（大小写不敏感）。</summary>
        public static bool IsNested(string a, string b)
        {
            a = a.TrimEnd('\\');
            b = b.TrimEnd('\\');
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
            return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ===================== Pathwinder.ini（文档 5.2.3）=====================

    public static class IniWriter
    {
        public static void Write(string iniPath, List<Rule> rules)
        {
            try
            {
                var sb = new StringBuilder();
                if (rules != null)
                {
                    foreach (var r in rules)
                    {
                        sb.AppendLine($"[FilesystemRule:{Sanitize(r.Name)}]");
                        sb.AppendLine($"OriginDirectory = {r.OriginDirectory.Trim()}");
                        sb.AppendLine($"TargetDirectory = {r.TargetDirectory.Trim()}");
                        if (r.FilePatterns != null)
                        {
                            foreach (var p in r.FilePatterns)
                            {
                                if (!string.IsNullOrWhiteSpace(p))
                                    sb.AppendLine($"FilePattern = {p.Trim()}");
                            }
                        }
                        sb.AppendLine();
                    }
                }
                File.WriteAllText(iniPath, sb.ToString());
            }
            catch (Exception ex)
            {
                throw new PwException(PwCode.IniWrite, ex.Message);
            }
        }

        private static readonly char[] InvalidSectionChars = { '[', ']', '\r', '\n' };

        /// <summary>替换 INI section 名中的非法字符（[ ] 回车 换行），单次遍历避免多次字符串分配。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(InvalidSectionChars, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    // ===================== 日志（文档第 9 节）=====================

    /// <summary>单文件超过 5MB 轮转为 .1/.2/.3；格式 [时间] [级别] [模块] 消息。</summary>
    public class FileLogger
    {
        private readonly string _path;
        private readonly string _module;
        private readonly object _lock = new();
        private const long MaxBytes = 5 * 1024 * 1024;
        /// <summary>每写多少条日志检查一次文件大小（避免每条都 new FileInfo 查磁盘）。</summary>
        private const int RotateCheckInterval = 50;
        private int _writeCount;

        public FileLogger(string path, string module)
        {
            _path = path;
            _module = module;
            // 目录在构造时确保一次，写入时不再重复调用 Directory.CreateDirectory
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { /* 目录创建失败时写入会被 catch 兜底，不影响主流程 */ }
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Warn(string msg) => Write("WARN", msg);
        public void Error(string msg) => Write("ERROR", msg);

        private void Write(string level, string msg)
        {
            lock (_lock)
            {
                try
                {
                    // 每写 RotateCheckInterval 条才检查一次文件大小，减少磁盘元数据查询
                    if (++_writeCount >= RotateCheckInterval)
                    {
                        _writeCount = 0;
                        if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) Rotate();
                    }
                    File.AppendAllText(_path,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{_module}] {msg}{Environment.NewLine}");
                }
                catch { /* 日志失败不影响主流程 */ }
            }
        }

        private void Rotate()
        {
            string p1 = _path + ".1", p2 = _path + ".2", p3 = _path + ".3";
            try
            {
                if (File.Exists(p3)) File.Delete(p3);
                if (File.Exists(p2)) File.Move(p2, p3);
                if (File.Exists(p1)) File.Move(p1, p2);
                File.Move(_path, p1);
            }
            catch { }
        }
    }
}
