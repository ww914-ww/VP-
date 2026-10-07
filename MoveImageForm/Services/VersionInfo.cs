using System;
using System.IO;
using System.Text;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 版本与应用根目录定位（版本真相源：包内 version.txt &gt; versions\ 目录名；不看程序集版本号）。
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>
        /// 应用根目录：正常部署结构为 appRoot\versions\{版本}\MoveImageForm.exe，
        /// 即当前 exe 向上两级（且该处存在 config.xml）；开发直跑（out-bin）时退化为 exe 所在目录。
        /// </summary>
        public static string GetAppRoot(string baseDir)
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
                if (File.Exists(Path.Combine(candidate, "config.xml")))
                    return candidate;
            }
            catch { }
            return baseDir;
        }

        /// <summary>
        /// 当前运行版本：优先读 exe 同目录 version.txt（正式包内嵌），
        /// 其次取父目录名（versions\{版本} 结构），最后兜底 0.0.0。
        /// </summary>
        public static string GetCurrentVersion(string baseDir)
        {
            try
            {
                string versionFile = Path.Combine(baseDir, "version.txt");
                if (File.Exists(versionFile))
                {
                    string ver = File.ReadAllText(versionFile).Trim();
                    Version parsed;
                    if (Version.TryParse(ver, out parsed))
                        return string.Format("{0}.{1}.{2}", parsed.Major, parsed.Minor, parsed.Build);
                    if (!string.IsNullOrEmpty(ver)) return ver;
                }
            }
            catch { }

            try
            {
                string dirName = new DirectoryInfo(baseDir.TrimEnd('\\', '/')).Name;
                Version dirVer;
                if (Version.TryParse(dirName, out dirVer))
                    return string.Format("{0}.{1}.{2}", dirVer.Major, dirVer.Minor, dirVer.Build);
            }
            catch { }

            return "0.0.0";
        }
    }
}
