using System;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest.Streaming;

public class HttpAudioStreamSourceTests
{
    [Fact(DisplayName = "HttpAudioStreamSource创建Url正确")]
    public void Constructor_SetsUrl()
    {
        var source = new HttpAudioStreamSource("http://localhost/test");
        Assert.Equal("http://localhost/test", source.Url);
    }

    [Fact(DisplayName = "HttpAudioStreamSource构造传null抛异常")]
    public void Constructor_NullUrl_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpAudioStreamSource(null));
    }

    [Fact(DisplayName = "HttpAudioStreamSource ContentType初始为null")]
    public void ContentType_InitiallyNull()
    {
        var source = new HttpAudioStreamSource("http://localhost/test");
        Assert.Null(source.ContentType);
    }

    [Fact(DisplayName = "HttpAudioStreamSource StreamName初始为null")]
    public void StreamName_InitiallyNull()
    {
        var source = new HttpAudioStreamSource("http://localhost/test");
        Assert.Null(source.StreamName);
    }

    [Fact(DisplayName = "HttpAudioStreamSource Dispose不抛异常")]
    public void Dispose_DoesNotThrow()
    {
        var source = new HttpAudioStreamSource("http://localhost/test");
        source.Dispose();
    }
}
