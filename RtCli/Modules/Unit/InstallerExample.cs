using System; using System.Threading.Tasks;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 这个类演示了如何从外部正确引用和使用PhotoshopStyleInstaller的方法
    /// 重要提示：修改后不再要求在主线程调用，但窗口和OpenGL上下文必须在同一线程
    /// </summary>
    public class InstallerExample
    {
        /// <summary>
        /// 运行安装界面的完整示例
        /// 推荐使用StartAsync方法以避免阻塞调用线程
        /// </summary>
        public static async Task RunInstallerExample()
        {
            // 创建安装界面实例
            var installer = new PhotoshopStyleInstaller();

            try
            {
                // 步骤1: 启动安装界面
                // 现在有两种选择：
                Console.WriteLine("启动安装界面...");

                // 方法1: 使用异步StartAsync方法（推荐）
                // 这会在一个新的线程上创建并运行窗口，不会阻塞当前线程
                await installer.StartAsync();

                // 方法2: 使用同步Start方法
                // 这会阻塞当前线程直到窗口关闭
                // installer.Start();

                // 步骤2: 等待UI初始化（给用户一些时间看到界面）
                await Task.Delay(1000);

                // 步骤3: 检查UI是否正在运行
                if (installer.IsRunning)
                {
                    Console.WriteLine("开始安装过程...");

                    // 步骤4: 更新各个安装任务的进度
                    // 注意：任务名称必须与InstallerWindow.InitializeTasks()中定义的任务名称完全匹配

                    // 更新第一个任务进度
                    installer.UpdateProgress("Extracting installation files", 25f);
                    await Task.Delay(500); // 模拟安装过程

                    installer.UpdateProgress("Extracting installation files", 100f); // 完成第一个任务
                    await Task.Delay(500);

                    // 更新第二个任务进度
                    installer.UpdateProgress("Installing core components", 30f);
                    await Task.Delay(500);

                    installer.UpdateProgress("Installing core components", 70f);
                    await Task.Delay(500);

                    installer.UpdateProgress("Installing core components", 100f); // 完成第二个任务
                    await Task.Delay(500);

                    // 可以继续更新其他任务...
                    installer.UpdateProgress("Configuring preferences", 50f);
                    await Task.Delay(500);

                    installer.UpdateProgress("Configuring preferences", 100f);
                    await Task.Delay(500);

                    // 步骤5: 完成所有安装任务
                    // 或者直接调用CompleteInstallation()一次性完成所有任务
                    installer.CompleteInstallation();

                    Console.WriteLine("安装完成！");
                    await Task.Delay(2000); // 给用户时间看到完成界面

                    // 步骤6: 关闭安装界面
                    // 调用StopAsync()确保窗口正确关闭并等待窗口线程结束
                    await installer.StopAsync();
                    Console.WriteLine("安装界面已关闭");
                }
            }
            catch (OpenTK.Windowing.GraphicsLibraryFramework.GLFWException ex)
            {
                // 特别捕获GLFW异常，通常是因为在非主线程上调用
                Console.WriteLine($"GLFW错误: {ex.Message}");
                Console.WriteLine("请确保在主线程上调用InstallerExample.RunInstallerExample()方法");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"安装过程中发生错误: {ex.Message}");
                // 确保在错误情况下也关闭窗口
                if (installer != null)
                {
                    await installer.StopAsync();
                }
            }
        }

        /// <summary>
        /// 如何从控制台应用的Main方法正确调用
        /// </summary>
        public static void ConsoleApplicationExample()
        {
            /* 现在有两种主要方式：
            
            1. 异步Main方法（推荐）
            static async Task Main(string[] args)
            {
                await RunInstallerExample(); // 使用异步方法，不会阻塞
            }
            
            2. 同步Main方法
            static void Main(string[] args)
            {
                // 选项A: 阻塞等待完成
                RunInstallerExample().Wait();
                
                // 选项B: 使用同步Start方法
                var installer = new PhotoshopStyleInstaller();
                installer.Start(); // 这会阻塞直到窗口关闭
            }
            */
        }
        /// <summary>
        /// 常见问题解答
        /// </summary>
        public static class FAQ
        {
            // Q: 为什么之前会出现"GLFW can only be called from the main thread!"错误？
            // A: 因为之前的实现尝试在不同线程上创建窗口和运行窗口，导致OpenGL上下文问题

            // Q: 现在的实现为什么能解决这个问题？
            // A: 现在确保窗口的创建和运行在同一个线程上，无论是主线程还是后台线程

            // Q: Start和StartAsync方法有什么区别？
            // A: Start会阻塞调用线程直到窗口关闭，StartAsync不会阻塞并在后台线程运行窗口

            // Q: 我应该使用哪个方法？
            // A: 推荐使用StartAsync，除非你有意让调用线程被阻塞
        }
    }
}