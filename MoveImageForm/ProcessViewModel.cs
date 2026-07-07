using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoveImageForm
{
    public class ProcessViewModel : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private bool _enabled;
        private bool _isRunning;
        private string _statusText;
        private System.Windows.Media.Brush _statusColor;
        private Action _saveCallback;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); _saveCallback?.Invoke(); }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                _isRunning = value;
                StatusText = value ? "● 运行中" : "● 未运行";
                StatusColor = value
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 0, 0));
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public System.Windows.Media.Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public Action SaveCallback
        {
            set => _saveCallback = value;
        }

        public ProcessViewModel()
        {
            _isRunning = false;
            _statusText = "● 未知";
            _statusColor = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(136, 136, 136));
        }

        public ProcessInfo ToProcessInfo()
        {
            return new ProcessInfo { Name = this.Name, Path = this.Path, Enabled = this.Enabled };
        }

        public static ProcessViewModel FromProcessInfo(ProcessInfo info, Action saveCallback)
        {
            var vm = new ProcessViewModel
            {
                Name = info.Name,
                Path = info.Path,
                Enabled = info.Enabled,
                SaveCallback = saveCallback
            };
            return vm;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
