using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NewLife.Audio.DSP;
using NewLife.Log;

namespace NewLife.Audio.Devices;

/// <summary>Linux ALSA 音频后端（libasound）</summary>
/// <remarks>
/// 用 libasound 的简化 API 播放与采集：<c>snd_pcm_open → snd_pcm_set_params → writei/readi</c>；
/// 设备枚举走 <c>snd_device_name_hint</c>。<br/>
/// 仅在 Linux 且能加载 <c>libasound.so.2</c> 时可用，其他平台 <see cref="IsAvailable"/> 为 false，
/// 由 <see cref="AudioDriverFactory"/> 回落到明确的“无可用后端”提示，不会静默返回空设备。
/// <para>本后端尚未在真实 Linux 声卡上验证，功能清单标记为 🟡（待 Linux 真机/CI 验证）。</para>
/// </remarks>
/// <example>
/// <code>
/// if (AlsaDriver.IsAvailable)
/// {
///     var driver = new AlsaDriver();
///     using var player = driver.CreatePlayer();
///     player.Init(new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 });
///     player.Play();
///     player.Write(pcm);
/// }
/// </code>
/// </example>
public sealed class AlsaDriver : IAudioDriver
{
    private static Boolean? _available;

    /// <summary>驱动名称</summary>
    public String Name => "alsa";

    /// <summary>当前是否在 Linux 平台</summary>
    /// <remarks>.NET Core 在 macOS 上同样报告 <see cref="PlatformID.Unix"/>，用 macOS 独有的 <c>/usr/bin/say</c> 排除</remarks>
    public static Boolean IsLinux => Environment.OSVersion.Platform == PlatformID.Unix && !File.Exists("/usr/bin/say");

    /// <summary>libasound 是否可用（Linux 且能加载）</summary>
    public static Boolean IsAvailable
    {
        get
        {
            if (!IsLinux) return false;
            if (_available != null) return _available.Value;

            try
            {
                AlsaNative.snd_asoundlib_version();
                _available = true;
            }
            catch
            {
                _available = false;
            }

            return _available.Value;
        }
    }

    /// <summary>枚举 PCM 设备。枚举失败时返回空列表</summary>
    /// <returns>输出与输入设备列表</returns>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var list = new List<AudioDeviceInfo>();
        if (!IsAvailable) return list;

        var hints = IntPtr.Zero;
        try
        {
            if (AlsaNative.snd_device_name_hint(-1, "pcm", out hints) < 0) return list;

            for (var offset = 0; ; offset += IntPtr.Size)
            {
                var hint = Marshal.ReadIntPtr(hints, offset);
                if (hint == IntPtr.Zero) break;

                var name = GetHint(hint, "NAME");
                if (name.IsNullOrEmpty()) continue;

                var description = GetHint(hint, "DESC");
                var direction = GetHint(hint, "IOID");

                var isInput = direction != null && direction.EqualIgnoreCase("Input");
                var isOutput = direction == null || direction.EqualIgnoreCase("Output");

                if (isOutput) list.Add(new AudioDeviceInfo { Id = name, Name = description ?? name });
                if (isInput) list.Add(new AudioDeviceInfo { Id = name, Name = description ?? name, IsInput = true });
            }
        }
        catch (Exception ex)
        {
            XTrace.WriteException(ex);
        }
        finally
        {
            if (hints != IntPtr.Zero) AlsaNative.snd_device_name_free_hint(hints);
        }

        return list;
    }

    /// <summary>取默认输出设备（ALSA 的 default）</summary>
    /// <returns>设备信息；当前环境无 libasound 时返回 null</returns>
    public AudioDeviceInfo GetDefaultOutputDevice() => IsAvailable ? new AudioDeviceInfo { Id = "default", Name = "default", IsDefault = true } : null;

    /// <summary>取默认输入设备（ALSA 的 default）</summary>
    /// <returns>设备信息；当前环境无 libasound 时返回 null</returns>
    public AudioDeviceInfo GetDefaultInputDevice() => IsAvailable ? new AudioDeviceInfo { Id = "default", Name = "default", IsInput = true, IsDefault = true } : null;

    /// <summary>创建播放器</summary>
    /// <returns>播放器实例</returns>
    public IAudioPlayer CreatePlayer() => new AlsaPlayer();

    /// <summary>创建录制器</summary>
    /// <returns>ALSA 录制器（内含采集线程与缓冲队列）</returns>
    public IAudioRecorder CreateRecorder() => new AlsaRecorder();

    private static String GetHint(IntPtr hint, String type)
    {
        var ptr = AlsaNative.snd_device_name_get_hint(hint, type);
        if (ptr == IntPtr.Zero) return null;

        try
        {
            return Marshal.PtrToStringAnsi(ptr);
        }
        finally
        {
            AlsaNative.free(ptr);
        }
    }
}

/// <summary>ALSA P/Invoke 声明与公共调用</summary>
internal static class AlsaNative
{
    public const Int32 StreamPlayback = 0;
    public const Int32 StreamCapture = 1;
    public const Int32 FormatS16LE = 2;
    public const Int32 AccessRwInterleaved = 3;

    [DllImport("libasound.so.2", EntryPoint = "snd_asoundlib_version", CharSet = CharSet.Ansi)]
    public static extern IntPtr snd_asoundlib_version();

    [DllImport("libasound.so.2", EntryPoint = "snd_device_name_hint", CharSet = CharSet.Ansi)]
    public static extern Int32 snd_device_name_hint(Int32 card, String iface, out IntPtr hints);

    [DllImport("libasound.so.2", EntryPoint = "snd_device_name_get_hint", CharSet = CharSet.Ansi)]
    public static extern IntPtr snd_device_name_get_hint(IntPtr hint, String id);

    [DllImport("libasound.so.2", EntryPoint = "snd_device_name_free_hint")]
    public static extern Int32 snd_device_name_free_hint(IntPtr hints);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_open", CharSet = CharSet.Ansi)]
    public static extern Int32 snd_pcm_open(out IntPtr pcm, String name, Int32 stream, Int32 mode);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_set_params")]
    public static extern Int32 snd_pcm_set_params(IntPtr pcm, Int32 format, Int32 access, UInt32 channels, UInt32 rate, Int32 softResample, UInt32 latency);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_writei")]
    public static extern IntPtr snd_pcm_writei(IntPtr pcm, Byte[] buffer, UIntPtr frames);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_readi")]
    public static extern IntPtr snd_pcm_readi(IntPtr pcm, Byte[] buffer, UIntPtr frames);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_prepare")]
    public static extern Int32 snd_pcm_prepare(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_drain")]
    public static extern Int32 snd_pcm_drain(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_drop")]
    public static extern Int32 snd_pcm_drop(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_pcm_close")]
    public static extern Int32 snd_pcm_close(IntPtr pcm);

    [DllImport("libasound.so.2", EntryPoint = "snd_strerror", CharSet = CharSet.Ansi)]
    public static extern IntPtr snd_strerror(Int32 err);

    [DllImport("libc", EntryPoint = "free")]
    public static extern void free(IntPtr ptr);

    /// <summary>打开 PCM 并按格式设置参数</summary>
    /// <param name="device">设备名</param>
    /// <param name="stream">播放/采集</param>
    /// <param name="format">音频格式</param>
    /// <param name="pcm">PCM 句柄</param>
    /// <returns>0 表示成功，其余为 ALSA 错误码</returns>
    public static Int32 Open(String device, Int32 stream, AudioFormat format, out IntPtr pcm)
    {
        var result = snd_pcm_open(out pcm, device, stream, 0);
        if (result != 0) return result;

        result = snd_pcm_set_params(
            pcm,
            FormatS16LE,
            AccessRwInterleaved,
            (UInt32)format.Channels,
            (UInt32)format.SampleRate,
            1,
            200000);

        if (result != 0)
        {
            snd_pcm_close(pcm);
            pcm = IntPtr.Zero;
        }

        return result;
    }

    /// <summary>取错误描述</summary>
    /// <param name="err">错误码</param>
    /// <returns>描述文本</returns>
    public static String Error(Int32 err)
    {
        try
        {
            var ptr = snd_strerror(err);
            return ptr == IntPtr.Zero ? err.ToString() : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return err.ToString();
        }
    }
}

/// <summary>ALSA 播放器：后台线程逐块写入 PCM</summary>
internal sealed class AlsaPlayer : IAudioPlayer, IAudioRender
{
    private readonly Object _lock = new();
    private readonly Queue<Byte[]> _queue = new();
    private readonly ManualResetEventSlim _space = new(false);
    private IntPtr _pcm;
    private Thread _thread;
    private AudioFormat _format;
    private volatile Boolean _running;
    private volatile Boolean _paused;

    /// <summary>缓冲区中待播放的字节数</summary>
    public Int32 BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                var total = 0;
                foreach (var item in _queue) total += item.Length;

                return total;
            }
        }
    }

    /// <summary>播放停止事件</summary>
    public event EventHandler PlaybackStopped;

    /// <summary>初始化播放器</summary>
    /// <param name="format">音频格式，仅支持 16 位有符号小端</param>
    /// <param name="deviceId">ALSA 设备名，如 default、hw:0,0</param>
    public void Init(AudioFormat format, String deviceId = null)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));
        if (format.BitsPerSample != 16) throw new NotSupportedException(L.T("The requested audio format is not supported: {0}", format.BitsPerSample));

        _format = format.Clone();

        if (!AlsaDriver.IsAvailable) throw new NotSupportedException(L.T("No audio backend is available."));

        var device = deviceId.IsNullOrEmpty() ? "default" : deviceId;
        var result = AlsaNative.Open(device, AlsaNative.StreamPlayback, _format, out _pcm);
        if (result != 0)
        {
            XTrace.WriteLine("ALSA playback device {0} failed to open: {1}", device, AlsaNative.Error(result));
            throw new InvalidOperationException(AlsaNative.Error(result));
        }
    }

    /// <summary>开始播放</summary>
    public void Play()
    {
        if (_pcm == IntPtr.Zero) throw new InvalidOperationException(L.T("No audio backend is available."));

        lock (_lock)
        {
            _running = true;
            _paused = false;

            if (_thread == null || !_thread.IsAlive)
            {
                _thread = new Thread(PlayLoop) { IsBackground = true, Name = "alsa-play" };
                _thread.Start();
            }

            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>暂停播放</summary>
    public void Pause() => _paused = true;

    /// <summary>停止播放并清空缓冲</summary>
    public void Stop()
    {
        _running = false;
        _paused = false;

        lock (_lock)
        {
            _queue.Clear();
            Monitor.PulseAll(_lock);
        }

        if (_pcm != IntPtr.Zero) AlsaNative.snd_pcm_drop(_pcm);

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>写入 PCM 数据（16 位小端交错）</summary>
    /// <param name="data">PCM 数据</param>
    public void Write(Byte[] data)
    {
        if (data == null || data.Length == 0) return;

        lock (_lock)
        {
            _queue.Enqueue(data);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>写入 PCM 数据（16 位小端交错）</summary>
    /// <param name="data">PCM 数据</param>
    public void Write(ReadOnlySpan<Byte> data)
    {
        if (data.Length == 0) return;

        // 底层 writei 按数组工作，这里收起一次拷贝
        Write(data.ToArray());
    }

    /// <summary>等待缓冲回落到水位以下</summary>
    /// <param name="maxBufferedBytes">允许的最大积压字节数</param>
    /// <param name="timeoutMilliseconds">超时时间（毫秒），小于 0 表示一直等</param>
    /// <returns>是否已回到水位以下</returns>
    public Boolean WaitForSpace(Int32 maxBufferedBytes, Int32 timeoutMilliseconds)
    {
        if (BufferedBytes <= maxBufferedBytes) return true;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (BufferedBytes > maxBufferedBytes)
        {
            if (timeoutMilliseconds >= 0 && watch.ElapsedMilliseconds >= timeoutMilliseconds) return false;

            _space.Wait(20);
            _space.Reset();
        }

        return true;
    }

    private void PlayLoop()
    {
        // drop/underrun 之后流处于 SETUP 状态，写入前必须 prepare
        AlsaNative.snd_pcm_prepare(_pcm);

        var frameBytes = Math.Max(1, _format.Channels * _format.BytesPerSample);

        while (_running)
        {
            Byte[] buffer;
            lock (_lock)
            {
                while (_running && (_paused || _queue.Count == 0)) Monitor.Wait(_lock, 50);
                if (!_running) break;
                if (_paused || _queue.Count == 0) continue;

                buffer = _queue.Dequeue();
            }

            // 取走数据后唤醒等空间的写入方
            _space.Set();

            var frames = buffer.Length / frameBytes;
            if (frames <= 0) continue;

            var written = AlsaNative.snd_pcm_writei(_pcm, buffer, (UIntPtr)(UInt32)frames).ToInt64();
            if (written < 0)
            {
                // -EPIPE（欠载）/ -ESTRPIPE（挂起）：重新准备后重试一次
                AlsaNative.snd_pcm_prepare(_pcm);
                AlsaNative.snd_pcm_writei(_pcm, buffer, (UIntPtr)(UInt32)frames);
            }
        }

        if (_pcm != IntPtr.Zero) AlsaNative.snd_pcm_drain(_pcm);
    }

    /// <summary>释放播放器</summary>
    public void Dispose()
    {
        Stop();

        _space.Dispose();

        var thread = _thread;
        _thread = null;
        if (thread != null && !thread.Join(2000))
        {
            // 后台线程仍阻塞在 writei，此时关闭句柄会导致访问已释放内存，宁可保留句柄
            XTrace.WriteLine("ALSA playback thread did not exit in 2s; the PCM handle is kept to avoid a race");
            return;
        }

        if (_pcm != IntPtr.Zero)
        {
            AlsaNative.snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
    }
}

/// <summary>ALSA 录制器：阻塞 readi 取数，队列缓冲由基类负责</summary>
internal sealed class AlsaRecorder : AudioRecorderBase
{
    private IntPtr _pcm;
    private Byte[] _staging = [];
    private Int32 _pendingOffset;
    private Int32 _pendingCount;

    /// <summary>初始化录制器</summary>
    /// <param name="format">音频格式，仅支持 16 位有符号小端</param>
    /// <param name="deviceId">ALSA 设备名</param>
    public override void Init(AudioFormat format, String deviceId = null)
    {
        if (format != null && format.BitsPerSample != 16) throw new NotSupportedException(L.T("The requested audio format is not supported: {0}", format.BitsPerSample));
        if (!AlsaDriver.IsAvailable) throw new NotSupportedException(L.T("No audio backend is available."));

        base.Init(format, deviceId);
    }

    /// <summary>打开采集设备</summary>
    protected override void Open()
    {
        if (!AlsaDriver.IsAvailable) throw new NotSupportedException(L.T("No audio backend is available."));

        var device = DeviceId.IsNullOrEmpty() ? "default" : DeviceId;
        var result = AlsaNative.Open(device, AlsaNative.StreamCapture, Format, out _pcm);
        if (result != 0)
        {
            XTrace.WriteLine("ALSA capture device {0} failed to open: {1}", device, AlsaNative.Error(result));
            throw new InvalidOperationException(AlsaNative.Error(result));
        }

        AlsaNative.snd_pcm_prepare(_pcm);

        var frameBytes = Math.Max(1, Format.Channels * Format.BytesPerSample);
        var frames = Math.Max(1, Format.SampleRate / 50);
        _staging = new Byte[frames * frameBytes];
        _pendingOffset = 0;
        _pendingCount = 0;
    }

    /// <summary>关闭采集设备</summary>
    protected override void Close()
    {
        if (_pcm != IntPtr.Zero)
        {
            AlsaNative.snd_pcm_drop(_pcm);
            AlsaNative.snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }

        _pendingOffset = 0;
        _pendingCount = 0;
    }

    /// <summary>阻塞读取一块 PCM（snd_pcm_readi 本身阻塞）</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="timeoutMilliseconds">超时时间（毫秒），readi 本身阻塞，此参数仅保持契约一致</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示已停止</returns>
    protected override Int32 ReadNative(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0 || _pcm == IntPtr.Zero) return 0;

        var frameBytes = Math.Max(1, Format.Channels * Format.BytesPerSample);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pendingCount > 0)
            {
                var take = Math.Min(_pendingCount, buffer.Length);
                _staging.AsSpan(_pendingOffset, take).CopyTo(buffer);
                _pendingOffset += take;
                _pendingCount -= take;

                return take;
            }

            var frames = Math.Min(_staging.Length / frameBytes, buffer.Length / frameBytes);
            if (frames <= 0) return 0;

            var read = AlsaNative.snd_pcm_readi(_pcm, _staging, (UIntPtr)(UInt32)frames).ToInt64();
            if (read < 0)
            {
                // -EPIPE（欠载）等：重新准备后重试，计入欠载次数
                AddOverrun();
                AlsaNative.snd_pcm_prepare(_pcm);
                continue;
            }
            if (read == 0) continue;

            _pendingOffset = 0;
            _pendingCount = (Int32)read * frameBytes;
        }
    }
}
