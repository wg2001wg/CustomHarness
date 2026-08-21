using System.Windows;
using System.Windows.Controls;
using DeepSeekHarness.App.ViewModels;

namespace DeepSeekHarness.App.Controls;

/// <summary>
/// Markdown 富文本视图:绑定 Text,内部将 Markdown 渲染为 FlowDocument。
/// 用于助手消息的流式富文本展示(对齐参考项目 AssistantMarkdown)。
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownTextProperty = DependencyProperty.Register(
        nameof(MarkdownText), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", OnMarkdownTextChanged));

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public MarkdownView()
    {
        IsReadOnly = true;
        BorderThickness = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsDocumentEnabled = true;
        Focusable = false;
        Padding = new Thickness(0);
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        var text = e.NewValue as string ?? "";
        if (string.IsNullOrEmpty(text))
        {
            view.Document = new System.Windows.Documents.FlowDocument();
            return;
        }
        view.Document = MarkdownRenderer.ToFlowDocument(text, 13);
        view.Document.PagePadding = new Thickness(0);
    }
}
