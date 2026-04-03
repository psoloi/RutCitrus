using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RtCli.Modules;
using RtExtensionManager;

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


            _isLoaded = false;
            Output.Log("扩展卸载完成", 1, Name);
        }

    }
}
