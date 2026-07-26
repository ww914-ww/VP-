using System.Windows;

namespace MoveImageForm.Views
{
    public partial class ChangePasswordDialog : Window
    {
        public string NewPassword { get; private set; }

        public ChangePasswordDialog(string accountName)
        {
            InitializeComponent();
            Title = $"修改密码 - {accountName}";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string pwd = txtNewPassword.Password;
            string confirm = txtConfirmPassword.Password;

            if (string.IsNullOrEmpty(pwd))
            {
                ShowError("密码不能为空");
                return;
            }
            if (pwd != confirm)
            {
                ShowError("两次密码不一致");
                return;
            }

            NewPassword = pwd;
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
