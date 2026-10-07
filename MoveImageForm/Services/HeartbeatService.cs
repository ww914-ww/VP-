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
    /// 心跳上报服务：周期性地将本机状态（machineId / 版本 / 灰度状态 / opt-in）
    /// 经已登录的可写 SFTP 连接上传为 heartbeat\{machineId}.json（一机一文件、覆盖写、不堆积）。
    ///
    /// 铁律：心跳是运维观测手段，绝不影响文件搬运业务——
    /// 失败静默（仅写本地 Logs\heartbeat.log）、不弹窗、不阻塞；未登录可写账号时本周期跳过。
    /// </summary>
    public class HeartbeatService
    {
        private readonly SessionManager _session;
        private readonly Func<AppConfig> _getConfig;
        private readonly string _appRoot;
        private readonly string _baseDir;
        private readonly Action<string> _log;
        private readonly TimeSpan _firstBeatDelay;
        private CancellationTokenSource _cts;

        /// <summary>
        /// firstBeatDelay 仅测试可注入；生产默认 30 秒内首拍（覆盖"升级后是否活着"关键观测点）。
        /// </summary>
        public HeartbeatService(SessionManager session, Func<AppConfig> getConfig,
            string appRoot, string baseDir, Action<string> log, TimeSpan? firstBeatDelay = null)
        {
            _session = session;
            _getConfig = getConfig;
            _appRoot = appRoot;
            _baseDir = baseDir;
            _log = log ?? (m => { });
            _firstBeatDelay = firstBeatDelay ?? TimeSpan.FromSeconds(30);
        }

        public void Start()
        {
            var config = _getConfig();
            if (config == null || !config.HeartbeatEnabled)
            {
                _log("[心跳] 未启用（HeartbeatEnabled=false），心跳服务不启动");
                return;
            }
            Stop();
            _cts = new CancellationTokenSource();
            Task.Run(() => Loop(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        private async Task Loop(CancellationToken token)
        {
            try
            {
                await Task.Delay(_firstBeatDelay, token);
                while (!token.IsCancellationRequested)
                {
                    Beat();
                    int intervalMin = 5;
                    try
                    {
                        var cfg = _getConfig();
                        if (cfg != null && cfg.HeartbeatIntervalMinutes > 0)
                            intervalMin = cfg.HeartbeatIntervalMinutes;
                    }
                    catch { }
                    await Task.Delay(TimeSpan.FromMinutes(intervalMin), token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                LogToFile("心跳循环异常终止: " + ex.Message);
            }
        }

        /// <summary>执行一次心跳上报（所有异常内部消化，绝不向外抛）</summary>
        public void Beat()
        {
            try
            {
                var config = _getConfig();
                if (config == null || !config.HeartbeatEnabled) return;

                // 1. 选择可写 SFTP 账号：优先 config 显式指定；否则第一个已登录且 Role 非 readonly 的 SFTP 账号
                IFileTransferService transport;
                string profileName;
                string sftpUser;
                if (!TryPickWritableSftp(config, out transport, out profileName, out sftpUser))
                {
                    LogToFile("本周期跳过：无已登录的可写 SFTP 账号（readonly/SMB/S3 不用于心跳）");
                    return;
                }

                // 2. 构造心跳内容
                string machineId = MachineIdStore.GetOrCreate(_appRoot);
                string version = VersionInfo.GetCurrentVersion(_baseDir);
                var payload = new Dictionary<string, object>
                {
                    ["machineId"] = machineId,
                    ["sftpUser"] = sftpUser,
                    ["hostname"] = Environment.MachineName,
                    ["version"] = version,
                    ["grayStatus"] = ResolveGrayStatus(version),
                    ["optIn"] = config.GrayOptIn,
                    ["time"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
                };
                string json = new JavaScriptSerializer().Serialize(payload);

                // 3. 写本地临时文件并上传（一机一文件、覆盖写）
                string tmpFile = Path.Combine(Path.GetTempPath(), "vp_heartbeat_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(tmpFile, json, new UTF8Encoding(false));
                    var result = transport.UploadFile(tmpFile, "heartbeat/" + machineId + ".json");
                    if (result.Success)
                    {
                        _log($"[心跳] 已上报（账号 {profileName}）：版本 {version}，灰度状态 {payload["grayStatus"]}");
                    }
                    else
                    {
                        LogToFile($"上传失败（账号 {profileName}）: {result.Detail}");
                    }
                }
                finally
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogToFile("心跳异常: " + ex.Message);
            }
        }

        /// <summary>选择心跳用的可写 SFTP 会话。SMB/S3/readonly 一律不用</summary>
        private bool TryPickWritableSftp(AppConfig config,
            out IFileTransferService transport, out string profileName, out string sftpUser)
        {
            transport = null; profileName = ""; sftpUser = "";

            // 显式指定优先
            if (!string.IsNullOrWhiteSpace(config.HeartbeatProfileName))
            {
                var named = config.FindProfile(config.HeartbeatProfileName);
                if (named != null && IsWritableSftp(named))
                {
                    var s = _session.GetSession(named.Name);
                    if (s != null && s.IsConnected)
                    {
                        transport = s; profileName = named.Name; sftpUser = named.Username;
                        return true;
                    }
                }
                // 指定的账号不可用：不再回退其他账号（显式配置即运维意图），记日志跳过
                LogToFile("本周期跳过：HeartbeatProfileName 指定的账号「" + config.HeartbeatProfileName +
                    "」未连接或非可写 SFTP");
                return false;
            }

            foreach (var name in _session.ConnectedProfiles)
            {
                var p = config.FindProfile(name);
                if (p == null || !IsWritableSftp(p)) continue;
                var s = _session.GetSession(name);
                if (s == null || !s.IsConnected) continue;
                transport = s; profileName = p.Name; sftpUser = p.Username;
                return true;
            }
            return false;
        }

        private static bool IsWritableSftp(Models.SftpProfile p)
        {
            return !p.IsSmb && !p.IsS3 &&
                !string.Equals(p.Role, "readonly", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 由 gray.state + update.failed 推导上报用的灰度状态：
        /// eligible（命中待升级）| applied（已升级灰度版本）| stay | rollback | failed（熔断）
        /// </summary>
        private string ResolveGrayStatus(string currentVersion)
        {
            try
            {
                // 熔断优先：update.failed 存在且 count>=2
                string failedPath = Path.Combine(_appRoot, "update.failed");
                if (File.Exists(failedPath))
                {
                    int count = 0;
                    foreach (var line in File.ReadAllLines(failedPath))
                    {
                        if (line.StartsWith("count="))
                            int.TryParse(line.Substring(6).Trim(), out count);
                    }
                    if (count >= 2) return "failed";
                }

                var state = GrayState.Read(_appRoot);
                switch (state.Decision)
                {
                    case "upgrade":
                        // 已升级到目标灰度版本 → applied；否则命中待升级 → eligible
                        return string.Equals(currentVersion, state.Target, StringComparison.OrdinalIgnoreCase)
                            ? "applied" : "eligible";
                    case "pending": return "eligible";
                    case "rollback": return "rollback";
                    case "failed": return "failed";
                    default: return "stay";
                }
            }
            catch
            {
                return "stay";
            }
        }

        private void LogToFile(string message)
        {
            try
            {
                string logDir = Path.Combine(_baseDir, "Logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "heartbeat.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
