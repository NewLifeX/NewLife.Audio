using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewLife.Audio;
using NewLife.Audio.Codecs;
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

    public ToolsViewModel()
    {
        LoadCodecRegistry();
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
