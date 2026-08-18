using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
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

        // 动态源文件夹列表
        private ObservableCollection<SourceFolderEntry> _sourceFolders
            = new ObservableCollection<SourceFolderEntry>();

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
                // 搬运进行中：弹确认框
                if (_isRunning)
                {
                    var result = MessageBox.Show(
                        "文件搬运正在进行中，登出将自动停止所有搬运任务。\n\n是否确认登出？",
                        "搬运进行中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                        return;
                    StopMoving();
                }

                // v1.0.3+：登出时保留已保存的密码，退出再登录/重启/断线重连无需重新输入
                _session.Logout();
                Log("[系统] 已登出");
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

            var profiles = new List<SftpProfile>();
            foreach (var name in activeProfileNames)
            {
                var p = _config.FindProfile(name);
                if (p != null) profiles.Add(p);
            }

            if (profiles.Count == 0)
            {
                MessageBox.Show("未找到已配置的账号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 逐个登录每个活跃账号（SMB 自动连接，无需弹窗输密码）
            int connected = 0;
            foreach (var profile in profiles)
            {
                if (profile.IsSmb)
                {
                    // SMB 无需密码，直接连接
                    try
                    {
                        _session.Login(profile, "");
                        connected++;
                        Log($"[系统] SMB 已就绪: {profile.Name} → {profile.RemoteRoot}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[系统] SMB 连接失败: {ex.Message}");
                    }
                    continue;
                }

                // v1.0.3+：已保存密码的账号直接自动登录，无需再输密码；
                // 密码已失效（服务器改密等）时自动回退到下方对话框重新输入
                string savedPassword = "";
                try { savedPassword = profile.GetPlainPassword(); }
                catch { /* 解密失败，当作无保存密码 */ }

                if (!string.IsNullOrEmpty(savedPassword))
                {
                    try
                    {
                        _session.Login(profile, savedPassword);
                        connected++;
                        Log($"[系统] 已连接: {profile.Name} → {profile.RemoteRoot}");
                        continue;
                    }
                    catch
                    {
                        Log($"[系统] 保存的密码连接 {profile.Name} 失败，请重新输入密码");
                    }
                }

                var dialog = new LoginDialog(new List<SftpProfile> { profile });
                dialog.Owner = this;
                dialog.Title = $"登录 - {profile.Name} ({profile.TransportType})";
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        _session.Login(dialog.SelectedProfile, dialog.Password);
                        connected++;
                        Log($"[系统] 已连接: {dialog.SelectedProfile.Name} → {dialog.SelectedProfile.RemoteRoot}");

                        // 保存密码到配置（DPAPI 加密），下次启动自动登录
                        var p = _config.FindProfile(dialog.SelectedProfile.Name);
                        if (p != null)
                        {
                            p.Password = dialog.Password;
                            SaveConfig(); // 内部自动 DPAPI 加密
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"连接 {profile.Name} 失败: {ex.Message}", "登录失败",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            if (connected > 0)
                Log($"[系统] 已连接 {connected}/{profiles.Count} 个账号。");
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
            // 搬运进行中：禁用登出按钮
            if (_isRunning)
            {
                btnLogin.IsEnabled = false;
                btnLogin.Content = "⏳ 搬运中...";
                btnLogin.ToolTip = "文件搬运进行中，请先停止搬运再登出";
                spLoginStatus.Visibility = _session.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (_session.IsLoggedIn)
            {
                btnLogin.IsEnabled = true;
                btnLogin.Content = "登出";
                btnLogin.ToolTip = null;
                spLoginStatus.Visibility = Visibility.Visible;

                var parts = new List<string>();
                foreach (var name in _session.ConnectedProfiles)
                {
                    var p = _config.FindProfile(name);
                    if (p != null)
                        parts.Add($"{p.Name}({p.Role})→{p.RemoteRoot}");
                }
                txtLoginInfo.Text = string.Join(" | ", parts);

                // 登录后锁定账号选择器，防止传输中途切换账号
                SetSourceProfileComboBoxesEnabled(false);
            }
            else
            {
                btnLogin.IsEnabled = true;
                btnLogin.Content = "登录";
                btnLogin.ToolTip = null;
                spLoginStatus.Visibility = Visibility.Collapsed;
                txtLoginInfo.Text = "";

                // 登出后恢复账号选择器
                SetSourceProfileComboBoxesEnabled(true);
            }
        }

        private void ApplyRoleRestrictions()
        {
            string role = _session.CurrentRole;

            bool canUpload = RoleEnforcer.CanUpload(role);
            bool canChooseMode = RoleEnforcer.CanChooseTransferMode(role);

            btnStart.IsEnabled = canUpload;

            // 更新每个目录的传输模式下拉框权限
            SetSourceTransferModeComboBoxesEnabled(canChooseMode);

            string forced = RoleEnforcer.ForcedTransferMode(role);
            if (forced == "Append")
            {
                // 强制仅追加：更新所有条目
                foreach (var entry in _sourceFolders)
                    entry.TransferMode = "Append";
            }
            if (forced == "None") btnStart.IsEnabled = false;
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
            // 账号管理 Tab 已对所有人开放（包括未登录状态），不再做权限拦截
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
                if (_isRunning)
                {
                    var result = MessageBox.Show(
                        "文件搬运正在进行中，退出将停止所有任务。\n\n是否确认退出？",
                        "搬运进行中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                        return;
                    StopMoving();
                }
                UpdateConfigFromUI();
                SaveConfig();
                _session.Logout();
                _notifyIcon.Visible = false;
                System.Windows.Application.Current.Shutdown();
                // Shutdown 会触发 Window_Closing，Dispose 放在 Shutdown 之后
                _notifyIcon.Dispose();
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

            // v1.0.3+：开机自启动写死启用（无界面选项，每次启动确保注册表 Run 键存在）
            EnsureAutoStart();

            // 自动登录：用已保存的密码连接活跃账号，成功后自动开始搬运
            // （开机自启动 = 启动软件 + 登录账号 + 开始搬运）
            TryAutoLogin();

            InitProcessMonitoring();
        }

        /// <summary>
        /// 自动登录：用已保存的（DPAPI 加密）密码连接活跃账号（后台，不阻塞 UI）。
        /// v1.0.3+：开机时网络可能未就绪，最多重试 10 次（每 30 秒一次，约 5 分钟）；
        /// 登录成功后自动开始搬运（开机自启动 = 启动软件 + 登录账号 + 开始搬运）。
        /// </summary>
        private async void TryAutoLogin()
        {
            var activeProfiles = new List<SftpProfile>();
            foreach (var name in _config.GetActiveProfileNames().Distinct())
            {
                var p = _config.FindProfile(name);
                if (p == null) continue;
                // SMB 无密码也可自动连接；SFTP/S3 需要有密码
                if (p.IsSmb || !string.IsNullOrEmpty(p.Password))
                    activeProfiles.Add(p);
            }

            if (activeProfiles.Count == 0)
            {
                Log("请点击左上角「登录」连接 SFTP/S3 服务器。");
                Log("提示：每个监控文件夹可以绑定不同的账号和远程路径。");
                return;
            }

            for (int attempt = 1; attempt <= 10; attempt++)
            {
                Log($"[系统] 自动登录（第 {attempt} 次，共 {activeProfiles.Count} 个账号）...");
                int success = await Task.Run(() => _session.LoginAll(activeProfiles));
                if (success > 0)
                {
                    Log($"[系统] 自动登录成功: {success}/{activeProfiles.Count} 个账号。");
                    // 自动开始搬运（StartMoving 内部有 _isRunning 守卫和各项校验）
                    Dispatcher.Invoke(() =>
                    {
                        if (!_isRunning)
                            StartMoving();
                    });
                    return;
                }

                if (attempt < 10)
                {
                    Log("[系统] 自动登录失败（网络或服务器未就绪），30 秒后重试...");
                    await Task.Delay(30_000);
                }
            }

            Log("自动登录多次失败，请手动点击「登录」后开始搬运。");
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

        private void BtnBrowseSourceGeneric_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var entry = button?.DataContext as SourceFolderEntry;
            if (entry == null) return;

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = $"选择监控文件夹 - {entry.Label}";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    entry.Path = dialog.SelectedPath;
            }
        }

        private void BtnAddSourceFolder_Click(object sender, RoutedEventArgs e)
        {
            var entry = new SourceFolderEntry
            {
                TransferMode = _config?.TransferMode ?? "Cut"
            };
            _sourceFolders.Add(entry);
            ReindexSourceFolders();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var entry = button?.DataContext as SourceFolderEntry;
            if (entry == null) return;

            _sourceFolders.Remove(entry);
            ReindexSourceFolders();
        }

        private void CmbSourceProfile_Loaded(object sender, RoutedEventArgs e)
        {
            var cmb = sender as ComboBox;
            if (_config?.SftpProfiles == null) return;

            cmb.ItemsSource = _config.SftpProfiles;
            var entry = cmb.DataContext as SourceFolderEntry;
            if (entry == null) return;

            // 选中当前绑定的 Profile
            if (!string.IsNullOrEmpty(entry.ProfileName))
            {
                foreach (var item in cmb.Items)
                {
                    if (item is SftpProfile p && p.Name == entry.ProfileName)
                    {
                        cmb.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void CmbSourceProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingConfig) return;

            var cmb = sender as ComboBox;
            var entry = cmb?.DataContext as SourceFolderEntry;
            if (entry == null) return;

            entry.ProfileName = (cmb.SelectedItem as SftpProfile)?.Name ?? "";
        }

        private void CmbTransferMode_Loaded(object sender, RoutedEventArgs e)
        {
            var cmb = sender as ComboBox;
            if (cmb == null || cmb.Items.Count > 0) return;

            cmb.Items.Add(new ComboBoxItem { Content = "仅追加", Tag = "Append" });
            cmb.Items.Add(new ComboBoxItem { Content = "复制", Tag = "Copy" });
            cmb.Items.Add(new ComboBoxItem { Content = "剪切", Tag = "Cut" });

            // 根据角色权限禁用不可选模式
            string role = _session.CurrentRole;
            bool canChoose = RoleEnforcer.CanChooseTransferMode(role);
            if (!canChoose)
            {
                // 非 admin/upload 角色只能选仅追加
                foreach (ComboBoxItem item in cmb.Items)
                {
                    if (item.Tag as string != "Append")
                        item.IsEnabled = false;
                }
            }
        }

        /// <summary>遍历 ItemsControl 中所有 TransferMode ComboBox，统一设置启用/禁用</summary>
        private void SetSourceTransferModeComboBoxesEnabled(bool canChooseMode)
        {
            foreach (var entry in _sourceFolders)
            {
                var container = icSourceFolders.ItemContainerGenerator.ContainerFromItem(entry);
                if (container == null) continue;

                // 在可视树中找所有 ComboBox（第 0 个是 Profile，第 1 个是 TransferMode）
                var combos = FindVisualChildren<ComboBox>(container).ToList();
                var transferCmb = combos.Count >= 2 ? combos[1] : null;
                if (transferCmb == null) continue;

                transferCmb.IsEnabled = canChooseMode;
                foreach (ComboBoxItem item in transferCmb.Items)
                {
                    if (item.Tag as string == "Append")
                        item.IsEnabled = true; // 仅追加始终可用
                    else
                        item.IsEnabled = canChooseMode;
                }
            }
        }

        /// <summary>在可视树中查找所有指定类型的子元素</summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        /// <summary>重新编号：每个条目 Index = 其在列表中的位置</summary>
        private void ReindexSourceFolders()
        {
            for (int i = 0; i < _sourceFolders.Count; i++)
                _sourceFolders[i].Index = i;
        }

        /// <summary>遍历 ItemsControl 中所有 ComboBox，统一设置启用/禁用</summary>
        private void SetSourceProfileComboBoxesEnabled(bool enabled)
        {
            foreach (var entry in _sourceFolders)
            {
                var container = icSourceFolders.ItemContainerGenerator.ContainerFromItem(entry);
                if (container == null) continue;
                var cmb = FindVisualChild<ComboBox>(container);
                if (cmb != null) cmb.IsEnabled = enabled;
            }
        }

        /// <summary>在可视树中查找指定类型的子元素</summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        #endregion

        #region Tab 1 — File Transfer (SFTP)

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            StartMoving();
        }

        /// <summary>获取指定 SourceFolderEntry 对应的传输会话（自动重连，SMB 按需创建）</summary>
        private IFileTransferService GetOrReconnectSession(SourceFolderEntry entry)
        {
            var profile = _config.GetProfileForSource(entry);
            if (profile == null) return null;

            var session = _session.GetSession(profile.Name);
            if (session != null && session.IsConnected) return session;

            // SMB 无需认证，按需直接创建新会话（不依赖密码缓存）
            if (profile.IsSmb)
            {
                var smb = new SmbService(profile.RemoteRoot);
                try { smb.Connect(); } catch { return null; }
                _session.RegisterSession(profile, smb, "");
                return smb;
            }

            // SFTP/S3: 尝试用缓存的密码重连
            if (_session.Reconnect(profile.Name))
                return _session.GetSession(profile.Name);

            return null;
        }

        private void StartMoving()
        {
            if (_isRunning) return; // 防止重复启动
            UpdateConfigFromUI();

            var activeProfiles = _config.GetActiveProfileNames().Distinct().ToList();
            if (activeProfiles.Count == 0)
            {
                MessageBox.Show("请先在源文件夹区域为监控目录分配账号！");
                return;
            }

            // SMB 账号不需要登录即可传输；其他协议必须已登录
            bool hasNonSmbProfile = activeProfiles.Any(name =>
            {
                var p = _config.FindProfile(name);
                return p != null && !p.IsSmb;
            });

            if (!_session.IsLoggedIn && hasNonSmbProfile)
            {
                MessageBox.Show("请先点击左上角「登录」连接传输服务器！\n（选择了 SFTP/S3 账号，需要先登录）",
                    "未登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 获取有效角色：如果所有活跃账号都是 SMB，无需登录即有 admin 权限
            string role = _session.CurrentRole;
            if (string.IsNullOrEmpty(role))
            {
                bool allSmb = activeProfiles.Count > 0 && activeProfiles.All(name =>
                {
                    var p = _config.FindProfile(name);
                    return p != null && p.IsSmb;
                });
                if (allSmb) role = "admin";
            }

            if (!RoleEnforcer.CanUpload(role))
            {
                MessageBox.Show($"当前账号「{_session.CurrentRole}」没有上传权限！", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool anyValid = _sourceFolders.Any(sf =>
                !string.IsNullOrWhiteSpace(sf.Path) && Directory.Exists(sf.Path));

            if (!anyValid)
            {
                MessageBox.Show("请至少设置一组有效的监控文件夹！");
                return;
            }

            SaveConfig();

            _isRunning = true;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            SetUIEnabled(false);

            // 搬运期间禁止登出
            Dispatcher.Invoke(() => UpdateLoginButton());

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
                    // 停止后重新应用登录状态和角色权限
                    UpdateLoginButton();
                    ApplyRoleRestrictions();
                    Log("停止监控。");
                });
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            icSourceFolders.IsEnabled = enabled;
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

                    // 获取当前活跃的源文件夹快照（UI 线程）
                    List<SourceFolderEntry> activeEntries = null;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        activeEntries = _sourceFolders
                            .Where(sf => Directory.Exists(sf.Path) && !string.IsNullOrWhiteSpace(sf.ProfileName))
                            .ToList();
                    });

                    if (activeEntries != null)
                    {
                        foreach (var entry in activeEntries)
                        {
                            if (token.IsCancellationRequested) break;

                            var sftp = GetOrReconnectSession(entry);
                            if (sftp != null)
                            {
                                if (timeTriggered || ShouldMoveFromSource(entry.Path, sftp))
                                {
                                    if (timeTriggered) Log($"[{entry.Path}] 触发时间规则...");
                                    await MoveFilesToSftpAsync(entry.Path, sftp, entry.TransferMode, token);
                                    if (_config.EnableEmptyFolderRule) CleanEmptyFolders(entry.Path, _config.EmptyFolderHours, token);
                                    movedAny = true;
                                }
                            }
                            else
                            {
                                Log($"[{entry.Path}] 传输未连接 ({entry.ProfileName})，跳过");
                            }
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

        private bool ShouldMoveFromSource(string source, IFileTransferService sftp)
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

        private Task MoveFilesToSftpAsync(string source, IFileTransferService sftp, string transferMode, CancellationToken token)
        {
            // 根据角色权限和条目设置决定实际传输模式
            string role = _session.CurrentRole;
            string effectiveMode = RoleEnforcer.ForcedTransferMode(role) == "Append" ? "Append" : (transferMode ?? "Cut");
            if (!RoleEnforcer.CanChooseTransferMode(role) && effectiveMode != "Append")
                effectiveMode = "Append";

            bool isAppend = effectiveMode == "Append";
            bool isCopy = effectiveMode == "Copy";
            bool isCut = effectiveMode == "Cut";
            string modeText = isAppend ? "追加" : (isCut ? "剪切" : "复制");

            return Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                long bytesTransferred = 0;
                int lastReportedPercent = -1;

                try
                {
                    DirectoryInfo dir = new DirectoryInfo(source);
                    FileInfo[] files = dir.GetFiles("*", SearchOption.AllDirectories);

                    int totalFiles = files.Length;
                    long totalBytes = files.Sum(f => f.Length);
                    int success = 0, skip = 0, fail = 0;
                    int processed = 0;

                    Log($"[开始] {source} | 模式: {modeText} | 共 {totalFiles} 个文件, {totalBytes / 1048576.0:F1} MB");

                    foreach (FileInfo file in files)
                    {
                        if (token.IsCancellationRequested)
                        {
                            Log($"[中断] {source} | 已处理 {processed}/{totalFiles} | 成功 {success} | 跳过 {skip} | 失败 {fail}");
                            return;
                        }
                        try
                        {
                            string relPath = file.FullName.Substring(source.Length)
                                .TrimStart('\\').Replace('\\', '/');

                            // Copy 模式：先做增量比对，跳过未变化的文件
                            if (isCopy && sftp.IsTargetFileCurrent(file.FullName, relPath))
                            {
                                skip++;
                                processed++;
                                continue;
                            }

                            if (sftp.UploadFile(file.FullName, relPath, appendOnly: isAppend))
                            {
                                success++;
                                bytesTransferred += file.Length;
                                if (isCut) { try { file.Delete(); } catch { } }
                            }
                            else skip++;

                            processed++;

                            // 每 20% 输出一次进度
                            int percent = totalFiles > 0 ? processed * 100 / totalFiles : 100;
                            int milestone = (percent / 20) * 20;
                            if (milestone > lastReportedPercent || (percent >= 100 && lastReportedPercent < 100))
                            {
                                lastReportedPercent = milestone;
                                double mbDone = bytesTransferred / 1048576.0;
                                double elapsed = sw.Elapsed.TotalSeconds;
                                double speed = elapsed > 0 ? mbDone / elapsed : 0;
                                Log($"[进度 {percent}%] 已处理 {processed}/{totalFiles} | 成功 {success} | 跳过 {skip} | 失败 {fail} | 已传输 {mbDone:F1} MB | 速率 {speed:F1} MB/s");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            processed++;
                            Log($"上传 {file.Name} 失败: {ex.Message}");
                        }
                    }

                    sw.Stop();
                    double totalMb = bytesTransferred / 1048576.0;
                    double avgSpeed = sw.Elapsed.TotalSeconds > 0 ? totalMb / sw.Elapsed.TotalSeconds : 0;
                    Log($"[完成] {source} | 成功 {success} | 跳过 {skip} | 失败 {fail} | 耗时 {sw.Elapsed.Minutes}分{sw.Elapsed.Seconds}秒 | 总传输 {totalMb:F1} MB | 平均速率 {avgSpeed:F1} MB/s");
                }
                catch (Exception ex)
                {
                    sw.Stop();
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

        #region Tab 3 — 开机自启动（v1.0.3+：写死启用，无界面选项）

        /// <summary>开机自启动：注册表 Run 键写入（优先 Launcher.exe，找不到时用自身），每次启动确保存在</summary>
        private void EnsureAutoStart()
        {
            try
            {
                // 值名用独立名称：联想等启动管理器已把旧名"VP运维工具"记为"已禁用"并在开机时强制覆盖，
                // 换名可绕开该历史决定（此类工具默认不会自动禁用未知的新启动项）
                string appName = "VP运维工具自启";
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string launcherPath = Path.Combine(
                    Path.GetDirectoryName(exePath), "..", "..", "Launcher.exe");
                string autoStartPath = Path.GetFullPath(launcherPath);

                var regKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (regKey == null) return;

                // 联想电脑管家等工具会把"已禁用"的启动项移入 Run\LenovoDisabled（值数据以 rem| 开头），
                // 并在每次开机时强制维持禁用状态。本软件写死开机自启，需先清除这条禁用记录，否则写回会被再次移除
                try
                {
                    foreach (var subName in regKey.GetSubKeyNames())
                    {
                        if (!subName.EndsWith("Disabled", StringComparison.OrdinalIgnoreCase))
                            continue;
                        using (var subKey = regKey.OpenSubKey(subName, true))
                        {
                            subKey?.DeleteValue(appName, false);
                        }
                    }
                }
                catch { /* 清理失败不阻断主流程 */ }

                string targetValue = File.Exists(autoStartPath)
                    ? $"\"{autoStartPath}\""
                    : $"\"{exePath}\"";

                // 已设置相同的值则跳过，避免每次启动重复写入/刷日志
                if (regKey.GetValue(appName) as string == targetValue)
                    return;

                regKey.SetValue(appName, targetValue);
                Log("已设置开机自启动（默认开启）");
            }
            catch (Exception ex)
            {
                Log($"设置开机自启动失败: {ex.Message}");
            }
        }

        #endregion

        #region Tab 4 — Account Management

        private void BtnEditAccount_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var profile = (SftpProfile)btn.Tag;

            var dialog = new AddAccountDialog(profile);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                // 密码有变动时需要重新加密
                if (!string.IsNullOrEmpty(dialog.Profile.Password)
                    && !DpapiHelper.IsEncrypted(dialog.Profile.Password))
                {
                    dialog.Profile.EncryptPassword();
                }
                SaveConfig();
                RefreshAccountList();
                UpdateSourceProfileDropdowns();
                Log($"[系统] 账号「{dialog.Profile.Name}」已更新");
            }
        }

        private void BtnAddAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AddAccountDialog();
                dialog.Owner = this;
                if (dialog.ShowDialog() == true && dialog.Profile != null)
                {
                    // 加密密码后保存
                    dialog.Profile.EncryptPassword();
                    if (_config == null) _config = new AppConfig();
                    if (_config.SftpProfiles == null) _config.SftpProfiles = new System.Collections.Generic.List<Models.SftpProfile>();
                    _config.SftpProfiles.Add(dialog.Profile);
                    SaveConfig();
                    RefreshAccountList();
                    UpdateSourceProfileDropdowns();
                    Log($"[系统] 已添加账号「{dialog.Profile.Name}」({dialog.Profile.Role}) — 密码已加密");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"添加账号失败:\n{ex.Message}\n\n堆栈:\n{ex.StackTrace}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

                // 确保 SMB 系统账号存在（内置账号，不可删除）
                if (!_config.SftpProfiles.Any(p => p.IsSmb))
                {
                    _config.SftpProfiles.Insert(0, new SftpProfile
                    {
                        Name = SftpProfile.SmbProfileName,
                        TransportType = "SMB",
                        Role = "admin",
                        RemoteRoot = ""
                    });
                    needsSave = true;
                    Log("已添加内置「SMB传输」账号，可在账号管理中编辑目标文件夹。");
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
                    SourceFolders = ParseSourceFolders(doc),
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

        private static List<SourceFolderEntry> ParseSourceFolders(XmlDocument doc)
        {
            var folders = new List<SourceFolderEntry>();
            var nodes = doc.SelectNodes("//SourceFolders/SourceFolder");
            if (nodes == null) return folders;

            foreach (XmlNode node in nodes)
            {
                string transferMode = node.SelectSingleNode("TransferMode")?.InnerText?.Trim() ?? "";
                if (string.IsNullOrEmpty(transferMode)) transferMode = "Cut";
                folders.Add(new SourceFolderEntry
                {
                    Path = node.SelectSingleNode("Path")?.InnerText?.Trim() ?? "",
                    ProfileName = node.SelectSingleNode("ProfileName")?.InnerText?.Trim() ?? "",
                    TransferMode = transferMode
                });
            }
            return folders;
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
                    TransportType = node.SelectSingleNode("TransportType")?.InnerText?.Trim() ?? "SFTP",
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

            // 合并源文件夹列表：target 为空时从 source 复制
            if ((target.SourceFolders == null || target.SourceFolders.Count == 0)
                && source.SourceFolders != null && source.SourceFolders.Count > 0)
            {
                target.SourceFolders = source.SourceFolders.Select(sf => new SourceFolderEntry
                {
                    Path = sf.Path,
                    ProfileName = sf.ProfileName,
                    TransferMode = string.IsNullOrEmpty(sf.TransferMode) ? "Cut" : sf.TransferMode
                }).ToList();
                changed = true;
            }

            // 兼容旧格式
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
            if (_config == null) return;

            try
            {
                // 加密所有 Profile 密码后再序列化
                if (_config.SftpProfiles != null)
                {
                    foreach (var profile in _config.SftpProfiles)
                    {
                        if (!profile.IsPasswordEncrypted && !string.IsNullOrEmpty(profile.Password))
                            profile.EncryptPassword();
                    }
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
                // 迁移旧配置
                _config.MigrateSourceFolders();

                // 填充源文件夹列表（深拷贝，避免直接修改 config 中的对象引用）
                _sourceFolders.Clear();
                if (_config.SourceFolders != null)
                {
                    foreach (var sf in _config.SourceFolders)
                    {
                        _sourceFolders.Add(new SourceFolderEntry
                        {
                            Path = sf.Path,
                            ProfileName = sf.ProfileName,
                            TransferMode = string.IsNullOrEmpty(sf.TransferMode) ? "Cut" : sf.TransferMode
                        });
                    }
                }
                ReindexSourceFolders();
                icSourceFolders.ItemsSource = _sourceFolders;

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
            // 刷新 ItemsControl 中所有 ComboBox 的 ItemsSource
            foreach (var entry in _sourceFolders)
            {
                var container = icSourceFolders.ItemContainerGenerator.ContainerFromItem(entry);
                if (container == null) continue;
                var cmb = FindVisualChild<ComboBox>(container);
                if (cmb == null) continue;

                var previousSelection = cmb.SelectedItem as SftpProfile;
                cmb.ItemsSource = null;
                cmb.ItemsSource = _config?.SftpProfiles;

                // 恢复之前的选择
                if (previousSelection != null)
                {
                    foreach (var item in cmb.Items)
                    {
                        if (item is SftpProfile p && p.Name == previousSelection.Name)
                        {
                            cmb.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
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

            // 从动态列表同步源文件夹配置
            _config.SourceFolders = _sourceFolders
                .Select(sf => new SourceFolderEntry
                {
                    Path = sf.Path,
                    ProfileName = sf.ProfileName,
                    TransferMode = sf.TransferMode
                })
                .ToList();

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
