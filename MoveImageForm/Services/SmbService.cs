using System;
using System.IO;
using System.Linq;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    /// <summary>SMB / 本地文件系统传输服务，直接走 System.IO，无需认证。</summary>
    public class SmbService : IFileTransferService
    {
        private readonly string _remoteRoot;
        private bool _connected;

        public bool IsConnected => _connected;

        public SmbService(string remoteRoot)
        {
            _remoteRoot = remoteRoot ?? "";
        }

        public void Connect()
        {
            // 确保目标目录存在，不存在则尝试创建
            if (!string.IsNullOrEmpty(_remoteRoot) && !Directory.Exists(_remoteRoot))
                Directory.CreateDirectory(_remoteRoot);
            _connected = true;
        }

        public void Disconnect()
        {
            _connected = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private string ResolvePath(string relativePath)
        {
            string combined = Path.Combine(_remoteRoot, relativePath ?? "");
            return Path.GetFullPath(combined);
        }

        public bool UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false)
        {
            if (!File.Exists(localPath)) return false;
            string destPath = ResolvePath(remoteRelativePath);
            EnsureDirectoryExists(Path.GetDirectoryName(destPath));

            if (appendOnly)
            {
                // 追加模式：目标已存在则跳过
                if (File.Exists(destPath))
                {
                    var srcInfo = new FileInfo(localPath);
                    var dstInfo = new FileInfo(destPath);
                    if (dstInfo.Length == srcInfo.Length && dstInfo.LastWriteTime >= srcInfo.LastWriteTime)
                        return true; // 已存在且一致，跳过
                }
            }

            File.Copy(localPath, destPath, true);
            return true;
        }

        public bool FileExists(string remoteRelativePath)
        {
            return File.Exists(ResolvePath(remoteRelativePath));
        }

        public long GetFileSize(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }

        public void DeleteFile(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            if (File.Exists(path)) File.Delete(path);
        }

        public void EnsureDirectoryExists(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        public void DownloadFile(string remoteRelativePath, string localPath)
        {
            string src = ResolvePath(remoteRelativePath);
            if (File.Exists(src))
            {
                string dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(src, localPath, true);
            }
        }

        public string ReadAllText(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public RemoteFileInfo[] ListDirectory(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            if (!Directory.Exists(path)) return new RemoteFileInfo[0];

            var dirInfo = new DirectoryInfo(path);
            return dirInfo.GetFileSystemInfos()
                .Select(f => new RemoteFileInfo
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    Length = (f is FileInfo fi) ? fi.Length : 0,
                    LastWriteTime = f.LastWriteTime,
                    IsDirectory = f is DirectoryInfo
                })
                .ToArray();
        }

        public DateTime GetLastWriteTime(string remoteRelativePath)
        {
            string path = ResolvePath(remoteRelativePath);
            return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
        }

        public bool IsTargetFileCurrent(string localPath, string remoteRelativePath)
        {
            string remotePath = ResolvePath(remoteRelativePath);
            if (!File.Exists(localPath) || !File.Exists(remotePath)) return false;
            var local = new FileInfo(localPath);
            var remote = new FileInfo(remotePath);
            return local.Length == remote.Length && local.LastWriteTime <= remote.LastWriteTime;
        }
    }
}
