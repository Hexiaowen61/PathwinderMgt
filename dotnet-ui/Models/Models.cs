using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PathwinderMgt.Models
{
    /// <summary>重定向规则，对应 Pathwinder.ini 中的一个 [FilesystemRule:名称] 块</summary>
    public class Rule
    {
        public string Name { get; set; } = "";
        public string OriginDirectory { get; set; } = "";
        public string TargetDirectory { get; set; } = "";
        public List<string> FilePatterns { get; set; } = new();

        /// <summary>内置规则（用户 AppData 重定向）：可编辑、不可删除，缺失时自动补回</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool BuiltIn { get; set; }

        public Rule Clone() => new()
        {
            Name = Name,
            OriginDirectory = OriginDirectory,
            TargetDirectory = TargetDirectory,
            FilePatterns = new List<string>(FilePatterns),
            BuiltIn = BuiltIn
        };
    }

    /// <summary>被管理的程序（config.json 中 programs 的一项）</summary>
    public class ProgramEntry
    {
        public const string X86 = "x86";
        public const string X64 = "x64";

        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string OriginalShortcut { get; set; } = "";
        public string OriginalTarget { get; set; } = "";
        public string OriginalArguments { get; set; } = "";
        public string OriginalWorkingDir { get; set; } = "";

        /// <summary>本程序生效的规则集（登记时从全局内置模板自动带入一条默认规则，可编辑）</summary>
        public List<Rule> OverrideRules { get; set; } = new();
        public string Architecture { get; set; } = X64;

        /// <summary>是否开启 Hookshot 诊断日志（默认关闭，LogLevel=0；开启时 LogLevel=2，用于排查子进程授权）。</summary>
        public bool HookshotLogEnabled { get; set; } = false;

        /// <summary>标记为"不管理"：主列表仅保留详情入口，全部添加/全部移除/删除全部记录等批量操作自动跳过。</summary>
        public bool Excluded { get; set; } = false;

        /// <summary>是否为拷贝生成的记录（拷贝功能文档）：落点快捷方式是拷贝时生成的副本，删除记录时直接删除该文件，不恢复备份。</summary>
        public bool IsCopy { get; set; } = false;

        /// <summary>程序 ID = SHA256(原 exe 完整路径) 取前 16 位十六进制（文档 5.1.2）</summary>
        public static string ComputeId(string exeFullPath)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(exeFullPath.Trim()));
            return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
        }
    }

    public class Settings
    {
        /// <summary>桌面快捷方式前缀默认值（符号 + 空格）。</summary>
        public const string DefaultShortcutPrefix = "● ";
        /// <summary>前缀最大长度（字符数）。</summary>
        public const int MaxShortcutPrefixLength = 4;

        public DateTime? LastCleanupTime { get; set; }
        /// <summary>首次启动时间（延迟展示依据，如联系方式满 3 天才显示）。仅首次运行时写入。</summary>
        public DateTime? FirstLaunchTime { get; set; }
        /// <summary>桌面受管快捷方式的文件名前缀，添加指令时自动加上。空/非法时回退默认值。</summary>
        public string ShortcutPrefix { get; set; } = DefaultShortcutPrefix;
    }

    /// <summary>config.json 根对象（文档第 6 节）。路径不写入配置，运行时按 exe 目录推导。</summary>
    public class AppConfig
    {
        public List<Rule> GlobalRules { get; set; } = new();
        public List<ProgramEntry> Programs { get; set; } = new();
        public List<string> PendingCleanup { get; set; } = new();
        public Settings Settings { get; set; } = new();

        /// <summary>程序 ID → 程序记录的索引，FindProgram 用。惰性重建：Programs 数量变化时重建。</summary>
        private Dictionary<string, ProgramEntry> _programIndex;
        private int _programIndexCount = -1;

        private void EnsureProgramIndex()
        {
            if (_programIndex == null || _programIndexCount != Programs.Count)
            {
                _programIndex = new Dictionary<string, ProgramEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Programs)
                    _programIndex[p.Id] = p; // 重复 ID 时后者覆盖前者，与原 Find 返回首项行为接近
                _programIndexCount = Programs.Count;
            }
        }

        public ProgramEntry FindProgram(string id)
        {
            EnsureProgramIndex();
            return _programIndex.TryGetValue(id, out var p) ? p : null;
        }
    }

    /// <summary>payload\version.json（文档 3.3 / 5.4）</summary>
    public class PayloadVersion
    {
        [JsonPropertyName("hookshot")] public string Hookshot { get; set; } = "";
        [JsonPropertyName("pathwinder")] public string Pathwinder { get; set; } = "";
    }

    public static class JsonOpts
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
