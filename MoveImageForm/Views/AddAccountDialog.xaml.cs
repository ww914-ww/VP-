using System.Windows;
using System.Windows.Controls;
using MoveImageForm.Models;

namespace MoveImageForm.Views
{
    public partial class AddAccountDialog : Window
    {
        public SftpProfile Profile { get; private set; }

        private bool _initialized;
        private readonly bool _isEditMode;
        private readonly SftpProfile _existingProfile;

        /// <summary>创建新账号</summary>
        public AddAccountDialog()
        {
            InitializeComponent();
            _initialized = true;
            _isEditMode = false;
        }

        /// <summary>编辑已有账号</summary>
        public AddAccountDialog(SftpProfile existing)
        {
            InitializeComponent();
            _isEditMode = true;
            _existingProfile = existing;
            _initialized = true;

            // 预填已有值
            txtName.Text = existing.Name;
            txtHost.Text = existing.Host;
            txtPort.Text = existing.Port.ToString();
            txtUsername.Text = existing.Username;
            // 密码保持 DPAPI 加密状态，编辑时不预填（安全考虑）
            // 如果用户要改密码才填，否则留空 = 保持不变
            txtRemoteRoot.Text = existing.RemoteRoot;

            // 根据 TransportType 设置选中
            if (existing.IsSmb)
            {
                rbSmb.IsChecked = true;
                rbSftp.IsChecked = false;
                rbS3.IsChecked = false;
            }
            else if (existing.IsS3)
            {
                rbS3.IsChecked = true;
                rbSftp.IsChecked = false;
                rbSmb.IsChecked = false;
            }
            else
            {
                rbSftp.IsChecked = true;
                rbS3.IsChecked = false;
                rbSmb.IsChecked = false;
            }

            // 根据权限设置 RadioButton
            string role = (existing.Role ?? "").ToLower();
            if (role == "admin") rbAdmin.IsChecked = true;
            else if (role == "readonly") rbReadonly.IsChecked = true;
            else rbUpload.IsChecked = true;

            // 编辑模式下不允许修改传输方式
            spTransportType.IsEnabled = false;
            rbSftp.IsEnabled = false;
            rbS3.IsEnabled = false;
            rbSmb.IsEnabled = false;

            Title = $"编辑账号 - {existing.Name}";
            btnAdd.Content = "保存";

            // SMB 系统账号：名称只读，隐藏敏感字段
            if (existing.IsSmb)
            {
                txtName.IsReadOnly = true;
                txtName.IsEnabled = false;
            }

            // 应用传输类型标签和字段可见性
            ApplyTransportLabels();
        }

        private void TransportType_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;

            bool isS3 = rbS3.IsChecked == true;
            bool isSmb = rbSmb.IsChecked == true;

            ApplyTransportLabels();

            // SMB 系统账号：名称固定，隐藏认证字段
            if (isSmb)
            {
                txtName.Text = SftpProfile.SmbProfileName;
                txtName.IsReadOnly = true;
                txtName.IsEnabled = false;
                if (string.IsNullOrEmpty(txtRemoteRoot.Text) || txtRemoteRoot.Text == "/")
                    txtRemoteRoot.Text = "";
                return;
            }
            txtName.IsReadOnly = false;
            txtName.IsEnabled = true;

            // 编辑模式下不自动替换已有值，避免覆盖用户配置
            if (_isEditMode) return;

            // 切换默认端口
            if (isS3 && (txtPort.Text == "22" || txtPort.Text == ""))
                txtPort.Text = "12001";
            else if (!isS3 && txtPort.Text == "12001")
                txtPort.Text = "22";

            // 切换默认地址
            if (isS3 && txtHost.Text == "172.16.22.138")
                txtHost.Text = "";
            else if (!isS3 && txtHost.Text == "")
                txtHost.Text = "172.16.22.138";

            // 切换远程路径默认值
            if (isS3 && txtRemoteRoot.Text == "/")
                txtRemoteRoot.Text = "test_bucket";
            else if (!isS3 && txtRemoteRoot.Text == "test_bucket")
                txtRemoteRoot.Text = "/";
        }

        private void ApplyTransportLabels()
        {
            bool isS3 = rbS3.IsChecked == true;
            bool isSmb = rbSmb.IsChecked == true;

            // SMB 模式：只显示名称和目标文件夹
            if (isSmb)
            {
                lblHost.Visibility = Visibility.Collapsed;
                txtHost.Visibility = Visibility.Collapsed;
                // 端口行
                txtPort.Visibility = Visibility.Collapsed;
                lblUsername.Visibility = Visibility.Collapsed;
                txtUsername.Visibility = Visibility.Collapsed;
                lblPassword.Visibility = Visibility.Collapsed;
                txtPassword.Visibility = Visibility.Collapsed;
                lblRole.Visibility = Visibility.Collapsed;
                spRole.Visibility = Visibility.Collapsed;
                lblRemoteRoot.Text = "目标文件夹:";
                return;
            }

            // 恢复所有字段
            lblHost.Visibility = Visibility.Visible;
            txtHost.Visibility = Visibility.Visible;
            txtPort.Visibility = Visibility.Visible;
            lblUsername.Visibility = Visibility.Visible;
            txtUsername.Visibility = Visibility.Visible;
            lblPassword.Visibility = Visibility.Visible;
            txtPassword.Visibility = Visibility.Visible;
            lblRole.Visibility = Visibility.Visible;
            spRole.Visibility = Visibility.Visible;

            // 切换标签文字
            lblHost.Text = isS3 ? "Endpoint 地址:" : "服务器地址:";
            lblUsername.Text = isS3 ? "Access Key (AK):" : "账号（SSH用户名）:";
            lblPassword.Text = isS3 ? "Secret Key (SK):" : "密码:";
            lblRemoteRoot.Text = isS3 ? "Bucket (存储桶名称):" : "远程路径:";

            // S3 账号不需要应用层权限控制（权限由 AK/SK 的服务端策略决定）
            if (isS3)
            {
                lblRole.Visibility = Visibility.Collapsed;
                spRole.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text?.Trim();
            string username = txtUsername.Text?.Trim();
            string password = txtPassword.Password;
            string remoteRoot = txtRemoteRoot.Text?.Trim();
            string host = txtHost.Text?.Trim();

            bool isS3 = rbS3.IsChecked == true;
            bool isSmb = rbSmb.IsChecked == true;

            if (string.IsNullOrEmpty(name))
            {
                ShowError("名称不能为空");
                return;
            }

            // SMB 模式：只需名称和目标文件夹
            if (isSmb)
            {
                if (string.IsNullOrEmpty(remoteRoot))
                {
                    ShowError("目标文件夹不能为空");
                    return;
                }
                if (_isEditMode && _existingProfile != null)
                {
                    _existingProfile.RemoteRoot = remoteRoot;
                    Profile = _existingProfile;
                }
                else
                {
                    Profile = new SftpProfile
                    {
                        Name = name,
                        TransportType = "SMB",
                        Role = "admin",
                        RemoteRoot = remoteRoot
                    };
                }
                DialogResult = true;
                Close();
                return;
            }

            if (string.IsNullOrEmpty(host))
            {
                ShowError("服务器地址不能为空");
                return;
            }
            if (!int.TryParse(txtPort.Text?.Trim(), out int port) || port <= 0)
            {
                ShowError("端口必须为有效数字");
                return;
            }
            if (string.IsNullOrEmpty(username))
            {
                ShowError(rbS3.IsChecked == true ? "Access Key 不能为空" : "账号不能为空");
                return;
            }
            // 新增时必须填密码，编辑时留空 = 保持原密码不变
            if (!_isEditMode && string.IsNullOrEmpty(password))
            {
                ShowError(rbS3.IsChecked == true ? "Secret Key 不能为空" : "密码不能为空");
                return;
            }

            // S3 角色面板隐藏，权限由 AK/SK 服务端策略控制，应用层默认给 admin
            string role = isS3 ? "admin"
                : rbAdmin.IsChecked == true ? "admin"
                : rbReadonly.IsChecked == true ? "readonly" : "upload";

            // S3 的 RemoteRoot (Bucket 名) 不需要首尾 /
            if (isS3)
            {
                remoteRoot = remoteRoot.Trim('/');
                if (string.IsNullOrEmpty(remoteRoot))
                {
                    ShowError("Bucket 名称不能为空");
                    return;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(remoteRoot))
                    remoteRoot = "/";
            }

            if (_isEditMode && _existingProfile != null)
            {
                // 编辑模式：更新已有 Profile
                _existingProfile.Name = name;
                _existingProfile.Host = host;
                _existingProfile.Port = port;
                _existingProfile.Username = username;
                _existingProfile.RemoteRoot = remoteRoot;
                _existingProfile.Role = role;
                // 只有用户输入了新密码才更新
                if (!string.IsNullOrEmpty(password))
                    _existingProfile.Password = password;
                Profile = _existingProfile;
            }
            else
            {
                Profile = new SftpProfile
                {
                    Name = name,
                    TransportType = isS3 ? "S3" : "SFTP",
                    Username = username,
                    Password = password,
                    Role = role,
                    Host = host,
                    Port = port,
                    RemoteRoot = remoteRoot
                };
            }
            DialogResult = true;
            Close();
        }

        private void ShowError(string msg)
        {
            txtError.Text = msg;
            txtError.Visibility = Visibility.Visible;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
