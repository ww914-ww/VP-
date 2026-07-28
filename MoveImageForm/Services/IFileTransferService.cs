using System;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 文件传输服务接口 — 协议无关。
    /// 现有实现：SftpService (SSH.NET), S3Service (Minio SDK)。
    /// </summary>
    public interface IFileTransferService : IDisposable
    {
        bool IsConnected { get; }
        void Connect();
        void Disconnect();

        /// <summary>上传文件。appendOnly=true 时仅追加（跳过已存在的文件）。返回 true=成功, false=跳过</summary>
        bool UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false);

        /// <summary>检查远程文件是否存在</summary>
        bool FileExists(string remoteRelativePath);

        /// <summary>获取远程文件大小（字节）</summary>
        long GetFileSize(string remoteRelativePath);

        /// <summary>删除远程文件</summary>
        void DeleteFile(string remoteRelativePath);

        /// <summary>确保远程目录存在</summary>
        void EnsureDirectoryExists(string remoteRelativePath);

        /// <summary>下载远程文件到本地</summary>
        void DownloadFile(string remoteRelativePath, string localPath);

        /// <summary>读取远程文本文件内容</summary>
        string ReadAllText(string remoteRelativePath);

        /// <summary>列出远程目录下的文件和文件夹</summary>
        RemoteFileInfo[] ListDirectory(string remoteRelativePath);

        /// <summary>获取远程文件最后修改时间</summary>
        DateTime GetLastWriteTime(string remoteRelativePath);

        /// <summary>
        /// 增量上传判断：本地文件与远程文件大小一致且本地修改时间不晚于远程 → 无需上传。
        /// 用于 Copy 模式跳过未变更文件，减少无意义上行流量。
        /// </summary>
        bool IsTargetFileCurrent(string localPath, string remoteRelativePath);
    }
}
