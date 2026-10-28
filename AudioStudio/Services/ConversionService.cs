using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;

namespace AudioStudio.Services;

/// <summary>格式转换服务。转码细节全部由 NewLife.Audio 的 <see cref="AudioConverter"/> 承担，本类只做 UI 侧参数映射</summary>
/// <remarks>
/// 需要重采样或改声道时通过 <see cref="AudioEncodeOptions"/> 传入；目标容器由扩展名决定，
/// 覆盖 wav/flac/mp3/aiff/ogg/opus/m4a/aac，失败时库内已做临时文件原子替换，不会留下半成品。
/// </remarks>
public class ConversionService
{
    /// <summary>转换单个音频文件</summary>
    /// <param name="inputPath">输入文件路径</param>
    /// <param name="outputPath">输出文件路径（扩展名决定格式）</param>
    /// <param name="targetFormat">目标格式参数，null 则沿用源格式</param>
    /// <param name="progress">进度回调（0.0~1.0）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public void ConvertFile(String inputPath, String outputPath, AudioFormat targetFormat = null, IProgress<Double> progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("输入文件不存在", inputPath);
        if (String.IsNullOrEmpty(Path.GetExtension(outputPath))) throw new ArgumentException("输出文件必须包含扩展名", nameof(outputPath));

        var options = new AudioEncodeOptions
        {
            Container = Path.GetExtension(outputPath).ToLowerInvariant(),
        };

        // 只覆盖调用方明确指定的参数，未指定时沿用源格式，避免把采样率/声道数改成 0
        if (targetFormat != null)
        {
            if (targetFormat.SampleRate > 0) options.SampleRate = targetFormat.SampleRate;
            if (targetFormat.Channels > 0) options.Channels = targetFormat.Channels;
        }

        AudioConverter.Convert(inputPath, outputPath, options, progress, cancellationToken);
    }

    /// <summary>异步转换单个文件</summary>
    public Task ConvertFileAsync(String inputPath, String outputPath, AudioFormat targetFormat = null, IProgress<Double> progress = null, CancellationToken cancellationToken = default) =>
        AudioConverter.ConvertAsync(inputPath, outputPath, new AudioEncodeOptions
        {
            Container = Path.GetExtension(outputPath)?.ToLowerInvariant(),
            SampleRate = targetFormat is { SampleRate: > 0 } ? targetFormat.SampleRate : 0,
            Channels = targetFormat is { Channels: > 0 } ? targetFormat.Channels : 0,
        }, progress, cancellationToken);

    /// <summary>批量转换文件</summary>
    /// <param name="files">输入输出路径对</param>
    /// <param name="targetFormat">目标格式参数</param>
    /// <param name="overallProgress">总进度回调（0.0~1.0）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ConvertBatchAsync(IEnumerable<(String input, String output)> files, AudioFormat targetFormat = null, IProgress<Double> overallProgress = null, CancellationToken cancellationToken = default)
    {
        var list = new List<(String input, String output)>(files);
        var total = Math.Max(1, list.Count);
        var completed = 0;

        foreach (var (input, output) in list)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await ConvertFileAsync(input, output, targetFormat, null, cancellationToken).ConfigureAwait(false);

            completed++;
            overallProgress?.Report((Double)completed / total);
        }
    }

    /// <summary>根据扩展名推断音频编码类型</summary>
    /// <param name="extension">文件扩展名（含点）</param>
    /// <returns>对应编码类型</returns>
    public static AVTypes GetCodecTypeForExtension(String extension) => extension.ToLowerInvariant() switch
    {
        ".wav" => AVTypes.LPCM,
        ".mp3" => AVTypes.MP3,
        ".aac" => AVTypes.AAC,
        ".m4a" => AVTypes.AACLC,
        ".ogg" => AVTypes.Vorbis,
        ".opus" => AVTypes.Opus,
        ".flac" => AVTypes.LPCM, // FLAC 容器自身处理
        ".g711a" or ".alaw" => AVTypes.G711A,
        ".g711u" or ".ulaw" or ".mu-law" => AVTypes.G711U,
        ".wma" => AVTypes.WMA9STD,
        _ => AVTypes.LPCM,
    };

    /// <summary>获取支持的输出格式列表</summary>
    /// <returns>扩展名数组</returns>
    public static String[] GetSupportedOutputFormats() =>
    [".wav", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".m4a", ".aiff"];
}
