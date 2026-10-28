using System;
using System.Collections.Generic;
using System.IO;
using NewLife.Audio;

namespace Test;

/// <summary>录音 → 转码 → 回放的一键听感验证</summary>
/// <remarks>
/// 用法：<c>dotnet run --project Test -- [录音秒数]</c>，默认 5 秒。<br/>
/// 三段都是一行：<see cref="AudioRecorder.RecordToFile"/> 边录边写盘、
/// <see cref="AudioConverter.Convert"/> 按扩展名转码、<see cref="AudioPlayer.PlayFile(String, Double)"/> 播完返回。<br/>
/// 单元测试能验证波形、时长与电平，却判断不了“听起来怎么样”，也没有麦克风和扬声器，因此这类验证放在控制台。
/// </remarks>
internal static class AudioLab
{
    public static Int32 Run(String[] args)
    {
        var seconds = args.Length > 0 && Double.TryParse(args[0], out var value) && value > 0 ? Math.Min(value, 60) : 5;
        var directory = Path.Combine(AppContext.BaseDirectory, "RecordSamples");
        var wav = Path.Combine(directory, "record.wav");

        try
        {
            Console.WriteLine($"准备录音 {seconds} 秒，请对着麦克风说话……");
            AudioRecorder.RecordToFile(wav, TimeSpan.FromSeconds(seconds));

            var files = new List<String> { wav };
            foreach (var extension in new[] { ".flac", ".mp3", ".m4a", ".ogg", ".opus" })
            {
                var path = Path.ChangeExtension(wav, extension);
                try
                {
                    AudioConverter.Convert(wav, path);
                    files.Add(path);
                    Console.WriteLine($"转码 {Path.GetFileName(path),-16} {new FileInfo(path).Length,10:N0} 字节");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"跳过 {extension}：{ex.Message}（Opus 需要 ffmpeg）");
                }
            }

            Console.WriteLine("开始逐个回放，注意对比音质差异……");
            foreach (var path in files) AudioPlayer.PlayFile(path);

            Console.WriteLine($"全部完成，文件在 {directory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
