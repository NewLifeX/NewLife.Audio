using System.Text;

namespace NewLife.Audio.Tts.Advanced;

/// <summary>阿拉伯数字转中文读法</summary>
/// <remarks>
/// 多数离线声学模型与音素化前端（espeak-ng 的 cmn 尤其明显）对纯数字串会读错或直接跳过，
/// 因此在送合成前先把数字转成中文汉字是通用做法。
/// 本类只处理“读法”，不处理量词与单位。
/// </remarks>
/// <example>
/// <code>
/// ChineseNumber.ToChinese("110")        // 一百一十
/// ChineseNumber.ToChinese("1998年")     // 只会被 TtsText 用来判定年份，此处返回 一九九八
/// ChineseNumber.ToChinese("3.14")       // 三点一四
/// ChineseNumber.ToDigits("2024")        // 二零二四
/// </code>
/// </example>
public static class ChineseNumber
{
    private static readonly String[] _digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    private static readonly String[] _units = ["", "十", "百", "千"];
    private static readonly String[] _groups = ["", "万", "亿", "万亿"];

    /// <summary>按读法约定转换数字串</summary>
    /// <param name="number">数字串，可含小数点与负号</param>
    /// <param name="mode">读法。Auto 时四位年份与七位以上号码逐位读</param>
    /// <returns>中文读法。无法识别时原样返回</returns>
    public static String ToChinese(String number, TtsNumberMode mode = TtsNumberMode.Auto)
    {
        if (number.IsNullOrEmpty()) return number;

        var text = number.Trim();
        var negative = text.StartsWith("-");
        if (negative) text = text.Substring(1);

        var dot = text.IndexOf('.');
        var integer = dot >= 0 ? text.Substring(0, dot) : text;
        var fraction = dot >= 0 ? text.Substring(dot + 1) : null;

        var body = mode switch
        {
            TtsNumberMode.Digit => ToDigits(integer),
            TtsNumberMode.Cardinal => ToCardinalText(integer),
            _ => DefaultRead(integer),
        };

        if (!fraction.IsNullOrEmpty()) body = body + "点" + ToDigits(fraction);
        else if (fraction != null) body += "点";

        return negative ? "负" + body : body;
    }

    /// <summary>逐位读，适用于年份、电话号码、编号</summary>
    /// <param name="number">数字串</param>
    /// <returns>逐位中文</returns>
    public static String ToDigits(String number)
    {
        if (number.IsNullOrEmpty()) return number;

        var sb = new StringBuilder(number.Length * 2);
        foreach (var ch in number)
        {
            if (ch >= '0' && ch <= '9') sb.Append(_digits[ch - '0']);
            else if (ch == '.') sb.Append('点');
        }

        return sb.Length == 0 ? number : sb.ToString();
    }

    /// <summary>数字按基数读</summary>
    /// <param name="number">数字串</param>
    /// <returns>基数中文</returns>
    public static String ToCardinalText(String number)
    {
        if (number.IsNullOrEmpty()) return number;

        // 超出 Int64 范围或过长时退化为逐位读，避免溢出
        var digits = number.TrimStart('0');
        if (digits.Length == 0) return "零";
        if (digits.Length > 15) return ToDigits(digits);

        return ToCardinal(Int64.Parse(digits));
    }

    /// <summary>整数按基数读</summary>
    /// <param name="value">非负整数</param>
    /// <returns>基数中文</returns>
    public static String ToCardinal(Int64 value)
    {
        if (value == 0) return "零";
        if (value < 0) return "负" + ToCardinal(-value);

        // 每四位一组，低位在前
        var groups = new List<Int32>();
        var rest = value;
        while (rest > 0)
        {
            groups.Add((Int32)(rest % 10000));
            rest /= 10000;
        }

        var sb = new StringBuilder();
        var needZero = false;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var g = groups[i];
            if (g == 0)
            {
                if (sb.Length > 0) needZero = true;
                continue;
            }

            // 高组存在、当前组不足千位，或中间空过一组时补“零”
            if (sb.Length > 0 && (needZero || g < 1000)) sb.Append('零');

            sb.Append(ReadGroup(g));
            sb.Append(_groups[i]);
            needZero = false;
        }

        return sb.ToString();
    }

    private static String DefaultRead(String digits)
    {
        // 前导零按编号读，如 007 读 零零七
        if (digits.Length > 1 && digits[0] == '0') return ToDigits(digits);

        // 七位以上一般是电话号码或编号，逐位读更自然
        return digits.Length >= 7 ? ToDigits(digits) : ToCardinalText(digits);
    }

    private static String ReadGroup(Int32 value)
    {
        var sb = new StringBuilder();
        var zero = false;
        for (var pos = 3; pos >= 0; pos--)
        {
            var div = pos switch { 3 => 1000, 2 => 100, 1 => 10, _ => 1 };
            var d = value / div % 10;
            if (d == 0)
            {
                if (sb.Length > 0) zero = true;
                continue;
            }

            if (zero)
            {
                sb.Append('零');
                zero = false;
            }

            sb.Append(_digits[d]);
            sb.Append(_units[pos]);
        }

        var text = sb.ToString();
        // 十位起头的数字习惯省掉“一”，如 15 读“十五”而非“一十五”
        if (text.StartsWith("一十")) text = text.Substring(1);

        return text;
    }
}
