using System;
using System.IO;

namespace FakeMainApp
{
    /// <summary>
    /// 测试用的假主程序：编译产物名为 MoveImageForm.exe。
    /// 支持 --version --probe-out <file> 应答（供 Launcher --apply 探活）；
    /// 无参数启动时立即退出（无 UI、不写注册表、不驻留进程），避免污染测试环境。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--version")
            {
                try
                {
                    string probeOut = null;
                    for (int i = 1; i < args.Length - 1; i++)
                    {
                        if (args[i] == "--probe-out")
                        {
                            probeOut = args[i + 1].Trim('"');
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(probeOut))
                    {
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        string versionFile = Path.Combine(baseDir, "version.txt");
                        string ver = File.Exists(versionFile)
                            ? File.ReadAllText(versionFile).Trim()
                            : "0.0.0";
                        File.WriteAllText(probeOut, ver);
                    }
                }
                catch
                {
                    return 1;
                }
            }
            return 0;
        }
    }
}
