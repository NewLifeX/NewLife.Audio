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

        // 确定目标格式：以源格式为基线，只覆盖调用方明确指定的参数
        // 直接整体替换会让未指定的采样率/位深变成 0，导致重采样目标为 0 或容器头写错
        var dstFormat = srcFormat.Clone();
        if (targetFormat != null)
        {
            if (targetFormat.SampleRate > 0) dstFormat.SampleRate = targetFormat.SampleRate;
            if (targetFormat.Channels > 0) dstFormat.Channels = targetFormat.Channels;
            if (targetFormat.BitsPerSample > 0) dstFormat.BitsPerSample = targetFormat.BitsPerSample;
            if (targetFormat.Encoding > 0) dstFormat.Encoding = targetFormat.Encoding;
        }

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

        // Opus 只接受 8/12/16/24/48 kHz；其他采样率在转换时重采样
        var srcChannels = srcFormat.Channels > 0 ? srcFormat.Channels : 1;
        var srcRate = srcFormat.SampleRate > 0 ? srcFormat.SampleRate : 48000;
        var pcmRate = srcCodec is OpusCodec ? OpusOptions.GetNearestSampleRate(srcRate) : srcRate;
        var srcOpusOptions = srcCodec is OpusCodec ? new OpusOptions { SampleRate = pcmRate, Channels = srcChannels } : null;
        var dstOpusOptions = dstCodec is OpusCodec ? new OpusOptions { SampleRate = OpusOptions.GetNearestSampleRate(pcmRate), Channels = srcChannels } : null;

        // Ogg Opus 写入器使用编码器算法延迟作为 pre-skip，避免播放时带入编码器前导静音
        if (writer is OggFileWriter oggWriter && dstOpusOptions != null)
            oggWriter.PreSkip = OpusCodec.GetLookahead(dstOpusOptions);

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
                pcmData = srcCodec.ToPcm(frame.GetSpan(), srcOpusOptions);
            else
                pcmData = frame; // 已经是 PCM

            // PCM → 目标
            if (dstCodec is OpusCodec opusCodec)
            {
                var pcm = pcmData.ToArray();
                if (pcmRate != dstOpusOptions.SampleRate)
                    pcm = Resample(pcm, pcmRate, dstOpusOptions.SampleRate, srcChannels);

                // Opus 包不自带长度边界，连续 PCM 通过 Encode 逐帧产出多个包
                foreach (var packet in opusCodec.Encode(pcm, dstOpusOptions))
                {
                    if (packet.Total > 0) writer.WriteFrame(packet.GetSpan());
                }
            }
            else if (dstCodec != null && dstCodecType != AVTypes.LPCM)
            {
                var pcm = pcmData.ToArray();
                if (pcmRate != dstFormat.SampleRate)
                    pcm = Resample(pcm, pcmRate, dstFormat.SampleRate, srcChannels);

                // 编解码器只吃 16 位 PCM，按目标声道数转换后再编码
                var dstChannels = dstFormat.Channels > 0 ? dstFormat.Channels : srcChannels;
                if (dstChannels != srcChannels) pcm = ConvertChannels(pcm, srcChannels, dstChannels);

                var encoded = dstCodec.FromPcm(pcm, null);
                writer.WriteFrame(encoded.GetSpan());
            }
            else
            {
                var pcm = pcmData.ToArray();
                if (pcmRate != dstFormat.SampleRate)
                    pcm = Resample(pcm, pcmRate, dstFormat.SampleRate, srcChannels);

                // 声道与位深必须真正转换：只改容器头会得到"时长对但声音全错"的文件
                pcm = ConvertFormat(pcm, srcChannels, dstFormat);

                writer.WriteFrame(pcm);
            }

            frameCount++;
            if (totalFrames > 0 && progress != null)
                progress.Report((Double)frameCount / totalFrames);
        }

        // Opus 编码器可能缓存不足一帧的 PCM，结束时补静音输出
        if (dstCodec is OpusCodec tailCodec)
        {
            foreach (var packet in tailCodec.Encode(ReadOnlySpan<Byte>.Empty, flush: true))
            {
                if (packet.Total > 0) writer.WriteFrame(packet.GetSpan());
            }
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
            ".ogg" or ".opus" => AVTypes.Opus,
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

    /// <summary>按声道拆分后重采样，避免立体声交错样本被跨声道插值</summary>
    private static Byte[] Resample(Byte[] pcm, Int32 inputRate, Int32 outputRate, Int32 channels)
    {
        if (pcm.Length == 0 || inputRate == outputRate || inputRate <= 0 || outputRate <= 0) return pcm;
        if (channels <= 1) return Resampler.ResamplePcm(pcm, inputRate, outputRate, ResamplerQuality.Sinc);

        var samples = pcm.Length / 2 / channels;
        var outputSamples = (Int32)((Int64)samples * outputRate / inputRate);
        var result = new Byte[outputSamples * channels * 2];
        var channelBytes = new Byte[samples * 2];

        for (var ch = 0; ch < channels; ch++)
        {
            for (var i = 0; i < samples; i++)
            {
                channelBytes[i * 2] = pcm[(i * channels + ch) * 2];
                channelBytes[i * 2 + 1] = pcm[(i * channels + ch) * 2 + 1];
            }

            var resampled = Resampler.ResamplePcm(channelBytes, inputRate, outputRate, ResamplerQuality.Sinc);
            var count = Math.Min(outputSamples, resampled.Length / 2);
            for (var i = 0; i < count; i++)
            {
                result[(i * channels + ch) * 2] = resampled[i * 2];
                result[(i * channels + ch) * 2 + 1] = resampled[i * 2 + 1];
            }
        }

        return result;
    }

    /// <summary>16 位交错 PCM 的声道数转换</summary>
    /// <param name="pcm">16 位交错 PCM</param>
    /// <param name="inputChannels">源声道数</param>
    /// <param name="outputChannels">目标声道数</param>
    /// <returns>转换后的 16 位交错 PCM</returns>
    private static Byte[] ConvertChannels(Byte[] pcm, Int32 inputChannels, Int32 outputChannels)
    {
        if (pcm.Length == 0 || inputChannels <= 0 || outputChannels <= 0 || inputChannels == outputChannels) return pcm;

        var samples = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
        var frames = samples.Length / inputChannels;
        var output = new Single[frames * outputChannels];
        ChannelConverter.Convert(samples, inputChannels, output, 0, outputChannels, frames);

        return PcmConverter.FromFloat(output, SampleFormat.Int16);
    }

    /// <summary>按目标格式转换声道数与位深</summary>
    /// <param name="pcm">源 16 位交错 PCM</param>
    /// <param name="inputChannels">源声道数</param>
    /// <param name="target">目标格式</param>
    /// <returns>与目标格式一致的 PCM</returns>
    private static Byte[] ConvertFormat(Byte[] pcm, Int32 inputChannels, AudioFormat target)
    {
        var channels = target.Channels > 0 ? target.Channels : inputChannels;
        var result = ConvertChannels(pcm, inputChannels, channels);

        var format = target.SampleFormat;
        return format is SampleFormat.Int16 or SampleFormat.Unknown ? result : PcmConverter.FromFloat(PcmConverter.ToFloat(result, SampleFormat.Int16), format);
    }
}
