using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>容器工厂。根据扩展名或魔术字节自动创建对应的读写器</summary>
public static class AudioContainerFactory
{
    /// <summary>根据文件路径创建读取器（自动识别格式）</summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>容器读取器</returns>
    public static IAudioContainerReader CreateReader(String filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        var stream = File.OpenRead(filePath);

        return ext switch
        {
            ".wav" => new WaveFileReader(stream),
            ".ogg" or ".opus" => new OggFileReader(stream),
            _ => CreateReader(stream, ext),
        };
    }

    /// <summary>根据流和格式提示创建读取器</summary>
    /// <param name="stream">输入流</param>
    /// <param name="formatHint">格式提示（扩展名）</param>
    /// <returns>容器读取器</returns>
    public static IAudioContainerReader CreateReader(Stream stream, String formatHint = null)
    {
        // 尝试魔术字节识别
        var magic = new Byte[4];
        var pos = stream.Position;
        if (stream.Read(magic, 0, 4) >= 4)
        {
            stream.Seek(pos, SeekOrigin.Begin);

            if (magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
                return new WaveFileReader(stream);

            if (magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
                return new OggFileReader(stream);

            if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
                return new FlacContainerReader(stream);
        }

        // 回退到原始 PCM
        return new RawPcmReader(stream);
    }

    /// <summary>根据文件路径和格式创建写入器</summary>
    /// <param name="filePath">文件路径（扩展名用于确定容器格式）</param>
    /// <param name="format">音频格式</param>
    /// <returns>容器写入器</returns>
    public static IAudioContainerWriter CreateWriter(String filePath, AudioFormat format)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        var stream = File.Create(filePath);

        return CreateWriter(stream, ext, format);
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
            ".ogg" or ".opus" => new OggFileWriter(stream, format, AVTypes.Transparent),
            ".mp3" => new Mp3FileWriter(stream, format),
            _ => throw new NotSupportedException($"不支持的容器格式: {formatHint}"),
        };
    }
}
