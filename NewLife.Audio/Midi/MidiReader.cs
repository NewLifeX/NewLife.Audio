using System;
using System.IO;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 轨道解析器</summary>
internal static class MidiReader
{
    /// <summary>解析一条 MTrk 轨道</summary>
    /// <param name="data">完整文件数据</param>
    /// <param name="offset">轨道数据起始位置</param>
    /// <param name="length">轨道数据长度</param>
    /// <returns>轨道</returns>
    public static MidiTrack ReadTrack(Byte[] data, Int32 offset, Int32 length)
    {
        var track = new MidiTrack();
        var reader = new Cursor(data, offset, length);
        var tick = 0L;
        var runningStatus = 0;
        String name = null;

        while (!reader.End)
        {
            tick += reader.ReadVlq();

            var b = reader.ReadByte();
            Int32 status;
            var data1 = -1;

            if (b < 0x80)
            {
                if (runningStatus == 0) throw new InvalidDataException("The MIDI running status is missing.");
                status = runningStatus;
                data1 = b;
            }
            else
            {
                status = b;
                if (status < 0xF0) runningStatus = status;
                else runningStatus = 0;
            }

            if (status < 0xF0)
            {
                var channel = status & 0x0F;
                var type = status & 0xF0;

                if (data1 < 0) data1 = reader.ReadByte();

                switch (type)
                {
                    case 0x80:
                        {
                            var velocity = reader.ReadByte();
                            track.Add(new MidiEvent
                            {
                                Tick = tick,
                                Type = MidiEventType.NoteOff,
                                Status = (Byte)status,
                                Channel = channel,
                                Data1 = data1 & 0x7F,
                                Data2 = velocity & 0x7F,
                            });
                            break;
                        }
                    case 0x90:
                        {
                            var velocity = reader.ReadByte();
                            track.Add(new MidiEvent
                            {
                                Tick = tick,
                                Type = velocity > 0 ? MidiEventType.NoteOn : MidiEventType.NoteOff,
                                Status = (Byte)status,
                                Channel = channel,
                                Data1 = data1 & 0x7F,
                                Data2 = velocity & 0x7F,
                            });
                            break;
                        }
                    case 0xA0:
                        {
                            var pressure = reader.ReadByte();
                            track.Add(new MidiEvent
                            {
                                Tick = tick,
                                Type = MidiEventType.PolyphonicKeyPressure,
                                Status = (Byte)status,
                                Channel = channel,
                                Data1 = data1 & 0x7F,
                                Data2 = pressure & 0x7F,
                            });
                            break;
                        }
                    case 0xB0:
                        {
                            var value = reader.ReadByte();
                            track.Add(new MidiEvent
                            {
                                Tick = tick,
                                Type = MidiEventType.ControlChange,
                                Status = (Byte)status,
                                Channel = channel,
                                Data1 = data1 & 0x7F,
                                Data2 = value & 0x7F,
                            });
                            break;
                        }
                    case 0xC0:
                        track.Add(new MidiEvent
                        {
                            Tick = tick,
                            Type = MidiEventType.ProgramChange,
                            Status = (Byte)status,
                            Channel = channel,
                            Data1 = data1 & 0x7F,
                        });
                        break;
                    case 0xD0:
                        track.Add(new MidiEvent
                        {
                            Tick = tick,
                            Type = MidiEventType.ChannelPressure,
                            Status = (Byte)status,
                            Channel = channel,
                            Data1 = data1 & 0x7F,
                        });
                        break;
                    case 0xE0:
                        {
                            var high = reader.ReadByte();
                            track.Add(new MidiEvent
                            {
                                Tick = tick,
                                Type = MidiEventType.PitchBend,
                                Status = (Byte)status,
                                Channel = channel,
                                Data1 = data1 & 0x7F,
                                Data2 = high & 0x7F,
                            });
                            break;
                        }
                    default:
                        throw new InvalidDataException("Unsupported MIDI channel message.");
                }
            }
            else if (status == 0xFF)
            {
                var metaType = reader.ReadByte();
                var size = reader.ReadVlq();
                if (size < 0 || size > Int32.MaxValue) throw new InvalidDataException("The MIDI meta event length is invalid.");

                var payload = reader.ReadBytes((Int32)size);
                track.Add(new MidiEvent
                {
                    Tick = tick,
                    Type = MidiEventType.Meta,
                    Status = 0xFF,
                    MetaType = metaType,
                    Data = payload,
                });

                if (metaType == 0x03 && name == null) name = MidiText.Decode(payload);
                if (metaType == 0x2F) break;
            }
            else if (status == 0xF0 || status == 0xF7)
            {
                var size = reader.ReadVlq();
                if (size < 0 || size > Int32.MaxValue) throw new InvalidDataException("The MIDI SysEx length is invalid.");

                track.Add(new MidiEvent
                {
                    Tick = tick,
                    Type = MidiEventType.SysEx,
                    Status = (Byte)status,
                    Data = reader.ReadBytes((Int32)size),
                });
            }
            else
            {
                // 系统公共/实时消息：按规范长度读取，保留原始字节
                var bytes = GetSystemDataLength(status);
                track.Add(new MidiEvent
                {
                    Tick = tick,
                    Type = MidiEventType.System,
                    Status = (Byte)status,
                    Data = reader.ReadBytes(bytes),
                });
            }
        }

        track.Name = name;
        return track;
    }

    private static Int32 GetSystemDataLength(Int32 status) => status switch
    {
        0xF1 => 1,
        0xF2 => 2,
        0xF3 => 1,
        0xF6 => 0,
        >= 0xF8 and <= 0xFE => 0,
        _ => throw new InvalidDataException("Unsupported MIDI system message."),
    };

    private sealed class Cursor
    {
        private readonly Byte[] _data;
        private readonly Int32 _end;
        private Int32 _position;

        public Cursor(Byte[] data, Int32 offset, Int32 length)
        {
            _data = data;
            _position = offset;
            _end = offset + length;
        }

        public Boolean End => _position >= _end;

        public Byte ReadByte()
        {
            if (_position >= _end) throw new InvalidDataException("Unexpected end of MIDI track data.");
            return _data[_position++];
        }

        public Byte[] ReadBytes(Int32 count)
        {
            if (count < 0 || _position + count > _end) throw new InvalidDataException("Unexpected end of MIDI track data.");

            var buffer = new Byte[count];
            if (count > 0) Array.Copy(_data, _position, buffer, 0, count);
            _position += count;
            return buffer;
        }

        public Int64 ReadVlq()
        {
            var value = 0L;
            for (var i = 0; i < 4; i++)
            {
                var b = ReadByte();
                value = (value << 7) | (UInt32)(b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }

            throw new InvalidDataException("The MIDI variable-length quantity is too long.");
        }
    }
}
