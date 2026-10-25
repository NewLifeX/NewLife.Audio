using System.Text;
using System.Text.RegularExpressions;

namespace NewLife.Audio.Tts;

/// <summary>合成前的文本规范化</summary>
/// <remarks>
/// 中文离线声学模型（尤其带自带词典的模型）有三个高频坑，实测都会直接影响听感：
/// <list type="number">
/// <item>字母与单词读不出：词典模型遇到 OOV 直接忽略，需整词替换或逐字母读名。</item>
/// <item>空格被当成发音位：会在词与词之间插入莫名其妙的音，必须去掉空白。</item>
/// <item>阿拉伯数字读错或跳过：需转中文读法，见 <see cref="ChineseNumber"/>。</item>
/// </list>
/// 自带英文词典的模型（能力位含 <see cref="TtsCapability.MultiLanguage"/>）应关闭英文与数字替换，交给模型自己处理。
/// </remarks>
/// <example>
/// <code>
/// var text = new TtsText();
/// text.Normalize("设备型号 A2 温度 35 度");   // 设备型号诶二温度三十五度
/// </code>
/// </example>
public class TtsText
{
    private static readonly Regex _letterWord = new("[A-Za-z][A-Za-z']*");
    private static readonly Regex _year = new(@"(?<!\d)(\d{4})(?=\s*年)");
    private static readonly Regex _number = new(@"(?<!\d)(\d+(?:\.\d+)?)(?!\d)");
    private static readonly Regex _space = new(@"\s+");
    private static readonly Regex _pause = new("[，、；：,;:]");

    private static readonly String[] _letterNames =
    [
        "诶", "必", "西", "迪", "伊", "艾弗", "吉", "艾尺", "爱", "杰", "开", "艾勒", "艾姆",
        "恩", "欧", "屁", "扣", "阿尔", "艾斯", "提", "优", "维", "达不溜", "艾克斯", "歪", "贼德",
    ];

    private readonly Dictionary<String, String> _words = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全局默认实例</summary>
    public static TtsText Default { get; } = new();

    /// <summary>是否把字母与英文单词转换成中文读名</summary>
    public Boolean ReadEnglish { get; set; } = true;

    /// <summary>是否把阿拉伯数字转换成中文读法</summary>
    public Boolean ReadNumbers { get; set; } = true;

    /// <summary>是否去掉全部空白字符</summary>
    public Boolean RemoveWhitespace { get; set; } = true;

    /// <summary>自定义词条。整词替换，长词优先</summary>
    public IDictionary<String, String> Words => _words;

    /// <summary>添加词条</summary>
    /// <param name="word">原词，大小写不敏感</param>
    /// <param name="reading">读法</param>
    /// <returns>当前实例，便于链式调用</returns>
    public TtsText AddWord(String word, String reading)
    {
        if (!word.IsNullOrEmpty() && reading != null) _words[word] = reading;
        return this;
    }

    /// <summary>规范化文本</summary>
    /// <param name="text">原始文本</param>
    /// <param name="mode">数字读法</param>
    /// <returns>可直接送合成的文本</returns>
    public String Normalize(String text, TtsNumberMode mode = TtsNumberMode.Auto)
    {
        if (text.IsNullOrEmpty()) return String.Empty;

        // 全角数字与符号先归一
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= '０' && ch <= '９') sb.Append((Char)('0' + (ch - '０')));
            else if (ch == '\r' || ch == '\n' || ch == '\t') sb.Append(' ');
            else sb.Append(ch);
        }
        var s = sb.ToString();

        foreach (var item in _words.OrderByDescending(e => e.Key.Length))
            s = Regex.Replace(s, Regex.Escape(item.Key), item.Value.Replace("$", "$$"), RegexOptions.IgnoreCase);

        if (ReadEnglish) s = _letterWord.Replace(s, m => Spell(m.Value));
        if (ReadNumbers)
        {
            s = _year.Replace(s, m => ChineseNumber.ToDigits(m.Groups[1].Value));
            s = _number.Replace(s, m => ChineseNumber.ToChinese(m.Groups[1].Value, mode));
        }

        if (RemoveWhitespace) s = _space.Replace(s, "");

        return s.Trim();
    }

    /// <summary>把逗号类停顿标点统一成句号</summary>
    /// <remarks>只认句末标点断句的引擎（典型如 piper）需要这样处理，否则中文逗号处不会产生停顿。</remarks>
    /// <param name="text">文本</param>
    /// <returns>统一标点后的文本</returns>
    public static String UnifyPunctuation(String text) => text.IsNullOrEmpty() ? text : _pause.Replace(text, "。");

    /// <summary>按标点切分短句</summary>
    /// <param name="text">文本</param>
    /// <returns>短句列表，不含空项</returns>
    public static IList<String> SplitClauses(String text)
    {
        var list = new List<String>();
        if (text.IsNullOrEmpty()) return list;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '。' && ch != '！' && ch != '？' && ch != '!' && ch != '?' && ch != ';') continue;

            var part = text.Substring(start, i - start + 1).Trim();
            if (part.Length > 0) list.Add(part);
            start = i + 1;
        }

        if (start < text.Length)
        {
            var tail = text.Substring(start).Trim();
            if (tail.Length > 0) list.Add(tail);
        }

        return list;
    }

    private static String Spell(String word)
    {
        // 单字母直接读名；多字母词没有词典时逐字母读，读成“欸二”比整词被吞掉更可用
        var sb = new StringBuilder(word.Length * 2);
        foreach (var ch in word)
        {
            if (ch >= 'a' && ch <= 'z') sb.Append(_letterNames[ch - 'a']);
            else if (ch >= 'A' && ch <= 'Z') sb.Append(_letterNames[ch - 'A']);
            else if (ch == '\'') continue;
            else sb.Append(ch);
        }

        return sb.ToString();
    }
}
