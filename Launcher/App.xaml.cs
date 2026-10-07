using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Launcher
{
    public partial class App : Application
    {
        /// <summary>
        /// 支持 --apply &lt;version&gt; 无界面安装模式：
        /// 由正常模式的 Launcher 在下载校验完成后拉起，执行安装后自动重启 Launcher。
        /// 该模式不创建主窗口（不调用 base.OnStartup 即不处理 StartupUri）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args != null && e.Args.Length >= 2 &&
                string.Equals(e.Args[0], "--apply", StringComparison.OrdinalIgnoreCase))
            {
                string appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string version = (e.Args[1] ?? "").Trim().Trim('"');
                int exitCode;
                try
                {
                    exitCode = UpdateInstaller.Apply(appRoot, version);
                }
                catch (Exception ex)
                {
                    UpdateInstaller.Log(appRoot, "--apply 未处理异常: " + ex.Message);
                    UpdateInstaller.WriteStatus(appRoot, "failed");
                    exitCode = 1;
                }
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);
        }
    }
}
