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
using System.Windows.Controls;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;
using MoveImageForm.Models;
using MoveImageForm.Services;
using MoveImageForm.Views;

namespace MoveImageForm
{
    public partial class MainWindow : Window
    {
        private AppConfig _config;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning;
        private DateTime _lastMoveTime;
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        // SFTP 会话管理
        private readonly SessionManager _session = new SessionManager();

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

        private bool _isLoadingConfig;

        public MainWindow()
        {
            InitializeComponent();
            InitNotifyIcon();
            _session.LoginStateChanged += OnLoginStateChanged;
        }

        #region Login / Session Management

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (_session.IsLoggedIn)
            {
                ShowLogoutMenu();
                return;
            }

            // 收集 SourcePath 中引用的 Profile
            UpdateConfigFromUI();
            var activeProfileNames = _config.GetActiveProfileNames().Distinct().ToList();
            if (activeProfileNames.Count == 0)
            {
                MessageBox.Show("请先在源文件夹区域为监控目录分配账号，再进行登录。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 尝试用存储的（DPAPI 解密后）密码自动连接
            int connected = 0;
            var failedProfiles = new List<SftpProfile>();

            foreach (var name in activeProfileNames)
            {
                var profile = _config.FindProfile(name);
                if (profile == null) continue;

                try
                {
                    string password = profile.GetPlainPassword();
                    if (!string.IsNullOrEmpty(password))
                    {
                        _session.Login(profile, password);
                        connected++;
                        Log($"[系统] 自动连接: {profile.Name} → {profile.RemoteRoot}");
                        continue;
                    }
                }
                catch { }

                failedProfiles.Add(profile);
            }

            // 如果有未连接的 Profile（密码为空或错误），弹出登录框手动输入
            if (failedProfiles.Count > 0)
            {
                var dialog = new LoginDialog(failedProfiles);
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        _session.Login(dialog.SelectedProfile, dialog.Password);
                        connected++;
                        Log($"[系统] 手动连接: {dialog.SelectedProfile.Name} → {dialog.SelectedProfile.RemoteRoot}");

                        // 更新 Profile 密码并加密保存
                        dialog.SelectedProfile.Password = dialog.Password;
                        dialog.SelectedProfile.EncryptPassword();
                        SaveConfig();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"连接失败: {ex.Message}", "登录失败",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                // 尝试连接剩余的
                foreach (var profile in failedProfiles)
                {
                    if (_session.IsProfileConnected(profile.Name)) continue;
                    try
                    {
                        string password = profile.GetPlainPassword();
                        if (!string.IsNullOrEmpty(password))
                        {
                            _session.Login(profile, password);
                            connected++;
                            Log($"[系统] 自动连接: {profile.Name} → {profile.RemoteRoot}");
                        }
                    }
                    catch { }
                }
            }

            if (connected > 0)
                Log($"[系统] 已连接 {connected} 个账号。");
            else
                Log("[系统] 登录失败，未连接任何账号。");

            UpdateTransferStatusBar();
        }

        private void ShowLogoutMenu()
        {
            var menu = new ContextMenu();

            foreach (var name in _session.ConnectedProfiles)
            {
                var item = new MenuItem { Header = $"已连接: {name}" };
                item.IsEnabled = false;
                item.FontWeight = FontWeights.Bold;
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());

            var logoutItem = new MenuItem { Header = "登出全部" };
            logoutItem.Click += (s, args) =>
            {
                _session.Logout();
                Log("[系统] 已登出全部账号");
            };
            menu.Items.Add(logoutItem);

            var switchItem = new MenuItem { Header = "切换账号..." };
            switchItem.Click += (s, args) =>
            {
                _session.Logout();
                BtnLogin_Click(null, null);
            };
            menu.Items.Add(switchItem);

            menu.IsOpen = true;
        }

        private void OnLoginStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateLoginButton();
                ApplyRoleRestrictions();
                UpdateTransferStatusBar();
            });
        }

        private void UpdateLoginButton()
        {
            if (_session.IsLoggedIn)
            {
                int count = _session.ConnectedProfiles.Count;
                string label = count == 1
                    ? _session.ConnectedProfiles.First()
                    : $"已连接 {count} 个账号";
                btnLogin.Content = $"{label} ▼";
                spLoginStatus.Visibility = Visibility.Visible;

                var parts = new List<string>();
                foreach (var name in _session.ConnectedProfiles)
                {
                    var p = _config.FindProfile(name);
                    if (p != null)
                        parts.Add($"{p.Name}({p.Role})→{p.RemoteRoot}");
                }
                txtLoginInfo.Text = string.Join(" | ", parts);
            }
            else
            {
                btnLogin.Content = "登录";
                spLoginStatus.Visibility = Visibility.Collapsed;
                txtLoginInfo.Text = "";
            }
        }

        private void ApplyRoleRestrictions()
        {
            string role = _session.CurrentRole;

            bool canUpload = RoleEnforcer.CanUpload(role);
            bool canChooseMode = RoleEnforcer.CanChooseTransferMode(role);

            rbAppendMode.IsEnabled = canUpload;
            rbCopyMode.IsEnabled = canChooseMode;
            rbCutMode.IsEnabled = canChooseMode;
            btnStart.IsEnabled = canUpload;

            string forced = RoleEnforcer.ForcedTransferMode(role);
            if (forced == "Append") rbAppendMode.IsChecked = true;
            if (forced == "None") btnStart.IsEnabled = false;

            btnCheckUpdate.IsEnabled = RoleEnforcer.CanCheckUpdate(_session.IsLoggedIn);
        }

        private void UpdateTransferStatusBar()
        {
            if (_session.IsLoggedIn)
            {
                borderTransferStatus.Visibility = Visibility.Visible;
                borderNotLoggedIn.Visibility = Visibility.Collapsed;

                var parts = new List<string>();
                foreach (var name in _session.ConnectedProfiles)
                {
                    var p = _config.FindProfile(name);
                    if (p != null)
                        parts.Add($"{p.Name}({p.Role})→{p.RemoteRoot}");
                }
                txtTransferStatus.Text = $"已连接 {_session.ConnectedProfiles.Count} 个账号: {string.Join(" | ", parts)}";
            }
            else
            {
                borderTransferStatus.Visibility = Visibility.Collapsed;
                borderNotLoggedIn.Visibility = Visibility.Visible;
            }
        }

        private void TabMain_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (tabMain.SelectedIndex == 3) // Tab 4 = 账号管理
            {
                if (!RoleEnforcer.CanManageAccounts(_session.CurrentRole))
                {
                    tabMain.SelectedIndex = e.RemovedItems.Count > 0
                        ? tabMain.Items.IndexOf(e.RemovedItems[0]) : 0;
                    MessageBox.Show("请使用管理员账号登录后再访问账号管理", "权限不足",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        #endregion

        #region NotifyIcon

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
            _notifyIcon.Text = "VP运维工具 (后台运行中)";
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
                _session.Logout();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            };

            contextMenu.Items.Add(showItem);
            contextMenu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        #endregion

        #region Window Events

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            UpdateUIFromConfig();

            // 不自动开始搬运 — 需要用户先登录
            Log("请点击左上角「登录」连接 SFTP 服务器。");
            Log("提示：每个监控文件夹可以绑定不同的 SFTP 账号和远程路径。");

            InitProcessMonitoring();
            InitVersionUpdate();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();

            _notifyIcon.ShowBalloonTip(2000, "提示",
                "程序已最小化到系统托盘并在后台继续搬运。",
                System.Windows.Forms.ToolTipIcon.Info);

            UpdateConfigFromUI();
            SaveConfig();
        }

        #endregion

        #region Tab 1 — File Browsing

        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择监控文件夹";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    txtSourcePath.Text = dialog.SelectedPath;
            }
        }

        private void BtnBrowseSource2_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择监控文件夹2";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    txtSourcePath2.Text = dialog.SelectedPath;
            }
        }

        #endregion

        #region Tab 1 — File Transfer (SFTP)

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            StartMoving();
        }

        /// <summary>获取指定 sourceKey 对应的 SFTP 会话（自动重连）</summary>
        private ISftpService GetOrReconnectSession(string sourceKey)
        {
            var profile = _config.GetProfileForSource(sourceKey);
            if (profile == null) return null;

            var session = _session.GetSession(profile.Name);
            if (session != null && session.IsConnected) return session;

            // 尝试用缓存的密码重连
            if (_session.Reconnect(profile.Name))
                return _session.GetSession(profile.Name);

            return null;
        }

        private void StartMoving()
        {
            UpdateConfigFromUI();

            var activeProfiles = _config.GetActiveProfileNames().Distinct().ToList();
            if (activeProfiles.Count == 0)
            {
                MessageBox.Show("请先在源文件夹区域为监控目录分配账号！");
                return;
            }

            if (!_session.IsLoggedIn)
            {
                MessageBox.Show("请先点击左上角「登录」连接 SFTP 服务器！");
                return;
            }

            string role = _session.CurrentRole;
            if (!RoleEnforcer.CanUpload(role))
            {
                MessageBox.Show($"当前账号「{_session.CurrentRole}」没有上传权限！", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool pair1Valid = !string.IsNullOrWhiteSpace(txtSourcePath.Text) && Directory.Exists(txtSourcePath.Text);
            bool pair2Valid = !string.IsNullOrWhiteSpace(txtSourcePath2.Text) && Directory.Exists(txtSourcePath2.Text);

            if (!pair1Valid && !pair2Valid)
            {
                MessageBox.Show("请至少设置一组有效的监控文件夹！");
                return;
            }

            SaveConfig();

            _isRunning = true;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            SetUIEnabled(false);

            _cancellationTokenSource = new CancellationTokenSource();
            _lastMoveTime = DateTime.Now;

            Log("开始监控 (SFTP)...");
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
            txtSourcePath2.IsEnabled = enabled;
            cmbSource1Profile.IsEnabled = enabled;
            cmbSource2Profile.IsEnabled = enabled;
            rbAppendMode.IsEnabled = enabled;
            rbCopyMode.IsEnabled = enabled;
            rbCutMode.IsEnabled = enabled;
            chkTimeRule.IsEnabled = enabled;
            txtTimeInterval.IsEnabled = enabled;
            chkSizeRule.IsEnabled = enabled;
            txtSizeLimit.IsEnabled = enabled;
            chkCountRule.IsEnabled = enabled;
            txtCountLimit.IsEnabled = enabled;
            chkEmptyFolderRule.IsEnabled = enabled;
            txtEmptyFolderHours.IsEnabled = enabled;
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool timeTriggered = _config.EnableTimeRule &&
                        (DateTime.Now - _lastMoveTime).TotalSeconds >= _config.TimeIntervalSeconds;
                    bool movedAny = false;

                    // SourcePath 1
                    if (Directory.Exists(_config.SourcePath) && !string.IsNullOrWhiteSpace(_config.SourcePath1Profile))
                    {
                        var sftp = GetOrReconnectSession("SourcePath");
                        if (sftp != null)
                        {
                            if (timeTriggered || ShouldMoveFromSource(_config.SourcePath, sftp))
                            {
                                if (timeTriggered) Log($"[{_config.SourcePath}] 触发时间规则...");
                                await MoveFilesToSftpAsync(_config.SourcePath, sftp, token);
                                if (_config.EnableEmptyFolderRule) CleanEmptyFolders(_config.SourcePath, _config.EmptyFolderHours, token);
                                movedAny = true;
                            }
                        }
                        else
                        {
                            Log($"[{_config.SourcePath}] SFTP 未连接 ({_config.SourcePath1Profile})，跳过");
                        }
                    }

                    // SourcePath 2
                    if (Directory.Exists(_config.SourcePath2) && !string.IsNullOrWhiteSpace(_config.SourcePath2Profile))
                    {
                        var sftp = GetOrReconnectSession("SourcePath2");
                        if (sftp != null)
                        {
                            if (timeTriggered || ShouldMoveFromSource(_config.SourcePath2, sftp))
                            {
                                if (timeTriggered) Log($"[{_config.SourcePath2}] 触发时间规则...");
                                await MoveFilesToSftpAsync(_config.SourcePath2, sftp, token);
                                if (_config.EnableEmptyFolderRule) CleanEmptyFolders(_config.SourcePath2, _config.EmptyFolderHours, token);
                                movedAny = true;
                            }
                        }
                        else
                        {
                            string profileName = _config.SourcePath2Profile;
                            Log($"[{_config.SourcePath2}] SFTP 未连接 ({profileName})，跳过");
                        }
                    }

                    if (movedAny)
                        _lastMoveTime = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Log($"监控异常: {ex.Message}");
                }

                await Task.Delay(1000, token);
            }
        }

        private bool ShouldMoveFromSource(string source, ISftpService sftp)
        {
            if (!_config.EnableSizeRule && !_config.EnableCountRule)
                return false;

            DirectoryInfo di = new DirectoryInfo(source);
            if (!di.Exists) return false;

            FileInfo[] files = di.GetFiles("*", SearchOption.AllDirectories);
            long totalSize = files.Sum(f => f.Length);
            int fileCount = files.Length;

            if (_config.EnableCountRule && fileCount >= _config.CountLimit)
            {
                Log($"[{source}] 触发文件数量规则 (当前 {fileCount} 个)，开始上传...");
                return true;
            }
            if (_config.EnableSizeRule && totalSize >= _config.SizeLimitMB * 1024 * 1024)
            {
                Log($"[{source}] 触发文件空间规则 (当前 {totalSize / 1024.0 / 1024.0:F2} MB)，开始上传...");
                return true;
            }
            return false;
        }

        private Task MoveFilesToSftpAsync(string source, ISftpService sftp, CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(source);
                    FileInfo[] files = dir.GetFiles("*", SearchOption.AllDirectories);

                    string role = _session.CurrentRole;
                    bool isAppend = RoleEnforcer.ForcedTransferMode(role) == "Append"
                        || rbAppendMode.IsChecked == true;
                    bool isCopy = rbCopyMode.IsChecked == true
                        && RoleEnforcer.CanChooseTransferMode(role);
                    bool isCut = rbCutMode.IsChecked == true
                        && RoleEnforcer.CanChooseTransferMode(role);

                    int success = 0, skip = 0, fail = 0;

                    foreach (FileInfo file in files)
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            string relPath = file.FullName.Substring(source.Length)
                                .TrimStart('\\').Replace('\\', '/');

                            // Copy 模式：先做增量比对，跳过未变化的文件
                            if (isCopy && sftp.IsTargetFileCurrent(file.FullName, relPath))
                            {
                                skip++;
                                continue;
                            }

                            if (sftp.UploadFile(file.FullName, relPath, appendOnly: isAppend))
                            {
                                success++;
                                if (isCut) { try { file.Delete(); } catch { } }
                            }
                            else skip++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            Log($"上传 {file.Name} 失败: {ex.Message}");
                        }
                    }

                    string modeText = isAppend ? "追加" : (isCut ? "剪切" : "复制");
                    Log($"上传完成 ({modeText}): 成功 {success}, 跳过 {skip}, 失败 {fail}");
                }
                catch (Exception ex)
                {
                    Log($"上传异常: {ex.Message}");
                }
            }, token);
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

        #endregion

        #region Logging

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
                    listBox.Items.RemoveAt(0);
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
                            Directory.CreateDirectory(logDir);

                        string logFile = Path.Combine(logDir, $"Log_{DateTime.Now:yyyy-MM-dd}.txt");
                        File.AppendAllText(logFile, logMessage + Environment.NewLine);
                    }
                }
                catch { }
            });
        }

        #endregion

        #region Tab 2 — Process Monitoring
        // (unchanged — process monitoring logic preserved from previous implementation)

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

        private void CmbProcessInterval_Changed(object sender, SelectionChangedEventArgs e)
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
                    txtProcessPath.Text = dialog.FileName;
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
                _config.WatchProcesses.Add(vm.ToProcessInfo());
            SaveConfig();
        }

        #endregion

        #region Tab 3 — Version Update (SFTP)

        private void InitVersionUpdate()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            txtLocalVersion.Text = $"{ver.Major}.{ver.Minor}.{ver.Build}";

            if (!string.IsNullOrWhiteSpace(_config.LastCheckTime))
                txtLastCheckTime.Text = $"上次检查时间: {_config.LastCheckTime} | 检查间隔: {_config.CheckIntervalMinutes} 分钟";

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
                var sftp = _session.ActiveSftp;
                if (sftp == null)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtCloudVersion.Text = "未登录";
                        txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (请先登录)";
                    });
                    return;
                }

                if (!sftp.IsConnected)
                {
                    try { sftp.Connect(); } catch { }
                }

                if (!sftp.FileExists("versions/version.json"))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtCloudVersion.Text = "不可达";
                        txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (云端不可达)";
                    });
                    _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveConfig();
                    return;
                }

                string jsonText = sftp.ReadAllText("versions/version.json");
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
                StartUpdateDownload(version);
        }

        private async void StartUpdateDownload(string version)
        {
            Log($"开始下载更新 {version}...");
            btnCheckUpdate.IsEnabled = false;

            await Task.Run(() =>
            {
                try
                {
                    var sftp = _session.ActiveSftp;
                    if (sftp == null)
                    {
                        Dispatcher.InvokeAsync(() => Log("更新下载失败: 未登录"));
                        return;
                    }

                    string baseDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string appRoot = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
                    string tempDir = Path.Combine(appRoot, "versions", ".temp");

                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                    Directory.CreateDirectory(tempDir);

                    string remoteVerPath = "versions/" + version;
                    if (!sftp.FileExists(remoteVerPath + "/MoveImageForm.exe"))
                    {
                        Dispatcher.InvokeAsync(() =>
                            Log("更新下载失败: 云端版本文件不完整"));
                        return;
                    }

                    DownloadDirectoryFromSftp(sftp, remoteVerPath, tempDir);

                    if (!File.Exists(Path.Combine(tempDir, "MoveImageForm.exe")))
                    {
                        Dispatcher.InvokeAsync(() =>
                            Log("更新下载失败: 下载后文件不完整"));
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

        private void DownloadDirectoryFromSftp(ISftpService sftp, string remoteDir, string localDir)
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
                    sftp.DownloadFile(remotePath, localPath);
                }
            }
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
echo 正在更新 VP运维工具 到版本 {version}...

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
                string appName = "VP运维工具";
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

        #region Tab 4 — Account Management

        private void BtnChangePassword_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var profile = (SftpProfile)btn.Tag;

            var dialog = new ChangePasswordDialog(profile.Name);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                profile.Password = dialog.NewPassword;
                profile.EncryptPassword();
                SaveConfig();
                RefreshAccountList();
                Log($"[系统] 账号「{profile.Name}」密码已修改（已加密保存）");
            }
        }

        private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddAccountDialog();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                // 加密密码后保存
                dialog.Profile.EncryptPassword();
                _config.SftpProfiles.Add(dialog.Profile);
                SaveConfig();
                RefreshAccountList();
                UpdateSourceProfileDropdowns();
                Log($"[系统] 已添加账号「{dialog.Profile.Name}」({dialog.Profile.Role}) — 密码已加密");
            }
        }

        private void RefreshAccountList()
        {
            lstAccounts.ItemsSource = null;
            lstAccounts.ItemsSource = _config.SftpProfiles;
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
            _isLoadingConfig = true;
            try
            {
                string path = GetConfigFilePath();
                _config = new AppConfig();
                bool needsSave = false;

                if (File.Exists(path))
                {
                    if (TryDeserializeAppConfig(path, out AppConfig loaded))
                    {
                        _config = loaded;
                    }
                    else if (TryParseConfigXml(path, out loaded))
                    {
                        _config = loaded;
                        needsSave = true;
                        Log("已从旧版 Config 格式读取配置，将自动转换。");
                    }
                    else
                    {
                        BackupCorruptConfig(path);
                    }
                }

                foreach (string legacyPath in GetLegacyConfigCandidates(path))
                {
                    if (!TryLoadConfigFromAnyFormat(legacyPath, out AppConfig legacy))
                        continue;

                    if (MergeConfig(_config, legacy))
                    {
                        needsSave = true;
                        Log($"已从 {Path.GetFileName(legacyPath)} 迁移缺失的配置项。");
                    }
                }

                // 解密所有存储的密码
                foreach (var profile in _config.SftpProfiles)
                {
                    if (profile.IsPasswordEncrypted)
                    {
                        // 密码保持加密状态在内存中也有问题 — 我们只在使用时才解密
                        // 但为了 SaveConfig 时能正确处理，让加密密码留在内存中
                        // 使用时通过 GetPlainPassword() 获取明文
                    }
                }

                if (needsSave)
                    SaveConfig();
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        private string GetLocalConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");
        }

        private IEnumerable<string> GetLegacyConfigCandidates(string primaryPath)
        {
            var candidates = new List<string>();
            string localConfig = GetLocalConfigPath();
            string fullPrimary = Path.GetFullPath(primaryPath);
            if (!string.Equals(Path.GetFullPath(localConfig), fullPrimary, StringComparison.OrdinalIgnoreCase))
                candidates.Add(localConfig);

            candidates.Add(primaryPath + ".bak");

            return candidates
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private bool TryLoadConfigFromAnyFormat(string path, out AppConfig config)
        {
            return TryDeserializeAppConfig(path, out config) || TryParseConfigXml(path, out config);
        }

        private bool TryDeserializeAppConfig(string path, out AppConfig config)
        {
            config = null;
            try
            {
                var serializer = new XmlSerializer(typeof(AppConfig));
                using (var reader = new StreamReader(path))
                {
                    config = (AppConfig)serializer.Deserialize(reader);
                }
                return config != null;
            }
            catch
            {
                return false;
            }
        }

        private bool TryParseConfigXml(string path, out AppConfig config)
        {
            config = null;
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                var root = doc.DocumentElement;
                if (root == null || (root.Name != "AppConfig" && root.Name != "Config"))
                    return false;

                config = new AppConfig
                {
                    SourcePath = GetXmlText(doc, "SourcePath"),
                    SourcePath2 = GetXmlText(doc, "SourcePath2"),
                    SourcePath1Profile = GetXmlText(doc, "SourcePath1Profile"),
                    SourcePath2Profile = GetXmlText(doc, "SourcePath2Profile"),
                    TransferMode = GetXmlText(doc, "TransferMode", "Cut"),
                    EnableTimeRule = GetXmlBool(doc, "EnableTimeRule", true),
                    TimeIntervalSeconds = GetXmlInt(doc, "TimeIntervalSeconds", 60),
                    EnableSizeRule = GetXmlBool(doc, "EnableSizeRule", false),
                    SizeLimitMB = GetXmlLong(doc, "SizeLimitMB", 100),
                    EnableCountRule = GetXmlBool(doc, "EnableCountRule", false),
                    CountLimit = GetXmlInt(doc, "CountLimit", 1000),
                    EnableEmptyFolderRule = GetXmlBool(doc, "EnableEmptyFolderRule", false),
                    EmptyFolderHours = GetXmlDouble(doc, "EmptyFolderHours", 24.0),
                    SftpProfiles = ParseSftpProfiles(doc),
                    CheckIntervalMinutes = GetXmlInt(doc, "CheckIntervalMinutes", 30),
                    AutoUpdate = GetXmlBool(doc, "AutoUpdate", false),
                    AutoStart = GetXmlBool(doc, "AutoStart", false),
                    LastCheckTime = GetXmlText(doc, "LastCheckTime"),
                    ProcessCheckIntervalSeconds = GetXmlInt(doc, "ProcessCheckIntervalSeconds", 5),
                    WatchProcesses = ParseWatchProcesses(doc)
                };

                // 兼容旧配置：没有 SourcePath1Profile 时，从 ActiveProfile 或旧格式回退
                if (string.IsNullOrWhiteSpace(config.SourcePath1Profile) && !string.IsNullOrWhiteSpace(config.SourcePath))
                {
                    string activeProfile = GetXmlText(doc, "ActiveProfile");
                    config.SourcePath1Profile = activeProfile;
                }
                if (string.IsNullOrWhiteSpace(config.SourcePath2Profile) && !string.IsNullOrWhiteSpace(config.SourcePath2))
                {
                    // SourcePath2 可能使用同一个 profile
                    if (string.IsNullOrWhiteSpace(config.SourcePath2Profile))
                        config.SourcePath2Profile = config.SourcePath1Profile;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<SftpProfile> ParseSftpProfiles(XmlDocument doc)
        {
            var profiles = new List<SftpProfile>();
            var nodes = doc.SelectNodes("//SftpProfiles/Profile");
            if (nodes == null) return profiles;

            foreach (XmlNode node in nodes)
            {
                profiles.Add(new SftpProfile
                {
                    Name = node.SelectSingleNode("Name")?.InnerText?.Trim() ?? "",
                    Role = node.SelectSingleNode("Role")?.InnerText?.Trim() ?? "upload",
                    Host = node.SelectSingleNode("Host")?.InnerText?.Trim() ?? "",
                    Port = int.TryParse(node.SelectSingleNode("Port")?.InnerText?.Trim(), out int port) ? port : 22,
                    Username = node.SelectSingleNode("Username")?.InnerText?.Trim() ?? "",
                    Password = node.SelectSingleNode("Password")?.InnerText?.Trim() ?? "",
                    RemoteRoot = node.SelectSingleNode("RemoteRoot")?.InnerText?.Trim() ?? "/"
                });
            }
            return profiles;
        }

        private static List<ProcessInfo> ParseWatchProcesses(XmlDocument doc)
        {
            var processes = new List<ProcessInfo>();
            var nodes = doc.SelectNodes("//WatchProcesses/Process");
            if (nodes == null) return processes;

            foreach (XmlNode node in nodes)
            {
                processes.Add(new ProcessInfo
                {
                    Name = node.SelectSingleNode("Name")?.InnerText?.Trim() ?? "",
                    Path = node.SelectSingleNode("Path")?.InnerText?.Trim() ?? "",
                    Enabled = bool.TryParse(node.SelectSingleNode("Enabled")?.InnerText?.Trim(), out bool enabled) && enabled
                });
            }
            return processes;
        }

        private static string GetXmlText(XmlDocument doc, string name, string defaultValue = "")
        {
            string value = doc.SelectSingleNode($"//{name}")?.InnerText?.Trim();
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        private static bool GetXmlBool(XmlDocument doc, string name, bool defaultValue)
        {
            string value = doc.SelectSingleNode($"//{name}")?.InnerText?.Trim();
            return string.IsNullOrEmpty(value) ? defaultValue : bool.TryParse(value, out bool result) && result;
        }

        private static int GetXmlInt(XmlDocument doc, string name, int defaultValue)
        {
            string value = doc.SelectSingleNode($"//{name}")?.InnerText?.Trim();
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static long GetXmlLong(XmlDocument doc, string name, long defaultValue)
        {
            string value = doc.SelectSingleNode($"//{name}")?.InnerText?.Trim();
            return long.TryParse(value, out long result) ? result : defaultValue;
        }

        private static double GetXmlDouble(XmlDocument doc, string name, double defaultValue)
        {
            string value = doc.SelectSingleNode($"//{name}")?.InnerText?.Trim();
            return double.TryParse(value, out double result) ? result : defaultValue;
        }

        private bool MergeConfig(AppConfig target, AppConfig source)
        {
            bool changed = false;
            if (MergeString(target.SourcePath, source.SourcePath, out string sourcePath))
            { target.SourcePath = sourcePath; changed = true; }
            if (MergeString(target.SourcePath2, source.SourcePath2, out string sourcePath2))
            { target.SourcePath2 = sourcePath2; changed = true; }
            if (MergeString(target.SourcePath1Profile, source.SourcePath1Profile, out string sp1p))
            { target.SourcePath1Profile = sp1p; changed = true; }
            if (MergeString(target.SourcePath2Profile, source.SourcePath2Profile, out string sp2p))
            { target.SourcePath2Profile = sp2p; changed = true; }
            if (MergeString(target.LastCheckTime, source.LastCheckTime, out string lastCheckTime))
            { target.LastCheckTime = lastCheckTime; changed = true; }

            if (string.IsNullOrWhiteSpace(target.TransferMode) && !string.IsNullOrWhiteSpace(source.TransferMode))
            { target.TransferMode = source.TransferMode; changed = true; }

            if (target.SftpProfiles.Count == 0 && source.SftpProfiles != null && source.SftpProfiles.Count > 0)
            { target.SftpProfiles = source.SftpProfiles; changed = true; }

            if (target.WatchProcesses.Count == 0 && source.WatchProcesses != null && source.WatchProcesses.Count > 0)
            { target.WatchProcesses = source.WatchProcesses; changed = true; }

            return changed;
        }

        private static bool MergeString(string target, string source, out string result)
        {
            result = target;
            if (!string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
                return false;

            result = source;
            return true;
        }

        private void BackupCorruptConfig(string path)
        {
            string backupPath = path + ".bak";
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(path, backupPath);
                Log($"配置文件已损坏，已备份为 {Path.GetFileName(backupPath)}，将尝试从其他位置迁移配置。");
            }
            catch (Exception ex)
            {
                Log($"配置文件读取失败: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            if (_isLoadingConfig) return;

            try
            {
                // 加密所有 Profile 密码后再序列化
                foreach (var profile in _config.SftpProfiles)
                {
                    if (!profile.IsPasswordEncrypted && !string.IsNullOrEmpty(profile.Password))
                        profile.EncryptPassword();
                }

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
            _isLoadingConfig = true;
            try
            {
                txtSourcePath.Text = _config.SourcePath;
                txtSourcePath2.Text = _config.SourcePath2;

                // 填充 Profile 下拉框
                UpdateSourceProfileDropdowns();

                // 选中当前绑定的 Profile
                SelectProfileInCombo(cmbSource1Profile, _config.SourcePath1Profile);
                SelectProfileInCombo(cmbSource2Profile, _config.SourcePath2Profile);

                // TransferMode 映射
                if (_config.TransferMode == "Append") rbAppendMode.IsChecked = true;
                else if (_config.TransferMode == "Copy") rbCopyMode.IsChecked = true;
                else rbCutMode.IsChecked = true;

                chkTimeRule.IsChecked = _config.EnableTimeRule;
                txtTimeInterval.Text = _config.TimeIntervalSeconds.ToString();

                chkSizeRule.IsChecked = _config.EnableSizeRule;
                txtSizeLimit.Text = _config.SizeLimitMB.ToString();

                chkCountRule.IsChecked = _config.EnableCountRule;
                txtCountLimit.Text = _config.CountLimit.ToString();

                chkEmptyFolderRule.IsChecked = _config.EnableEmptyFolderRule;
                txtEmptyFolderHours.Text = _config.EmptyFolderHours.ToString();

                // 账号列表
                lstAccounts.ItemsSource = _config.SftpProfiles;

                // 根据登录状态更新 UI
                ApplyRoleRestrictions();
                UpdateTransferStatusBar();
                UpdateLoginButton();
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        private void UpdateSourceProfileDropdowns()
        {
            cmbSource1Profile.ItemsSource = null;
            cmbSource1Profile.ItemsSource = _config.SftpProfiles;

            cmbSource2Profile.ItemsSource = null;
            cmbSource2Profile.ItemsSource = _config.SftpProfiles;
        }

        private void SelectProfileInCombo(ComboBox combo, string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return;
            foreach (var item in combo.Items)
            {
                if (item is SftpProfile p && p.Name == profileName)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void UpdateConfigFromUI()
        {
            if (_isLoadingConfig) return;

            _config.SourcePath = txtSourcePath.Text;
            _config.SourcePath2 = txtSourcePath2.Text;

            // 保存 Profile 选择
            _config.SourcePath1Profile = (cmbSource1Profile.SelectedItem as SftpProfile)?.Name ?? "";
            _config.SourcePath2Profile = (cmbSource2Profile.SelectedItem as SftpProfile)?.Name ?? "";

            _config.TransferMode = rbAppendMode.IsChecked == true ? "Append"
                : rbCopyMode.IsChecked == true ? "Copy" : "Cut";

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
