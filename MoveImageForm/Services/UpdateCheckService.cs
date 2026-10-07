using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 运行中更新检查服务：主程序运行期间每 N 小时（默认 4，可配 UpdateCheckIntervalHours）
    /// 后台读取更新服务器 version.json 并做灰度判定，把结果写入 gray.state：
    ///   有新版本 → decision=pending（下次启动由 Launcher 安装）
    ///   目标低于本地 → decision=rollback（下次启动由 Launcher 自动降级）
    ///   已最新 → decision=stay
    /// 只在首次发现新版本时在日志区提示一条；失败静默、不弹窗、不影响搬运业务。
    /// 安装动作恒由 Launcher 在下次启动时执行，本服务不触发任何安装。
    /// </summary>
    public class UpdateCheckService
    {
        private readonly Func<AppConfig> _getConfig;
        private readonly string _appRoot;
        private readonly string _baseDir;
        private readonly Action<string> _log;
        private readonly TimeSpan _firstCheckDelay;
        private CancellationTokenSource _cts;
        private string _lastNotifiedTarget = "";

        public UpdateCheckService(Func<AppConfig> getConfig, string appRoot, string baseDir,
            Action<string> log, TimeSpan? firstCheckDelay = null)
        {
            _getConfig = getConfig;
            _appRoot = appRoot;
            _baseDir = baseDir;
            _log = log ?? (m => { });
            // 首次检查默认 10 分钟后（避开启动时 Launcher 刚做过的检查）
            _firstCheckDelay = firstCheckDelay ?? TimeSpan.FromMinutes(10);
        }

        public void Start()
        {
            var config = _getConfig();
            string serverPath = config?.UpdateServerPath?.Trim() ?? "";
            if (string.IsNullOrEmpty(serverPath))
            {
                _log("[更新检查] 未配置更新服务器（UpdateServerPath），运行中检查未启用");
                return;
            }
            Stop();
            _cts = new CancellationTokenSource();
            int hours = config.UpdateCheckIntervalHours;
            if (hours < 1 || hours > 72) hours = 4;
            _log($"[更新检查] 运行中检查已启用（每 {hours} 小时一次）");
            Task.Run(() => Loop(TimeSpan.FromHours(hours), _cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        private async Task Loop(TimeSpan interval, CancellationToken token)
        {
            try
            {
                await Task.Delay(_firstCheckDelay, token);
                while (!token.IsCancellationRequested)
                {
                    Check();
                    await Task.Delay(interval, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                LogToFile("运行中检查循环异常终止: " + ex.Message);
            }
        }

        /// <summary>执行一次检查（所有异常内部消化）</summary>
        public void Check()
        {
            try
            {
                var config = _getConfig();
                string serverPath = config?.UpdateServerPath?.Trim() ?? "";
                if (string.IsNullOrEmpty(serverPath)) return;

                string versionFile = Path.Combine(serverPath, "version.json");
                if (!File.Exists(versionFile))
                {
                    LogToFile("version.json 不可达，本周期跳过");
                    return;
                }

                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(versionFile, Encoding.UTF8));
                if (data == null) return;

                string latest = data.ContainsKey("latest") ? data["latest"]?.ToString()?.Trim() ?? "" : "";
                var gray = GrayConfig.FromVersionJson(data);

                string machineId = MachineIdStore.GetOrCreate(_appRoot);
                bool optIn = config.GrayOptIn;
                var decision = GrayPolicy.Decide(machineId, optIn, gray);

                bool hitGray = decision == GrayDecision.Upgrade;
                string target = hitGray ? gray.Version : latest;
                string source = hitGray ? "gray" : "latest";
                int policyPercent = hitGray ? gray.Percent : 0;
                if (string.IsNullOrWhiteSpace(target)) return;

                string localVer = VersionInfo.GetCurrentVersion(_baseDir);
                int cmp = CompareVersions(target, localVer);

                if (cmp > 0)
                {
                    // 熔断版本不重提：写 failed，等待 Launcher 下次启动处理
                    if (IsVersionFused(target))
                    {
                        GrayState.Write(_appRoot, "failed", target, source, optIn, policyPercent);
                        return;
                    }
                    GrayState.Write(_appRoot, "pending", target, source, optIn, policyPercent);
                    // 同一进程内同一目标只提示一次
                    if (!string.Equals(_lastNotifiedTarget, target, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastNotifiedTarget = target;
                        _log($"[更新检查] 检测到新版本 v{target}（{(source == "gray" ? "灰度" : "正式")}），将在下次启动时更新");
                    }
                }
                else if (cmp < 0)
                {
                    GrayState.Write(_appRoot, "rollback", target, source, optIn, policyPercent);
                }
                else
                {
                    GrayState.Write(_appRoot, "stay", target, source, optIn, policyPercent);
                }
            }
            catch (Exception ex)
            {
                LogToFile("运行中检查异常: " + ex.Message);
            }
        }

        private bool IsVersionFused(string version)
        {
            try
            {
                string path = Path.Combine(_appRoot, "update.failed");
                if (!File.Exists(path)) return false;
                string v = ""; int count = 0;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("version=")) v = line.Substring(8).Trim();
                    else if (line.StartsWith("count=")) int.TryParse(line.Substring(6).Trim(), out count);
                }
                return count >= 2 && string.Equals(v, version, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static int CompareVersions(string a, string b)
        {
            try
            {
                return new Version(a).CompareTo(new Version(b));
            }
            catch
            {
                return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void LogToFile(string message)
        {
            try
            {
                string logDir = Path.Combine(_baseDir, "Logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "update_check.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
