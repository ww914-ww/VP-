using System;
using System.IO;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 机器标识存储（主程序侧）：读取 Launcher 生成的 appRoot\machine.id；
    /// 缺失时自行生成（与 Launcher 侧规则一致：GUID 落盘、损坏重建）。
    /// </summary>
    public static class MachineIdStore
    {
        public const string FileName = "machine.id";

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
                }

                string created = Guid.NewGuid().ToString();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, created);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return created;
            }
            catch
            {
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
