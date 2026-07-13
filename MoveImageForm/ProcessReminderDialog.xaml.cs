using System.Windows;

namespace MoveImageForm
{
    public enum ProcessReminderChoice
    {
        Start,
        Snooze,
        DisableMonitoring
    }

    public partial class ProcessReminderDialog : Window
    {
        public ProcessReminderChoice Choice { get; private set; } = ProcessReminderChoice.Snooze;

        public ProcessReminderDialog(string message)
        {
            InitializeComponent();
            txtMessage.Text = message;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            Choice = ProcessReminderChoice.Start;
            DialogResult = true;
            Close();
        }

        private void BtnSnooze_Click(object sender, RoutedEventArgs e)
        {
            Choice = ProcessReminderChoice.Snooze;
            DialogResult = true;
            Close();
        }

        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            Choice = ProcessReminderChoice.DisableMonitoring;
            DialogResult = true;
            Close();
        }
    }
}
