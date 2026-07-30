using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MoveImageForm.Helpers
{
    /// <summary>
    /// 崩溃日志和 MiniDump 生成工具。
    /// MiniDump 可用 WinDbg 或 Visual Studio 打开进行事后分析。
    /// </summary>
    public static class CrashDumper
    {
        // dbghelp.dll 的 MiniDumpWriteDump
        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            IntPtr hFile,
            uint dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        // MiniDumpWithFullMemory = 2：包含完整内存快照
        private const uint MiniDumpWithFullMemory = 2;

        private static string LogDir =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>写文本崩溃日志</summary>
        public static void WriteCrashLog(Exception ex, string category)
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string path = Path.Combine(LogDir, $"Crash_{timestamp}.txt");

                var sb = new StringBuilder();
                sb.AppendLine("==== 崩溃报告 ====");
                sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"类型: {category}");
                sb.AppendLine();
                sb.AppendLine("==== 异常信息 ====");
                sb.AppendLine(ex?.ToString() ?? "无异常信息");
                sb.AppendLine();
                sb.AppendLine("==== 系统信息 ====");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($".NET: {Environment.Version}");
                sb.AppendLine($"64位进程: {Environment.Is64BitProcess}");
                sb.AppendLine($"处理器数: {Environment.ProcessorCount}");
                sb.AppendLine($"工作集内存: {Environment.WorkingSet / 1048576} MB");

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 崩溃日志本身绝不能抛异常
            }
        }

        /// <summary>生成 MiniDump 文件（完整内存快照）</summary>
        public static void WriteMiniDump(Exception ex)
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string path = Path.Combine(LogDir, $"Crash_{timestamp}.dmp");

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        bool ok = MiniDumpWriteDump(
                            process.Handle,
                            (uint)process.Id,
                            fs.SafeFileHandle.DangerousGetHandle(),
                            MiniDumpWithFullMemory,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            IntPtr.Zero);

                        if (!ok)
                        {
                            // MiniDump 失败时写一个标记文件
                            string failPath = Path.Combine(LogDir, $"Crash_{timestamp}_dump_failed.txt");
                            File.WriteAllText(failPath,
                                $"MiniDumpWriteDump failed. LastError: {Marshal.GetLastWin32Error()}",
                                new UTF8Encoding(false));
                        }
                    }
                }
            }
            catch
            {
                // Dump 失败不能影响主流程
            }
        }
    }
}
