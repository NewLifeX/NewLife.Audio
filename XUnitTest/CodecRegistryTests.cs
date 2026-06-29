using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest;

public class CodecRegistryTests
{
    private readonly CodecRegistry _registry = new();

    [Fact(DisplayName = "注册表Register后GetCodec返回同一实例")]
    public void Register_ThenGetCodec_ReturnsSameInstance()
    {
        var codec = new G711ACodec();
        _registry.Register(AVTypes.G711A, codec);

        var result = _registry.GetCodec(AVTypes.G711A);
        Assert.Same(codec, result);
    }

    [Fact(DisplayName = "注册表RegisterFactory每次GetCodec返回新实例")]
    public void RegisterFactory_ThenGetCodec_ReturnsNewInstance()
    {
        _registry.RegisterFactory(AVTypes.G722, () => new G722Codec());

        var codec1 = _registry.GetCodec(AVTypes.G722);
        var codec2 = _registry.GetCodec(AVTypes.G722);

        Assert.NotSame(codec1, codec2);
    }

    [Fact(DisplayName = "注册表未注册类型GetCodec抛异常")]
    public void GetCodec_UnregisteredType_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => _registry.GetCodec(AVTypes.AAC));
    }

    [Fact(DisplayName = "注册表IsSupported正确反映注册状态")]
    public void IsSupported_ReflectsRegistration()
    {
        Assert.False(_registry.IsSupported(AVTypes.G711A));

        _registry.Register(AVTypes.G711A, new G711ACodec());
        Assert.True(_registry.IsSupported(AVTypes.G711A));
        Assert.False(_registry.IsSupported(AVTypes.MP3));
    }

    [Fact(DisplayName = "注册表RegisteredTypes返回所有已注册类型")]
    public void RegisteredTypes_ReturnsAllRegistered()
    {
        _registry.Register(AVTypes.G711A, new G711ACodec());
        _registry.Register(AVTypes.G711U, new G711UCodec());
        _registry.RegisterFactory(AVTypes.G722, () => new G722Codec());

        var types = _registry.RegisteredTypes;
        Assert.Contains(AVTypes.G711A, types);
        Assert.Contains(AVTypes.G711U, types);
        Assert.Contains(AVTypes.G722, types);
        Assert.Equal(3, types.Count);
    }

    [Fact(DisplayName = "注册表Register传null抛异常")]
    public void Register_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Register(AVTypes.G711A, null));
        Assert.Throws<ArgumentNullException>(() => _registry.RegisterFactory(AVTypes.G711A, null));
    }
}
