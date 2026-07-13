using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace AudioStudio.Services;

/// <summary>格式转换服务。支持音频文件格式互转和参数调整</summary>
public class ConversionService
{
    private readonly AudioCodecFactory _codecFactory;

    public ConversionService()
    {
        _codecFactory = new AudioCodecFactory();
    }

    /// <summary>转换单个音频文件</summary>
    /// <param name="inputPath">输入文件路径</param>
    /// <param name="outputPath">输出文件路径（扩展名决定格式）</param>
    /// <param name="targetFormat">目标格式参数，null 则沿用源格式</param>
    /// <param name="progress">进度回调（0.0~1.0）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void ConvertFile(String inputPath, String outputPath,
        AudioFormat? targetFormat = null,
        IProgress<Double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("输入文件不存在", inputPath);

        var ext = Path.GetExtension(outputPath);
        if (String.IsNullOrEmpty(ext))
            throw new ArgumentException("输出文件必须包含扩展名", nameof(outputPath));

        // 打开源文件
        using var reader = AudioContainerFactory.CreateReader(inputPath);
        var srcFormat = reader.Format;
        var srcCodecType = reader.CodecType;

        // 确定目标格式
        var dstFormat = targetFormat ?? srcFormat.Clone();

        // 目标编码类型（根据输出扩展名推断）
        var dstCodecType = GetCodecTypeForExtension(ext);

        // 创建写入器
        using var writer = AudioContainerFactory.CreateWriter(outputPath, dstFormat);

        // 写入元数据
        if (reader.Metadata != null)
            writer.WriteMetadata(reader.Metadata);

        // 获取编解码器
        var srcCodec = GetCodec(srcCodecType);
        var dstCodec = GetCodec(dstCodecType);

        // 逐帧转换
        var totalFrames = reader.TotalFrames;
        var frameCount = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = reader.ReadFrame();
            if (frame == null) break;

            IPacket pcmData;

            // 源 → PCM
            if (srcCodec != null && srcCodecType != AVTypes.LPCM)
                pcmData = srcCodec.ToPcm(frame.GetSpan(), null);
            else
                pcmData = frame; // 已经是 PCM

            // PCM → 目标
            if (dstCodec != null && dstCodecType != AVTypes.LPCM)
            {
                var encoded = dstCodec.FromPcm(pcmData.GetSpan(), null);
                writer.WriteFrame(encoded.GetSpan());
            }
            else
            {
                writer.WriteFrame(pcmData.GetSpan());
            }

            frameCount++;
            if (totalFrames > 0 && progress != null)
                progress.Report((Double)frameCount / totalFrames);
        }

        writer.Flush();

        if (cancellationToken.IsCancellationRequested)
        {
            // 取消时删除不完整的输出文件
            try { File.Delete(outputPath); } catch { /* ignore */ }
        }
    }

    /// <summary>异步转换单个文件</summary>
    public Task ConvertFileAsync(String inputPath, String outputPath,
        AudioFormat? targetFormat = null,
        IProgress<Double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ConvertFile(inputPath, outputPath, targetFormat, progress, cancellationToken), cancellationToken);
    }

    /// <summary>批量转换文件</summary>
    public async Task ConvertBatchAsync(IEnumerable<(String input, String output)> files,
        AudioFormat? targetFormat = null,
        IProgress<Double>? overallProgress = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<(String input, String output)>(files);
        var total = list.Count;
        var completed = 0;

        foreach (var (input, output) in list)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var fileProgress = new Progress<Double>(_ => { });
            await ConvertFileAsync(input, output, targetFormat, fileProgress, cancellationToken);

            completed++;
            overallProgress?.Report((Double)completed / total);
        }
    }

    /// <summary>根据扩展名推断音频编码类型</summary>
    public static AVTypes GetCodecTypeForExtension(String extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".wav" => AVTypes.LPCM,
            ".mp3" => AVTypes.MP3,
            ".aac" or ".m4a" => AVTypes.AAC,
            ".ogg" or ".opus" => AVTypes.Transparent, // Opus
            ".flac" => AVTypes.LPCM, // FLAC 容器自身处理
            ".g711a" or ".alaw" => AVTypes.G711A,
            ".g711u" or ".ulaw" or ".mu-law" => AVTypes.G711U,
            ".wma" => AVTypes.WMA9STD,
            _ => AVTypes.LPCM,
        };
    }

    /// <summary>获取支持的输出格式列表</summary>
    public static String[] GetSupportedOutputFormats() =>
    [".wav", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".m4a"];

    private IAudioCodec? GetCodec(AVTypes avType)
    {
        if (avType == AVTypes.LPCM) return null;
        try { return _codecFactory.Registry.GetCodec(avType); }
        catch { return null; }
    }
}
