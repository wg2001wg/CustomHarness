using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeepSeekHarness.App.ViewModels;
using DeepSeekHarness.App.Views;

namespace DeepSeekHarness.App;

/// <summary>主窗口:三栏布局(Harness UI)。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // 启动时按当前显示器工作区自适应大小与位置(适配任意分辨率)
        FitToScreen();

        // 会话流自动滚动到底部
        _vm.Conversation.ScrollToBottomRequested += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageScroll.ScrollToEnd();
            });
        };

        // 打开设置对话框
        _vm.SettingsOpenRequested += () =>
        {
            var win = new SettingsWindow(_vm.Settings) { Owner = this };
            win.ShowDialog();
        };

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            MessageScroll.ScrollToEnd();
        };
    }

    /// <summary>按工作区(排除任务栏)适配窗口:宽 94% × 高 92%,居中。上限 1720×1080。</summary>
    private void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        var w = Math.Min(wa.Width * 0.94, 1720);
        var h = Math.Min(wa.Height * 0.92, 1080);
        Width = Math.Max(w, MinWidth);
        Height = Math.Max(h, MinHeight);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            if (_vm.Conversation.SendCommand.CanExecute(null))
                _vm.Conversation.SendCommand.Execute(null);
        }
    }
}
