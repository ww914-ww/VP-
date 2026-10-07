using System;
using System.IO;

namespace Launcher
{
    /// <summary>
    /// 机器标识存储：首次启动生成 GUID 落盘 machine.id，此后复用。
    /// 供灰度判定（分桶）与心跳上报（文件名）共同使用，保证同一机器标识稳定。
    /// </summary>
    public static class MachineIdStore
    {
        public const string FileName = "machine.id";

        /// <summary>读取已有标识；缺失或内容损坏时生成新 GUID 并落盘。任何 IO 失败都返回临时标识（不落盘），绝不抛异常</summary>
        public static string GetOrCreate(string appRoot)
        {
            string path = Path.Combine(appRoot, FileName);
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    Guid parsed;
                    if (Guid.TryParse(existing, out parsed))
                        return parsed.ToString();
                    // 内容损坏：按新机器重建（等效新机器，最多多观察一轮）
                }

                string created = Guid.NewGuid().ToString();
                // 先写临时文件再替换，避免中途断电留下半个文件
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, created);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return created;
            }
            catch
            {
                // 磁盘只读等极端情况：返回会话级临时标识，不影响启动主流程
                try
                {
                    if (File.Exists(path))
                    {
                        string fallback = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrEmpty(fallback)) return fallback;
                    }
                }
                catch { }
                return "unknown-" + Environment.MachineName;
            }
        }
    }
}
