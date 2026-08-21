using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekHarness.App;

/// <summary>应用入口:全局异常处理 + 启动。</summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "dsh-app-errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常捕获,写入日志便于诊断
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception.ToString());
            MessageBox.Show($"发生未处理异常:\n{args.Exception.Message}\n\n详情已写入 {LogPath}",
                "DeepSeek Harness", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log(ex.ToString());
        };
        base.OnStartup(e);
    }

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch { /* 忽略 */ }
    }
}
