using System.Diagnostics;
using System.Text;

namespace NewLife.Audio.Tts;

/// <summary>跨目标框架的平台判断</summary>
internal static class TtsPlatform
{
    /// <summary>是否 Windows</summary>
    public static Boolean IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>是否 macOS</summary>
    public static Boolean IsMacOS =>
        Environment.OSVersion.Platform == PlatformID.MacOSX ||
        (Environment.OSVersion.Platform == PlatformID.Unix && File.Exists("/usr/bin/say"));

    /// <summary>是否 Linux</summary>
    public static Boolean IsLinux => !IsWindows && !IsMacOS;
}

/// <summary>子进程工具。统一处理可执行文件查找、启动与取消</summary>
internal static class TtsProcess
{
    /// <summary>在 PATH 与常见目录里查找可执行文件</summary>
    /// <param name="fileName">文件名或路径</param>
    /// <returns>完整路径，找不到时返回 null</returns>
    public static String FindExecutable(String fileName)
    {
        if (fileName.IsNullOrEmpty()) return null;

        if (File.Exists(fileName)) return fileName;

        var names = new List<String> { fileName };
        if (Environment.OSVersion.Platform == PlatformID.Win32NT && !Path.HasExtension(fileName))
            names.Add(fileName + ".exe");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.IsNullOrEmpty()) continue;

            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    /// <summary>创建子进程启动参数</summary>
    /// <param name="fileName">可执行文件</param>
    /// <param name="arguments">命令行参数</param>
    /// <param name="redirectInput">是否重定向标准输入</param>
    /// <param name="workingDirectory">工作目录</param>
    /// <returns>进程启动参数</returns>
    public static ProcessStartInfo CreateStartInfo(String fileName, String arguments, Boolean redirectInput, String workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!workingDirectory.IsNullOrEmpty()) psi.WorkingDirectory = workingDirectory;

        return psi;
    }

    /// <summary>写入标准输入并关闭</summary>
    /// <param name="process">子进程</param>
    /// <param name="text">文本</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步任务</returns>
    public static async Task WriteInputAsync(Process process, String text, CancellationToken cancellationToken)
    {
        var input = Encoding.UTF8.GetBytes(text ?? "");
        await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    /// <summary>等待进程退出。取消时杀掉进程</summary>
    /// <param name="process">子进程</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>退出码</returns>
    public static async Task<Int32> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Kill(process));

        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return process.ExitCode;
    }

    /// <summary>安全结束进程</summary>
    /// <param name="process">子进程</param>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch
        {
            // 进程可能已退出，忽略
        }
    }

    /// <summary>给命令行参数加引号</summary>
    /// <param name="text">原始文本</param>
    /// <returns>加引号后的文本</returns>
    public static String Quote(String text)
    {
        var escaped = (text ?? "").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }
}
