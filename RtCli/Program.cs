using RtCli.Modules;
using RtExtensionManager;
using System.Xml.Linq;

namespace RtCli
{
    internal class Program
    {
        static void Main(string[] args)
        {

            Console.Title = "RtCli";
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Output.TextBlock("Start the main thread...", 1, "Task#0");

            string ThisProgramName = "RtCli";
            Thread.CurrentThread.Name = "Main";

            Output.Log("Running...", 1, ThisProgramName);

            RtExtensionManager.RtExtensionManager.LoadAll();
        }
    }
}
