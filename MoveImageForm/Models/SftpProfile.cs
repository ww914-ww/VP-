using System.Xml.Serialization;
using MoveImageForm.Services;

namespace MoveImageForm.Models
{
    [XmlRoot("Profile")]
    public class SftpProfile
    {
        [XmlElement]
        public string Name { get; set; } = "";

        [XmlElement]
        public string Role { get; set; } = "upload";  // "upload" | "admin" | "readonly"

        /// <summary>传输协议类型："SFTP"（默认，兼容旧配置）或 "S3"</summary>
        [XmlElement]
        public string TransportType { get; set; } = "SFTP";

        /// <summary>是否为 S3 传输模式</summary>
        [XmlIgnore]
        public bool IsS3 => (TransportType ?? "").ToUpper() == "S3";

        /// <summary>是否为 SMB 传输模式（内置系统账号）</summary>
        [XmlIgnore]
        public bool IsSmb => (TransportType ?? "").ToUpper() == "SMB";

        /// <summary>系统内置 SMB 账号名</summary>
        public const string SmbProfileName = "SMB传输";

        [XmlElement]
        public string Host { get; set; } = "";

        [XmlElement]
        public int Port { get; set; } = 22;

        [XmlElement]
        public string Username { get; set; } = "";

        /// <summary>
        /// 存储格式：DPAPI 加密后以 "DPAPI:" 前缀存储。
        /// 明文仅在内存中使用，不落地到磁盘。
        /// </summary>
        [XmlElement]
        public string Password { get; set; } = "";

        [XmlElement]
        public string RemoteRoot { get; set; } = "/";

        /// <summary>返回可用于 SFTP 连接的明文密码（自动解密 DPAPI）</summary>
        public string GetPlainPassword()
        {
            return DpapiHelper.Decrypt(Password);
        }

        /// <summary>加密密码以便持久化存储</summary>
        public void EncryptPassword()
        {
            Password = DpapiHelper.Encrypt(Password);
        }

        /// <summary>密码是否已是加密状态</summary>
        [XmlIgnore]
        public bool IsPasswordEncrypted => DpapiHelper.IsEncrypted(Password);

        /// <summary>账号列表显示用的连接信息</summary>
        [XmlIgnore]
        public string ConnectionSummary => IsSmb
            ? $"目标: {RemoteRoot}"
            : $"{Username}@{Host}:{Port}";

        public override string ToString()
        {
            if (IsSmb)
                return $"{Name} (SMB/本地) → {RemoteRoot}";
            string type = IsS3 ? "S3" : "SFTP";
            return $"{Name} ({Role}/{type}) - {Username}@{Host}:{Port}";
        }
    }
}
