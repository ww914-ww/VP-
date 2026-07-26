using System;
using System.Collections.Generic;
using System.Linq;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 管理多个 SFTP 会话（每个 SourcePath 可以有不同的账号）。
    /// 无 WPF 依赖，纯逻辑，方便单元测试。
    /// </summary>
    public class SessionManager
    {
        // 按 Profile.Name 索引的会话
        private readonly Dictionary<string, ISftpService> _sessions = new Dictionary<string, ISftpService>();
        private readonly Dictionary<string, SftpProfile> _sessionProfiles = new Dictionary<string, SftpProfile>();
        private readonly Dictionary<string, string> _sessionPasswords = new Dictionary<string, string>();

        /// <summary>是否有任意会话已连接</summary>
        public bool IsLoggedIn => _sessions.Values.Any(s => s.IsConnected);

        /// <summary>已连接的 Profile 名称列表</summary>
        public List<string> ConnectedProfiles =>
            _sessions.Where(kv => kv.Value.IsConnected).Select(kv => kv.Key).ToList();

        /// <summary>所有活跃的 Profile</summary>
        public List<SftpProfile> ActiveProfiles =>
            _sessionProfiles.Values.ToList();

        /// <summary>主 Profile（第一个连接的）</summary>
        public SftpProfile CurrentProfile =>
            _sessionProfiles.Values.FirstOrDefault();

        /// <summary>主角色（第一个连接的 Profile 的角色）</summary>
        public string CurrentRole =>
            (CurrentProfile?.Role ?? "").ToLower();

        /// <summary>默认 SFTP 服务（第一个连接的会话）</summary>
        public ISftpService ActiveSftp =>
            _sessions.Values.FirstOrDefault(s => s.IsConnected);

        public event Action LoginStateChanged;

        /// <summary>检查指定 Profile 是否已连接</summary>
        public bool IsProfileConnected(string profileName)
        {
            return _sessions.TryGetValue(profileName, out var s) && s.IsConnected;
        }

        /// <summary>获取指定 Profile 的 SFTP 会话</summary>
        public ISftpService GetSession(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return null;
            _sessions.TryGetValue(profileName, out var s);
            return s != null && s.IsConnected ? s : null;
        }

        /// <summary>获取指定 Profile 的明文密码</summary>
        public string GetProfilePassword(string profileName)
        {
            _sessionPasswords.TryGetValue(profileName, out var pwd);
            return pwd;
        }

        /// <summary>用指定 Profile 和密码登录，建立 SFTP 连接。失败时抛异常。</summary>
        public void Login(SftpProfile profile, string password)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // 如果已连接同名 Profile，先断开
            DisconnectProfile(profile.Name);

            var sftp = new SftpService(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
            sftp.Connect();

            _sessions[profile.Name] = sftp;
            _sessionProfiles[profile.Name] = profile;
            _sessionPasswords[profile.Name] = password;
            LoginStateChanged?.Invoke();
        }

        /// <summary>使用存储在 config 中的（DPAPI 解密后）密码批量登录所有活跃 Profile</summary>
        public int LoginAll(List<SftpProfile> profiles)
        {
            int success = 0;
            foreach (var profile in profiles)
            {
                try
                {
                    string password = profile.GetPlainPassword();
                    if (string.IsNullOrEmpty(password))
                        continue; // 密码为空或解密失败，跳过

                    Login(profile, password);
                    success++;
                }
                catch
                {
                    // 密码错误或连接失败，这个 Profile 跳过
                }
            }
            return success;
        }

        /// <summary>断开指定 Profile 的连接</summary>
        public void DisconnectProfile(string profileName)
        {
            if (_sessions.TryGetValue(profileName, out var sftp))
            {
                sftp.Dispose();
                _sessions.Remove(profileName);
                _sessionProfiles.Remove(profileName);
                _sessionPasswords.Remove(profileName);
            }
        }

        /// <summary>登出所有连接</summary>
        public void Logout()
        {
            foreach (var sftp in _sessions.Values)
                sftp.Dispose();
            _sessions.Clear();
            _sessionProfiles.Clear();
            _sessionPasswords.Clear();
            LoginStateChanged?.Invoke();
        }

        /// <summary>切换账号：先全部登出，再用新账号登录</summary>
        public bool SwitchTo(SftpProfile profile, string password)
        {
            Logout();
            try
            {
                Login(profile, password);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用缓存的密码重新连接指定 Profile</summary>
        public bool Reconnect(string profileName)
        {
            if (!_sessionPasswords.TryGetValue(profileName, out var password))
                return false;
            if (!_sessionProfiles.TryGetValue(profileName, out var profile))
                return false;

            try
            {
                DisconnectProfile(profileName);
                Login(profile, password);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
