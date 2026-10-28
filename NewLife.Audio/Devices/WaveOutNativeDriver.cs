using System.Buffers;
using System.Diagnostics;
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
    /// <returns>WaveIn 录制器（内含采集线程与缓冲队列）</returns>
    public IAudioRecorder CreateRecorder() => new WaveInRecorder();
}

/// <summary>WaveOut 播放器（P/Invoke winmm）</summary>
/// <remarks>用 <c>CALLBACK_EVENT</c> 感知缓冲播放完成，
/// 于是 <see cref="WaitForSpace"/> 可以等事件而不是睡眠轮询。
/// </remarks>
internal class WaveOutPlayer : IAudioPlayer, IAudioRender
{
    private IntPtr _hWaveOut;
    private IntPtr _eventHandle;
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

        // 缓冲完成时由系统置事件，供 WaitForSpace 阻塞等待，不占用 CPU
        _eventHandle = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
        var flags = _eventHandle != IntPtr.Zero ? NativeMethods.CALLBACK_EVENT : NativeMethods.CALLBACK_NULL;

        var result = NativeMethods.waveOutOpen(
            out _hWaveOut, devId, ref wfx,
            _eventHandle, IntPtr.Zero,
            flags);

        if (result != 0)
        {
            _hWaveOut = IntPtr.Zero;
            CloseEvent();
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
    /// <param name="data">PCM 数据</param>
    public void Write(Byte[] data)
    {
        if (data == null || data.Length == 0) return;

        WriteCore(data, 0, data.Length);
    }

    /// <summary>写入音频数据（不使用额外托管分配）</summary>
    /// <param name="data">PCM 数据</param>
    public void Write(ReadOnlySpan<Byte> data)
    {
        if (data.Length == 0) return;

        if (_hWaveOut == IntPtr.Zero)
        {
            // 无设备时保留排队语义，缓冲需独立于池化临时数组
            lock (_lock) _pendingBuffers.Enqueue(data.ToArray());
            return;
        }

        // 非托管缓冲只能由 Marshal.Copy 从数组拷入，用池化临时数组避免每次分配
        var temp = ArrayPool<Byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(temp);
            WriteCore(temp, 0, data.Length);
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(temp);
        }
    }

    /// <summary>等待缓冲回落到水位以下</summary>
    /// <param name="maxBufferedBytes">允许的最大积压字节数</param>
    /// <param name="timeoutMilliseconds">超时时间（毫秒），小于 0 表示一直等</param>
    /// <returns>是否已回到水位以下</returns>
    public Boolean WaitForSpace(Int32 maxBufferedBytes, Int32 timeoutMilliseconds)
    {
        if (BufferedBytes <= maxBufferedBytes) return true;

        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (BufferedBytes <= maxBufferedBytes) return true;
            if (timeoutMilliseconds >= 0 && watch.ElapsedMilliseconds >= timeoutMilliseconds) return false;

            if (_eventHandle != IntPtr.Zero)
                NativeMethods.WaitForSingleObject(_eventHandle, 20);
            else
                Thread.Sleep(2);
        }
    }

    private void WriteCore(Byte[] data, Int32 offset, Int32 count)
    {
        if (_hWaveOut == IntPtr.Zero)
        {
            var copy = new Byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            lock (_lock) _pendingBuffers.Enqueue(copy);
            return;
        }

        // 清理已完成的头部
        CleanupDoneHeaders();

        // 准备并写入。驱动会异步修改非托管头部的 dwFlags，必须保留非托管头部才能感知完成状态
        var headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        var headerPtr = Marshal.AllocHGlobal(headerSize);
        var dataPtr = Marshal.AllocHGlobal(count);
        Marshal.Copy(data, offset, dataPtr, count);

        var hdr = new NativeMethods.WAVEHDR
        {
            lpData = dataPtr,
            dwBufferLength = (UInt32)count,
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

        CloseEvent();
    }

    private void CloseEvent()
    {
        var handle = _eventHandle;
        _eventHandle = IntPtr.Zero;
        if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
    }
}

/// <summary>WaveIn 录制器（P/Invoke winmm）：预投递缓冲 + 事件等待，线程与队列由基类负责</summary>
/// <remarks>
/// 预投递 <c>4</c> 个 20 毫秒缓冲，<see cref="AudioRecorderBase.ReadNative"/> 内等待 <c>CALLBACK_EVENT</c> 事件后
/// 把已完成缓冲交给调用方并立即重投；不再为每块数据分配托管数组。
/// 打开设备失败时抛 <see cref="InvalidOperationException"/>，与 WASAPI 录制器行为一致。
/// </remarks>
internal sealed class WaveInRecorder : AudioRecorderBase
{
    private const Int32 BufferCount = 4;

    private IntPtr _hWaveIn;
    private IntPtr _eventHandle;
    private readonly List<IntPtr> _headers = [];
    private readonly Object _lock = new();
    private Int32 _headerSize;
    private Byte[] _staging = [];
    private Int32 _pendingOffset;
    private Int32 _pendingCount;

    /// <summary>打开设备并预投递缓冲</summary>
    protected override void Open()
    {
        var format = Format ?? AudioFormat.Default;

        var wfx = new NativeMethods.WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = (Int16)format.Channels,
            nSamplesPerSec = format.SampleRate,
            nAvgBytesPerSec = format.SampleRate * format.Channels * format.BitsPerSample / 8,
            nBlockAlign = (Int16)(format.Channels * format.BitsPerSample / 8),
            wBitsPerSample = (Int16)format.BitsPerSample,
            cbSize = 0,
        };

        var devId = UIntPtr.Zero;
        if (DeviceId != null && Int32.TryParse(DeviceId, out var devIdx))
            devId = (UIntPtr)(UInt32)devIdx;

        // 每次缓冲完成系统都会置这个事件，读取时直接等它，不再空转轮询
        _eventHandle = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
        var flags = _eventHandle != IntPtr.Zero ? NativeMethods.CALLBACK_EVENT : NativeMethods.CALLBACK_NULL;

        var result = NativeMethods.waveInOpen(
            out _hWaveIn, devId, ref wfx,
            _eventHandle, IntPtr.Zero,
            flags);
        if (result != 0)
        {
            _hWaveIn = IntPtr.Zero;
            CloseEvent();
            throw new InvalidOperationException(L.T("No default audio device is available."));
        }

        try
        {
            // 4 个 20ms 缓冲，兼顾回调延迟与采集连续性
            _headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
            var bufferSize = Math.Max(1, format.SampleRate / 50) * format.Channels * format.BytesPerSample;
            _staging = new Byte[bufferSize];
            _pendingOffset = 0;
            _pendingCount = 0;

            lock (_lock)
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    var dataPtr = Marshal.AllocHGlobal(bufferSize);
                    var headerPtr = Marshal.AllocHGlobal(_headerSize);
                    var hdr = new NativeMethods.WAVEHDR
                    {
                        lpData = dataPtr,
                        dwBufferLength = (UInt32)bufferSize,
                    };
                    Marshal.StructureToPtr(hdr, headerPtr, false);

                    var prepared = NativeMethods.waveInPrepareHeader(_hWaveIn, headerPtr, (UInt32)_headerSize) == 0;
                    var hr = prepared ? NativeMethods.waveInAddBuffer(_hWaveIn, headerPtr, (UInt32)_headerSize) : -1;
                    if (hr != 0)
                    {
                        if (prepared) NativeMethods.waveInUnprepareHeader(_hWaveIn, headerPtr, (UInt32)_headerSize);

                        Marshal.FreeHGlobal(dataPtr);
                        Marshal.FreeHGlobal(headerPtr);
                        throw new InvalidOperationException(L.T("No default audio device is available."));
                    }

                    _headers.Add(headerPtr);
                }
            }

            result = NativeMethods.waveInStart(_hWaveIn);
            if (result != 0) throw new InvalidOperationException(L.T("No default audio device is available."));
        }
        catch
        {
            CloseDevice();
            throw;
        }
    }

    /// <summary>关闭设备</summary>
    protected override void Close() => CloseDevice();

    /// <summary>阻塞读取一块已完成缓冲</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="timeoutMilliseconds">等待超时时间（毫秒），小于等于 0 表示一直等</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示超时</returns>
    protected override Int32 ReadNative(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0) return 0;

        var watch = timeoutMilliseconds > 0 ? Stopwatch.StartNew() : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 先把上一块没交完的剩余部分交给调用方，攒够一块再处理下一块
            if (_pendingCount > 0)
            {
                var take = Math.Min(_pendingCount, buffer.Length);
                _staging.AsSpan(_pendingOffset, take).CopyTo(buffer);
                _pendingOffset += take;
                _pendingCount -= take;

                return take;
            }

            if (ReadDevice()) continue;

            if (watch != null && watch.ElapsedMilliseconds >= timeoutMilliseconds) return 0;

            if (_eventHandle != IntPtr.Zero)
                NativeMethods.WaitForSingleObject(_eventHandle, 20);
            else
                Thread.Sleep(1);
        }
    }

    /// <summary>取走一块已完成缓冲并重新投递；没有已完成缓冲时返回 false</summary>
    /// <returns>是否取到数据</returns>
    private Boolean ReadDevice()
    {
        lock (_lock)
        {
            if (_hWaveIn == IntPtr.Zero) return false;

            for (var i = 0; i < _headers.Count; i++)
            {
                var ptr = _headers[i];
                var hdr = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(ptr);
                if ((hdr.dwFlags & NativeMethods.WHDR_DONE) == 0) continue;

                var bytes = (Int32)hdr.dwBytesRecorded;
                if (bytes > _staging.Length) bytes = _staging.Length;
                if (bytes > 0) Marshal.Copy(hdr.lpData, _staging, 0, bytes);
                _pendingOffset = 0;
                _pendingCount = bytes;

                // 只清 WHDR_DONE 再重新投递，必须保留驱动设置的 WHDR_PREPARED，
                // 否则 waveInAddBuffer 会因未准备而失败，导致采完首轮缓冲后静默停采
                hdr.dwFlags &= ~NativeMethods.WHDR_DONE;
                hdr.dwBytesRecorded = 0;
                Marshal.StructureToPtr(hdr, ptr, false);
                var hr = NativeMethods.waveInAddBuffer(_hWaveIn, ptr, (UInt32)_headerSize);
                if (hr != 0) XTrace.Log.InfoPrefixed("WaveInRecorder: ", "waveInAddBuffer returned {0}", hr);

                return bytes > 0;
            }

            return false;
        }
    }

    private void CloseDevice()
    {
        if (_hWaveIn == IntPtr.Zero)
        {
            CloseEvent();
            return;
        }

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
        _pendingOffset = 0;
        _pendingCount = 0;
        CloseEvent();
    }

    private void CloseEvent()
    {
        var handle = _eventHandle;
        _eventHandle = IntPtr.Zero;
        if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
    }
}

/// <summary>WinMM 本地方法封装</summary>
internal static class NativeMethods
{
    public const UInt32 CALLBACK_NULL = 0x00000000;
    public const UInt32 CALLBACK_EVENT = 0x00050000;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, Boolean bManualReset, Boolean bInitialState, String lpName);

    [DllImport("kernel32.dll")]
    public static extern UInt32 WaitForSingleObject(IntPtr hHandle, UInt32 dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern Boolean CloseHandle(IntPtr hObject);
}
