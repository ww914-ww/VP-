using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Xml;

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private string _appRoot;
        private string _configPath;
        private string _statusPath;
        private string _updateServerPath;

        public MainWindow()
        {
            InitializeComponent();
            _appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _configPath = Path.Combine(_appRoot, "config.xml");
            _statusPath = Path.Combine(_appRoot, "update.status");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 轻量操作（同步，无网络）
                txtStatus.Text = "正在检查...";
                string status = ReadUpdateStatus();
                LoadConfig();

                // 2. 清理残留状态
                if (status == "installing")
                {
                    txtStatus.Text = "正在恢复上次更新...";
                    if (!VerifyCurrentVersion())
                        RecoverFromBackup();
                    WriteUpdateStatus("idle");
                }
                else if (status == "ready")
                {
                    txtStatus.Text = "有待安装的更新，正在安装...";
                    InstallUpdate();
                    return;
                }
                else if (status == "downloading")
                {
                    string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    WriteUpdateStatus("idle");
                }

                // 3. 本地版本就绪 → 直接启动，跳过 SMB
                if (!string.IsNullOrWhiteSpace(_updateServerPath) && VerifyCurrentVersion())
                {
                    txtStatus.Text = "本地版本就绪，直接启动...";
                    StartMainApp();
                    return;
                }

                // 4. SMB 检查更新（6 秒超时）
                if (!string.IsNullOrWhiteSpace(_updateServerPath))
                {
                    txtStatus.Text = "正在检查版本更新...";
                    var checkTask = Task.Run(() => CheckForUpdate());
                    if (await Task.WhenAny(checkTask, Task.Delay(6000)) == checkTask)
                        return; // CheckForUpdate 内部已处理（调用 StartMainApp 或显示更新面板）

                    txtStatus.Text = "更新服务器不可达（网络超时），直接启动...";
                }
                else
                {
                    txtStatus.Text = "未配置更新服务器，直接启动...";
                }
                StartMainApp();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"启动失败: {ex.Message}\n尝试直接启动主程序...";
                StartMainApp();
            }
        }

        // ==================== 更新状态文件 ====================

        private string ReadUpdateStatus()
        {
            try
            {
                if (File.Exists(_statusPath))
                    return File.ReadAllText(_statusPath).Trim();
            }
            catch { }
            return "idle";
        }

        private void WriteUpdateStatus(string status)
        {
            try { File.WriteAllText(_statusPath, status); } catch { }
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
            }
            catch { }
        }

        // ==================== 版本检查 ====================

        private void CheckForUpdate()
        {
            try
            {
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (!File.Exists(versionFile))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到云端版本信息，直接启动...");
                    StartMainApp();
                    return;
                }

                string json = File.ReadAllText(versionFile);
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

                var versions = data["versions"] as System.Collections.Generic.Dictionary<string, object>;
                string cloudDate = "";
                string cloudNote = "";
                if (versions != null && versions.ContainsKey(latestVersion))
                {
                    var verInfo = versions[latestVersion] as System.Collections.Generic.Dictionary<string, object>;
                    if (verInfo != null)
                    {
                        cloudDate = verInfo.ContainsKey("date") ? verInfo["date"]?.ToString() ?? "" : "";
                        cloudNote = verInfo.ContainsKey("note") ? verInfo["note"]?.ToString() ?? "" : "";
                    }
                }

                string localVer = GetLocalLatestVersion();

                if (string.IsNullOrWhiteSpace(latestVersion))
                {
                    StartMainApp();
                    return;
                }

                if (IsNewerVersion(latestVersion, localVer))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtStatus.Text = $"发现新版本 {latestVersion}";
                        txtUpdateInfo.Text = $"版本: {latestVersion}\n日期: {cloudDate}\n\n{cloudNote}\n\n当前本地版本: {localVer}";
                        panelUpdate.Visibility = Visibility.Visible;
                    });
                }
                else
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = $"已是最新版本 ({localVer})，正在启动...");
                    StartMainApp();
                }
            }
            catch
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = "无法连接云端，直接启动...");
                StartMainApp();
            }
        }

        // ==================== 版本号 ====================

        private string GetLocalLatestVersion()
        {
            string versionsDir = Path.Combine(_appRoot, "versions");
            if (!Directory.Exists(versionsDir)) return "0.0.0";

            var dirs = Directory.GetDirectories(versionsDir);
            Version best = new Version(0, 0, 0);
            foreach (var dir in dirs)
            {
                string dirName = Path.GetFileName(dir);
                if (Version.TryParse(dirName, out Version ver) && ver > best)
                    best = ver;
            }
            return $"{best.Major}.{best.Minor}.{best.Build}";
        }

        private bool IsNewerVersion(string cloudVer, string localVer)
        {
            try
            {
                var cv = new Version(cloudVer);
                var lv = new Version(localVer);
                return cv > lv;
            }
            catch
            {
                return string.Compare(cloudVer, localVer, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }

        // ==================== 下载和安装 ====================

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            panelUpdate.Visibility = Visibility.Collapsed;
            btnUpdate.IsEnabled = false;
            btnSkip.IsEnabled = false;

            await Task.Run(() => DownloadAndInstall());
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            StartMainApp();
        }

        private void DownloadAndInstall()
        {
            try
            {
                string latestVersion = "";

                // 获取最新版本号
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (File.Exists(versionFile))
                {
                    string json = File.ReadAllText(versionFile);
                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<dynamic>(json);
                    latestVersion = data["latest"]?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(latestVersion))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到最新版本号");
                    return;
                }

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "正在下载更新...";
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = true;
                    txtProgress.Text = $"正在下载 v{latestVersion} 更新文件...";
                });

                WriteUpdateStatus("downloading");

                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                string remoteVerDir = Path.Combine(_updateServerPath, latestVersion);
                if (!Directory.Exists(remoteVerDir) ||
                    !File.Exists(Path.Combine(remoteVerDir, "MoveImageForm.exe")))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "更新下载失败: 云端版本文件不完整");
                    WriteUpdateStatus("idle");
                    return;
                }

                // SMB 复制
                CopyDirectory(remoteVerDir, tempDir);

                if (!File.Exists(Path.Combine(tempDir, "MoveImageForm.exe")))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "更新下载失败: 下载后文件不完整");
                    WriteUpdateStatus("idle");
                    return;
                }

                WriteUpdateStatus("ready");

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "下载完成，正在安装更新...";
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 100;
                });

                InstallUpdate();
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = $"更新失败: {ex.Message}");
                WriteUpdateStatus("idle");
            }
        }

        private void InstallUpdate()
        {
            try
            {
                WriteUpdateStatus("installing");

                // 获取版本号
                string latestVersion = "";
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (File.Exists(versionFile))
                {
                    string json = File.ReadAllText(versionFile);
                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<dynamic>(json);
                    latestVersion = data["latest"]?.ToString() ?? "";
                }

                string batPath = Path.Combine(_appRoot, "update.bat");
                string batContent = GenerateUpdateBat(latestVersion);
                // 使用系统默认编码（中文 Windows 为 GBK），避免 cmd.exe 解析中文路径乱码
                File.WriteAllText(batPath, batContent, System.Text.Encoding.Default);

                Dispatcher.InvokeAsync(() => txtStatus.Text = "即将重启完成更新...");

                Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        Process.Start(new ProcessStartInfo(batPath)
                        {
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WorkingDirectory = _appRoot
                        });
                        Application.Current.Shutdown();
                    });
                });
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = $"安装更新失败: {ex.Message}");
                WriteUpdateStatus("idle");
            }
        }

        private string GenerateUpdateBat(string version)
        {
            string versionsDir = Path.Combine(_appRoot, "versions");
            string newVerDir = Path.Combine(versionsDir, version);
            string tempDir = Path.Combine(versionsDir, ".temp");
            string backupDir = Path.Combine(_appRoot, "backup");
            string launcherPath = Path.Combine(_appRoot, "Launcher.exe");

            return $@"@echo off
chcp 65001 >nul
echo 正在更新 VP运维工具 到版本 {version}...
echo.
echo 请勿关闭此窗口，更新完成后将自动启动程序...
echo.

timeout /t 2 /nobreak >nul

taskkill /f /im MoveImageForm.exe >nul 2>&1

if exist ""{newVerDir}"" (
    if not exist ""{backupDir}"" mkdir ""{backupDir}""
    robocopy ""{newVerDir}"" ""{backupDir}\{version}.bak"" /E /MOVE >nul 2>&1
)

robocopy ""{tempDir}"" ""{newVerDir}"" /E /MOVE >nul 2>&1

rd /s /q ""{tempDir}"" 2>nul

echo idle> ""{Path.Combine(_appRoot, "update.status")}""

if exist ""{launcherPath}"" (
    start """" ""{launcherPath}""
) else (
    start """" ""{Path.Combine(newVerDir, "MoveImageForm.exe")}""
)

del ""%~f0"" & exit
";
        }

        // ==================== 崩溃恢复 ====================

        private bool VerifyCurrentVersion()
        {
            string localVer = GetLocalLatestVersion();
            string verDir = Path.Combine(_appRoot, "versions", localVer);
            return File.Exists(Path.Combine(verDir, "MoveImageForm.exe"));
        }

        private void RecoverFromBackup()
        {
            string backupDir = Path.Combine(_appRoot, "backup");
            if (!Directory.Exists(backupDir)) return;

            var dirs = Directory.GetDirectories(backupDir);
            Version best = new Version(0, 0, 0);
            string bestDir = "";
            foreach (var dir in dirs)
            {
                string dirName = Path.GetFileName(dir);
                string verStr = dirName.Replace(".bak", "");
                if (Version.TryParse(verStr, out Version ver) && ver > best)
                {
                    best = ver;
                    bestDir = dir;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestDir))
            {
                string versionsDir = Path.Combine(_appRoot, "versions");
                string restoreDir = Path.Combine(versionsDir, $"{best.Major}.{best.Minor}.{best.Build}");
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

        // ==================== 启动主程序 ====================

        private void StartMainApp()
        {
            string localVer = GetLocalLatestVersion();
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
                    $"versions dirs = [{string.Join(", ", Array.ConvertAll(dirs, d => Path.GetFileName(d)))}]\r\n" +
                    $"exePath bytes = [{string.Join(" ", System.Text.Encoding.UTF8.GetBytes(exePath).Select(b => b.ToString("X2")))}\r\n" +
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
