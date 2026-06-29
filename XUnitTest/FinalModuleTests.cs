using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest;

public class FinalModuleTests
{
    [Fact(DisplayName = "WASAPI后端创建成功")]
    public void WasapiBackend_CreatesInstances()
    {
        var backend = AudioBackendFactory.Create("wasapi");
        Assert.NotNull(backend);
        Assert.Contains("WASAPI", backend.Name);

        var player = backend.CreatePlayer();
        Assert.NotNull(player);
        player.Dispose();

        var recorder = backend.CreateRecorder();
        Assert.NotNull(recorder);
        recorder.Dispose();
    }

    [Fact(DisplayName = "ASIO后端创建成功")]
    public void AsioBackend_CreatesInstances()
    {
        var backend = AudioBackendFactory.Create("asio");
        Assert.NotNull(backend);
        Assert.Contains("ASIO", backend.Name);
    }

    [Fact(DisplayName = "环路录制器初始化和启停")]
    public void LoopbackRecorder_Init_StartStop()
    {
        var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);
        Assert.False(recorder.IsRecording);

        recorder.StartRecording();
        Assert.True(recorder.IsRecording);

        recorder.StopRecording();
        Assert.False(recorder.IsRecording);
    }

    [Fact(DisplayName = "FLAC容器解析非FLAC文件抛异常")]
    public void FlacContainer_InvalidData_Throws()
    {
        var ms = new MemoryStream(new Byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        Assert.Throws<InvalidDataException>(() => new FlacContainerReader(ms));
    }

    [Fact(DisplayName = "FLAC容器解析有效fLaC标记不抛异常")]
    public void FlacContainer_ValidMarker_ParsesInfo()
    {
        var ms = new MemoryStream();
        ms.Write(new Byte[] { (Byte)'f', (Byte)'L', (Byte)'a', (Byte)'C' }, 0, 4);
        ms.WriteByte(0x80);
        ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(34);

        var streamInfo = new Byte[34];
        // 填充最小有效数据
        ms.Write(streamInfo, 0, 34);
        ms.Seek(0, SeekOrigin.Begin);

        var reader = new FlacContainerReader(ms);
        Assert.NotNull(reader.Format);
        Assert.True(reader.Format.SampleRate >= 0);
    }
}
