<p align="center">
 <h2 align="center">RtCli</h2>
 <p align="center">一个基于C#控制台程序的Minecraft服务器后期的维护、监测、管理器</p>
</p>

<div align="center">

![Test Passed](https://img.shields.io/badge/Test-Passed-brightgreen)
![GitHub Repo Size](https://img.shields.io/github/repo-size/psoloi/RutCitrus)
![GitHub License](https://img.shields.io/github/license/psoloi/RutCitrus)
![GitHub Issues](https://img.shields.io/github/issues/psoloi/RutCitrus)
![RCMB Status](https://img.shields.io/github/actions/workflow/status/psoloi/RutCitrus/dotnet-console.yml?label=RCM%20Build)

</div>

## 使用说明
本项目是基于使用程序的人们会一定的MC服务端基础，需要您已经创建了服务端
打开MC服务端后再打开程序通过命令或配置文件即可
注意这个程序暂时不是开服器，只是您的服务端后期维护、检测和管理的工具
目前服务端命令发送有RCON和RUN模式，一种是通过RCON输入+Log读取的方式；
另一种是RUN获取和输入方式，如果想要精准控制建议使用RUN，如果RUN模式有问题则选择RCON模式

当您需要以任何方式使用或为此项目扩展时，只需要把原作者名称和项目原地址标出即可，谢谢你对作者的尊重！ [GitHub](https://github.com/psoloi/RutCitrus)

## 项目结构
该项目为RtCli作为服务器来管理Minecraft服务器，RutCitrusServer为控制RtCli的面板，其次可选择Rt为扩展或自制扩展

## 面板使用方法
给RtCli设置服务器端口再打开RutCitrusServer连接就可以了
如果您觉得面板不好用或不美观，欢迎任何作者制作

## RtCli 扩展
这些只是基本的定义，还有事件作为扩展的调用，详情见源代码Events.cs
如果还是无法理解可以试着看看Rt扩展示列
```csharp
    /// <summary>
    /// 插件接口定义
    /// </summary>
    public interface IExtension
    {
        /// <summary>
        /// 插件名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 插件版本
        /// </summary>
        string Version { get; }

        /// <summary>
        /// 插件描述
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 加载插件
        /// </summary>
        void Load();

        /// <summary>
        /// 运行插件
        /// </summary>
        void Run();

        /// <summary>
        /// 卸载插件
        /// </summary>
        void Unload();
    }

    /// <summary>
    /// 插件信息
    /// </summary>
    public class ExtensionInfo
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? AssemblyPath { get; set; }
        public string? TypeName { get; set; }
        public bool IsLoaded { get; set; }
        public DateTime LoadTime { get; set; }
    }
```
> [!WARNING]\
> 该程序目前不推荐在公网或危险的网络环境下使用！
> 程序的扩展系统还不是很稳定！

## 更新项目
1. 完善项目（对你没听错）
2. 实现MC服务器配置文件配置
3. 完善Rt扩展插件
4. 实现对主流MC服务端核心的支持
5. 实现对MC速度代理的支持
6. 完善Event以给扩展调用
7. 实现对MC服务端插件的配置
8. 实现群组服务器的管理
9. 扩展管理器的连接协议
    - TCP
    - UDP
10. 实现常见MC服务端报错的分析器
11. 更人性的配置（图形化）更安全的系统（连接固）更智能的管理（小龙虾？）
12. 数据库等日志保存


#### 建议及引用
非常推荐您能对项目指出点评，程序使用了人工智能制作，有错误十分感谢您可以提出甚至改进，如果有时间拜托能点个Stars吗？
![GitHub Stars](https://img.shields.io/github/stars/psoloi/RutCitrus?logo=github)

项目中作者主动引用了的一些库（无序排列）：Spectre.Console、Silk.NET.Core、TouchSocket、WPF-UI、Newtonsoft.Json、RestSharp、Serilog、YamlDotNet、MineStat、PacketDotNet、SharpPcap等
