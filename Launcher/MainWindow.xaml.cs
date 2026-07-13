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

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private string _appRoot;
        private string _configPath;
        private string _statusPath;
        private string _cloudPath;
        private string _cloudUser;
        private string _cloudPassword;

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
                    {
                        RecoverFromBackup();
                    }
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

                // 2. 读 config.xml
                Dispatcher.InvokeAsync(() => txtStatus.Text = "正在读取配置...");
                LoadConfig();

                // 3. 挂载 SMB
                if (!string.IsNullOrWhiteSpace(_cloudPath))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在连接云端...");
                    MountSMB();
                }

                // 4. 检查更新
                if (!string.IsNullOrWhiteSpace(_cloudPath) && Directory.Exists("Y:\\"))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在检查版本更新...");
                    CheckForUpdate();
                }
                else
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "无法连接云端，直接启动...");
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

            var doc = new XmlDocument();
            doc.Load(_configPath);

            _cloudPath = doc.SelectSingleNode("//CloudPath")?.InnerText ?? "";
            _cloudUser = doc.SelectSingleNode("//CloudUser")?.InnerText ?? "";
            _cloudPassword = doc.SelectSingleNode("//CloudPassword")?.InnerText ?? "";
        }

        private void MountSMB()
        {
            try
            {
                string letter = "Y:";
                // 先断开
                var psi1 = new ProcessStartInfo("net", $"use {letter} /delete /y")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var p1 = Process.Start(psi1);
                p1?.WaitForExit(3000);

                // 再连接
                string userPass = "";
                if (!string.IsNullOrWhiteSpace(_cloudUser))
                {
                    userPass = $" /user:{_cloudUser}";
                    if (!string.IsNullOrWhiteSpace(_cloudPassword))
                        userPass += $" {_cloudPassword}";
                }

                var psi2 = new ProcessStartInfo("net", $"use {letter} \"{_cloudPath}\"{userPass}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var p2 = Process.Start(psi2);
                p2?.WaitForExit(10000);
            }
            catch { }
        }

        private void CheckForUpdate()
        {
            try
            {
                string jsonPath = Path.Combine("Y:", "version.json");
                if (!File.Exists(jsonPath))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到云端版本信息，直接启动...");
                    StartMainApp();
                    return;
                }

                string json = File.ReadAllText(jsonPath);
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
            }
            catch
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = "版本检查失败，直接启动...");
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

                string json = File.ReadAllText(Path.Combine("Y:", "version.json"));
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                Dispatcher.InvokeAsync(() => txtProgress.Text = "正在从云端复制文件...");
                CopyDirectory(Path.Combine("Y:", latestVersion), tempDir);

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

        private void InstallUpdate()
        {
            try
            {
                WriteUpdateStatus("installing");

                string json = File.ReadAllText(Path.Combine("Y:", "version.json"));
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

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

        private void StartMainApp()
        {
            string localVer = GetLocalLatestVersion();
            string versionsDir = Path.Combine(_appRoot, "versions");
            string exePath = Path.Combine(versionsDir, localVer, "MoveImageForm.exe");

            if (!File.Exists(exePath))
            {
                // 开发环境：尝试从当前目录启动
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
