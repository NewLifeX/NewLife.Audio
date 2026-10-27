using System;
using System.Collections.Generic;

namespace NewLife.Audio.Midi;

/// <summary>UMP 包流读取器</summary>
/// <remarks>按消息类型确定 32 位字数并切分；不完整的尾部包会被忽略。</remarks>
public static class UmpReader
{
    /// <summary>读取 UMP 包</summary>
    /// <param name="words">32 位字序列</param>
    /// <returns>UMP 包列表</returns>
    public static IList<UmpPacket> Read(UInt32[] words)
    {
        if (words == null) throw new ArgumentNullException(nameof(words));

        var packets = new List<UmpPacket>();
        var index = 0;
        while (index < words.Length)
        {
            var type = (UmpMessageType)((words[index] >> 28) & 0x0F);
            var count = UmpPacket.GetWordCount(type);
            if (index + count > words.Length) break;

            var packetWords = new UInt32[count];
            Array.Copy(words, index, packetWords, 0, count);
            packets.Add(new UmpPacket(packetWords));
            index += count;
        }

        return packets;
    }

    /// <summary>读取 Big-Endian 字节流中的 UMP 包</summary>
    /// <param name="data">字节数据</param>
    /// <returns>UMP 包列表</returns>
    public static IList<UmpPacket> Read(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        var words = new UInt32[data.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            var offset = i * 4;
            words[i] = ((UInt32)data[offset] << 24)
                | ((UInt32)data[offset + 1] << 16)
                | ((UInt32)data[offset + 2] << 8)
                | data[offset + 3];
        }

        return Read(words);
    }
}

/// <summary>SysEx7 多包组装器</summary>
public sealed class UmpSysEx7Assembler
{
    private readonly List<Byte> _data = [];

    /// <summary>已组装的数据</summary>
    public Byte[] Data => _data.ToArray();

    /// <summary>是否已完成</summary>
    public Boolean IsComplete { get; private set; }

    /// <summary>追加一个 SysEx7 包</summary>
    /// <param name="packet">UMP 包</param>
    /// <returns>是否已完成</returns>
    /// <exception cref="ArgumentNullException">包为空</exception>
    /// <exception cref="ArgumentException">不是 SysEx7 包</exception>
    public Boolean Append(UmpPacket packet)
    {
        if (packet == null) throw new ArgumentNullException(nameof(packet));
        if (packet.MessageType != UmpMessageType.SysEx7) throw new ArgumentException("The UMP packet is not a SysEx7 packet.", nameof(packet));

        _data.AddRange(packet.GetSysEx7Payload());

        var status = packet.SysEx7Status;
        if (status == 0 || status == 3) IsComplete = true;
        return IsComplete;
    }
}
