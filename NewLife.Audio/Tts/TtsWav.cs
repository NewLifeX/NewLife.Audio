namespace NewLife.Audio.Tts;

/// <summary>WAV 头信息</summary>
public class TtsWavInfo
{
    /// <summary>采样率</summary>
    public Int32 SampleRate { get; set; }

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>每样本位数</summary>
    public Int32 BitsPerSample { get; set; } = 16;

    /// <summary>PCM 数据起始偏移</summary>
    public Int32 DataOffset { get; set; }

    /// <summary>PCM 数据长度</summary>
    public Int32 DataSize { get; set; }
}

/// <summary>WAV 头解析工具</summary>
/// <remarks>
/// 在线服务与子进程引擎经常直接返回 WAV，送声卡前必须把文件头剥掉。
/// 这里只做最小解析：定位格式参数与 data 块，不处理多声道交织等细节。
/// </remarks>
public static class TtsWav
{
    /// <summary>解析内存中的 WAV 数据</summary>
    /// <param name="data">完整 WAV 数据</param>
    /// <param name="info">解析结果</param>
    /// <returns>是否解析成功</returns>
    public static Boolean TryParse(Byte[] data, out TtsWavInfo info)
    {
        info = null;
        if (data == null || data.Length < 44) return false;
        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

        var result = new TtsWavInfo();
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = "" + (Char)data[offset] + (Char)data[offset + 1] + (Char)data[offset + 2] + (Char)data[offset + 3];
            var size = BitConverter.ToInt32(data, offset + 4);
            var body = offset + 8;

            if (id == "fmt " && body + 16 <= data.Length)
            {
                result.Channels = BitConverter.ToInt16(data, body + 2);
                result.SampleRate = BitConverter.ToInt32(data, body + 4);
                result.BitsPerSample = BitConverter.ToInt16(data, body + 14);
            }
            else if (id == "data")
            {
                result.DataOffset = body;
                result.DataSize = size > 0 && body + size <= data.Length ? size : data.Length - body;
                info = result;
                return true;
            }

            if (size <= 0) break;
            offset = body + size + (size % 2);
        }

        return false;
    }

    /// <summary>流式跳过 WAV 文件头，使流位置停在 PCM 数据起点</summary>
    /// <param name="stream">输入流</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>解析到的头信息，失败时返回 null</returns>
    public static async Task<TtsWavInfo> SkipHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new Byte[12];
        if (!await ReadFullAsync(stream, header, 0, 12, cancellationToken).ConfigureAwait(false)) return null;

        var info = new TtsWavInfo();
        var chunk = new Byte[8];
        while (await ReadFullAsync(stream, chunk, 0, 8, cancellationToken).ConfigureAwait(false))
        {
            var id = "" + (Char)chunk[0] + (Char)chunk[1] + (Char)chunk[2] + (Char)chunk[3];
            var size = BitConverter.ToInt32(chunk, 4);

            if (id == "fmt " && size >= 16)
            {
                var fmt = new Byte[Math.Min(size, 64)];
                if (!await ReadFullAsync(stream, fmt, 0, fmt.Length, cancellationToken).ConfigureAwait(false)) return null;

                info.Channels = BitConverter.ToInt16(fmt, 2);
                info.SampleRate = BitConverter.ToInt32(fmt, 4);
                info.BitsPerSample = BitConverter.ToInt16(fmt, 14);

                var rest = size - fmt.Length;
                while (rest > 0)
                {
                    var step = Math.Min(rest, 64);
                    if (!await ReadFullAsync(stream, fmt, 0, step, cancellationToken).ConfigureAwait(false)) return null;
                    rest -= step;
                }
                continue;
            }

            if (id == "data")
            {
                info.DataSize = size;
                return info;
            }

            if (size <= 0) return null;

            var skip = new Byte[Math.Min(size, 8192)];
            var left = size;
            while (left > 0)
            {
                var read = await stream.ReadAsync(skip, 0, Math.Min(skip.Length, left), cancellationToken).ConfigureAwait(false);
                if (read <= 0) return null;
                left -= read;
            }
        }

        return null;
    }

    private static async Task<Boolean> ReadFullAsync(Stream stream, Byte[] buffer, Int32 offset, Int32 count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer, offset + total, count - total, cancellationToken).ConfigureAwait(false);
            if (read <= 0) return false;
            total += read;
        }

        return true;
    }
}
