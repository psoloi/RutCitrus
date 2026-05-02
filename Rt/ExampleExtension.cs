using MineStatLib;
using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using RtExtensionManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;

namespace Rt
{
    public class ExampleExtension : ExtensionBase
    {
        public override string Name => "Rt";
        public override string Version => "1.1.5";
        public override string Description => "示例扩展插件，扩展了MC服务器状态监测、安全和网页面板";

        private bool _isLoaded = false;

        public override void Load()
        {
            if (_isLoaded)
            {
                Output.Log("Rt扩展已经加载过了", 2, Name);
                return;
            }

            RegisterAssemblyResolver();

            Output.Log("Rt扩展正在加载...", 1, Name);

            SubscribeEvent<CommandExecuteEvent>(OnCommandExecute);

            CommandRegistry.RegisterCommand("rte", args =>
            {
                Output.Log($"[green]Rt版本：{Version} 输入rte help获取帮助[/]", 1, "Rt");
            }, "Rt扩展命令");
            CommandRegistry.RegisterCommand("rte help", args =>
            {
                Output.Log($"[green]目前该扩展还在开发只有一个get副命令，你先别急，急又是何意味？[/]", 1, "Rt");
            }, "Rt扩展命令帮助");
            CommandRegistry.RegisterCommand("rte get", args =>
            {
                Console.WriteLine("正在获取本地127.0.0.1:25565服务器信息...");
                MineStat ms = new MineStat("127.0.0.1", 25565);
                if (ms.ServerUp)
                {
                    Console.WriteLine($"服务器在线其版本为{ms.Version}最大玩家{ms.MaximumPlayers}当前玩家{ms.CurrentPlayers}");
                    if (ms.Gamemode != null)
                        Console.WriteLine($"服务器游戏模式为{ms.Gamemode}");
                    Console.WriteLine($"服务器版本为{ms.Version}最大玩家{ms.MaximumPlayers}当前玩家{ms.CurrentPlayers}");
                    Console.WriteLine("服务器消息为" + ms.Motd);
                    Console.WriteLine($"服务器延迟为{ms.Latency}ms");
                    Console.WriteLine($"服务器在线玩家列表为{string.Join(", ", ms.PlayerList)}");
                    Console.WriteLine($"服务器使用的协议为{ms.Protocol}");
                }
                else
                    Console.WriteLine("服务器离线");


            }, "Rt扩展获取Minecraft信息");


            _isLoaded = true;
            Output.Log("Rt扩展加载完成", 1, Name);
        }

        private void OnCommandExecute(CommandExecuteEvent e)
        {
            //Output.Log($"[green]命令执行[/]: {e.Command}", 1, Name);
        }

        public override void Run()
        {
            if (!_isLoaded)
            {
                Output.Log("Rt扩展未加载，无法运行", 3, Name);
                return;
            }

            Output.Log("Rt扩展正在运行...", 1, Name);

            throw new NotImplementedException("示例扩展的Run方法尚未实现");
        }

        public override void Unload()
        {
            if (!_isLoaded)
            {
                Output.Log("Rt扩展未加载，无法卸载", 2, Name);
                return;
            }

            Output.Log("Rt扩展正在卸载...", 1, Name);
            CommandRegistry.UnregisterCommand("rte");
            CommandRegistry.UnregisterCommand("rte help");
            CommandRegistry.UnregisterCommand("rte get");

            base.Unload();

            _isLoaded = false;
            Output.Log("Rt扩展卸载完成", 1, Name);
        }




        private static bool _resolverRegistered = false;
        private static readonly Dictionary<string, byte[]> _loadedAssemblies = new();

        private static void RegisterAssemblyResolver()
        {
            if (_resolverRegistered)
                return;

            var currentContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            if (currentContext == null)
            {
                Output.Log("扩展缺失AssemblyLoadContext", 2, "Rt");
                return;
            }

            currentContext.Resolving += (context, assemblyName) =>
            {
                string resourceName = assemblyName.Name + ".dll";

                if (_loadedAssemblies.TryGetValue(assemblyName.Name!, out byte[]? assemblyBytes))
                {
                    using var stream = new MemoryStream(assemblyBytes);
                    return context.LoadFromStream(stream);
                }

                Assembly asm = Assembly.GetExecutingAssembly();
                string[] resources = asm.GetManifestResourceNames();

                var matchingResource = resources.FirstOrDefault(r =>
                    r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase) ||
                    r == resourceName ||
                    r.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase));

                if (matchingResource != null)
                {
                    using Stream? resourceStream = asm.GetManifestResourceStream(matchingResource);
                    if (resourceStream != null)
                    {
                        byte[] bytes = new byte[resourceStream.Length];
                        resourceStream.Read(bytes, 0, bytes.Length);

                        _loadedAssemblies[assemblyName.Name!] = bytes;

                        using var memStream = new MemoryStream(bytes);
                        return context.LoadFromStream(memStream);
                    }
                }

                return null;
            };

            _resolverRegistered = true;
        }
    }
}
