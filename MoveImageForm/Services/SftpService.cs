using System;
using System.IO;
using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace MoveImageForm.Services
{
    public class SftpService : ISftpService
    {
        private SftpClient _client;
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly string _remoteRoot;

        public bool IsConnected => _client != null && _client.IsConnected;

        public SftpService(string host, int port, string username, string password, string remoteRoot = "/")
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            _remoteRoot = remoteRoot.TrimEnd('/');
        }

        public void Connect()
        {
            if (IsConnected) return;

            _client = new SftpClient(_host, _port, _username, _password);
            _client.Connect();
        }

        public void Disconnect()
        {
            if (_client != null)
            {
                if (_client.IsConnected)
                    _client.Disconnect();
                _client.Dispose();
                _client = null;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>解析远程绝对路径（拼接 remote_root + relative_path）</summary>
        private string ResolveRemotePath(string remoteRelativePath)
        {
            string path = (_remoteRoot + "/" + remoteRelativePath.TrimStart('/')).Replace("\\", "/");
            while (path.Contains("//"))
                path = path.Replace("//", "/");
            return path;
        }

        /// <summary>确保远程目录存在</summary>
        public void EnsureDirectoryExists(string remoteRelativePath)
        {
            string fullPath = ResolveRemotePath(remoteRelativePath);
            if (!_client.Exists(fullPath))
            {
                var parts = fullPath.Split('/');
                string current = "";
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    current += "/" + part;
                    if (!_client.Exists(current))
                        _client.CreateDirectory(current);
                }
            }
        }

        /// <summary>上传单个文件到远程路径。appendOnly=true 时跳过已存在文件。返回 true=成功, false=跳过</summary>
        public bool UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false)
        {
            try
            {
                string remoteDir = Path.GetDirectoryName(remoteRelativePath)
                    .Replace("\\", "/");
                EnsureDirectoryExists(remoteDir);

                string remoteFullPath = ResolveRemotePath(remoteRelativePath);

                if (appendOnly && _client.Exists(remoteFullPath))
                {
                    return false; // skipped — already exists
                }

                using (var fileStream = File.OpenRead(localPath))
                {
                    _client.UploadFile(fileStream, remoteFullPath, true);
                }
                return true; // success
            }
            catch
            {
                return false; // failed
            }
        }

        /// <summary>
        /// 增量上传判断：本地文件与远程文件大小一致，且本地修改时间不晚于远程 → 认为文件当前，无需上传。
        /// 远程文件不存在时返回 false（需要上传）。
        /// </summary>
        public bool IsTargetFileCurrent(string localPath, string remoteRelativePath)
        {
            try
            {
                string remoteFullPath = ResolveRemotePath(remoteRelativePath);
                if (!_client.Exists(remoteFullPath))
                    return false;

                var localInfo = new FileInfo(localPath);
                if (!localInfo.Exists)
                    return false;

                var remoteAttrs = _client.GetAttributes(remoteFullPath);

                // 大小一致 + 本地修改时间 ≤ 远程修改时间 → 无需上传
                return localInfo.Length == remoteAttrs.Size
                    && localInfo.LastWriteTime <= remoteAttrs.LastWriteTime;
            }
            catch
            {
                // 比较失败时保守处理：认为需要上传
                return false;
            }
        }

        /// <summary>检查远程文件是否存在</summary>
        public bool FileExists(string remoteRelativePath)
        {
            return _client.Exists(ResolveRemotePath(remoteRelativePath));
        }

        /// <summary>获取远程文件大小</summary>
        public long GetFileSize(string remoteRelativePath)
        {
            var attrs = _client.GetAttributes(ResolveRemotePath(remoteRelativePath));
            return attrs.Size;
        }

        /// <summary>删除远程文件</summary>
        public void DeleteFile(string remoteRelativePath)
        {
            _client.DeleteFile(ResolveRemotePath(remoteRelativePath));
        }

        /// <summary>删除远程空文件夹</summary>
        public void DeleteDirectoryIfEmpty(string remoteRelativePath)
        {
            string fullPath = ResolveRemotePath(remoteRelativePath);
            if (_client.Exists(fullPath) &&
                !_client.ListDirectory(fullPath).Any(f => !f.Name.EndsWith(".")))
            {
                _client.DeleteDirectory(fullPath);
            }
        }

        /// <summary>下载远程文件到本地</summary>
        public void DownloadFile(string remoteRelativePath, string localPath)
        {
            string remoteFullPath = ResolveRemotePath(remoteRelativePath);
            string localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                Directory.CreateDirectory(localDir);

            using (var fileStream = File.Create(localPath))
            {
                _client.DownloadFile(remoteFullPath, fileStream);
            }
        }

        /// <summary>读取远程文本文件内容</summary>
        public string ReadAllText(string remoteRelativePath)
        {
            string remoteFullPath = ResolveRemotePath(remoteRelativePath);
            using (var ms = new MemoryStream())
            {
                _client.DownloadFile(remoteFullPath, ms);
                ms.Position = 0;
                using (var reader = new StreamReader(ms, System.Text.Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>列出远程目录下的文件和文件夹</summary>
        public ISftpFile[] ListDirectory(string remoteRelativePath)
        {
            string fullPath = ResolveRemotePath(remoteRelativePath);
            return _client.ListDirectory(fullPath)
                .Where(f => !f.Name.StartsWith("."))
                .ToArray();
        }

        /// <summary>获取远程文件最后修改时间</summary>
        public DateTime GetLastWriteTime(string remoteRelativePath)
        {
            return _client.GetAttributes(ResolveRemotePath(remoteRelativePath)).LastWriteTime;
        }
    }
}
