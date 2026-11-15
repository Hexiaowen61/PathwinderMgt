using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PathwinderMgt.Services
{
    /// <summary>
    /// 浅色 / 深色主题切换：主题字典位于 Themes\Light.xaml 与 Themes\Dark.xaml，
    /// 两个文件的键名完全一致，切换时整体替换 Application.Resources 中的主题字典。
    /// 界面里所有颜色都必须通过 {DynamicResource 键名} 引用，才能即时生效。
    /// </summary>
    public static class ThemeManager
    {
        public const string Light = "Light";
        public const string Dark = "Dark";

        /// <summary>当前生效的主题（默认浅色）。</summary>
        public static string Current { get; private set; } = Light;

        /// <summary>主题切换后触发：供自绘颜色的界面（如主列表状态标签）重新取色。</summary>
        public static event Action ThemeChanged;

        /// <summary>把任意配置值规范化为 Light / Dark，非法值按浅色处理。</summary>
        public static string Normalize(string theme) =>
            string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

        public static bool IsDark => Current == Dark;

        /// <summary>应用主题（可重复调用）。只替换主题字典，其余资源保持不变。</summary>
        public static void Apply(string theme)
        {
            string name = Normalize(theme);
            var app = Application.Current;
            if (app == null) return;

            var merged = app.Resources.MergedDictionaries;
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute)
            };

            // 先移除旧主题字典，再把新字典追加到末尾。
            // 注意：合并字典中「后加入的优先」，追加才能覆盖；用 Contains("WindowBg") 判定
            // 比匹配 Source 字符串可靠（相对 URI 是 "Themes/Light.xaml"，不含前导斜杠）。
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Contains("WindowBg"))
                    merged.RemoveAt(i);
            }
            merged.Add(dict);

            Current = name;

            // 标题栏跟随主题（Win10 2004+ 生效，旧系统调用失败时忽略）
            if (app.Windows != null)
                foreach (Window w in app.Windows)
                    ApplyTitleBar(w);

            ThemeChanged?.Invoke();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DwmwaUseImmersiveDarkMode = 20;      // Win10 2004+
        private const int DwmwaUseImmersiveDarkModeOld = 19;   // 早期 Win10

        /// <summary>让系统标题栏跟随深色主题，避免深色内容配白色标题栏。</summary>
        public static void ApplyTitleBar(Window window)
        {
            if (window == null) return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref dark, sizeof(int));
            }
            catch { /* 系统不支持时保持默认标题栏 */ }
        }

        /// <summary>按令牌取画笔；令牌缺失时返回灰色，避免界面因取色失败崩溃。</summary>
        public static Brush Brush(string key) =>
            Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        /// <summary>按令牌取颜色字符串（供绑定到 Brush/Background 的字符串属性使用）。</summary>
        public static string Color(string key)
        {
            Brush brush = Brush(key);
            return brush is SolidColorBrush solid ? solid.Color.ToString() : "#FF808080";
        }
    }
}
