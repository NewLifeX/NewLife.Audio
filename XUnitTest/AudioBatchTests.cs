using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>批量转换测试：顺序/并发、进度回调、取消与失败隔离</summary>
public class AudioBatchTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "批量转换逐个输出并回报进度")]
    public void Convert_ReportsProgress()
    {
        var directory = TempDirectory();
        try
        {
            var files = CreateWavs(directory, 3);
            var outDir = Path.Combine(directory, "out");
            var seen = new List<Int32>();

            var items = AudioBatchConverter.Convert(files, new AudioBatchOptions
            {
                OutputDirectory = outDir,
                TargetExtension = ".flac",
            }, (item, index, total) => seen.Add(index));

            Assert.Equal(3, items.Count);
            Assert.All(items, item => Assert.True(item.Success, item.Error));
            Assert.Equal([1, 2, 3], seen);

            foreach (var item in items)
            {
                Assert.True(File.Exists(item.Target));
                Assert.True(item.Result.Reencoded);
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact(DisplayName = "批量转换并发执行同样逐项成功")]
    public void Convert_Parallel_Works()
    {
        var directory = TempDirectory();
        try
        {
            var files = CreateWavs(directory, 4);
            var outDir = Path.Combine(directory, "out");

            var items = AudioBatchConverter.Convert(files, new AudioBatchOptions
            {
                OutputDirectory = outDir,
                TargetExtension = ".mp3",
                MaxDegreeOfParallelism = 2,
            });

            Assert.Equal(4, items.Count);
            Assert.All(items, item => Assert.True(item.Success, item.Error));
            Assert.All(items, item => Assert.True(File.Exists(item.Target)));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact(DisplayName = "批量转换遵守取消标记")]
    public void Convert_Cancelled_Throws()
    {
        var directory = TempDirectory();
        try
        {
            var files = CreateWavs(directory, 4);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => AudioBatchConverter.Convert(files, new AudioBatchOptions
            {
                OutputDirectory = Path.Combine(directory, "out"),
                TargetExtension = ".flac",
            }, null, cts.Token));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact(DisplayName = "单个文件失败不影响其余文件")]
    public void Convert_BadFile_Isolated()
    {
        var directory = TempDirectory();
        try
        {
            var files = CreateWavs(directory, 2);
            var broken = Path.Combine(directory, "broken.wav");
            File.WriteAllBytes(broken, new Byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            files.Insert(1, broken);

            var items = AudioBatchConverter.Convert(files, new AudioBatchOptions
            {
                OutputDirectory = Path.Combine(directory, "out"),
                TargetExtension = ".flac",
            });

            Assert.Equal(3, items.Count);
            Assert.True(items[0].Success);
            Assert.False(items[1].Success);
            Assert.NotNull(items[1].Error);
            Assert.True(items[2].Success);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact(DisplayName = "目标已存在且不覆盖时跳过")]
    public void Convert_NoOverwrite_Skips()
    {
        var directory = TempDirectory();
        try
        {
            var files = CreateWavs(directory, 1);
            var outDir = Path.Combine(directory, "out");
            Directory.CreateDirectory(outDir);
            var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(files[0]) + ".flac");
            File.WriteAllBytes(target, [0x01]);

            var items = AudioBatchConverter.Convert(files, new AudioBatchOptions
            {
                OutputDirectory = outDir,
                TargetExtension = ".flac",
                Overwrite = false,
            });

            Assert.False(items[0].Success);
            Assert.Equal(new Byte[] { 0x01 }, File.ReadAllBytes(target));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    #region 辅助
    private static String TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nlaudio_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private static List<String> CreateWavs(String directory, Int32 count)
    {
        var files = new List<String>();
        var pcm = BuildSine(0.1, 0.5);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(directory, "file" + i + ".wav");
            AudioFile.WritePcm(path, Format, pcm);
            files.Add(path);
        }

        return files;
    }

    private static Byte[] BuildSine(Double seconds, Double amplitude)
    {
        var samples = (Int32)(Format.SampleRate * seconds);
        var data = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * 440 * i / Format.SampleRate) * amplitude * 32000);
            data[i * 2] = (Byte)(v & 0xFF);
            data[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        return data;
    }

    private static void TryDeleteDirectory(String path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
    #endregion
}

