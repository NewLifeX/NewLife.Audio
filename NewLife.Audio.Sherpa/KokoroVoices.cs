namespace NewLife.Audio.Tts.Sherpa;

/// <summary>Kokoro v1.1-zh 的音色编号与名称对照</summary>
/// <remarks>
/// 该模型 103 个音色按语言分段：0~1 美式英文、2 英式英文、3~57 中文女声、58~102 中文男声。
/// 用英文音色读中文会像外国人念中文，所以中文场景必须选 sid≥3。
/// </remarks>
public static class KokoroVoices
{
    private static readonly String[] _names = (
        "af_maple,af_sol,bf_vale," +
        "zf_001,zf_002,zf_003,zf_004,zf_005,zf_006,zf_007,zf_008,zf_017,zf_018,zf_019,zf_021,zf_022," +
        "zf_023,zf_024,zf_026,zf_027,zf_028,zf_032,zf_036,zf_038,zf_039,zf_040,zf_042,zf_043,zf_044," +
        "zf_046,zf_047,zf_048,zf_049,zf_051,zf_059,zf_060,zf_067,zf_070,zf_071,zf_072,zf_073,zf_074," +
        "zf_075,zf_076,zf_077,zf_078,zf_079,zf_083,zf_084,zf_085,zf_086,zf_087,zf_088,zf_090,zf_092," +
        "zf_093,zf_094,zf_099," +
        "zm_009,zm_010,zm_011,zm_012,zm_013,zm_014,zm_015,zm_016,zm_020,zm_025,zm_029,zm_030,zm_031," +
        "zm_033,zm_034,zm_035,zm_037,zm_041,zm_045,zm_050,zm_052,zm_053,zm_054,zm_055,zm_056,zm_057," +
        "zm_058,zm_061,zm_062,zm_063,zm_064,zm_065,zm_066,zm_068,zm_069,zm_080,zm_081,zm_082,zm_089," +
        "zm_091,zm_095,zm_096,zm_097,zm_098,zm_100"
    ).Split(',');

    /// <summary>音色数量</summary>
    public static Int32 Count => _names.Length;

    /// <summary>取音色名</summary>
    /// <param name="sid">音色编号</param>
    /// <returns>形如 zf_001；越界时返回 sidN</returns>
    public static String Name(Int32 sid) => sid >= 0 && sid < _names.Length ? _names[sid] : "sid" + sid;

    /// <summary>是否英文音色（读中文会有外国口音）</summary>
    /// <param name="sid">音色编号</param>
    /// <returns>0~2 为英文音色</returns>
    public static Boolean IsEnglish(Int32 sid) => sid is >= 0 and <= 2;

    /// <summary>枚举全部音色</summary>
    /// <returns>音色列表，含名称、区域与性别</returns>
    public static IReadOnlyList<TtsVoice> ToVoices()
    {
        var list = new List<TtsVoice>(_names.Length);
        for (var i = 0; i < _names.Length; i++)
        {
            list.Add(new TtsVoice
            {
                Name = _names[i],
                Culture = Culture(_names[i]),
                Gender = _names[i].StartsWith("zm_", StringComparison.Ordinal) ? "Male" : "Female",
            });
        }

        return list;
    }

    private static String Culture(String name)
    {
        if (name.StartsWith("af_", StringComparison.Ordinal)) return "en-US";
        if (name.StartsWith("bf_", StringComparison.Ordinal)) return "en-GB";

        return "zh-CN";
    }
}
