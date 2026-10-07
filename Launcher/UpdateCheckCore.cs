using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Launcher
{
    /// <summary>
    /// 更新检查核心逻辑（无 UI 依赖，可无头测试）。
    /// 读取 version.json（latest + gray 灰度块）→ 灰度判定 → 得出目标版本与动作，
    /// 并叠加熔断（update.failed）与用户跳过（update.dismissed）状态。
    /// </summary>
    public static class UpdateCheckCore
    {
        public enum UpdateAction
        {
            /// <summary>检查失败（网络/解析异常），按本地版本直接启动</summary>
            Error,
            /// <summary>已是最新，无需动作</summary>
            None,
            /// <summary>有新版本，提示用户升级</summary>
            Upgrade,
            /// <summary>目标版本低于本地（灰度收敛/摘除），自动降级</summary>
            Downgrade,
            /// <summary>该版本已被熔断（同版本失败 ≥2 次），跳过</summary>
            Fused,
            /// <summary>用户已跳过该版本，本次不再提示</summary>
            Dismissed
        }

        public class CheckResult
        {
            public UpdateAction Action = UpdateAction.Error;
            public string Error = "";
            public string Latest = "";
            public GrayConfig Gray = new GrayConfig();
            public GrayDecision Decision = GrayDecision.Stay;
            public string TargetVersion = "";
            public string TargetSource = "latest"; // gray | latest
            public string LocalVersion = "0.0.0";
            public string CloudDate = "";
            public string CloudNote = "";
            public int PolicyPercent;
        }

        /// <summary>
        /// 执行检查。serverPath 为空或 version.json 不存在/解析失败时 Action=Error，调用方按本地版直接启动。
        /// </summary>
        public static CheckResult Evaluate(string serverPath, string machineId, bool optIn, string appRoot)
        {
            var result = new CheckResult();
            try
            {
                if (string.IsNullOrWhiteSpace(serverPath))
                {
                    result.Error = "未配置更新服务器";
                    return result;
                }

                string versionFile = Path.Combine(serverPath, "version.json");
                if (!File.Exists(versionFile))
                {
                    result.Error = "未找到云端 version.json";
                    return result;
                }

                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(versionFile, Encoding.UTF8));
                if (data == null)
                {
                    result.Error = "version.json 解析失败";
                    return result;
                }

                result.Latest = data.ContainsKey("latest") ? data["latest"]?.ToString()?.Trim() ?? "" : "";
                result.Gray = GrayConfig.FromVersionJson(data);
                result.LocalVersion = VersionDirs.GetLocalLatestVersion(appRoot);

                // 灰度判定（黑名单 > 白名单豁免 > requireOptIn > 百分比分桶）
                result.Decision = GrayPolicy.Decide(machineId, optIn, result.Gray);
                bool hitGray = result.Decision == GrayDecision.Upgrade;
                result.TargetVersion = hitGray ? result.Gray.Version : result.Latest;
                result.TargetSource = hitGray ? "gray" : "latest";
                result.PolicyPercent = hitGray ? result.Gray.Percent : 0;

                // 读取目标版本的说明信息
                ReadVersionNote(data, result.TargetVersion, out result.CloudDate, out result.CloudNote);

                if (string.IsNullOrWhiteSpace(result.TargetVersion))
                {
                    result.Action = UpdateAction.Error;
                    result.Error = "云端未配置目标版本号";
                    return result;
                }

                int cmp = CompareVersions(result.TargetVersion, result.LocalVersion);
                if (cmp > 0)
                {
                    // 熔断：同一版本失败 ≥2 次 → 跳过，不再尝试也不再提示
                    if (UpdateInstaller.IsVersionFused(appRoot, result.TargetVersion))
                    {
                        result.Action = UpdateAction.Fused;
                        return result;
                    }
                    // 用户已跳过该版本：不再重复弹窗
                    string dismissed = UpdateInstaller.ReadDismissedVersion(appRoot);
                    if (!string.IsNullOrEmpty(dismissed) &&
                        string.Equals(dismissed, result.TargetVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Action = UpdateAction.Dismissed;
                        return result;
                    }
                    result.Action = UpdateAction.Upgrade;
                }
                else if (cmp < 0)
                {
                    // 目标版本低于本地：灰度收敛/摘除/回滚指令，自动降级
                    result.Action = UpdateAction.Downgrade;
                }
                else
                {
                    result.Action = UpdateAction.None;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Action = UpdateAction.Error;
                result.Error = ex.Message;
                return result;
            }
        }

        private static void ReadVersionNote(Dictionary<string, object> data, string version,
            out string date, out string note)
        {
            date = ""; note = "";
            try
            {
                if (string.IsNullOrEmpty(version) || !data.ContainsKey("versions")) return;
                var versions = data["versions"] as Dictionary<string, object>;
                if (versions == null || !versions.ContainsKey(version)) return;
                var info = versions[version] as Dictionary<string, object>;
                if (info == null) return;
                if (info.ContainsKey("date")) date = info["date"]?.ToString() ?? "";
                if (info.ContainsKey("note")) note = info["note"]?.ToString() ?? "";
            }
            catch { }
        }

        /// <summary>版本号比较：target &gt; local 返回正数。解析失败退化为字符串比较</summary>
        public static int CompareVersions(string a, string b)
        {
            try
            {
                var va = new Version(a);
                var vb = new Version(b);
                return va.CompareTo(vb);
            }
            catch
            {
                return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 本地版本目录扫描（版本真相源：只认 versions\ 目录名，不看程序集版本号）。
    /// 有效目录 = 目录名是版本号 且 含 MoveImageForm.exe 且
    /// （无 manifest.json —— 老版本兼容放行；或 manifest 校验通过）。
    /// 半成品目录（无 exe 或 manifest 校验失败）被排除，自动回退到最近可用版本。
    /// </summary>
    public static class VersionDirs
    {
        public static string GetLocalLatestVersion(string appRoot)
        {
            string versionsDir = Path.Combine(appRoot, "versions");
            if (!Directory.Exists(versionsDir)) return "0.0.0";

            Version best = new Version(0, 0, 0);
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                string dirName = Path.GetFileName(dir);
                Version ver;
                if (!Version.TryParse(dirName, out ver)) continue;
                if (!IsUsableVersionDir(dir)) continue;
                if (ver > best) best = ver;
            }
            return string.Format("{0}.{1}.{2}", best.Major, best.Minor, best.Build);
        }

        /// <summary>目录是否可用：exe 存在，且（无 manifest（老版本兼容）或 manifest 校验通过）</summary>
        public static bool IsUsableVersionDir(string dir)
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, "MoveImageForm.exe")))
                    return false;
                if (!ManifestVerifier.HasManifest(dir))
                    return true; // 老版本目录无 manifest，兼容放行
                return ManifestVerifier.VerifyDirectory(dir).Success;
            }
            catch
            {
                return false;
            }
        }
    }
}
