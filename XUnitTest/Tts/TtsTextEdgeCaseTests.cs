using NewLife.Audio.Tts.Advanced;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>中文数字读法的边界</summary>
public class ChineseNumberEdgeCaseTests
{
    [Fact(DisplayName = "空输入原样返回")]
    public void Empty_ReturnsInput()
    {
        Assert.Null(ChineseNumber.ToChinese(null));
        Assert.Equal("", ChineseNumber.ToChinese(""));
        Assert.Equal("", ChineseNumber.ToDigits(""));
        Assert.Equal("", ChineseNumber.ToCardinalText(""));
    }

    [Fact(DisplayName = "首尾空白被去掉后读")]
    public void Whitespace_Trimmed()
    {
        Assert.Equal("四十二", ChineseNumber.ToChinese(" 42 "));
    }

    [Fact(DisplayName = "小数点结尾读作点")]
    public void TrailingPoint()
    {
        Assert.Equal("三点", ChineseNumber.ToChinese("3."));
    }

    [Fact(DisplayName = "小数逐位读且保留前导零")]
    public void Decimal_KeepsLeadingZero()
    {
        Assert.Equal("一点零五", ChineseNumber.ToChinese("1.05"));
        Assert.Equal("三点一四", ChineseNumber.ToChinese("3.14", TtsNumberMode.Cardinal));
    }

    [Fact(DisplayName = "零与全零数字串都读作零")]
    public void Zero_Variants()
    {
        Assert.Equal("零", ChineseNumber.ToChinese("0"));
        Assert.Equal("零", ChineseNumber.ToCardinalText("000"));
    }

    [Fact(DisplayName = "逐位读支持小数点，遇非数字原样返回")]
    public void Digits_WithPoint()
    {
        Assert.Equal("一二点三", ChineseNumber.ToDigits("12.3"));
        Assert.Equal("abc", ChineseNumber.ToDigits("abc"));
    }

    [Fact(DisplayName = "超过 15 位退化为逐位读")]
    public void LongNumber_FallsBackToDigits()
    {
        Assert.Equal("一二三四五六七八九零一二三四五六", ChineseNumber.ToCardinalText("1234567890123456"));
    }

    [Fact(DisplayName = "负整数按负数读")]
    public void Negative_Cardinal()
    {
        Assert.Equal("负一万", ChineseNumber.ToCardinal(-10000));
    }

    [Fact(DisplayName = "组间补零与万亿级单位")]
    public void GroupBoundaries()
    {
        Assert.Equal("一千零一十", ChineseNumber.ToCardinal(1010));
        Assert.Equal("一亿零一", ChineseNumber.ToCardinal(100000001));
        Assert.Equal("一万亿", ChineseNumber.ToCardinal(1000000000000));
    }

    [Fact(DisplayName = "显式基数读法忽略前导零")]
    public void CardinalMode_IgnoresLeadingZero()
    {
        Assert.Equal("三十五", ChineseNumber.ToChinese("35", TtsNumberMode.Cardinal));
        Assert.Equal("七", ChineseNumber.ToChinese("007", TtsNumberMode.Cardinal));
    }
}

/// <summary>文本规范化的边界</summary>
public class TtsTextEdgeCaseTests
{
    [Fact(DisplayName = "空文本规范化为空串")]
    public void Normalize_Empty()
    {
        var text = new TtsText();

        Assert.Equal("", text.Normalize(null));
        Assert.Equal("", text.Normalize(""));
    }

    [Fact(DisplayName = "关闭英文读名后保留原文")]
    public void Normalize_ReadEnglishOff()
    {
        var text = new TtsText { ReadEnglish = false };

        Assert.Equal("RTAS三十五", text.Normalize("RTAS 35"));
    }

    [Fact(DisplayName = "保留空白时词间空格不被去掉")]
    public void Normalize_KeepWhitespace()
    {
        var text = new TtsText { RemoveWhitespace = false };

        Assert.Equal("阿尔提诶艾斯 三十五", text.Normalize("RTAS 35"));
    }

    [Fact(DisplayName = "非法词条被忽略")]
    public void AddWord_IgnoresInvalid()
    {
        var text = new TtsText()
            .AddWord("", "空")
            .AddWord("A", null)
            .AddWord("CPU", "处理器");

        Assert.Equal("诶", text.Normalize("A"));
        Assert.Single(text.Words);
        Assert.Equal("处理器", text.Words["CPU"]);
    }

    [Fact(DisplayName = "词条读法中的美元符号按字面输出")]
    public void AddWord_DollarLiteral()
    {
        // $& 在正则替换里表示整个匹配，必须转义成字面量
        var text = new TtsText().AddWord("A", "$&");

        Assert.Equal("$&", text.Normalize("A"));
    }

    [Fact(DisplayName = "显式基数读法覆盖自动判断")]
    public void Normalize_CardinalMode()
    {
        var text = new TtsText();

        Assert.Equal("一百一十", text.Normalize("110", TtsNumberMode.Cardinal));
    }

    [Fact(DisplayName = "两位年份按基数读，小数按点读")]
    public void Normalize_ShortYearAndDecimal()
    {
        var text = new TtsText();

        Assert.Equal("二十四年", text.Normalize("24年"));
        Assert.Equal("三点一四", text.Normalize("3.14"));
    }

    [Fact(DisplayName = "带撇号的英文单词逐字母读名")]
    public void Normalize_Apostrophe()
    {
        var text = new TtsText();

        Assert.Equal("迪欧恩提", text.Normalize("don't"));
    }

    [Fact(DisplayName = "标点统一对空输入安全")]
    public void UnifyPunctuation_Empty()
    {
        Assert.Null(TtsText.UnifyPunctuation(null));
        Assert.Equal("", TtsText.UnifyPunctuation(""));
    }

    [Fact(DisplayName = "短句切分对空输入与无标点文本安全")]
    public void SplitClauses_EdgeCases()
    {
        Assert.Empty(TtsText.SplitClauses(null));
        Assert.Empty(TtsText.SplitClauses(""));

        var single = TtsText.SplitClauses("你好，世界");
        Assert.Single(single);
        Assert.Equal("你好，世界", single[0]);

        var two = TtsText.SplitClauses("你好。 世界。");
        Assert.Equal(2, two.Count);
        Assert.Equal("你好。", two[0]);
        Assert.Equal("世界。", two[1]);
    }
}
