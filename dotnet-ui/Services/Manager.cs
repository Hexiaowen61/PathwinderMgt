using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using PathwinderMgt.Models;

namespace PathwinderMgt.Services
{
    /// <summary>所有目录均以管理器 exe 所在目录为根推导（文档 3.3 / 第 6 节：路径不写进 config.json）。</summary>
    public static class AppPaths
    {
        public static string AppDir { get; } =
            Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);

        public static string ConfigPath => Path.Combine(AppDir, "config.json");
        public static string PayloadDir => Path.Combine(AppDir, "payload");
        public static string RuntimeRoot => Path.Combine(AppDir, "runtime");
        public static string BackupRoot => Path.Combine(AppDir, "backups");
        public static string LogDir => Path.Combine(AppDir, "logs");
        public static string ManagerLogPath => Path.Combine(LogDir, "manager.log");
        public static string CleanupLogPath => Path.Combine(LogDir, "cleanup.log");

        public static string WorkDir(string programId) => Path.Combine(RuntimeRoot, programId);
        public static string BackupFile(string programId) => Path.Combine(BackupRoot, programId + ".lnk");

        public static void EnsureDirs()
        {
            // 单个目录创建失败不中断启动（如某子目录被占用），其余仍尽力创建
            TryCreateDir(PayloadDir);
            TryCreateDir(RuntimeRoot);
            TryCreateDir(BackupRoot);
            TryCreateDir(LogDir);
        }

        private static void TryCreateDir(string dir)
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* 启动日志尚未就绪，失败由后续实际写操作报错 */ }
        }

        /// <summary>管理器目录是否可写（文档 12.6：装在受保护目录时启动提示 PW006）。</summary>
        public static bool IsWritable()
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                string probe = Path.Combine(AppDir, ".write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    // ===================== config.json：原子写 + 损坏备份 + 旧格式迁移 =====================

    public static class ConfigManager
    {
        /// <summary>旧版 [PW] 前缀常量，用于剥离与启动迁移兼容。</summary>
        public const string LegacyPrefix = "[PW] ";

        public static AppConfig Load(out List<string> warnings)
        {
            warnings = new List<string>();
            if (!File.Exists(AppPaths.ConfigPath))
            {
                var fresh = new AppConfig();
                Normalize(fresh, warnings);
                return fresh;
            }

            string json;
            try { json = File.ReadAllText(AppPaths.ConfigPath); }
            catch (Exception ex)
            {
                warnings.Add($"读取 config.json 失败：{ex.Message}");
                var fresh = new AppConfig();
                Normalize(fresh, warnings);
                return fresh;
            }

            bool legacy;
            AppConfig cfg;
            try
            {
                using var doc = JsonDocument.Parse(json);
                legacy = doc.RootElement.TryGetProperty("rules", out _);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts.Default) ?? new AppConfig();
                if (legacy) MigrateLegacy(cfg, doc.RootElement, warnings);
            }
            catch (Exception ex)
            {
                // PW010：损坏文件备份为 config.json.bak，从头开始
                try { File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".bak", true); } catch { }
                warnings.Add($"{ErrorMessages.Text(PwCode.ConfigCorrupted)}（{ex.Message}）");
                var fresh = new AppConfig();
                Normalize(fresh, warnings);
                return fresh;
            }

            Normalize(cfg, warnings);
            return cfg;
        }

        /// <summary>旧版（rules + selectedRuleId + prog_NNN）→ 新版（globalRules + useGlobalRules + SHA256 ID）。</summary>
        private static void MigrateLegacy(AppConfig cfg, JsonElement root, List<string> warnings)
        {
            cfg.GlobalRules ??= new List<Rule>();
            if (root.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in rulesEl.EnumerateArray())
                {
                    var r = el.Deserialize<Rule>(JsonOpts.Default);
                    if (r == null) continue;
                    r.FilePatterns ??= new List<string>();
                    if (!string.IsNullOrWhiteSpace(r.Name) &&
                        (!string.IsNullOrWhiteSpace(r.OriginDirectory) || !string.IsNullOrWhiteSpace(r.TargetDirectory)))
                    {
                        cfg.GlobalRules.Add(r);
                    }
                }
            }
            foreach (var p in cfg.Programs)
            {
                if (!string.IsNullOrWhiteSpace(p.OriginalTarget)) p.Id = ProgramEntry.ComputeId(p.OriginalTarget);
            }
            try { File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".legacy.bak", true); } catch { }
            warnings.Add("检测到旧版配置，已自动迁移（原文件备份为 config.json.legacy.bak）");
        }

        private static void Normalize(AppConfig cfg, List<string> warnings = null)
        {
            cfg.GlobalRules ??= new List<Rule>();
            cfg.Programs ??= new List<ProgramEntry>();
            cfg.PendingCleanup ??= new List<string>();
            cfg.Settings ??= new Settings();
            cfg.Settings.ShortcutPrefix = ValidatePrefix(cfg.Settings.ShortcutPrefix);
            foreach (var p in cfg.Programs) p.OverrideRules ??= new List<Rule>();
            foreach (var r in cfg.GlobalRules.Concat(cfg.Programs.SelectMany(p => p.OverrideRules)))
                r.FilePatterns ??= new List<string>();

            // 同一目标（同 ID）被重复添加时只保留最早的一条：多条记录共用同一个工作区，
            // 后添加的那条会用它的默认规则覆盖先前的 Pathwinder.ini（表现为“添加的路径没进 ini”）。
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            for (int i = cfg.Programs.Count - 1; i >= 0; i--)
            {
                if (!seenIds.Add(cfg.Programs[i].Id))
                {
                    duplicates.Add(cfg.Programs[i].DisplayName);
                    cfg.Programs.RemoveAt(i);
                }
            }
            if (duplicates.Count > 0)
                warnings?.Add($"检测到重复添加的程序，已合并为最早的一条：" +
                              $"{string.Join("、", duplicates)}（共 {duplicates.Count} 条重复记录），" +
                              "其工作区规则将按保留项重新写入。");

            EnsureBuiltInRule(cfg.GlobalRules);
            // 全局规则仅存于配置（模板）：程序没有规则时自动带入一条默认规则
            foreach (var p in cfg.Programs)
                if (p.OverrideRules.Count == 0)
                    p.OverrideRules = CreateDefaultOverrideRules(cfg, p.DisplayName, p.Id);
        }

        /// <summary>校验快捷方式前缀：剔除文件系统非法字符与控制字符，超长截断，空/全空白回退默认值。</summary>
        public static string ValidatePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return Settings.DefaultShortcutPrefix;
            var invalid = Path.GetInvalidFileNameChars();
            string cleaned = new string(prefix.Where(c => c >= 0x20 && Array.IndexOf(invalid, c) < 0).ToArray());
            if (cleaned.Length > Settings.MaxShortcutPrefixLength)
                cleaned = cleaned.Substring(0, Settings.MaxShortcutPrefixLength);
            return cleaned.Length == 0 ? Settings.DefaultShortcutPrefix : cleaned;
        }

        /// <summary>内置全局模板规则（规则名 Users，对应源路径 C:\Users）：用户主目录上级（C:\Users）→ 管理器目录\AppData（仅存于配置，无 UI 入口）。</summary>
        internal static Rule CreateBuiltInRule() => new()
        {
            Name = "Users",
            BuiltIn = true,
            OriginDirectory = DefaultOriginDirectory(),
            TargetDirectory = Path.Combine(AppPaths.AppDir, "AppData")
        };

        /// <summary>
        /// 全局默认源路径：用户主目录的上一级（通常即 C:\Users，按系统账户动态解析，不写死用户名）。
        /// 用户主目录解析失败或为盘符根时回退字面量 C:\Users。
        /// </summary>
        internal static string DefaultOriginDirectory()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string parent = Path.GetDirectoryName(profile?.TrimEnd('\\') ?? "");
            return string.IsNullOrEmpty(parent) || parent.Length <= 3 ? @"C:\Users" : parent;
        }

        private static void EnsureBuiltInRule(List<Rule> rules)
        {
            var template = CreateBuiltInRule();

            // 已有内置规则：旧版本源目录为 <用户主目录>\AppData，自动迁移为新的全局默认源路径（C:\Users）
            var builtIn = rules.Find(r => r.BuiltIn);
            if (builtIn != null)
            {
                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData");
                if (string.Equals(builtIn.OriginDirectory?.TrimEnd('\\'), legacy, StringComparison.OrdinalIgnoreCase))
                    builtIn.OriginDirectory = template.OriginDirectory;
                // 旧默认规则名 AppData → Users（与源路径 C:\Users 对应）；已存在同名 Users 规则时保持原名
                if (string.Equals(builtIn.Name, "AppData", StringComparison.Ordinal) &&
                    !rules.Any(r => r != builtIn && string.Equals(r.Name, "Users", StringComparison.Ordinal)))
                    builtIn.Name = "Users";
                return;
            }

            // 用户已有同源规则（如手工迁移过的 AppData 规则）→ 直接标记为内置，避免 Origin 重复
            string origin = template.OriginDirectory.TrimEnd('\\');
            var existing = rules.Find(r => string.Equals(
                r.OriginDirectory?.TrimEnd('\\'), origin, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.BuiltIn = true;
                return;
            }

            // 规则名去重
            int n = 1;
            while (rules.Any(r => string.Equals(r.Name, template.Name, StringComparison.Ordinal)))
                template.Name = "Users" + (++n);
            rules.Insert(0, template);
        }

        /// <summary>
        /// 程序的默认规则：复制内置全局模板（AppData 重定向），目标目录 = 管理器目录\data\&lt;程序名&gt;
        /// （部署在 D:\APP\PathwinderMgt 时即 D:\APP\PathwinderMgt\data\&lt;程序名&gt;）；
        /// 目录不可创建（D 盘不存在或不可写等）时降级到 C:\PathwinderMgt\data\&lt;程序名&gt;。
        /// </summary>
        internal static List<Rule> CreateDefaultOverrideRules(AppConfig cfg, string displayName, string id)
        {
            var template = cfg.GlobalRules.FirstOrDefault(r => r.BuiltIn) ?? CreateBuiltInRule();
            var rule = template.Clone();
            rule.BuiltIn = false; // 程序私有规则，可编辑
            rule.TargetDirectory = DefaultTargetDir(displayName, id);
            return new List<Rule> { rule };
        }

        private static string DefaultTargetDir(string displayName, string id)
        {
            string safe = string.Concat((displayName ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
            if (safe.Length == 0) safe = id;
            try
            {
                string primary = Path.Combine(AppPaths.AppDir, "data", safe);
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch
            {
                return Path.Combine(@"C:\PathwinderMgt\data", safe);
            }
        }

        /// <summary>临时文件 + 原子替换（文档第 7 节并发保护）。</summary>
        public static void Save(AppConfig cfg)
        {
            Normalize(cfg);
            string json = JsonSerializer.Serialize(cfg, JsonOpts.Default);
            string tmp = AppPaths.ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            try
            {
                File.Move(tmp, AppPaths.ConfigPath, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 目标文件只读 / 被占用（杀毒扫描）/ 权限异常：清除只读属性后重试，
                // 仍失败则退化为直接覆盖写（非原子，但保证配置不丢失）
                try { File.SetAttributes(AppPaths.ConfigPath, FileAttributes.Normal); } catch { }
                try
                {
                    File.Move(tmp, AppPaths.ConfigPath, true);
                }
                catch
                {
                    File.WriteAllText(AppPaths.ConfigPath, json);
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
    }

    // ===================== payload（文档 3.2 / 3.3 / 5.4）=====================

    public static class PayloadManager
    {
        public static readonly string[] AllBinaries =
        {
            "Hookshot.32.exe", "Hookshot.32.dll", "Pathwinder.HookModule.32.dll",
            "Hookshot.64.exe", "Hookshot.64.dll", "Pathwinder.HookModule.64.dll"
        };

        /// <summary>
        /// 工作区必须同时硬链接 32/64 全部 6 个二进制：主程序与子进程位数可能不同
        /// （如 64 位 QQ 拉起 32 位 QQPYUserCenter），Hookshot 注入异位数子进程时
        /// 需要同目录下对应位数的 Hookshot.XX.exe，缺失会报
        /// “Error creating a new 32-bit Hookshot executable process”。
        /// 官方建议两个版本同目录放置，Hookshot 自动选择正确版本。
        /// </summary>
        public static IEnumerable<string> WorkspaceBinaries() => AllBinaries;

        public static List<string> MissingBinaries() =>
            AllBinaries.Where(f => !File.Exists(Path.Combine(AppPaths.PayloadDir, f))).ToList();

        // ---------------- 缺失自动修复（从 resources 的 zip 或解压目录恢复）----------------

        /// <summary>payload 缺失的文件 → 二进制名在压缩包/版本目录内的子目录（x64 / Win32）。</summary>
        private static readonly Dictionary<string, string> BinarySourceDir = new()
        {
            ["Hookshot.64.exe"] = "x64",
            ["Hookshot.64.dll"] = "x64",
            ["Hookshot.32.exe"] = "Win32",
            ["Hookshot.32.dll"] = "Win32",
            ["Pathwinder.HookModule.64.dll"] = "x64",
            ["Pathwinder.HookModule.32.dll"] = "Win32"
        };

        /// <summary>payload 缺失时自动从 resources 修复（zip 优先，解压目录兜底）。返回成功恢复的文件名列表。</summary>
        public static List<string> EnsurePayload()
        {
            var repaired = new List<string>();
            foreach (string baseDir in ResourceRoots())
            {
                // 只有全部补齐才算完成；部分成功（如某个 zip 条目格式异常）时继续尝试下一个来源
                if (MissingBinaries().Count == 0) break;
                try
                {
                    foreach (var f in TryRepairFrom(baseDir, MissingBinaries()))
                        if (!repaired.Contains(f)) repaired.Add(f);
                }
                catch { /* 单一来源失败时尝试下一个 */ }
            }
            return repaired;
        }

        private static IEnumerable<string> ResourceRoots()
        {
            yield return Path.Combine(AppPaths.AppDir, "resources");
            yield return Path.GetFullPath(Path.Combine(AppPaths.AppDir, "..", "..", "..", "resources")); // 开发态
        }

        private static List<string> TryRepairFrom(string baseDir, List<string> missing)
        {
            var repaired = new List<string>();
            Directory.CreateDirectory(AppPaths.PayloadDir);

            // 来源 1：Hookshot-*.zip / Pathwinder-*.zip（发布形态最常见）
            string hsZip = Directory.GetFiles(baseDir, "Hookshot-*.zip").FirstOrDefault();
            string pwZip = Directory.GetFiles(baseDir, "Pathwinder-*.zip").FirstOrDefault();
            if (hsZip != null) repaired.AddRange(ExtractFromZip(hsZip, missing));
            if (pwZip != null) repaired.AddRange(ExtractFromZip(pwZip, missing));

            // 来源 2：解压后的版本目录（Hookshot-vX/ x64|Win32/ 文件）
            if (repaired.Count < missing.Count)
            {
                foreach (var dir in Directory.GetDirectories(baseDir, "*-*"))
                {
                    string prefix = Path.GetFileName(dir).Split('-')[0]; // Hookshot / Pathwinder
                    foreach (var file in missing.Except(repaired).Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        string src = Path.Combine(dir, BinarySourceDir[file], file);
                        if (!File.Exists(src)) continue;
                        File.Copy(src, Path.Combine(AppPaths.PayloadDir, file), overwrite: true);
                        repaired.Add(file);
                    }
                }
            }

            if (repaired.Count > 0) WriteVersion(baseDir);
            return repaired;
        }

        /// <summary>从单个 zip 提取本包负责的缺失文件。entry 形如 Hookshot-v2.0.0/x64/Hookshot.64.exe。</summary>
        private static IEnumerable<string> ExtractFromZip(string zipPath, List<string> missing)
        {
            string pkgPrefix = Path.GetFileNameWithoutExtension(zipPath).Split('-')[0]; // Hookshot / Pathwinder
            var targets = missing.Where(f => f.StartsWith(pkgPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0) yield break;

            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                string raw = entry.FullName;
                if (raw.EndsWith("/", StringComparison.Ordinal) || raw.EndsWith("\\", StringComparison.Ordinal))
                    continue;
                string name = Path.GetFileName(raw);
                if (!targets.Contains(name)) continue;

                // 条目分隔符可能是 '/'（官方包）也可能是 '\'（手工重打包的 zip，如资源管理器压缩），
                // 统一替换后再判断目录层级，否则整包会被误判为层级不匹配而全部跳过。
                var segs = raw.Replace('\\', '/').Split('/');
                if (segs.Length < 3 || !string.Equals(segs[^2], BinarySourceDir[name], StringComparison.OrdinalIgnoreCase))
                    continue; // 目录层级不匹配（如 SDK 头文件同名场景）

                entry.ExtractToFile(Path.Combine(AppPaths.PayloadDir, name), overwrite: true);
                yield return name;
            }
        }

        /// <summary>按 resources 实际版本（zip 文件名或目录名）重写 payload\version.json。</summary>
        internal static bool WriteVersion(string baseDir)
        {
            try
            {
                string hs = Directory.GetFiles(baseDir, "Hookshot-*.zip").Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
                            ?? Directory.GetDirectories(baseDir, "Hookshot-*").Select(Path.GetFileName).FirstOrDefault();
                string pw = Directory.GetFiles(baseDir, "Pathwinder-*.zip").Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
                            ?? Directory.GetDirectories(baseDir, "Pathwinder-*").Select(Path.GetFileName).FirstOrDefault();
                if (hs == null || pw == null) return false;
                File.WriteAllText(Path.Combine(AppPaths.PayloadDir, "version.json"),
                    JsonSerializer.Serialize(new PayloadVersion
                    {
                        Hookshot = hs.Replace("Hookshot-", ""),
                        Pathwinder = pw.Replace("Pathwinder-", "")
                    }, JsonOpts.Default));
                return true;
            }
            catch { return false; /* 版本号写入失败不影响修复结果 */ }
        }

        /// <summary>version.json 缺失或为空时的静默自愈：从 resources 重写。</summary>
        internal static bool RewriteVersionFromResources()
        {
            foreach (string baseDir in ResourceDirs())
            {
                if (!Directory.Exists(baseDir)) continue;
                if (WriteVersion(baseDir)) return true;
            }
            return false;
        }

        public static PayloadVersion ReadVersion()
        {
            try
            {
                string path = Path.Combine(AppPaths.PayloadDir, "version.json");
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<PayloadVersion>(File.ReadAllText(path), JsonOpts.Default);
            }
            catch { return null; }
        }

        /// <summary>resources 候选目录：发布形态 AppDir\resources，开发形态回退到项目源码 resources。</summary>
        private static string[] ResourceDirs() => new[]
        {
            Path.Combine(AppPaths.AppDir, "resources"),
            Path.Combine(AppPaths.AppDir, "..", "..", "..", "resources")
        };

        /// <summary>扫描 resources 下版本（zip 文件名或解压目录名；发布目录无 resources 时返回 null）。</summary>
        public static PayloadVersion ReadResourceVersion()
        {
            foreach (string baseDir in ResourceDirs())
            {
                try
                {
                    if (!Directory.Exists(baseDir)) continue;
                    // zip 文件名优先（发布形态），解压目录兜底（开发形态）
                    string hs = Directory.GetFiles(baseDir, "Hookshot-*.zip").Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
                                ?? Directory.GetDirectories(baseDir, "Hookshot-*").Select(Path.GetFileName).FirstOrDefault();
                    string pw = Directory.GetFiles(baseDir, "Pathwinder-*.zip").Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
                                ?? Directory.GetDirectories(baseDir, "Pathwinder-*").Select(Path.GetFileName).FirstOrDefault();
                    if (hs == null || pw == null) continue;
                    return new PayloadVersion
                    {
                        Hookshot = hs.Replace("Hookshot-", ""),
                        Pathwinder = pw.Replace("Pathwinder-", "")
                    };
                }
                catch { }
            }
            return null;
        }
    }

    // ===================== 工作区 runtime\&lt;hash&gt;（文档 4.1 / 5.1.2 / 5.3.1）=====================

    public static class WorkspaceManager
    {
        public static string HookshotExePath(ProgramEntry p)
        {
            string suffix = p.Architecture == ProgramEntry.X86 ? "32" : "64";
            return Path.Combine(AppPaths.WorkDir(p.Id), $"Hookshot.{suffix}.exe");
        }

        /// <summary>
        /// 准备工作区：按文件增量维护，不再“删掉整个目录再重建”。
        /// 运行中的程序会锁住工作区里的 Hookshot.64.dll / Pathwinder.HookModule.64.dll，
        /// 整目录删除会删一半就失败，留下没有 Hookshot.64.exe 的半截目录，快捷方式随即断链（界面显示“未受管”）。
        /// 因此：缺失的文件补硬链接；指向过期 payload 的文件尽力替换，被锁定的保留旧版并记入返回值，
        /// 由下次启动（程序已退出）自动替换。INI 每次按配置重写，保证 ini 与 config.json 始终一致。
        /// </summary>
        public static List<string> Prepare(ProgramEntry p, List<Rule> effectiveRules)
        {
            string dir = AppPaths.WorkDir(p.Id);
            Directory.CreateDirectory(dir);

            var pending = new List<string>();
            foreach (string file in PayloadManager.WorkspaceBinaries())
            {
                string source = Path.Combine(AppPaths.PayloadDir, file);
                string link = Path.Combine(dir, file);
                if (!File.Exists(source))
                    throw new PwException(PwCode.HardLinkFailed, $"payload 中缺少源文件：{file}");

                bool exists = File.Exists(link);
                if (exists && HardLinkHelper.IsLinkedTo(link, source)) continue; // 已指向最新真身
                if (exists)
                {
                    try { File.Delete(link); }
                    catch { pending.Add(file); continue; } // 被运行中的程序加载，保留旧文件
                }
                try { HardLinkHelper.Create(source, link); }
                catch { pending.Add(file); }
            }

            IniWriter.Write(Path.Combine(dir, "Pathwinder.ini"), effectiveRules);
            WriteHookshotIni(dir, p.HookshotLogEnabled);
            return pending;
        }

        /// <summary>校验工作区硬链接与 payload 真身是否一致，不一致则增量修复。返回是否发生了重建。</summary>
        public static bool VerifyAndRebuild(ProgramEntry p, List<Rule> effectiveRules, FileLogger log)
        {
            string dir = AppPaths.WorkDir(p.Id);
            try
            {
                bool needRebuild = !Directory.Exists(dir) ||
                                   !File.Exists(Path.Combine(dir, "Pathwinder.ini")) ||
                                   !File.Exists(Path.Combine(dir, "Hookshot.ini"));
                if (!needRebuild)
                {
                    foreach (string file in PayloadManager.WorkspaceBinaries())
                    {
                        string link = Path.Combine(dir, file);
                        string source = Path.Combine(AppPaths.PayloadDir, file);
                        if (!File.Exists(source) || !File.Exists(link) || !HardLinkHelper.IsLinkedTo(link, source))
                        {
                            needRebuild = true;
                            break;
                        }
                    }
                }

                // 无论是否需要重建都调用：顺带按 config 重写两个 INI（ini 与界面配置自动同步）
                var pending = Prepare(p, effectiveRules);
                if (pending.Count > 0)
                {
                    log.Warn($"工作区有文件被运行中的程序占用，已保留旧版（下次启动自动替换）：" +
                             $"{p.Id}（{p.DisplayName}）：{string.Join("、", pending)}");
                    return false;
                }
                if (needRebuild)
                    log.Warn($"工作区断链或文件缺失，已重建：{p.Id}（{p.DisplayName}）");
                return needRebuild;
            }
            catch (Exception ex)
            {
                log.Error($"工作区校验/重建失败：{p.Id}（{p.DisplayName}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>删除工作区；失败由调用方记入 pendingCleanup。</summary>
        public static void Delete(string programId)
        {
            string dir = AppPaths.WorkDir(programId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        /// <summary>
        /// Hookshot v2 配置：
        /// - HookshotDll 与 HookshotExe 同在工作区目录，必须显式让它从自身目录找 hook module；
        /// - 存在非空 ini 时 UseConfiguredHookModules 默认为 yes（只加载列出的模块），
        ///   因此必须显式写 HookModule = Pathwinder，漏写会导致 Pathwinder 根本不加载；
        /// - LogLevel：0=完全禁用（默认，桌面零日志）；2=错误+警告（诊断用，发现未授权子进程）。
        /// </summary>
        public static string BuildHookshotIni(bool logEnabled) =>
            $"LogLevel = {(logEnabled ? 2 : 0)}\r\n" +
            "LoadHookModulesFromHookshotDirectory = yes\r\n" +
            "HookModule = Pathwinder\r\n";

        private static void WriteHookshotIni(string workDir, bool logEnabled)
        {
            try
            {
                File.WriteAllText(Path.Combine(workDir, "Hookshot.ini"), BuildHookshotIni(logEnabled));
            }
            catch (Exception ex)
            {
                throw new PwException(PwCode.IniWrite, "Hookshot.ini：" + ex.Message);
            }
        }

        /// <summary>仅更新工作区 Hookshot.ini 的 LogLevel（用户在详情页切换开关时调用，不重建工作区）。</summary>
        public static void SetHookshotLogLevel(ProgramEntry p)
        {
            string ini = Path.Combine(AppPaths.WorkDir(p.Id), "Hookshot.ini");
            if (!File.Exists(ini)) return; // 未添加指令，Prepare 时会按开关写入
            string content = File.ReadAllText(ini);
            string updated = System.Text.RegularExpressions.Regex.Replace(
                content, @"(?m)^(\s*LogLevel\s*=\s*)\d\s*$",
                "${1}" + (p.HookshotLogEnabled ? "2" : "0"));
            if (!string.Equals(updated, content, StringComparison.Ordinal))
                File.WriteAllText(ini, updated);
        }
    }

    // ===================== 指令编排（文档 5.1）=====================

    public class ProgramService
    {
        private readonly AppConfig _cfg;
        public FileLogger Log { get; }

        /// <summary>目录可写性探针结果缓存：目录可写性在进程生命周期内极少变化，避免每次 UI 刷新都做磁盘 I/O。</summary>
        private readonly Dictionary<string, bool> _dirWritableCache = new(StringComparer.OrdinalIgnoreCase);

        public ProgramService(AppConfig cfg)
        {
            _cfg = cfg;
            Log = new FileLogger(AppPaths.ManagerLogPath, "Program");
        }

        /// <summary>快捷方式是否可用：桌面文件存在，或备份目录中存在（拖入 exe 场景）。</summary>
        public static bool IsShortcutAvailable(ProgramEntry p) =>
            File.Exists(p.OriginalShortcut) || File.Exists(AppPaths.BackupFile(p.Id));

        /// <summary>
        /// 目录可写性探针（带缓存）：在目录中创建/删除 .pw-write-probe 文件。
        /// 同一目录的结果会被缓存，避免 UI 刷新时重复磁盘 I/O；探针实际失败时不缓存，便于重试。
        /// </summary>
        private bool CheckDirWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true; // 目标缺失交由后续流程报错
            if (_dirWritableCache.TryGetValue(dir, out bool cached)) return cached;
            try
            {
                string probe = Path.Combine(dir, ".pw-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                _dirWritableCache[dir] = true;
                return true;
            }
            catch
            {
                // 失败不缓存：用户可能中途提权或释放占用，下次应重新探测
                return false;
            }
        }

        /// <summary>
        /// 目标目录可写性校验：Hookshot 授权文件（&lt;exe&gt;.hookshot / .hookshot）必须写入 exe 同目录。
        /// Program Files 等受保护目录在非管理员下写入被拒，此时继续只会产生半残指令，故提前拦截。
        /// </summary>
        private void EnsureTargetPermission(ProgramEntry p)
        {
            string dir = Path.GetDirectoryName(p.OriginalTarget);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return; // 目标缺失交由后续流程报错
            if (!CheckDirWritable(dir))
            {
                Log.Warn($"目标目录当前用户不可写，拦截添加指令：{dir}");
                throw new PwException(PwCode.NoPermission, dir);
            }
        }

        /// <summary>
        /// 目标 exe 所在目录是否可写（无需管理员即可写入 .hookshot 授权文件）。
        /// 与 EnsureTargetPermission 共用探针，结果带缓存，供 UI 前置判断使用。
        /// </summary>
        public bool IsTargetWritable(ProgramEntry p)
        {
            string dir = Path.GetDirectoryName(p.OriginalTarget);
            return CheckDirWritable(dir);
        }

        /// <summary>
        /// 快捷方式所在位置是否可写（改写 .lnk 需要对所在目录有写权限，且文件本身不能是只读）。
        /// 用于 UI 前置判断与 ApplyInstruction 权限拦截，避免 IShellLink.Save 抛 E_ACCESSDENIED。
        /// </summary>
        public bool IsShortcutWritable(ProgramEntry p)
        {
            string lnkPath = p.OriginalShortcut;
            if (string.IsNullOrEmpty(lnkPath)) return true;
            string dir = Path.GetDirectoryName(lnkPath);
            if (string.IsNullOrEmpty(dir)) return true;

            // 目录可写性探针（如 C:\Users\Public\Desktop 非管理员不可写），带缓存
            try { Directory.CreateDirectory(dir); } catch { return false; }
            if (!CheckDirWritable(dir)) return false;

            // 快捷方式文件存在时，检查只读属性（IShellLink.Save 对只读文件返回 E_ACCESSDENIED）
            if (File.Exists(lnkPath))
            {
                try
                {
                    var attrs = File.GetAttributes(lnkPath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(lnkPath, attrs & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>拖入 .lnk：解析并登记。返回程序记录（已登记则返回原记录）。</summary>
        public ProgramEntry RegisterShortcut(string lnkPath)
        {
            var info = ShortcutManager.Read(lnkPath);
            if (string.IsNullOrWhiteSpace(info.TargetPath))
                throw new PwException(PwCode.ShortcutRewrite, "快捷方式没有指向任何目标程序");

            // 拖入的是已带指令的 [PW] 快捷方式：从参数中还原原 exe（文档 5.1.1 已带指令检测）
            if (ShortcutManager.IsManaged(lnkPath, AppPaths.RuntimeRoot, out _))
            {
                string origExe = ExtractFirstQuoted(info.Arguments);
                if (!string.IsNullOrEmpty(origExe))
                {
                    var restored = RegisterCore(lnkPath, origExe,
                        StripFirstQuoted(info.Arguments), info.WorkingDirectory);
                    Log.Info($"登记已带指令的快捷方式：{lnkPath} → {origExe}");
                    return restored;
                }
            }

            if (!File.Exists(info.TargetPath))
                throw new PwException(PwCode.ShortcutRewrite, $"目标程序不存在：{info.TargetPath}");
            return RegisterCore(lnkPath, info.TargetPath, info.Arguments, info.WorkingDirectory);
        }

        /// <summary>拖入 .exe：原始快捷方式直接存入备份目录，桌面仅记录目标路径，添加指令时才生成 [PW] 快捷方式。</summary>
        public ProgramEntry RegisterExe(string exePath)
        {
            if (!File.Exists(exePath))
                throw new PwException(PwCode.ShortcutRewrite, $"目标程序不存在：{exePath}");

            // 桌面目标路径：不创建文件，仅作为添加指令后 [PW] 快捷方式的落点
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string baseName = Path.GetFileNameWithoutExtension(exePath);
            string lnkPath = Path.Combine(desktop, baseName + ".lnk");
            for (int i = 2; File.Exists(lnkPath); i++)
                lnkPath = Path.Combine(desktop, $"{baseName} ({i}).lnk");

            // 原始快捷方式直接创建到备份目录，避免桌面出现多余快捷方式
            string id = ProgramEntry.ComputeId(exePath);
            Directory.CreateDirectory(AppPaths.BackupRoot);
            ShortcutManager.CreateFromExe(AppPaths.BackupFile(id), exePath, "", Path.GetDirectoryName(exePath));

            var entry = RegisterCore(lnkPath, exePath, "", Path.GetDirectoryName(exePath));
            Log.Info($"拖入 exe，原始快捷方式已存于备份目录，桌面目标路径：{lnkPath}");
            return entry;
        }

        private ProgramEntry RegisterCore(string lnkPath, string targetExe, string arguments, string workingDir)
        {
            string id = ProgramEntry.ComputeId(targetExe);
            var existing = _cfg.FindProgram(id);
            if (existing != null) return existing;

            string arch = PeDetector.Detect(targetExe);
            if (arch == PeDetector.Unknown)
            {
                arch = ProgramEntry.X64; // 默认 x64，详情页可手动切换（文档 4.3 / PW007）
                Log.Warn($"位数检测失败，默认按 x64 登记，请手动确认：{targetExe}");
            }

            string displayName = StripManagedPrefix(Path.GetFileNameWithoutExtension(lnkPath));

            var entry = new ProgramEntry
            {
                Id = id,
                DisplayName = displayName,
                OriginalShortcut = lnkPath,
                OriginalTarget = targetExe,
                OriginalArguments = arguments ?? "",
                OriginalWorkingDir = workingDir ?? "",
                // 登记即带入默认规则（内置 AppData 模板 → 管理器目录\<程序名>）
                OverrideRules = ConfigManager.CreateDefaultOverrideRules(_cfg, displayName, id),
                Architecture = arch
            };
            _cfg.Programs.Add(entry);
            Log.Info($"登记程序：{entry.DisplayName}（{id}，{arch}）→ {targetExe}");
            return entry;
        }

        /// <summary>拷贝编号正则：提取显示名末尾的" 数字"后缀（空格 + 数字结尾）。</summary>
        private static readonly Regex CopyNumberRegex = new(@"^(.+)\s(\d+)$", RegexOptions.Compiled);

        /// <summary>提取显示名的基础名与编号（拷贝功能文档 3.1）。无数字后缀时整名为基础名（视为原件，编号 0）。</summary>
        private static string ExtractBaseName(string displayName, out int number)
        {
            displayName ??= "";
            var m = CopyNumberRegex.Match(displayName);
            if (m.Success)
            {
                number = int.Parse(m.Groups[2].Value);
                return m.Groups[1].Value.TrimEnd();
            }
            number = 0;
            return displayName;
        }

        /// <summary>扫描全部记录（含已排除项）中同基础名的最大编号，+1 保证不重复（拷贝功能文档 3.2）。</summary>
        private int NextCopyNumber(string baseName)
        {
            int max = 0;
            foreach (var p in _cfg.Programs)
            {
                string otherBase = ExtractBaseName(p.DisplayName, out int n);
                if (!string.Equals(otherBase, baseName, StringComparison.Ordinal)) continue;
                if (n > max) max = n;
            }
            return max + 1;
        }

        /// <summary>剔除文件名非法字符（显示名可能含用户手输字符，拷贝的 .lnk 落点必须是合法文件名）。</summary>
        private static string SanitizeFileName(string name)
        {
            string safe = string.Concat((name ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
            return safe.Length > 0 ? safe : "copy";
        }

        /// <summary>
        /// 拷贝程序记录（拷贝功能文档第 4/5 节）：深拷贝全部字段为新记录，
        /// 显示名与规则目标路径自动编号（取同基础名最大编号 +1），Id 重算避免冲突。
        /// </summary>
        public ProgramEntry CopyProgram(ProgramEntry src)
        {
            string baseName = ExtractBaseName(src.DisplayName, out _);
            int number = NextCopyNumber(baseName);
            string newName = baseName + " " + number;
            string id = ProgramEntry.ComputeId(src.OriginalTarget + "|" + newName);

            // 快捷方式落点跟随新显示名，避免拷贝记录与源记录写同一个 .lnk
            string srcDir = Path.GetDirectoryName(src.OriginalShortcut);
            if (string.IsNullOrEmpty(srcDir))
                srcDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcut = Path.Combine(srcDir, SanitizeFileName(newName) + ".lnk");

            var entry = new ProgramEntry
            {
                Id = id,
                DisplayName = newName,
                OriginalShortcut = shortcut,
                OriginalTarget = src.OriginalTarget,
                OriginalArguments = src.OriginalArguments,
                OriginalWorkingDir = src.OriginalWorkingDir,
                Architecture = src.Architecture,
                HookshotLogEnabled = src.HookshotLogEnabled,
                Excluded = src.Excluded,
                IsCopy = true, // 拷贝记录：删除时不恢复快捷方式，直接删除落点文件
                OverrideRules = CopyRulesWithNumber(src.OverrideRules, baseName, newName, number)
            };
            _cfg.Programs.Add(entry);
            // 为拷贝记录创建落点快捷方式：否则 IsShortcutAvailable=false，主列表误报"文件缺失"且无法添加指令
            EnsureCopyShortcutFile(src, shortcut);
            Log.Info($"拷贝程序：{src.DisplayName} → {newName}（{id}）");
            return entry;
        }

        /// <summary>
        /// 为拷贝记录创建落点快捷方式：优先从源记录备份复制（保留原图标）；
        /// 源快捷方式存在且未被改写时直接复制；兜底用原始信息重建。
        /// 创建失败不中断拷贝（状态会显示"文件缺失"，可在详情页重试添加指令）。
        /// </summary>
        private void EnsureCopyShortcutFile(ProgramEntry src, string newShortcutPath)
        {
            if (File.Exists(newShortcutPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newShortcutPath)!);
                string srcBackup = AppPaths.BackupFile(src.Id);
                if (File.Exists(srcBackup))
                    File.Copy(srcBackup, newShortcutPath, true);
                else if (File.Exists(src.OriginalShortcut) &&
                         !ShortcutManager.IsManaged(src.OriginalShortcut, AppPaths.RuntimeRoot, out _))
                    File.Copy(src.OriginalShortcut, newShortcutPath, true);
                else
                {
                    ShortcutManager.CreateFromExe(newShortcutPath, src.OriginalTarget, src.OriginalArguments,
                        string.IsNullOrWhiteSpace(src.OriginalWorkingDir)
                            ? Path.GetDirectoryName(src.OriginalTarget)
                            : src.OriginalWorkingDir);
                }
                Log.Info($"拷贝已创建落点快捷方式：{newShortcutPath}");
            }
            catch (Exception ex)
            {
                Log.Warn($"拷贝创建落点快捷方式失败：{newShortcutPath}：{ex.Message}");
            }
        }

        /// <summary>深拷贝规则集：目标路径整段替换基础名为新显示名；不含基础名时末尾追加编号（拷贝功能文档 4.1），并预创建目标目录。</summary>
        private List<Rule> CopyRulesWithNumber(List<Rule> rules, string baseName, string newName, int number)
        {
            var list = new List<Rule>();
            foreach (var r in rules)
            {
                var copy = r.Clone();
                string target = copy.TargetDirectory;
                if (!string.IsNullOrWhiteSpace(target))
                {
                    copy.TargetDirectory = NumberTargetDir(target.TrimEnd('\\', '/'), baseName, newName, number);
                    try { Directory.CreateDirectory(copy.TargetDirectory); }
                    catch (Exception ex)
                    {
                        Log.Warn($"拷贝预创建目标目录失败（不影响登记）：{copy.TargetDirectory}：{ex.Message}");
                    }
                }
                list.Add(copy);
            }
            return list;
        }

        /// <summary>
        /// 目标路径编号（拷贝功能文档 4.1）：最后一段含基础名时重建为新显示名，并去掉旧显示名遗留的编号链
        /// （如 "微信 3 2" → "微信 4"，保证目录名与快捷方式名称一致）；父级路径中出现的仍整段替换；
        /// 最后一段不含基础名时在末段追加编号。
        /// </summary>
        private static string NumberTargetDir(string target, string baseName, string newName, int number)
        {
            int idx = target.LastIndexOf('\\');
            string parent = idx >= 0 ? target.Substring(0, idx) : "";
            string last = idx >= 0 ? target.Substring(idx + 1) : target;

            if (!string.IsNullOrEmpty(last) && !string.IsNullOrEmpty(baseName) && last.Contains(baseName))
            {
                // 先去掉旧编号链（"微信 3 2"→"微信"），再整段替换基础名。
                // 顺序不能反：若先替换成新显示名"微信 1"再去尾，会把新名字自带的编号也删掉（退回"微信"）
                last = Regex.Replace(last, @"(?:\s\d+)+$", "").Replace(baseName, newName);
            }
            else
                last = last + " " + number;
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(baseName) && parent.Contains(baseName))
                parent = parent.Replace(baseName, newName);
            return string.IsNullOrEmpty(parent) ? last : parent + Path.DirectorySeparatorChar + last;
        }

        /// <summary>一键添加指令（文档 5.1.2）。allowEmptyRules=true 用于批量确认后仍继续。</summary>
        public void ApplyInstruction(ProgramEntry p, bool allowEmptyRules = false)
        {
            // 0. 权限前置校验：目标目录不可写时 Hookshot 授权文件无法创建，直接拦截（文档 5.1.2）
            EnsureTargetPermission(p);
            // 快捷方式所在目录不可写或文件只读时，IShellLink.Save 会抛 E_ACCESSDENIED，提前拦截为 PW006
            if (!IsShortcutWritable(p))
                throw new PwException(PwCode.NoPermission, Path.GetDirectoryName(p.OriginalShortcut));

            // 兜底：payload 缺失时先尝试从 resources 自动修复
            var missing = PayloadManager.MissingBinaries();
            if (missing.Count > 0)
            {
                var repaired = PayloadManager.EnsurePayload();
                if (repaired.Count > 0)
                    Log.Info($"[ApplyInstruction] payload 缺失已自动修复：{string.Join("、", repaired)}");
            }

            if (p.Architecture == PeDetector.Arm64)
                throw new PwException(PwCode.Arm64Unsupported, p.DisplayName);
            if (!IsShortcutAvailable(p))
                throw new PwException(PwCode.ShortcutRewrite, $"快捷方式文件不存在：{p.OriginalShortcut}");

            var rules = p.OverrideRules;
            if (rules.Count == 0)
            {
                if (!allowEmptyRules)
                    throw new PwException(PwCode.RuleInvalid, "没有任何生效规则");
                Log.Warn($"程序 {p.DisplayName} 生效规则为空，将生成空 Pathwinder.ini");
            }
            else
            {
                string err = RuleValidator.ValidateList(rules);
                if (err != null) throw new PwException(PwCode.RuleInvalid, err);
            }

            // 1. 备份原快捷方式（拖入 exe 的场景原始快捷方式已在 backups 目录，OriginalShortcut 文件不存在，无需重复备份）
            Directory.CreateDirectory(AppPaths.BackupRoot);
            if (File.Exists(p.OriginalShortcut))
            {
                File.Copy(p.OriginalShortcut, AppPaths.BackupFile(p.Id), true);
            }

            // 2. 准备工作区（增量补硬链接 + 按配置写 INI）
            var pendingFiles = WorkspaceManager.Prepare(p, rules);
            if (pendingFiles.Count > 0)
                Log.Warn($"工作区有文件被运行中的程序占用，已保留旧版（程序下次启动时自动替换）：{string.Join("、", pendingFiles)}");

            // 3. Hookshot v2 授权：目标 exe 旁需要空文件 "&lt;exe&gt;.hookshot"，失败仅告警（受保护目录由用户手动处理）
            TryCreateAuthFile(p);

            // 4. 改写快捷方式（保留原程序图标：IconLocation 为空时用原 exe 兜底，避免系统 fallback 到 Hookshot.exe 的图标）
            // 桌面 .lnk 不存在时（拖入 exe 场景）从备份读；备份也读不到时用原 exe 路径兜底
            string iconLocation = "";
            string iconSourceLnk = File.Exists(p.OriginalShortcut)
                ? p.OriginalShortcut
                : AppPaths.BackupFile(p.Id);
            try
            {
                if (File.Exists(iconSourceLnk))
                    iconLocation = ShortcutManager.Read(iconSourceLnk).IconLocation;
            }
            catch (Exception ex) { Log.Warn($"读取原图标失败，将用原 exe 兜底：{ex.Message}"); }

            // 原快捷方式未显式设 IconLocation 时，用原 exe 路径兜底
            // 否则系统会按新 TargetPath(Hookshot.XX.exe) 取图标，原程序图标丢失
            if (string.IsNullOrEmpty(iconLocation))
                iconLocation = p.OriginalTarget + ",0";

            string arguments = $"\"{p.OriginalTarget}\"";
            if (!string.IsNullOrWhiteSpace(p.OriginalArguments))
                arguments += " " + p.OriginalArguments.Trim();
            string workingDir = string.IsNullOrWhiteSpace(p.OriginalWorkingDir)
                ? Path.GetDirectoryName(p.OriginalTarget)
                : p.OriginalWorkingDir;

            ShortcutManager.Write(p.OriginalShortcut, new ShortcutManager.ShortcutInfo
            {
                TargetPath = WorkspaceManager.HookshotExePath(p),
                Arguments = arguments,
                WorkingDirectory = workingDir,
                IconLocation = iconLocation
            });

            // 5. [PW] 视觉标记（改名前缀）
            AddPrefixToShortcut(p);

            Log.Info($"已添加指令：{p.DisplayName}（{p.Id}，{p.Architecture}）");
        }

        /// <summary>一键移除指令（文档 5.1.3）。</summary>
        public void RemoveInstruction(ProgramEntry p)
        {
            if (!IsShortcutAvailable(p))
                throw new PwException(PwCode.BackupRestore, $"快捷方式文件不存在：{p.OriginalShortcut}");

            // 快捷方式所在目录不可写时，恢复/删除快捷方式会失败，提前拦截为 PW006
            if (!IsShortcutWritable(p))
                throw new PwException(PwCode.NoPermission, Path.GetDirectoryName(p.OriginalShortcut));

            string dir = Path.GetDirectoryName(p.OriginalShortcut);
            if (!Directory.Exists(dir))
                throw new PwException(PwCode.BackupRestore, dir);

            string name = StripManagedPrefix(Path.GetFileNameWithoutExtension(p.OriginalShortcut));
            string restoredPath = Path.Combine(dir, name + ".lnk");
            string backup = AppPaths.BackupFile(p.Id);

            if (File.Exists(backup))
            {
                File.Copy(backup, restoredPath, true);
                if (!string.Equals(p.OriginalShortcut, restoredPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(p.OriginalShortcut);
            }
            else
            {
                // 备份缺失兜底：用 config 中保存的原始信息重建
                Log.Warn($"备份缺失，使用配置信息重建快捷方式：{p.DisplayName}");
                ShortcutManager.Write(restoredPath, new ShortcutManager.ShortcutInfo
                {
                    TargetPath = p.OriginalTarget,
                    Arguments = p.OriginalArguments,
                    WorkingDirectory = string.IsNullOrWhiteSpace(p.OriginalWorkingDir)
                        ? Path.GetDirectoryName(p.OriginalTarget)
                        : p.OriginalWorkingDir
                });
                if (!string.Equals(p.OriginalShortcut, restoredPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(p.OriginalShortcut);
            }

            p.OriginalShortcut = restoredPath;
            if (File.Exists(backup))
            {
                try { File.Delete(backup); } catch { }
            }

            DeleteWorkspaceSafe(p.Id);

            Log.Info($"已移除指令：{p.DisplayName}（{p.Id}）");
        }

        /// <summary>删除程序记录（详情页"删除"）：拷贝记录直接删除落点快捷方式，普通记录受管时先恢复快捷方式；同步删除管理器目录内的规则目标目录。</summary>
        public void DeleteEntry(ProgramEntry p)
        {
            bool managed = IsShortcutAvailable(p) &&
                           File.Exists(p.OriginalShortcut) &&
                           ShortcutManager.IsManaged(p.OriginalShortcut, AppPaths.RuntimeRoot, out _);

            if (p.IsCopy)
            {
                // 拷贝记录（拷贝功能文档）：落点快捷方式是拷贝时生成的副本，删除记录时直接删除，不恢复备份
                if (File.Exists(p.OriginalShortcut))
                {
                    try { File.Delete(p.OriginalShortcut); Log.Info($"已删除拷贝记录快捷方式：{p.OriginalShortcut}"); }
                    catch (Exception ex) { Log.Warn($"删除拷贝记录快捷方式失败：{p.OriginalShortcut}：{ex.Message}"); }
                }
                DeleteWorkspaceSafe(p.Id); // 受管时清理工作区；未受管时目录不存在，安全跳过
            }
            else if (managed)
            {
                RemoveInstruction(p);
            }

            _cfg.Programs.Remove(p);
            string backup = AppPaths.BackupFile(p.Id);
            if (File.Exists(backup)) { try { File.Delete(backup); } catch { } }
            DeleteDataDirs(p);
            Log.Info($"已删除程序记录：{p.DisplayName}（{p.Id}）");
        }

        /// <summary>删除工作区；被占用则记入 pendingCleanup，启动时优先清理（RemoveInstruction 与删除拷贝记录共用）。</summary>
        private void DeleteWorkspaceSafe(string programId)
        {
            try
            {
                WorkspaceManager.Delete(programId);
            }
            catch (Exception ex)
            {
                if (!_cfg.PendingCleanup.Contains(programId)) _cfg.PendingCleanup.Add(programId);
                Log.Warn($"工作区暂无法删除，已记入 pendingCleanup：{programId}：{ex.Message}");
            }
        }

        /// <summary>
        /// 删除记录时同步删除其私有规则的目标目录。安全边界：仅删除管理器安装目录内的目录
        /// （如 data\程序名），外部路径（D:\MyData\...）不动；与全局规则或其他记录共享的目录跳过，
        /// 避免误删公共数据。删除失败仅记日志，不阻断记录删除。
        /// </summary>
        private void DeleteDataDirs(ProgramEntry p)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string appDir = AppPaths.AppDir.TrimEnd('\\') + Path.DirectorySeparatorChar;
            foreach (var r in p.OverrideRules)
            {
                string t = r.TargetDirectory?.TrimEnd('\\', '/');
                if (string.IsNullOrWhiteSpace(t) || !t.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                targets.Add(t);
            }
            foreach (string t in targets)
            {
                bool isGlobal = _cfg.GlobalRules.Any(g =>
                    string.Equals(g.TargetDirectory?.TrimEnd('\\', '/'), t, StringComparison.OrdinalIgnoreCase));
                bool shared = _cfg.Programs.Any(o => o.OverrideRules.Any(x =>
                    string.Equals(x.TargetDirectory?.TrimEnd('\\', '/'), t, StringComparison.OrdinalIgnoreCase)));
                if (isGlobal || shared)
                {
                    Log.Info($"目标目录被全局规则或其他记录共享，跳过删除：{t}");
                    continue;
                }
                try
                {
                    if (Directory.Exists(t)) { Directory.Delete(t, true); Log.Info($"已删除记录目标目录：{t}"); }
                }
                catch (Exception ex)
                {
                    Log.Warn($"删除记录目标目录失败（可手动清理）：{t}：{ex.Message}");
                }
            }
        }

        /// <summary>手动切换位数；若已受管则按新位数重建工作区并重写快捷方式。</summary>
        public void ChangeArchitecture(ProgramEntry p, string architecture)
        {
            if (p.Architecture == architecture) return;
            p.Architecture = architecture;
            Log.Info($"位数手动切换为 {architecture}：{p.DisplayName}");
            if (File.Exists(p.OriginalShortcut) &&
                ShortcutManager.IsManaged(p.OriginalShortcut, AppPaths.RuntimeRoot, out _))
                ApplyInstruction(p, allowEmptyRules: true);
        }

        /// <summary>通过快捷方式启动程序（受管时等价于 Hookshot 启动，未受管时启动原程序）。</summary>
        public void Launch(ProgramEntry p)
        {
            if (!File.Exists(p.OriginalShortcut))
                throw new PwException(PwCode.BackupRestore, $"快捷方式文件不存在：{p.OriginalShortcut}");
            Process.Start(new ProcessStartInfo(p.OriginalShortcut) { UseShellExecute = true });
            Log.Info($"启动：{p.DisplayName}");
        }

        /// <summary>同名进程是否已在运行（Hookshot 无法注入已运行进程，文档风险 2）。</summary>
        public static bool IsTargetRunning(ProgramEntry p)
        {
            try
            {
                string exeName = Path.GetFileNameWithoutExtension(p.OriginalTarget);
                return Process.GetProcessesByName(exeName).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>当前生效的快捷方式前缀（来自配置，兜底默认值）。</summary>
        private string CurrentPrefix => _cfg?.Settings.ShortcutPrefix ?? Settings.DefaultShortcutPrefix;

        /// <summary>剥离受管前缀：优先匹配当前前缀，其次兼容旧版 [PW] 前缀（拖入旧快捷方式时仍能识别）。</summary>
        private string StripManagedPrefix(string name)
        {
            string prefix = CurrentPrefix;
            if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(prefix.Length);
            if (name.StartsWith(ConfigManager.LegacyPrefix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(ConfigManager.LegacyPrefix.Length);
            return name;
        }

        private void AddPrefixToShortcut(ProgramEntry p)
        {
            try
            {
                string dir = Path.GetDirectoryName(p.OriginalShortcut);
                string name = Path.GetFileNameWithoutExtension(p.OriginalShortcut);
                string prefix = CurrentPrefix;
                // 已带当前前缀 → 无需改名
                if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                // 带旧前缀或无前缀 → 统一改为当前前缀（含旧 [PW] → 新前缀的迁移）
                string baseName = StripManagedPrefix(name);
                string newPath = Path.Combine(dir, prefix + baseName + ".lnk");
                if (File.Exists(newPath))
                {
                    Log.Warn($"目标文件名已存在，跳过前缀改名：{newPath}");
                    return;
                }
                File.Move(p.OriginalShortcut, newPath);
                p.OriginalShortcut = newPath;
            }
            catch (Exception ex)
            {
                Log.Warn($"前缀改名失败（不影响指令生效）：{ex.Message}");
            }
        }

        private void TryCreateAuthFile(ProgramEntry p)
        {
            // exe 级授权：<exe名>.hookshot；目录级授权：.hookshot（覆盖同目录所有 exe 及其同层启动的子进程）
            string exeDir = Path.GetDirectoryName(p.OriginalTarget);
            foreach (string authFile in new[] { p.OriginalTarget + ".hookshot", Path.Combine(exeDir, ".hookshot") })
            {
                try
                {
                    if (!File.Exists(authFile)) File.WriteAllText(authFile, "");
                }
                catch (Exception ex)
                {
                    Log.Warn($"无法创建 Hookshot 授权文件 {authFile}（目录可能受保护）：{ex.Message}");
                }
            }

            // 基于历史 Hookshot 日志，为上轮运行中被拒的子进程补授权（补后下次启动生效），并清理桌面日志
            var sync = ChildProcessAuth.SyncAll(_cfg.Programs, Log);
            if (sync.Added > 0)
                Log.Info($"已为 {sync.Added} 个子进程补 Hookshot 授权，下次经快捷方式启动时生效");
            if (sync.Failed.Count > 0)
                Log.Warn($"以下子进程目录受保护，授权文件需手动创建：{string.Join("、", sync.Failed)}");
        }

        /// <summary>定位首个成对引号的起止索引；无成对引号时返回 false。供 Extract/StripFirstQuoted 共用。</summary>
        private static bool TryFindFirstQuoted(string s, out int start, out int end)
        {
            start = -1; end = -1;
            if (string.IsNullOrEmpty(s)) return false;
            start = s.IndexOf('"');
            if (start < 0) return false;
            end = s.IndexOf('"', start + 1);
            return end >= 0;
        }

        private static string ExtractFirstQuoted(string arguments)
        {
            if (!TryFindFirstQuoted(arguments, out int start, out int end)) return null;
            return arguments.Substring(start + 1, end - start - 1);
        }

        private static string StripFirstQuoted(string arguments)
        {
            if (string.IsNullOrEmpty(arguments)) return "";
            if (!TryFindFirstQuoted(arguments, out _, out int end)) return arguments.Trim();
            return arguments.Substring(end + 1).Trim();
        }
    }

    // ===================== 子进程授权同步 + Hookshot 日志治理（多进程程序支持）=====================

    /// <summary>
    /// Hookshot v2 对主进程授权后，注入子进程仍需逐个授权（子进程 exe 旁的 .hookshot 文件）。
    /// 微信/Chromium 类多进程程序的实际数据读写常发生在子进程，缺授权会导致重定向整体不生效。
    /// 子进程路径无法提前枚举，因此解析 Hookshot 写到桌面的日志中
    /// “Failed to inject child process ... Not authorized” 行，事后批量补授权。
    ///
    /// 日志洪水治理：LogLevel≥1 时每个被注入进程每次运行都会在桌面创建日志（实测 1=仅错误时
    /// 即使无错误也会留下空日志文件），且文件名含子进程名前缀，多次启动堆积严重。
    /// 日志头的 Module 行带 runtime\<id>\，可精确归属受管程序。策略：
    /// - 新添加指令/重建工作区时 LogLevel=2（错误+警告，用于发现被拒子进程）；
    /// - 一轮运行零拒绝警告 → 该工作区 LogLevel 降为 0（完全禁用，桌面零日志）；
    /// - 大版本更新带来新子进程目录致重定向失效时，在管理器移除并重新添加指令即可重新走收敛；
    /// - 解析完即删除可删除的日志（目标程序运行中被占用则留到下轮）。
    /// </summary>
    public static class ChildProcessAuth
    {
        public class SyncResult
        {
            public int Added;
            /// <summary>本轮删除的桌面日志数（含归属本管理器的孤儿日志）。</summary>
            public int LogsCleaned;
            public readonly List<string> Failed = new();
        }

        private static readonly Regex DeniedChildRegex = new(
            @"\[[WE]\]\s+(.+?)\s+-\s+Failed to inject child process:\s*Not authorized",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 日志头 Module 行形如：Module:  D:\...\runtime\&lt;id&gt;\Hookshot.64.dll。运行时按本管理器 runtime 根构造，
        /// 精确区分其他 Hookshot 用户的日志。编译为 Compiled 静态只读，避免每次 SyncAll 重新构建。
        /// </summary>
        private static readonly Regex ModuleIdRegex = new(
            Regex.Escape(AppPaths.RuntimeRoot) + @"[\\/]+([0-9a-fA-F]{16,})[\\/]+Hookshot\.(?:32|64)\.dll",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 扫描桌面全部 Hookshot_*.log：归属受管工作区 → 为被拒子进程补授权 →
        /// 按拒绝有无升降 LogLevel → 删除日志。对全部受管程序统一执行一次。
        /// </summary>
        public static SyncResult SyncAll(IEnumerable<ProgramEntry> programs, FileLogger log)
        {
            var result = new SyncResult();
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!Directory.Exists(desktop)) return result;

                var byId = programs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
                // id -> 本轮被拒子进程路径集合
                var deniedById = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (string logFile in Directory.GetFiles(desktop, "Hookshot_*.log"))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(logFile); }
                    catch { continue; } // 被运行中进程独占，下轮再处理

                    string ownerId = null;
                    foreach (string line in lines)
                    {
                        var hm = ModuleIdRegex.Match(line);
                        if (hm.Success) { ownerId = hm.Groups[1].Value; break; }
                    }
                    // Module 行不含本管理器 runtime 根 = 其他 Hookshot 用户的日志，一律不动
                    if (ownerId == null) continue;
                    // 归属本管理器但程序已从配置移除 = 孤儿日志，直接清理
                    if (!byId.ContainsKey(ownerId))
                    {
                        try { File.Delete(logFile); result.LogsCleaned++; } catch { }
                        continue;
                    }

                    if (!deniedById.TryGetValue(ownerId, out var denied))
                    {
                        denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        deniedById[ownerId] = denied;
                    }

                    foreach (string line in lines)
                    {
                        var m = DeniedChildRegex.Match(line);
                        if (m.Success) denied.Add(m.Groups[1].Value.Trim());
                    }

                    try { File.Delete(logFile); result.LogsCleaned++; }
                    catch { /* 占用中，下轮删除 */ }
                }

                foreach (var kv in deniedById)
                {
                    string id = kv.Key;
                    int newAuth = 0;
                    foreach (string child in kv.Value)
                    {
                        // 系统目录子进程无法也无需授权，直接忽略
                        if (IsSystemLocation(child)) continue;
                        if (CreateAuth(child, log)) newAuth++;
                        else result.Failed.Add(child);
                    }
                    result.Added += newAuth;
                    // 不再自动调整 LogLevel——由用户在详情页开关控制；这里只补授权和清日志
                    if (newAuth > 0)
                        log?.Info($"已为 {byId[id].DisplayName} 补 {newAuth} 个子进程授权，下次经 [PW] 快捷方式启动时生效");
                }
            }
            catch (Exception ex)
            {
                log?.Warn("子进程授权同步异常：" + ex.Message);
            }
            return result;
        }

        private static bool IsSystemLocation(string exePath)
        {
            string dir = Path.GetDirectoryName(exePath) ?? "";
            return dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>优先 exe 级授权，失败再退目录级 .hookshot（同目录其他子进程也一并覆盖）。返回是否新建成功。</summary>
        private static bool CreateAuth(string childExePath, FileLogger log)
        {
            string exeAuth = childExePath + ".hookshot";
            try
            {
                if (!File.Exists(exeAuth)) { File.WriteAllText(exeAuth, ""); }
                return true;
            }
            catch (Exception ex)
            {
                log?.Warn($"创建子进程授权失败 {exeAuth}：{ex.Message}，尝试目录级授权");
            }

            try
            {
                string dirAuth = Path.Combine(Path.GetDirectoryName(childExePath), ".hookshot");
                if (!File.Exists(dirAuth)) File.WriteAllText(dirAuth, "");
                return true;
            }
            catch (Exception ex)
            {
                log?.Warn($"目录级授权也失败 {childExePath}：{ex.Message}");
                return false;
            }
        }
    }

    // ===================== 启动清理（文档 5.3）=====================

    public class CleanupResult
    {
        public int PendingRemoved;
        public int OrphanDirsRemoved;
        public int WorkspacesRebuilt;
        public int OrphanBackupsRemoved;
        public readonly List<string> Errors = new();
    }

    public static class CleanupManager
    {
        public static CleanupResult Run(AppConfig cfg)
        {
            var log = new FileLogger(AppPaths.CleanupLogPath, "Cleanup");
            var result = new CleanupResult();
            var validIds = new HashSet<string>(cfg.Programs.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

            // 1. pendingCleanup 优先
            foreach (string id in cfg.PendingCleanup.ToList())
            {
                try
                {
                    WorkspaceManager.Delete(id);
                    cfg.PendingCleanup.Remove(id);
                    result.PendingRemoved++;
                    log.Info($"已清理 pendingCleanup 工作区：{id}");
                }
                catch (Exception ex)
                {
                    log.Error($"pendingCleanup 工作区仍无法删除：{id}：{ex.Message}");
                    result.Errors.Add($"{id}：{ex.Message}");
                }
            }

            // 2. 扫描 runtime
            if (Directory.Exists(AppPaths.RuntimeRoot))
            {
                foreach (string dir in Directory.GetDirectories(AppPaths.RuntimeRoot))
                {
                    string id = Path.GetFileName(dir);
                    var p = cfg.FindProgram(id);
                    if (p == null)
                    {
                        // 孤儿目录；同时清理同名 pending 记录
                        try
                        {
                            Directory.Delete(dir, true);
                            cfg.PendingCleanup.Remove(id);
                            result.OrphanDirsRemoved++;
                            log.Info($"已删除孤儿工作区：{id}");
                        }
                        catch (Exception ex)
                        {
                            if (!cfg.PendingCleanup.Contains(id)) cfg.PendingCleanup.Add(id);
                            log.Error($"孤儿工作区删除失败：{id}：{ex.Message}");
                            result.Errors.Add($"{id}：{ex.Message}");
                        }
                    }
                    else
                    {
                        if (WorkspaceManager.VerifyAndRebuild(p, p.OverrideRules, log))
                            result.WorkspacesRebuilt++;
                    }
                }
            }

            // 3. 扫描 backups：对应 ID 不在配置中则删除
            if (Directory.Exists(AppPaths.BackupRoot))
            {
                foreach (string file in Directory.GetFiles(AppPaths.BackupRoot, "*.lnk"))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    if (!validIds.Contains(id))
                    {
                        try
                        {
                            File.Delete(file);
                            result.OrphanBackupsRemoved++;
                            log.Info($"已删除孤儿备份：{id}.lnk");
                        }
                        catch (Exception ex)
                        {
                            log.Error($"孤儿备份删除失败：{id}.lnk：{ex.Message}");
                        }
                    }
                }
            }

            cfg.Settings.LastCleanupTime = DateTime.Now;
            log.Info($"清理完成：pending {result.PendingRemoved}，孤儿目录 {result.OrphanDirsRemoved}，" +
                     $"重建 {result.WorkspacesRebuilt}，孤儿备份 {result.OrphanBackupsRemoved}");
            return result;
        }
    }

    // ===================== 启动协调 =====================

    public class StartupResult
    {
        public AppConfig Config;
        public ProgramService Programs;
        public CleanupResult Cleanup;
        public bool PermissionFatal;
        /// <summary>本次启动从 Hookshot 日志中新补的子进程授权数（提示用户重启目标程序生效）。</summary>
        public int ChildAuthAdded;
        public int ChildAuthFailed;
        public readonly List<string> Warnings = new();
    }

    public static class StartupCoordinator
    {
        /// <summary>启动时批量迁移：把旧 [PW] 前缀的受管快捷方式改名为当前前缀。</summary>
        private static void MigrateShortcutPrefixes(AppConfig cfg, FileLogger log)
        {
            string prefix = cfg.Settings.ShortcutPrefix;
            if (prefix == ConfigManager.LegacyPrefix) return;
            int migrated = 0;
            foreach (var p in cfg.Programs)
            {
                try
                {
                    if (string.IsNullOrEmpty(p.OriginalShortcut) || !File.Exists(p.OriginalShortcut)) continue;
                    string dir = Path.GetDirectoryName(p.OriginalShortcut);
                    string name = Path.GetFileNameWithoutExtension(p.OriginalShortcut);
                    if (!name.StartsWith(ConfigManager.LegacyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string baseName = name.Substring(ConfigManager.LegacyPrefix.Length);
                    string newPath = Path.Combine(dir, prefix + baseName + ".lnk");
                    if (File.Exists(newPath))
                    {
                        log.Warn($"迁移前缀跳过（目标已存在）：{newPath}");
                        continue;
                    }
                    File.Move(p.OriginalShortcut, newPath);
                    p.OriginalShortcut = newPath;
                    migrated++;
                }
                catch (Exception ex)
                {
                    log.Warn($"迁移前缀失败 {p.OriginalShortcut}：{ex.Message}");
                }
            }
            if (migrated > 0)
                log.Info($"已将 {migrated} 个旧 [PW] 快捷方式迁移到新前缀「{prefix}」");
        }

        public static StartupResult Initialize()
        {
            var result = new StartupResult();

            if (!AppPaths.IsWritable())
            {
                result.PermissionFatal = true;
                return result;
            }

            AppPaths.EnsureDirs();
            var bootLog = new FileLogger(AppPaths.ManagerLogPath, "Startup");
            bootLog.Info("管理器启动");

            result.Config = ConfigManager.Load(out var warnings);
            result.Warnings.AddRange(warnings);
            foreach (var w in warnings) bootLog.Warn("配置加载警告：" + w);

            MigrateShortcutPrefixes(result.Config, bootLog);

            // 记录首次启动时间（延迟展示依据）：仅首次为空时写入，随下方无条件 Save 持久化
            result.Config.Settings.FirstLaunchTime ??= DateTime.Now;

            // payload 修复必须先于工作区校验：安装包会清空 payload（强制重新解压），
            // 若先跑清理，工作区会因 payload 缺失而重建失败，本次启动仍会使用旧 DLL。
            var missing = PayloadManager.MissingBinaries();
            if (missing.Count > 0)
            {
                // 自动修复：从 resources（zip / 解压目录）恢复缺失的二进制
                var repaired = PayloadManager.EnsurePayload();
                if (repaired.Count > 0)
                {
                    // 静默恢复：只写日志，不弹提示（无法修复时才告警）
                    bootLog.Info("payload 缺失已自动修复：" + string.Join("、", repaired));
                    missing = PayloadManager.MissingBinaries();
                }
            }
            if (missing.Count > 0)
                result.Warnings.Add("payload 目录二进制不完整且 resources 中无可修复来源，添加指令将失败，缺失：" +
                                    string.Join("、", missing));

            result.Cleanup = CleanupManager.Run(result.Config);
            bootLog.Info($"启动清理完成：待处理 {result.Cleanup.PendingRemoved}，孤儿目录 {result.Cleanup.OrphanDirsRemoved}，" +
                         $"断链重建 {result.Cleanup.WorkspacesRebuilt}，孤儿备份 {result.Cleanup.OrphanBackupsRemoved}，" +
                         $"错误 {result.Cleanup.Errors.Count}");
            foreach (var e in result.Cleanup.Errors) bootLog.Error("清理错误：" + e);
            ConfigManager.Save(result.Config);

            var v = PayloadManager.ReadVersion();
            var rv = PayloadManager.ReadResourceVersion();
            if (rv != null && (v == null || string.IsNullOrEmpty(v.Hookshot) || string.IsNullOrEmpty(v.Pathwinder)))
            {
                // 构建期版本推导失败等场景留下的空/缺失版本：静默自愈，不提示
                if (PayloadManager.RewriteVersionFromResources())
                {
                    bootLog.Info("payload version.json 缺失或为空，已按 resources 版本重写");
                    v = PayloadManager.ReadVersion();
                }
            }
            if (v != null && rv != null &&
                !string.IsNullOrEmpty(v.Hookshot) && !string.IsNullOrEmpty(v.Pathwinder) &&
                (!string.Equals(v.Hookshot, rv.Hookshot, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(v.Pathwinder, rv.Pathwinder, StringComparison.OrdinalIgnoreCase)))
            {
                result.Warnings.Add($"resources 版本（Hookshot {rv.Hookshot} / Pathwinder {rv.Pathwinder}）" +
                                    $"与 payload（Hookshot {v.Hookshot} / Pathwinder {v.Pathwinder}）不一致，建议重新构建发布");
            }

            result.Programs = new ProgramService(result.Config);

            // 子进程授权同步 + 桌面 Hookshot 日志治理：
            // 解析上轮全部日志，为被拒子进程补授权并清理日志；LogLevel 由用户在详情页开关控制，管理器不自动改动
            var authLog = new FileLogger(AppPaths.ManagerLogPath, "ChildAuth");
            var sr = ChildProcessAuth.SyncAll(result.Config.Programs, authLog);
            result.ChildAuthAdded = sr.Added;
            result.ChildAuthFailed = sr.Failed.Count;
            if (sr.LogsCleaned > 0)
                authLog.Info($"Hookshot 日志治理：清理 {sr.LogsCleaned} 个桌面日志");
            foreach (var f in sr.Failed)
                authLog.Warn($"子进程授权失败（目录受保护）：{f}");

            ConfigManager.Save(result.Config);

            return result;
        }
    }
}
