using System;
using System.Text;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 文本编解码</summary>
/// <remarks>优先按 UTF-8 严格解码，失败时回退 Latin-1，兼容旧 MIDI 文件。</remarks>
internal static class MidiText
{
    /// <summary>解码文本</summary>
    /// <param name="data">原始字节</param>
    /// <returns>解码后的文本</returns>
    public static String Decode(Byte[] data)
    {
        if (data == null || data.Length == 0) return String.Empty;

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            var chars = new Char[data.Length];
            for (var i = 0; i < data.Length; i++) chars[i] = (Char)data[i];
            return new String(chars);
        }
    }

    /// <summary>编码文本</summary>
    /// <param name="text">文本</param>
    /// <returns>UTF-8 字节</returns>
    public static Byte[] Encode(String text) => String.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);
}
