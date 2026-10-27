using System;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class UmpTests
{
    [Fact(DisplayName = "UMP MIDI 1.0 Note On 解析")]
    public void Midi1NoteOn_Parses()
    {
        var word = Word(UmpMessageType.Midi1ChannelVoice, 0, 0x9, 3, 60, 100);
        var packets = UmpReader.Read(new[] { word });

        Assert.Single(packets);
        var e = packets[0].ToMidiEvent(0);
        Assert.NotNull(e);
        Assert.Equal(MidiEventType.NoteOn, e.Type);
        Assert.Equal(3, e.Channel);
        Assert.Equal(60, e.NoteNumber);
        Assert.Equal(100, e.Velocity);
    }

    [Fact(DisplayName = "UMP MIDI 2.0 Note On 速度缩放到 7 位")]
    public void Midi2NoteOn_ScalesVelocity()
    {
        var word0 = Word(UmpMessageType.Midi2ChannelVoice, 0, 0x9, 2, 64, 0);
        var word1 = 0xFFFFu << 16;
        var packet = new UmpPacket(new[] { word0, word1 });

        var e = packet.ToMidiEvent(0);
        Assert.NotNull(e);
        Assert.Equal(MidiEventType.NoteOn, e.Type);
        Assert.Equal(64, e.NoteNumber);
        Assert.Equal(127, e.Velocity);
    }

    [Fact(DisplayName = "UMP MIDI 2.0 弯音解析")]
    public void Midi2PitchBend_Parses()
    {
        var word0 = Word(UmpMessageType.Midi2ChannelVoice, 0, 0xE, 1, 0, 0);
        var word1 = 0x80000000u + (2048u << 18);
        var packet = new UmpPacket(new[] { word0, word1 });

        var e = packet.ToMidiEvent(0);
        Assert.NotNull(e);
        Assert.Equal(MidiEventType.PitchBend, e.Type);
        Assert.Equal(2048, e.PitchBendValue);
    }

    [Fact(DisplayName = "UMP MIDI 2.0 控制器解析")]
    public void Midi2ControlChange_Parses()
    {
        var word0 = Word(UmpMessageType.Midi2ChannelVoice, 0, 0xB, 0, 7, 0);
        var word1 = 0xFFFFFFFFu;
        var packet = new UmpPacket(new[] { word0, word1 });

        var e = packet.ToMidiEvent(0);
        Assert.NotNull(e);
        Assert.Equal(MidiEventType.ControlChange, e.Type);
        Assert.Equal(7, e.Controller);
        Assert.Equal(127, e.ControllerValue);
    }

    [Fact(DisplayName = "UMP SysEx7 多包组装")]
    public void SysEx7_Assembles()
    {
        var first = SysExPacket(1, new Byte[] { 1, 2, 3, 4, 5, 6 });
        var second = SysExPacket(3, new Byte[] { 0x0A, 0x0B });
        var assembler = new UmpSysEx7Assembler();

        Assert.False(assembler.Append(first));
        Assert.True(assembler.Append(second));
        Assert.Equal(new Byte[] { 1, 2, 3, 4, 5, 6, 0x0A, 0x0B }, assembler.Data);
    }

    [Fact(DisplayName = "UMP 字节流按 Big-Endian 解析")]
    public void ByteStream_Parses()
    {
        var words = new[]
        {
            Word(UmpMessageType.Midi1ChannelVoice, 0, 0x9, 0, 60, 100),
            Word(UmpMessageType.Midi1ChannelVoice, 0, 0x8, 0, 60, 0),
        };
        var bytes = new Byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            var offset = i * 4;
            bytes[offset] = (Byte)((words[i] >> 24) & 0xFF);
            bytes[offset + 1] = (Byte)((words[i] >> 16) & 0xFF);
            bytes[offset + 2] = (Byte)((words[i] >> 8) & 0xFF);
            bytes[offset + 3] = (Byte)(words[i] & 0xFF);
        }

        var packets = UmpReader.Read(bytes);
        Assert.Equal(2, packets.Count);
        Assert.Equal(MidiEventType.NoteOn, packets[0].ToMidiEvent(0).Type);
        Assert.Equal(MidiEventType.NoteOff, packets[1].ToMidiEvent(0).Type);
    }

    [Fact(DisplayName = "UMP 包字数不匹配时拒绝构造")]
    public void WrongWordCount_Throws()
    {
        var word = Word(UmpMessageType.Midi2ChannelVoice, 0, 0x9, 0, 60, 100);
        Assert.Throws<ArgumentException>(() => new UmpPacket(new[] { word }));
    }

    private static UInt32 Word(UmpMessageType type, Int32 group, Int32 status, Int32 channel, Int32 data1, Int32 data2) =>
        ((UInt32)type << 28)
        | ((UInt32)(group & 0x0F) << 24)
        | ((UInt32)(status & 0x0F) << 20)
        | ((UInt32)(channel & 0x0F) << 16)
        | ((UInt32)(data1 & 0x7F) << 8)
        | (UInt32)(data2 & 0x7F);

    private static UmpPacket SysExPacket(Int32 status, Byte[] payload)
    {
        var count = payload.Length;
        var word0 = ((UInt32)UmpMessageType.SysEx7 << 28)
            | ((UInt32)(status & 0x0F) << 20)
            | ((UInt32)(count & 0x0F) << 16)
            | ((UInt32)payload[0] << 8)
            | payload[1];

        UInt32 word1 = 0;
        for (var i = 2; i < count; i++) word1 |= (UInt32)payload[i] << ((5 - i) * 8);

        return new UmpPacket(new[] { word0, word1 });
    }
}
