using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;

namespace MoveImageForm
{
    public partial class MainWindow : Window
    {
        private AppConfig _config;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning;
        private DateTime _lastMoveTime;
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        public MainWindow()
        {
            InitializeComponent();
            InitNotifyIcon();
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            // 默认使用系统的应用程序图标，这里也可以替换为你自己的ico文件
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Text = "图片搬运工具 (后台运行中)";
            _notifyIcon.Visible = true;

            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            
            var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主界面");
            showItem.Click += (s, e) => 
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };
            
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("完全退出");
            exitItem.Click += (s, e) =>
            {
                UpdateConfigFromUI();
                SaveConfig();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            };

            contextMenu.Items.Add(showItem);
            contextMenu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            UpdateUIFromConfig();
            
            // 默认打开时自动开始搬运（如果配置了至少一组有效的路径）
            bool pair1Valid = !string.IsNullOrWhiteSpace(_config.SourcePath) && !string.IsNullOrWhiteSpace(_config.DestPath) && Directory.Exists(_config.SourcePath);
            bool pair2Valid = !string.IsNullOrWhiteSpace(_config.SourcePath2) && !string.IsNullOrWhiteSpace(_config.DestPath2) && Directory.Exists(_config.SourcePath2);

            if (pair1Valid || pair2Valid)
            {
                StartMoving();
            }
            else
            {
                Log("未使用默认自动启动功能：没有找到完全配置好且存在的监控文件夹。");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 阻止默认关闭行为，改为隐藏到系统托盘
            e.Cancel = true;
            this.Hide();
            
            // 可以在此处弹出气泡提示告诉用户程序还在运行
            _notifyIcon.ShowBalloonTip(2000, "提示", "程序已最小化到系统托盘并在后台继续搬运。", System.Windows.Forms.ToolTipIcon.Info);

            UpdateConfigFromUI();
            SaveConfig();
            // 注意：这里去掉了 StopMoving()，因为我们要它在后台继续运行
        }

        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择监控文件夹";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtSourcePath.Text = dialog.SelectedPath;
                }
            }
        }

        private void BtnBrowseDest_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择目标文件夹1";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtDestPath.Text = dialog.SelectedPath;
                }
            }
        }

        private void BtnBrowseSource2_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择监控文件夹2";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtSourcePath2.Text = dialog.SelectedPath;
                }
            }
        }

        private void BtnBrowseDest2_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择目标文件夹2";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtDestPath2.Text = dialog.SelectedPath;
                }
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            StartMoving();
        }

        private void StartMoving()
        {
            bool pair1Valid = !string.IsNullOrWhiteSpace(txtSourcePath.Text) && !string.IsNullOrWhiteSpace(txtDestPath.Text);
            bool pair2Valid = !string.IsNullOrWhiteSpace(txtSourcePath2.Text) && !string.IsNullOrWhiteSpace(txtDestPath2.Text);
            
            if (!pair1Valid && !pair2Valid)
            {
                MessageBox.Show("请至少设置一组完整的监控文件夹和目标文件夹！");
                return;
            }

            bool pair1Exists = pair1Valid && Directory.Exists(txtSourcePath.Text);
            bool pair2Exists = pair2Valid && Directory.Exists(txtSourcePath2.Text);

            if (!pair1Exists && !pair2Exists)
            {
                MessageBox.Show("配置的监控文件夹不存在！");
                return;
            }

            UpdateConfigFromUI();
            SaveConfig();

            _isRunning = true;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            SetUIEnabled(false);

            _cancellationTokenSource = new CancellationTokenSource();
            _lastMoveTime = DateTime.Now;

            Log("开始监控...");
            Task.Run(() => MonitorLoop(_cancellationTokenSource.Token));
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopMoving();
        }

        private void StopMoving()
        {
            if (_isRunning)
            {
                _isRunning = false;
                _cancellationTokenSource?.Cancel();

                Dispatcher.Invoke(() =>
                {
                    btnStart.IsEnabled = true;
                    btnStop.IsEnabled = false;
                    SetUIEnabled(true);
                    Log("停止监控。");
                });
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            txtSourcePath.IsEnabled = enabled;
            txtDestPath.IsEnabled = enabled;
            txtSourcePath2.IsEnabled = enabled;
            txtDestPath2.IsEnabled = enabled;
            rbCutMode.IsEnabled = enabled;
            rbCopyMode.IsEnabled = enabled;
            chkTimeRule.IsEnabled = enabled;
            txtTimeInterval.IsEnabled = enabled;
            chkSizeRule.IsEnabled = enabled;
            txtSizeLimit.IsEnabled = enabled;
            chkCountRule.IsEnabled = enabled;
            txtCountLimit.IsEnabled = enabled;
            chkEmptyFolderRule.IsEnabled = enabled;
            txtEmptyFolderHours.IsEnabled = enabled;
            // buttons for browse are in grid, hard to access by name if not named, but we can just disable the textboxes
        }

        private static readonly object _logLock = new object();

        private void Log(string message)
        {
            string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logMessage = $"[{timeStamp}] {message}";

            // 1. 在界面上显示（限制最多显示1000条，防止内存溢出）
            Dispatcher.InvokeAsync(() =>
            {
                lstLog.Items.Add(logMessage);
                if (lstLog.Items.Count > 1000)
                {
                    lstLog.Items.RemoveAt(0);
                }
                lstLog.ScrollIntoView(lstLog.Items[lstLog.Items.Count - 1]);
            });

            // 2. 写入本地日志文件
            Task.Run(() =>
            {
                try
                {
                    lock (_logLock)
                    {
                        string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                        if (!Directory.Exists(logDir))
                        {
                            Directory.CreateDirectory(logDir);
                        }
                        
                        // 每天生成一个日志文件
                        string logFile = Path.Combine(logDir, $"Log_{DateTime.Now:yyyy-MM-dd}.txt");
                        File.AppendAllText(logFile, logMessage + Environment.NewLine);
                    }
                }
                catch
                {
                    // 如果写文件失败（如权限问题），不抛出异常以免影响主程序运行
                }
            });
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool timeTriggered = _config.EnableTimeRule && (DateTime.Now - _lastMoveTime).TotalSeconds >= _config.TimeIntervalSeconds;
                    bool movedAny = false;

                    // 检查第一组
                    if (IsValidPair(_config.SourcePath, _config.DestPath))
                    {
                        if (timeTriggered || ShouldMovePair(_config.SourcePath, _config.DestPath))
                        {
                            if (timeTriggered) Log($"[{_config.SourcePath}] 触发时间规则...");
                            await MoveFilesAsync(_config.SourcePath, _config.DestPath, token);
                            if (_config.EnableEmptyFolderRule) CleanEmptyFolders(_config.SourcePath, _config.EmptyFolderHours, token);
                            movedAny = true;
                        }
                    }

                    // 检查第二组
                    if (IsValidPair(_config.SourcePath2, _config.DestPath2))
                    {
                        if (timeTriggered || ShouldMovePair(_config.SourcePath2, _config.DestPath2))
                        {
                            if (timeTriggered) Log($"[{_config.SourcePath2}] 触发时间规则...");
                            await MoveFilesAsync(_config.SourcePath2, _config.DestPath2, token);
                            if (_config.EnableEmptyFolderRule) CleanEmptyFolders(_config.SourcePath2, _config.EmptyFolderHours, token);
                            movedAny = true;
                        }
                    }

                    if (movedAny)
                    {
                        _lastMoveTime = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    Log($"监控异常: {ex.Message}");
                }

                // 每秒检查一次条件
                await Task.Delay(1000, token);
            }
        }

        private bool IsValidPair(string source, string dest)
        {
            return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(dest) && Directory.Exists(source);
        }

        private void CleanEmptyFolders(string startLocation, double hoursOld, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            try
            {
                foreach (var directory in Directory.GetDirectories(startLocation))
                {
                    if (token.IsCancellationRequested) return;

                    CleanEmptyFolders(directory, hoursOld, token);

                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(directory);
                        if ((DateTime.Now - dirInfo.CreationTime).TotalHours >= hoursOld)
                        {
                            try
                            {
                                Directory.Delete(directory);
                                Log($"已清理空文件夹: {dirInfo.Name}");
                            }
                            catch (Exception ex)
                            {
                                Log($"清理空文件夹 {dirInfo.Name} 失败: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"遍历清理文件夹时出错: {ex.Message}");
            }
        }

        private bool ShouldMovePair(string source, string dest)
        {
            if (_config.EnableSizeRule || _config.EnableCountRule)
            {
                long totalSize = 0;
                int fileCount = 0;

                DirectoryInfo di = new DirectoryInfo(source);
                if (di.Exists)
                {
                    FileInfo[] files = di.GetFiles("*", SearchOption.AllDirectories);
                    if (IsCopyMode())
                    {
                        files = files.Where(file => !IsTargetFileCurrent(file, source, dest)).ToArray();
                    }

                    fileCount = files.Length;
                    totalSize = files.Sum(f => f.Length);

                    if (_config.EnableCountRule && fileCount >= _config.CountLimit)
                    {
                        Log($"[{source}] 触发文件数量规则 (当前 {fileCount} 个)，开始{GetTransferActionText()}...");
                        return true;
                    }

                    if (_config.EnableSizeRule && totalSize >= _config.SizeLimitMB * 1024 * 1024)
                    {
                        Log($"[{source}] 触发文件空间规则 (当前 {totalSize / 1024.0 / 1024.0:F2} MB)，开始{GetTransferActionText()}...");
                        return true;
                    }
                }
            }

            return false;
        }

        private async Task MoveFilesAsync(string source, string dest, CancellationToken token)
        {
            try
            {
                if (!Directory.Exists(dest))
                {
                    Directory.CreateDirectory(dest);
                }

                DirectoryInfo dir = new DirectoryInfo(source);
                FileInfo[] files = dir.GetFiles("*", SearchOption.AllDirectories);

                bool copyMode = IsCopyMode();
                int successCount = 0;
                int skipCount = 0;
                int failCount = 0;

                foreach (FileInfo file in files)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        string relativePath = file.FullName.Substring(source.Length).TrimStart('\\');
                        string targetPath = Path.Combine(dest, relativePath);
                        string targetDir = Path.GetDirectoryName(targetPath);

                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        if (copyMode && IsTargetFileCurrent(file, source, dest))
                        {
                            skipCount++;
                            continue;
                        }

                        // 目标文件存在且需要更新时，先删除后再写入，避免只读文件导致覆盖失败
                        if (File.Exists(targetPath))
                        {
                            FileInfo targetInfo = new FileInfo(targetPath);
                            if (targetInfo.IsReadOnly)
                            {
                                targetInfo.IsReadOnly = false;
                            }
                            File.Delete(targetPath);
                        }

                        if (copyMode)
                        {
                            File.Copy(file.FullName, targetPath);
                            File.SetLastWriteTimeUtc(targetPath, file.LastWriteTimeUtc);
                        }
                        else
                        {
                            File.Move(file.FullName, targetPath);
                        }
                        successCount++;
                    }
                    catch (IOException)
                    {
                        // 文件可能被占用，忽略，下次再处理
                        failCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 没有权限、文件被系统锁定或只读，忽略，下次再处理
                        failCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"{GetTransferActionText()}文件 {file.Name} 时出错: {ex.Message}");
                        failCount++;
                    }
                }

                if (successCount > 0 || failCount > 0)
                {
                    string skipMessage = copyMode ? $", 跳过(已存在且未变化) {skipCount} 个" : "";
                    Log($"{GetTransferActionText()}完成: 成功 {successCount} 个{skipMessage}, 失败(或被占用) {failCount} 个");
                }
            }
            catch (Exception ex)
            {
                Log($"{GetTransferActionText()}过程中发生异常: {ex.Message}");
            }
        }

        private bool IsCopyMode()
        {
            return string.Equals(_config.TransferMode, "Copy", StringComparison.OrdinalIgnoreCase);
        }

        private string GetTransferActionText()
        {
            return IsCopyMode() ? "复制" : "搬运";
        }

        private bool IsTargetFileCurrent(FileInfo sourceFile, string sourceRoot, string destRoot)
        {
            string relativePath = sourceFile.FullName.Substring(sourceRoot.Length).TrimStart('\\');
            string targetPath = Path.Combine(destRoot, relativePath);

            if (!File.Exists(targetPath))
            {
                return false;
            }

            FileInfo targetFile = new FileInfo(targetPath);
            return targetFile.Length == sourceFile.Length &&
                   targetFile.LastWriteTimeUtc >= sourceFile.LastWriteTimeUtc;
        }

        #region Configuration Management

        private string GetConfigFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");
        }

        private void LoadConfig()
        {
            string path = GetConfigFilePath();
            if (!File.Exists(path))
            {
                _config = new AppConfig();
                return;
            }

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppConfig));
                using (StreamReader reader = new StreamReader(path))
                {
                    _config = (AppConfig)serializer.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                string backupPath = path + ".bak";
                try
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(path, backupPath);
                    Log($"配置文件已损坏，已备份为 {System.IO.Path.GetFileName(backupPath)}，将使用默认配置。错误: {ex.Message}");
                }
                catch
                {
                    Log($"配置文件读取失败，将使用默认配置。错误: {ex.Message}");
                }
                _config = new AppConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigFilePath();
                string tmpPath = path + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(AppConfig));
                using (StreamWriter writer = new StreamWriter(tmpPath))
                {
                    serializer.Serialize(writer, _config);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                Log($"保存配置失败: {ex.Message}");
            }
        }

        private void UpdateUIFromConfig()
        {
            txtSourcePath.Text = _config.SourcePath;
            txtDestPath.Text = _config.DestPath;
            txtSourcePath2.Text = _config.SourcePath2;
            txtDestPath2.Text = _config.DestPath2;

            rbCopyMode.IsChecked = IsCopyMode();
            rbCutMode.IsChecked = !IsCopyMode();

            chkTimeRule.IsChecked = _config.EnableTimeRule;
            txtTimeInterval.Text = _config.TimeIntervalSeconds.ToString();

            chkSizeRule.IsChecked = _config.EnableSizeRule;
            txtSizeLimit.Text = _config.SizeLimitMB.ToString();

            chkCountRule.IsChecked = _config.EnableCountRule;
            txtCountLimit.Text = _config.CountLimit.ToString();

            chkEmptyFolderRule.IsChecked = _config.EnableEmptyFolderRule;
            txtEmptyFolderHours.Text = _config.EmptyFolderHours.ToString();
        }

        private void UpdateConfigFromUI()
        {
            _config.SourcePath = txtSourcePath.Text;
            _config.DestPath = txtDestPath.Text;
            _config.SourcePath2 = txtSourcePath2.Text;
            _config.DestPath2 = txtDestPath2.Text;
            _config.TransferMode = rbCopyMode.IsChecked == true ? "Copy" : "Cut";

            _config.EnableTimeRule = chkTimeRule.IsChecked ?? false;
            int.TryParse(txtTimeInterval.Text, out int timeInterval);
            _config.TimeIntervalSeconds = timeInterval > 0 ? timeInterval : 60;

            _config.EnableSizeRule = chkSizeRule.IsChecked ?? false;
            long.TryParse(txtSizeLimit.Text, out long sizeLimit);
            _config.SizeLimitMB = sizeLimit > 0 ? sizeLimit : 100;

            _config.EnableCountRule = chkCountRule.IsChecked ?? false;
            int.TryParse(txtCountLimit.Text, out int countLimit);
            _config.CountLimit = countLimit > 0 ? countLimit : 1000;

            _config.EnableEmptyFolderRule = chkEmptyFolderRule.IsChecked ?? false;
            double.TryParse(txtEmptyFolderHours.Text, out double emptyFolderHours);
            _config.EmptyFolderHours = emptyFolderHours > 0 ? emptyFolderHours : 24.0;
        }

        #endregion
    }

}