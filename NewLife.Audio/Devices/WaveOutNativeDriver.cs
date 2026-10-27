using System.Runtime.InteropServices;
using NewLife;
using NewLife.Audio.DSP;
using NewLife.Log;

namespace NewLife.Audio.Devices;

/// <summary>Windows WaveOut 原生驱动（P/Invoke winmm.dll）</summary>
/// <remarks>
/// 使用 Windows 原生 WaveOut API（winmm.dll）实现音频播放。
/// 支持 PCM 8/16 bit，单声道/立体声。
/// </remarks>
public class WaveOutNativeDriver : IAudioDriver
{
    /// <summary>驱动名称</summary>
    public String Name => "Windows WaveOut (winmm.dll)";

    /// <summary>枚举音频设备（输出 + 输入）</summary>
    /// <returns>设备列表；非 Windows 平台返回空列表</returns>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (!AudioDriverFactory.IsWindows) return [];

        var devices = new List<AudioDeviceInfo>();
        var count = NativeMethods.waveOutGetNumDevs();
        for (var i = 0; i < count && i < 16; i++)
        {
            var caps = new NativeMethods.WAVEOUTCAPS();
            if (NativeMethods.waveOutGetDevCaps((UIntPtr)i, ref caps, (UInt32)Marshal.SizeOf(caps)) == 0)
            {
                devices.Add(new()
                {
                    Id = i.ToString(),
                    Name = caps.szPname,
                    IsInput = false,
                    IsDefault = i == 0,
                });
            }
        }

        var inCount = NativeMethods.waveInGetNumDevs();
        for (var i = 0; i < inCount && i < 16; i++)
        {
            var caps = new NativeMethods.WAVEINCAPS();
            if (NativeMethods.waveInGetDevCaps((UIntPtr)i, ref caps, (UInt32)Marshal.SizeOf(caps)) == 0)
            {
                devices.Add(new()
                {
                    Id = i.ToString(),
                    Name = caps.szPname,
                    IsInput = true,
                    IsDefault = i == 0,
                });
            }
        }

        // 没有任何物理设备时保留一个"默认设备"条目，waveOut/waveIn 的 WAVE_MAPPER(0) 仍然可用
        if (!devices.Any(e => !e.IsInput))
            devices.Add(new() { Id = "0", Name = L.T("Default audio device"), IsInput = false, IsDefault = true });
        if (!devices.Any(e => e.IsInput))
            devices.Add(new() { Id = "0", Name = L.T("Default recording device"), IsInput = true, IsDefault = true });

        return devices;
    }

    /// <summary>获取默认输出设备</summary>
    public AudioDeviceInfo GetDefaultOutputDevice()
    {
        var devices = EnumerateDevices();
        return devices.FirstOrDefault(e => !e.IsInput && e.IsDefault) ?? devices.FirstOrDefault(e => !e.IsInput);
    }

    /// <summary>获取默认输入设备</summary>
    public AudioDeviceInfo GetDefaultInputDevice()
    {
        var devices = EnumerateDevices();
        return devices.FirstOrDefault(e => e.IsInput && e.IsDefault) ?? devices.FirstOrDefault(e => e.IsInput);
    }

    /// <summary>创建播放器</summary>
    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();

    /// <summary>创建录制器</summary>
    public IAudioRecorder CreateRecorder() => new WaveInRecorder();
}

/// <summary>WaveOut 播放器（P/Invoke winmm）</summary>
internal class WaveOutPlayer : IAudioPlayer
{
    private IntPtr _hWaveOut;
    private AudioFormat _format;
    private readonly List<IntPtr> _headers = [];
    private readonly Queue<Byte[]> _pendingBuffers = new();
    private readonly Object _lock = new();
    private Boolean _playing;

    /// <summary>待播放字节数（已排队到驱动、尚未播完的数据）</summary>
    public Int32 BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                if (_hWaveOut != IntPtr.Zero) CleanupDoneHeadersLocked();

                var count = 0;
                foreach (var item in _pendingBuffers) count += item.Length;
                foreach (var ptr in _headers) count += (Int32)Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr).dwBufferLength;

                return count;
            }
        }
    }

    /// <summary>播放停止事件</summary>
    public event EventHandler PlaybackStopped;

    /// <summary>初始化播放器</summary>
    public void Init(AudioFormat format, String deviceId = null)
    {
        _format = format?.Clone() ?? AudioFormat.Default;

        var wfx = new NativeMethods.WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = (Int16)_format.Channels,
            nSamplesPerSec = _format.SampleRate,
            nAvgBytesPerSec = _format.SampleRate * _format.Channels * _format.BitsPerSample / 8,
            nBlockAlign = (Int16)(_format.Channels * _format.BitsPerSample / 8),
            wBitsPerSample = (Int16)_format.BitsPerSample,
            cbSize = 0,
        };

        var devId = UIntPtr.Zero;
        if (deviceId != null && Int32.TryParse(deviceId, out var devIdx))
            devId = (UIntPtr)(UInt32)devIdx;

        var result = NativeMethods.waveOutOpen(
            out _hWaveOut, devId, ref wfx,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.CALLBACK_NULL);

        if (result != 0)
        {
            _hWaveOut = IntPtr.Zero;
            XTrace.Log.InfoPrefixed("WaveOut: ", "waveOutOpen returned {0}; falling back to the null player.", result);
        }
    }

    /// <summary>开始播放</summary>
    public void Play() => _playing = true;

    /// <summary>暂停播放</summary>
    public void Pause()
    {
        _playing = false;
        if (_hWaveOut != IntPtr.Zero)
            NativeMethods.waveOutPause(_hWaveOut);
    }

    /// <summary>停止播放</summary>
    public void Stop()
    {
        _playing = false;
        if (_hWaveOut != IntPtr.Zero)
        {
            NativeMethods.waveOutReset(_hWaveOut);

            // 重置后所有头部均已完成，统一回收非托管内存
            lock (_lock) ReleaseAllHeadersLocked();
        }

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>写入音频数据</summary>
    public void Write(Byte[] data)
    {
        if (data == null || data.Length == 0) return;

        if (_hWaveOut == IntPtr.Zero) { lock (_lock) _pendingBuffers.Enqueue(data); return; }

        // 清理已完成的头部
        CleanupDoneHeaders();

        // 准备并写入。驱动会异步修改非托管头部的 dwFlags，必须保留非托管头部才能感知完成状态
        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        var headerPtr = Marshal.AllocHGlobal(headerSize);
        var dataPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, dataPtr, data.Length);

        var hdr = new NativeMethods.WAVEHDR
        {
            lpData = dataPtr,
            dwBufferLength = (UInt32)data.Length,
        };
        Marshal.StructureToPtr(hdr, headerPtr, false);

        var result = NativeMethods.waveOutPrepareHeader(_hWaveOut, headerPtr, (UInt32)headerSize);
        if (result == 0)
            result = NativeMethods.waveOutWrite(_hWaveOut, headerPtr, (UInt32)headerSize);
        if (result != 0)
        {
            // 失败时释放本次分配，避免非托管内存泄漏
            if ((Marshal.PtrToStructure<NativeMethods.WAVEHDR>(headerPtr).dwFlags & NativeMethods.WHDR_PREPARED) != 0)
                NativeMethods.waveOutUnprepareHeader(_hWaveOut, headerPtr, (UInt32)headerSize);

            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(headerPtr);
            return;
        }

        lock (_lock) _headers.Add(headerPtr);
    }

    /// <summary>清理已播放完成的头部</summary>
    private void CleanupDoneHeaders()
    {
        lock (_lock) CleanupDoneHeadersLocked();
    }

    private void CleanupDoneHeadersLocked()
    {
        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        for (var i = _headers.Count - 1; i >= 0; i--)
        {
            var ptr = _headers[i];
            var hdr = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr);
            if ((hdr.dwFlags & NativeMethods.WHDR_DONE) == 0) continue;

            NativeMethods.waveOutUnprepareHeader(_hWaveOut, ptr, (UInt32)headerSize);
            Marshal.FreeHGlobal(hdr.lpData);
            Marshal.FreeHGlobal(ptr);
            _headers.RemoveAt(i);
        }
    }

    private void ReleaseAllHeadersLocked()
    {
        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        foreach (var ptr in _headers)
        {
            var hdr = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr);
            NativeMethods.waveOutUnprepareHeader(_hWaveOut, ptr, (UInt32)headerSize);
            Marshal.FreeHGlobal(hdr.lpData);
            Marshal.FreeHGlobal(ptr);
        }
        _headers.Clear();
        _pendingBuffers.Clear();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        Stop();
        if (_hWaveOut != IntPtr.Zero)
        {
            NativeMethods.waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }
    }
}

/// <summary>WaveIn 录制器（P/Invoke winmm）</summary>
/// <remarks>
/// 使用 <c>waveInPrepareHeader</c> + <c>waveInAddBuffer</c> 预投递多个缓冲，
/// 由后台线程轮询 <c>WHDR_DONE</c> 后触发 <see cref="DataAvailable"/> 并回收缓冲，形成持续采集。
/// 打开设备失败时抛 <see cref="InvalidOperationException"/>，与 WASAPI 采集器行为一致。
/// </remarks>
internal class WaveInRecorder : IAudioRecorder
{
    private const Int32 BufferCount = 4;

    private IntPtr _hWaveIn;
    private AudioFormat _format;
    private String _deviceId;
    private readonly List<IntPtr> _headers = [];
    private readonly Object _lock = new();
    private Thread _thread;
    private volatile Boolean _running;
    private Boolean _disposed;

    /// <summary>数据可用事件</summary>
    public event EventHandler<Byte[]> DataAvailable;

    /// <summary>请求的音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>是否正在录制</summary>
    public Boolean IsRecording => _running;

    /// <summary>初始化录制器</summary>
    public void Init(AudioFormat format, String deviceId = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WaveInRecorder));

        _format = format?.Clone() ?? AudioFormat.Default;
        _deviceId = deviceId;
    }

    /// <summary>开始录制</summary>
    /// <exception cref="InvalidOperationException">设备不可用或初始化失败</exception>
    public void StartRecording()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WaveInRecorder));
        if (_hWaveIn != IntPtr.Zero) return;

        if (_format == null) _format = AudioFormat.Default;

        var wfx = new NativeMethods.WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = (Int16)_format.Channels,
            nSamplesPerSec = _format.SampleRate,
            nAvgBytesPerSec = _format.SampleRate * _format.Channels * _format.BitsPerSample / 8,
            nBlockAlign = (Int16)(_format.Channels * _format.BitsPerSample / 8),
            wBitsPerSample = (Int16)_format.BitsPerSample,
            cbSize = 0,
        };

        var devId = UIntPtr.Zero;
        if (_deviceId != null && Int32.TryParse(_deviceId, out var devIdx))
            devId = (UIntPtr)(UInt32)devIdx;

        var result = NativeMethods.waveInOpen(
            out _hWaveIn, devId, ref wfx,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.CALLBACK_NULL);
        if (result != 0)
        {
            _hWaveIn = IntPtr.Zero;
            throw new InvalidOperationException(L.T("No default audio device is available."));
        }

        try
        {
            // 4 个 20ms 缓冲，兼顾回调延迟与采集连续性
            var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
            var bufferSize = Math.Max(1, _format.SampleRate / 50) * _format.Channels * _format.BytesPerSample;

            lock (_lock)
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    var dataPtr = Marshal.AllocHGlobal(bufferSize);
                    var headerPtr = Marshal.AllocHGlobal(headerSize);
                    var hdr = new NativeMethods.WAVEHDR
                    {
                        lpData = dataPtr,
                        dwBufferLength = (UInt32)bufferSize,
                    };
                    Marshal.StructureToPtr(hdr, headerPtr, false);

                    var prepared = NativeMethods.waveInPrepareHeader(_hWaveIn, headerPtr, (UInt32)headerSize) == 0;
                    var hr = prepared ? NativeMethods.waveInAddBuffer(_hWaveIn, headerPtr, (UInt32)headerSize) : -1;
                    if (hr != 0)
                    {
                        if (prepared) NativeMethods.waveInUnprepareHeader(_hWaveIn, headerPtr, (UInt32)headerSize);

                        Marshal.FreeHGlobal(dataPtr);
                        Marshal.FreeHGlobal(headerPtr);
                        throw new InvalidOperationException(L.T("No default audio device is available."));
                    }

                    _headers.Add(headerPtr);
                }
            }

            _running = true;
            _thread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = "WaveInRecorder",
            };
            _thread.Start();

            result = NativeMethods.waveInStart(_hWaveIn);
            if (result != 0) throw new InvalidOperationException(L.T("No default audio device is available."));
        }
        catch
        {
            _running = false;

            var thread = _thread;
            _thread = null;
            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(500);

            CloseDevice();
            throw;
        }
    }

    /// <summary>停止录制</summary>
    public void StopRecording()
    {
        _running = false;

        var thread = _thread;
        _thread = null;
        if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(1000);

        CloseDevice();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
    }

    private void CaptureThread()
    {
        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();

        while (_running)
        {
            List<Byte[]> chunks = null;
            lock (_lock)
            {
                for (var i = 0; i < _headers.Count; i++)
                {
                    var ptr = _headers[i];
                    var hdr = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr);
                    if ((hdr.dwFlags & NativeMethods.WHDR_DONE) == 0) continue;

                    var bytes = (Int32)hdr.dwBytesRecorded;
                    if (bytes > 0)
                    {
                        var data = new Byte[bytes];
                        Marshal.Copy(hdr.lpData, data, 0, bytes);
                        (chunks ??= []).Add(data);
                    }

                    // 只清 WHDR_DONE 再重新投递，必须保留驱动设置的 WHDR_PREPARED，
                    // 否则 waveInAddBuffer 会因未准备而失败，导致采完首轮缓冲后静默停采
                    hdr.dwFlags &= ~NativeMethods.WHDR_DONE;
                    hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, ptr, false);
                    var hr = NativeMethods.waveInAddBuffer(_hWaveIn, ptr, (UInt32)headerSize);
                    if (hr != 0) XTrace.Log.InfoPrefixed("WaveInRecorder: ", "waveInAddBuffer returned {0}", hr);
                }
            }

            if (chunks != null)
            {
                foreach (var chunk in chunks)
                {
                    try
                    {
                        DataAvailable?.Invoke(this, chunk);
                    }
                    catch (Exception ex)
                    {
                        XTrace.Log.InfoPrefixed("WaveInRecorder: ", "Data handler failed: {0}", ex.Message);
                    }
                }
            }

            Thread.Sleep(5);
        }
    }

    private void CloseDevice()
    {
        if (_hWaveIn == IntPtr.Zero) return;

        NativeMethods.waveInStop(_hWaveIn);
        NativeMethods.waveInReset(_hWaveIn);

        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        lock (_lock)
        {
            foreach (var ptr in _headers)
            {
                var hdr = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr);
                NativeMethods.waveInUnprepareHeader(_hWaveIn, ptr, (UInt32)headerSize);
                Marshal.FreeHGlobal(hdr.lpData);
                Marshal.FreeHGlobal(ptr);
            }
            _headers.Clear();
        }

        NativeMethods.waveInClose(_hWaveIn);
        _hWaveIn = IntPtr.Zero;
    }
}

/// <summary>WinMM 本地方法封装</summary>
internal static class NativeMethods
{
    public const UInt32 CALLBACK_NULL = 0x00000000;
    public const UInt32 WHDR_DONE = 0x00000001;
    public const UInt32 WHDR_PREPARED = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct WAVEFORMATEX
    {
        public Int16 wFormatTag;
        public Int16 nChannels;
        public Int32 nSamplesPerSec;
        public Int32 nAvgBytesPerSec;
        public Int16 nBlockAlign;
        public Int16 wBitsPerSample;
        public Int16 cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public UInt32 dwBufferLength;
        public UInt32 dwBytesRecorded;
        public UIntPtr dwUser;
        public UInt32 dwFlags;
        public UInt32 dwLoops;
        public IntPtr lpNext;
        public UIntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct WAVEOUTCAPS
    {
        public Int16 wMid;
        public Int16 wPid;
        public UInt32 vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public String szPname;
        public UInt32 dwFormats;
        public Int16 wChannels;
        public Int16 wReserved1;
        public UInt32 dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct WAVEINCAPS
    {
        public Int16 wMid;
        public Int16 wPid;
        public UInt32 vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public String szPname;
        public UInt32 dwFormats;
        public Int16 wChannels;
        public Int16 wReserved1;
    }

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern Int32 waveOutGetDevCaps(UIntPtr uDeviceID, ref WAVEOUTCAPS caps, UInt32 cbCaps);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutOpen(out IntPtr hWaveOut, UIntPtr uDeviceID,
        ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, UInt32 dwFlags);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutPause(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutRestart(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern Int32 waveInGetDevCaps(UIntPtr uDeviceID, ref WAVEINCAPS caps, UInt32 cbCaps);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInOpen(out IntPtr hWaveIn, UIntPtr uDeviceID,
        ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, UInt32 dwFlags);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInPrepareHeader(IntPtr hWaveIn, IntPtr pwh, UInt32 cbwh);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInUnprepareHeader(IntPtr hWaveIn, IntPtr pwh, UInt32 cbwh);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInAddBuffer(IntPtr hWaveIn, IntPtr pwh, UInt32 cbwh);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInStart(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInStop(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInReset(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInClose(IntPtr hWaveIn);
}
