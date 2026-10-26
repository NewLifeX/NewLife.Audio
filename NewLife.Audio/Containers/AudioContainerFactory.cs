using NewLife.Audio.DSP;
using NewLife;

namespace NewLife.Audio.Containers;

/// <summary>容器工厂。根据扩展名或魔术字节自动创建对应的读写器</summary>
public static class AudioContainerFactory
{
    /// <summary>根据文件路径创建读取器（自动识别格式）</summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>容器读取器</returns>
    public static IAudioContainerReader CreateReader(String filePath)
    {
        if (filePath == null) throw new ArgumentNullException(nameof(filePath));

        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        var stream = File.OpenRead(filePath);
        try
        {
            return ext switch
            {
                ".wav" => new WaveFileReader(stream),
                ".ogg" or ".opus" => new OggFileReader(stream),
                ".mp3" => new Mp3FileReader(stream),
                ".mp4" or ".m4a" or ".mov" => new Mp4FileReader(stream),
                _ => CreateReader(stream, ext),
            };
        }
        catch
        {
            // 读取器未创建成功时由本方法负责关闭文件流
            stream.Dispose();
            throw;
        }
    }

    /// <summary>根据流和格式提示创建读取器</summary>
    /// <remarks>
    /// 只按魔术字节识别容器，不依据扩展名或参数猜测：未识别时抛 <see cref="NotSupportedException"/>，
    /// 避免把未知或损坏文件当作默认参数（8kHz/16 位/单声道）的裸 PCM“成功”解码出错误音频。
    /// 需要判断未知流或读取裸 PCM 时，用 <see cref="AudioProbe"/> 探测，或显式构造 <see cref="RawPcmReader"/> 并传入格式。
    /// <para>可定位流在识别后位置不变；不可定位流会把已读取的头部字节还回读取器。</para>
    /// </remarks>
    /// <param name="stream">输入流</param>
    /// <param name="formatHint">格式提示（扩展名），仅用于异常消息</param>
    /// <returns>容器读取器</returns>
    public static IAudioContainerReader CreateReader(Stream stream, String formatHint = null)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        // 尝试魔术字节识别
        var magic = new Byte[8];
        var pos = stream.CanSeek ? stream.Position : 0;
        var count = 0;
        while (count < magic.Length)
        {
            var read = stream.Read(magic, count, magic.Length - count);
            if (read <= 0) break;
            count += read;
        }
        if (stream.CanSeek) stream.Seek(pos, SeekOrigin.Begin);

        var reader = count >= 4 ? CreateReaderByMagic(magic, count, stream) : null;
        if (reader != null) return reader;

        var hint = formatHint.IsNullOrEmpty() ? "unknown" : formatHint;
        throw new NotSupportedException(L.T("Unsupported container format: {0}.", hint));
    }

    /// <summary>按魔术字节创建读取器</summary>
    /// <param name="magic">已读取的头部字节</param>
    /// <param name="count">实际读取的字节数</param>
    /// <param name="stream">输入流，识别前已复位</param>
    /// <returns>容器读取器，未识别时返回 null</returns>
    private static IAudioContainerReader CreateReaderByMagic(Byte[] magic, Int32 count, Stream stream)
    {
        // 不可定位流无法复位，用前置缓冲把已读字节还回去，否则读取器会丢掉文件头
        if (!stream.CanSeek)
        {
            var prefix = new Byte[count];
            Array.Copy(magic, prefix, count);
            stream = new PrependStream(prefix, stream);
        }

        if (magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
            return new WaveFileReader(stream);

        if (magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
            return new OggFileReader(stream);

        if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
            return new FlacContainerReader(stream);

        // ID3v2 标签或 MPEG 音频帧同步字（0xFF 0xEx 起）
        if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
            return new Mp3FileReader(stream);

        if (magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0 && (magic[1] & 0x06) == 0x02)
            return new Mp3FileReader(stream);

        // MP4/M4A: bytes 4-7 = 'ftyp'
        if (count >= 8 && magic[4] == 'f' && magic[5] == 't' && magic[6] == 'y' && magic[7] == 'p')
            return new Mp4FileReader(stream);

        return null;
    }

    /// <summary>根据文件路径和格式创建写入器</summary>
    /// <param name="filePath">文件路径（扩展名用于确定容器格式）</param>
    /// <param name="format">音频格式</param>
    /// <returns>容器写入器</returns>
    public static IAudioContainerWriter CreateWriter(String filePath, AudioFormat format)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        var stream = File.Create(filePath);
        try
        {
            return CreateWriter(stream, ext, format);
        }
        catch
        {
            // 写入器未创建成功时由本方法负责关闭文件流，避免句柄泄漏与文件被锁
            stream.Dispose();
            throw;
        }
    }

    /// <summary>根据流和格式提示创建写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="formatHint">格式提示（扩展名: .wav / .ogg / .opus / .mp3）</param>
    /// <param name="format">音频格式</param>
    /// <returns>容器写入器</returns>
    public static IAudioContainerWriter CreateWriter(Stream stream, String formatHint, AudioFormat format)
    {
        var hint = formatHint?.ToLowerInvariant();

        return hint switch
        {
            ".wav" => new WaveFileWriter(stream, format),
            ".ogg" or ".opus" => new OggFileWriter(stream, format, AVTypes.Opus),
            ".mp3" => new Mp3FileWriter(stream, format),
            ".mp4" or ".m4a" => new Mp4FileWriter(stream, format),
            ".flac" => new FlacFileWriter(stream, format),
            _ => throw new NotSupportedException(L.T("Unsupported container format: {0}.", formatHint)),
        };
    }
}
