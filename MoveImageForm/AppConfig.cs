using System.Collections.Generic;
using System.Xml.Serialization;
using MoveImageForm.Models;

namespace MoveImageForm
{
    [XmlRoot("Config")]
    public class AppConfig
    {
        // ===== 文件搬运 — 源目录 =====
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

        /// <summary>多源文件夹配置；旧 SourcePath/SourcePath2 由 MigrateSourceFolders 迁移。</summary>
        [XmlArray("SourceFolders")]
        [XmlArrayItem("SourceFolder")]
        public List<SourceFolderEntry> SourceFolders { get; set; } = new List<SourceFolderEntry>();

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

        /// <summary>获取 SourcePath 对应的 Profile。未配置返回 null。</summary>
        public SftpProfile GetProfileForSource(string sourceKey)
        {
            string profileName = sourceKey == "SourcePath2"
                ? SourcePath2Profile
                : SourcePath1Profile;
            return FindProfile(profileName);
        }

        /// <summary>获取 SourceFolderEntry 绑定的 Profile。未配置返回 null。</summary>
        public SftpProfile GetProfileForSource(SourceFolderEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.ProfileName))
                return null;
            return FindProfile(entry.ProfileName);
        }

        /// <summary>将旧 SourcePath/SourcePath2 迁移到 SourceFolders（仅当 SourceFolders 为空时）。</summary>
        public void MigrateSourceFolders()
        {
            if (SourceFolders == null)
                SourceFolders = new List<SourceFolderEntry>();
            if (SourceFolders.Count > 0)
                return;

            string defaultMode = string.IsNullOrEmpty(TransferMode) ? "Cut" : TransferMode;

            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                SourceFolders.Add(new SourceFolderEntry
                {
                    Path = SourcePath,
                    ProfileName = SourcePath1Profile ?? "",
                    TransferMode = defaultMode
                });
            }
            if (!string.IsNullOrWhiteSpace(SourcePath2))
            {
                SourceFolders.Add(new SourceFolderEntry
                {
                    Path = SourcePath2,
                    ProfileName = SourcePath2Profile ?? "",
                    TransferMode = defaultMode
                });
            }
        }

        /// <summary>返回所有在源目录中引用到的 Profile 名称</summary>
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
            if (names.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(SourcePath1Profile))
                    names.Add(SourcePath1Profile);
                if (!string.IsNullOrWhiteSpace(SourcePath2) && !string.IsNullOrWhiteSpace(SourcePath2Profile))
                    names.Add(SourcePath2Profile);
            }
            return names;
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
