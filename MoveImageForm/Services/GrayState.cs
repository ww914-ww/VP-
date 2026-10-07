using System;
using System.IO;
using System.Text;

namespace MoveImageForm.Services
{
    /// <summary>
    /// gray.state 判定轨迹文件（appRoot\gray.state）的读写模型。
    /// Launcher（启动检查）与主程序 UpdateCheckService（运行中检查）都会写入，
    /// HeartbeatService 读取上报；写入采用临时文件 + 替换，避免并发写造成半截文件。
    /// 格式与 Launcher 侧完全一致：
    ///   decision=upgrade|stay|rollback|pending|failed
    ///   target=1.0.6
    ///   source=gray|latest
    ///   optIn=true|false
    ///   time=2026-10-09T09:00:12
    ///   policyPercent=20
    /// </summary>
    public class GrayState
    {
        public string Decision = "stay";
        public string Target = "";
        public string Source = "latest";
        public bool OptIn;
        public string Time = "";
        public int PolicyPercent;

        public static string GetPath(string appRoot) => Path.Combine(appRoot, "gray.state");

        public static GrayState Read(string appRoot)
        {
            var state = new GrayState();
            try
            {
                string path = GetPath(appRoot);
                if (!File.Exists(path)) return state;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "decision": state.Decision = val; break;
                        case "target": state.Target = val; break;
                        case "source": state.Source = val; break;
                        case "optIn": state.OptIn = val == "true"; break;
                        case "time": state.Time = val; break;
                        case "policyPercent":
                            int pct;
                            if (int.TryParse(val, out pct)) state.PolicyPercent = pct;
                            break;
                    }
                }
            }
            catch { }
            return state;
        }

        public static void Write(string appRoot, string decision, string target, string source,
            bool optIn, int policyPercent)
        {
            try
            {
                string path = GetPath(appRoot);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp,
                    "decision=" + decision +
                    "\ntarget=" + target +
                    "\nsource=" + source +
                    "\noptIn=" + (optIn ? "true" : "false") +
                    "\ntime=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") +
                    "\npolicyPercent=" + policyPercent,
                    new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }
    }
}
