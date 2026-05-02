using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class Intelligence
    {
        //.guide
        public static void Guide()
        {
            if (Checker.CheckJava().StartsWith(I18n.Get("checker_nojava")))
            {
                Output.Log("（1）请下载JDK并安装后重试...", 1, "Guide");
                var table = new Table()
                    .AddColumn("JDK类型")
                    .AddColumn("网站")
                    .AddColumn("备注")
                    .AddRow("<非商业>OracleJDK", "https://www.oracle.com/java/technologies/downloads/", "当MC服务器涉及商业用途时请尽量不要选择")
                    .AddRow("<非商业>GraalvmJDK", "https://www.graalvm.org/downloads/", "性能相对良好")
                    .AddRow("[green]<GNU2>OpenJDK[/]", "https://jdk.java.net/26/", "可自由使用");
                AnsiConsole.Write(table);
            }
            else
            {
                Output.Log($"（1）Java环境检测通过，版本为{Checker.CheckJava().Substring(I18n.Get("checker_java").Length)}", 1, "Guide");
                Output.Log("（2）请下载Minecraft服务端并配置Config.yml中的work_path和run_server_flags后输入rt reload重启", 1, "Guide");
            }
        }
        //.auto
        public static void Auto()
        {

        }
        public static void MinecraftServer()
        {

        }
    }
}
