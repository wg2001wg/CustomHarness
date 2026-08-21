using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>
/// Markdown → FlowDocument 渲染器(基于 Markdig AST,支持标题/粗体/斜体/行内代码/代码块/列表/引用)。
/// 用于消息流中助手消息的富文本渲染。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .Build();

    public static FlowDocument ToFlowDocument(string markdown, double fontSize = 13)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            LineHeight = 1.35,
            PagePadding = new Thickness(0),
        };

        var root = Markdown.Parse(markdown ?? "", Pipeline);
        var paragraph = new Paragraph();
        foreach (var block in root)
            RenderBlock(block, doc, paragraph);
        if (paragraph.Inlines.Count > 0)
            doc.Blocks.Add(paragraph);

        return doc;
    }

    private static void RenderBlock(MdBlock block, FlowDocument doc, Paragraph paragraph)
    {
        switch (block)
        {
            case HeadingBlock heading:
                Flush(paragraph, doc);
                var hp = new Paragraph
                {
                    FontSize = heading.Level == 1 ? 20 : heading.Level == 2 ? 17 : 15,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 10, 0, 4),
                };
                if (heading.Inline != null)
                    foreach (var inline in heading.Inline)
                        hp.Inlines.Add(RenderInline(inline));
                doc.Blocks.Add(hp);
                break;

            case ParagraphBlock para:
            {
                if (para.Inline != null)
                {
                    foreach (var inline in para.Inline)
                        paragraph.Inlines.Add(RenderInline(inline));
                    paragraph.Inlines.Add(new LineBreak());
                }
                break;
            }

            case FencedCodeBlock code:
            {
                Flush(paragraph, doc);
                var codeText = code.Lines.ToString();
                var cp = new Paragraph
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5,
                    Background = new SolidColorBrush(Color.FromRgb(245, 246, 250)),
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 6, 0, 6),
                };
                cp.Inlines.Add(new Run(codeText.TrimEnd('\n'))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(40, 44, 60)),
                });
                doc.Blocks.Add(cp);
                break;
            }

            case QuoteBlock quote:
            {
                Flush(paragraph, doc);
                var qp = new Paragraph
                {
                    Background = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(180, 190, 210)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 6, 0, 6),
                };
                foreach (var child in quote)
                {
                    if (child is ParagraphBlock qchild && qchild.Inline != null)
                        foreach (var inline in qchild.Inline)
                            qp.Inlines.Add(RenderInline(inline));
                }
                doc.Blocks.Add(qp);
                break;
            }

            case ListBlock list:
            {
                Flush(paragraph, doc);
                var lp = new Paragraph { Margin = new Thickness(12, 4, 0, 4) };
                var index = 1;
                foreach (var item in list)
                {
                    if (item is ListItemBlock li)
                    {
                        var bullet = list.IsOrdered ? $"{index++}. " : "• ";
                        lp.Inlines.Add(new Run(bullet) { Foreground = new SolidColorBrush(Color.FromRgb(65, 118, 230)) });
                        foreach (var child in li)
                        {
                            if (child is ParagraphBlock lpChild && lpChild.Inline != null)
                                foreach (var inline in lpChild.Inline)
                                    lp.Inlines.Add(RenderInline(inline));
                        }
                        lp.Inlines.Add(new LineBreak());
                    }
                }
                doc.Blocks.Add(lp);
                break;
            }

            case ThematicBreakBlock:
                Flush(paragraph, doc);
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 232)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 8),
                });
                break;

            case HtmlBlock html:
                // 简单提取文本
                Flush(paragraph, doc);
                doc.Blocks.Add(new Paragraph(new Run(html.ToHtmlText())) { Margin = new Thickness(0, 4, 0, 4) });
                break;

            default:
                Flush(paragraph, doc);
                doc.Blocks.Add(new Paragraph(new Run(block.ToHtmlText())) { Margin = new Thickness(0, 4, 0, 4) });
                break;
        }
    }

    private static void Flush(Paragraph paragraph, FlowDocument doc)
    {
        if (paragraph.Inlines.Count == 0) return;
        doc.Blocks.Add(paragraph);
        paragraph.Inlines.Clear();
    }

    /// <summary>简单 HTML/块 → 纯文本。</summary>
    private static string ToHtmlText(this MdBlock block)
    {
        if (block is Markdig.Syntax.LeafBlock leaf && leaf.Inline != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var inline in leaf.Inline)
                sb.Append(inline.ToString());
            return sb.ToString().Trim();
        }
        return block.ToString()?.Trim() ?? "";
    }

    private static System.Windows.Documents.Inline RenderInline(MdInline inline) => inline switch
    {
        LiteralInline literal => new Run(literal.Content.ToString()),
        CodeInline code => new Run(code.Content)
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(235, 238, 244)),
            Foreground = new SolidColorBrush(Color.FromRgb(190, 60, 90)),
        },
        EmphasisInline emphasis => new Run(emphasis.ToString())
        {
            FontWeight = FontWeights.Bold,
        },
        LinkInline link => new Run(link.Url ?? "")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(65, 118, 230)),
            TextDecorations = TextDecorations.Underline,
        },
        LineBreakInline => new LineBreak(),
        _ => new Run(inline.ToString()),
    };
}
