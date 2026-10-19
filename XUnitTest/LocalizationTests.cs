using System;
using System.Collections.Generic;
using System.Globalization;
using NewLife;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using NewLife.Log;
using Xunit;

namespace XUnitTest;

/// <summary>运行时多语言与带前缀日志测试</summary>
[Collection("GlobalState")]
public class LocalizationTests
{
    [Fact(DisplayName = "简体中文：运行时消息命中词表")]
    public void Translate_runtime_messages()
    {
        var zh = TryGetCulture("zh-Hans");
        if (zh == null) return;

        var expected = new Dictionary<String, String>
        {
            ["No ASIO driver is installed; the device list is a placeholder."] = "未检测到已安装的 ASIO 驱动，设备列表为空壳",
            ["ASIO default device (no driver detected)"] = "ASIO 默认设备（未检测到驱动）",
            ["ASIO default device (no driver installed)"] = "ASIO 默认设备（未安装驱动）",
            ["WASAPI default output"] = "WASAPI 默认输出",
            ["WASAPI default input"] = "WASAPI 默认输入",
            ["Default audio device"] = "默认音频设备",
            ["Default recording device"] = "默认录音设备",
            ["Default device"] = "默认设备",
            ["Failed to set the FAAC configuration."] = "设置 FAAC 配置失败",
        };

        WithCulture(zh, () =>
        {
            foreach (var item in expected)
            {
                Assert.Equal(item.Value, Localization.Translate(item.Key));
            }
        });
    }

    [Fact(DisplayName = "简体中文：带占位符的运行时消息")]
    public void Translate_runtime_messages_with_placeholders()
    {
        var zh = TryGetCulture("zh-Hans");
        if (zh == null) return;

        WithCulture(zh, () =>
        {
            Assert.Equal("DESCRIBE 返回 404 Not Found", Localization.Translate("DESCRIBE returned {0} {1}", 404, "Not Found"));
            Assert.Equal("启动捕获线程失败：boom", Localization.Translate("Failed to start the capture thread: {0}", "boom"));
            Assert.Equal("waveOutOpen 返回 2，回退到空壳模式", Localization.Translate("waveOutOpen returned {0}; falling back to the null player.", 2));
            Assert.Equal("BytesToStruct 出错：boom", Localization.Translate("Error in BytesToStruct: {0}", "boom"));
        });
    }

    [Fact(DisplayName = "多语言词表：未知语言回退英文原文")]
    public void Translate_unknown_culture_falls_back()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            Assert.Equal("Default device", Localization.Translate("Default device"));
        });
    }

    [Fact(DisplayName = "带前缀日志：先翻译消息再拼接前缀")]
    public void Prefixed_log_translates_message_before_prefix()
    {
        if (!OperatingSystem.IsWindows()) return;

        var zh = TryGetCulture("zh-Hans");
        if (zh == null) return;

        var oldLog = XTrace.Log;
        var lines = new List<String>();
        var log = new ActionLog((format, args) => lines.Add(args.Length == 0 ? format : String.Format(format, args)))
        {
            Level = LogLevel.All,
        };

        try
        {
            XTrace.Log = log;
            WithCulture(zh, () =>
            {
                using var player = new WaveOutNativeBackend().CreatePlayer();
                player.Init(AudioFormat.Default, "9999");
            });
        }
        finally
        {
            XTrace.Log = oldLog;
        }

        Assert.Contains(lines, line => line.StartsWith("WaveOut: ") && line.Contains("回退到空壳模式"));
        Assert.DoesNotContain(lines, line => line.Contains("falling back"));
    }

    private static void WithCulture(CultureInfo culture, Action action)
    {
        var old = Localization.Culture;
        Localization.Culture = culture;
        try
        {
            action();
        }
        finally
        {
            Localization.Culture = old;
        }
    }

    private static CultureInfo TryGetCulture(String name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (Exception ex) when (ex is CultureNotFoundException || ex is ArgumentException)
        {
            // 不变全球化（InvariantGlobalization=true）下具名区域性不可用
            return null;
        }
    }
}
