using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NewLife.Audio.Midi;

/// <summary>WinMM MIDI 输出</summary>
internal sealed class WinMmMidiOutput : IMidiOutput
{
    private IntPtr _handle;
    private Boolean _disposed;

    public MidiDeviceInfo Device { get; }

    private WinMmMidiOutput(IntPtr handle, MidiDeviceInfo device)
    {
        _handle = handle;
        Device = device;
    }

    public static IList<MidiDeviceInfo> Enumerate()
    {
        var list = new List<MidiDeviceInfo>();
        var count = WinMmNative.MidiOutGetNumDevs();
        for (var i = 0; i < count; i++)
        {
            var caps = new WinMmNative.MidiOutCaps();
            var size = (UInt32)Marshal.SizeOf<WinMmNative.MidiOutCaps>();
            if (WinMmNative.MidiOutGetDevCaps((UIntPtr)i, ref caps, size) != 0) continue;

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

    public static IMidiOutput Open(Int32 index)
    {
        var result = WinMmNative.MidiOutOpen(out var handle, (UInt32)index, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new InvalidOperationException($"midiOutOpen failed with error {result}.");

        var devices = Enumerate();
        var device = index >= 0 && index < devices.Count
            ? devices[index]
            : new MidiDeviceInfo { Index = index, Name = $"MIDI Out {index}" };

        return new WinMmMidiOutput(handle, device);
    }

    public void SendShortMessage(Byte status, Byte data1, Byte data2 = 0)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinMmMidiOutput));

        var message = (UInt32)status | ((UInt32)data1 << 8) | ((UInt32)data2 << 16);
        var result = WinMmNative.MidiOutShortMsg(_handle, message);
        if (result != 0) throw new InvalidOperationException($"midiOutShortMsg failed with error {result}.");
    }

    public void SendSysEx(Byte[] data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinMmMidiOutput));
        if (data == null) throw new ArgumentNullException(nameof(data));

        var buffer = new Byte[data.Length + 2];
        buffer[0] = 0xF0;
        Array.Copy(data, 0, buffer, 1, data.Length);
        buffer[buffer.Length - 1] = 0xF7;

        var headerSize = Marshal.SizeOf<WinMmNative.MidiHdr>();
        var dataPtr = Marshal.AllocHGlobal(buffer.Length);
        var headerPtr = Marshal.AllocHGlobal(headerSize);

        try
        {
            Marshal.Copy(buffer, 0, dataPtr, buffer.Length);
            var header = new WinMmNative.MidiHdr
            {
                Data = dataPtr,
                BufferLength = (UInt32)buffer.Length,
                BytesRecorded = (UInt32)buffer.Length,
            };
            Marshal.StructureToPtr(header, headerPtr, false);

            var result = WinMmNative.MidiOutPrepareHeader(_handle, headerPtr, (UInt32)headerSize);
            if (result != 0) throw new InvalidOperationException($"midiOutPrepareHeader failed with error {result}.");

            try
            {
                result = WinMmNative.MidiOutLongMsg(_handle, headerPtr, (UInt32)headerSize);
                if (result != 0) throw new InvalidOperationException($"midiOutLongMsg failed with error {result}.");

                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 2000)
                {
                    var current = Marshal.PtrToStructure<WinMmNative.MidiHdr>(headerPtr);
                    if ((current.Flags & WinMmNative.MhdrDone) != 0) break;
                    Thread.Sleep(1);
                }
            }
            finally
            {
                WinMmNative.MidiOutUnprepareHeader(_handle, headerPtr, (UInt32)headerSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(headerPtr);
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    public void Send(MidiEvent e)
    {
        if (e == null) throw new ArgumentNullException(nameof(e));

        switch (e.Type)
        {
            case MidiEventType.NoteOff:
            case MidiEventType.NoteOn:
            case MidiEventType.PolyphonicKeyPressure:
            case MidiEventType.ControlChange:
            case MidiEventType.PitchBend:
                SendShortMessage(e.Status, (Byte)e.Data1, (Byte)e.Data2);
                break;
            case MidiEventType.ProgramChange:
            case MidiEventType.ChannelPressure:
                SendShortMessage(e.Status, (Byte)e.Data1);
                break;
            case MidiEventType.SysEx:
                SendSysEx(e.Data ?? []);
                break;
        }
    }

    public void Reset()
    {
        if (_disposed) return;

        for (var channel = 0; channel < 16; channel++)
        {
            SendShortMessage((Byte)(0xB0 | channel), 123, 0);
            SendShortMessage((Byte)(0xB0 | channel), 120, 0);
        }
        WinMmNative.MidiOutReset(_handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            WinMmNative.MidiOutReset(_handle);
            WinMmNative.MidiOutClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
