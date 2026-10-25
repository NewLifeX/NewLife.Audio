using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>Windows 系统语音引擎（SAPI）</summary>
/// <remarks>
/// 通过反射使用 <c>System.Speech.Synthesis.SpeechSynthesizer</c>，与 NewLife.Core 的 <c>Speak</c> 同一路线，
/// 不新增编译期依赖。.NET Framework 直接可用；.NET Core / .NET 5+ 应用需要引用 <c>System.Speech</c> NuGet 包。
/// <para>直接播报走系统音频设备，延迟最低；合成时写入 WAV 流后剥出 PCM 块。</para>
/// </remarks>
public sealed class SystemSpeechEngine : TtsEngineBase, ITtsSpeaker
{
    private const String TypeName = "System.Speech.Synthesis.SpeechSynthesizer";

    private static readonly Object _typeLock = new();
    private static Type _type;
    private static Boolean _typeResolved;

    private readonly Object _sync = new();
    private readonly SemaphoreSlim _speakGate = new(1, 1);
    private Object _synth;
    private TaskCompletionSource<Boolean> _speakTcs;

    /// <summary>初始化系统语音引擎</summary>
    public SystemSpeechEngine() : base("system") { }

    /// <summary>当前是否可用。仅在 Windows 且能找到 System.Speech 时为 true</summary>
    public override Boolean IsAvailable
    {
        get
        {
            if (!TtsPlatform.IsWindows) return false;
            return ResolveType() != null;
        }
    }

    /// <summary>枚举系统已安装音色</summary>
    /// <returns>音色列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices()
    {
        if (!IsAvailable) return Array.Empty<TtsVoice>();

        var list = new List<TtsVoice>();
        try
        {
            var synth = EnsureSynth();
            if (Invoke(synth, "GetInstalledVoices") is not IEnumerable voices) return list;

            foreach (var voice in voices)
            {
                if (voice == null) continue;

                var enabled = voice.GetType().GetProperty("Enabled")?.GetValue(voice);
                if (enabled is Boolean b && !b) continue;

                var info = voice.GetType().GetProperty("VoiceInfo")?.GetValue(voice);
                if (info == null) continue;

                list.Add(new TtsVoice
                {
                    Name = info.GetType().GetProperty("Name")?.GetValue(info) as String,
                    Culture = info.GetType().GetProperty("Culture")?.GetValue(info)?.ToString(),
                    Gender = info.GetType().GetProperty("Gender")?.GetValue(info)?.ToString(),
                });
            }
        }
        catch
        {
            // 枚举失败按无音色处理
        }

        return list;
    }

    /// <summary>直接播报</summary>
    /// <param name="text">待播报文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播报任务</returns>
    public async Task SpeakAsync(String text, TtsOptions options, CancellationToken cancellationToken)
    {
        await _speakGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SpeakCoreAsync(text, options, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _speakGate.Release();
        }
    }

    /// <summary>合成语音</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (pcm, format) = await SynthesizePcmAsync(text, options, cancellationToken).ConfigureAwait(false);
        if (pcm.Length > 0) yield return new TtsChunk { Format = format, Data = pcm };
    }

    /// <summary>停止当前播报</summary>
    public void Stop()
    {
        try
        {
            if (_synth != null) Invoke(_synth, "SpeakAsyncCancelAll");
        }
        catch
        {
            // 合成器可能已释放
        }

        _speakTcs?.TrySetCanceled();
    }

    private async Task<(Byte[] Pcm, AudioFormat Format)> SynthesizePcmAsync(String text, TtsOptions options, CancellationToken cancellationToken)
    {
        await _speakGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream();
            await SpeakCoreAsync(text, options, buffer, cancellationToken).ConfigureAwait(false);

            var data = buffer.ToArray();
            var info = TtsWav.TryParse(data, out var wav) ? wav : null;
            var format = info != null
                ? new AudioFormat
                {
                    SampleRate = wav.SampleRate,
                    Channels = wav.Channels,
                    BitsPerSample = wav.BitsPerSample,
                }
                : AudioFormat.Default;

            var pcm = info != null && wav.DataSize > 0 && wav.DataOffset + wav.DataSize <= data.Length
                ? new ReadOnlySpan<Byte>(data, wav.DataOffset, wav.DataSize).ToArray()
                : data;

            return (pcm, format);
        }
        finally
        {
            _speakGate.Release();
        }
    }

    private async Task SpeakCoreAsync(String text, TtsOptions options, Stream output, CancellationToken cancellationToken)
    {
        var synth = EnsureSynth();

        lock (_sync)
        {
            if (output != null)
                Invoke(synth, "SetOutputToWaveStream", output);
            else
                Invoke(synth, "SetOutputToDefaultAudioDevice");

            ApplyOptions(synth, options);
        }

        try
        {
            await SpeakTextAsync(synth, text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (output != null)
            {
                lock (_sync)
                {
                    try { Invoke(synth, "SetOutputToDefaultAudioDevice"); } catch { /* 合成器可能已释放 */ }
                }
            }
        }
    }

    private async Task SpeakTextAsync(Object synth, String text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var eventInfo = synth.GetType().GetEvent("SpeakCompleted");
        var tcs = new TaskCompletionSource<Boolean>();
        Delegate handler = null;

        if (eventInfo?.EventHandlerType != null)
        {
            var method = typeof(SystemSpeechEngine).GetMethod(nameof(OnSpeakCompleted), BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null) handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, method, false);
        }

        if (handler == null)
        {
            // 无法订阅事件时退回同步 Speak；取消只能等当前句读完
            await Task.Run(() => Invoke(synth, "Speak", text), cancellationToken).ConfigureAwait(false);
            return;
        }

        _speakTcs = tcs;
        eventInfo.AddEventHandler(synth, handler);

        using var registration = cancellationToken.Register(() =>
        {
            try { Invoke(synth, "SpeakAsyncCancelAll"); } catch { /* 合成器可能已释放 */ }
            tcs.TrySetCanceled();
        });

        try
        {
            Invoke(synth, "SpeakAsync", text);
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            eventInfo.RemoveEventHandler(synth, handler);
            if (ReferenceEquals(_speakTcs, tcs)) _speakTcs = null;
        }
    }

    private void OnSpeakCompleted(Object sender, EventArgs e)
    {
        var tcs = _speakTcs;
        if (tcs == null) return;

        var cancelled = e?.GetType().GetProperty("Cancelled")?.GetValue(e) as Boolean?;
        if (cancelled == true)
        {
            tcs.TrySetCanceled();
            return;
        }

        var error = e?.GetType().GetProperty("Error")?.GetValue(e) as Exception;
        if (error != null)
            tcs.TrySetException(error);
        else
            tcs.TrySetResult(true);
    }

    private static void ApplyOptions(Object synth, TtsOptions options)
    {
        if (options != null && !options.Voice.IsNullOrEmpty()) Invoke(synth, "SelectVoice", options.Voice);

        var rate = options?.Rate > 0 ? options.Rate : 1;
        var sapiRate = (Int32)Math.Round(10 * Math.Log(rate, 2));
        if (sapiRate < -10) sapiRate = -10;
        if (sapiRate > 10) sapiRate = 10;
        synth.GetType().GetProperty("Rate")?.SetValue(synth, sapiRate);

        var volume = options?.Volume ?? 1;
        if (volume < 0) volume = 0;
        if (volume > 1) volume = 1;
        synth.GetType().GetProperty("Volume")?.SetValue(synth, (Int32)Math.Round(volume * 100));
    }

    private Object EnsureSynth()
    {
        if (_synth != null) return _synth;

        lock (_sync)
        {
            if (_synth != null) return _synth;

            var type = ResolveType();
            if (type == null)
                throw new NotSupportedException(L.T("System.Speech is not available. On .NET Core/.NET 5+ add the System.Speech NuGet package on Windows."));

            var synth = Activator.CreateInstance(type);
            _synth = synth;
            return synth;
        }
    }

    private static Type ResolveType()
    {
        if (_typeResolved) return _type;

        lock (_typeLock)
        {
            if (_typeResolved) return _type;

            try
            {
                _type = Type.GetType(TypeName + ", System.Speech");
                if (_type == null) _type = Type.GetType(TypeName);

                if (_type == null)
                {
                    var assembly = Assembly.Load("System.Speech");
                    _type = assembly?.GetType(TypeName);
                }
            }
            catch
            {
                _type = null;
            }

            _typeResolved = true;
            return _type;
        }
    }

    private static Object Invoke(Object target, String name, params Object[] args)
    {
        if (target == null) throw new ObjectDisposedException(nameof(SystemSpeechEngine));

        return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
    }

    /// <summary>释放系统语音资源</summary>
    public override void Dispose()
    {
        if (_synth == null) return;

        lock (_sync)
        {
            try { Invoke(_synth, "SpeakAsyncCancelAll"); } catch { /* 合成器可能已释放 */ }
            try { (_synth as IDisposable)?.Dispose(); } catch { /* 忽略释放异常 */ }
            _synth = null;
        }
    }
}
