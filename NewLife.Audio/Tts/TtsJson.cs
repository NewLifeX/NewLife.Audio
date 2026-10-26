using System.Text;

namespace NewLife.Audio.Tts;

/// <summary>语音合成请求体的 JSON 片段拼装</summary>
/// <remarks>
/// 公共云请求体字段固定且数量有限，手工拼装可以保持 NativeAOT 友好，不引入反射序列化。
/// 只提供字符串转义与带引号输出，嵌套结构由调用方自行组织。
/// </remarks>
internal static class TtsJson
{
    /// <summary>转义 JSON 字符串内容</summary>
    /// <param name="text">原始文本</param>
    /// <returns>可直接放入双引号之间的文本</returns>
    public static String Escape(String text)
    {
        if (text == null) return "";

        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((Int32)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>输出带引号的 JSON 字符串</summary>
    /// <param name="text">原始文本</param>
    /// <returns>形如 <c>"文本"</c> 的片段</returns>
    public static String Quote(String text) => "\"" + Escape(text) + "\"";

    /// <summary>追加字段（含前导逗号由调用方控制）</summary>
    /// <param name="sb">目标构建器</param>
    /// <param name="first">是否首个字段</param>
    /// <param name="name">字段名</param>
    /// <param name="value">字符串值</param>
    /// <returns>当前构建器</returns>
    public static StringBuilder AppendField(StringBuilder sb, ref Boolean first, String name, String value)
    {
        if (!first) sb.Append(',');
        first = false;
        return sb.Append('"').Append(name).Append("\":").Append(Quote(value));
    }

    /// <summary>追加数值字段</summary>
    /// <param name="sb">目标构建器</param>
    /// <param name="first">是否首个字段</param>
    /// <param name="name">字段名</param>
    /// <param name="value">数值</param>
    /// <returns>当前构建器</returns>
    public static StringBuilder AppendField(StringBuilder sb, ref Boolean first, String name, Int64 value)
    {
        if (!first) sb.Append(',');
        first = false;
        return sb.Append('"').Append(name).Append("\":").Append(value);
    }
}
