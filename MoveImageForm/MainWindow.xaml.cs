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
        private int _moveRound;
        private DateTime _lastIdleLogTime = DateTime.MinValue;
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        // 动态源文件夹列表
        private ObservableCollection<SourceFolderEntry> _sourceFolders
            = new ObservableCollection<SourceFolderEntry>();

        // SFTP 会话管理
        private readonly SessionManager _session = new SessionManager();

        // 断连重连节流：每个 Profile 上次重连尝试时间
        private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new Dictionary<string, DateTime>();

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

        // 灰度更新与心跳（v1.1.0+）
        private HeartbeatService _heartbeatService;
        private UpdateCheckService _updateCheckService;
        private AutoResumeService _autoResumeService;
        private string _appRoot;
        private string _baseDir;

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
                    StopMoving("用户登出");
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
                // v1.0.3+ 兜底：搬运期间任何路径都不得点亮「开始搬运」
                btnStart.IsEnabled = false;
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

            // 搬运进行中：保持「开始」禁用、「停止」可用。
            // 否则重连触发 LoginStateChanged → 此处会把 btnStart 重新打开，出现双按钮都可点。
            if (_isRunning)
            {
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                SetSourceTransferModeComboBoxesEnabled(false);
            }
            else
            {
                btnStart.IsEnabled = canUpload;
                btnStop.IsEnabled = false;
                SetSourceTransferModeComboBoxesEnabled(canChooseMode);
            }

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
                    StopMoving("用户退出程序");
                }
                UpdateConfigFromUI();
                SaveConfig();
                _heartbeatService?.Stop();
                _updateCheckService?.Stop();
                _autoResumeService?.Stop();
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
#if !DEBUG
            Title = "VP运维工具  构建 " + BuildInfo.BuildTime;
#endif
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _appRoot = VersionInfo.GetAppRoot(_baseDir);

            // Launcher 自更新：版本包携带 Launcher.new 时，在 Launcher 未运行的窗口期替换
            ApplyLauncherSelfUpdate();

            LoadConfig();
            UpdateUIFromConfig();

            // v1.0.3+：开机自启动写死启用（无界面选项，每次启动确保注册表 Run 键存在）
            EnsureAutoStart();

            // v1.1.0+：心跳上报（SFTP）与运行中定时更新检查（失败静默，绝不影响搬运业务）
            _heartbeatService = new HeartbeatService(_session, () => _config, _appRoot, _baseDir, Log);
            _heartbeatService.Start();
            _updateCheckService = new UpdateCheckService(() => _config, _appRoot, _baseDir, Log);
            _updateCheckService.Start();

            // v1.2.0+：自动恢复搬运（账号在线且未搬运时按设定间隔自动开启；功能开关见「软件设置」Tab）
            _autoResumeService = new AutoResumeService(
                getConfig: () => _config,
                isOnline: () => _session.IsLoggedIn,
                isMoving: () => _isRunning,
                canResume: CanAutoResumeMoving,
                startMoving: () => Dispatcher.Invoke(() => { if (!_isRunning) StartMoving(); }),
                log: Log);
            _autoResumeService.Start();

            // 自动登录：用已保存的密码连接活跃账号，成功后自动开始搬运
            // （开机自启动 = 启动软件 + 登录账号 + 开始搬运）
            TryAutoLogin();

            InitProcessMonitoring();
        }

        /// <summary>
        /// Launcher 自更新：当前版本目录携带 Launcher.new 时，备份旧 Launcher.exe 为 Launcher.old 后替换。
        /// 替换失败（文件占用/权限）静默跳过，Launcher.new 保留待下次启动重试。
        /// </summary>
        private void ApplyLauncherSelfUpdate()
        {
            try
            {
                string newFile = Path.Combine(_baseDir, "Launcher.new");
                if (!File.Exists(newFile)) return;

                // 仅正常部署结构（appRoot\versions\{ver}\）下执行，开发直跑不触碰
                if (!string.Equals(Path.GetFullPath(_appRoot),
                        Path.GetFullPath(Path.Combine(_baseDir, "..", "..")),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                string target = Path.Combine(_appRoot, "Launcher.exe");
                string backup = Path.Combine(_appRoot, "Launcher.old");

                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(target)) File.Move(target, backup);
                File.Move(newFile, target);
                Log("[系统] Launcher 已自更新（旧版备份为 Launcher.old）");
            }
            catch
            {
                // 替换失败：尽力还原，下次启动再试
                try
                {
                    string target = Path.Combine(_appRoot, "Launcher.exe");
                    string backup = Path.Combine(_appRoot, "Launcher.old");
                    if (!File.Exists(target) && File.Exists(backup))
                        File.Move(backup, target);
                }
                catch { }
            }
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
            if (profile == null)
            {
                Log($"[连接] {entry.Path} | 未找到账号「{entry.ProfileName}」");
                return null;
            }

            var session = _session.GetSession(profile.Name);
            if (session != null && session.IsConnected)
            {
                // 连接正常，清除重连记录
                _lastReconnectAttempt.Remove(profile.Name);
                return session;
            }

            string protocol = profile.IsSmb ? "SMB" : (profile.IsS3 ? "S3" : "SFTP");

            // SMB 无需认证，按需直接创建新会话（不依赖密码缓存）
            if (profile.IsSmb)
            {
                Log($"[连接] {entry.Path} | 正在连接 {protocol}「{profile.Name}」→ {profile.RemoteRoot}...");
                var swSmb = Stopwatch.StartNew();
                var smb = new SmbService(profile.RemoteRoot);
                try
                {
                    smb.Connect();
                }
                catch (Exception ex)
                {
                    Log($"[连接] SMB「{profile.Name}」失败: {ex.Message}");
                    return null;
                }
                _session.RegisterSession(profile, smb, "");
                Log($"[连接] SMB「{profile.Name}」已就绪，耗时 {swSmb.ElapsedMilliseconds} ms");
                return smb;
            }

            // SFTP/S3: 断连重连，每隔 1 分钟尝试一次
            string pn = profile.Name;
            if (_lastReconnectAttempt.TryGetValue(pn, out var lastAttempt))
            {
                double secondsSince = (DateTime.Now - lastAttempt).TotalSeconds;
                if (secondsSince < 60)
                {
                    // 距上次尝试不足 1 分钟，跳过
                    return null;
                }
            }

            _lastReconnectAttempt[pn] = DateTime.Now;
            Log($"[重连] {entry.Path} | 账号「{profile.Name}」({protocol}) 会话不可用，正在重连 → {profile.RemoteRoot}...");
            SetMoveActivity($"重连 {profile.Name}...");
            var sw = Stopwatch.StartNew();
            // 传 profile 而非名字：启动时自动登录失败（从未注册过会话）的账号也能用 config 保存的密码重连
            if (_session.TryReconnectWithRetry(profile, maxRetries: 3))
            {
                Log($"[重连] 账号「{profile.Name}」重连成功，耗时 {sw.ElapsedMilliseconds} ms");
                _lastReconnectAttempt.Remove(pn);
                return _session.GetSession(pn);
            }

            Log($"[重连] 账号「{profile.Name}」重连失败（耗时 {sw.ElapsedMilliseconds} ms），将在 1 分钟后再次尝试");
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
            _moveRound = 0;
            _lastIdleLogTime = DateTime.MinValue;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            SetUIEnabled(false);

            // 搬运期间禁止登出
            Dispatcher.Invoke(() => UpdateLoginButton());

            _cancellationTokenSource = new CancellationTokenSource();
            _lastMoveTime = DateTime.Now;

            var monitorFolders = _sourceFolders
                .Where(sf => !string.IsNullOrWhiteSpace(sf.Path) && Directory.Exists(sf.Path) && !string.IsNullOrWhiteSpace(sf.ProfileName))
                .ToList();

            Log("[监控] 已启动");
            Log($"[监控] 时间规则={(_config.EnableTimeRule ? "开" : "关")} 间隔={_config.TimeIntervalSeconds}s | " +
                $"数量规则={(_config.EnableCountRule ? $"开({_config.CountLimit})" : "关")} | " +
                $"空间规则={(_config.EnableSizeRule ? $"开({_config.SizeLimitMB}MB)" : "关")} | " +
                $"空目录清理={(_config.EnableEmptyFolderRule ? $"开({_config.EmptyFolderHours}h)" : "关")}");

            if (_config.EnableTimeRule)
                Log($"[监控] 将在约 {_config.TimeIntervalSeconds}s 后首次按时间规则检查（启动时刻不算到期）");
            else
                Log("[监控] 时间规则未开启，仅在数量/空间规则命中时搬运");

            int i = 0;
            foreach (var sf in monitorFolders)
            {
                i++;
                var p = _config.GetProfileForSource(sf);
                string protocol = p == null ? "?" : (p.IsSmb ? "SMB" : (p.IsS3 ? "S3" : "SFTP"));
                string remote = p?.RemoteRoot ?? "(无)";
                Log($"[监控] 目录 {i}/{monitorFolders.Count}: {sf.Path} | 账号={sf.ProfileName} | {protocol} | 模式={sf.TransferMode} | 远端={remote}");
            }

            SetMoveActivity(_config.EnableTimeRule
                ? $"等待首次检查（约 {_config.TimeIntervalSeconds}s）"
                : "等待数量/空间规则触发");

            Task.Run(() => MonitorLoop(_cancellationTokenSource.Token));
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopMoving("用户点击停止");
        }

        private void StopMoving(string reason = "用户停止")
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
                    txtMoveActivity.Visibility = Visibility.Collapsed;
                    txtMoveActivity.Text = "";
                    Log($"[监控] 已停止 | 原因: {reason}");
                });
            }
        }

        private void SetMoveActivity(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrEmpty(text))
                {
                    txtMoveActivity.Text = "";
                    txtMoveActivity.Visibility = Visibility.Collapsed;
                    return;
                }

                if (_isRunning)
                {
                    borderTransferStatus.Visibility = Visibility.Visible;
                    borderNotLoggedIn.Visibility = Visibility.Collapsed;
                }
                txtMoveActivity.Text = "当前: " + text;
                txtMoveActivity.Visibility = Visibility.Visible;
            });
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

                    int folderCount = activeEntries?.Count ?? 0;

                    // 空转心跳：约每 60 秒说明还在等什么
                    if (!timeTriggered && _config.EnableTimeRule && folderCount > 0)
                    {
                        double remain = _config.TimeIntervalSeconds - (DateTime.Now - _lastMoveTime).TotalSeconds;
                        if (remain < 0) remain = 0;
                        if ((DateTime.Now - _lastIdleLogTime).TotalSeconds >= 60)
                        {
                            _lastIdleLogTime = DateTime.Now;
                            string nextAt = _lastMoveTime.AddSeconds(_config.TimeIntervalSeconds).ToString("HH:mm:ss");
                            Log($"[等待] 时间规则未到期 | 约 {Math.Ceiling(remain)}s 后检查（下次≈{nextAt}，间隔 {_config.TimeIntervalSeconds}s，监控 {folderCount} 个目录）");
                            SetMoveActivity($"等待中，约 {Math.Ceiling(remain)}s 后检查");
                        }
                    }

                    if (timeTriggered)
                    {
                        _moveRound++;
                        Log($"[轮次 #{_moveRound}] 开始 | 原因=时间规则 | 目录数={folderCount} | 间隔={_config.TimeIntervalSeconds}s");
                        SetMoveActivity($"轮次 #{_moveRound}：准备处理 {folderCount} 个目录");
                    }

                    if (activeEntries != null)
                    {
                        int index = 0;
                        foreach (var entry in activeEntries)
                        {
                            if (token.IsCancellationRequested) break;
                            index++;

                            var profile = _config.GetProfileForSource(entry);
                            string protocol = profile == null ? "?" : (profile.IsSmb ? "SMB" : (profile.IsS3 ? "S3" : "SFTP"));
                            string remote = profile?.RemoteRoot ?? "(无)";

                            var sftp = GetOrReconnectSession(entry);
                            if (sftp != null)
                            {
                                bool sizeOrCount = !timeTriggered && ShouldMoveFromSource(entry.Path, sftp);
                                if (timeTriggered || sizeOrCount)
                                {
                                    string reason = timeTriggered ? "时间规则" : "数量/空间规则";
                                    Log($"[目录 {index}/{folderCount}] {entry.Path} | 账号={entry.ProfileName} | {protocol} | 模式={entry.TransferMode} | 远端={remote} | 触发={reason}");
                                    SetMoveActivity($"[{index}/{folderCount}] {entry.Path}");

                                    await MoveFilesToSftpAsync(entry, sftp, token);
                                    if (_config.EnableEmptyFolderRule)
                                    {
                                        Log($"[清理] {entry.Path} | 检查空文件夹（>{_config.EmptyFolderHours}h）...");
                                        CleanEmptyFolders(entry.Path, _config.EmptyFolderHours, token);
                                    }
                                    movedAny = true;
                                }
                            }
                            else
                            {
                                // 断连状态，跳过本轮，等待 GetOrReconnectSession 的 1 分钟重连节流
                                Log($"[目录 {index}/{folderCount}] {entry.Path} | 传输未连接（账号={entry.ProfileName}），本轮跳过");
                            }
                        }
                    }

                    if (movedAny)
                    {
                        _lastMoveTime = DateTime.Now;
                        _lastIdleLogTime = DateTime.MinValue;
                        if (timeTriggered)
                        {
                            string nextAt = _lastMoveTime.AddSeconds(_config.TimeIntervalSeconds).ToString("HH:mm:ss");
                            Log($"[轮次 #{_moveRound}] 结束 | 下次时间规则约 {nextAt}（间隔 {_config.TimeIntervalSeconds}s）");
                            SetMoveActivity($"轮次 #{_moveRound} 已结束，等待下次约 {_config.TimeIntervalSeconds}s");
                        }
                        else
                        {
                            Log("[规则] 本轮数量/空间触发的搬运已结束");
                            SetMoveActivity("等待中");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[监控] 异常: {ex.Message}");
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private bool ShouldMoveFromSource(string source, IFileTransferService sftp)
        {
            if (!_config.EnableSizeRule && !_config.EnableCountRule)
                return false;

            if (!Directory.Exists(source)) return false;

            // 数量/空间规则只做静默统计（监控循环会频繁调用，避免刷屏）
            if (!TryCountSourceFiles(source, CancellationToken.None, logProgress: false, out int fileCount, out long totalSize))
                return false;

            if (_config.EnableCountRule && fileCount >= _config.CountLimit)
            {
                Log($"[规则] {source} | 触发文件数量规则 (当前 {fileCount} ≥ {_config.CountLimit})");
                return true;
            }
            if (_config.EnableSizeRule && totalSize >= _config.SizeLimitMB * 1024 * 1024)
            {
                Log($"[规则] {source} | 触发文件空间规则 (当前 {totalSize / 1024.0 / 1024.0:F2} MB ≥ {_config.SizeLimitMB} MB)");
                return true;
            }
            return false;
        }

        /// <summary>
        /// 安全枚举源目录文件：跳过无权限目录与重解析点（junction/symlink），支持取消，并可选输出扫描进度。
        /// 替代 Directory.GetFiles(AllDirectories)，避免大目录/坏链接导致长时间无日志卡死。
        /// </summary>
        private List<FileInfo> CollectSourceFiles(string source, CancellationToken token, bool logProgress, out long totalBytes)
        {
            var files = new List<FileInfo>();
            totalBytes = 0;
            var stack = new Stack<string>();
            stack.Push(source);

            int lastLoggedCount = 0;
            var progressSw = Stopwatch.StartNew();
            if (logProgress)
                Log($"[扫描] {source} | 正在枚举本地文件...");

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = stack.Pop();

                try
                {
                    foreach (string filePath in Directory.EnumerateFiles(current))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(filePath);
                            files.Add(fi);
                            totalBytes += fi.Length;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }

                        if (logProgress && (files.Count - lastLoggedCount >= 2000 || progressSw.Elapsed.TotalSeconds >= 3))
                        {
                            lastLoggedCount = files.Count;
                            progressSw.Restart();
                            Log($"[扫描] {source} | 已发现 {files.Count} 个文件, {totalBytes / 1048576.0:F1} MB...");
                            SetMoveActivity($"扫描 {Path.GetFileName(source)}：已发现 {files.Count} 个文件");
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (logProgress) Log($"[扫描] 无权限读取文件: {current}");
                }
                catch (DirectoryNotFoundException) { }
                catch (IOException ex)
                {
                    if (logProgress) Log($"[扫描] 读取文件失败: {current} ({ex.Message})");
                }

                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            var di = new DirectoryInfo(subDir);
                            // 跳过 junction/symlink，防止环路或扫到超大网络路径导致卡死
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                if (logProgress) Log($"[扫描] 跳过链接目录: {subDir}");
                                continue;
                            }
                            stack.Push(subDir);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (logProgress) Log($"[扫描] 无权限遍历子目录: {current}");
                }
                catch (DirectoryNotFoundException) { }
                catch (IOException ex)
                {
                    if (logProgress) Log($"[扫描] 遍历子目录失败: {current} ({ex.Message})");
                }
            }

            if (logProgress)
                Log($"[扫描] {source} | 枚举完成，共 {files.Count} 个文件, {totalBytes / 1048576.0:F1} MB");
            return files;
        }

        private bool TryCountSourceFiles(string source, CancellationToken token, bool logProgress, out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                var files = CollectSourceFiles(source, token, logProgress, out totalBytes);
                fileCount = files.Count;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log($"[{source}] 统计本地文件失败: {ex.Message}");
                return false;
            }
        }

        private Task MoveFilesToSftpAsync(SourceFolderEntry entry, IFileTransferService sftp, CancellationToken token)
        {
            string source = entry.Path;
            string transferMode = entry.TransferMode;
            string profileName = entry.ProfileName;

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
                var heartbeatSw = Stopwatch.StartNew();
                int lastHeartbeatProcessed = 0;
                int failLogged = 0;
                const int MaxFailLogsPerFolder = 20;

                try
                {
                    SetMoveActivity($"扫描 {source}");
                    Log($"[准备] {source} | 模式: {modeText} | 开始扫描本地目录...");
                    List<FileInfo> files = CollectSourceFiles(source, token, logProgress: true, out long totalBytes);
                    files.Sort((a, b) => DateTime.Compare(b.LastWriteTime, a.LastWriteTime));

                    int totalFiles = files.Count;
                    int success = 0, skip = 0, fail = 0;
                    int skipSync = 0, skipExists = 0, skipOther = 0, deleteFail = 0;
                    int processed = 0;

                    Log($"[开始] {source} | 模式: {modeText} | 共 {totalFiles} 个文件, {totalBytes / 1048576.0:F1} MB | 按修改时间新→旧");
                    if (totalFiles == 0)
                        Log($"[开始] {source} | 本地无文件，本目录空跑结束");

                    SetMoveActivity($"上传 {Path.GetFileName(source)} 0/{totalFiles}");

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
                                skipSync++;
                                processed++;
                            }
                            else
                            {
                                TransferUploadResult uploadResult = sftp.UploadFile(file.FullName, relPath, appendOnly: isAppend);
                                if (uploadResult.Success)
                                {
                                    success++;
                                    bytesTransferred += file.Length;
                                    if (isCut)
                                    {
                                        try { file.Delete(); }
                                        catch (Exception dex)
                                        {
                                            deleteFail++;
                                            if (deleteFail <= 5)
                                                Log($"[警告] 剪切后删除本地失败: {file.Name} | {dex.Message}");
                                        }
                                    }
                                }
                                else if (uploadResult.Skipped)
                                {
                                    skip++;
                                    if ((uploadResult.Detail ?? "").Contains("已存在"))
                                        skipExists++;
                                    else
                                        skipOther++;
                                }
                                else
                                {
                                    fail++;
                                    // 上传失败且连接已断开：中止本轮，等待下一轮 GetOrReconnectSession 自动重连
                                    if (!sftp.IsConnected)
                                    {
                                        Log($"[断连] 检测到账号连接断开 ({profileName})，本轮搬运中止，将在下次检测时自动重连");
                                        return;
                                    }
                                    if (failLogged < MaxFailLogsPerFolder)
                                    {
                                        failLogged++;
                                        Log($"[失败] {source} | {relPath} | {uploadResult.Detail}");
                                        if (failLogged == MaxFailLogsPerFolder)
                                            Log($"[失败] {source} | 后续失败仅计入统计（界面最多显示 {MaxFailLogsPerFolder} 条）");
                                    }
                                }

                                processed++;
                            }

                            // 心跳：每 5 秒或每 50 个文件（避免大文件/慢网时长时间无日志）
                            if (heartbeatSw.Elapsed.TotalSeconds >= 5 || processed - lastHeartbeatProcessed >= 50)
                            {
                                heartbeatSw.Restart();
                                lastHeartbeatProcessed = processed;
                                double mbDone = bytesTransferred / 1048576.0;
                                double speed = sw.Elapsed.TotalSeconds > 0 ? mbDone / sw.Elapsed.TotalSeconds : 0;
                                Log($"[心跳] {source} | {processed}/{totalFiles} | 当前 {file.Name} | 成功 {success} | 跳过 {skip} | 失败 {fail} | {mbDone:F1} MB | {speed:F1} MB/s");
                                SetMoveActivity($"上传 {Path.GetFileName(source)} {processed}/{totalFiles} — {file.Name}");
                            }

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
                            // 异常也可能是连接断开导致
                            if (!sftp.IsConnected)
                            {
                                Log($"[断连] 上传异常，检测到账号连接断开 ({profileName}): {ex.Message}，本轮搬运中止");
                                return;
                            }
                            fail++;
                            processed++;
                            if (failLogged < MaxFailLogsPerFolder)
                            {
                                failLogged++;
                                Log($"[失败] {source} | {file.Name} | {ex.Message}");
                            }
                        }
                    }

                    sw.Stop();
                    double totalMb = bytesTransferred / 1048576.0;
                    double avgSpeed = sw.Elapsed.TotalSeconds > 0 ? totalMb / sw.Elapsed.TotalSeconds : 0;
                    Log($"[完成] {source} | 成功 {success} | 跳过 {skip}（已同步 {skipSync} / 远端已存在 {skipExists} / 其他 {skipOther}） | 失败 {fail}" +
                        (deleteFail > 0 ? $" | 删本地失败 {deleteFail}" : "") +
                        $" | 耗时 {sw.Elapsed.Minutes}分{sw.Elapsed.Seconds}秒 | 总传输 {totalMb:F1} MB | 平均速率 {avgSpeed:F1} MB/s");

                    if (isCopy && skipSync > 0 && success == 0 && fail == 0)
                        Log($"[完成] {source} | 说明: 复制模式下远端文件已是最新，故全部跳过（本地文件保留）");
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    Log($"[中断] {source} | 扫描/上传已取消");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Log($"[异常] {source} | 上传异常: {ex.Message}");
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

        /// <summary>可选灰度开关：勾选即保存到 config.xml（GrayOptIn），云端 requireOptIn=true 时仅勾选机台参与百分比灰度</summary>
        private void ChkGrayOptIn_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingConfig || _config == null) return;
            _config.GrayOptIn = chkGrayOptIn.IsChecked ?? false;
            SaveConfig();
            Log(_config.GrayOptIn
                ? "[系统] 本机已报名参与灰度测试（云端发布灰度版本时可能被选中升级）"
                : "[系统] 本机已退出灰度测试（仅接收正式版本；已安装的灰度版本不会自动降级，如需回退请联系运维）");
        }

        /// <summary>
        /// 自动恢复搬运的前置校验（与 StartMoving 的弹窗校验等价但静默）：
        /// 在线 + 存在有效监控目录 + 角色有上传权限。全部满足时 StartMoving 才不会弹窗。
        /// </summary>
        private bool CanAutoResumeMoving()
        {
            if (_isRunning) return false;
            if (!_session.IsLoggedIn) return false;
            if (_config == null) return false;

            bool anyValid = _sourceFolders.Any(sf =>
                !string.IsNullOrWhiteSpace(sf.Path) && Directory.Exists(sf.Path)
                && !string.IsNullOrWhiteSpace(sf.ProfileName));
            if (!anyValid) return false;

            var activeProfiles = _config.GetActiveProfileNames().Distinct().ToList();
            if (activeProfiles.Count == 0) return false;

            // 角色判定逻辑与 StartMoving 一致：全部为 SMB 账号时视为 admin
            string role = _session.CurrentRole;
            if (string.IsNullOrEmpty(role))
            {
                bool allSmb = activeProfiles.All(name =>
                {
                    var p = _config.FindProfile(name);
                    return p != null && p.IsSmb;
                });
                if (allSmb) role = "admin";
            }
            return RoleEnforcer.CanUpload(role);
        }

        /// <summary>自动恢复搬运设置变更：开关/间隔数值/间隔单位统一处理，改动即存 config.xml</summary>
        private void AutoResumeSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingConfig || _config == null) return;

            bool enabled = chkAutoResume.IsChecked ?? false;
            int intervalValue;
            if (!int.TryParse(txtAutoResumeInterval.Text?.Trim(), out intervalValue) || intervalValue < 1)
                intervalValue = _config.AutoResumeIntervalValue > 0 ? _config.AutoResumeIntervalValue : 5;
            string unit = (cmbAutoResumeUnit.SelectedItem as ComboBoxItem)?.Tag as string ?? "Minutes";

            _config.AutoResumeEnabled = enabled;
            _config.AutoResumeIntervalValue = intervalValue;
            _config.AutoResumeIntervalUnit = unit;
            SaveConfig();

            // 仅在开关切换时记日志（输入间隔不刷日志）
            if (sender == chkAutoResume)
            {
                Log(enabled
                    ? $"[系统] 自动恢复搬运已启用（账号在线且停止搬运 {intervalValue} {UnitDisplayName(unit)} 后自动开启）"
                    : "[系统] 自动恢复搬运已关闭");
            }
        }

        private static string UnitDisplayName(string unit)
        {
            switch (unit)
            {
                case "Seconds": return "秒";
                case "Hours": return "小时";
                default: return "分钟";
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

                // 可选灰度开关
                chkGrayOptIn.IsChecked = _config.GrayOptIn;

                // 自动恢复搬运设置
                chkAutoResume.IsChecked = _config.AutoResumeEnabled;
                txtAutoResumeInterval.Text = _config.AutoResumeIntervalValue.ToString();
                foreach (ComboBoxItem item in cmbAutoResumeUnit.Items)
                {
                    if (string.Equals(item.Tag as string, _config.AutoResumeIntervalUnit,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        cmbAutoResumeUnit.SelectedItem = item;
                        break;
                    }
                }
                if (cmbAutoResumeUnit.SelectedItem == null && cmbAutoResumeUnit.Items.Count > 0)
                    cmbAutoResumeUnit.SelectedIndex = 1; // 默认"分钟"

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
