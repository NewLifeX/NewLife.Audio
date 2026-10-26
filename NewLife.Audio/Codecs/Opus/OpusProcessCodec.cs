using System.Diagnostics;
using System.Globalization;
using NewLife.Audio.Containers;
using NewLife.Audio.Writers;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Opus 外部进程编解码器（操作系统能力降级方案）</summary>
/// <remarks>
/// 在纯托管 SILK/CELT 实现完成之前，使用系统 ffmpeg（libopus）完成标准 Opus 编解码。<br/>
/// 该实现不引入任何 NuGet 依赖与 P/Invoke，仅通过标准输入输出与子进程交互，Windows/Linux/macOS 通用。<br/>
/// 查找顺序：环境变量 <c>OPUS_FFMPEG</c> 指定的可执行文件 → <c>PATH</c> 中的 <c>ffmpeg</c>。<br/>
/// 进程回退属于过渡能力，性能低于进程内编解码；实时场景应等待 M2~M5 的托管实现。
/// </remarks>
public static class OpusProcessCodec
{
    private static readonly Object _sync = new();
    private static volatile Boolean _probed;
    private static String _ffmpegPath;
    private static Boolean _hasLibOpus;

    /// <summary>是否找到可用的 ffmpeg + libopus</summary>
    public static Boolean IsAvailable
    {
        get
        {
            EnsureProbe();
            return _hasLibOpus;
        }
    }

    /// <summary>当前使用的 ffmpeg 可执行文件路径；不可用时为 null</summary>
    public static String FFmpegPath
    {
        get
        {
            EnsureProbe();
            return _hasLibOpus ? _ffmpegPath : null;
        }
    }

    /// <summary>清除可用性探测缓存，便于切换 <c>OPUS_FFMPEG</c> 后重新探测</summary>
    public static void ResetAvailability()
    {
        lock (_sync)
        {
            _probed = false;
            _ffmpegPath = null;
            _hasLibOpus = false;
        }
    }

    /// <summary>解码单个 Opus 包为 16 位小端交错 PCM</summary>
    /// <param name="packet">Opus 包（TOC + payload）</param>
    /// <param name="options">输出采样率与声道数</param>
    /// <returns>PCM 数据；包为空时返回空数组</returns>
    /// <exception cref="NotSupportedException">未找到可用的 ffmpeg + libopus 时抛出</exception>
    /// <exception cref="InvalidOperationException">ffmpeg 解码失败时抛出</exception>
    public static Byte[] DecodePacket(ReadOnlySpan<Byte> packet, OpusOptions options)
    {
        if (packet.Length == 0) return [];
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();
        EnsureAvailable();

        if (!OpusPacket.TryParse(packet, out var parsed)) throw new ArgumentException(L.T("Not a valid Opus packet."), nameof(packet));

        // 单包封装为最小 Ogg Opus 流，交给 ffmpeg 解码；pre-skip 取 0，保持“一包进、整包样本出”的语义
        using var input = new MemoryStream();
        var writer = new OggOpusStreamWriter("NewLife.Audio", parsed.Channels, OpusPacket.SampleRate, 0);
        writer.WriteHeaderAsync(input).GetAwaiter().GetResult();
        writer.WriteChunkAsync(input, packet.ToArray()).GetAwaiter().GetResult();
        writer.WriteTrailerAsync(input).GetAwaiter().GetResult();

        var arguments = $"-hide_banner -loglevel error -i pipe:0 -vn -f s16le -acodec pcm_s16le -ar {options.SampleRate} -ac {options.Channels} pipe:1";
        return Run(arguments, input.ToArray());
    }

    /// <summary>编码一帧 16 位小端交错 PCM 为单个 Opus 包</summary>
    /// <param name="pcm">PCM 数据，长度应为 <see cref="OpusOptions.FrameBytes"/></param>
    /// <param name="options">编码选项</param>
    /// <returns>Opus 包（TOC + payload）</returns>
    /// <exception cref="NotSupportedException">未找到可用的 ffmpeg + libopus 时抛出</exception>
    /// <exception cref="InvalidOperationException">ffmpeg 编码失败或未产生 Opus 包时抛出</exception>
    public static Byte[] EncodeFrame(ReadOnlySpan<Byte> pcm, OpusOptions options)
    {
        if (pcm.Length == 0) return [];
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();
        EnsureAvailable();

        var arguments = BuildEncodeArguments(options);
        var ogg = Run(arguments, pcm.ToArray());
        if (ogg.Length == 0) throw new InvalidOperationException(L.T("ffmpeg produced no Opus data."));

        using var stream = new MemoryStream(ogg);
        using var reader = new OggFileReader(stream);
        var frame = reader.ReadFrame();
        if (frame == null || frame.Total == 0) throw new InvalidOperationException(L.T("ffmpeg produced no Opus packet."));

        return frame.ToArray();
    }

    private static String BuildEncodeArguments(OpusOptions options)
    {
        var duration = ((Int32)options.FrameDuration / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var application = options.Application switch
        {
            OpusApplication.Voip => "voip",
            OpusApplication.RestrictedLowDelay => "lowdelay",
            _ => "audio",
        };
        var vbr = options.UseConstrainedVbr ? "constrained" : options.UseVbr ? "on" : "off";

        var arguments = $"-hide_banner -loglevel error -f s16le -ar {options.SampleRate} -ac {options.Channels} -i pipe:0 " +
            $"-c:a libopus -b:a {options.Bitrate} -application {application} -frame_duration {duration} -vbr {vbr} " +
            $"-compression_level {options.Complexity}";
        if (options.PacketLossPercent > 0) arguments += $" -packet_loss {options.PacketLossPercent}";
        arguments += " -map_metadata -1 -f ogg pipe:1";

        return arguments;
    }

    private static Byte[] Run(String arguments, Byte[] input)
    {
        var info = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException(L.T("Failed to start ffmpeg."));

        using var output = new MemoryStream();
        var writeTask = Task.Run(() =>
        {
            try
            {
                process.StandardInput.BaseStream.Write(input, 0, input.Length);
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
            }
        });
        var readTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            Task.WaitAll(writeTask, readTask, errorTask);
            process.WaitForExit();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = errorTask.Result?.Trim();
            throw new InvalidOperationException(L.T("ffmpeg exited with code {0}: {1}", process.ExitCode, error));
        }

        return output.ToArray();
    }

    private static void EnsureAvailable()
    {
        EnsureProbe();
        if (!_hasLibOpus)
            throw new NotSupportedException(L.T("Opus managed codec is not complete yet (M2-M5), and no ffmpeg with libopus was found. Install ffmpeg or set OPUS_FFMPEG to the ffmpeg executable."));
    }

    private static void EnsureProbe()
    {
        if (_probed) return;

        lock (_sync)
        {
            if (_probed) return;

            var path = FindFFmpeg();
            if (path != null && HasLibOpus(path))
            {
                _ffmpegPath = path;
                _hasLibOpus = true;
            }

            _probed = true;
        }
    }

    private static String FindFFmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("OPUS_FFMPEG");
        var resolved = ResolveExecutable(configured);
        if (resolved != null) return resolved;

        return ResolveExecutable("ffmpeg");
    }

    private static String ResolveExecutable(String name)
    {
        if (String.IsNullOrWhiteSpace(name)) return null;

        try
        {
            if (File.Exists(name)) return Path.GetFullPath(name);
        }
        catch
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (String.IsNullOrEmpty(path)) return null;

        var names = new List<String> { name };
        if (Path.DirectorySeparatorChar == '\\' && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) names.Add(name + ".exe");

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (String.IsNullOrWhiteSpace(dir)) continue;

            foreach (var candidate in names)
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full)) return full;
                }
                catch
                {
                    // 非法 PATH 项忽略
                }
            }
        }

        return null;
    }

    private static Boolean HasLibOpus(String path)
    {
        try
        {
            var encoders = RunProbe(path, "-hide_banner -loglevel error -encoders");
            if (encoders.IndexOf("libopus", StringComparison.OrdinalIgnoreCase) < 0) return false;

            var decoders = RunProbe(path, "-hide_banner -loglevel error -decoders");
            return decoders.IndexOf(" opus", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static String RunProbe(String path, String arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info);
        if (process == null) return String.Empty;

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return output;
    }
}
