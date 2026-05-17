<p align="center">
 <h2 align="center">RtCli</h2>
 <p align="center">一个基于C#控制台程序的Minecraft服务器后期的自动化维护、监测、管理器</p>
</p>

<div align="center">

![Test Passed](https://img.shields.io/badge/Test-Passed-brightgreen)
![GitHub Repo Size](https://img.shields.io/github/repo-size/psoloi/RutCitrus)
![GitHub License](https://img.shields.io/github/license/psoloi/RutCitrus)
![GitHub Issues](https://img.shields.io/github/issues/psoloi/RutCitrus)
![RCB Status](https://img.shields.io/github/actions/workflow/status/psoloi/RutCitrus/dotnet-console.yml?label=RtCli%20Build)

</div>

## 使用说明
本项目是基于使用程序的人们会一定的MC服务端基础，最好需要您已经创建了服务端
如果实在不会可以通过`.guide`命令来快速入门

目前程序测试了Paper服务端的以下MC版本：1.8.8、1.21.11（完善程序后将测试更多）

## 目前功能

 - 支持MC服务端简单的创建
 - MC控制台错误分析（包括基础型和给AI分析|使用AI分析需要自己准备API和Key）
 - 可自定义控制台错误分析正表达式匹配和执行脚本
 - 可自定义大多数正表达式配置内容和配置项
 - 支持自定义CSharp、Python脚本、完整扩展（DLL）
 - 支持自定义任务计划（可自定义配置触发方式或条件、执行预制模块或脚本等等）
 - 支持服务器自动备份、服务器问题插件筛选、服务器文件修改筛选
 - 支持多个服务器的控制和切换（支持服务器间功能的相互隔离）
 - [Rt扩展]支持Antibot和数据包限制

## 项目结构
> [!WARNING]\
> 注意请修改程序的开发者千万不要上传自己的AI API key要详细检查

该项目为RtCli作为服务器来管理Minecraft服务器，RtPanel为辅助控制RtCli的面板，其次可选择Rt为扩展或自制扩展
RtPanel的功能主要是辅助RtCli并不推荐开放到公网当作面板使用，如果是在云服务器上建议使用宝塔之类的面板
给RtCli设置服务器端口再打开RtPanel连接就可以了
如果您觉得面板不好用或不美观，欢迎任何作者制作
Rt的主要作用是安全性方面的扩展，目前不推荐使用，如果需要其中功能可自行编译

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
1. 完善项目（对你没听错 - 进度大约75%）
2. 实现MC服务器配置文件配置
3. 完善Rt扩展插件
4. 实现对主流MC服务端核心的支持
    - 新版本含config文件夹的
    - 旧版本不含的
5. 实现对MC速度代理的支持
6. 完善Event以给扩展调用
7. 实现对MC服务端插件的配置
8. 实现群组服务器的管理
9. 自动化管理
10. 强化连接安全
11. 数据库等日志保存
12. 支持网页面板简单配置RtCli
13. 多语言完善

#### 最后及引用
当您需要以任何方式使用或为此项目修改时，只需要把项目中原作者名称和项目原地址保留即可，谢谢你对作者的尊重！ [GitHub](https://github.com/psoloi/RutCitrus)
非常推荐您能对项目指出点评，我只是高中生没有多少时间所以程序一半使用了人工智能来制作，我的设备可能有问题无法运行容器程序的兼容性没测试过，有错误十分感谢您可以提出甚至改进，如果有时间拜托能点个Stars吗？
![GitHub Stars](https://img.shields.io/github/stars/psoloi/RutCitrus?logo=github)

该项目引用了大量库，我十分尊重这些作者的努力，并且没有对库的代码进行修改，具体可见项目引用输入`rt about`命令查看主动引用的库