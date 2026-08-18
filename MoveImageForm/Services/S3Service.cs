using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    /// <summary>
    /// S3 兼容对象存储传输实现（MinIO / AWS S3 等）。
    /// 使用纯 HttpClient + AWS Signature V4 签名，无需额外 NuGet 依赖。
    /// </summary>
    public class S3Service : IFileTransferService
    {
        private readonly string _endpoint;
        private readonly int _port;
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly string _bucketName;
        private readonly string _baseUrl;
        private readonly HttpClient _http;

        private bool _connected;

        public bool IsConnected => _connected;

        public S3Service(string endpoint, int port, string accessKey, string secretKey, string bucketName, bool useSsl = false)
        {
            // 处理 endpoint：用户可能存了完整 URL（如 http://172.16.0.200:12001），提取纯 host
            string ep = endpoint ?? "";
            if (ep.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(7);
                useSsl = false;
            }
            else if (ep.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(8);
                useSsl = true;
            }

            // 如果 endpoint 中已含端口号，优先使用
            int colonIdx = ep.LastIndexOf(':');
            if (colonIdx > 0)
            {
                string portStr = ep.Substring(colonIdx + 1);
                if (int.TryParse(portStr, out int extractedPort))
                {
                    port = extractedPort;
                    ep = ep.Substring(0, colonIdx);
                }
            }

            _endpoint = ep;
            _port = port;
            _accessKey = accessKey;
            _secretKey = secretKey;
            _bucketName = bucketName;
            _baseUrl = $"{(useSsl ? "https" : "http")}://{_endpoint}:{_port}";

            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        public void Connect()
        {
            if (_connected) return;

            // 验证连接：用 HEAD 探测 bucket，兼容性最好（无需任何 list 操作）
            try
            {
                using (var response = SendRequest(HttpMethod.Head, "", ""))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // HEAD 失败时，尝试 GET（不带 query 参数，最基础的兼容方式）
                        using (var getResponse = SendRequest(HttpMethod.Get, "", "?max-keys=1"))
                        {
                            if (!getResponse.IsSuccessStatusCode)
                            {
                                string body = "";
                                try { body = getResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
                                throw new Exception($"S3 认证失败 ({(int)getResponse.StatusCode}): {TruncateError(body)}");
                            }
                        }
                    }
                }
                _connected = true;
            }
            catch (Exception ex)
            {
                throw new Exception($"无法连接到 S3 存储桶 {_bucketName}: {ex.Message}", ex);
            }
        }

        public void Disconnect()
        {
            _connected = false;
        }

        public void Dispose()
        {
            Disconnect();
            _http?.Dispose();
        }

        // ===== 文件操作 =====

        public bool UploadFile(string localPath, string remoteRelativePath, bool appendOnly = false)
        {
            try
            {
                string key = NormalizeKey(remoteRelativePath);

                // 追加模式：跳过已存在的文件
                if (appendOnly && FileExists(remoteRelativePath))
                    return false;

                // 确保中间"目录"存在（S3 中可跳过，但也无妨创建一个空标记）
                string dirKey = GetDirectoryKey(remoteRelativePath);
                if (!string.IsNullOrEmpty(dirKey) && dirKey != key)
                {
                    EnsureDirectoryExistsInternal(dirKey);
                }

                byte[] fileBytes = File.ReadAllBytes(localPath);
                using (var content = new ByteArrayContent(fileBytes))
                {
                    content.Headers.Add("Content-Type", "application/octet-stream");
                    using (var response = SendRequest(HttpMethod.Put, key, "", content))
                    {
                        if (!response.IsSuccessStatusCode)
                            return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UploadFile error: {ex.Message}");
                return false;
            }
        }

        public bool FileExists(string remoteRelativePath)
        {
            try
            {
                string key = NormalizeKey(remoteRelativePath);
                using (var response = SendRequest(HttpMethod.Head, key, ""))
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        public long GetFileSize(string remoteRelativePath)
        {
            try
            {
                string key = NormalizeKey(remoteRelativePath);
                using (var response = SendRequest(HttpMethod.Head, key, ""))
                {
                    if (response.IsSuccessStatusCode)
                        return response.Content.Headers.ContentLength ?? 0;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        public void DeleteFile(string remoteRelativePath)
        {
            string key = NormalizeKey(remoteRelativePath);
            using (var response = SendRequest(HttpMethod.Delete, key, ""))
            {
                // 删除操作：204 No Content 或 404 都已不存在，忽略状态码
            }
        }

        /// <summary>S3 中无需显式创建目录，这里创建空对象作为目录标记</summary>
        public void EnsureDirectoryExists(string remoteRelativePath)
        {
            string dirKey = GetDirectoryKey(remoteRelativePath);
            if (!string.IsNullOrEmpty(dirKey))
            {
                EnsureDirectoryExistsInternal(dirKey);
            }
        }

        private void EnsureDirectoryExistsInternal(string dirKey)
        {
            // S3 中目录是虚拟的，但为了兼容 SFTP 行为，创建一个零字节标记对象
            if (!FileExists(dirKey))
            {
                try
                {
                    using (var content = new ByteArrayContent(new byte[0]))
                    {
                        var response = SendRequest(HttpMethod.Put, dirKey, "", content);
                        response.Dispose();
                    }
                }
                catch { /* 目录创建失败不影响上传 */ }
            }
        }

        public void DownloadFile(string remoteRelativePath, string localPath)
        {
            string key = NormalizeKey(remoteRelativePath);
            string localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                Directory.CreateDirectory(localDir);

            using (var response = SendRequest(HttpMethod.Get, key, ""))
            {
                response.EnsureSuccessStatusCode();
                using (var fileStream = File.Create(localPath))
                {
                    response.Content.CopyToAsync(fileStream).GetAwaiter().GetResult();
                }
            }
        }

        public string ReadAllText(string remoteRelativePath)
        {
            string key = NormalizeKey(remoteRelativePath);
            using (var response = SendRequest(HttpMethod.Get, key, ""))
            {
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        public RemoteFileInfo[] ListDirectory(string remoteRelativePath)
        {
            var result = new List<RemoteFileInfo>();
            string prefix = NormalizeKey(remoteRelativePath, forDirectory: true);

            try
            {
                string encodedPrefix = string.IsNullOrEmpty(prefix) ? "" : Uri.EscapeDataString(prefix);
                string query = "?list-type=2&delimiter=/";
                if (!string.IsNullOrEmpty(encodedPrefix))
                    query += "&prefix=" + encodedPrefix;

                using (var response = SendRequest(HttpMethod.Get, "", query))
                {
                    response.EnsureSuccessStatusCode();
                    string xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var doc = XDocument.Parse(xml);
                    XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";

                    // 解析文件
                    foreach (var contents in doc.Descendants(ns + "Contents"))
                    {
                        string name = contents.Element(ns + "Key")?.Value ?? "";
                        // 文件名 = 去除 prefix 后的部分
                        string displayName = prefix.Length > 0 && name.StartsWith(prefix)
                            ? name.Substring(prefix.Length)
                            : name;

                        // 跳过目录标记（以 / 结尾且长度就是 prefix+/）
                        if (string.IsNullOrEmpty(displayName) || (displayName.EndsWith("/") && displayName.Length == 1))
                            continue;

                        // 跳过以 / 结尾的文件名（目录标记）
                        if (displayName.EndsWith("/"))
                            displayName = displayName.TrimEnd('/');

                        result.Add(new RemoteFileInfo
                        {
                            Name = displayName,
                            FullPath = name,
                            Length = long.TryParse(contents.Element(ns + "Size")?.Value ?? "0", out long size) ? size : 0,
                            LastWriteTime = DateTime.TryParse(contents.Element(ns + "LastModified")?.Value, out DateTime dt) ? dt : DateTime.MinValue,
                            IsDirectory = false
                        });
                    }

                    // 解析子目录（CommonPrefixes）
                    foreach (var commonPrefix in doc.Descendants(ns + "CommonPrefixes"))
                    {
                        string dirName = commonPrefix.Element(ns + "Prefix")?.Value ?? "";
                        if (dirName.EndsWith("/"))
                            dirName = dirName.TrimEnd('/');
                        string displayName = prefix.Length > 0 && dirName.StartsWith(prefix)
                            ? dirName.Substring(prefix.Length)
                            : dirName;

                        if (string.IsNullOrEmpty(displayName))
                            continue;

                        result.Add(new RemoteFileInfo
                        {
                            Name = displayName,
                            FullPath = dirName,
                            Length = 0,
                            LastWriteTime = DateTime.MinValue,
                            IsDirectory = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListDirectory error: {ex.Message}");
            }

            return result.ToArray();
        }

        public DateTime GetLastWriteTime(string remoteRelativePath)
        {
            try
            {
                string key = NormalizeKey(remoteRelativePath);
                using (var response = SendRequest(HttpMethod.Head, key, ""))
                {
                    if (response.IsSuccessStatusCode && response.Content.Headers.LastModified.HasValue)
                        return response.Content.Headers.LastModified.Value.DateTime;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        public bool IsTargetFileCurrent(string localPath, string remoteRelativePath)
        {
            try
            {
                string key = NormalizeKey(remoteRelativePath);

                // 检查远程文件是否存在
                if (!FileExists(remoteRelativePath))
                    return false;

                var localInfo = new FileInfo(localPath);
                if (!localInfo.Exists)
                    return false;

                long remoteSize = GetFileSize(remoteRelativePath);
                DateTime remoteTime = GetLastWriteTime(remoteRelativePath);

                // S3 Last-Modified 时间戳精度为秒级，NTFS 为 100纳秒，使用 2 秒容差防止截断导致误判
                bool sameSize = localInfo.Length == remoteSize;
                bool notNewer = localInfo.LastWriteTime <= remoteTime
                    || Math.Abs((localInfo.LastWriteTime - remoteTime).TotalSeconds) < 2;
                return sameSize && notNewer;
            }
            catch
            {
                return false;
            }
        }

        // ===== 内部方法 =====

        /// <summary>规范化 S3 对象 Key。去除首部 / 和尾部 /</summary>
        private string NormalizeKey(string remoteRelativePath, bool forDirectory = false)
        {
            string key = remoteRelativePath?.TrimStart('/') ?? "";
            if (!forDirectory)
                key = key.TrimEnd('/');
            return key;
        }

        /// <summary>获取目录前缀（用于创建目录标记）</summary>
        private string GetDirectoryKey(string filePath)
        {
            string key = NormalizeKey(filePath);
            int lastSlash = key.LastIndexOf('/');
            if (lastSlash <= 0) return "";
            return key.Substring(0, lastSlash) + "/";
        }

        /// <summary>
        /// 发送带 AWS Signature V2 签名的 HTTP 请求（深信服对象网关使用 V2 签名）。
        /// </summary>
        private HttpResponseMessage SendRequest(HttpMethod method, string key, string query, HttpContent content = null)
        {
            string uriPath = string.IsNullOrEmpty(key) ? $"/{_bucketName}/" : $"/{_bucketName}/{key}";
            string fullQuery = query;

            var request = new HttpRequestMessage(method, $"{_baseUrl}{uriPath}{fullQuery}");

            if (content != null)
            {
                request.Content = content;
            }

            // 使用 Signature V2 签名（深信服 Sangfor 网关兼容）
            SignRequestV2(request, method);

            return _http.SendAsync(request).GetAwaiter().GetResult();
        }

        /// <summary>
        /// AWS Signature V2 签名。（深信服对象网关只支持 V2）
        /// </summary>
        private void SignRequestV2(HttpRequestMessage request, HttpMethod method)
        {
            string dateString = DateTime.UtcNow.ToString("R"); // RFC 1123
            request.Headers.TryAddWithoutValidation("Date", dateString);

            // 构建 String to Sign
            string contentType = "";
            string contentMd5 = "";

            if (request.Content != null)
            {
                if (request.Content.Headers.ContentType != null)
                    contentType = request.Content.Headers.ContentType.ToString();
                if (request.Content.Headers.ContentMD5 != null)
                    contentMd5 = Convert.ToBase64String(request.Content.Headers.ContentMD5);
            }

            // CanonicalizedResource: /bucket/key?query (不在 ?/=/& 上编码)
            string canonicalResource = request.RequestUri.AbsolutePath;
            if (!string.IsNullOrEmpty(request.RequestUri.Query))
                canonicalResource += request.RequestUri.Query;

            string stringToSign = method.Method + "\n" +
                contentMd5 + "\n" +
                contentType + "\n" +
                dateString + "\n" +
                canonicalResource;

            using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_secretKey)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
                string signature = Convert.ToBase64String(hash);
                request.Headers.TryAddWithoutValidation("Authorization",
                    $"AWS {_accessKey}:{signature}");
            }
        }

        // ===== 加密工具方法 =====

        private static string SHA256Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return HexEncode(hash);
            }
        }

        private static string SHA256Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                return HexEncode(hash);
            }
        }

        private static byte[] HmacSHA256(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static byte[] GetSignatureKey(string secretKey, string dateStamp, string region, string service)
        {
            byte[] kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
            byte[] kDate = HmacSHA256(kSecret, Encoding.UTF8.GetBytes(dateStamp));
            byte[] kRegion = HmacSHA256(kDate, Encoding.UTF8.GetBytes(region));
            byte[] kService = HmacSHA256(kRegion, Encoding.UTF8.GetBytes(service));
            return HmacSHA256(kService, Encoding.UTF8.GetBytes("aws4_request"));
        }

        private static string HexEncode(byte[] bytes)
        {
            // 不使用 BitConverter（它会加 -），手动转换确保正确
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string TruncateError(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            return message.Length > 300 ? message.Substring(0, 300) : message;
        }
    }
}
