using System;
using System.Collections.Generic;

namespace MoveImageForm.Services
{
    /// <summary>灰度判定结果（与 Launcher 侧实现保持一致）</summary>
    public enum GrayDecision
    {
        Stay,
        Upgrade
    }

    /// <summary>
    /// version.json 中 gray 块的配置模型（主程序侧，供运行中更新检查使用）。
    /// 字段与解析规则和 Launcher 侧完全一致，全部可选。
    /// </summary>
    public class GrayConfig
    {
        public string Version { get; set; } = "";
        public int Percent { get; set; } = 0;
        public List<string> Whitelist { get; set; } = new List<string>();
        public List<string> Blacklist { get; set; } = new List<string>();
        public bool RequireOptIn { get; set; } = true;
        public bool WhitelistOverridesOptIn { get; set; } = true;
        public string StartTime { get; set; } = "";

        public bool IsActive => !string.IsNullOrWhiteSpace(Version);

        public static GrayConfig FromVersionJson(Dictionary<string, object> versionJson)
        {
            var cfg = new GrayConfig();
            try
            {
                if (versionJson == null || !versionJson.ContainsKey("gray"))
                    return cfg;

                var gray = versionJson["gray"] as Dictionary<string, object>;
                if (gray == null)
                    return cfg;

                if (gray.ContainsKey("version"))
                    cfg.Version = gray["version"]?.ToString()?.Trim() ?? "";
                if (gray.ContainsKey("percent"))
                {
                    int pct;
                    if (int.TryParse(gray["percent"]?.ToString(), out pct))
                        cfg.Percent = Math.Max(0, Math.Min(100, pct));
                }
                if (gray.ContainsKey("requireOptIn"))
                    cfg.RequireOptIn = ParseBool(gray["requireOptIn"], true);
                if (gray.ContainsKey("whitelistOverridesOptIn"))
                    cfg.WhitelistOverridesOptIn = ParseBool(gray["whitelistOverridesOptIn"], true);
                if (gray.ContainsKey("startTime"))
                    cfg.StartTime = gray["startTime"]?.ToString() ?? "";
                cfg.Whitelist = ParseStringList(gray, "whitelist");
                cfg.Blacklist = ParseStringList(gray, "blacklist");
            }
            catch
            {
                return new GrayConfig();
            }
            return cfg;
        }

        private static bool ParseBool(object value, bool defaultValue)
        {
            if (value == null) return defaultValue;
            if (value is bool) return (bool)value;
            bool b;
            return bool.TryParse(value.ToString(), out b) ? b : defaultValue;
        }

        private static List<string> ParseStringList(Dictionary<string, object> gray, string key)
        {
            var list = new List<string>();
            if (!gray.ContainsKey(key)) return list;
            var arr = gray[key] as System.Collections.ArrayList;
            if (arr == null) return list;
            foreach (var item in arr)
            {
                string s = item?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }
    }

    /// <summary>
    /// 灰度判定（纯逻辑，无 IO；与 Launcher 侧规则保持一致）：
    /// 黑名单 &gt; 白名单(豁免 opt-in) &gt; requireOptIn 过滤 &gt; 百分比稳定分桶。
    /// </summary>
    public static class GrayPolicy
    {
        public static GrayDecision Decide(string machineId, bool optIn, GrayConfig gray)
        {
            if (gray == null || !gray.IsActive)
                return GrayDecision.Stay;
            if (string.IsNullOrWhiteSpace(machineId))
                return GrayDecision.Stay;

            if (ContainsId(gray.Blacklist, machineId))
                return GrayDecision.Stay;

            if (gray.WhitelistOverridesOptIn && ContainsId(gray.Whitelist, machineId))
                return GrayDecision.Upgrade;

            if (gray.RequireOptIn && !optIn)
                return GrayDecision.Stay;

            if (gray.Percent <= 0)
                return GrayDecision.Stay;
            if (gray.Percent >= 100)
                return GrayDecision.Upgrade;

            int bucket = (int)(StableHash(machineId) % 100);
            return bucket < gray.Percent ? GrayDecision.Upgrade : GrayDecision.Stay;
        }

        private static bool ContainsId(List<string> list, string machineId)
        {
            if (list == null) return false;
            foreach (var id in list)
            {
                if (string.Equals(id, machineId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>FNV-1a 32 位稳定哈希（必须与 Launcher 侧完全一致，保证同一机器判定一致）</summary>
        public static uint StableHash(string text)
        {
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= fnvPrime;
            }
            return hash;
        }
    }
}
