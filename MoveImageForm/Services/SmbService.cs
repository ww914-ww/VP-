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

        public TransferUploadResult UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false)
        {
            try
            {
                if (!File.Exists(localPath))
                    return TransferUploadResult.Fail("本地文件不存在");

                string destPath = ResolvePath(remoteRelativePath);
                EnsureDirectoryExists(Path.GetDirectoryName(destPath));

                if (appendOnly && File.Exists(destPath))
                    return TransferUploadResult.Skip("远端已存在(追加模式)");

                File.Copy(localPath, destPath, true);
                return TransferUploadResult.Ok();
            }
            catch (Exception ex)
            {
                return TransferUploadResult.Fail(ex.Message);
            }
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
            // SMB 文件系统时间戳精度可能低于 NTFS（秒级 vs 100纳秒），
            // 使用 2 秒容差避免因截断导致的虚假"文件不一致"判断
            bool sameSize = local.Length == remote.Length;
            bool notNewer = local.LastWriteTime <= remote.LastWriteTime
                || Math.Abs((local.LastWriteTime - remote.LastWriteTime).TotalSeconds) < 2;
            return sameSize && notNewer;
        }
    }
}
