using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PathwinderMgt.Services
{
    /// <summary>Markdown 块类型。</summary>
    public enum MarkdownBlockKind
    {
        /// <summary>普通段落。</summary>
        Paragraph,
        /// <summary>标题（<see cref="MarkdownBlock.Level"/> 为 1–6）。</summary>
        Heading,
        /// <summary>无序列表项。</summary>
        Bullet,
        /// <summary>有序列表项（<see cref="MarkdownBlock.Level"/> 为序号）。</summary>
        Ordered
    }

    /// <summary>解析后的单个 Markdown 块（不含内联样式，便于移植或自定义渲染）。</summary>
    public sealed class MarkdownBlock
    {
        public MarkdownBlockKind Kind { get; set; }
        public int Level { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>渲染配色与字号；移植时可整体替换。</summary>
    public sealed class MarkdownTheme
    {
        public string FontFamily { get; set; } = "Microsoft YaHei";
        public string CodeFontFamily { get; set; } = "Consolas";
        public double FontSize { get; set; } = 12;
        public string TextColor { get; set; } = "#374151";
        public string HeadingColor { get; set; } = "#111827";
        public string CodeBackground { get; set; } = "#EEF2F7";
        public string CodeColor { get; set; } = "#1F2937";

        /// <summary>浅色主题配色（默认值）。</summary>
        public static MarkdownTheme Light { get; } = new MarkdownTheme();

        /// <summary>深色主题配色：正文/标题取浅色，行内代码块用深底浅字。</summary>
        public static MarkdownTheme Dark { get; } = new MarkdownTheme
        {
            TextColor = "#C2C6CC",
            HeadingColor = "#E8EAED",
            CodeBackground = "#33363B",
            CodeColor = "#E8EAED"
        };

        /// <summary>默认配色（浅色），保留给不关心主题的调用方。</summary>
        public static MarkdownTheme Default => Light;

        /// <summary>按界面当前主题（深色 / 浅色）选择配色。</summary>
        public static MarkdownTheme FromTheme(bool isDark) => isDark ? Dark : Light;
    }

    /// <summary>
    /// 极简 Markdown 渲染器：文本 → WPF FlowDocument。
    /// 仅依赖 WPF 自带类型，无第三方库，可整文件移植到其它 WPF 项目。
    /// 支持：# / ## / ### 标题、- 无序列表、1. 有序列表、空行分段、
    /// 缩进续行合并、**粗体**、*斜体*、`代码`。
    /// </summary>
    public static class MiniMarkdown
    {
        private static readonly Regex HeadingRx =
            new Regex(@"^\s{0,3}(#{1,6})\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex BulletRx =
            new Regex(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex OrderedRx =
            new Regex(@"^\s*(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);

        /// <summary>把 Markdown 文本解析为块列表（纯文本处理，不涉及 UI）。</summary>
        public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
        {
            var blocks = new List<MarkdownBlock>();
            if (string.IsNullOrEmpty(markdown)) return blocks;

            MarkdownBlock current = null;
            foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0)
                {
                    current = null;
                    continue;
                }

                var heading = HeadingRx.Match(line);
                if (heading.Success)
                {
                    blocks.Add(new MarkdownBlock
                    {
                        Kind = MarkdownBlockKind.Heading,
                        Level = heading.Groups[1].Length,
                        Text = heading.Groups[2].Value.Trim()
                    });
                    current = null; // 标题不与后续行合并
                    continue;
                }

                var bullet = BulletRx.Match(line);
                if (bullet.Success)
                {
                    current = new MarkdownBlock
                    {
                        Kind = MarkdownBlockKind.Bullet,
                        Text = bullet.Groups[1].Value.Trim()
                    };
                    blocks.Add(current);
                    continue;
                }

                var ordered = OrderedRx.Match(line);
                if (ordered.Success)
                {
                    current = new MarkdownBlock
                    {
                        Kind = MarkdownBlockKind.Ordered,
                        Level = int.Parse(ordered.Groups[1].Value),
                        Text = ordered.Groups[2].Value.Trim()
                    };
                    blocks.Add(current);
                    continue;
                }

                // 其余行：空行后的第一行开启新段落；缩进续行/同段续行追加到当前块
                if (current == null)
                {
                    current = new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph, Text = line.Trim() };
                    blocks.Add(current);
                }
                else
                {
                    current.Text = current.Text + " " + line.Trim();
                }
            }

            return blocks;
        }

        /// <summary>把 Markdown 文本渲染为 FlowDocument（可直接赋给 FlowDocumentScrollViewer / RichTextBox）。</summary>
        public static FlowDocument ToFlowDocument(string markdown, MarkdownTheme theme = null)
        {
            theme ??= MarkdownTheme.Default;
            var document = new FlowDocument
            {
                FontFamily = new FontFamily(theme.FontFamily),
                FontSize = theme.FontSize,
                Foreground = Brush(theme.TextColor),
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                TextAlignment = TextAlignment.Left
            };

            foreach (var block in Parse(markdown))
                document.Blocks.Add(BuildParagraph(block, theme));

            return document;
        }

        private static Paragraph BuildParagraph(MarkdownBlock block, MarkdownTheme theme)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    int level = Math.Max(1, Math.Min(6, block.Level));
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Foreground = Brush(theme.HeadingColor);
                    paragraph.FontSize = level switch
                    {
                        1 => theme.FontSize + 6,
                        2 => theme.FontSize + 3,
                        3 => theme.FontSize + 1.5,
                        _ => theme.FontSize
                    };
                    paragraph.Margin = new Thickness(0, level <= 1 ? 0 : 10, 0, 4);
                    AppendInline(paragraph, block.Text, theme);
                    break;

                case MarkdownBlockKind.Bullet:
                    paragraph.Margin = new Thickness(14, 2, 0, 2);
                    paragraph.TextIndent = -12;
                    paragraph.Inlines.Add(new Run("•  "));
                    AppendInline(paragraph, block.Text, theme);
                    break;

                case MarkdownBlockKind.Ordered:
                    paragraph.Margin = new Thickness(14, 2, 0, 2);
                    paragraph.TextIndent = -12;
                    paragraph.Inlines.Add(new Run(block.Level + ".  "));
                    AppendInline(paragraph, block.Text, theme);
                    break;

                default:
                    AppendInline(paragraph, block.Text, theme);
                    break;
            }

            return paragraph;
        }

        /// <summary>处理 **粗体**、*斜体*、`代码` 三种内联标记。</summary>
        private static void AppendInline(Paragraph paragraph, string text, MarkdownTheme theme)
        {
            var plain = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        Flush();
                        paragraph.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = new FontFamily(theme.CodeFontFamily),
                            Background = Brush(theme.CodeBackground),
                            Foreground = Brush(theme.CodeColor)
                        });
                        i = end + 1;
                        continue;
                    }
                }
                else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        Flush();
                        paragraph.Inlines.Add(new Run(text.Substring(i + 2, end - i - 2))
                        {
                            FontWeight = FontWeights.SemiBold
                        });
                        i = end + 2;
                        continue;
                    }
                }
                else if (c == '*')
                {
                    int end = text.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        Flush();
                        paragraph.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontStyle = FontStyles.Italic
                        });
                        i = end + 1;
                        continue;
                    }
                }

                plain.Append(c);
                i++;
            }

            Flush();

            void Flush()
            {
                if (plain.Length == 0) return;
                paragraph.Inlines.Add(new Run(plain.ToString()));
                plain.Clear();
            }
        }

        private static Brush Brush(string color)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
            catch { return System.Windows.Media.Brushes.Black; }
        }
    }
}
