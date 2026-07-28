using System;

namespace MoveImageForm.Models
{
    /// <summary>
    /// 传输协议无关的远程文件元数据，替代 ISftpFile 耦合。
    /// SFTP 和 S3 统一使用此 DTO。
    /// </summary>
    public class RemoteFileInfo
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteTime { get; set; }
        public bool IsDirectory { get; set; }
    }
}
