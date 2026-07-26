using System.Windows;
using MoveImageForm.Models;

namespace MoveImageForm.Views
{
    public partial class AddAccountDialog : Window
    {
        public SftpProfile Profile { get; private set; }

        public AddAccountDialog()
        {
            InitializeComponent();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text?.Trim();
            string username = txtUsername.Text?.Trim();
            string password = txtPassword.Password;
            string remoteRoot = txtRemoteRoot.Text?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                ShowError("名称不能为空");
                return;
            }
            if (string.IsNullOrEmpty(username))
            {
                ShowError("账号不能为空");
                return;
            }
            if (string.IsNullOrEmpty(password))
            {
                ShowError("密码不能为空");
                return;
            }

            string role = rbAdmin.IsChecked == true ? "admin"
                : rbReadonly.IsChecked == true ? "readonly" : "upload";

            Profile = new SftpProfile
            {
                Name = name,
                Username = username,
                Password = password,
                Role = role,
                Host = "172.16.22.138",
                Port = 22,
                RemoteRoot = string.IsNullOrEmpty(remoteRoot) ? "/" : remoteRoot
            };
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
