using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>ASIO 低延迟驱动（Steinberg ASIO 协议）</summary>
/// <remarks>
/// ASIO 需要加载厂商提供的 ASIO 驱动 COM 组件并调用 <c>IASIO::createBuffers</c>/<c>start</c> 等原生接口，
/// 本库尚未实现该后端。<br/>
/// 为避免"选择 ASIO 后无声无错"的静默失败，当前行为是：<see cref="EnumerateDevices"/> 返回空列表，
/// <see cref="CreatePlayer"/>/<see cref="CreateRecorder"/> 明确抛出 <see cref="NotSupportedException"/>。<br/>
/// Windows 上需要低延迟输出时请使用 WASAPI（<see cref="WasapiDriver"/>）或 WaveOut。
/// </remarks>
public class AsioDriver : IAudioDriver
{
    /// <summary>是否已实现 ASIO 原生后端</summary>
    public static Boolean IsSupported => false;

    /// <summary>驱动名称</summary>
    public String Name => "ASIO (Steinberg)";

    /// <summary>枚举 ASIO 设备</summary>
    /// <returns>当前后端未实现，始终返回空列表</returns>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => [];

    /// <summary>获取默认输出设备</summary>
    /// <returns>当前后端未实现，始终返回 null</returns>
    public AudioDeviceInfo GetDefaultOutputDevice() => null;

    /// <summary>获取默认输入设备</summary>
    /// <returns>当前后端未实现，始终返回 null</returns>
    public AudioDeviceInfo GetDefaultInputDevice() => null;

    /// <summary>创建 ASIO 播放器</summary>
    /// <returns>不会返回</returns>
    /// <exception cref="NotSupportedException">ASIO 后端尚未实现</exception>
    public IAudioPlayer CreatePlayer() => throw new NotSupportedException(L.T("The ASIO backend is not implemented."));

    /// <summary>创建 ASIO 录制器</summary>
    /// <returns>不会返回</returns>
    /// <exception cref="NotSupportedException">ASIO 后端尚未实现</exception>
    public IAudioRecorder CreateRecorder() => throw new NotSupportedException(L.T("The ASIO backend is not implemented."));
}
