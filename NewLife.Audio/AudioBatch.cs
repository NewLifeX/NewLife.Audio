using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Containers;

namespace NewLife.Audio;

/// <summary>批量转换选项</summary>
public class AudioBatchOptions
{
    /// <summary>输出目录，为空时输出到源文件同目录</summary>
    public String OutputDirectory { get; set; }

    /// <summary>目标扩展名（含点，如 <c>.flac</c>）；为空时保持源扩展名</summary>
    public String TargetExtension { get; set; }

    /// <summary>目标文件已存在时是否覆盖，默认 true</summary>
    public Boolean Overwrite { get; set; } = true;

    /// <summary>并发度，1 表示顺序转换</summary>
    public Int32 MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>单个文件的写入选项（编码参数、标签、帧级复制开关）</summary>
    public AudioWriteOptions WriteOptions { get; set; }
}

/// <summary>批量转换的单项结果</summary>
public class AudioBatchItem
{
    /// <summary>源文件路径</summary>
    public String Source { get; set; }

    /// <summary>目标文件路径</summary>
    public String Target { get; set; }

    /// <summary>是否成功</summary>
    public Boolean Success { get; set; }

    /// <summary>保存结果（成功时非空）</summary>
    public AudioSaveResult Result { get; set; }

    /// <summary>失败原因（成功时为空）</summary>
    public String Error { get; set; }
}

/// <summary>批量音频转换器</summary>
/// <remarks>
/// 逐文件调用 <see cref="AudioFile.SaveAs"/>：容器与编码一致时走帧级复制，否则重编码；
/// 支持进度回调、取消与有限并发，输出文件均为原子写入。单个文件失败不影响其余文件，失败原因记录在结果中。
/// </remarks>
/// <example>
/// <code>
/// var items = AudioBatchConverter.Convert(files, "out", new AudioBatchOptions { TargetExtension = ".flac" },
///     (item, index, total) => Console.WriteLine($"{index}/{total} {item.Target}"));
/// </code>
/// </example>
public static class AudioBatchConverter
{
    /// <summary>批量转换</summary>
    /// <param name="sources">源文件路径集合</param>
    /// <param name="options">批量选项，为空时使用默认值</param>
    /// <param name="progress">进度回调，参数为当前项结果、已完成数、总数；可为空</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>与输入顺序一致的逐项结果</returns>
    /// <exception cref="OperationCanceledException">收到取消请求</exception>
    public static IList<AudioBatchItem> Convert(IEnumerable<String> sources, AudioBatchOptions options = null, Action<AudioBatchItem, Int32, Int32> progress = null, CancellationToken cancellationToken = default)
    {
        if (sources == null) throw new ArgumentNullException(nameof(sources));

        options ??= new AudioBatchOptions();
        var files = new List<String>();
        foreach (var item in sources)
        {
            if (!item.IsNullOrEmpty()) files.Add(item);
        }

        var results = new AudioBatchItem[files.Count];
        var total = files.Count;
        var completed = 0;
        var error = default(Exception);

        void Run(Int32 index)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = ConvertOne(files[index], options);
            results[index] = item;

            var done = Interlocked.Increment(ref completed);
            progress?.Invoke(item, done, total);
        }

        if (options.MaxDegreeOfParallelism > 1 && total > 1)
        {
            var optionsParallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            };
            try
            {
                Parallel.For(0, total, optionsParallel, Run);
            }
            catch (AggregateException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                error = ex.Flatten().InnerException;
            }
        }
        else
        {
            for (var i = 0; i < total; i++) Run(i);
        }

        if (error != null) throw error;
        cancellationToken.ThrowIfCancellationRequested();

        return results;
    }

    /// <summary>转换单个文件，异常转为结果中的失败原因</summary>
    private static AudioBatchItem ConvertOne(String source, AudioBatchOptions options)
    {
        var item = new AudioBatchItem { Source = source };
        try
        {
            var extension = options.TargetExtension.IsNullOrEmpty() ? Path.GetExtension(source) : options.TargetExtension;
            if (extension.IsNullOrEmpty()) throw new InvalidOperationException("Target extension is required: " + source);

            var directory = options.OutputDirectory.IsNullOrEmpty() ? Path.GetDirectoryName(Path.GetFullPath(source)) : options.OutputDirectory;
            var name = Path.GetFileNameWithoutExtension(source) + extension;
            var target = Path.Combine(directory ?? ".", name);
            item.Target = target;

            if (!options.Overwrite && File.Exists(target))
            {
                item.Error = "Target exists.";
                return item;
            }

            if (directory != null && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            item.Result = AudioFile.Open(source).SaveAs(target, options.WriteOptions);
            item.Success = true;
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
        }

        return item;
    }
}
