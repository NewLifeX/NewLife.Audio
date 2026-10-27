using System;
using System.Runtime.InteropServices;

namespace NewLife.Audio.Midi;

/// <summary>WinMM MIDI 原生互操作</summary>
internal static class WinMmNative
{
    public const UInt32 CallbackFunction = 0x00030000;
    public const UInt32 MhdrDone = 0x00000001;
    public const UInt32 MhdrPrepared = 0x00000002;
    public const UInt32 MimData = 0x3C3;
    public const UInt32 MimLongData = 0x3C4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MidiOutCaps
    {
        public UInt16 ManufacturerId;
        public UInt16 ProductId;
        public UInt32 DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public String Name;
        public UInt16 Technology;
        public UInt16 Voices;
        public UInt16 Notes;
        public UInt16 ChannelMask;
        public UInt32 Support;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MidiInCaps
    {
        public UInt16 ManufacturerId;
        public UInt16 ProductId;
        public UInt32 DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public String Name;
        public UInt16 Support;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MidiHdr
    {
        public IntPtr Data;
        public UInt32 BufferLength;
        public UInt32 BytesRecorded;
        public IntPtr User;
        public UInt32 Flags;
        public IntPtr Next;
        public IntPtr Reserved;
        public UInt32 Offset;
        public IntPtr Reserved2;
    }

    public delegate void MidiInCallback(IntPtr handle, UInt32 message, UIntPtr instance, UIntPtr param1, UIntPtr param2);

    [DllImport("winmm.dll", EntryPoint = "midiOutGetNumDevs")]
    public static extern UInt32 MidiOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode)]
    public static extern UInt32 MidiOutGetDevCaps(UIntPtr deviceId, ref MidiOutCaps caps, UInt32 size);

    [DllImport("winmm.dll", EntryPoint = "midiOutOpen")]
    public static extern UInt32 MidiOutOpen(out IntPtr handle, UInt32 deviceId, IntPtr callback, IntPtr instance, UInt32 flags);

    [DllImport("winmm.dll", EntryPoint = "midiOutClose")]
    public static extern UInt32 MidiOutClose(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiOutShortMsg")]
    public static extern UInt32 MidiOutShortMsg(IntPtr handle, UInt32 message);

    [DllImport("winmm.dll", EntryPoint = "midiOutLongMsg")]
    public static extern UInt32 MidiOutLongMsg(IntPtr handle, IntPtr header, UInt32 size);

    [DllImport("winmm.dll", EntryPoint = "midiOutPrepareHeader")]
    public static extern UInt32 MidiOutPrepareHeader(IntPtr handle, IntPtr header, UInt32 size);

    [DllImport("winmm.dll", EntryPoint = "midiOutUnprepareHeader")]
    public static extern UInt32 MidiOutUnprepareHeader(IntPtr handle, IntPtr header, UInt32 size);

    [DllImport("winmm.dll", EntryPoint = "midiOutReset")]
    public static extern UInt32 MidiOutReset(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInGetNumDevs")]
    public static extern UInt32 MidiInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)]
    public static extern UInt32 MidiInGetDevCaps(UIntPtr deviceId, ref MidiInCaps caps, UInt32 size);

    [DllImport("winmm.dll", EntryPoint = "midiInOpen")]
    public static extern UInt32 MidiInOpen(out IntPtr handle, UInt32 deviceId, MidiInCallback callback, IntPtr instance, UInt32 flags);

    [DllImport("winmm.dll", EntryPoint = "midiInClose")]
    public static extern UInt32 MidiInClose(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInStart")]
    public static extern UInt32 MidiInStart(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInStop")]
    public static extern UInt32 MidiInStop(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInReset")]
    public static extern UInt32 MidiInReset(IntPtr handle);
}
