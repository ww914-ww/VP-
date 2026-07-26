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

        public override string ToString()
        {
            return $"{Name} ({Role}) - {Username}@{Host}:{Port}";
        }
    }
}
