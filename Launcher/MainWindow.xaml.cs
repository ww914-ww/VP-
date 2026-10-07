using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private string _appRoot;
        private string _configPath;
        private string _updateServerPath;
        private bool _grayOptIn;
        private string _machineId = "";
        private UpdateCheckCore.CheckResult _lastCheck;

        public MainWindow()
        {
            InitializeComponent();
            _appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _configPath = Path.Combine(_appRoot, "config.xml");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 轻量操作（同步，无网络）
                txtStatus.Text = "正在检查...";
                string status = UpdateInstaller.ReadStatus(_appRoot);
                LoadConfig();
                _machineId = MachineIdStore.GetOrCreate(_appRoot);

                // 2. 清理/恢复上次未完成的更新状态
                if (status == "installing")
                {
                    // --apply 中途被杀（断电等）：本地无可用版本时从备份恢复
                    txtStatus.Text = "正在恢复上次更新...";
                    if (VersionDirs.GetLocalLatestVersion(_appRoot) == "0.0.0")
                        RecoverFromBackup();
                    UpdateInstaller.WriteStatus(_appRoot, "idle");
                }
                else if (status == "ready")
                {
                    // 下载完成但 --apply 未执行（启动安装前被杀）：直接继续安装
                    string pendingVersion = ReadPendingVersion();
                    if (!string.IsNullOrEmpty(pendingVersion) &&
                        Directory.Exists(Path.Combine(_appRoot, "versions", ".temp")))
                    {
                        txtStatus.Text = "继续安装未完成的更新...";
                        StartApply(pendingVersion);
                        return;
                    }
                    UpdateInstaller.WriteStatus(_appRoot, "idle");
                }
                else if (status == "downloading")
                {
                    // 下载中途被杀：清理临时目录，本次重新检查
                    DeleteTempDir();
                    UpdateInstaller.WriteStatus(_appRoot, "idle");
                }
                else if (status == "done" || status == "failed")
                {
                    // --apply 终态：归位后继续正常检查流程
                    UpdateInstaller.WriteStatus(_appRoot, "idle");
                }

                // 3. 启动时必查更新（6 秒超时；超时/异常直接启动本地版本，绝不阻塞开机自启链路）
                if (!string.IsNullOrWhiteSpace(_updateServerPath))
                {
                    txtStatus.Text = "正在检查版本更新...";
                    var checkTask = Task.Run(() => CheckForUpdate());
                    if (await Task.WhenAny(checkTask, Task.Delay(6000)) == checkTask)
                        return; // CheckForUpdate 内部已处理（StartMainApp / 显示面板 / 自动降级）

                    txtStatus.Text = "更新服务器不可达（网络超时），直接启动本地版本...";
                }
                else
                {
                    // 未配置更新服务器：写 gray.state（stay）供心跳上报
                    UpdateInstaller.WriteGrayState(_appRoot, "stay",
                        VersionDirs.GetLocalLatestVersion(_appRoot), "latest", _grayOptIn, 0);
                    txtStatus.Text = "未配置更新服务器，直接启动...";
                }
                StartMainApp();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"启动检查异常: {ex.Message}\n尝试直接启动主程序...";
                StartMainApp();
            }
        }

        // ==================== 配置 ====================

        private void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;

            try
            {
                var doc = new XmlDocument();
                doc.Load(_configPath);
                var node = doc.SelectSingleNode("//UpdateServerPath");
                _updateServerPath = node?.InnerText?.Trim() ?? "";

                // 也支持旧版 CloudPath（向后兼容）
                if (string.IsNullOrEmpty(_updateServerPath))
                {
                    var cloudNode = doc.SelectSingleNode("//CloudPath");
                    _updateServerPath = cloudNode?.InnerText?.Trim() ?? "";
                }

                // 可选灰度：本机是否自愿参与灰度测试（默认 false 不参与）
                var optInNode = doc.SelectSingleNode("//GrayOptIn");
                bool optIn;
                _grayOptIn = optInNode != null &&
                    bool.TryParse(optInNode.InnerText?.Trim(), out optIn) && optIn;
            }
            catch { }
        }

        // ==================== 版本检查 ====================

        private void CheckForUpdate()
        {
            var result = UpdateCheckCore.Evaluate(_updateServerPath, _machineId, _grayOptIn, _appRoot);
            _lastCheck = result;

            // 写 gray.state 判定轨迹（检查失败时保留上次记录，不覆盖）
            if (result.Action != UpdateCheckCore.UpdateAction.Error)
            {
                string decision;
                switch (result.Action)
                {
                    case UpdateCheckCore.UpdateAction.Upgrade: decision = "upgrade"; break;
                    case UpdateCheckCore.UpdateAction.Downgrade: decision = "rollback"; break;
                    case UpdateCheckCore.UpdateAction.Fused: decision = "failed"; break;
                    default: decision = "stay"; break;
                }
                UpdateInstaller.WriteGrayState(_appRoot, decision,
                    result.TargetVersion, result.TargetSource, _grayOptIn, result.PolicyPercent);
            }

            switch (result.Action)
            {
                case UpdateCheckCore.UpdateAction.None:
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"已是最新版本 ({result.LocalVersion})，正在启动...");
                    StartMainApp();
                    break;

                case UpdateCheckCore.UpdateAction.Upgrade:
                    Dispatcher.InvokeAsync(() => ShowUpdatePanel(result));
                    break;

                case UpdateCheckCore.UpdateAction.Downgrade:
                    // 灰度收敛/摘除/回滚指令：自动降级，无需用户确认
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"版本回退: v{result.LocalVersion} → v{result.TargetVersion}（灰度策略调整），正在自动处理...");
                    Task.Run(() => DownloadAndInstall(result.TargetVersion));
                    break;

                case UpdateCheckCore.UpdateAction.Fused:
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"版本 v{result.TargetVersion} 已暂停更新（安装多次失败），请联系运维。\n正在启动本地版本...");
                    StartMainApp();
                    break;

                case UpdateCheckCore.UpdateAction.Dismissed:
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在启动...");
                    StartMainApp();
                    break;

                default: // Error
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"无法检查更新（{result.Error}），直接启动本地版本...");
                    StartMainApp();
                    break;
            }
        }

        private void ShowUpdatePanel(UpdateCheckCore.CheckResult result)
        {
            bool isGray = result.TargetSource == "gray";
            txtStatus.Text = isGray
                ? $"发现灰度版本 {result.TargetVersion}"
                : $"发现新版本 {result.TargetVersion}";

            string header = isGray
                ? $"【灰度更新】版本: {result.TargetVersion}（灰度 {result.PolicyPercent}%，本机已报名参与灰度测试）"
                : $"版本: {result.TargetVersion}";
            txtUpdateInfo.Text =
                $"{header}\n日期: {result.CloudDate}\n\n{result.CloudNote}\n\n当前本地版本: {result.LocalVersion}";
            panelUpdate.Visibility = Visibility.Visible;
        }

        // ==================== 下载和安装 ====================

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            panelUpdate.Visibility = Visibility.Collapsed;
            btnUpdate.IsEnabled = false;
            btnSkip.IsEnabled = false;

            string target = _lastCheck?.TargetVersion ?? "";
            if (string.IsNullOrEmpty(target))
            {
                txtStatus.Text = "更新目标缺失，请重启 Launcher 重试";
                return;
            }
            await Task.Run(() => DownloadAndInstall(target));
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            // 记录跳过的版本：该版本不再重复弹窗（云端目标版本变化后恢复提示）
            string target = _lastCheck?.TargetVersion ?? "";
            if (!string.IsNullOrEmpty(target))
                UpdateInstaller.WriteDismissedVersion(_appRoot, target);
            StartMainApp();
        }

        private void DownloadAndInstall(string version)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "正在下载更新...";
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = true;
                    txtProgress.Text = $"正在下载 v{version} 更新文件...";
                });

                UpdateInstaller.WriteStatus(_appRoot, "downloading");
                UpdateInstaller.Log(_appRoot, "开始下载版本 " + version + "（来源: " + _updateServerPath + "）");

                DeleteTempDir();
                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                Directory.CreateDirectory(tempDir);

                string remoteVerDir = Path.Combine(_updateServerPath, version);
                if (!Directory.Exists(remoteVerDir))
                {
                    OnDownloadFailed(version, "云端版本目录不存在: " + remoteVerDir);
                    return;
                }

                // SMB 复制（UNC 直读，不做 net use 挂载，规避 1219 多身份冲突）
                CopyDirectory(remoteVerDir, tempDir);

                // 下载完整性校验：必须携带 manifest 且逐文件通过，杜绝半成品安装
                var verify = ManifestVerifier.VerifyDirectory(tempDir);
                if (!verify.Success)
                {
                    OnDownloadFailed(version, verify.Error);
                    return;
                }
                UpdateInstaller.Log(_appRoot, "下载完成，manifest 校验通过（" + verify.FileCount + " 个文件）");

                UpdateInstaller.WriteStatus(_appRoot, "ready");
                WritePendingVersion(version);

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "下载完成，正在安装更新...";
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 100;
                });

                // 启动 --apply 无界面安装模式（同步代码执行、全程 update.log、失败自动回滚）
                StartApply(version);
            }
            catch (Exception ex)
            {
                OnDownloadFailed(version, ex.Message);
            }
        }

        private void OnDownloadFailed(string version, string reason)
        {
            UpdateInstaller.Log(_appRoot, "下载失败: " + reason);
            DeleteTempDir();
            UpdateInstaller.WriteStatus(_appRoot, "idle");
            int count = UpdateInstaller.IncrementFailedCount(_appRoot, version, "下载/校验失败: " + reason);

            Dispatcher.InvokeAsync(() =>
            {
                progressBar.Visibility = Visibility.Collapsed;
                txtProgress.Text = "";
                if (count >= 2)
                    txtStatus.Text = $"版本 v{version} 更新多次失败，已暂停该版本的更新。\n请检查更新服务器或联系运维。正在启动本地版本...";
                else
                    txtStatus.Text = $"更新下载失败: {reason}\n正在启动本地版本...";
            });
            // 失败不阻塞使用：稍作停顿让用户看到提示后启动本地版本
            Task.Delay(2500).ContinueWith(_ => StartMainApp());
        }

        /// <summary>启动 Launcher --apply 安装模式并退出本实例</summary>
        private void StartApply(string version)
        {
            try
            {
                string launcherPath = Path.Combine(_appRoot, "Launcher.exe");
                Process.Start(new ProcessStartInfo(launcherPath, "--apply \"" + version + "\"")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _appRoot
                });
                UpdateInstaller.Log(_appRoot, "已启动安装模式（--apply " + version + "），Launcher 即将退出");
            }
            catch (Exception ex)
            {
                UpdateInstaller.Log(_appRoot, "启动安装模式失败: " + ex.Message);
                UpdateInstaller.WriteStatus(_appRoot, "idle");
                int count = UpdateInstaller.IncrementFailedCount(_appRoot, version, "启动安装失败: " + ex.Message);
                if (count >= 2)
                {
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"版本 v{version} 更新多次失败已熔断，正在启动本地版本...");
                }
                StartMainApp();
                return;
            }

            Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
        }

        // ==================== 崩溃恢复 ====================

        private void RecoverFromBackup()
        {
            string backupDir = Path.Combine(_appRoot, "backup");
            if (!Directory.Exists(backupDir)) return;

            Version best = new Version(0, 0, 0);
            string bestDir = "";
            foreach (var dir in Directory.GetDirectories(backupDir))
            {
                string verStr = Path.GetFileName(dir).Replace(".bak", "");
                Version ver;
                if (Version.TryParse(verStr, out ver) && ver > best)
                {
                    best = ver;
                    bestDir = dir;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestDir))
            {
                string restoreDir = Path.Combine(_appRoot, "versions",
                    string.Format("{0}.{1}.{2}", best.Major, best.Minor, best.Build));
                if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, true);
                CopyDirectory(bestDir, restoreDir);

                Dispatcher.InvokeAsync(() => txtStatus.Text = "已从备份恢复，正在启动...");
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private void DeleteTempDir()
        {
            try
            {
                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
            catch { }
        }

        // ==================== 待安装版本记录（ready 状态恢复用） ====================

        private string PendingVersionPath => Path.Combine(_appRoot, "update.pending");

        private void WritePendingVersion(string version)
        {
            try { File.WriteAllText(PendingVersionPath, version ?? ""); } catch { }
        }

        private string ReadPendingVersion()
        {
            try
            {
                if (File.Exists(PendingVersionPath))
                    return File.ReadAllText(PendingVersionPath).Trim();
            }
            catch { }
            return "";
        }

        // ==================== 启动主程序 ====================

        private void StartMainApp()
        {
            string localVer = VersionDirs.GetLocalLatestVersion(_appRoot);
            string exePath = Path.Combine(_appRoot, "versions", localVer, "MoveImageForm.exe");

            // 写诊断日志
            try
            {
                string logPath = Path.Combine(_appRoot, "launcher_debug.log");
                string[] dirs = Directory.Exists(Path.Combine(_appRoot, "versions"))
                    ? Directory.GetDirectories(Path.Combine(_appRoot, "versions"))
                    : new string[0];
                File.WriteAllText(logPath,
                    $"_appRoot = [{_appRoot}]\r\n" +
                    $"localVer = [{localVer}]\r\n" +
                    $"exePath = [{exePath}]\r\n" +
                    $"File.Exists(exePath) = {File.Exists(exePath)}\r\n" +
                    $"grayOptIn = {_grayOptIn}\r\n" +
                    $"machineId = [{_machineId}]\r\n" +
                    $"versions dirs = [{string.Join(", ", Array.ConvertAll(dirs, d => Path.GetFileName(d)))}]\r\n" +
                    $"time = {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { /* 诊断日志失败不影响主流程 */ }

            if (!File.Exists(exePath))
            {
                exePath = Path.Combine(_appRoot, "MoveImageForm.exe");
                if (!File.Exists(exePath))
                {
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"错误: 找不到主程序文件。\n_appRoot: {_appRoot}\nlocalVer: {localVer}\n搜索路径: {exePath}");
                    return;
                }
            }

            Dispatcher.InvokeAsync(() => txtStatus.Text = "正在启动主程序...");

            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });

            Task.Delay(500).ContinueWith(_ =>
            {
                Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
            });
        }
    }
}
