using System.Runtime.InteropServices;
using NewLife;
using NewLife.Audio.DSP;
using NewLife.Log;

namespace NewLife.Audio.Devices;

/// <summary>Windows WASAPI 驱动（Windows Audio Session API，COM 互操作）</summary>
/// <remarks>
/// 通过 COM 互操作调用 Windows Core Audio API：<br/>
/// 1. <c>IMMDeviceEnumerator</c> 枚举端点，<c>IPropertyStore</c> 读取友好名称；<br/>
/// 2. 共享模式（AUDCLNT_SHAREMODE_SHARED）播放：<c>IAudioClient</c> + <c>IAudioRenderClient</c>，后台线程按设备缓冲填充；<br/>
/// 3. 共享模式采集：<c>IAudioCaptureClient</c>；渲染端点加 <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> 即系统声音回采。<br/>
/// 设备混音格式与请求格式不一致（采样率/声道/位深）时，内部完成格式转换与线性重采样。<br/>
/// 非 Windows 平台或 COM 激活失败时明确抛出异常，不再静默回退到空实现。
/// </remarks>
public class WasapiDriver : IAudioDriver
{
    /// <summary>当前平台是否支持 WASAPI</summary>
    public static Boolean IsPlatformSupported => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>驱动名称</summary>
    public String Name => "Windows WASAPI";

    /// <summary>枚举音频设备（输出 + 输入）</summary>
    /// <returns>设备列表；非 Windows 或没有活动端点时返回空列表</returns>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (!IsPlatformSupported) return [];

        var devices = new List<AudioDeviceInfo>();
        devices.AddRange(WasapiInterop.Enumerate(false));
        devices.AddRange(WasapiInterop.Enumerate(true));
        return devices;
    }

    /// <summary>获取默认输出设备</summary>
    /// <returns>默认渲染端点；不可用时返回 null</returns>
    public AudioDeviceInfo GetDefaultOutputDevice() => EnumerateDevices().FirstOrDefault(e => !e.IsInput && e.IsDefault);

    /// <summary>获取默认输入设备</summary>
    /// <returns>默认采集端点；不可用时返回 null</returns>
    public AudioDeviceInfo GetDefaultInputDevice() => EnumerateDevices().FirstOrDefault(e => e.IsInput && e.IsDefault);

    /// <summary>创建播放器</summary>
    /// <returns>WASAPI 共享模式播放器</returns>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    public IAudioPlayer CreatePlayer()
    {
        if (!IsPlatformSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        return new WasapiPlayer();
    }

    /// <summary>创建录制器</summary>
    /// <returns>WASAPI 共享模式录制器</returns>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    public IAudioRecorder CreateRecorder()
    {
        if (!IsPlatformSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        return new WasapiRecorder(false);
    }

    /// <summary>创建系统声音回采录制器</summary>
    /// <returns>基于渲染端点 + loopback 标志的录制器</returns>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    public IAudioRecorder CreateLoopbackRecorder()
    {
        if (!IsPlatformSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        return new WasapiRecorder(true);
    }
}

#region 内部实现
/// <summary>WASAPI 格式描述（WAVEFORMATEX / WAVEFORMATEXTENSIBLE）</summary>
internal sealed class WasapiAudioFormat
{
    /// <summary>格式标签：1=PCM，3=IEEE float</summary>
    public UInt16 Tag { get; private set; }

    /// <summary>声道数</summary>
    public Int32 Channels { get; private set; }

    /// <summary>采样率</summary>
    public Int32 SampleRate { get; private set; }

    /// <summary>位深</summary>
    public Int32 BitsPerSample { get; private set; }

    /// <summary>每帧字节数</summary>
    public Int32 BlockAlign { get; private set; }

    /// <summary>是否为浮点格式</summary>
    public Boolean IsFloat => Tag == 3;

    /// <summary>原始格式字节（WAVEFORMATEX 18 字节或 WAVEFORMATEXTENSIBLE 40 字节）</summary>
    public Byte[] Raw { get; private set; }

    /// <summary>按 <see cref="AudioFormat"/> 构造 PCM 格式</summary>
    /// <param name="format">音频格式</param>
    /// <returns>WASAPI 格式</returns>
    public static WasapiAudioFormat FromAudio(AudioFormat format)
    {
        var bits = format.BitsPerSample;
        var blockAlign = format.Channels * bits / 8;

        return new WasapiAudioFormat
        {
            Tag = 1,
            Channels = format.Channels,
            SampleRate = format.SampleRate,
            BitsPerSample = bits,
            BlockAlign = blockAlign,
            Raw = BuildWaveFormat(1, format.Channels, format.SampleRate, bits, blockAlign),
        };
    }

    /// <summary>从非托管内存读取格式</summary>
    /// <param name="ptr">WAVEFORMATEX 指针</param>
    /// <returns>WASAPI 格式</returns>
    public static WasapiAudioFormat Read(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) throw new ArgumentNullException(nameof(ptr));

        var tag = (UInt16)Marshal.ReadInt16(ptr, 0);
        var channels = (Int32)(UInt16)Marshal.ReadInt16(ptr, 2);
        var sampleRate = Marshal.ReadInt32(ptr, 4);
        var blockAlign = (Int32)(UInt16)Marshal.ReadInt16(ptr, 12);
        var bits = (Int32)(UInt16)Marshal.ReadInt16(ptr, 14);
        var cbSize = (Int32)(UInt16)Marshal.ReadInt16(ptr, 16);

        var size = 18 + cbSize;
        if (size > 40) size = 40;

        var raw = new Byte[size];
        Marshal.Copy(ptr, raw, 0, size);

        if (tag == 0xFFFE && size >= 40)
        {
            // WAVEFORMATEXTENSIBLE：SubFormat GUID 起始 4 字节即实际格式标签
            tag = (UInt16)Marshal.ReadInt32(ptr, 24);
        }

        return new WasapiAudioFormat
        {
            Tag = tag,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bits,
            BlockAlign = blockAlign,
            Raw = raw,
        };
    }

    /// <summary>复制格式字节到非托管内存</summary>
    /// <returns>调用方负责 <see cref="Marshal.FreeHGlobal"/> 的指针</returns>
    public IntPtr AllocRaw()
    {
        var ptr = Marshal.AllocHGlobal(Raw.Length);
        Marshal.Copy(Raw, 0, ptr, Raw.Length);
        return ptr;
    }

    private static Byte[] BuildWaveFormat(UInt16 tag, Int32 channels, Int32 sampleRate, Int32 bits, Int32 blockAlign)
    {
        var raw = new Byte[18];
        BitConverter.GetBytes(tag).CopyTo(raw, 0);
        BitConverter.GetBytes((Int16)channels).CopyTo(raw, 2);
        BitConverter.GetBytes(sampleRate).CopyTo(raw, 4);
        BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(raw, 8);
        BitConverter.GetBytes((Int16)blockAlign).CopyTo(raw, 12);
        BitConverter.GetBytes((Int16)bits).CopyTo(raw, 14);
        BitConverter.GetBytes((Int16)0).CopyTo(raw, 16);
        return raw;
    }
}

/// <summary>WASAPI 设备与客户端互操作</summary>
internal static class WasapiInterop
{
    public const Int32 E_RENDER = 0;
    public const Int32 E_CAPTURE = 1;
    public const Int32 E_CONSOLE = 0;
    public const UInt32 DEVICE_STATE_ACTIVE = 0x00000001;
    public const Int32 SHAREMODE_SHARED = 0;
    public const UInt32 STREAMFLAGS_LOOPBACK = 0x00020000;
    public const UInt32 BUFFERFLAGS_SILENT = 0x00000002;
    public const UInt32 STGM_READ = 0;
    public const UInt32 CLSCTX_ALL = 23;
    public const Int64 REFTIMES_PER_SEC = 10_000_000;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    /// <summary>创建设备枚举器</summary>
    /// <returns>设备枚举器</returns>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    public static IMMDeviceEnumerator CreateEnumerator()
    {
        if (!WasapiDriver.IsPlatformSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        if (type == null) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        var instance = Activator.CreateInstance(type);
        return instance as IMMDeviceEnumerator
            ?? throw new PlatformNotSupportedException(L.T("No audio backend is available."));
    }

    /// <summary>枚举指定方向的端点</summary>
    /// <param name="input">true 枚举输入（采集）端点</param>
    /// <returns>设备列表</returns>
    public static List<AudioDeviceInfo> Enumerate(Boolean input)
    {
        var list = new List<AudioDeviceInfo>();
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(input ? E_CAPTURE : E_RENDER, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection == null)
                return list;

            try
            {
                if (collection.GetCount(out var count) != 0) return list;

                var defaultId = GetDefaultId(enumerator, input);
                for (var i = 0u; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0 || device == null) continue;

                    try
                    {
                        if (device.GetId(out var id) != 0 || id.IsNullOrEmpty()) continue;

                        var name = GetFriendlyName(device);
                        list.Add(new AudioDeviceInfo
                        {
                            Id = id,
                            Name = name.IsNullOrEmpty() ? id : name,
                            IsInput = input,
                            IsDefault = String.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(collection);
            }
        }
        finally
        {
            Release(enumerator);
        }

        return list;
    }

    /// <summary>获取指定方向的默认端点 ID</summary>
    /// <param name="enumerator">设备枚举器</param>
    /// <param name="input">true 取采集端点</param>
    /// <returns>设备 ID；失败返回 null</returns>
    public static String GetDefaultId(IMMDeviceEnumerator enumerator, Boolean input)
    {
        if (enumerator.GetDefaultAudioEndpoint(input ? E_CAPTURE : E_RENDER, E_CONSOLE, out var device) != 0 || device == null) return null;

        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>激活指定端点的 <c>IAudioClient</c></summary>
    /// <param name="enumerator">设备枚举器</param>
    /// <param name="input">true 激活采集端点</param>
    /// <param name="deviceId">设备 ID；为空时使用默认端点</param>
    /// <returns>音频客户端</returns>
    /// <exception cref="InvalidOperationException">没有可用端点或激活失败</exception>
    public static IAudioClient ActivateClient(IMMDeviceEnumerator enumerator, Boolean input, String deviceId)
    {
        IMMDevice device = null;
        try
        {
            var hr = deviceId.IsNullOrEmpty()
                ? enumerator.GetDefaultAudioEndpoint(input ? E_CAPTURE : E_RENDER, E_CONSOLE, out device)
                : enumerator.GetDevice(deviceId, out device);

            if (hr != 0 || device == null) throw new InvalidOperationException(L.T("No default audio device is available."));

            var iid = IID_IAudioClient;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance);
            if (hr != 0 || instance is not IAudioClient client)
                throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr));

            return client;
        }
        finally
        {
            if (device != null) Release(device);
        }
    }

    /// <summary>选择实际使用的设备格式</summary>
    /// <param name="client">音频客户端</param>
    /// <param name="requested">请求格式</param>
    /// <param name="forceMixFormat">true 时强制使用设备混音格式（loopback 必须）</param>
    /// <returns>初始化 <c>IAudioClient</c> 时应使用的格式</returns>
    public static WasapiAudioFormat ChooseFormat(IAudioClient client, WasapiAudioFormat requested, Boolean forceMixFormat = false)
    {
        if (!forceMixFormat)
        {
            var ptr = requested.AllocRaw();
            try
            {
                var hr = client.IsFormatSupported(SHAREMODE_SHARED, ptr, out var closest);
                if (hr == 0) return requested;

                if (closest != IntPtr.Zero)
                {
                    try
                    {
                        return WasapiAudioFormat.Read(closest);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(closest);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        if (client.GetMixFormat(out var mix) != 0 || mix == IntPtr.Zero)
            throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", -1));

        try
        {
            return WasapiAudioFormat.Read(mix);
        }
        finally
        {
            Marshal.FreeCoTaskMem(mix);
        }
    }

    private static String GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store == null) return null;

        try
        {
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;

            try
            {
                return value.vt == 31 ? Marshal.PtrToStringUni(value.data) : null;
            }
            finally
            {
                // 仅 VT_LPWSTR 的指针由 COM 分配，需要 CoTaskMemFree 释放；其它类型是内联值，不能当指针处理
                if (value.vt == 31 && value.data != IntPtr.Zero) Marshal.FreeCoTaskMem(value.data);
            }
        }
        finally
        {
            Release(store);
        }
    }

    public static void Release(Object instance)
    {
        if (instance == null) return;

        try
        {
            if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
        catch
        {
            // 释放失败不影响主流程
        }
    }
}

/// <summary>PCM 格式转换器。在请求格式与设备混音格式之间转换采样格式、声道数与采样率</summary>
internal sealed class WasapiPcmConverter
{
    private readonly WasapiAudioFormat _source;
    private readonly WasapiAudioFormat _target;
    private readonly Int32 _sourceBlockAlign;
    private readonly Int32 _targetBlockAlign;
    private readonly Boolean _passthrough;
    private readonly Double _ratio;
    private Double _position;

    public WasapiPcmConverter(WasapiAudioFormat source, WasapiAudioFormat target)
    {
        _source = source;
        _target = target;
        _sourceBlockAlign = source.BlockAlign;
        _targetBlockAlign = target.BlockAlign;
        _ratio = (Double)source.SampleRate / target.SampleRate;
        _passthrough = source.Tag == target.Tag && source.Channels == target.Channels &&
                       source.SampleRate == target.SampleRate && source.BitsPerSample == target.BitsPerSample;
    }

    /// <summary>是否需要转换</summary>
    public Boolean IsPassthrough => _passthrough;

    /// <summary>转换一段 PCM 数据</summary>
    /// <param name="input">源格式 PCM 字节</param>
    /// <returns>目标格式 PCM 字节</returns>
    public Byte[] Convert(ReadOnlySpan<Byte> input)
    {
        if (input.Length == 0) return [];
        if (_passthrough) return input.ToArray();
        if (_sourceBlockAlign <= 0 || _targetBlockAlign <= 0) return [];

        // 1) 解码为 float 交错样本
        var srcFrames = input.Length / _sourceBlockAlign;
        if (srcFrames == 0) return [];

        var src = new Single[srcFrames * _source.Channels];
        Decode(input, src);

        // 2) 声道映射
        var mapped = MapChannels(src, srcFrames, _source.Channels, _target.Channels);

        // 3) 采样率转换
        var frames = _source.SampleRate == _target.SampleRate
            ? mapped
            : Resample(mapped, srcFrames, _source.Channels, _target.Channels);
        var outFrames = frames.Length / _target.Channels;
        if (outFrames == 0) return [];

        // 4) 编码为目标格式
        return Encode(frames, outFrames);
    }

    private void Decode(ReadOnlySpan<Byte> input, Single[] output)
    {
        var count = output.Length;

        if (_source.IsFloat && _source.BitsPerSample == 32)
        {
            for (var i = 0; i < count; i++)
            {
                output[i] = BitConverter.ToSingle(input.Slice(i * 4, 4).ToArray(), 0);
            }
            return;
        }

        switch (_source.BitsPerSample)
        {
            case 8:
                for (var i = 0; i < count; i++)
                {
                    output[i] = (input[i] - 128) / 128f;
                }
                break;

            case 16:
                for (var i = 0; i < count; i++)
                {
                    var value = (Int16)(input[i * 2] | input[i * 2 + 1] << 8);
                    output[i] = value / 32768f;
                }
                break;

            case 24:
                for (var i = 0; i < count; i++)
                {
                    var offset = i * 3;
                    var value = input[offset] | input[offset + 1] << 8 | input[offset + 2] << 16;
                    if ((value & 0x800000) != 0) value |= unchecked((Int32)0xFF000000);
                    output[i] = value / 8388608f;
                }
                break;

            case 32:
                for (var i = 0; i < count; i++)
                {
                    var value = input[i * 4] | input[i * 4 + 1] << 8 | input[i * 4 + 2] << 16 | input[i * 4 + 3] << 24;
                    output[i] = (Single)(value / 2147483648.0);
                }
                break;

            default:
                throw new NotSupportedException(L.T("The requested audio format is not supported: {0}.", _source.BitsPerSample));
        }
    }

    private static Single[] MapChannels(Single[] input, Int32 frames, Int32 srcChannels, Int32 dstChannels)
    {
        if (srcChannels == dstChannels) return input;

        var output = new Single[frames * dstChannels];

        if (srcChannels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var value = input[i];
                for (var c = 0; c < dstChannels; c++) output[i * dstChannels + c] = value;
            }
            return output;
        }

        if (dstChannels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var sum = 0f;
                for (var c = 0; c < srcChannels; c++) sum += input[i * srcChannels + c];
                output[i] = sum / srcChannels;
            }
            return output;
        }

        var copy = Math.Min(srcChannels, dstChannels);
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < copy; c++) output[i * dstChannels + c] = input[i * srcChannels + c];
        }
        return output;
    }

    private Single[] Resample(Single[] input, Int32 frames, Int32 srcChannels, Int32 dstChannels)
    {
        var ratio = _ratio;
        var output = new Single[(Int32)Math.Ceiling(frames / ratio + 2) * dstChannels];
        var outFrames = 0;

        var position = _position;
        while (position < frames - 1)
        {
            var index = (Int32)position;
            var frac = (Single)(position - index);

            for (var c = 0; c < dstChannels; c++)
            {
                var c0 = Math.Min(c, srcChannels - 1);
                var s0 = input[index * srcChannels + c0];
                var s1 = input[(index + 1) * srcChannels + c0];
                output[outFrames * dstChannels + c] = s0 + (s1 - s0) * frac;
            }

            outFrames++;
            position += ratio;

            if (outFrames * dstChannels >= output.Length) break;
        }

        _position = position - frames;
        if (_position < 0) _position = 0;

        if (outFrames * dstChannels == output.Length) return output;

        var trimmed = new Single[outFrames * dstChannels];
        Array.Copy(output, trimmed, trimmed.Length);
        return trimmed;
    }

    private Byte[] Encode(Single[] input, Int32 frames)
    {
        var count = frames * _target.Channels;
        var output = new Byte[count * _target.BitsPerSample / 8];

        if (_target.IsFloat && _target.BitsPerSample == 32)
        {
            for (var i = 0; i < count; i++)
            {
                var bytes = BitConverter.GetBytes(input[i]);
                output[i * 4] = bytes[0];
                output[i * 4 + 1] = bytes[1];
                output[i * 4 + 2] = bytes[2];
                output[i * 4 + 3] = bytes[3];
            }
            return output;
        }

        switch (_target.BitsPerSample)
        {
            case 8:
                for (var i = 0; i < count; i++)
                {
                    var value = (Int32)(input[i] * 128) + 128;
                    output[i] = (Byte)Math.Max(0, Math.Min(255, value));
                }
                break;

            case 16:
                for (var i = 0; i < count; i++)
                {
                    var value = (Int32)(input[i] * 32767);
                    value = Math.Max(-32768, Math.Min(32767, value));
                    output[i * 2] = (Byte)(value & 0xFF);
                    output[i * 2 + 1] = (Byte)((value >> 8) & 0xFF);
                }
                break;

            case 24:
                for (var i = 0; i < count; i++)
                {
                    var value = (Int32)(input[i] * 8388607);
                    value = Math.Max(-8388608, Math.Min(8388607, value));
                    output[i * 3] = (Byte)(value & 0xFF);
                    output[i * 3 + 1] = (Byte)((value >> 8) & 0xFF);
                    output[i * 3 + 2] = (Byte)((value >> 16) & 0xFF);
                }
                break;

            case 32:
                for (var i = 0; i < count; i++)
                {
                    var value = (Int32)(input[i] * 2147483647.0);
                    output[i * 4] = (Byte)(value & 0xFF);
                    output[i * 4 + 1] = (Byte)((value >> 8) & 0xFF);
                    output[i * 4 + 2] = (Byte)((value >> 16) & 0xFF);
                    output[i * 4 + 3] = (Byte)((value >> 24) & 0xFF);
                }
                break;

            default:
                throw new NotSupportedException(L.T("The requested audio format is not supported: {0}.", _target.BitsPerSample));
        }

        return output;
    }
}

/// <summary>字节环形队列。用于播放器暂存待渲染 PCM</summary>
internal sealed class WasapiByteQueue
{
    private Byte[] _buffer = [];
    private Int32 _head;
    private Int32 _count;

    public Int32 Count => _count;

    public void Enqueue(ReadOnlySpan<Byte> data)
    {
        if (data.Length == 0) return;

        Ensure(data.Length + _count);
        var tail = (_head + _count) % _buffer.Length;
        var first = Math.Min(data.Length, _buffer.Length - tail);
        data.Slice(0, first).CopyTo(_buffer.AsSpan(tail));
        if (first < data.Length) data.Slice(first).CopyTo(_buffer.AsSpan(0));
        _count += data.Length;
    }

    public Int32 Dequeue(Byte[] target, Int32 offset, Int32 max)
    {
        var count = Math.Min(max, _count);
        if (count <= 0) return 0;

        var first = Math.Min(count, _buffer.Length - _head);
        Array.Copy(_buffer, _head, target, offset, first);
        if (first < count) Array.Copy(_buffer, 0, target, offset + first, count - first);

        _head = (_head + count) % _buffer.Length;
        _count -= count;
        if (_count == 0) _head = 0;

        return count;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    private void Ensure(Int32 size)
    {
        if (_buffer.Length >= size) return;

        var capacity = Math.Max(4096, _buffer.Length);
        while (capacity < size) capacity <<= 1;

        var buffer = new Byte[capacity];
        if (_count > 0) Dequeue(buffer, 0, _count);
        _buffer = buffer;
        _head = 0;
    }
}

/// <summary>WASAPI 播放器（IAudioClient + IAudioRenderClient）</summary>
internal sealed class WasapiPlayer : IAudioPlayer
{
    private readonly Object _sync = new();
    private readonly WasapiByteQueue _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private AudioFormat _format;
    private String _deviceId;
    private WasapiPcmConverter _converter;
    private Thread _thread;
    private Exception _initError;
    private volatile Boolean _running;
    private volatile Boolean _playing;
    private Boolean _disposed;

    /// <summary>待播放字节数</summary>
    public Int32 BufferedBytes
    {
        get { lock (_sync) return _queue.Count; }
    }

    /// <summary>播放停止事件</summary>
    public event EventHandler PlaybackStopped;

    /// <summary>初始化播放器并等待设备就绪</summary>
    /// <param name="format">请求的 PCM 格式</param>
    /// <param name="deviceId">设备 ID；为空时使用默认渲染端点</param>
    /// <exception cref="InvalidOperationException">设备不可用或初始化失败</exception>
    public void Init(AudioFormat format, String deviceId = null)
    {
        ThrowIfDisposed();

        _format = format?.Clone() ?? AudioFormat.Default;
        _deviceId = deviceId;
        _initError = null;
        _ready.Reset();
        _running = true;

        _thread = new Thread(RenderThread)
        {
            IsBackground = true,
            Name = "WasapiPlayer",
        };
        _thread.Start();

        if (!_ready.Wait(5000))
        {
            _running = false;
            throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", "timeout"));
        }

        if (_initError != null)
        {
            _running = false;
            throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", _initError.Message), _initError);
        }
    }

    /// <summary>开始播放</summary>
    public void Play() => _playing = true;

    /// <summary>暂停播放</summary>
    public void Pause() => _playing = false;

    /// <summary>停止播放</summary>
    public void Stop()
    {
        _playing = false;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>写入 PCM 数据（请求格式），内部转换为设备格式后排队</summary>
    /// <param name="data">PCM 数据</param>
    public void Write(Byte[] data)
    {
        if (data == null || data.Length == 0 || _disposed) return;

        lock (_sync)
        {
            if (_converter == null) return;

            var converted = _converter.Convert(data);
            if (converted.Length == 0) return;

            // 限制积压为 2 秒，超出时丢弃最旧数据，避免内存持续增长
            var limit = _format.SampleRate * _format.Channels * _format.BytesPerSample * 2;
            if (_queue.Count + converted.Length > limit && _queue.Count > 0)
            {
                var drop = new Byte[Math.Max(0, _queue.Count + converted.Length - limit)];
                _queue.Dequeue(drop, 0, drop.Length);
            }

            _queue.Enqueue(converted);
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playing = false;
        _running = false;

        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive) thread.Join(1000);

        _ready.Dispose();
        lock (_sync) _queue.Clear();
    }

    private void RenderThread()
    {
        IMMDeviceEnumerator enumerator = null;
        IAudioClient client = null;
        IAudioRenderClient render = null;
        var started = false;

        try
        {
            enumerator = WasapiInterop.CreateEnumerator();
            client = WasapiInterop.ActivateClient(enumerator, false, _deviceId);

            var requested = WasapiAudioFormat.FromAudio(_format);
            var deviceFormat = WasapiInterop.ChooseFormat(client, requested);

            var ptr = deviceFormat.AllocRaw();
            try
            {
                var hr = client.Initialize(WasapiInterop.SHAREMODE_SHARED, 0, WasapiInterop.REFTIMES_PER_SEC / 10, 0, ptr, IntPtr.Zero);
                if (hr != 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var hr2 = client.GetBufferSize(out var bufferFrames);
            if (hr2 != 0 || bufferFrames == 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr2));

            var iid = WasapiInterop.IID_IAudioRenderClient;
            hr2 = client.GetService(ref iid, out var service);
            render = service as IAudioRenderClient;
            if (hr2 != 0 || render == null) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr2));

            lock (_sync)
            {
                _converter = new WasapiPcmConverter(requested, deviceFormat);
            }

            var blockAlign = deviceFormat.BlockAlign;
            var scratch = new Byte[(Int32)bufferFrames * blockAlign];

            _ready.Set();

            while (_running)
            {
                if (!_playing)
                {
                    if (started)
                    {
                        client.Stop();
                        started = false;
                    }
                    Thread.Sleep(5);
                    continue;
                }

                if (!started)
                {
                    var startHr = client.Start();
                    if (startHr != 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", startHr));
                    started = true;
                }

                Pump(render, client, bufferFrames, blockAlign, scratch);
                Thread.Sleep(5);
            }

            if (started) client.Stop();
        }
        catch (Exception ex)
        {
            _initError ??= ex;
            _ready.Set();
            if (_running) XTrace.Log.InfoPrefixed("WasapiPlayer: ", "Playback stopped: {0}", ex.Message);
        }
        finally
        {
            WasapiInterop.Release(render);
            WasapiInterop.Release(client);
            WasapiInterop.Release(enumerator);
            _ready.Set();
        }
    }

    private void Pump(IAudioRenderClient render, IAudioClient client, UInt32 bufferFrames, Int32 blockAlign, Byte[] scratch)
    {
        if (client.GetCurrentPadding(out var padding) != 0) return;

        var free = (Int32)bufferFrames - (Int32)padding;
        if (free <= 0) return;

        var bytes = free * blockAlign;
        if (bytes > scratch.Length) bytes = scratch.Length / blockAlign * blockAlign;
        if (bytes <= 0) return;

        if (render.GetBuffer((UInt32)free, out var ptr) != 0 || ptr == IntPtr.Zero) return;

        Int32 read;
        lock (_sync) read = _queue.Dequeue(scratch, 0, bytes);
        if (read < bytes) Array.Clear(scratch, read, bytes - read);

        Marshal.Copy(scratch, 0, ptr, bytes);
        render.ReleaseBuffer((UInt32)(bytes / blockAlign), read > 0 ? 0u : WasapiInterop.BUFFERFLAGS_SILENT);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WasapiPlayer));
    }
}

/// <summary>WASAPI 录制器（IAudioClient + IAudioCaptureClient）</summary>
internal sealed class WasapiRecorder : IAudioRecorder, IDisposable
{
    private readonly Boolean _loopback;
    private readonly ManualResetEventSlim _ready = new(false);
    private AudioFormat _format;
    private String _deviceId;
    private Thread _thread;
    private Exception _initError;
    private volatile Boolean _running;
    private Boolean _disposed;

    public WasapiRecorder(Boolean loopback) => _loopback = loopback;

    /// <summary>数据可用事件</summary>
    public event EventHandler<Byte[]> DataAvailable;

    /// <summary>请求的音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>是否正在录制</summary>
    public Boolean IsRecording => _running;

    /// <summary>初始化录制器</summary>
    /// <param name="format">请求的 PCM 格式</param>
    /// <param name="deviceId">设备 ID；为空时使用默认端点</param>
    public void Init(AudioFormat format, String deviceId = null)
    {
        ThrowIfDisposed();

        _format = format?.Clone() ?? AudioFormat.Default;
        _deviceId = deviceId;
        _initError = null;
        _ready.Reset();
    }

    /// <summary>开始录制</summary>
    /// <exception cref="InvalidOperationException">设备不可用或初始化失败</exception>
    public void StartRecording()
    {
        ThrowIfDisposed();
        if (_running) return;

        if (_format == null) _format = AudioFormat.Default;

        _initError = null;
        _ready.Reset();
        _running = true;

        _thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = _loopback ? "WasapiLoopbackRecorder" : "WasapiRecorder",
        };
        _thread.Start();

        if (!_ready.Wait(5000))
        {
            _running = false;
            throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", "timeout"));
        }

        if (_initError != null)
        {
            _running = false;
            throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", _initError.Message), _initError);
        }
    }

    /// <summary>停止录制</summary>
    public void StopRecording()
    {
        _running = false;

        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive) thread.Join(1000);
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        _ready.Dispose();
    }

    private void CaptureThread()
    {
        IMMDeviceEnumerator enumerator = null;
        IAudioClient client = null;
        IAudioCaptureClient capture = null;
        var started = false;

        try
        {
            enumerator = WasapiInterop.CreateEnumerator();

            // loopback 使用渲染端点的混音格式
            client = WasapiInterop.ActivateClient(enumerator, !_loopback, _deviceId);
            var requested = WasapiAudioFormat.FromAudio(_format);
            var deviceFormat = WasapiInterop.ChooseFormat(client, requested, _loopback);

            var flags = _loopback ? WasapiInterop.STREAMFLAGS_LOOPBACK : 0u;
            var ptr = deviceFormat.AllocRaw();
            try
            {
                var hr = client.Initialize(WasapiInterop.SHAREMODE_SHARED, flags, WasapiInterop.REFTIMES_PER_SEC / 10, 0, ptr, IntPtr.Zero);
                if (hr != 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var hr2 = client.GetBufferSize(out _);
            if (hr2 != 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr2));

            var iid = WasapiInterop.IID_IAudioCaptureClient;
            hr2 = client.GetService(ref iid, out var service);
            capture = service as IAudioCaptureClient;
            if (hr2 != 0 || capture == null) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr2));

            var converter = new WasapiPcmConverter(deviceFormat, requested);
            var blockAlign = deviceFormat.BlockAlign;

            hr2 = client.Start();
            if (hr2 != 0) throw new InvalidOperationException(L.T("Failed to initialize the WASAPI audio client: {0}", hr2));
            started = true;

            _ready.Set();

            while (_running)
            {
                if (capture.GetNextPacketSize(out var framesInNext) != 0) break;

                while (framesInNext > 0 && _running)
                {
                    if (capture.GetBuffer(out var data, out var frames, out var packetFlags, out _, out _) != 0) break;

                    try
                    {
                        var bytes = (Int32)frames * blockAlign;
                        if (bytes > 0)
                        {
                            var raw = new Byte[bytes];
                            if ((packetFlags & WasapiInterop.BUFFERFLAGS_SILENT) != 0)
                                Array.Clear(raw, 0, raw.Length);
                            else
                                Marshal.Copy(data, raw, 0, bytes);

                            var pcm = converter.Convert(raw);
                            if (pcm.Length > 0)
                            {
                                try
                                {
                                    DataAvailable?.Invoke(this, pcm);
                                }
                                catch (Exception ex)
                                {
                                    XTrace.Log.InfoPrefixed("WasapiRecorder: ", "Data handler failed: {0}", ex.Message);
                                }
                            }
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }

                    if (capture.GetNextPacketSize(out framesInNext) != 0) break;
                }

                Thread.Sleep(3);
            }
        }
        catch (Exception ex)
        {
            _initError ??= ex;
            _ready.Set();
            if (_running) XTrace.Log.InfoPrefixed("WasapiRecorder: ", "Capture stopped: {0}", ex.Message);
        }
        finally
        {
            if (started && client != null) client.Stop();
            WasapiInterop.Release(capture);
            WasapiInterop.Release(client);
            WasapiInterop.Release(enumerator);
            _ready.Set();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WasapiRecorder));
    }
}
#endregion

#region COM 接口
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] Int32 EnumAudioEndpoints(Int32 dataFlow, UInt32 stateMask, out IMMDeviceCollection devices);
    [PreserveSig] Int32 GetDefaultAudioEndpoint(Int32 dataFlow, Int32 role, out IMMDevice device);
    [PreserveSig] Int32 GetDevice([MarshalAs(UnmanagedType.LPWStr)] String id, out IMMDevice device);
    [PreserveSig] Int32 RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] Int32 UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] Int32 GetCount(out UInt32 count);
    [PreserveSig] Int32 Item(UInt32 index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] Int32 Activate(ref Guid iid, UInt32 clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out Object instance);
    [PreserveSig] Int32 OpenPropertyStore(UInt32 stgmAccess, out IPropertyStore properties);
    [PreserveSig] Int32 GetId([MarshalAs(UnmanagedType.LPWStr)] out String id);
    [PreserveSig] Int32 GetState(out UInt32 state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] Int32 GetCount(out UInt32 count);
    [PreserveSig] Int32 GetAt(UInt32 index, out PROPERTYKEY key);
    [PreserveSig] Int32 GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] Int32 SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] Int32 Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] Int32 Initialize(Int32 shareMode, UInt32 streamFlags, Int64 bufferDuration, Int64 periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] Int32 GetBufferSize(out UInt32 bufferFrames);
    [PreserveSig] Int32 GetStreamLatency(out Int64 latency);
    [PreserveSig] Int32 GetCurrentPadding(out UInt32 paddingFrames);
    [PreserveSig] Int32 IsFormatSupported(Int32 shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] Int32 GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] Int32 GetDevicePeriod(out Int64 defaultPeriod, out Int64 minimumPeriod);
    [PreserveSig] Int32 Start();
    [PreserveSig] Int32 Stop();
    [PreserveSig] Int32 Reset();
    [PreserveSig] Int32 SetEventHandle(IntPtr eventHandle);
    [PreserveSig] Int32 GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out Object service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] Int32 GetBuffer(UInt32 numFramesRequested, out IntPtr data);
    [PreserveSig] Int32 ReleaseBuffer(UInt32 numFramesWritten, UInt32 flags);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] Int32 GetBuffer(out IntPtr data, out UInt32 numFrames, out UInt32 flags, out UInt64 devicePosition, out UInt64 qpcPosition);
    [PreserveSig] Int32 ReleaseBuffer(UInt32 numFramesRead);
    [PreserveSig] Int32 GetNextPacketSize(out UInt32 packetFrames);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public UInt32 pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public UInt16 vt;
    public UInt16 reserved1;
    public UInt16 reserved2;
    public UInt16 reserved3;
    public IntPtr data;
    public IntPtr data2;
}
#endregion
