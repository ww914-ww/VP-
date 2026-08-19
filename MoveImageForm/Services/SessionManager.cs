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
        private readonly Dictionary<string, IFileTransferService> _sessions = new Dictionary<string, IFileTransferService>();
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
        public IFileTransferService ActiveSftp =>
            _sessions.Values.FirstOrDefault(s => s.IsConnected);

        public event Action LoginStateChanged;

        /// <summary>检查指定 Profile 是否已连接</summary>
        public bool IsProfileConnected(string profileName)
        {
            return _sessions.TryGetValue(profileName, out var s) && s.IsConnected;
        }

        /// <summary>获取指定 Profile 的 SFTP 会话</summary>
        public IFileTransferService GetSession(string profileName)
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

        /// <summary>用指定 Profile 和密码登录，建立传输连接。失败时抛异常。</summary>
        public void Login(SftpProfile profile, string password)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // 如果已连接同名 Profile，先断开
            DisconnectProfile(profile.Name);

            var transport = CreateTransport(profile, password);
            transport.Connect();

            _sessions[profile.Name] = transport;
            _sessionProfiles[profile.Name] = profile;
            _sessionPasswords[profile.Name] = password;
            LoginStateChanged?.Invoke();
        }

        /// <summary>外部按需注册已创建的会话（用于 SMB 等无需登录的场景）</summary>
        public void RegisterSession(SftpProfile profile, IFileTransferService transport, string password)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            DisconnectProfile(profile.Name);
            _sessions[profile.Name] = transport;
            _sessionProfiles[profile.Name] = profile;
            _sessionPasswords[profile.Name] = password ?? "";
            LoginStateChanged?.Invoke();
        }

        /// <summary>根据 Profile 的 TransportType 创建对应的传输服务</summary>
        private IFileTransferService CreateTransport(SftpProfile profile, string password)
        {
            if (profile.IsSmb)
            {
                return new SmbService(profile.RemoteRoot);
            }
            if (profile.IsS3)
            {
                return new S3Service(
                    endpoint: profile.Host,
                    port: profile.Port,
                    accessKey: profile.Username,
                    secretKey: password,
                    bucketName: profile.RemoteRoot,
                    useSsl: profile.Port == 443
                );
            }
            return new SftpService(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
        }

        /// <summary>使用存储在 config 中的（DPAPI 解密后）密码批量登录所有活跃 Profile</summary>
        public int LoginAll(List<SftpProfile> profiles)
        {
            int success = 0;
            foreach (var profile in profiles)
            {
                try
                {
                    // SMB 无需密码，直接连接
                    if (profile.IsSmb)
                    {
                        Login(profile, "");
                        success++;
                        continue;
                    }

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

        /// <summary>用缓存的密码重新连接指定 Profile。失败时保留凭据以便再次重试；内存无缓存时回退到 config 保存的密码</summary>
        public bool Reconnect(string profileName)
        {
            _sessionPasswords.TryGetValue(profileName, out var password);
            if (!_sessionProfiles.TryGetValue(profileName, out var profile))
                return false;

            // 内存无缓存密码时（如启动时自动登录失败过的账号），
            // 回退使用 config 中 DPAPI 保存的密码，保证断线重连不需要重新输入
            if (string.IsNullOrEmpty(password))
            {
                try { password = profile.GetPlainPassword(); }
                catch { return false; }
            }
            if (string.IsNullOrEmpty(password))
                return false;

            // 只释放旧连接，先不清凭据，避免 Connect 失败后无法再次重连
            if (_sessions.TryGetValue(profileName, out var old))
            {
                try { old.Dispose(); } catch { }
                _sessions.Remove(profileName);
            }

            try
            {
                var transport = CreateTransport(profile, password);
                transport.Connect();
                _sessions[profile.Name] = transport;
                _sessionProfiles[profile.Name] = profile;
                _sessionPasswords[profile.Name] = password;
                LoginStateChanged?.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用指定 Profile 重新连接（Profile 来自 config，即使启动时自动登录失败从未注册过会话也能重连）</summary>
        public bool Reconnect(SftpProfile profile)
        {
            if (profile == null) return false;

            string password = null;
            _sessionPasswords.TryGetValue(profile.Name, out password);
            // 内存无缓存密码时回退使用 config 中 DPAPI 保存的密码
            if (string.IsNullOrEmpty(password))
            {
                try { password = profile.GetPlainPassword(); }
                catch { return false; }
            }
            if (string.IsNullOrEmpty(password))
                return false;

            try
            {
                DisconnectProfile(profile.Name);
                Login(profile, password);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 带重试的自动重连：用缓存的密码重连，最多尝试 maxRetries 次，每次间隔 500ms。
        /// 返回 true 表示重连成功，false 表示全部失败。
        /// </summary>
        public bool TryReconnectWithRetry(string profileName, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (Reconnect(profileName))
                    return true;

                if (i < maxRetries - 1)
                    System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        /// <summary>带重试的自动重连（按 Profile，可重连从未注册过会话的账号）</summary>
        public bool TryReconnectWithRetry(SftpProfile profile, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (Reconnect(profile))
                    return true;

                if (i < maxRetries - 1)
                    System.Threading.Thread.Sleep(500);
            }
            return false;
        }
    }
}
