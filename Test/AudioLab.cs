using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

namespace Test;

/// <summary>本机录音 → 多格式导出 → 逐个回放的一键听感验证</summary>
/// <remarks>
/// 一次跑完：列出设备 → 按回车开始 → 录制 → 导出全部支持格式 → 回读校验 → 逐个回放。<br/>
/// 用法：<c>dotnet run --project Test -- [录音秒数]</c>，默认 5 秒。<br/>
/// 单元测试能验证波形、时长与电平，却判断不了"听起来怎么样"，也没有麦克风和扬声器，因此这类验证放在控制台。
/// </remarks>
internal static class AudioLab
{
    private const Int32 DefaultSeconds = 5;
    private const Int32 SampleRate = 44100;

    #region 入口
    /// <summary>执行一次完整验证</summary>
    /// <param name="args">命令行参数，可传录音秒数</param>
    /// <returns>进程退出码，0 表示全部通过</returns>
    public static Int32 Run(String[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "?" or "/?")
        {
            Console.WriteLine("用法：Test [录音秒数]，默认 5 秒。");
            Console.WriteLine("流程：列出设备 → 按回车开始录音 → 导出各格式 → 回读校验 → 逐个回放。");
            Console.WriteLine("Opus 需要 ffmpeg(libopus)：依次查找 OPUS_FFMPEG、程序目录、PATH 与本机常见安装位置。");
            return 0;
        }

        var seconds = args.Length > 0 && Int32.TryParse(args[0], out var value) && value > 0 ? Math.Min(value, 60) : DefaultSeconds;
        var outDir = Path.Combine(AppContext.BaseDirectory, "RecordSamples");

        try
        {
            EnsureFFmpeg();

            var driver = AudioDriverFactory.Create();
            ListDevices(driver);
            WaitForEnter($"准备好后按回车开始录音（{seconds} 秒）");

            var pcm = Record(driver, seconds);
            var peak = GetPeak(pcm.Data);
            Console.WriteLine($"采集 {pcm.Data.Length:N0} 字节 / {pcm.Duration:F2} 秒，峰值 {peak}，RMS {GetRms(pcm.Data):F0}");

            // 没录到声音时比对时长/电平没有意义，直接提示而不是把每个格式都报成失败
            var silent = peak < 100;
            if (silent) Console.WriteLine("警告：几乎没有采集到声音，请检查麦克风是否被静音或占用；本次跳过回读校验");

            Directory.CreateDirectory(outDir);
            var saved = ExportAll(pcm, outDir, $"record-{DateTime.Now:yyyyMMdd-HHmmss}", out var failed);

            if (saved.Count > 0)
            {
                if (!silent)
                {
                    Console.WriteLine();
                    Console.WriteLine("== 回读校验（时长与电平与源一致即通过）==");
                    foreach (var item in saved)
                    {
                        if (Verify(pcm, item, out var message))
                            Console.WriteLine($"[PASS] {item.Spec.Name}：{message}");
                        else
                        {
                            failed++;
                            Console.WriteLine($"[FAIL] {item.Spec.Name}：{message}");
                        }
                    }
                }

                failed += PlayAll(driver, saved);
            }

            Console.WriteLine();
            Console.WriteLine(failed == 0 ? $"全部通过，文件在 {outDir}" : $"有 {failed} 项失败，文件在 {outDir}");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>等待用户按回车，输入被重定向时直接继续</summary>
    /// <param name="prompt">提示文字</param>
    private static void WaitForEnter(String prompt)
    {
        Console.WriteLine();
        Console.Write($"{prompt}……");
        if (Console.IsInputRedirected)
            Console.WriteLine("（检测到输入重定向，直接开始）");
        else
            Console.ReadLine();
    }

    /// <summary>ffmpeg 不在环境变量与 PATH 中时，尝试本机常见安装位置并设入进程环境</summary>
    /// <remarks>环境变量只在进程启动时读取一次，修改后需要重启终端/编辑器才生效；
    /// 这里直接改进程内变量，避免为了加 Opus 支持被迫重启开发环境。</remarks>
    private static void EnsureFFmpeg()
    {
        if (OpusCodec.IsAvailable) return;

        foreach (var candidate in GetFFmpegCandidates())
        {
            if (!File.Exists(candidate)) continue;

            Environment.SetEnvironmentVariable("OPUS_FFMPEG", candidate);
            OpusProcessCodec.ResetAvailability();
            if (OpusCodec.IsAvailable) return;

            Environment.SetEnvironmentVariable("OPUS_FFMPEG", null);
        }
    }

    /// <summary>枚举 ffmpeg 的常见安装位置</summary>
    /// <returns>候选可执行文件路径</returns>
    private static IEnumerable<String> GetFFmpegCandidates()
    {
        var name = Path.DirectorySeparatorChar == '\\' ? "ffmpeg.exe" : "ffmpeg";

        // 程序目录及向上若干层：覆盖仓库根的 tools/ffmpeg 与随应用分发的 ffmpeg 目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, name);
            yield return Path.Combine(dir.FullName, "ffmpeg", "bin", name);
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", name);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!String.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "Apps", "ffmpeg", name);
            yield return Path.Combine(profile, "ffmpeg", "bin", name);
            yield return Path.Combine(profile, "scoop", "shims", name);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!String.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", name);

        yield return Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\", "ffmpeg", "bin", name);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", name);
    }
    #endregion

    #region 设备
    /// <summary>列出音频设备</summary>
    /// <param name="driver">音频驱动</param>
    private static void ListDevices(IAudioDriver driver)
    {
        var devices = driver.EnumerateDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("没有枚举到音频设备");
            return;
        }

        Console.WriteLine("音频设备：");
        foreach (var device in devices)
        {
            Console.WriteLine($"  {(device.IsInput ? "输入" : "输出")} {(device.IsDefault ? "*" : " ")} [{device.Id}] {device.Name}");
        }
    }
    #endregion

    #region 录制
    /// <summary>录制默认麦克风的指定时长</summary>
    /// <param name="driver">音频驱动</param>
    /// <param name="seconds">录制时长（秒）</param>
    /// <returns>采集到的 PCM</returns>
    private static AudioPcm Record(IAudioDriver driver, Int32 seconds)
    {
        var input = driver.GetDefaultInputDevice();
        if (input == null) throw new InvalidOperationException("本机没有可用的录音设备");
        Console.WriteLine($"录音设备：{input.Name}");

        var format = new AudioFormat { SampleRate = SampleRate, Channels = 1, BitsPerSample = 16 };
        var target = (Int64)format.ByteRate * seconds;

        using var recorder = driver.CreateRecorder();
        var buffer = new MemoryStream();
        var sync = new Object();
        recorder.DataAvailable += (s, data) =>
        {
            lock (sync) buffer.Write(data, 0, data.Length);
        };

        recorder.Init(format, input.Id);
        recorder.StartRecording();
        Console.WriteLine("开始录音，请对着麦克风说话……");

        var watch = Stopwatch.StartNew();
        var reported = -1;
        try
        {
            while (true)
            {
                Int64 length;
                lock (sync) length = buffer.Length;

                if (length >= target) break;

                // 设备不给数据时也要能退出，避免把环境问题拖成挂起
                if (watch.Elapsed.TotalSeconds >= seconds + 5) break;

                var elapsed = (Int32)watch.Elapsed.TotalSeconds;
                if (elapsed != reported)
                {
                    reported = elapsed;
                    Console.Write($"\r录音中 {elapsed,2} / {seconds} 秒");
                }

                Thread.Sleep(20);
            }
        }
        finally
        {
            recorder.StopRecording();
            Console.WriteLine();
        }

        Byte[] data;
        lock (sync) data = buffer.ToArray();

        // 截到目标时长并对齐样本边界，避免尾部半样本使编码器报错
        var sampleBytes = format.Channels * format.BitsPerSample / 8;
        var size = Math.Min(data.Length, (Int32)target);
        size -= size % sampleBytes;
        if (size < data.Length) Array.Resize(ref data, size);

        if (data.Length == 0) throw new InvalidOperationException("没有采集到音频数据：请检查麦克风是否启用、是否被其他程序独占");

        return new AudioPcm { Format = format, Data = data };
    }
    #endregion

    #region 导出
    /// <summary>一种导出格式</summary>
    /// <param name="Name">显示名称</param>
    /// <param name="Extension">文件扩展名</param>
    /// <param name="Encode">编码委托，返回说明文字</param>
    private sealed record FormatSpec(String Name, String Extension, Func<AudioPcm, String, String> Encode);

    /// <summary>已导出的文件</summary>
    /// <param name="Spec">格式定义</param>
    /// <param name="Path">文件路径</param>
    /// <param name="Detail">说明文字</param>
    private sealed record SavedFile(FormatSpec Spec, String Path, String Detail);

    /// <summary>导出全部支持格式</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="outDir">输出目录</param>
    /// <param name="baseName">文件名（不含扩展名）</param>
    /// <param name="failed">失败个数</param>
    /// <returns>成功导出的文件</returns>
    private static List<SavedFile> ExportAll(AudioPcm pcm, String outDir, String baseName, out Int32 failed)
    {
        Console.WriteLine();
        Console.WriteLine("== 导出各格式 ==");
        if (OpusCodec.IsAvailable)
            Console.WriteLine($"[OK]   ffmpeg/libopus：{OpusProcessCodec.FFmpegPath}");
        else
            Console.WriteLine("[SKIP] Ogg Opus：未找到 ffmpeg/libopus，可用 OPUS_FFMPEG 环境变量或 PATH 提供 ffmpeg");

        var saved = new List<SavedFile>();
        failed = 0;
        foreach (var spec in GetSpecs())
        {
            var path = Path.Combine(outDir, baseName + spec.Extension);
            try
            {
                var detail = spec.Encode(pcm, path);
                saved.Add(new SavedFile(spec, path, detail));
                Console.WriteLine($"[OK]   {Path.GetFileName(path),-30} {new FileInfo(path).Length,10:N0} 字节  {detail}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {spec.Name}：{ex}");
            }
        }

        return saved;
    }

    /// <summary>获取全部导出格式</summary>
    /// <returns>格式列表</returns>
    private static IReadOnlyList<FormatSpec> GetSpecs()
    {
        var list = new List<FormatSpec>
        {
            new("WAV（PCM 16-bit）", ".wav", EncodeWav),
            new("FLAC（无损，压缩级 5）", ".flac", EncodeFlac),
            new("MP3（128 kbps）", ".mp3", EncodeMp3),
            new("AIFF（PCM 16-bit 大端）", ".aiff", EncodeAiff),
            new("OGG Vorbis（质量 5）", ".ogg", EncodeVorbis),
            new("M4A（AAC-LC 64 kbps）", ".m4a", EncodeM4a),
            new("AAC ADTS（64 kbps）", ".aac", EncodeAdts),
        };

        // Opus 依赖 ffmpeg(libopus) 进程回退，环境不具备时不参与对比
        if (OpusCodec.IsAvailable) list.Add(new("Ogg Opus（64 kbps）", ".opus", EncodeOpus));

        return list;
    }

    /// <summary>WAV：直写 PCM</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeWav(AudioPcm pcm, String path)
    {
        AudioFile.WritePcm(path, pcm, new AudioWriteOptions { Metadata = CreateMetadata() });
        return "16-bit PCM";
    }

    /// <summary>FLAC：无损压缩</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeFlac(AudioPcm pcm, String path)
    {
        AudioFile.WritePcm(path, pcm, new AudioWriteOptions { FlacCompressionLevel = 5, Metadata = CreateMetadata() });
        return "无损，压缩级 5";
    }

    /// <summary>MP3：托管编码器</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeMp3(AudioPcm pcm, String path)
    {
        AudioFile.WritePcm(path, pcm, new AudioWriteOptions { Mp3BitrateKbps = 128, Metadata = CreateMetadata() });
        return "128 kbps";
    }

    /// <summary>AIFF：大端 PCM</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeAiff(AudioPcm pcm, String path)
    {
        AudioFile.WritePcm(path, pcm);
        return "16-bit PCM（大端）";
    }

    /// <summary>OGG Vorbis：纯托管编码器</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeVorbis(AudioPcm pcm, String path)
    {
        var format = pcm.Format;
        var options = new VorbisOptions { SampleRate = format.SampleRate, Channels = format.Channels, Quality = 5 };

        // 末块不足一帧时补零，避免编码器复用上一块残留样本造成尾部杂音
        var frameBytes = format.Channels * format.BytesPerSample;
        var blockBytes = options.BlockSize1 * frameBytes;
        var remainder = pcm.Data.Length % blockBytes;
        var data = pcm.Data;
        if (remainder != 0)
        {
            data = new Byte[pcm.Data.Length + blockBytes - remainder];
            Buffer.BlockCopy(pcm.Data, 0, data, 0, pcm.Data.Length);
        }

        using var codec = new VorbisCodec();
        var encoded = codec.FromPcm(data, options);

        File.WriteAllBytes(path, encoded.GetSpan().ToArray());
        return $"质量 5，{encoded.Total:N0} 字节";
    }

    /// <summary>M4A：AAC-LC 编码后封装为 ISO BMFF</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeM4a(AudioPcm pcm, String path)
    {
        var format = new AudioFormat
        {
            SampleRate = pcm.Format.SampleRate,
            Channels = pcm.Format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.AAC,
            SamplesPerFrame = 1024,
        };
        var options = new AacEncoderOptions
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Bitrate = 64000,
            Adts = false,
        };

        var blockBytes = 1024 * format.Channels * 2;
        var codec = new AacCodec();
        using var stream = File.Create(path);
        using var writer = new Mp4FileWriter(stream, format);
        writer.WriteMetadata(CreateMetadata());

        var frames = 0;
        for (var offset = 0; offset < pcm.Data.Length; offset += blockBytes)
        {
            var block = new Byte[blockBytes];
            var count = Math.Min(blockBytes, pcm.Data.Length - offset);
            Buffer.BlockCopy(pcm.Data, offset, block, 0, count);

            var frame = codec.FromPcm(block, options).GetSpan().ToArray();
            if (frame.Length == 0) continue;

            writer.WriteFrame(frame);
            frames++;
        }
        writer.Flush();

        return $"{frames} 帧 raw AAC";
    }

    /// <summary>AAC ADTS：裸码流</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeAdts(AudioPcm pcm, String path)
    {
        var options = new AacEncoderOptions
        {
            SampleRate = pcm.Format.SampleRate,
            Channels = pcm.Format.Channels,
            Bitrate = 64000,
            Adts = true,
        };

        var codec = new AacCodec();
        var head = codec.FromPcm(pcm.Data, options).GetSpan().ToArray();

        // 冲刷不足 1024 样本的尾部
        var tail = codec.FromPcm(ReadOnlySpan<Byte>.Empty, options).GetSpan().ToArray();

        using var stream = File.Create(path);
        stream.Write(head, 0, head.Length);
        stream.Write(tail, 0, tail.Length);

        return $"{head.Length + tail.Length:N0} 字节 ADTS";
    }

    /// <summary>Ogg Opus：ffmpeg(libopus) 编码后封装为 Ogg</summary>
    /// <param name="pcm">源 PCM</param>
    /// <param name="path">目标路径</param>
    /// <returns>说明文字</returns>
    private static String EncodeOpus(AudioPcm pcm, String path)
    {
        const Int32 OpusRate = 48000;

        var format = pcm.Format;
        var resampled = format.SampleRate != OpusRate;
        var data = resampled ? Resampler.ResamplePcm(pcm.Data, format.SampleRate, OpusRate) : pcm.Data;

        var options = new OpusOptions { SampleRate = OpusRate, Channels = format.Channels, Bitrate = 64000 };
        using var codec = new OpusCodec();
        var packets = codec.Encode(data, options, true);

        var opusFormat = new AudioFormat
        {
            SampleRate = OpusRate,
            Channels = format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.Opus,
            SamplesPerFrame = options.SamplesPerFrame,
        };

        using var stream = File.Create(path);
        using var writer = new OggFileWriter(stream, opusFormat, AVTypes.Opus)
        {
            PreSkip = OpusCodec.GetLookahead(options),
        };
        writer.WriteMetadata(CreateMetadata());
        foreach (var packet in packets) writer.WriteFrame(packet.GetSpan());
        writer.Flush();

        return resampled ? $"{packets.Count} 个包（重采样到 48 kHz）" : $"{packets.Count} 个包";
    }

    /// <summary>构造导出用标签</summary>
    /// <returns>音频元数据</returns>
    private static AudioMetadata CreateMetadata() => new()
    {
        Title = "NewLife.Audio 录音测试",
        Artist = "NewLife.Audio",
        Album = "录制格式对比",
        Comment = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
    };
    #endregion

    #region 校验
    /// <summary>回读文件并与源 PCM 比对时长与电平</summary>
    /// <param name="source">源 PCM</param>
    /// <param name="item">已导出的文件</param>
    /// <param name="message">校验结论</param>
    /// <returns>是否通过</returns>
    private static Boolean Verify(AudioPcm source, SavedFile item, out String message)
    {
        var info = AudioFile.Detect(item.Path);
        var decoded = AudioFile.ReadPcm(item.Path);

        var expected = source.Duration;
        var actual = decoded.Duration;
        var tolerance = Math.Max(0.15, expected * 0.05);
        if (Math.Abs(actual - expected) > tolerance)
        {
            message = $"时长偏差过大：源 {expected:F2}s，回读 {actual:F2}s";
            return false;
        }

        var sourceRms = GetRms(source.Data);
        var decodedRms = GetRms(decoded.Data);
        if (decodedRms < 1)
        {
            message = "回读结果为静音";
            return false;
        }

        var ratio = sourceRms > 0 ? decodedRms / sourceRms : 0;
        if (ratio is < 0.5 or > 2.0)
        {
            message = $"电平偏差过大：源 RMS {sourceRms:F0}，回读 {decodedRms:F0}";
            return false;
        }

        message = $"{info.Container}/{info.Codec}，{decoded.Format.SampleRate} Hz / {decoded.Format.Channels} ch / {actual:F2}s，RMS {decodedRms:F0}";
        return true;
    }
    #endregion

    #region 回放
    /// <summary>按导出顺序逐个回放</summary>
    /// <param name="driver">音频驱动</param>
    /// <param name="files">已导出的文件</param>
    /// <returns>失败个数</returns>
    private static Int32 PlayAll(IAudioDriver driver, List<SavedFile> files)
    {
        Console.WriteLine();
        Console.WriteLine($"== 逐个回放（{files.Count} 个文件，注意对比音质差异）==");

        var failed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var item = files[i];
            try
            {
                var info = AudioFile.Detect(item.Path);
                var audio = AudioFile.ReadPcm(item.Path);
                Console.WriteLine($"[{i + 1}/{files.Count}] {Path.GetFileName(item.Path)}    {info.Container}/{info.Codec} {audio.Format.SampleRate}Hz {audio.Format.Channels}ch {audio.Duration:F2}s");

                Play(driver, audio);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] 无法播放 {Path.GetFileName(item.Path)}：{ex.Message}");
            }

            // 留一点间隔，便于人耳区分相邻文件
            Thread.Sleep(300);
        }

        return failed;
    }

    /// <summary>播放一段 PCM</summary>
    /// <param name="driver">音频驱动</param>
    /// <param name="audio">待播放的 PCM</param>
    private static void Play(IAudioDriver driver, AudioPcm audio)
    {
        var format = audio.Format;
        using var player = driver.CreatePlayer();
        player.Init(format);
        player.Play();

        // 按 200ms 分块送入并限制积压：WASAPI 播放器会丢弃超过 2 秒的积压，一次性写入长音频会被截断
        var frameBytes = format.Channels * format.BitsPerSample / 8;
        var chunkBytes = Math.Max(frameBytes, format.ByteRate / 5 / frameBytes * frameBytes);
        var limit = format.ByteRate;

        var watch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(audio.Duration + 10);

        for (var offset = 0; offset < audio.Data.Length && watch.Elapsed < timeout; offset += chunkBytes)
        {
            var count = Math.Min(chunkBytes, audio.Data.Length - offset);
            var chunk = new Byte[count];
            Buffer.BlockCopy(audio.Data, offset, chunk, 0, count);
            player.Write(chunk);

            while (player.BufferedBytes > limit && watch.Elapsed < timeout) Thread.Sleep(10);
        }

        // 等待驱动播完剩余缓冲；无输出设备时缓冲不会下降，用超时兜底
        while (player.BufferedBytes > 0 && watch.Elapsed < timeout) Thread.Sleep(20);

        if (player.BufferedBytes > 0) Console.WriteLine("（等待播放超时：本机可能没有可用的输出设备）");

        player.Stop();
    }
    #endregion

    #region 辅助
    /// <summary>读取峰值电平</summary>
    /// <param name="pcm">16 位小端 PCM</param>
    /// <returns>峰值绝对值</returns>
    private static Int32 GetPeak(Byte[] pcm)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = Math.Abs((Int32)(Int16)(pcm[i] | (pcm[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }

        return peak;
    }

    /// <summary>读取 RMS 电平</summary>
    /// <param name="pcm">16 位小端 PCM</param>
    /// <returns>均方根</returns>
    private static Double GetRms(Byte[] pcm)
    {
        if (pcm.Length < 2) return 0;

        var sum = 0.0;
        var count = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (Int16)(pcm[i] | (pcm[i + 1] << 8));
            sum += (Double)sample * sample;
            count++;
        }

        return count > 0 ? Math.Sqrt(sum / count) : 0;
    }
    #endregion
}
