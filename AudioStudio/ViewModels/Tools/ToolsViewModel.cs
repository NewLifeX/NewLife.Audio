using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Speech;
using NewLife.Audio.Streaming;
using NewLife.Data;

namespace AudioStudio.ViewModels.Tools;

/// <summary>编解码信息项</summary>
public class CodecInfoItem
{
    public String Name { get; set; } = "";
    public String AvType { get; set; } = "";
    public String Version { get; set; } = "";
    public String IsStateful { get; set; } = "";
    public String SupportedTypes { get; set; } = "";
}

/// <summary>RTP 包信息项</summary>
public class RtpPacketInfo
{
    public UInt16 SequenceNumber { get; set; }
    public UInt32 Timestamp { get; set; }
    public UInt32 Ssrc { get; set; }
    public Boolean Marker { get; set; }
    public Byte PayloadType { get; set; }
    public Int32 PayloadLength { get; set; }
    public String HexPreview { get; set; } = "";
}

/// <summary>编解码与诊断工具视图模型</summary>
public partial class ToolsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<CodecInfoItem> _codecList = [];

    [ObservableProperty]
    private CodecInfoItem? _selectedCodec;

    [ObservableProperty]
    private String _pcmHexInput = "";

    [ObservableProperty]
    private String _pcmHexOutput = "";

    [ObservableProperty]
    private Int32 _selectedTab;

    [ObservableProperty]
    private String _rtpInput = "";

    [ObservableProperty]
    private RtpPacketInfo? _rtpParsedPacket;

    [ObservableProperty]
    private String _rtpStatus = "";

    /// <summary>已解析的 RTP 包列表</summary>
    public ObservableCollection<RtpPacketInfo> RtpPackets { get; } = [];

    // ─── VAD 测试 ───
    [ObservableProperty]
    private Boolean _vadIsRunning;

    [ObservableProperty]
    private Boolean _vadResult;

    [ObservableProperty]
    private Double _vadProbability;

    [ObservableProperty]
    private Int32 _vadAggressiveness = 1;

    [ObservableProperty]
    private Int32 _vadTestToneFrequency = 440;

    [ObservableProperty]
    private String _vadStatus = "就绪";

    [ObservableProperty]
    private ObservableCollection<String> _vadLog = [];

    private CancellationTokenSource? _vadCts;
    private readonly GmmVad _vad;

    public ToolsViewModel()
    {
        _vad = new GmmVad(8000, _vadAggressiveness);
        LoadCodecRegistry();
    }

    /// <summary>VAD 测试：生成测试音并检测</summary>
    [RelayCommand]
    private void VadStartTest()
    {
        if (VadIsRunning) return;

        _vadCts = new CancellationTokenSource();
        VadIsRunning = true;
        VadLog.Clear();
        VadStatus = "测试中...";

        var token = _vadCts.Token;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var vad = new GmmVad(8000, VadAggressiveness);
            var sampleRate = 8000;
            var frameSize = 160; // 20ms @ 8kHz
            var rng = new Random();

            for (var phase = 0; phase < 5 && !token.IsCancellationRequested; phase++)
            {
                // Phase0-3: 440Hz 正弦波（语音模拟）
                // Phase1: silence
                // Phase2: 复合频率
                // Phase3: 噪声
                // Phase4: 混合
                for (var f = 0; f < 50 && !token.IsCancellationRequested; f++)
                {
                    var frame = new Byte[frameSize * 2];
                    var isSpeech = false;

                    switch (phase)
                    {
                        case 0: // 440Hz 纯音（语音模拟）
                            for (var i = 0; i < frameSize; i++)
                            {
                                var val = (Int16)(Math.Sin(2 * Math.PI * VadTestToneFrequency * (f * frameSize + i) / sampleRate) * 20000);
                                frame[i * 2] = (Byte)(val & 0xFF);
                                frame[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
                            }
                            isSpeech = true;
                            break;
                        case 1: // 静音
                            isSpeech = false;
                            break;
                        case 2: // 多频复合
                            for (var i = 0; i < frameSize; i++)
                            {
                                var idx = f * frameSize + i;
                                var val = (Int16)((Math.Sin(2 * Math.PI * 300 * idx / sampleRate) * 0.5 +
                                                   Math.Sin(2 * Math.PI * 1000 * idx / sampleRate) * 0.3 +
                                                   Math.Sin(2 * Math.PI * 3000 * idx / sampleRate) * 0.2) * 20000);
                                frame[i * 2] = (Byte)(val & 0xFF);
                                frame[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
                            }
                            isSpeech = true;
                            break;
                        case 3: // 白噪声
                            for (var i = 0; i < frameSize; i++)
                            {
                                var val = (Int16)((rng.NextDouble() - 0.5) * 20000);
                                frame[i * 2] = (Byte)(val & 0xFF);
                                frame[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
                            }
                            isSpeech = false;
                            break;
                        case 4: // 交替
                            isSpeech = f % 10 < 6;
                            for (var i = 0; i < frameSize; i++)
                            {
                                var val = isSpeech
                                    ? (Int16)(Math.Sin(2 * Math.PI * 440 * (f * frameSize + i) / sampleRate) * 20000)
                                    : (Int16)0;
                                frame[i * 2] = (Byte)(val & 0xFF);
                                frame[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
                            }
                            break;
                    }

                    var prob = vad.GetSpeechProbability(frame);
                    var detected = vad.IsSpeech(frame);

                    var phaseName = phase switch
                    {
                        0 => "正弦波 440Hz",
                        1 => "静音",
                        2 => "复合频率",
                        3 => "白噪声",
                        _ => "交替模式"
                    };

                    var logLine = $"[{phaseName}] 帧{f:D2}: 概率={prob:P1} 判定={(detected ? "🗣 语音" : "🔇 静音")}";
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        VadResult = detected;
                        VadProbability = prob;
                        if (VadLog.Count > 100) VadLog.RemoveAt(0);
                        VadLog.Add(logLine);
                    });

                    Thread.Sleep(20); // 模拟实时
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                VadIsRunning = false;
                VadStatus = "测试完成 ✓";
            });
        });
    }

    [RelayCommand]
    private void VadStop()
    {
        _vadCts?.Cancel();
        VadIsRunning = false;
        VadStatus = "已停止";
    }

    [RelayCommand]
    private void VadClear()
    {
        VadLog.Clear();
        VadProbability = 0;
        VadResult = false;
        VadStatus = "就绪";
    }

    partial void OnVadAggressivenessChanged(Int32 value)
    {
        _vad.Aggressiveness = value;
    }

    private void LoadCodecRegistry()
    {
        var factory = new AudioCodecFactory();
        var registry = factory.Registry;

        CodecList.Clear();
        foreach (var avType in registry.RegisteredTypes)
        {
            try
            {
                var codec = registry.GetCodec(avType);
                var info = codec as ICodecInfo;
                CodecList.Add(new CodecInfoItem
                {
                    Name = info?.Name ?? codec.GetType().Name,
                    AvType = avType.ToString(),
                    Version = info?.Version ?? "1.0",
                    IsStateful = info?.IsStateful == true ? "是" : "否",
                    SupportedTypes = info != null
                        ? String.Join(", ", info.SupportedTypes.Select(t => t.ToString()))
                        : avType.ToString(),
                });
            }
            catch
            {
                // 跳过无法获取的编解码器
            }
        }

        // 添加额外已知但未注册的类型
        CodecList.Add(new CodecInfoItem
        {
            Name = "PCM (线性)",
            AvType = "LPCM",
            Version = "-",
            IsStateful = "否",
            SupportedTypes = "LPCM",
        });

        CodecList.Add(new CodecInfoItem
        {
            Name = "FLAC (内置)",
            AvType = "FLAC",
            Version = "-",
            IsStateful = "是",
            SupportedTypes = "FLAC (容器内部解码)",
        });
    }

    /// <summary>清空 PCM 查看器</summary>
    [RelayCommand]
    private void ClearPcm()
    {
        PcmHexInput = "";
        PcmHexOutput = "";
    }

    /// <summary>解析 PCM 数据</summary>
    [RelayCommand]
    private void ParsePcm()
    {
        if (String.IsNullOrWhiteSpace(PcmHexInput))
        {
            PcmHexOutput = "请输入十六进制数据";
            return;
        }

        try
        {
            var hex = PcmHexInput.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var bytes = new Byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);

            // 显示解析信息
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"字节数: {bytes.Length}");
            sb.AppendLine();

            if (bytes.Length >= 2)
            {
                // 尝试作为 16-bit PCM 解析
                var samples = bytes.Length / 2;
                sb.AppendLine($"16-bit PCM 样本数: {samples}");
                var maxSample = 0;
                var minSample = 0;
                for (var i = 0; i < bytes.Length - 1; i += 2)
                {
                    var sample = (Int16)(bytes[i] | (bytes[i + 1] << 8));
                    if (sample > maxSample) maxSample = sample;
                    if (sample < minSample) minSample = sample;
                }
                sb.AppendLine($"最大样本值: {maxSample}");
                sb.AppendLine($"最小样本值: {minSample}");
                sb.AppendLine($"动态范围: {maxSample - minSample}");
                sb.AppendLine();
            }

            // 十六进制预览（前 256 字节）
            sb.AppendLine("前 256 字节十六进制:");
            var previewLen = Math.Min(256, bytes.Length);
            for (var i = 0; i < previewLen; i += 16)
            {
                var lineBytes = Math.Min(16, previewLen - i);
                var line = "";
                for (var j = 0; j < lineBytes; j++)
                    line += $"{bytes[i + j]:X2} ";
                sb.AppendLine(line);
            }

            PcmHexOutput = sb.ToString();
        }
        catch (Exception ex)
        {
            PcmHexOutput = $"解析失败: {ex.Message}";
        }
    }

    /// <summary>解析 RTP 包</summary>
    [RelayCommand]
    private void ParseRtp()
    {
        if (String.IsNullOrWhiteSpace(RtpInput))
        {
            RtpStatus = "请输入十六进制数据";
            return;
        }

        try
        {
            var hex = RtpInput.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var bytes = new Byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);

            if (bytes.Length < 12)
            {
                RtpStatus = "数据不足 12 字节，不是有效的 RTP 头";
                return;
            }

            var packet = RtpPacket.FromBytes(bytes);

            var info = new RtpPacketInfo
            {
                SequenceNumber = packet.SequenceNumber,
                Timestamp = packet.Timestamp,
                Ssrc = packet.Ssrc,
                Marker = packet.Marker,
                PayloadType = packet.PayloadType,
                PayloadLength = packet.Payload?.Length ?? 0,
                HexPreview = packet.Payload?.Length > 0
                    ? BitConverter.ToString(packet.Payload, 0, Math.Min(32, packet.Payload.Length)).Replace("-", " ")
                    : "无负载",
            };

            RtpParsedPacket = info;
            RtpPackets.Insert(0, info);
            RtpStatus = $"解析成功: 序列号={info.SequenceNumber}, SSRC={info.Ssrc:X8}";
        }
        catch (Exception ex)
        {
            RtpStatus = $"解析失败: {ex.Message}";
        }
    }

    /// <summary>清空 RTP 日志</summary>
    [RelayCommand]
    private void ClearRtp()
    {
        RtpPackets.Clear();
        RtpParsedPacket = null;
        RtpStatus = "";
    }

    /// <summary>加载示例 RTP 包</summary>
    [RelayCommand]
    private void LoadSampleRtp()
    {
        // 一个简单的 RTP 包示例（G.711 A-law 负载）
        // 80 90 00 01 00 00 00 00 00 00 00 00 = RTP header with seq=1, ts=0, ssrc=0
        RtpInput = "80 90 00 01 00 00 00 00 00 00 00 00 D5 87 A3 6B 4F 1E 2C 9A";
    }
}
