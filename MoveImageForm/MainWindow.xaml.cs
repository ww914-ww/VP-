using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace MoveImageForm
{
    public partial class MainWindow : Window
    {
        private AppConfig _config;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning;
        private DateTime _lastMoveTime;
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        // 进程监听
        private System.Windows.Threading.DispatcherTimer _processTimer;
        private ObservableCollection<ProcessViewModel> _processList;
        private Dictionary<ProcessViewModel, Process> _watchedProcesses = new Dictionary<ProcessViewModel, Process>();
        private bool _suppressProcessEnabledHandler;
        private DateTime _nextProcessCheckAt;
        private DateTime _nextRemindAllowedAt;
        private System.Windows.Threading.DispatcherTimer _countdownTimer;
        private bool _isInitializingProcessMonitoring;

        // 版本更新
        private System.Windows.Threading.DispatcherTimer _updateTimer;
        private string _latestCloudVersion;
        private string _latestCloudDate;
        private string _latestCloudNote;

        public MainWindow()
        {
            InitializeComponent();
            InitNotifyIcon();
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
            _notifyIcon.Text = "poco运维工具 (后台运行中)";
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

            InitProcessMonitoring();
            InitVersionUpdate();
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
            AppendLog(lstLog, message);
        }

        private void LogProcess(string message)
        {
            AppendLog(lstProcessLog, message);
        }

        private void AppendLog(System.Windows.Controls.ListBox listBox, string message)
        {
            string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logMessage = $"[{timeStamp}] {message}";

            Dispatcher.InvokeAsync(() =>
            {
                listBox.Items.Add(logMessage);
                if (listBox.Items.Count > 1000)
                {
                    listBox.Items.RemoveAt(0);
                }
                listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
            });

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

                        string logFile = Path.Combine(logDir, $"Log_{DateTime.Now:yyyy-MM-dd}.txt");
                        File.AppendAllText(logFile, logMessage + Environment.NewLine);
                    }
                }
                catch
                {
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

        #region Process Monitoring

        private string GetProcessNameFromPath(string path)
        {
            string baseName = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(baseName) ? "进程" : baseName;
        }

        private void SyncProcessNameFromPath(ProcessInfo proc)
        {
            if (string.IsNullOrWhiteSpace(proc.Path)) return;
            proc.Name = GetProcessNameFromPath(proc.Path);
        }

        private void InitProcessMonitoring()
        {
            _isInitializingProcessMonitoring = true;
            try
            {
                _processList = new ObservableCollection<ProcessViewModel>();
                bool namesUpdated = false;
                foreach (var proc in _config.WatchProcesses)
                {
                    string oldName = proc.Name;
                    SyncProcessNameFromPath(proc);
                    if (oldName != proc.Name)
                        namesUpdated = true;
                    _processList.Add(ProcessViewModel.FromProcessInfo(proc, SaveProcessList));
                }
                if (namesUpdated)
                    SaveProcessList();
                dgProcesses.ItemsSource = _processList;

                int interval = _config.ProcessCheckIntervalSeconds;
                if (interval < 1 || interval > 60) interval = 5;

                int[] intervalValues = { 1, 2, 3, 5, 10, 15, 30, 60 };
                int idx = Array.IndexOf(intervalValues, interval);
                if (idx < 0) idx = 3;

                _processTimer = new System.Windows.Threading.DispatcherTimer();
                _processTimer.Interval = TimeSpan.FromSeconds(interval);
                _processTimer.Tick += ProcessTimer_Tick;
                cmbProcessInterval.SelectedIndex = idx;

                InitProcessCountdownTimer();
                _processTimer.Start();
                _nextProcessCheckAt = DateTime.Now.AddSeconds(interval);
                _nextRemindAllowedAt = _nextProcessCheckAt;

                CheckAllProcesses();
                LogProcess($"进程监控已启动，检查间隔 {interval} 秒，共 {_processList.Count} 个进程");
                SyncProcessCheckCountdown();
            }
            finally
            {
                _isInitializingProcessMonitoring = false;
            }
        }

        private void InitProcessCountdownTimer()
        {
            _countdownTimer = new System.Windows.Threading.DispatcherTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += (s, e) => UpdateProcessCheckCountdown();
            _countdownTimer.Start();
        }

        private int GetProcessCheckIntervalSeconds()
        {
            if (_processTimer != null)
                return Math.Max(1, (int)_processTimer.Interval.TotalSeconds);

            int seconds = _config?.ProcessCheckIntervalSeconds ?? 5;
            return seconds < 1 ? 5 : seconds;
        }

        private void ResetProcessCheckCountdown()
        {
            int interval = GetProcessCheckIntervalSeconds();
            _nextProcessCheckAt = DateTime.Now.AddSeconds(interval);
            _nextRemindAllowedAt = _nextProcessCheckAt;

            if (_processTimer != null)
            {
                _processTimer.Stop();
                _processTimer.Start();
            }

            UpdateProcessCheckCountdown();
        }

        private void HideProcessCheckCountdown()
        {
            if (borderProcessCountdown != null)
                borderProcessCountdown.Visibility = Visibility.Collapsed;

            if (_processList != null)
            {
                foreach (var proc in _processList)
                    proc.UpdateNextCheckCountdown(0, false);
            }
        }

        private void SyncProcessCheckCountdown()
        {
            if (_processList != null && _processList.Any(ProcessNeedsRemind))
                UpdateProcessCheckCountdown();
            else
                HideProcessCheckCountdown();
        }

        private bool ProcessNeedsRemind(ProcessViewModel proc)
        {
            return proc.Enabled
                && proc.MonitorState != ProcessMonitorState.Running
                && proc.MonitorState != ProcessMonitorState.Starting;
        }

        private void UpdateProcessCheckCountdown()
        {
            if (txtProcessCountdown == null || _processList == null)
                return;

            if (!_processList.Any(ProcessNeedsRemind))
            {
                HideProcessCheckCountdown();
                return;
            }

            borderProcessCountdown.Visibility = Visibility.Visible;

            int remaining = Math.Max(0, (int)Math.Ceiling((_nextProcessCheckAt - DateTime.Now).TotalSeconds));
            int interval = GetProcessCheckIntervalSeconds();

            txtProcessCountdown.Text = $"下次检测倒计时：{remaining} 秒";
            txtProcessCountdownHint.Text = remaining > 0
                ? $"检测间隔 {interval} 秒，倒计时结束后将自动启动未运行进程"
                : "即将开始检测...";

            foreach (var proc in _processList)
                proc.UpdateNextCheckCountdown(remaining, ProcessNeedsRemind(proc));
        }

        private void ProcessTimer_Tick(object sender, EventArgs e)
        {
            _nextRemindAllowedAt = DateTime.Now;
            CheckAllProcesses();
            RemindDownProcesses();

            int interval = GetProcessCheckIntervalSeconds();
            _nextProcessCheckAt = DateTime.Now.AddSeconds(interval);
            _nextRemindAllowedAt = _nextProcessCheckAt;
            SyncProcessCheckCountdown();
        }

        private void RemindDownProcesses(bool immediate = false)
        {
            foreach (var proc in _processList)
                RemindDownProcess(proc, immediate);
        }

        private void RemindDownProcess(ProcessViewModel proc, bool immediate = false)
        {
            if (!proc.Enabled
                || proc.MonitorState == ProcessMonitorState.Running
                || proc.MonitorState == ProcessMonitorState.Starting)
                return;

            if (IsProcessRunning(proc))
            {
                CheckSingleProcess(proc);
                return;
            }

            if (!immediate && DateTime.Now < _nextRemindAllowedAt)
                return;

            LogProcess($"检测到 {proc.DisplayName} 未运行，正在自动启动");
            StartWatchedProcess(proc);
        }

        private void UpdateProcessStartingLoading()
        {
            if (borderProcessStarting == null || txtProcessStarting == null || _processList == null)
                return;

            var starting = _processList.FirstOrDefault(p => p.MonitorState == ProcessMonitorState.Starting);
            if (starting != null)
            {
                txtProcessStarting.Text = $"正在启动 {starting.DisplayName}，请稍候...";
                borderProcessStarting.Visibility = Visibility.Visible;
            }
            else
            {
                borderProcessStarting.Visibility = Visibility.Collapsed;
            }
        }
        private bool IsProcessRunning(ProcessViewModel proc)
        {
            try
            {
                string procName = Path.GetFileNameWithoutExtension(proc.Path);
                if (string.IsNullOrWhiteSpace(procName))
                    return false;

                return Process.GetProcessesByName(procName).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void CheckAllProcesses()
        {
            foreach (var proc in _processList)
                CheckSingleProcess(proc);
        }

        private void CheckSingleProcess(ProcessViewModel proc)
        {
            if (!proc.Enabled)
            {
                proc.SetMonitorState(ProcessMonitorState.NotMonitoring);
                UnwatchProcess(proc);
                return;
            }

            try
            {
                string procName = Path.GetFileNameWithoutExtension(proc.Path);
                var processes = Process.GetProcessesByName(procName);
                bool isRunning = processes.Length > 0;
                var previousState = proc.MonitorState;

                if (isRunning)
                {
                    if (previousState == ProcessMonitorState.Starting)
                        LogProcess($"{proc.DisplayName} 启动成功");
                    else if (previousState != ProcessMonitorState.Running)
                        LogProcess($"{proc.DisplayName} 已恢复运行");

                    proc.SetMonitorState(ProcessMonitorState.Running);
                    UpdateProcessStartingLoading();

                    if (!_watchedProcesses.ContainsKey(proc))
                    {
                        var p = processes[0];
                        p.EnableRaisingEvents = true;
                        p.Exited += (s, e) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() => OnWatchedProcessExited(proc)));
                        };
                        _watchedProcesses[proc] = p;
                    }
                }
                else
                {
                    if (previousState == ProcessMonitorState.Running)
                    {
                        UnwatchProcess(proc);
                        proc.SetMonitorState(ProcessMonitorState.Stopped);
                    }
                    else if (previousState != ProcessMonitorState.Starting
                        && previousState != ProcessMonitorState.StartFailed
                        && previousState != ProcessMonitorState.NotMonitoring)
                    {
                        proc.SetMonitorState(ProcessMonitorState.Stopped);
                    }
                }
            }
            catch
            {
                if (proc.MonitorState != ProcessMonitorState.Starting)
                    proc.SetMonitorState(ProcessMonitorState.Stopped);
            }
        }

        private void UnwatchProcess(ProcessViewModel proc)
        {
            if (_watchedProcesses.TryGetValue(proc, out var p))
            {
                try { p.Dispose(); } catch { }
                _watchedProcesses.Remove(proc);
            }
        }

        private void OnWatchedProcessExited(ProcessViewModel proc)
        {
            UnwatchProcess(proc);

            if (IsProcessRunning(proc))
            {
                CheckSingleProcess(proc);
                return;
            }

            proc.SetMonitorState(ProcessMonitorState.Stopped);
            LogProcess($"{proc.DisplayName} 已停止运行");
            RemindDownProcess(proc, immediate: true);
        }

        private void StartWatchedProcess(ProcessViewModel proc)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(proc.Path) && File.Exists(proc.Path))
                {
                    proc.SetMonitorState(ProcessMonitorState.Starting);
                    UpdateProcessStartingLoading();
                    Process.Start(new ProcessStartInfo(proc.Path)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(proc.Path)
                    });
                    LogProcess($"正在启动: {proc.DisplayName}");
                    BeginWaitForProcessStart(proc);
                }
                else
                {
                    proc.SetMonitorState(ProcessMonitorState.StartFailed);
                    UpdateProcessStartingLoading();
                    LogProcess($"启动失败: 找不到文件 {proc.Path}");
                }
            }
            catch (Exception ex)
            {
                proc.SetMonitorState(ProcessMonitorState.StartFailed);
                UpdateProcessStartingLoading();
                LogProcess($"启动进程 {proc.DisplayName} 失败: {ex.Message}");
            }
        }

        private async void BeginWaitForProcessStart(ProcessViewModel proc)
        {
            string procName = Path.GetFileNameWithoutExtension(proc.Path);

            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                if (proc.MonitorState != ProcessMonitorState.Starting)
                    return;

                if (Process.GetProcessesByName(procName).Length > 0)
                {
                    await Dispatcher.InvokeAsync(() => CheckSingleProcess(proc));
                    return;
                }
            }

            if (proc.MonitorState == ProcessMonitorState.Starting)
            {
                proc.SetMonitorState(ProcessMonitorState.StartFailed);
                LogProcess($"{proc.DisplayName} 启动超时，未检测到进程运行");
                UpdateProcessStartingLoading();
            }
        }

        private void CmbProcessInterval_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_config == null || _isInitializingProcessMonitoring) return;

            if (cmbProcessInterval.SelectedItem is System.Windows.Controls.ComboBoxItem item
                && int.TryParse(item.Content.ToString(), out int seconds))
            {
                _config.ProcessCheckIntervalSeconds = seconds;
                if (_processTimer != null)
                    _processTimer.Interval = TimeSpan.FromSeconds(seconds);
                if (_processList != null && _processList.Any(ProcessNeedsRemind))
                    ResetProcessCheckCountdown();
                SaveConfig();
                LogProcess($"监控间隔已更新为 {seconds} 秒");
            }
        }

        private void BtnBrowseProcessPath_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "选择可执行文件";
                dialog.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtProcessPath.Text = dialog.FileName;
                }
            }
        }

        private void BtnAddProcess_Click(object sender, RoutedEventArgs e)
        {
            string path = txtProcessPath.Text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("请选择可执行文件路径！");
                return;
            }

            foreach (var p in _processList)
            {
                if (string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"该路径已在监控列表中（{p.DisplayName}）！");
                    return;
                }
            }

            var vm = new ProcessViewModel
            {
                Path = path,
                Enabled = true,
                SaveCallback = SaveProcessList
            };
            vm.Name = vm.DisplayName;
            _processList.Add(vm);
            SaveProcessList();

            txtProcessPath.Clear();
            LogProcess($"已添加进程监控: {vm.DisplayName}");

            CheckSingleProcess(vm);
            if (ProcessNeedsRemind(vm))
                ResetProcessCheckCountdown();
            else
                SyncProcessCheckCountdown();
        }

        private void ApplyProcessMonitoringState(ProcessViewModel proc, bool enabled, string customLog = null)
        {
            _suppressProcessEnabledHandler = true;
            try
            {
                proc.Enabled = enabled;
                if (enabled)
                {
                    LogProcess(customLog ?? $"已开启监听: {proc.DisplayName}");
                    CheckSingleProcess(proc);
                    if (ProcessNeedsRemind(proc))
                        ResetProcessCheckCountdown();
                    else
                        SyncProcessCheckCountdown();
                }
                else
                {
                    UnwatchProcess(proc);
                    LogProcess(customLog ?? $"已关闭监听: {proc.DisplayName}");
                    SyncProcessCheckCountdown();
                }
                SaveProcessList();
            }
            finally
            {
                _suppressProcessEnabledHandler = false;
            }
        }

        private void ProcessMonitorToggle_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_suppressProcessEnabledHandler) return;

            var checkBox = sender as System.Windows.Controls.CheckBox;
            var vm = checkBox?.DataContext as ProcessViewModel;
            if (vm == null) return;

            e.Handled = true;
            ApplyProcessMonitoringState(vm, !vm.Enabled);
        }

        private void BtnDeleteProcess_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            var vm = button?.Tag as ProcessViewModel;
            if (vm != null)
            {
                UnwatchProcess(vm);
                _processList.Remove(vm);
                SaveProcessList();
                LogProcess($"已删除进程监控: {vm.DisplayName}");
            }
        }

        private void SaveProcessList()
        {
            _config.WatchProcesses.Clear();
            foreach (var vm in _processList)
            {
                _config.WatchProcesses.Add(vm.ToProcessInfo());
            }
            SaveConfig();
        }

        #endregion

        #region Version Update

        private void InitVersionUpdate()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            txtLocalVersion.Text = $"{ver.Major}.{ver.Minor}.{ver.Build}";

            if (!string.IsNullOrWhiteSpace(_config.LastCheckTime))
            {
                txtLastCheckTime.Text = $"上次检查时间: {_config.LastCheckTime} | 检查间隔: {_config.CheckIntervalMinutes} 分钟";
            }

            chkAutoStart.IsChecked = _config.AutoStart;

            chkAutoCheck.IsChecked = _config.CheckIntervalMinutes > 0;
            if (_config.CheckIntervalMinutes > 0)
            {
                chkAutoCheck.Content = $"启用定时检查（每{_config.CheckIntervalMinutes}分钟）";
                _updateTimer = new System.Windows.Threading.DispatcherTimer();
                _updateTimer.Interval = TimeSpan.FromMinutes(_config.CheckIntervalMinutes);
                _updateTimer.Tick += (s, ev) => CheckForUpdate();
                _updateTimer.Start();
            }

            Task.Run(() => CheckForUpdate());
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdate.IsEnabled = false;
            btnCheckUpdate.Content = "检查中...";
            await Task.Run(() => CheckForUpdate());
            btnCheckUpdate.IsEnabled = true;
            btnCheckUpdate.Content = "立即检查";
        }

        private void CheckForUpdate()
        {
            try
            {
                string cloudPath = _config.CloudPath;
                if (string.IsNullOrWhiteSpace(cloudPath))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (云端路径未配置)";
                        txtCloudVersion.Text = "未配置";
                    });
                    _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveConfig();
                    return;
                }

                string driveLetter = "Y:";
                if (!Directory.Exists(driveLetter + "\\"))
                {
                    string userPass = "";
                    if (!string.IsNullOrWhiteSpace(_config.CloudUser))
                    {
                        userPass = $" /user:{_config.CloudUser}";
                        if (!string.IsNullOrWhiteSpace(_config.CloudPassword))
                            userPass += $" {_config.CloudPassword}";
                    }

                    var psi = new ProcessStartInfo("net", $"use {driveLetter} \"{cloudPath}\"{userPass}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(10000);
                }

                string jsonPath = Path.Combine(driveLetter + "\\", "version.json");
                if (!File.Exists(jsonPath))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (云端不可达)";
                        txtCloudVersion.Text = "不可达";
                    });
                    _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveConfig();
                    return;
                }

                string jsonText = File.ReadAllText(jsonPath);
                var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(jsonText);
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

                _latestCloudVersion = latestVersion;
                _latestCloudDate = cloudDate;
                _latestCloudNote = cloudNote;

                var localVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string localVerStr = $"{localVer.Major}.{localVer.Minor}.{localVer.Build}";

                _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveConfig();

                Dispatcher.InvokeAsync(() =>
                {
                    txtCloudVersion.Text = latestVersion;
                    txtLastCheckTime.Text = $"上次检查时间: {_config.LastCheckTime} | 检查间隔: {_config.CheckIntervalMinutes} 分钟";

                    if (!string.IsNullOrWhiteSpace(latestVersion) && IsNewerVersion(latestVersion, localVerStr))
                    {
                        txtNewVersionInfo.Text = $"发现新版本 {latestVersion}{(string.IsNullOrWhiteSpace(cloudDate) ? "" : $" ({cloudDate})")}\n{cloudNote}";
                        borderNewVersion.Visibility = Visibility.Visible;
                        Log($"发现新版本: {latestVersion}");
                        ShowUpdateDialog(latestVersion, cloudDate, cloudNote);
                    }
                    else
                    {
                        borderNewVersion.Visibility = Visibility.Collapsed;
                        Log($"版本检查: 已是最新版本 (本地 {localVerStr}, 云端 {latestVersion})");
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    Log($"版本检查失败: {ex.Message}");
                    txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (检查失败)";
                });
                _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveConfig();
            }
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

        private void ShowUpdateDialog(string version, string date, string note)
        {
            string msg = $"发现新版本 {version}";
            if (!string.IsNullOrWhiteSpace(date))
                msg += $"\n更新日期: {date}";
            if (!string.IsNullOrWhiteSpace(note))
                msg += $"\n\n{note}";
            msg += "\n\n是否立即更新？（将下载更新并重启程序）";

            var result = MessageBox.Show(msg, "发现新版本",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                StartUpdateDownload(version);
            }
        }

        private async void StartUpdateDownload(string version)
        {
            Log($"开始下载更新 {version}...");
            btnCheckUpdate.IsEnabled = false;

            await Task.Run(() =>
            {
                try
                {
                    string baseDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string appRoot = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
                    string tempDir = Path.Combine(appRoot, "versions", ".temp");

                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                    Directory.CreateDirectory(tempDir);

                    string cloudVerPath = Path.Combine("Y:", version);
                    CopyDirectory(cloudVerPath, tempDir);

                    if (!File.Exists(Path.Combine(tempDir, "MoveImageForm.exe")))
                    {
                        Dispatcher.InvokeAsync(() =>
                            Log("更新下载失败: 云端版本文件不完整"));
                        return;
                    }

                    string batPath = Path.Combine(appRoot, "update.bat");
                    string batContent = GenerateUpdateBat(appRoot, version);
                    File.WriteAllText(batPath, batContent, System.Text.Encoding.UTF8);

                    Dispatcher.InvokeAsync(() =>
                    {
                        Log("更新已下载，即将退出并执行更新...");
                        var timer = new System.Windows.Threading.DispatcherTimer();
                        timer.Interval = TimeSpan.FromSeconds(1);
                        timer.Tick += (s, args) =>
                        {
                            timer.Stop();
                            Process.Start(new ProcessStartInfo(batPath)
                            {
                                UseShellExecute = true,
                                CreateNoWindow = false,
                                WorkingDirectory = appRoot
                            });
                            _notifyIcon.Visible = false;
                            _notifyIcon.Dispose();
                            Application.Current.Shutdown();
                        };
                        timer.Start();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() =>
                        Log($"更新下载失败: {ex.Message}"));
                }
            });
        }

        private string GenerateUpdateBat(string appRoot, string version)
        {
            string versionsDir = Path.Combine(appRoot, "versions");
            string newVerDir = Path.Combine(versionsDir, version);
            string tempDir = Path.Combine(versionsDir, ".temp");
            string backupDir = Path.Combine(appRoot, "backup");
            string launcherPath = Path.Combine(appRoot, "Launcher.exe");

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

        private void ChkAutoCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (chkAutoCheck.IsChecked == true)
            {
                _config.CheckIntervalMinutes = 30;
                if (_updateTimer == null)
                {
                    _updateTimer = new System.Windows.Threading.DispatcherTimer();
                    _updateTimer.Interval = TimeSpan.FromMinutes(_config.CheckIntervalMinutes);
                    _updateTimer.Tick += (s, ev) => CheckForUpdate();
                }
                _updateTimer.Start();
                chkAutoCheck.Content = $"启用定时检查（每{_config.CheckIntervalMinutes}分钟）";
                Log("已启用定时版本检查（每30分钟）");
            }
            else
            {
                _config.CheckIntervalMinutes = 0;
                _updateTimer?.Stop();
                chkAutoCheck.Content = "启用定时检查（每30分钟）";
                Log("已关闭定时版本检查");
            }
            SaveConfig();
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            _config.AutoStart = chkAutoStart.IsChecked == true;
            SetAutoStart(_config.AutoStart);
            SaveConfig();
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                string appName = "poco运维工具";
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string launcherPath = Path.Combine(
                    Path.GetDirectoryName(exePath), "..", "..", "Launcher.exe");
                string autoStartPath = Path.GetFullPath(launcherPath);

                var regKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (enable)
                {
                    if (regKey != null)
                    {
                        if (File.Exists(autoStartPath))
                            regKey.SetValue(appName, $"\"{autoStartPath}\"");
                        else
                            regKey.SetValue(appName, $"\"{exePath}\"");
                    }
                    Log("已设置开机自启动");
                }
                else
                {
                    regKey?.DeleteValue(appName, false);
                    Log("已取消开机自启动");
                }
            }
            catch (Exception ex)
            {
                Log($"设置开机自启动失败: {ex.Message}");
            }
        }

        #endregion

        #region Configuration Management

        private string GetConfigFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string rootConfig = Path.Combine(baseDir, "..", "..", "config.xml");
            string fullRootConfig = Path.GetFullPath(rootConfig);
            if (File.Exists(fullRootConfig))
                return fullRootConfig;

            string localConfig = Path.Combine(baseDir, "config.xml");
            return localConfig;
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