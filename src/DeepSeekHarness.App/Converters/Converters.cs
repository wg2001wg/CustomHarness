using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DeepSeekHarness.App.ViewModels;
using DeepSeekHarness.Core.Tools;

namespace DeepSeekHarness.App.Converters;

/// <summary>消息角色 → 对齐方向/气泡样式。</summary>
public sealed class MessageKindToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageKind.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MessageKindToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBg = new(Color.FromRgb(65, 118, 230));
    private static readonly SolidColorBrush AssistantBg = new(Color.FromRgb(244, 246, 250));
    private static readonly SolidColorBrush SystemBg = new(Color.FromRgb(255, 248, 230));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            MessageKind.User => UserBg,
            MessageKind.System => SystemBg,
            _ => AssistantBg,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MessageKindToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush Dark = new(Color.FromRgb(40, 44, 60));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageKind.User ? White : Dark;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>工具状态 → 状态颜色。</summary>
public sealed class ToolStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = new(Color.FromRgb(65, 118, 230));
    private static readonly SolidColorBrush Done = new(Color.FromRgb(52, 168, 83));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(217, 48, 37));
    private static readonly SolidColorBrush Blocked = new(Color.FromRgb(249, 168, 37));
    private static readonly SolidColorBrush Pending = new(Color.FromRgb(154, 160, 176));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            "running" => Running,
            "done" => Done,
            "error" => Error,
            "blocked" => Blocked,
            _ => Pending,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ToolStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            "running" => "运行中",
            "done" => "完成",
            "error" => "失败",
            "blocked" => "已阻止",
            "pending" => "等待",
            _ => value,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>空字符串 → 折叠。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var collapse = parameter as string == "invert";
        var isEmpty = string.IsNullOrEmpty(value as string);
        var visible = collapse ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>MessageKind → 是否用户消息。</summary>
public sealed class KindIsUserConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageKind.User;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>MessageKind → 是否助手消息。</summary>
public sealed class KindIsAssistantConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageKind.Assistant;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>MessageKind → 是否系统消息。</summary>
public sealed class KindIsSystemConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageKind.System;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → GridLength(用于侧栏折叠:280 或 0)。</summary>
public sealed class SidebarWidthConverter : IValueConverter
{
    private static readonly GridLength Expanded = new(280);
    private static readonly GridLength Collapsed = new(0);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Collapsed : Expanded;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>设置页 Tab 名 → 可见性(参数为匹配的 Tab 名)。</summary>
public sealed class TabVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
