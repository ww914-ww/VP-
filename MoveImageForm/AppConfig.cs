using System.Collections.Generic;
using System.Xml.Serialization;

namespace MoveImageForm
{
    [XmlRoot("Config")]
    public class AppConfig
    {
        // ===== 文件搬运（已有，不变） =====
        [XmlElement]
        public string SourcePath { get; set; } = "";
        [XmlElement]
        public string DestPath { get; set; } = "";
        [XmlElement]
        public string SourcePath2 { get; set; } = "";
        [XmlElement]
        public string DestPath2 { get; set; } = "";
        [XmlElement]
        public string TransferMode { get; set; } = "Cut";

        [XmlElement]
        public bool EnableTimeRule { get; set; } = true;
        [XmlElement]
        public int TimeIntervalSeconds { get; set; } = 60;

        [XmlElement]
        public bool EnableSizeRule { get; set; } = false;
        [XmlElement]
        public long SizeLimitMB { get; set; } = 100;

        [XmlElement]
        public bool EnableCountRule { get; set; } = false;
        [XmlElement]
        public int CountLimit { get; set; } = 1000;

        [XmlElement]
        public bool EnableEmptyFolderRule { get; set; } = false;
        [XmlElement]
        public double EmptyFolderHours { get; set; } = 24.0;

        // ===== 版本更新（新增） =====
        [XmlElement]
        public string CloudPath { get; set; } = "";
        [XmlElement]
        public string CloudUser { get; set; } = "";
        [XmlElement]
        public string CloudPassword { get; set; } = "";
        [XmlElement]
        public int CheckIntervalMinutes { get; set; } = 30;
        [XmlElement]
        public bool AutoUpdate { get; set; } = false;
        [XmlElement]
        public bool AutoStart { get; set; } = false;
        [XmlElement]
        public string LastCheckTime { get; set; } = "";

        // ===== 进程监听（新增） =====
        [XmlElement]
        public int ProcessCheckIntervalSeconds { get; set; } = 5;

        [XmlArray("WatchProcesses")]
        [XmlArrayItem("Process")]
        public List<ProcessInfo> WatchProcesses { get; set; } = new List<ProcessInfo>();
    }
}
