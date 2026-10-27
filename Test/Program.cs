using System;

namespace Test;

/// <summary>手工验证入口：本机录音、多格式导出与逐个回放</summary>
class Program
{
    /// <summary>执行命令并返回进程退出码</summary>
    /// <param name="args">命令行参数，省略时执行 all</param>
    /// <returns>0 表示全部通过</returns>
    static Int32 Main(String[] args)
    {
        try
        {
            return AudioLab.Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}