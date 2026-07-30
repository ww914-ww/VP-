using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MoveImageForm.Helpers;

namespace MoveImageForm
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "MoveImageForm_SingleInstanceApp";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // app is already running! Exiting the application
                MessageBox.Show("程序已经在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            // 注册全局异常处理器
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;

            base.OnStartup(e);
        }

        /// <summary>UI 线程异常 — 记录日志后可恢复</summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            CrashDumper.WriteCrashLog(e.Exception, "UI线程异常(Dispatcher)");
            CrashDumper.WriteMiniDump(e.Exception);

            MessageBox.Show(
                $"软件遇到异常，已记录崩溃日志到 Logs 目录。\n\n错误: {e.Exception.Message}",
                "程序异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true; // 阻止进程终止
        }

        /// <summary>后台线程未处理异常 — 通常无法恢复</summary>
        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("未知异常(非Exception对象)");

            CrashDumper.WriteCrashLog(ex,
                e.IsTerminating ? "未处理异常(后台线程,进程将终止)" : "未处理异常(后台线程)");
            CrashDumper.WriteMiniDump(ex);

            if (e.IsTerminating)
            {
                MessageBox.Show(
                    $"软件发生严重错误，即将退出。\n\n崩溃日志和 Dump 文件已保存到 Logs 目录，请联系管理员。\n\n错误: {ex.Message}",
                    "严重错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>被遗忘的 Task 异常 — 记录后标记为已观察，防止进程被杀</summary>
        private void App_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            CrashDumper.WriteCrashLog(e.Exception, "未观察到的Task异常(UnobservedTask)");
            e.SetObserved(); // 防止 .NET 4.0+ 的进程终止策略
        }
    }
}
