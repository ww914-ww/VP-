using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    public class SftpService : IFileTransferService
    {
        private SftpClient _client;
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly string _remoteRoot;

        // ===== 增量记忆缓存（方案B）：避免每轮对每个文件都做 2 次 SFTP 往返（Exists + GetAttributes）=====
        // 只在远程比对确认一致或上传成功时写入；程序重启自动清空；仅被 IsTargetFileCurrent 消费。
        // 语义前提：远程目录只被本工具写入，故"本地文件未变"⇒"远程仍一致"。
        private const int SyncedCacheMax = 50000;
        private readonly Dictionary<string, SyncedFileInfo> _syncedFiles =
            new Dictionary<string, SyncedFileInfo>(StringComparer.OrdinalIgnoreCase);
        // 写入顺序队列（含快照）：超上限时按写入顺序淘汰最旧记录，快照不匹配的旧条目作废，避免误删新记录
        private readonly Queue<KeyValuePair<string, SyncedFileInfo>> _syncedOrder =
            new Queue<KeyValuePair<string, SyncedFileInfo>>();

        /// <summary>已确认同步的本地文件快照：大小 + 本地修改时间（NTFS 100ns 精度）</summary>
        private struct SyncedFileInfo
        {
            public long Length;
            public DateTime LastWriteTime;
        }

        /// <summary>缓存键：本地路径 + 远端路径（NUL 不会出现在合法路径中，防止分隔符碰撞）</summary>
        private static string CacheKey(string localPath, string remoteRelativePath)
        {
            return localPath + "\0" + remoteRelativePath;
        }

        /// <summary>记录"已确认同步"的文件快照；超上限时按写入顺序淘汰最旧记录（FIFO）。</summary>
        private void RecordSynced(string localPath, string remoteRelativePath, SyncedFileInfo info)
        {
            string key = CacheKey(localPath, remoteRelativePath);
            _syncedFiles[key] = info;
            _syncedOrder.Enqueue(new KeyValuePair<string, SyncedFileInfo>(key, info));
            while (_syncedFiles.Count > SyncedCacheMax && _syncedOrder.Count > 0)
            {
                var oldest = _syncedOrder.Dequeue();
                SyncedFileInfo current;
                // 快照与当前记录一致才淘汰，否则说明该 key 已被更新的记录覆盖（旧队列条目作废）
                if (_syncedFiles.TryGetValue(oldest.Key, out current)
                    && current.Length == oldest.Value.Length
                    && current.LastWriteTime == oldest.Value.LastWriteTime)
                {
                    _syncedFiles.Remove(oldest.Key);
                }
            }
        }

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

            // 释放旧的 SftpClient（避免资源泄漏）
            if (_client != null)
            {
                try { _client.Disconnect(); } catch { }
                try { _client.Dispose(); } catch { }
                _client = null;
            }

            _client = new SftpClient(_host, _port, _username, _password);
            // 单次 SFTP 操作最多等待 120 秒：连接半死（无响应无断开）时避免无限阻塞卡死（曾卡死 6 天）
            _client.OperationTimeout = TimeSpan.FromSeconds(120);
            _client.Connect();
        }

        /// <summary>判断异常是否为连接中断/超时类（半开连接、网络断、代理断），遍历内部异常以防包装</summary>
        private static bool IsConnectionException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is SshOperationTimeoutException || ex is SshConnectionException ||
                    ex is SocketException || ex is ProxyException)
                    return true;
                ex = ex.InnerException;
            }
            return false;
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

        /// <summary>上传单个文件到远程路径。appendOnly=true 时跳过已存在文件。</summary>
        public TransferUploadResult UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false)
        {
            try
            {
                string remoteDir = Path.GetDirectoryName(remoteRelativePath)
                    .Replace("\\", "/");
                EnsureDirectoryExists(remoteDir);

                string remoteFullPath = ResolveRemotePath(remoteRelativePath);

                if (appendOnly && _client.Exists(remoteFullPath))
                    return TransferUploadResult.Skip("远端已存在(追加模式)");

                using (var fileStream = File.OpenRead(localPath))
                {
                    _client.UploadFile(fileStream, remoteFullPath, true);
                }
                // 上传成功 → 记录到增量缓存，下一轮直接跳过（远程内容即本地内容）
                var localInfo = new FileInfo(localPath);
                RecordSynced(localPath, remoteRelativePath, new SyncedFileInfo { Length = localInfo.Length, LastWriteTime = localInfo.LastWriteTime });
                return TransferUploadResult.Ok();
            }
            catch (Exception ex)
            {
                // 连接类异常：主动断开，让上层检测 IsConnected 后中止本轮并触发重连
                if (IsConnectionException(ex))
                    Disconnect();
                return TransferUploadResult.Fail(ex.Message);
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
                var localInfo = new FileInfo(localPath);
                if (!localInfo.Exists)
                    return false;

                // 增量记忆缓存命中：本地文件与上次确认同步时完全一致 → 直接判定已同步（0 次 SFTP 往返）
                SyncedFileInfo cached;
                if (_syncedFiles.TryGetValue(CacheKey(localPath, remoteRelativePath), out cached)
                    && cached.Length == localInfo.Length
                    && cached.LastWriteTime == localInfo.LastWriteTime)
                {
                    return true;
                }

                string remoteFullPath = ResolveRemotePath(remoteRelativePath);
                if (!_client.Exists(remoteFullPath))
                    return false;

                var remoteAttrs = _client.GetAttributes(remoteFullPath);

                // 大小一致 + 本地修改时间不晚于远程 → 无需上传
                // SFTP 协议 v3 时间戳精度为秒级，NTFS 为 100纳秒，使用 2 秒容差防止截断导致误判
                bool sameSize = localInfo.Length == remoteAttrs.Size;
                bool notNewer = localInfo.LastWriteTime <= remoteAttrs.LastWriteTime
                    || Math.Abs((localInfo.LastWriteTime - remoteAttrs.LastWriteTime).TotalSeconds) < 2;
                bool current = sameSize && notNewer;
                if (current)
                    RecordSynced(localPath, remoteRelativePath, new SyncedFileInfo { Length = localInfo.Length, LastWriteTime = localInfo.LastWriteTime });
                return current;
            }
            catch (Exception ex)
            {
                // 连接类异常：主动断开，让上层检测 IsConnected 后中止本轮并触发重连
                if (IsConnectionException(ex))
                    Disconnect();
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
                !_client.ListDirectory(fullPath).Any(f => f.Name != "." && f.Name != ".."))
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
        public RemoteFileInfo[] ListDirectory(string remoteRelativePath)
        {
            string fullPath = ResolveRemotePath(remoteRelativePath);
            return _client.ListDirectory(fullPath)
                .Where(f => !f.Name.StartsWith("."))
                .Select(f => new RemoteFileInfo
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    Length = f.Length,
                    LastWriteTime = f.LastWriteTime,
                    IsDirectory = f.IsDirectory
                })
                .ToArray();
        }

        /// <summary>获取远程文件最后修改时间</summary>
        public DateTime GetLastWriteTime(string remoteRelativePath)
        {
            return _client.GetAttributes(ResolveRemotePath(remoteRelativePath)).LastWriteTime;
        }
    }
}
