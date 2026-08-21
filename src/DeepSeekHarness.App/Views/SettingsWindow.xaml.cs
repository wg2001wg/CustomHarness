using System.Windows;
using DeepSeekHarness.App.ViewModels;

namespace DeepSeekHarness.App.Views;

/// <summary>设置对话框(对齐参考项目 ui-settings)。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplyCommand.Execute(null);
        DialogResult = true;
        Close();
    }
}
