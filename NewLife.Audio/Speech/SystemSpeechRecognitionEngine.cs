using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>Windows 系统语音识别适配（System.Speech，反射调用）</summary>
/// <remarks>
/// 通过反射调用 <c>System.Speech.Recognition.SpeechRecognitionEngine</c>，因此核心库不引入桌面专属依赖：
/// 目标框架缺少该程序集时 <see cref="IsAvailable"/> 为 false，识别请求返回空文本而不抛异常。<br/>
/// 识别使用听写语法（DictationGrammar），把整段 PCM 包成内存 WAV 后交给引擎离线识别；
/// 端点检测与部分结果由 <see cref="SegmentedSpeechRecognizer"/> 统一处理。
/// </remarks>
/// <example>
/// <code>
/// var engine = new SystemSpeechRecognitionEngine();
/// if (engine.IsAvailable) Console.WriteLine(engine.Transcribe(pcm, format, new SpeechRecognitionOptions { Language = "zh-CN" }).Text);
/// </code>
/// </example>
public class SystemSpeechRecognitionEngine : ISpeechRecognitionEngine
{
    private readonly Type _engineType;
    private readonly Type _grammarType;

    /// <summary>引擎是否可用（当前框架是否带 System.Speech）</summary>
    public Boolean IsAvailable => _engineType != null && _grammarType != null;

    /// <summary>构造引擎</summary>
    public SystemSpeechRecognitionEngine()
    {
        _engineType = Type.GetType("System.Speech.Recognition.SpeechRecognitionEngine, System.Speech", false);
        _grammarType = Type.GetType("System.Speech.Recognition.DictationGrammar, System.Speech", false);
    }

    /// <summary>识别一段语音</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <param name="options">识别参数</param>
    /// <returns>识别文本与置信度；引擎不可用或未识别到内容时文本为空</returns>
    public SpeechRecognitionResult Transcribe(Byte[] pcm, AudioFormat format, SpeechRecognitionOptions options)
    {
        var result = new SpeechRecognitionResult();
        if (!IsAvailable || pcm == null || pcm.Length < 2 || format == null) return result;

        try
        {
            var engine = CreateEngine(options?.Language, format);
            if (engine == null) return result;

            try
            {
                var grammar = Activator.CreateInstance(_grammarType);
                if (grammar != null) FindMethod(_engineType, "LoadGrammar")?.Invoke(engine, [grammar]);

                using var wave = new MemoryStream();
                using (var writer = new WaveFileWriter(wave, format))
                {
                    writer.WriteFrame(pcm);
                    writer.Flush();
                }
                wave.Position = 0;

                FindMethod(_engineType, "SetInputToWaveStream")?.Invoke(engine, [wave]);

                var recognize = FindMethod(_engineType, "Recognize");
                var recognized = recognize?.Invoke(engine, null);
                if (recognized == null) return result;

                var type = recognized.GetType();
                result.Text = type.GetProperty("Text")?.GetValue(recognized) as String;
                if (type.GetProperty("Confidence")?.GetValue(recognized) is Single confidence) result.Confidence = confidence;
            }
            finally
            {
                (engine as IDisposable)?.Dispose();
            }
        }
        catch
        {
            // 引擎内部异常（无麦克风、语言包缺失等）不影响调用方，返回空结果
            result.Text = null;
        }

        return result;
    }

    #region 辅助
    private Object CreateEngine(String language, AudioFormat format)
    {
        var culture = ResolveCulture(language, format);
        try
        {
            var ctor = _engineType.GetConstructor([typeof(CultureInfo)]);
            if (ctor != null) return ctor.Invoke([culture]);
        }
        catch
        {
            // 指定的语言未安装时回退默认构造
        }

        try
        {
            return Activator.CreateInstance(_engineType);
        }
        catch
        {
            return null;
        }
    }

    private static CultureInfo ResolveCulture(String language, AudioFormat format)
    {
        if (!language.IsNullOrEmpty())
        {
            try
            {
                return CultureInfo.GetCultureInfo(language);
            }
            catch
            {
                // 无效语言标签时回退当前区域
            }
        }

        return CultureInfo.CurrentCulture;
    }

    private static MethodInfo FindMethod(Type type, String name)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name == name) return method;
        }

        return null;
    }
    #endregion
}
