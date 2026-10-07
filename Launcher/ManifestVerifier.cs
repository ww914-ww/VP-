using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Launcher
{
    /// <summary>
    /// 版本目录完整性清单（manifest.json）校验。
    /// 每个正式发布的版本目录由 pack_release.py 生成 manifest.json：
    /// { "version": "1.0.6", "files": [ { "path": "MoveImageForm.exe", "sha256": "...", "size": 12345 }, ... ] }
    /// 下载后逐文件校验（存在性 + 大小 + SHA256），全部通过才允许安装，杜绝半成品。
    /// </summary>
    public static class ManifestVerifier
    {
        public const string FileName = "manifest.json";

        public class ManifestEntry
        {
            public string Path = "";
            public string Sha256 = "";
            public long Size;
        }

        public class VerifyResult
        {
            public bool Success;
            public string Error = "";
            public string ManifestVersion = "";
            public int FileCount;

            public static VerifyResult Ok(string version, int count)
            {
                return new VerifyResult { Success = true, ManifestVersion = version, FileCount = count };
            }

            public static VerifyResult Fail(string error)
            {
                return new VerifyResult { Success = false, Error = error };
            }
        }

        /// <summary>目录中是否存在 manifest.json（老版本目录没有，用于兼容判断）</summary>
        public static bool HasManifest(string dir)
        {
            try { return File.Exists(Path.Combine(dir, FileName)); }
            catch { return false; }
        }

        /// <summary>
        /// 校验目录内容是否与 manifest.json 一致。
        /// manifest 缺失/损坏、任一文件缺失、大小或哈希不符 → 失败并给出原因。
        /// 目录中多出 manifest 之外的文件（如运行时生成的 Logs\）不影响结果。
        /// </summary>
        public static VerifyResult VerifyDirectory(string dir)
        {
            try
            {
                string manifestPath = Path.Combine(dir, FileName);
                if (!File.Exists(manifestPath))
                    return VerifyResult.Fail("缺少 manifest.json（非正式版本包）");

                List<ManifestEntry> entries;
                string manifestVersion;
                if (!TryParseManifest(manifestPath, out manifestVersion, out entries))
                    return VerifyResult.Fail("manifest.json 解析失败（文件损坏）");

                if (entries.Count == 0)
                    return VerifyResult.Fail("manifest.json 文件清单为空");

                foreach (var entry in entries)
                {
                    // 防路径穿越：清单内只允许相对路径
                    if (string.IsNullOrWhiteSpace(entry.Path) ||
                        entry.Path.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                        Path.IsPathRooted(entry.Path))
                        return VerifyResult.Fail($"manifest 含非法路径: {entry.Path}");

                    string fullPath = Path.Combine(dir, entry.Path.Replace('/', '\\'));
                    if (!File.Exists(fullPath))
                        return VerifyResult.Fail($"文件缺失: {entry.Path}");

                    var info = new FileInfo(fullPath);
                    if (info.Length != entry.Size)
                        return VerifyResult.Fail($"文件大小不符: {entry.Path}（期望 {entry.Size}，实际 {info.Length}）");

                    string actualHash = ComputeSha256(fullPath);
                    if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        return VerifyResult.Fail($"文件校验失败（SHA256 不符）: {entry.Path}");
                }

                return VerifyResult.Ok(manifestVersion, entries.Count);
            }
            catch (Exception ex)
            {
                return VerifyResult.Fail("校验过程异常: " + ex.Message);
            }
        }

        private static bool TryParseManifest(string manifestPath, out string version, out List<ManifestEntry> entries)
        {
            version = "";
            entries = new List<ManifestEntry>();
            try
            {
                string json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<Dictionary<string, object>>(json);
                if (data == null) return false;

                if (data.ContainsKey("version"))
                    version = data["version"]?.ToString()?.Trim() ?? "";

                if (!data.ContainsKey("files")) return false;
                var files = data["files"] as System.Collections.ArrayList;
                if (files == null) return false;

                foreach (var item in files)
                {
                    var f = item as Dictionary<string, object>;
                    if (f == null) continue;
                    var entry = new ManifestEntry();
                    if (f.ContainsKey("path")) entry.Path = f["path"]?.ToString() ?? "";
                    if (f.ContainsKey("sha256")) entry.Sha256 = f["sha256"]?.ToString() ?? "";
                    if (f.ContainsKey("size"))
                    {
                        long size;
                        long.TryParse(f["size"]?.ToString(), out size);
                        entry.Size = size;
                    }
                    entries.Add(entry);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
