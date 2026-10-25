using System;
using System.IO;
using System.Text;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

public class ChineseNumberTests
{
    [Fact(DisplayName = "三位数字按基数读")]
    public void Cardinal_ThreeDigits()
    {
        Assert.Equal("一百一十", ChineseNumber.ToChinese("110"));
        Assert.Equal("三十五", ChineseNumber.ToChinese("35"));
        Assert.Equal("十五", ChineseNumber.ToChinese("15"));
        Assert.Equal("一百零五", ChineseNumber.ToChinese("105"));
    }

    [Fact(DisplayName = "整数基数读覆盖零与组间补零")]
    public void Cardinal_GroupZero()
    {
        Assert.Equal("零", ChineseNumber.ToCardinal(0));
        Assert.Equal("十", ChineseNumber.ToCardinal(10));
        Assert.Equal("一万", ChineseNumber.ToCardinal(10000));
        Assert.Equal("一万零一", ChineseNumber.ToCardinal(10001));
        Assert.Equal("一亿零一万", ChineseNumber.ToCardinal(100010000));
        Assert.Equal("一亿", ChineseNumber.ToCardinal(100000000));
    }

    [Fact(DisplayName = "逐位读用于年份与编号")]
    public void Digits_ReadOneByOne()
    {
        Assert.Equal("二零二四", ChineseNumber.ToDigits("2024"));
        Assert.Equal("一一零", ChineseNumber.ToDigits("110"));
    }

    [Fact(DisplayName = "七位以上与带前导零自动逐位读")]
    public void Auto_LongNumberReadDigitByDigit()
    {
        Assert.Equal("一零零零零零零", ChineseNumber.ToChinese("1000000"));
        Assert.Equal("零零七", ChineseNumber.ToChinese("007"));
    }

    [Fact(DisplayName = "小数按整数加点再逐位读")]
    public void Decimal_ReadWithDian()
    {
        Assert.Equal("三点一四", ChineseNumber.ToChinese("3.14"));
        Assert.Equal("负五", ChineseNumber.ToChinese("-5"));
    }

    [Fact(DisplayName = "显式指定读法覆盖自动判断")]
    public void ExplicitMode_OverridesAuto()
    {
        Assert.Equal("一一零", ChineseNumber.ToChinese("110", TtsNumberMode.Digit));
        Assert.Equal("二零二四", ChineseNumber.ToChinese("2024", TtsNumberMode.Digit));
    }
}

public class TtsTextTests
{
    [Fact(DisplayName = "规范化把字母与数字转成中文并去掉空白")]
    public void Normalize_LettersNumbersWhitespace()
    {
        var text = new TtsText();

        Assert.Equal("设备型号诶二温度三十五度", text.Normalize("设备型号 A2 温度 35 度"));
    }

    [Fact(DisplayName = "四位数带年时逐位读")]
    public void Normalize_YearReadDigitByDigit()
    {
        var text = new TtsText();

        Assert.Equal("二零二四年三月", text.Normalize("2024年3月"));
    }

    [Fact(DisplayName = "英文单词按字母读名，避免被词典模型丢弃")]
    public void Normalize_EnglishWordSpelled()
    {
        var text = new TtsText();

        Assert.Equal("阿尔提诶艾斯", text.Normalize("RTAS"));
    }

    [Fact(DisplayName = "自定义词条优先于字母读名")]
    public void Normalize_CustomWordFirst()
    {
        var text = new TtsText().AddWord("CPU", "处理器");

        Assert.Equal("处理器占用率高", text.Normalize("CPU 占用率高"));
    }

    [Fact(DisplayName = "全角数字归一为半角后再读")]
    public void Normalize_FullWidthDigits()
    {
        var text = new TtsText();

        Assert.Equal("三十五", text.Normalize("３５"));
    }

    [Fact(DisplayName = "关闭数字读法时保留阿拉伯数字")]
    public void Normalize_KeepDigitsWhenDisabled()
    {
        var text = new TtsText { ReadNumbers = false };

        Assert.Equal("温度35度", text.Normalize("温度35度"));
    }

    [Fact(DisplayName = "逗号类停顿统一成句号")]
    public void UnifyPunctuation_CommaToPeriod()
    {
        Assert.Equal("你好。世界。再见", TtsText.UnifyPunctuation("你好，世界；再见"));
    }

    [Fact(DisplayName = "按句末标点切分短句")]
    public void SplitClauses_SplitsByEndPunctuation()
    {
        var list = TtsText.SplitClauses("你好。世界！再见");

        Assert.Equal(3, list.Count);
        Assert.Equal("你好。", list[0]);
        Assert.Equal("世界！", list[1]);
        Assert.Equal("再见", list[2]);
    }
}

public class TtsWavTests
{
    [Fact(DisplayName = "解析标准 WAV 头取出采样率与 PCM 偏移")]
    public void TryParse_StandardHeader()
    {
        var wav = TtsTestHelper.BuildWav(16000, 3200);

        Assert.True(TtsWav.TryParse(wav, out var info));
        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(44, info.DataOffset);
        Assert.Equal(3200, info.DataSize);
    }

    [Fact(DisplayName = "非 WAV 数据解析失败")]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(TtsWav.TryParse(new Byte[100], out _));
    }

    [Fact(DisplayName = "流式跳过 WAV 头后剩余数据正好是 PCM")]
    public async System.Threading.Tasks.Task SkipHeader_LeavesPcmOnly()
    {
        var wav = TtsTestHelper.BuildWav(22050, 2000);

        using var stream = new MemoryStream(wav);
        var info = await TtsWav.SkipHeaderAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(22050, info.SampleRate);

        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest, TestContext.Current.CancellationToken);
        Assert.Equal(2000, (Int32)rest.Length);
    }
}

internal static class TtsTestHelper
{
    /// <summary>构造标准 44 字节头的单声道 16 位 WAV</summary>
    public static Byte[] BuildWav(Int32 sampleRate, Int32 dataSize)
    {
        const Int32 channels = 1;
        const Int32 bits = 16;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((Int16)1);
        writer.Write((Int16)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((Int16)(channels * bits / 8));
        writer.Write((Int16)bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (var i = 0; i < dataSize; i++) writer.Write((Byte)(i % 100));
        writer.Flush();

        return ms.ToArray();
    }
}
