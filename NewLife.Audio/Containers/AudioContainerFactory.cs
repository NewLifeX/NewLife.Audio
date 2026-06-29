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
}
