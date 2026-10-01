using RtCli.Modules.Unit;

namespace RtCli.Tests;

/// <summary>
/// 事件板归属(VALID): 扩展加载/卸载、配置重载、面板指令是全局事件, 不属于任何实例。
/// 此前 BoardEventRecorder.Buffer 对空实例一律回退到"当前实例",
/// 导致这些全局事件被显示成当前实例的事件。
/// </summary>
public class BoardEventInstanceTests
{
    [Fact]
    public void 全局标识不会被回退到当前实例()
    {
        string id = BoardEventRecorder.ResolveEventInstanceId(BoardEventRecorder.GlobalInstanceId);
        Assert.Equal(BoardEventRecorder.GlobalInstanceId, id);
        Assert.NotEqual(Config.App.CurrentServer, id);
    }

    [Fact]
    public void 全局标识是星号且非空()
    {
        Assert.Equal("*", BoardEventRecorder.GlobalInstanceId);
    }

    [Fact]
    public void 空标识才回退到当前实例或system()
    {
        string id = BoardEventRecorder.ResolveEventInstanceId("");
        string expected = string.IsNullOrEmpty(Config.App.CurrentServer) ? "system" : Config.App.CurrentServer;
        Assert.Equal(expected, id);
        Assert.NotEqual(BoardEventRecorder.GlobalInstanceId, id);
    }

    [Fact]
    public void 具体实例标识原样保留()
    {
        Assert.Equal("survival", BoardEventRecorder.ResolveEventInstanceId("survival"));
    }
}
