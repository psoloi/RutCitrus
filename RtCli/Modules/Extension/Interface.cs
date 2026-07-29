using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RtCli.Modules.Extension
{
    internal interface Interface
    {
        //simple plugin bridge
    }
    public class Extension : Interface
    {
        public static void Plugin()
        {
            // 插件bridge，支持插件的加载、卸载、更新、配置、管理等功能
        }
        public static void API()
        { 

        }
    }
}
