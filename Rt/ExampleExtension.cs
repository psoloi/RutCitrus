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
        public string Version => "1.0.0";
        public string Description => "这是一个示例扩展插件，演示插件的基本功能";

        private bool _isLoaded = false;
        private Timer _timer;

        public void Load()
        {
            if (_isLoaded)
            {
                Output.Log("扩展已经加载过了", 2, Name);
                return;
            }

            // 模拟加载过程
            Output.Log("扩展正在加载...", 1, Name);

            // 初始化资源
            _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);

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

            // 模拟业务逻辑
            for (int i = 1; i <= 3; i++)
            {
                Output.Log($"扩展执行任务 {i}/3", 1, Name);
                Thread.Sleep(300);
            }

            // 启动定时器
            _timer.Change(0, 2000);

            Output.Log("扩展运行完成", 1, Name);
        }

        public void Unload()
        {
            if (!_isLoaded)
            {
                Output.Log("扩展未加载，无法卸载", 2, Name);
                return;
            }

            Output.Log("扩展正在卸载...", 1, Name);

            // 停止定时器
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;

            // 模拟清理资源
            Thread.Sleep(300);

            _isLoaded = false;
            Output.Log("扩展卸载完成", 1, Name);
        }

        private void OnTimerTick(object state)
        {
            Output.Log($"扩展定时任务执行{DateTime.Now:HH:mm:ss}", 1, Name);
        }
    }
}
