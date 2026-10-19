using Xunit;

namespace XUnitTest;

/// <summary>会改动进程级状态的测试集合，与其他集合并行隔离</summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public class GlobalStateCollection
{
}
