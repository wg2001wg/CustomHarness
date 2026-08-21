using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarness.Core.Tools.Builtin;

/// <summary>Shell 工具(bash / pwsh,对齐参考项目 bash/pwsh 工具)。</summary>
public sealed class ShellTool : ITool
{
    private readonly string _shell;
    private readonly string _shellArgsPrefix;

    public ShellTool(string shell = "auto")
    {
        _shell = shell;
        _shellArgsPrefix = shell switch
        {
            "bash" => "-lc",
            "pwsh" => "-NoProfile -Command",
            "powershell" => "-NoProfile -Command",
            "cmd" => "/c",
            _ => DetectDefault(),
        };
    }

    private static string DetectDefault()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("COMSPEC") != null ? "-NoProfile -Command" : "/c";
        return "-lc";
    }

    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var pwsh = Environment.GetEnvironmentVariable("ProgramFiles") + "\\PowerShell\\7\\pwsh.exe";
            return File.Exists(pwsh) ? pwsh : (Environment.GetEnvironmentVariable("COMSPEC") ?? "powershell.exe");
        }
        return "/bin/bash";
    }

    public ToolDefinition Definition => new()
    {
        Name = "bash",
        Description = "执行 shell 命令并返回 stdout/stderr 与退出码。Windows 上等价于 pwsh/PowerShell。适合文件操作、运行脚本、git、构建等。参数 command 为要执行的命令字符串,workdir 为可选工作目录。",
        RequiresApproval = false,
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "要执行的 shell 命令" },
                ["workdir"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "可选,工作目录" },
            },
            ["required"] = new[] { "command" },
        },
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var command = args.TryGetProperty("command", out var c) ? c.GetString() : null;
        if (string.IsNullOrEmpty(command))
            return ToolResult.Fail("缺少参数: command");

        var workdir = args.TryGetProperty("workdir", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetString()!
            : ctx.WorkingDirectory;

        if (string.IsNullOrEmpty(workdir) || !Directory.Exists(workdir))
            workdir = ctx.WorkingDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = DefaultShell(),
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(_shellArgsPrefix);
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            if (!proc.Start()) return ToolResult.Fail("无法启动 shell 进程");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ctx.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            return ToolResult.Fail("命令执行被取消");
        }

        var outText = stdout.ToString().TrimEnd();
        var errText = stderr.ToString().TrimEnd();
        var meta = new Dictionary<string, object?>
        {
            ["exit_code"] = proc.ExitCode,
            ["shell"] = psi.FileName,
            ["workdir"] = workdir,
        };

        var combined = new StringBuilder();
        if (outText.Length > 0) combined.AppendLine(outText);
        if (errText.Length > 0) combined.AppendLine("[stderr]").AppendLine(errText);
        combined.Append($"[exit code: {proc.ExitCode}]");

        return new ToolResult
        {
            Output = combined.ToString().TrimEnd(),
            IsError = proc.ExitCode != 0,
            Error = proc.ExitCode != 0 ? (errText.Length > 0 ? errText : $"exit code {proc.ExitCode}") : null,
            Meta = meta,
        };
    }
}
