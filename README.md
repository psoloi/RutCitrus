| [**中文**](README.md) | [English](README_EN.md) |
| :---: | :---: |

<p align="center">
 <h2 align="center">RtCli</h2>
 <p align="center">一个基于人工智能的Minecraft服务器后期的自动化维护、监测、管理器</p>
</p>

<div align="center">

![Test Passed](https://img.shields.io/badge/Test-Passed-brightgreen)
![GitHub Repo Size](https://img.shields.io/github/repo-size/psoloi/RutCitrus)
![GitHub License](https://img.shields.io/github/license/psoloi/RutCitrus)
![GitHub Issues](https://img.shields.io/github/issues/psoloi/RutCitrus)
![RCB Status](https://img.shields.io/github/actions/workflow/status/psoloi/RutCitrus/dotnet-console.yml?label=RtCli%20Build)

</div>

## ✨ 使用说明
本项目是基于使用程序的人们会一定的MC服务端基础，最好需要您已经创建了服务端。
如果实在不会可以通过`.guide`命令来快速创建后完善配置文件便可使用`.ai`功能。
很显然，您需要选择一个很好的AI模型，如果AI模型不给力很可能会带来一些无法意料的问题。
其次便是您一定要会如何正确编辑配置文件，有BUG则在Github上报。

## 🧪 测试参考
测试仅供参考，毕竟大多数功能自定义很强，不支持也能改配置文件进行支持。
不过目前对模组端支持还在改善，代理端群组推荐Velocity。
目前程序测试了以下MC服务端：
 - [x] Paper
   > 1.8.8、1.12.2、1.16.5、1.20.1、1.21.11、26.2
 - [x] Vanilla (原版)
   > 1.8.8、1.12.2、1.16.5、1.18.2、1.19.2、1.20.1、1.20.4、1.20.6、1.21、1.21.11、26.1、26.2、26.3
 - [ ] Folia
   > 待测
 - [ ] Leaf
   > 待测
 - [ ] Velocity (代理)
   > 待测

## 🔨 目前功能

 - 支持服务器无人自动化维护和监测、支持AI记忆和AI技能规则等
 - 支持全栈日志可查看面板发生的大多数事件，包括AI工具调用
 - 支持MC服务端的创建和导入、服务端Jar下载、服务端群组实例管理
 - 支持MC服务端的文件管理、上传和删除、插件的更新
 - 支持MC服务端配置，高版本的服务端游戏规则修改和开启中服务器配置修改
 - 支持玩家管理封禁等操作、支持玩家事件的查询
 - MC控制台错误分析（包括基础型和给AI分析|使用AI分析需要自己准备API和Key）
 - 可自定义控制台错误分析正表达式匹配和执行脚本（如内嵌的mclog脚本）
 - 可自定义大多数正表达式配置内容和配置项
 - 支持自定义CSharp、Python脚本、完整扩展（DLL）
 - 支持自定义任务计划（可自定义配置触发方式或条件、执行预制模块或脚本等等）
 - 支持服务器自动备份、服务器问题插件筛选（命令行）、服务器文件修改筛选（命令行）
 - 支持多个服务器实例的控制和切换（支持服务器间功能的相互隔离）
 - 支持多端组合局域网组合前后端（如将RtCli放在云服务器，将面板放在本地）
 - 大多数功能自定义比较强，但也有部分没做支持，代码的注释比较易懂也可自行尝试添加
 - [Rt扩展]支持外源Antibot和数据包发包限制及服务器Motd状态事件

## 📘 项目结构
> [!WARNING]\
> 注意请修改程序的开发者千万不要上传自己的AI API key要详细检查

该项目为RtCli作为服务器来管理Minecraft服务器，RtPanel为辅助控制RtCli的面板，其次可选择Rt为扩展或自制扩展。
RtPanel的功能主要是辅助RtCli并不推荐开放到公网当作面板使用，不过只要不分享端口问题不大也可以使用。
给RtCli设置服务器端口再打开RtPanel连接就可以了，如果您觉得面板不好用或不美观，欢迎任何作者制作，但仍然推荐使用原面板。
Rt的主要作用是安全性方面的扩展，目前功能测试中，如果需要其中功能可使用`ct`来释放扩展。

## 🔌 RtCli 扩展
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
> 该程序目前不推荐在公网已经被暴露或危险的网络环境下使用！

## 更新项目
1. 完善项目（对你没听错 - 进度大约89%）

#### 最后及引用
当您需要以任何方式使用或为此项目修改时，非常希望能把项目中原作者名称和项目原地址保留，谢谢你对作者的尊重！ [GitHub](https://github.com/psoloi/RutCitrus)
非常推荐您能对项目指出点评，我只是高中生没有多少时间所以程序后面一半使用了AI来制作和修复，我的设备可能有问题无法运行Docker容器程序的兼容性没测试过，有错误十分感谢您可以提出甚至改进，如果有时间拜托能点个Stars吗？
![GitHub Stars](https://img.shields.io/github/stars/psoloi/RutCitrus?logo=github)

该项目引用了大量库，我十分尊重这些作者的努力，并且没有对库的代码进行修改，具体可见项目引用输入`rt about`命令查看主动引用的库