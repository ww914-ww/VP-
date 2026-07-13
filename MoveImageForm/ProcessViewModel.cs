using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MoveImageForm
{
    public class ProcessViewModel : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private bool _enabled;
        private ProcessMonitorState _monitorState = ProcessMonitorState.Stopped;
        private string _statusText;
        private Brush _statusColor;
        private Action _saveCallback;
        private int _nextCheckSeconds;
        private bool _showNextCheckCountdown;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_path))
                {
                    string exeName = System.IO.Path.GetFileNameWithoutExtension(_path);
                    if (!string.IsNullOrWhiteSpace(exeName))
                        return exeName;
                }
                return string.IsNullOrWhiteSpace(_name) ? "进程" : _name;
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value)
                    SetMonitorState(ProcessMonitorState.NotMonitoring);
                else if (_monitorState == ProcessMonitorState.NotMonitoring)
                    SetMonitorState(ProcessMonitorState.Stopped);
                OnPropertyChanged();
                _saveCallback?.Invoke();
            }
        }

        public ProcessMonitorState MonitorState
        {
            get => _monitorState;
        }

        public bool IsRunning => _monitorState == ProcessMonitorState.Running;

        public void SetMonitorState(ProcessMonitorState state)
        {
            if (_monitorState == state) return;
            _monitorState = state;
            UpdateStatusDisplay();
            OnPropertyChanged(nameof(MonitorState));
            OnPropertyChanged(nameof(IsRunning));
        }

        public void UpdateNextCheckCountdown(int seconds, bool show)
        {
            _nextCheckSeconds = seconds;
            _showNextCheckCountdown = show;
            UpdateStatusDisplay();
        }

        private string GetCountdownSuffix()
        {
            if (!_showNextCheckCountdown || _nextCheckSeconds <= 0)
                return "";
            if (_monitorState == ProcessMonitorState.Running
                || _monitorState == ProcessMonitorState.Starting
                || _monitorState == ProcessMonitorState.NotMonitoring)
                return "";
            return $" ({_nextCheckSeconds}s)";
        }

        private void UpdateStatusDisplay()
        {
            string suffix = GetCountdownSuffix();
            switch (_monitorState)
            {
                case ProcessMonitorState.NotMonitoring:
                    StatusText = "● 未监听";
                    StatusColor = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                    break;
                case ProcessMonitorState.Stopped:
                    StatusText = "● 未运行" + suffix;
                    StatusColor = new SolidColorBrush(Color.FromRgb(220, 0, 0));
                    break;
                case ProcessMonitorState.Starting:
                    StatusText = "● 正在启动中";
                    StatusColor = new SolidColorBrush(Color.FromRgb(255, 140, 0));
                    break;
                case ProcessMonitorState.Running:
                    StatusText = "● 运行中";
                    StatusColor = new SolidColorBrush(Color.FromRgb(0, 128, 0));
                    break;
                case ProcessMonitorState.StartFailed:
                    StatusText = "● 启动失败" + suffix;
                    StatusColor = new SolidColorBrush(Color.FromRgb(178, 34, 34));
                    break;
            }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public Brush StatusColor
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
            _enabled = true;
            UpdateStatusDisplay();
        }

        public ProcessInfo ToProcessInfo()
        {
            return new ProcessInfo { Name = DisplayName, Path = this.Path, Enabled = this.Enabled };
        }

        public static ProcessViewModel FromProcessInfo(ProcessInfo info, Action saveCallback)
        {
            var vm = new ProcessViewModel
            {
                Path = info.Path,
                Enabled = info.Enabled,
                SaveCallback = saveCallback
            };
            vm.Name = vm.DisplayName;
            if (!info.Enabled)
                vm.SetMonitorState(ProcessMonitorState.NotMonitoring);
            return vm;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
