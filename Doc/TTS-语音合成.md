# TTS-语音合成

> 版本：v3.0 | 日期：2026-10-25 | [← 返回 README](../README.zh-CN.md) | [English README](../README.md)
> 需求对应：[需求文档](需求文档.md) 第 8 章 | 功能清单：[功能模块清单](功能模块清单.md)

> 设计目标：普通用户三行代码出声；需要音色、在线服务或自定义模型时再往下看。主命名空间只放极简入口、模型与引擎实现，缓存/预生成/文本规范化全部下沉到 `NewLife.Audio.Tts.Advanced`。

> 逐项验证引擎与参数时，可打开 AudioStudio 客户端的「🗣 语音合成」页签：合成测试 / 引擎参数 / 高级能力三个子页覆盖本文全部用法。

---

## 1. 30 秒上手

```csharp
using NewLife.Audio.Tts;

var tts = new TtsClient();          // 自动选择当前平台系统语音
await tts.SayAsync("设备A2温度过高，请检查");
```

- Windows：系统 SAPI（`System.Speech`）
- macOS：系统 `say`
- Linux：`espeak-ng`（需系统安装）
- 需要在线服务、piper 或自定义模型时，构造函数注入引擎（见第 5 节）

全局默认实例适合零散调用：

```csharp
await TtsClient.Default.SayAsync("系统已启动");
```

> 默认实例由进程生命周期持有，不建议调用 `Dispose`；需要独立生命周期时用 `new TtsClient()`。

---

## 2. 三种主用法

三个入口都是异步方法，统一签名 `(text, options = null, cancellationToken = default)`：

```csharp
// 1) 直接播报：播完/取消/Stop 时任务完成
await tts.SayAsync("设备A2温度过高");

// 2) 保存为文件：扩展名决定格式（.wav / .pcm）
var result = await tts.SaveAsync("设备A2温度过高", "alert.wav");
Console.WriteLine($"{result.Bytes} 字节，{result.AudioTime:F1} 秒");

// 3) 流式输出：逐块产出裸 PCM，适合 HTTP/WebSocket
await foreach (var chunk in tts.StreamAsync("设备A2温度过高"))
{
    await response.Body.WriteAsync(chunk.Data.AsMemory(), cancellationToken);
    // chunk.Format 描述采样率/声道/位深
}
```

只传取消令牌时用命名参数或 `null` 占位：

```csharp
await tts.SayAsync("文字", cancellationToken: ct);
await tts.SayAsync("文字", null, ct);
await tts.SaveAsync("文字", stream, TtsFormat.Wav, null, ct);
```

`SaveAsync` 有两个重载：

```csharp
// 按路径保存，扩展名推断格式
Task<TtsResult> SaveAsync(String text, String path, TtsOptions options = null, CancellationToken ct = default)

// 保存到流，显式指定格式
Task<TtsResult> SaveAsync(String text, Stream output, TtsFormat format = TtsFormat.Wav, TtsOptions options = null, CancellationToken ct = default)
```

### 2.1 网络流式示例

```csharp
// ASP.NET Core 最小 API：首包即出，不攒整段
app.MapGet("/tts", async (String text, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "audio/wav";
    await TtsClient.Default.SaveAsync(text, ctx.Response.Body, TtsFormat.Wav, null, ct);
});
```

- `SaveAsync(stream, TtsFormat.Wav)`：写出完整 WAV（头 + PCM），适合 HTTP/文件。
- `StreamAsync`：只产出裸 PCM，适合 WebSocket、Web Audio 或自定义协议；容器头由调用方决定。
- 引擎支持分块时是真流式；不支持时退化为“整段合成后分块写出”，调用方行为一致。

---

## 3. 音色与语速

```csharp
foreach (var voice in tts.GetVoices())
    Console.WriteLine($"{voice.Name} | {voice.Culture} | {voice.Gender}");

// 全局默认
tts.Options.Voice = "Microsoft Huihui Desktop";
tts.Options.Rate = 1.2f;     // 1.0 = 正常，>1 更快
tts.Options.Volume = 0.8f;   // 0~1

// 单次覆盖，不影响默认
await tts.SaveAsync("请注意", "alert.wav",
    new TtsOptions { Voice = "Microsoft Kangkang", Rate = 0.9f },
    cancellationToken);
```

语速倍率映射到各引擎原生单位：

| 引擎 | 映射 |
|---|---|
| Windows SAPI | `Rate = clamp(10 * log2(倍率), -10, 10)` |
| macOS `say` | 词/分钟 = 175 × 倍率，限制 80~500 |
| espeak-ng | 词/分钟 = 175 × 倍率，限制 80~500 |

---

## 4. 文件格式能力

**本轮只承诺 WAV 与裸 PCM**。这不是能力上限，而是现有压缩编码器还不具备交付质量：

| 格式 | 现状 | 结论 |
|---|---|---|
| WAV (PCM) | `WavStreamWriter` / `WaveFileWriter` 完整，可回填长度 | ✅ 支持 |
| 裸 PCM | 16 位小端 | ✅ 支持（流式/自定义协议） |
| MP3 编码 | 现有 `Mp3Codec.FromPcm` 是简化实现，帧长与 Huffman 不符合标准，标准解码器无法互通 | ❌ 不暴露 |
| AAC 编码 | 纯托管 AAC-LC 编码器已可用（ADTS/raw），TTS 输出尚未接入 | 🟡 可用，待接入 |
| Opus 编码 | `OpusCodec` 自研 M1：包解析/范围编解码器就绪，托管 CELT/SILK 未完成；当前通过系统 ffmpeg 降级，可与标准解码器互操作 | 🟡 编解码降级可用，托管实现进行中，TTS 输出待接入 |
| FLAC 编码 | 现有实现帧头/子帧不完整，解码器仅支持 Verbatim 子帧 | ❌ 不暴露 |
| Ogg Opus 封装 | `OggOpusStreamWriter` 可封装**已编码**的 Opus 包 | 🟡 仅供在线服务透传 |

> **MP3 输入**：在线服务返回的 MP3 由内置 Layer III 解码器（`Mp3Layer3Decoder`，纯托管、零外部依赖）解码为 16 位 PCM，覆盖 MPEG-1/2/2.5、长短块与联合立体声；MP3 输出仍不在本轮范围。

后续路线（需单独立项）：

1. **网络流式首选 Opus**：`OpusCodec` 已接入自研包解析与范围编解码器，托管 CELT/SILK 完成前通过系统 ffmpeg 降级；PCM 按源采样率编码后接 `OggOpusStreamWriter`，输出 `audio/ogg; codecs=opus`。
2. **文件分发需要 MP3**：Windows Media Foundation（内置）或可选原生 LAME。
3. **无损 FLAC**：重写为可互操作的纯托管编码器（固定预测 + Rice + 正确帧头/CRC）。

`TtsFormat` 与 `SaveAsync(stream, format)` 已为后续格式留好位置，加格式不改变极简 API。

---

## 5. 引擎

系统语音、HTTP、子进程与公共云服务都只是 `ITtsEngine` 的实现：

```csharp
// 在线 HTTP 引擎
var tts = new TtsClient(new HttpTtsEngine
{
    Url = "https://example.com/tts",
    Method = "POST",
    BodyTemplate = "{\"text\":\"{text}\",\"voice\":\"{voice}\"}",
    ResponseKind = TtsResponseKind.Wav,
});

// 子进程引擎（piper / espeak-ng）
var tts = new TtsClient(new ProcessTtsEngine("piper", "piper", "--model zh_CN-huayan-medium.onnx --output-raw", 22050)
{
    Normalize = true,
});
```

| 引擎 | 用途 | 直接播报 |
|---|---|---|
| `SystemSpeechEngine` | Windows SAPI | ✅ |
| `MacSayTtsEngine` | macOS `say` | ✅ |
| `EspeakTtsEngine` | Linux/macOS `espeak-ng` | ✅ |
| `HttpTtsEngine` | 在线 HTTP/HTTPS 服务 | ❌ |
| `AliyunTtsEngine` | 阿里云 NLS | ❌ |
| `TencentTtsEngine` | 腾讯云语音合成 | ❌ |
| `BaiduTtsEngine` | 百度语音合成 | ❌ |
| `BaiduTranslateTtsEngine` | 百度翻译发音（免密钥） | ❌ |
| `AzureTtsEngine` | 微软 Azure / Bing 语音合成 | ❌ |
| `SherpaTtsEngine` | sherpa-onnx 常驻离线（可选包） | ❌ |
| `ProcessTtsEngine` | piper 等命令行合成器 | ❌ |
| `NullTtsEngine` | 测试/无声卡兜底 | ❌ |

### 5.1 为什么用构造注入而不是继承 `TtsClient`

- `TtsClient` 是编排器，扩展点是 `ITtsEngine` / `ITtsSpeaker` / `CreatePlayer`，不是继承。
- 构造注入意图明确、易组合、易测试；继承会耦合基类实现。
- 横切逻辑（重试、缓存）用 `ITtsEngine` 装饰器，例如：

```csharp
var tts = new TtsClient(new CachedTtsEngine(new HttpTtsEngine { Url = "..." }, new TtsCache("/data/tts")));
```

- 单元测试注入假引擎即可，不需要为了让测试可替换而给 `TtsClient` 加虚方法。

### 5.2 自定义引擎

实现一个返回 `IAsyncEnumerable<TtsChunk>` 的方法即可，不需要回调、sink 或队列：

```csharp
using System.Runtime.CompilerServices;

public sealed class NeuralTtsEngine : TtsEngineBase
{
    public NeuralTtsEngine() : base("neural") { }

    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(
        String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1) 常驻模型一次性加载；2) 生成时逐块 yield 16 位小端 PCM
        await foreach (var pcm in Model.GenerateAsync(text, cancellationToken))
        {
            yield return new TtsChunk
            {
                Format = new AudioFormat { SampleRate = 24000, Channels = 1, BitsPerSample = 16 },
                Data = pcm,
            };
        }
    }
}
```

核心库不引 ONNX Runtime，保持纯托管与 `netstandard2.0` 兼容；模型适配器单独发包。

### 5.3 公共云服务

公共云服务都继承 `CloudTtsEngine`，统一 HTTP 复用、超时、错误读取与 PCM 解码；凭证只保存在实例属性中，异常消息只带服务端返回的错误描述，不包含密钥。

| 引擎 | 服务 | 凭证 | 响应 |
|---|---|---|---|
| `AliyunTtsEngine` | 阿里云智能语音交互 NLS | `AppKey` + `Token`，或 `AppKey` + `AccessKeyId`/`AccessKeySecret`（自动换取令牌并缓存） | WAV / PCM / MP3 |
| `TencentTtsEngine` | 腾讯云语音合成 `TextToVoice` | `SecretId` + `SecretKey`（TC3-HMAC-SHA256 签名） | base64 音频，整段解码 |
| `BaiduTtsEngine` | 百度语音合成 `text2audio` | `ApiKey` + `SecretKey`（OAuth 换取 access_token 并缓存），或直接 `Token` | WAV / PCM / MP3 |
| `AzureTtsEngine` | 微软 Azure / Bing 语音合成 | `SubscriptionKey`，或 `Token`（Bearer） | WAV / MP3（SSML 请求） |
| `BaiduTranslateTtsEngine` | 百度翻译发音（非官方、免密钥） | 无 | MP3 |

```csharp
// 阿里云：AccessKey 自动换取令牌
var tts = new TtsClient(new AliyunTtsEngine
{
    AppKey = "your-appkey",
    AccessKeyId = "LTAI...",
    AccessKeySecret = "your-secret",
});

// 腾讯云
var tts = new TtsClient(new TencentTtsEngine { SecretId = "AKID...", SecretKey = "your-secret" });

// 百度
var tts = new TtsClient(new BaiduTtsEngine { ApiKey = "your-api-key", SecretKey = "your-secret" });

// 微软 Bing / Azure
var tts = new TtsClient(new AzureTtsEngine { SubscriptionKey = "your-key", Voice = "zh-CN-XiaoxiaoNeural" });

await tts.SaveAsync("设备A2温度过高", "alert.wav");
```

音色、语速、音量都从 `TtsOptions` 映射到各家原生参数：

| 引擎 | 音色 | 语速（1 = 常速） | 音量（1 = 原音量） |
|---|---|---|---|
| 阿里云 | `voice`，默认 xiaoyun | `(倍率-1)×500`，范围 -500~500 | `×100`，范围 0~100 |
| 腾讯云 | `VoiceType` 编号，默认 101001 | 就近映射到 -2~6（0.6/0.8/1.0/1.2/1.5/2.5 倍） | `(音量-1)×10`，范围 -10~0 |
| 百度 | `per` 编号，默认 0（度小美） | `5+(倍率-1)×5`，范围 0~15 | `×5`，范围 0~15 |
| 微软 | 完整音色名，默认 zh-CN-XiaoxiaoNeural | SSML prosody 百分比 | SSML prosody 百分比 |

- WAV 响应边收边剥头，适合大段文本；MP3 与腾讯云的 base64 音频必须整段解码，内存占用与音频时长成正比。
- 服务端错误：HTTP 非 2xx 抛 `HttpRequestException`；HTTP 200 但返回 JSON 错误对象时抛 `InvalidOperationException`，消息里带服务端错误描述。
- 令牌缓存：阿里云与百度在实例内缓存令牌并提前 60 秒刷新；多个引擎实例之间不共享缓存，需要共享时复用同一个引擎实例。
- `OutputFormat`/`Codec` 选 `mp3` 时依赖库内简化解码器，与第三方编码器的互操作不保证；生产环境优先 WAV/PCM。
- 免密钥的 Edge 朗读走 WebSocket 私有协议，不在本轮范围，见 TTS-15 暂缓项；需要同源音色时用 `AzureTtsEngine`。
- 其他 HTTP 形态的服务（如火山引擎）可以继承 `CloudTtsEngine` 复用时序、超时与解码，或直接用 `HttpTtsEngine` 配模板。

### 5.4 sherpa-onnx 常驻离线引擎（可选包）

sherpa-onnx 需要原生推理运行时，因此不放进核心库，而是独立可选包 `NewLife.Audio.Sherpa`（依赖 `org.k2fsa.sherpa.onnx`）；核心库 `NewLife.Audio` 继续保持纯托管、零外部 NuGet 依赖。

```powershell
dotnet add package NewLife.Audio.Sherpa
```

支持的模型架构与目录约定（与 sherpa-onnx 官方 release 包一致，不硬编码文件名）：

| 架构 | 目录特征 | 说明 |
|---|---|---|
| VITS | 最大 `*.onnx` + `tokens.txt` + `lexicon*.txt` 或 `phone/date/number.fst` | piper / icefall / MeloTTS 转换模型；缺文本前端的模型标记为不可用 |
| Matcha | `matcha*` 目录 + 模型根目录的 `vocos-*.onnx` / `hifigan_v2.onnx` | 声学模型与声码器分开 |
| Kokoro | `kokoro*` 目录 + `voices.bin` | 103 个音色，`KokoroVoices` 提供 sid ↔ 名称对照 |

```csharp
using NewLife.Audio.Tts.Sherpa;

var root = SherpaTtsCatalog.DefaultRoot;             // 环境变量 TTS_MODELS 可覆盖
var model = SherpaTtsCatalog.Find("chaowen", root); // 别名：chaowen / baker / kokoro / xiaoya…
using var tts = new TtsClient(new SherpaTtsEngine(model, threads: 3));
await tts.SaveAsync("设备A2温度过高", "alert.wav");
```

- **常驻加载**：`OfflineTts` 首次使用时构造一次，后续合成不再付模型加载开销；`LoadTime` 可读取。
- **逐块流式**：原生回调产出的 float 采样即时转 16 位 PCM 并 `yield`，首包延迟由模型决定。
- **音色**：`TtsOptions.Voice` 传说话人编号；Kokoro 可用 `KokoroVoices.Name(3)` 换算编号，`GetVoices()` 直接返回 103 个命名音色。
- **线程**：`threads` 按设备内存设置，1GB 无 swap 设备建议 2~3；int8 模型在 ARM 上不一定更快，以实测为准。
- **许可**：`SherpaTtsModel.License` 标注模型许可，商用前必须核对；Kokoro/aishell3 为 Apache-2.0，Baker 与 xiao_ya 为非商用。
- 模型体积大（几十 MB 到上百 MB），不随包分发；目录不存在时 `Discover` 返回空列表、`IsAvailable` 为 false。

### 5.5 百度翻译发音（免密钥）

`BaiduTranslateTtsEngine` 调用百度翻译网页的发音接口，无需账号，返回 16kHz 单声道 MP3 后解码为 PCM：

```csharp
var tts = new TtsClient(new BaiduTranslateTtsEngine());
await tts.SayAsync("设备A2温度过高");
```

- 属于**非官方接口**，可能随时调整或限流；生产环境请改用官方 `BaiduTtsEngine`（需密钥）。
- `TtsOptions.Rate` 映射到 `spd`（1~15，5 为常速）；接口不支持音量。
- 请求自动带浏览器 UA 与 Referer，满足接口的防盗链要求。

---

## 6. 高级能力（`NewLife.Audio.Tts.Advanced`）

主命名空间不暴露缓存、预生成与文本规范化，避免普通用户被“架构”吓到。需要时显式 `using NewLife.Audio.Tts.Advanced;`。

### 6.1 磁盘缓存

```csharp
using NewLife.Audio.Tts.Advanced;

var cache = new TtsCache("/data/tts-cache");
var engine = new CachedTtsEngine(new SystemSpeechEngine(), cache);
var tts = new TtsClient(engine);
```

只对相同引擎/文本/音色/语速生效：整段合成直接读写缓存；`SaveAsync` / `StreamAsync` 会边转发边缓冲，结束后写入缓存。启用缓存意味着接受整段音频缓冲在内存中，需要极致低内存的流式场景不要套本装饰器。缓存键由引擎名、音色、语速与文本决定。

### 6.2 预生成语音库

固定告警语提前合成落盘，运行时就是一次文件读取：

```csharp
var pregen = new TtsPreGenerated(new SystemSpeechEngine(), new TtsCache("/data/tts-cache"));
var count = await pregen.GenerateAsync(["设备离线", "温度过高", "请检查网络"]);

// 运行时让 TtsClient 指向同一缓存目录
var tts = new TtsClient(new CachedTtsEngine(pregen.Engine, pregen.Cache));
await tts.SayAsync("设备离线");
```

预生成与缓存共用键与存储格式；`TryGet` 可直接查询某句是否已生成。

### 6.3 文本规范化

`TtsText` / `ChineseNumber` 供需要中文数字、英文读名、去空白的离线模型使用：

```csharp
using NewLife.Audio.Tts.Advanced;

var text = TtsText.Default.Normalize("设备型号 A2 温度 35 度");
// 设备型号诶二温度三十五度
```

`ProcessTtsEngine` 提供 `Normalize` 开关；系统语音与在线服务默认不做规范化，避免破坏英文空格与数字读法。

---

## 7. 设计决策

| 决策 | 理由 |
|---|---|
| 三个主入口全部异步，`options` 可选、`ct` 最后 | 统一调用形态；`SayAsync`/`SaveAsync`/`StreamAsync` 语义一致 |
| 每类只保留一个方法（不做成对重载） | API 面最小；只传取消令牌时用命名参数或 `null` 占位 |
| 引擎 SPI 用 `IAsyncEnumerable<TtsChunk>` | 直接 `yield return`，消费端天然背压；删除 `ITtsSink`、`TtsRequest`、桥接队列与委托引擎 |
| 引擎只产 PCM，不返回结果对象 | 文件封装、统计、播放统一由 `TtsClient` 完成，引擎实现更薄 |
| 引擎构造注入，`TtsClient` 不开放继承 | 组合优于继承；单元测试与多引擎组合更简单 |
| 默认按平台选择系统语音 | 普通用户零配置；Windows SAPI / macOS say / Linux espeak-ng |
| 本轮只做 WAV/PCM | 现有 MP3/AAC/Opus/FLAC 编码器不具备互操作质量，不拿用户当测试 |
| 系统语音走反射而非编译期引用 | 保持核心库零原生依赖与三 TFM；.NET Core 应用按需引用 `System.Speech` |
| 缓存/预生成/文本规范化下沉到 `Tts.Advanced` | 主路径不落盘、不创建临时目录；高频告警场景按需启用 |
| 不内置引擎降级链 | 链式降级会掩盖首包后失败、拼接不同音色；需要时由调用方用自定义引擎组合 |
| 首包后失败直接抛出 | 已写出的音频无法撤回，换引擎会产生不同音色拼接 |
| 公共云服务一云一引擎，而不是通用模板 | 签名、鉴权与错误对象各家差异大，通用模板会把签名调试成本推给用户；强类型属性让凭证与参数映射集中、可测试 |
| sherpa-onnx 做成独立可选包 | 原生推理运行时体积大、平台相关；核心库保持纯托管与三 TFM，需要离线神经网络时按需引用 |
| 免密钥百度翻译发音作为便利入口 | 无需账号即可验证链路；属非官方接口，生产仍推荐官方引擎 |

---

## 8. 实测参考数据

以下数据来自 RK3568（4×Cortex-A55，949MB 内存，无 swap）与开发机的同批模型对比，用于容量规划：

| 引擎形态 | 采样率 | 模型体积 | 实时率 | 首包 | 加载后内存增量 |
|---|---|---|---|---|---|
| 中文流式模型（fp32） | 22.05kHz | 60MB | 0.67 | 1.33s | 214MB |
| 中文流式模型（int8） | 22.05kHz | 17MB | 1.38 | 2.65s | 180MB |
| 大模型（int8，80MB） | 24kHz | 105MB | 8.14 | 14.3s | 567MB |
| 子进程（每句起进程） | 22.05kHz | 60MB | — | 3.11s | — |

三条结论：

1. **int8 在 ARM 上不一定更快**：实测比 fp32 慢 1.7~2.3 倍，只有体积优势。
2. **进程式引擎固定开销大**：每句重新加载模型，高频播报应使用缓存或预生成。
3. **大模型会吃掉半台设备**：567MB 增量在 949MB 设备上属于危险区，嵌入式场景优先小模型或子进程方案。

---

## 9. 平台与 AOT

| 平台 | 默认引擎 | 说明 |
|---|---|---|
| Windows | `SystemSpeechEngine` | .NET Framework 开箱可用；.NET Core/.NET 5+ 应用需引用 `System.Speech` NuGet 包 |
| macOS | `MacSayTtsEngine` | 系统自带 `/usr/bin/say`；流式为“整段生成后分块” |
| Linux | `EspeakTtsEngine` | 需安装 `espeak-ng`；`--stdout` 输出 WAV，真流式 |
| 非直出引擎 | `HttpTtsEngine` / `ProcessTtsEngine` | `SayAsync` 走 PCM 播放回退；非 Windows 无可用 PCM 播放驱动时请用 `SaveAsync`/`StreamAsync` |
| 公共云引擎 | `AliyunTtsEngine` / `TencentTtsEngine` / `BaiduTtsEngine` / `AzureTtsEngine` | 纯托管 + HTTPS，需要外网与有效凭证；同样走 PCM 播放回退 |
| 免密钥在线引擎 | `BaiduTranslateTtsEngine` | 纯托管 + HTTPS，无需凭证；非官方接口 |
| 离线神经引擎 | `SherpaTtsEngine`（`NewLife.Audio.Sherpa` 可选包） | 需要本地模型与 sherpa-onnx 原生运行时；支持 Windows/Linux/macOS 的 x64/arm64（Android 需另配） |

AOT/裁剪：`SystemSpeechEngine` 依赖反射，不保证 NativeAOT 可用；AOT 场景请注入自定义引擎或使用 `ProcessTtsEngine` / `HttpTtsEngine` / 公共云引擎。

---

（完）
