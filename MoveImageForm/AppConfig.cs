using System.Collections.Generic;
using System.Xml.Serialization;
using MoveImageForm.Models;

namespace MoveImageForm
{
    [XmlRoot("Config")]
    public class AppConfig
    {
        // ===== 文件搬运 — 源目录（新版：动态列表） =====
        [XmlArray("SourceFolders")]
        [XmlArrayItem("SourceFolder")]
        public List<SourceFolderEntry> SourceFolders { get; set; } = new List<SourceFolderEntry>();

        // ===== 旧版兼容字段（加载旧 config.xml 后自动迁移到 SourceFolders） =====
        [XmlElement]
        public string SourcePath { get; set; } = "";
        [XmlElement]
        public string SourcePath2 { get; set; } = "";

        /// <summary>SourcePath 绑定的 SFTP 账号（Profile.Name）</summary>
        [XmlElement]
        public string SourcePath1Profile { get; set; } = "";

        /// <summary>SourcePath2 绑定的 SFTP 账号（Profile.Name）</summary>
        [XmlElement]
        public string SourcePath2Profile { get; set; } = "";

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

        // ===== SFTP 账号配置 =====
        [XmlArray("SftpProfiles")]
        [XmlArrayItem("Profile")]
        public List<SftpProfile> SftpProfiles { get; set; } = new List<SftpProfile>();

        /// <summary>根据名称查找 Profile</summary>
        public SftpProfile FindProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return SftpProfiles.Find(p => p.Name == name);
        }

        /// <summary>获取 SourceFolderEntry 对应的 Profile。未配置返回 null。</summary>
        public SftpProfile GetProfileForSource(SourceFolderEntry entry)
        {
            if (entry == null) return null;
            return FindProfile(entry.ProfileName);
        }

        /// <summary>返回所有在 SourceFolders 中引用到的 Profile 名称</summary>
        public List<string> GetActiveProfileNames()
        {
            var names = new List<string>();
            if (SourceFolders != null)
            {
                foreach (var sf in SourceFolders)
                {
                    if (!string.IsNullOrWhiteSpace(sf.Path) && !string.IsNullOrWhiteSpace(sf.ProfileName))
                        names.Add(sf.ProfileName);
                }
            }
            // 兼容旧格式
            if (!string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(SourcePath1Profile))
                names.Add(SourcePath1Profile);
            if (!string.IsNullOrWhiteSpace(SourcePath2) && !string.IsNullOrWhiteSpace(SourcePath2Profile))
                names.Add(SourcePath2Profile);
            return names;
        }

        /// <summary>将旧版 SourcePath/SourcePath2 配置迁移到 SourceFolders 列表</summary>
        public void MigrateSourceFolders()
        {
            if (SourceFolders == null)
                SourceFolders = new List<SourceFolderEntry>();

            if (SourceFolders.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(SourcePath))
                {
                    SourceFolders.Add(new SourceFolderEntry
                    {
                        Path = SourcePath,
                        ProfileName = SourcePath1Profile ?? ""
                    });
                    SourcePath = "";
                    SourcePath1Profile = "";
                }
                if (!string.IsNullOrWhiteSpace(SourcePath2))
                {
                    SourceFolders.Add(new SourceFolderEntry
                    {
                        Path = SourcePath2,
                        ProfileName = SourcePath2Profile ?? ""
                    });
                    SourcePath2 = "";
                    SourcePath2Profile = "";
                }
            }
        }

        // ===== 版本更新 =====
        /// <summary>SMB 共享路径，如 \\192.168.1.100\AppUpdate</summary>
        [XmlElement]
        public string UpdateServerPath { get; set; } = "";
        [XmlElement]
        public int CheckIntervalMinutes { get; set; } = 30;
        [XmlElement]
        public bool AutoUpdate { get; set; } = false;
        [XmlElement]
        public bool AutoStart { get; set; } = false;
        [XmlElement]
        public string LastCheckTime { get; set; } = "";

        // ===== 进程监听 =====
        [XmlElement]
        public int ProcessCheckIntervalSeconds { get; set; } = 5;

        [XmlArray("WatchProcesses")]
        [XmlArrayItem("Process")]
        public List<ProcessInfo> WatchProcesses { get; set; } = new List<ProcessInfo>();
    }
}
