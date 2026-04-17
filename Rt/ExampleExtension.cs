using MineStatLib;
using RtCli.Modules;
using RtCli.Modules.Function;
using RtExtensionManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rt
{
    /// <summary>
    /// 示例扩展插件
    /// </summary>
    public class ExampleExtension : IExtension
    {
        public string Name => "Rt";
        public string Version => "1.0.3";
        public string Description => "示例扩展插件，实现了MC服务器状态监测及安全方面扩展";

        private bool _isLoaded = false;

        public void Load()
        {
            if (_isLoaded)
            {
                Output.Log("扩展已经加载过了", 2, Name);
                return;
            }

            Output.Log("扩展正在加载...", 1, Name);
            // 注册命令
            CommandRegistry.RegisterCommand("rte", args =>
            {
                Output.Log($"[green]Rt版本：{Version} 输入rte help获取帮助", 1, "Rt");
            }, "Rt扩展命令");
            CommandRegistry.RegisterCommand("rte help", args =>
            {
                Output.Log($"[green]目前该扩展还在开发只有一个get副命令，你先别急，急又是何意味？", 1, "Rt");
            }, "Rt扩展命令帮助");
            CommandRegistry.RegisterCommand("rte get", args =>
            {
                Output.Log($"[green]获取本地127.0.0.1:25565服务器信息", 1, "Rt");
                MineStat ms = new MineStat("127.0.0.1", 25565);
                if (ms.ServerUp)
                {
                    Output.Log($"服务器在线其版本为{ms.Version}最大玩家{ms.MaximumPlayers}当前玩家{ms.CurrentPlayers}", 1, "Rt");
                    if (ms.Gamemode != null)
                        Output.Log($"服务器游戏模式为{ms.Gamemode}", 1, "Rt");
                    Output.Log($"服务器消息为{ms.Stripped_Motd}", 1, "Rt");
                    Output.Log($"服务器延迟为{ms.Latency}ms", 1, "Rt");
                    Output.Log($"服务器在线玩家列表为{string.Join(", ", ms.PlayerList)}", 1, "Rt");
                    Output.Log($"服务器使用的协议为{ms.Protocol}", 1, "Rt");
                }
                else
                    Output.Log($"服务器离线", 1, "Rt");


            }, "Rt扩展获取Minecraft信息");


            _isLoaded = true;
            Output.Log("扩展加载完成", 1, Name);
        }

        public void Run()
        {
            if (!_isLoaded)
            {
                Output.Log("扩展未加载，无法运行", 3, Name);
                return;
            }

            Output.Log("扩展正在运行...", 1, Name);


            //
            throw new NotImplementedException("示例扩展的Run方法尚未实现");

        }

        public void Unload()
        {
            if (!_isLoaded)
            {
                Output.Log("扩展未加载，无法卸载", 2, Name);
                return;
            }

            Output.Log("扩展正在卸载...", 1, Name);
            // 注销命令
            CommandRegistry.UnregisterCommand("rte");


            _isLoaded = false;
            Output.Log("扩展卸载完成", 1, Name);
        }

    }
}
