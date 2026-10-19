using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NewLife;
using NewLife.Audio.Codecs;
using NewLife.Data;

namespace MobileSmoke;

/// <summary>移动端冒烟：验证主库在 Android/iOS 上可加载并执行核心 API</summary>
public static class Smoke
{
    /// <summary>执行全部检查</summary>
    /// <returns>通过数、失败数与结果文件路径</returns>
    public static (Int32 Pass, Int32 Fail, String Path) Run()
    {
        var log = new List<String>();
        var pass = 0;
        var fail = 0;

        void Check(String name, Action action)
        {
            try
            {
                action();
                log.Add($"OK {name}");
                pass++;
            }
            catch (Exception ex)
            {
                log.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        var asm = typeof(NewLife.Audio.CodecRegistry).Assembly;
        Check("assembly:" + asm.GetName().Name, () =>
        {
            if (CountTypes(asm) == 0) throw new Exception("no exported types");
        });

        Check("G.711 A-law roundtrip", () =>
        {
            var pcm = new Byte[320];
            var codec = new G711ACodec();
            var encoded = codec.FromPcm(pcm, null);
            var decoded = codec.ToPcm(encoded.GetSpan(), null);
            if (encoded.Total != pcm.Length / 2 || decoded.Total != pcm.Length) throw new Exception($"length {encoded.Total}/{decoded.Total}");
        });

        Check("Localization 31 locales", () =>
        {
            var cultures = new[]
            {
                "ar", "bn", "cs", "da", "de", "el", "es", "fa", "fi", "fr", "he", "hi", "hu", "id", "it", "ja",
                "ko", "ms", "nb", "nl", "pl", "pt", "ro", "ru", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant",
            };

            var saved = Localization.Culture;
            try
            {
                foreach (var name in cultures)
                {
                    CultureInfo culture;
                    try { culture = CultureInfo.GetCultureInfo(name); }
                    catch (CultureNotFoundException) { continue; }

                    Localization.Culture = culture;
                    if (Localization.Translate("Default device") == "Default device") throw new Exception($"missing translation: {name}");
                }

                Localization.Culture = CultureInfo.InvariantCulture;
                if (Localization.Translate("Default device") != "Default device") throw new Exception("english fallback failed");
            }
            finally
            {
                Localization.Culture = saved;
            }
        });

        var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "smoke.json");
        var json = JsonSerializer.Serialize(new
        {
            pass,
            fail,
            arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            log,
        });
        File.WriteAllText(file, json);
        return (pass, fail, file);
    }

    private static Int32 CountTypes(Assembly asm)
    {
        try
        {
            return asm.GetExportedTypes().Length;
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Count(e => e != null);
        }
    }
}
