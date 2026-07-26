using System;
using System.Security.Cryptography;
using System.Text;

namespace MoveImageForm.Services
{
    /// <summary>
    /// Windows DPAPI 加密/解密工具。
    /// 使用当前 Windows 用户凭据保护数据，仅限当前用户的当前机器解密。
    /// 比明文存储安全性高一个档次，但 Key Vault 方案可后续替换。
    /// </summary>
    public static class DpapiHelper
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MoveImageForm.Sftp.2026");

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (plainText.StartsWith("DPAPI:")) return plainText; // 已经加密

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return "DPAPI:" + Convert.ToBase64String(cipherBytes);
            }
            catch
            {
                // 加密失败时回退明文（非 Windows 系统可能没有 DPAPI）
                return plainText;
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return cipherText;
            if (!cipherText.StartsWith("DPAPI:"))
                return cipherText; // 尚未加密的旧数据，原样返回

            try
            {
                string base64 = cipherText.Substring(6);
                byte[] cipherBytes = Convert.FromBase64String(base64);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // 解密失败（可能是换了 Windows 账号或机器）
                // 返回标记值，上层检测到空密码会提示用户重新输入
                return "";
            }
        }

        /// <summary>检测是否为已加密的密码</summary>
        public static bool IsEncrypted(string text)
        {
            return !string.IsNullOrEmpty(text) && text.StartsWith("DPAPI:");
        }
    }
}
