using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NewLife.Audio.Midi;

/// <summary>WinMM MIDI 输入</summary>
/// <remarks>当前只处理短消息（Note/CC/Program/PitchBend/Aftertouch），SysEx 长消息暂不透出。</remarks>
internal sealed class WinMmMidiInput : IMidiInput
{
    private readonly WinMmNative.MidiInCallback _callback;
    private IntPtr _handle;
    private Boolean _disposed;

    public MidiDeviceInfo Device { get; }

    public event EventHandler<MidiEvent> MessageReceived;

    private WinMmMidiInput(IntPtr handle, MidiDeviceInfo device)
    {
        _handle = handle;
        Device = device;
        _callback = OnMessage;
    }

    public static IList<MidiDeviceInfo> Enumerate()
    {
        var list = new List<MidiDeviceInfo>();
        var count = WinMmNative.MidiInGetNumDevs();
        for (var i = 0; i < count; i++)
        {
            var caps = new WinMmNative.MidiInCaps();
            var size = (UInt32)Marshal.SizeOf<WinMmNative.MidiInCaps>();
            if (WinMmNative.MidiInGetDevCaps((UIntPtr)i, ref caps, size) != 0) continue;

            list.Add(new MidiDeviceInfo
            {
                Index = i,
                Name = caps.Name ?? String.Empty,
                Manufacturer = $"{caps.ManufacturerId:X4}",
                Version = (Int32)caps.DriverVersion,
            });
        }

        return list;
    }

    public static IMidiInput Open(Int32 index)
    {
        var input = new WinMmMidiInput(IntPtr.Zero, new MidiDeviceInfo { Index = index, Name = $"MIDI In {index}" });
        var result = WinMmNative.MidiInOpen(out var handle, (UInt32)index, input._callback, IntPtr.Zero, WinMmNative.CallbackFunction);
        if (result != 0) throw new InvalidOperationException($"midiInOpen failed with error {result}.");

        input._handle = handle;

        var devices = Enumerate();
        foreach (var item in devices)
        {
            if (item.Index == index)
            {
                input.Device.Name = item.Name;
                input.Device.Manufacturer = item.Manufacturer;
                input.Device.Version = item.Version;
                break;
            }
        }

        return input;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinMmMidiInput));

        var result = WinMmNative.MidiInStart(_handle);
        if (result != 0) throw new InvalidOperationException($"midiInStart failed with error {result}.");
    }

    public void Stop()
    {
        if (_disposed) return;

        WinMmNative.MidiInStop(_handle);
        WinMmNative.MidiInReset(_handle);
    }

    private void OnMessage(IntPtr handle, UInt32 message, UIntPtr instance, UIntPtr param1, UIntPtr param2)
    {
        if (message != WinMmNative.MimData) return;

        var value = (UInt32)param1.ToUInt64();
        var status = (Byte)(value & 0xFF);
        var data1 = (Byte)((value >> 8) & 0x7F);
        var data2 = (Byte)((value >> 16) & 0x7F);
        var e = CreateEvent(status, data1, data2);
        if (e != null) MessageReceived?.Invoke(this, e);
    }

    private static MidiEvent CreateEvent(Byte status, Byte data1, Byte data2)
    {
        var type = status & 0xF0;
        var channel = status & 0x0F;

        return type switch
        {
            0x80 => MidiEvent.NoteOff(0, channel, data1, data2),
            0x90 => data2 > 0 ? MidiEvent.NoteOn(0, channel, data1, data2) : MidiEvent.NoteOff(0, channel, data1),
            0xA0 => new MidiEvent
            {
                Type = MidiEventType.PolyphonicKeyPressure,
                Status = status,
                Channel = channel,
                Data1 = data1,
                Data2 = data2,
            },
            0xB0 => MidiEvent.ControlChange(0, channel, data1, data2),
            0xC0 => MidiEvent.ProgramChange(0, channel, data1),
            0xD0 => new MidiEvent
            {
                Type = MidiEventType.ChannelPressure,
                Status = status,
                Channel = channel,
                Data1 = data1,
            },
            0xE0 => MidiEvent.PitchBend(0, channel, ((data2 & 0x7F) << 7 | data1) - 8192),
            _ => null,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            WinMmNative.MidiInStop(_handle);
            WinMmNative.MidiInReset(_handle);
            WinMmNative.MidiInClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
