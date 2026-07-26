using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Xml;
using Renci.SshNet;

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private string _appRoot;
        private string _configPath;
        private string _statusPath;
        private string _sftpHost;
        private int _sftpPort;
        private string _sftpUser;
        private string _sftpPassword;
        private string _sftpRemoteRoot;

        public MainWindow()
        {
            InitializeComponent();
            _appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _configPath = Path.Combine(_appRoot, "config.xml");
            _statusPath = Path.Combine(_appRoot, "update.status");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => StartupFlow());
        }

        private void StartupFlow()
        {
            try
            {
                // 1. 读 update.status — 崩溃恢复
                Dispatcher.InvokeAsync(() => txtStatus.Text = "正在检查更新状态...");
                string status = ReadUpdateStatus();

                if (status == "installing")
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在恢复上次更新...");
                    if (!VerifyCurrentVersion())
                        RecoverFromBackup();
                    WriteUpdateStatus("idle");
                }
                else if (status == "ready")
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "有待安装的更新，正在安装...");
                    InstallUpdate();
                    return;
                }
                else if (status == "downloading")
                {
                    string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    WriteUpdateStatus("idle");
                }

                // 2. 读 config.xml — 获取 SFTP 连接信息
                Dispatcher.InvokeAsync(() => txtStatus.Text = "正在读取配置...");
                LoadConfig();

                // 3. 通过 SFTP 检查更新
                if (!string.IsNullOrWhiteSpace(_sftpHost) && !string.IsNullOrWhiteSpace(_sftpUser))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在连接云端检查版本更新...");
                    CheckForUpdateSftp();
                }
                else
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未配置 SFTP 连接，直接启动...");
                    StartMainApp();
                }
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                    txtStatus.Text = $"启动失败: {ex.Message}\n尝试直接启动主程序...");
                StartMainApp();
            }
        }

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
            try
            {
                File.WriteAllText(_statusPath, status);
            }
            catch { }
        }

        private void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;

            try
            {
                var doc = new XmlDocument();
                doc.Load(_configPath);

                // 优先读取 SftpProfiles 中的第一个 admin 账号
                var profileNodes = doc.SelectNodes("//SftpProfiles/Profile");
                if (profileNodes != null && profileNodes.Count > 0)
                {
                    // 优先选择 admin 角色
                    XmlNode selectedNode = null;
                    foreach (XmlNode node in profileNodes)
                    {
                        string role = node.SelectSingleNode("Role")?.InnerText?.Trim() ?? "";
                        if (role == "admin")
                        {
                            selectedNode = node;
                            break;
                        }
                    }
                    // 没有 admin，用第一个
                    if (selectedNode == null)
                        selectedNode = profileNodes[0];

                    _sftpHost = selectedNode.SelectSingleNode("Host")?.InnerText?.Trim() ?? "";
                    _sftpPort = int.TryParse(selectedNode.SelectSingleNode("Port")?.InnerText?.Trim(), out int port) ? port : 22;
                    _sftpUser = selectedNode.SelectSingleNode("Username")?.InnerText?.Trim() ?? "";
                    _sftpPassword = selectedNode.SelectSingleNode("Password")?.InnerText?.Trim() ?? "";
                    _sftpRemoteRoot = selectedNode.SelectSingleNode("RemoteRoot")?.InnerText?.Trim() ?? "/";
                }
                else
                {
                    // 回退: 旧版 CloudPath 格式
                    string cloudPath = doc.SelectSingleNode("//CloudPath")?.InnerText ?? "";
                    _sftpHost = cloudPath;
                    _sftpUser = doc.SelectSingleNode("//CloudUser")?.InnerText ?? "";
                    _sftpPassword = doc.SelectSingleNode("//CloudPassword")?.InnerText ?? "";
                }
            }
            catch { }
        }

        private void CheckForUpdateSftp()
        {
            try
            {
                using (var sftp = new SftpClient(_sftpHost, _sftpPort, _sftpUser, _sftpPassword))
                {
                    sftp.Connect();

                    string jsonPath = _sftpRemoteRoot.TrimEnd('/') + "/versions/version.json";
                    if (!sftp.Exists(jsonPath))
                    {
                        Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到云端版本信息，直接启动...");
                        StartMainApp();
                        return;
                    }

                    string json;
                    using (var ms = new MemoryStream())
                    {
                        sftp.DownloadFile(jsonPath, ms);
                        ms.Position = 0;
                        using (var reader = new StreamReader(ms, System.Text.Encoding.UTF8))
                        {
                            json = reader.ReadToEnd();
                        }
                    }

                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<dynamic>(json);
                    string latestVersion = data["latest"]?.ToString() ?? "";

                    var versions = data["versions"] as Dictionary<string, object>;
                    string cloudDate = "";
                    string cloudNote = "";
                    if (versions != null && versions.ContainsKey(latestVersion))
                    {
                        var verInfo = versions[latestVersion] as Dictionary<string, object>;
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

                    sftp.Disconnect();
                }
            }
            catch
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = "无法连接云端，直接启动...");
                StartMainApp();
            }
        }

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
                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "正在下载更新...";
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = true;
                });

                WriteUpdateStatus("downloading");

                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                using (var sftp = new SftpClient(_sftpHost, _sftpPort, _sftpUser, _sftpPassword))
                {
                    sftp.Connect();

                    string jsonPath = _sftpRemoteRoot.TrimEnd('/') + "/versions/version.json";
                    string json;
                    using (var ms = new MemoryStream())
                    {
                        sftp.DownloadFile(jsonPath, ms);
                        ms.Position = 0;
                        using (var reader = new StreamReader(ms, System.Text.Encoding.UTF8))
                        {
                            json = reader.ReadToEnd();
                        }
                    }

                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<dynamic>(json);
                    string latestVersion = data["latest"]?.ToString() ?? "";

                    Dispatcher.InvokeAsync(() => txtProgress.Text = "正在通过 SFTP 下载更新文件...");

                    // Download version directory from SFTP
                    string remoteVerDir = _sftpRemoteRoot.TrimEnd('/') + "/versions/" + latestVersion;
                    DownloadDirectoryFromSftp(sftp, remoteVerDir, tempDir);

                    sftp.Disconnect();
                }

                if (!File.Exists(Path.Combine(tempDir, "MoveImageForm.exe")))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "更新下载失败: 版本文件不完整");
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

        private void DownloadDirectoryFromSftp(SftpClient sftp, string remoteDir, string localDir)
        {
            var items = sftp.ListDirectory(remoteDir);
            foreach (var item in items)
            {
                if (item.Name == "." || item.Name == "..") continue;

                string remotePath = remoteDir + "/" + item.Name;
                string localPath = Path.Combine(localDir, item.Name);

                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(localPath);
                    DownloadDirectoryFromSftp(sftp, remotePath, localPath);
                }
                else
                {
                    using (var fileStream = File.Create(localPath))
                    {
                        sftp.DownloadFile(remotePath, fileStream);
                    }
                }
            }
        }

        private void InstallUpdate()
        {
            try
            {
                WriteUpdateStatus("installing");

                string latestVersion = "";
                using (var sftp = new SftpClient(_sftpHost, _sftpPort, _sftpUser, _sftpPassword))
                {
                    sftp.Connect();
                    string jsonPath = _sftpRemoteRoot.TrimEnd('/') + "/versions/version.json";
                    using (var ms = new MemoryStream())
                    {
                        sftp.DownloadFile(jsonPath, ms);
                        ms.Position = 0;
                        using (var reader = new StreamReader(ms, System.Text.Encoding.UTF8))
                        {
                            string json = reader.ReadToEnd();
                            var jss = new JavaScriptSerializer();
                            var data = jss.Deserialize<dynamic>(json);
                            latestVersion = data["latest"]?.ToString() ?? "";
                        }
                    }
                    sftp.Disconnect();
                }

                string batPath = Path.Combine(_appRoot, "update.bat");
                string batContent = GenerateUpdateBat(latestVersion);
                File.WriteAllText(batPath, batContent, System.Text.Encoding.UTF8);

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "即将重启完成更新...";
                });

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
echo 正在更新 poco运维工具 到版本 {version}...

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

        private bool VerifyCurrentVersion()
        {
            string localVer = GetLocalLatestVersion();
            string versionsDir = Path.Combine(_appRoot, "versions");
            string verDir = Path.Combine(versionsDir, localVer);
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

                Dispatcher.InvokeAsync(() =>
                    txtStatus.Text = "已从备份恢复，正在启动...");
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
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

        private void StartMainApp()
        {
            string localVer = GetLocalLatestVersion();
            string versionsDir = Path.Combine(_appRoot, "versions");
            string exePath = Path.Combine(versionsDir, localVer, "MoveImageForm.exe");

            if (!File.Exists(exePath))
            {
                exePath = Path.Combine(_appRoot, "MoveImageForm.exe");
                if (!File.Exists(exePath))
                {
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"错误: 找不到主程序文件。\n搜索路径: {exePath}");
                    return;
                }
            }

            Dispatcher.InvokeAsync(() =>
                txtStatus.Text = "正在启动主程序...");

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
