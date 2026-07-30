using System.ComponentModel;
using System.Xml.Serialization;

namespace MoveImageForm.Models
{
    /// <summary>
    /// 单个源文件夹配置项：本地路径 + 绑定的传输账号名称。
    /// </summary>
    public class SourceFolderEntry : INotifyPropertyChanged
    {
        private string _path = "";
        private string _profileName = "";
        private string _transferMode = "Cut";
        private int _index;

        [XmlElement]
        public string Path
        {
            get => _path;
            set { _path = value ?? ""; OnPropertyChanged(nameof(Path)); }
        }

        [XmlElement]
        public string ProfileName
        {
            get => _profileName;
            set { _profileName = value ?? ""; OnPropertyChanged(nameof(ProfileName)); }
        }

        /// <summary>每个目录独立的传输模式: Append / Copy / Cut（默认 Cut）</summary>
        [XmlElement]
        public string TransferMode
        {
            get => _transferMode;
            set { _transferMode = string.IsNullOrEmpty(value) ? "Cut" : value; OnPropertyChanged(nameof(TransferMode)); }
        }

        /// <summary>UI 列表中的序号（不持久化）</summary>
        [XmlIgnore]
        public int Index
        {
            get => _index;
            set
            {
                if (_index != value)
                {
                    _index = value;
                    OnPropertyChanged(nameof(Index));
                    OnPropertyChanged(nameof(Label));
                }
            }
        }

        /// <summary>UI 显示的标签文字（不持久化）</summary>
        [XmlIgnore]
        public string Label => $"监控文件夹{Index + 1}:";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
