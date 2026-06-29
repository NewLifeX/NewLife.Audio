using NewLife.Audio.Containers;
using Xunit;

namespace XUnitTest.Containers;

public class AudioMetadataTests
{
    [Fact(DisplayName = "AudioMetadata Title属性读写正确")]
    public void Title_ReadWrite()
    {
        var meta = new AudioMetadata();
        Assert.Null(meta.Title);

        meta.Title = "测试标题";
        Assert.Equal("测试标题", meta.Title);
    }

    [Fact(DisplayName = "AudioMetadata Artist属性读写正确")]
    public void Artist_ReadWrite()
    {
        var meta = new AudioMetadata();
        meta.Artist = "测试艺术家";
        Assert.Equal("测试艺术家", meta.Artist);
    }

    [Fact(DisplayName = "AudioMetadata Album属性读写正确")]
    public void Album_ReadWrite()
    {
        var meta = new AudioMetadata();
        meta.Album = "测试专辑";
        Assert.Equal("测试专辑", meta.Album);
    }

    [Fact(DisplayName = "AudioMetadata年份读写正确")]
    public void Year_ReadWrite()
    {
        var meta = new AudioMetadata();
        meta.Year = 2026;
        Assert.Equal(2026, meta.Year);
    }

    [Fact(DisplayName = "AudioMetadata流派读写正确")]
    public void Genre_ReadWrite()
    {
        var meta = new AudioMetadata();
        meta.Genre = "Rock";
        Assert.Equal("Rock", meta.Genre);
    }

    [Fact(DisplayName = "AudioMetadata轨道号读写正确")]
    public void TrackNumber_ReadWrite()
    {
        var meta = new AudioMetadata { TrackNumber = 5 };
        Assert.Equal(5, meta.TrackNumber);
    }

    [Fact(DisplayName = "AudioMetadata默认值验证")]
    public void Defaults_AreCorrect()
    {
        var meta = new AudioMetadata();
        Assert.Null(meta.Title);
        Assert.Null(meta.Artist);
        Assert.Null(meta.Album);
        Assert.Equal(0, meta.TrackNumber);
    }
}
