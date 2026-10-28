using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;

namespace NewLife.Audio;

/// <summary>转码门面</summary>
/// <remarks>
/// 一个入口搞定“任意可解码格式 → 任意可编码格式”：内部逐帧解码、按需重采样与声道转换、再交给
/// <see cref="AudioEncodePipeline"/> 编码封装，目标容器由扩展名决定。<br/>
/// 目标文件先写同目录临时文件再原子替换，中途失败不会留下半成品；源文件标签默认沿用。
/// </remarks>
/// <example>
/// <code>
/// AudioConverter.Convert("record.wav", "record.mp3");
/// AudioConverter.Convert("record.wav", "record.m4a", new AudioEncodeOptions { BitrateKbps = 96000 });
/// AudioConverter.Convert("record.wav", "record.ogg", new AudioEncodeOptions { VorbisQuality = 7 });
/// AudioConverter.Convert("record.wav", "record.opus");
/// </code>
/// </example>
public static class AudioConverter
{
    /// <summary>把音频文件转成另一种格式</summary>
    /// <param name="source">源文件路径</param>
    /// <param name="target">目标文件路径，扩展名决定容器与编码</param>
    /// <param name="options">编码参数；为空时使用各格式默认值</param>
    /// <param name="progress">进度回调，参数为 0~1</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>转换结果</returns>
    /// <exception cref="FileNotFoundException">源文件不存在</exception>
    /// <exception cref="NotSupportedException">源容器无法解码或目标容器无法编码</exception>
    public static AudioSaveResult Convert(String source, String target, AudioEncodeOptions options = null, IProgress<Double> progress = null, CancellationToken cancellationToken = default)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (!File.Exists(source)) throw new FileNotFoundException("Source audio file not found.", source);

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!directory.IsNullOrEmpty()) Directory.CreateDirectory(directory);

        options ??= new AudioEncodeOptions();
        options.Container = Path.GetExtension(target).ToLowerInvariant();

        var info = AudioProbe.Probe(source);
        if (options.Metadata == null && options.PreserveMetadata && info.Metadata != null) options.Metadata = info.Metadata;

        // 先写临时文件，成功后再替换目标，避免中途失败留下损坏文件
        var temp = target + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ConvertCore(input, info, output, options, progress, cancellationToken);
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 清理失败不影响主流程
                }
            }
        }

        return new AudioSaveResult { Reencoded = true, Container = AudioProbe.ProbeKind(target), Detail = "Decode and re-encode" };
    }

    /// <summary>把音频文件转成另一种格式（不占用调用方线程）</summary>
    /// <param name="source">源文件路径</param>
    /// <param name="target">目标文件路径</param>
    /// <param name="options">编码参数</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>转换结果</returns>
    public static Task<AudioSaveResult> ConvertAsync(String source, String target, AudioEncodeOptions options = null, IProgress<Double> progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Convert(source, target, options, progress, cancellationToken), cancellationToken);

    /// <summary>把音频流转码写入目标流（均需可定位）</summary>
    /// <param name="source">源数据流</param>
    /// <param name="target">目标数据流</param>
    /// <param name="info">源探测信息；为空时自动探测</param>
    /// <param name="options">编码参数，<see cref="AudioEncodeOptions.Container"/> 必须指定</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消标记</param>
    public static void Convert(Stream source, Stream target, AudioStreamInfo info = null, AudioEncodeOptions options = null, IProgress<Double> progress = null, CancellationToken cancellationToken = default)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (target == null) throw new ArgumentNullException(nameof(target));

        options ??= new AudioEncodeOptions();
        info ??= AudioProbe.Probe(source);

        ConvertCore(source, info, target, options, progress, cancellationToken);
    }

    private static void ConvertCore(Stream input, AudioStreamInfo info, Stream output, AudioEncodeOptions options, IProgress<Double> progress, CancellationToken cancellationToken)
    {
        // 输出流由调用方持有，管线只借用
        options.LeaveOpen = true;

        var duration = info?.Duration ?? 0;
        var elapsed = 0.0;
        AudioEncodePipeline pipeline = null;

        try
        {
            foreach (var chunk in AudioFile.StreamPcm(input, info, 64 * 1024))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var data = chunk.Data;
                if (data == null || data.Length == 0) continue;

                // 解码后的实际 PCM 格式在首块才确定，管线因此延迟创建
                pipeline ??= new AudioEncodePipeline(output, chunk.Format, options);
                pipeline.Write(data);

                elapsed += chunk.Duration;
                if (progress != null && duration > 0) progress.Report(Math.Min(1.0, elapsed / duration));
            }

            // 空源也要产出合法容器：用探测到的格式建一条空管线
            pipeline ??= new AudioEncodePipeline(output, info?.Format?.Clone() ?? new AudioFormat(), options);

            pipeline.Complete();
        }
        finally
        {
            pipeline?.Dispose();
        }
    }
}
