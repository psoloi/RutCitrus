using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RtCli.Modules.Unit
{
    internal class CProgressBar
    {
        public static async Task Create(string type)
        {
            string ThisProgramName = "CProgressBar";


            int total = 100;
            for (int i = 0; i <= total; i++)
            {
                Console.Write($"\rProgress: [{new string('|', i / 2)}{new string('-', (total - i) / 2)}] {i}%");
                await Task.Delay(2); // Simulate work
            }
            Output.Log("完成", 1, "CProgressBar");


            if (string.IsNullOrEmpty(type))
            {
                //Output.Log("异常的程序启动参数?", 2, ThisProgramName);
                return;
            }

            var actions = new Dictionary<string, Action>
                {
                    { "SetMode", () => {
                        Output.Log("设置模式中...", 1, ThisProgramName);
                    } },
                    { "2", () => { /* Add functionality for option 2 */ } },
                    { "3", () => { /* Add functionality for option 3 */ } },
                    { "4", () => { /* Add functionality for option 4 */ } },
                    { "5", () => { /* Add functionality for option 5 */ } },
                    { "6", () => { } },
                    { "7", () => { } }
                };

            if (actions.ContainsKey(type))
            {
                actions[type].Invoke();
            }
            else
            {
                Output.Log("未知的程序启动参数!", 2, ThisProgramName);
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

        }
    }
}
