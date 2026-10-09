using System;
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

        // ===== 灰度更新与心跳（v1.1.0+） =====
        /// <summary>可选灰度：本机是否自愿参与灰度测试（默认 false 不参与）；
        /// 云端 gray.requireOptIn=true 时仅 true 的机台参与百分比灰度</summary>
        [XmlElement]
        public bool GrayOptIn { get; set; } = false;

        /// <summary>是否启用 SFTP 心跳上报（默认 true）</summary>
        [XmlElement]
        public bool HeartbeatEnabled { get; set; } = true;

        /// <summary>心跳上报间隔（分钟，默认 5）</summary>
        [XmlElement]
        public int HeartbeatIntervalMinutes { get; set; } = 5;

        /// <summary>心跳上报显式指定账号（Profile.Name，可选；为空自动选第一个可写 SFTP 账号）</summary>
        [XmlElement]
        public string HeartbeatProfileName { get; set; } = "";

        /// <summary>运行中更新检查间隔（小时，默认 4）</summary>
        [XmlElement]
        public int UpdateCheckIntervalHours { get; set; } = 4;

        // ===== 自动恢复搬运（v1.2.0+） =====
        /// <summary>自动恢复搬运开关（默认 false 关闭）：
        /// 账号在线且当前未在搬运时，按设定间隔自动开启搬运；已开启或离线时不动作</summary>
        [XmlElement]
        public bool AutoResumeEnabled { get; set; } = false;

        /// <summary>自动恢复间隔数值（默认 5，与 AutoResumeIntervalUnit 组合）</summary>
        [XmlElement]
        public int AutoResumeIntervalValue { get; set; } = 5;

        /// <summary>自动恢复间隔单位："Seconds" | "Minutes"（默认） | "Hours"</summary>
        [XmlElement]
        public string AutoResumeIntervalUnit { get; set; } = "Minutes";

        /// <summary>自动恢复间隔（带边界钳制：最短 5 秒，最长 24 小时）</summary>
        public TimeSpan GetAutoResumeInterval()
        {
            int value = AutoResumeIntervalValue;
            if (value < 1) value = 1;
            if (value > 9999) value = 9999;

            TimeSpan interval;
            switch ((AutoResumeIntervalUnit ?? "").Trim())
            {
                case "Seconds": interval = TimeSpan.FromSeconds(value); break;
                case "Hours": interval = TimeSpan.FromHours(value); break;
                default: interval = TimeSpan.FromMinutes(value); break;
            }
            if (interval < TimeSpan.FromSeconds(5)) return TimeSpan.FromSeconds(5);
            if (interval > TimeSpan.FromHours(24)) return TimeSpan.FromHours(24);
            return interval;
        }

        // ===== 进程监听 =====
        [XmlElement]
        public int ProcessCheckIntervalSeconds { get; set; } = 5;

        [XmlArray("WatchProcesses")]
        [XmlArrayItem("Process")]
        public List<ProcessInfo> WatchProcesses { get; set; } = new List<ProcessInfo>();
    }
}
