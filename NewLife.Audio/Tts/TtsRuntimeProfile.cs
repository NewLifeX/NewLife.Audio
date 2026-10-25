namespace NewLife.Audio.Tts;

/// <summary>运行环境画像</summary>
/// <remarks>
/// 同一套 TTS 代码要同时跑在 RK3568 这种 1GB 内存的嵌入式板子、开发机和服务器上，
/// 差别不在接口而在“能用什么引擎”：小内存设备跑不动大模型，断网设备不能依赖在线服务，
/// 合规要求高的场景可能根本不允许把文本发到第三方。
/// <para>本类把这些约束收敛成几个开关，交给 <see cref="TtsRouter"/> 做选路，业务代码不必到处写 if。</para>
/// </remarks>
/// <example>
/// <code>
/// // 嵌入式：限制内存、默认只用离线
/// var profile = TtsRuntimeProfile.Embedded;
///
/// // 或按真实机器探测
/// var auto = TtsRuntimeProfile.Detect();
/// </code>
/// </example>
public class TtsRuntimeProfile
{
    /// <summary>运行目标</summary>
    public TtsTarget Target { get; set; } = TtsTarget.Desktop;

    /// <summary>可用于合成的内存上限（字节）。0 表示不限制</summary>
    /// <remarks>对齐的是引擎加载后的额外内存增量，不是进程 RSS 总量。</remarks>
    public Int64 MemoryLimit { get; set; }

    /// <summary>当前是否有可用网络外网</summary>
    public Boolean NetworkAvailable { get; set; } = true;

    /// <summary>是否允许把文本发往在线服务</summary>
    /// <remarks>合规与隐私开关。为 false 时任何在线引擎都不会被选中，与网络是否可用无关。</remarks>
    public Boolean AllowOnline { get; set; } = true;

    /// <summary>是否允许在并发场景下共用同一个引擎实例</summary>
    public Boolean AllowSharedEngine { get; set; } = true;

    /// <summary>桌面预设：内存 2GB、允许在线</summary>
    public static TtsRuntimeProfile Desktop => new()
    {
        Target = TtsTarget.Desktop,
        MemoryLimit = 2L * 1024 * 1024 * 1024,
        AllowOnline = true,
    };

    /// <summary>嵌入式预设：内存 384MB、只用离线</summary>
    /// <remarks>384MB 是实测安全线：22.05kHz 中文流式模型加载后增量约 214MB，留出余量且挡住 500MB 级大模型。</remarks>
    public static TtsRuntimeProfile Embedded => new()
    {
        Target = TtsTarget.Embedded,
        MemoryLimit = 384L * 1024 * 1024,
        AllowOnline = false,
    };

    /// <summary>服务器预设：不限制内存、允许在线</summary>
    public static TtsRuntimeProfile Server => new()
    {
        Target = TtsTarget.Server,
        MemoryLimit = 0,
        AllowOnline = true,
    };

    /// <summary>判断引擎在指定场景下是否可用</summary>
    /// <param name="engine">引擎</param>
    /// <param name="scenario">合成场景</param>
    /// <returns>是否可用</returns>
    public Boolean Allows(ITtsEngine engine, TtsScenario scenario)
    {
        if (engine == null || !engine.IsAvailable) return false;

        // 在线引擎受合规开关与网络双重约束
        if ((engine.Capabilities & TtsCapability.Online) != 0 && (!AllowOnline || !NetworkAvailable)) return false;

        // 离线引擎永远可用，但小内存设备要挡住跑不起来的大模型
        if (MemoryLimit > 0 && engine.RequiredMemoryBytes > MemoryLimit) return false;

        return true;
    }

    /// <summary>判断引擎能否满足流式要求</summary>
    /// <param name="engine">引擎</param>
    /// <returns>是否支持分块输出</returns>
    public static Boolean SupportsStreaming(ITtsEngine engine) => engine != null && (engine.Capabilities & TtsCapability.Streaming) != 0;

    /// <summary>读取系统可用内存（字节）。无法获取时返回 0</summary>
    /// <remarks>只实现了 Linux 的 /proc/meminfo，便于嵌入式现场探测；其他平台返回 0 表示未知。</remarks>
    /// <returns>可用内存字节数</returns>
    public static Int64 ReadAvailableMemory()
    {
        try
        {
            if (!File.Exists("/proc/meminfo")) return 0;

            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:")) continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return 0;

                return Int64.TryParse(parts[1], out var kb) ? kb * 1024 : 0;
            }
        }
        catch
        {
            // 读取失败按未知处理，不影响主流程
        }

        return 0;
    }

    /// <summary>按当前机器探测画像</summary>
    /// <remarks>
    /// 可用内存低于 1GB 判为嵌入式，核心数不少于 8 且内存不少于 8GB 判为服务器，其余为桌面。
    /// 探测不到内存时按处理器数退化为桌面/嵌入式两档。
    /// </remarks>
    /// <returns>运行画像</returns>
    public static TtsRuntimeProfile Detect()
    {
        var memory = ReadAvailableMemory();
        var cores = Environment.ProcessorCount;

        var profile = new TtsRuntimeProfile
        {
            NetworkAvailable = true,
            AllowOnline = true,
        };

        if (memory > 0 && memory < 1024L * 1024 * 1024)
        {
            profile.Target = TtsTarget.Embedded;
            // 只用一半可用内存，给播放缓冲与业务留余量
            profile.MemoryLimit = memory / 2;
            profile.AllowOnline = false;
        }
        else if (memory >= 8L * 1024 * 1024 * 1024 && cores >= 8)
        {
            profile.Target = TtsTarget.Server;
            profile.MemoryLimit = 0;
        }
        else
        {
            profile.Target = TtsTarget.Desktop;
            profile.MemoryLimit = memory > 0 ? memory / 2 : 2L * 1024 * 1024 * 1024;
        }

        return profile;
    }

    /// <summary>返回画像描述</summary>
    /// <returns>目标、内存与网络约束</returns>
    public override String ToString()
    {
        var limit = MemoryLimit > 0 ? $"{MemoryLimit / 1024 / 1024}MB" : "unlimited";
        return $"{Target} memory<={limit} online={(AllowOnline ? "allowed" : "denied")} network={(NetworkAvailable ? "up" : "down")}";
    }
}
