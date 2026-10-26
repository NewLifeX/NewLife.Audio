using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>公共云语音合成引擎基类</summary>
/// <remarks>
/// 阿里云、腾讯云、百度、微软 Azure 等公共云服务形态一致：一次 HTTP 请求换回一段音频，
/// 差异集中在鉴权、请求体与响应封装。本类统一 HttpClient 复用、超时、错误读取与 PCM 解码，
/// 派生类只负责拼装请求、解释响应。
/// <para>密钥只保存在实例属性中，不写入日志；异常消息只带服务端返回的错误描述，不含密钥。</para>
/// <para>派生引擎把 <see cref="TtsOptions.Voice"/> 映射到各自的音色参数，音色为空时使用引擎默认值。</para>
/// </remarks>
public abstract class CloudTtsEngine : TtsEngineBase
{
    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>自定义 HttpClient，为 null 时使用内置共享实例</summary>
    public HttpClient Client { get; set; }

    /// <summary>超时时间（秒）。0 表示不限制，仍受取消令牌约束</summary>
    public Int32 TimeoutSeconds { get; set; } = 30;

    /// <summary>初始化云服务引擎</summary>
    /// <param name="name">引擎名</param>
    protected CloudTtsEngine(String name) : base(name) { }

    /// <summary>内置音色目录快照</summary>
    /// <remarks>
    /// 云服务的音色由服务端维护且时常变化，引擎只内置一份常用快照（含默认音色，供 <see cref="TtsVoiceCatalog"/> 挑选）。
    /// 需要完整或最新列表时整体替换本属性，或向 <see cref="TtsVoiceCatalog"/> 追加静态描述。
    /// </remarks>
    public IList<TtsVoice> Voices { get; set; } = [];

    /// <summary>枚举内置音色</summary>
    /// <returns>音色列表，未配置时返回空列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices()
    {
        var voices = Voices;
        if (voices == null || voices.Count <= 0) return Array.Empty<TtsVoice>();
        if (voices is IReadOnlyList<TtsVoice> list) return list;

        var copy = new List<TtsVoice>(voices.Count);
        foreach (var voice in voices)
        {
            if (voice != null) copy.Add(voice);
        }

        return copy;
    }

    /// <summary>创建音色描述</summary>
    /// <param name="id">引擎原生音色标识，直接传入请求参数</param>
    /// <param name="name">音色名称</param>
    /// <param name="culture">区域，如 zh-CN；只支持特定语种时可只写 zh、en</param>
    /// <param name="gender">性别，Female 或 Male</param>
    /// <param name="isDefault">是否引擎默认音色</param>
    /// <param name="scenarios">适用场景，留空表示通用</param>
    /// <returns>音色描述</returns>
    protected static TtsVoice CreateVoice(String id, String name, String culture, String gender, Boolean isDefault, params TtsScenario[] scenarios) => new()
    {
        Id = id,
        Name = name,
        Culture = culture,
        Gender = gender,
        IsDefault = isDefault,
        Scenarios = scenarios == null || scenarios.Length <= 0 ? [] : new List<TtsScenario>(scenarios),
    };

    /// <summary>取当前 HttpClient</summary>
    /// <returns>注入的实例或内置共享实例</returns>
    protected HttpClient GetClient() => Client ?? _shared;

    /// <summary>创建带超时的取消源。由调用方 using 保证生命周期覆盖整个请求</summary>
    /// <param name="cancellationToken">外部取消令牌</param>
    /// <returns>取消源</returns>
    protected CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeoutSeconds > 0) cts.CancelAfter(TimeoutSeconds * 1000);

        return cts;
    }

    /// <summary>读取响应正文（UTF-8），读取失败时返回空串</summary>
    /// <param name="response">HTTP 响应</param>
    /// <returns>正文</returns>
    protected static async Task<String> ReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return data == null || data.Length == 0 ? String.Empty : Encoding.UTF8.GetString(data);
        }
        catch
        {
            return String.Empty;
        }
    }

    /// <summary>创建 16 位单声道音频格式</summary>
    /// <param name="sampleRate">采样率，非法值回落到 16kHz</param>
    /// <returns>音频格式</returns>
    protected static AudioFormat CreateFormat(Int32 sampleRate) => new()
    {
        SampleRate = sampleRate > 0 ? sampleRate : 16000,
        Channels = 1,
        BitsPerSample = 16,
    };

    /// <summary>把整段音频解成一块 PCM。WAV 会剥离文件头并按头信息回填格式</summary>
    /// <param name="data">音频数据</param>
    /// <param name="format">输出格式，WAV 时按头信息回填</param>
    /// <param name="codec">编码：wav / pcm / mp3</param>
    /// <returns>PCM 块，没有音频时返回 null</returns>
    protected static TtsChunk DecodeAudio(Byte[] data, AudioFormat format, String codec)
    {
        if (data == null || data.Length == 0) return null;

        if (codec != null && codec.Equals("mp3", StringComparison.OrdinalIgnoreCase))
        {
            var mp3Pcm = TtsMp3.Decode(data, format);
            return mp3Pcm.Length == 0 ? null : new TtsChunk { Format = format, Data = mp3Pcm };
        }

        if (codec != null && codec.Equals("pcm", StringComparison.OrdinalIgnoreCase))
            return new TtsChunk { Format = format, Data = data };

        if (!TtsWav.TryParse(data, out var info) || info == null)
            return new TtsChunk { Format = format, Data = data };

        if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
        if (info.Channels > 0) format.Channels = info.Channels;
        if (info.BitsPerSample > 0) format.BitsPerSample = info.BitsPerSample;

        var offset = info.DataOffset;
        var size = info.DataSize > 0 && offset + info.DataSize <= data.Length ? info.DataSize : data.Length - offset;
        if (size <= 0) return null;

        var pcm = new Byte[size];
        Buffer.BlockCopy(data, offset, pcm, 0, size);
        return new TtsChunk { Format = format, Data = pcm };
    }

    /// <summary>按声明格式读取响应流。WAV 边收边剥头，MP3 整段解码，PCM 原样透传</summary>
    /// <param name="response">HTTP 响应</param>
    /// <param name="format">输出格式</param>
    /// <param name="codec">编码：wav / pcm / mp3</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块</returns>
    protected static async IAsyncEnumerable<TtsChunk> ReadAudioAsync(HttpResponseMessage response, AudioFormat format, String codec, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (codec != null && codec.Equals("mp3", StringComparison.OrdinalIgnoreCase))
        {
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var chunk = DecodeAudio(data, format, codec);
            if (chunk != null) yield return chunk;

            yield break;
        }

        var wav = codec == null || !codec.Equals("pcm", StringComparison.OrdinalIgnoreCase);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        if (wav)
        {
            var info = await TtsWav.SkipHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (info != null)
            {
                if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
                if (info.Channels > 0) format.Channels = info.Channels;
                if (info.BitsPerSample > 0) format.BitsPerSample = info.BitsPerSample;
            }
        }

        var buffer = new Byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            var data = new Byte[read];
            Buffer.BlockCopy(buffer, 0, data, 0, read);
            yield return new TtsChunk { Format = format, Data = data };
        }
    }

    /// <summary>从 JSON 对象里取字符串值</summary>
    /// <param name="map">JSON 对象</param>
    /// <param name="key">字段名</param>
    /// <returns>字符串值，缺失时返回 null</returns>
    protected static String GetString(IDictionary<String, Object> map, String key)
    {
        if (map == null || key == null) return null;
        if (!map.TryGetValue(key, out var value) || value == null) return null;

        return value as String ?? value.ToString();
    }

    /// <summary>从 JSON 对象里取嵌套对象</summary>
    /// <param name="map">JSON 对象</param>
    /// <param name="key">字段名</param>
    /// <returns>嵌套对象，缺失时返回 null</returns>
    protected static IDictionary<String, Object> GetObject(IDictionary<String, Object> map, String key)
    {
        if (map == null || key == null) return null;
        if (!map.TryGetValue(key, out var value)) return null;

        return value as IDictionary<String, Object>;
    }

    /// <summary>从 JSON 对象里取整数</summary>
    /// <param name="map">JSON 对象</param>
    /// <param name="key">字段名</param>
    /// <returns>整数值，缺失或非法时返回 0</returns>
    protected static Int64 GetInt64(IDictionary<String, Object> map, String key)
    {
        var value = GetString(map, key);
        return !value.IsNullOrEmpty() && Int64.TryParse(value, out var number) ? number : 0;
    }
}
