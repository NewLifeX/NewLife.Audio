using System.Runtime.InteropServices;
using NewLife.Audio.DSP;
using NewLife.Log;

namespace NewLife.Audio.Devices;

/// <summary>Windows WaveOut 原生后端（P/Invoke winmm.dll）</summary>
/// <remarks>
/// 使用 Windows 原生 WaveOut API（winmm.dll）实现音频播放。
/// 支持 PCM 8/16 bit，单声道/立体声。
/// </remarks>
public class WaveOutNativeBackend : IAudioBackend
{
    /// <summary>后端名称</summary>
    public String Name => "Windows WaveOut (winmm.dll)";

    /// <summary>枚举输出设备</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
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
        if (devices.Count == 0)
            devices.Add(new() { Id = "0", Name = "默认音频设备", IsInput = false, IsDefault = true });

        return devices;
    }

    /// <summary>获取默认输出设备</summary>
    public AudioDeviceInfo GetDefaultOutputDevice()
    {
        var devices = EnumerateDevices();
        return devices.Count > 0 ? devices[0] : new() { Id = "0", Name = "默认音频设备", IsDefault = true };
    }

    /// <summary>获取默认输入设备</summary>
    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "capture", Name = "默认录音设备", IsInput = true, IsDefault = true };

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
    private readonly Queue<NativeMethods.WAVEHDR> _headers = new();
    private readonly Queue<Byte[]> _pendingBuffers = new();
    private readonly Object _lock = new();
    private Boolean _playing;

    /// <summary>待播放字节数</summary>
    public Int32 BufferedBytes
    {
        get { lock (_lock) return _pendingBuffers.Sum(b => b.Length); }
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
            XTrace.WriteLine($"WaveOut: waveOutOpen 返回 {result}, 回退到空壳模式");
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

            // 清理所有头部
            lock (_lock)
            {
                while (_headers.Count > 0)
                {
                    var hdr = _headers.Dequeue();
                    NativeMethods.waveOutUnprepareHeader(_hWaveOut, ref hdr, (UInt32)Marshal.SizeOf<NativeMethods.WAVEHDR>());
                }
                _pendingBuffers.Clear();
            }
        }

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>写入音频数据</summary>
    public void Write(Byte[] data)
    {
        if (_hWaveOut == IntPtr.Zero) { lock (_lock) _pendingBuffers.Enqueue(data); return; }

        // 清理已完成的头部
        CleanupDoneHeaders();

        // 准备并写入
        var hdr = new NativeMethods.WAVEHDR
        {
            lpData = Marshal.AllocHGlobal(data.Length),
            dwBufferLength = (UInt32)data.Length,
            dwFlags = 0,
        };
        Marshal.Copy(data, 0, hdr.lpData, data.Length);

        var result = NativeMethods.waveOutPrepareHeader(_hWaveOut, ref hdr, (UInt32)Marshal.SizeOf<NativeMethods.WAVEHDR>());
        if (result != 0) return;

        result = NativeMethods.waveOutWrite(_hWaveOut, ref hdr, (UInt32)Marshal.SizeOf<NativeMethods.WAVEHDR>());
        if (result != 0) return;

        lock (_lock) _headers.Enqueue(hdr);
    }

    private void CleanupDoneHeaders()
    {
        lock (_lock)
        {
            var done = new List<NativeMethods.WAVEHDR>();
            while (_headers.Count > 0)
            {
                var hdr = _headers.Peek();
                if ((hdr.dwFlags & NativeMethods.WHDR_DONE) != 0)
                {
                    _headers.Dequeue();
                    NativeMethods.waveOutUnprepareHeader(_hWaveOut, ref hdr, (UInt32)Marshal.SizeOf<NativeMethods.WAVEHDR>());
                    Marshal.FreeHGlobal(hdr.lpData);
                    done.Add(hdr);
                }
                else break;
            }
        }
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
internal class WaveInRecorder : IAudioRecorder
{
    private IntPtr _hWaveIn;
    private AudioFormat _format;

    /// <summary>数据可用事件</summary>
    public event EventHandler<Byte[]> DataAvailable;

    /// <summary>初始化录制器</summary>
    public void Init(AudioFormat format, String deviceId = null)
    {
        _format = format?.Clone() ?? AudioFormat.Default;
    }

    /// <summary>开始录制</summary>
    public void StartRecording()
    {
        if (_hWaveIn != IntPtr.Zero) return;

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

        var result = NativeMethods.waveInOpen(
            out _hWaveIn, UIntPtr.Zero, ref wfx,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.CALLBACK_NULL);

        if (result == 0)
            NativeMethods.waveInStart(_hWaveIn);
    }

    /// <summary>停止录制</summary>
    public void StopRecording()
    {
        if (_hWaveIn != IntPtr.Zero)
        {
            NativeMethods.waveInStop(_hWaveIn);
            NativeMethods.waveInReset(_hWaveIn);
            NativeMethods.waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose() => StopRecording();
}

/// <summary>WinMM 本地方法封装</summary>
internal static class NativeMethods
{
    public const UInt32 CALLBACK_NULL = 0x00000000;
    public const UInt32 WHDR_DONE = 0x00000001;

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

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern Int32 waveOutGetDevCaps(UIntPtr uDeviceID, ref WAVEOUTCAPS caps, UInt32 cbCaps);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutOpen(out IntPtr hWaveOut, UIntPtr uDeviceID,
        ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, UInt32 dwFlags);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, UInt32 uSize);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutPause(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutRestart(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInOpen(out IntPtr hWaveIn, UIntPtr uDeviceID,
        ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, UInt32 dwFlags);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInStart(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInStop(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInReset(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    public static extern Int32 waveInClose(IntPtr hWaveIn);
}
