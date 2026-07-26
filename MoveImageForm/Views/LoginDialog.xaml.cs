using System.Collections.Generic;
using System.Windows;
using MoveImageForm.Models;
using MoveImageForm.Services;

namespace MoveImageForm.Views
{
    public partial class LoginDialog : Window
    {
        public SftpProfile SelectedProfile { get; private set; }
        public SftpService SftpService { get; private set; }
        public string Password { get; private set; }

        public LoginDialog(List<SftpProfile> profiles)
        {
            InitializeComponent();
            cmbAccount.ItemsSource = profiles;
            if (profiles.Count > 0)
                cmbAccount.SelectedIndex = 0;
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

            var sftp = new SftpService(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
            try
            {
                sftp.Connect();
                SelectedProfile = profile;
                SftpService = sftp;
                Password = password;
                DialogResult = true;
                Close();
            }
            catch
            {
                sftp.Dispose();
                ShowError("密码错误，请重试");
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
