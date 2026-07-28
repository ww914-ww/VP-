using System;
using System.Collections.Generic;
using System.Windows;
using MoveImageForm.Models;
using MoveImageForm.Services;

namespace MoveImageForm.Views
{
    public partial class LoginDialog : Window
    {
        public SftpProfile SelectedProfile { get; private set; }
        public IFileTransferService FileTransferService { get; private set; }
        public string Password { get; private set; }

        public LoginDialog(List<SftpProfile> profiles)
        {
            InitializeComponent();
            cmbAccount.ItemsSource = profiles;
            if (profiles.Count > 0)
            {
                cmbAccount.SelectedIndex = 0;
                UpdatePasswordLabel();
            }
            cmbAccount.SelectionChanged += (s, e) => UpdatePasswordLabel();
        }

        private void UpdatePasswordLabel()
        {
            // 切换账号时清空密码，避免误用上一个账号的密码
            txtPassword.Password = "";

            if (cmbAccount.SelectedItem is SftpProfile profile && profile.IsS3)
            {
                lblPassword.Text = "Secret Key (SK):";
                txtPassword.ToolTip = "输入 S3 的 Secret Key（SK）";
            }
            else
            {
                lblPassword.Text = "密码:";
                txtPassword.ToolTip = null;
            }
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (cmbAccount.SelectedItem == null)
            {
                ShowError("请选择账号");
                return;
            }

            var profile = (SftpProfile)cmbAccount.SelectedItem;
            string password = txtPassword.Password;

            if (string.IsNullOrEmpty(password))
            {
                ShowError("请输入密码");
                return;
            }

            IFileTransferService transport;
            if (profile.IsS3)
            {
                transport = new S3Service(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
            }
            else
            {
                transport = new SftpService(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
            }

            try
            {
                transport.Connect();
                SelectedProfile = profile;
                FileTransferService = transport;
                Password = password;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                transport.Dispose();
                ShowError($"连接失败: {ex.Message}");
            }
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
