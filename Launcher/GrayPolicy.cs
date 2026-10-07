using System;
using System.Collections.Generic;

namespace Launcher
{
    /// <summary>灰度判定结果</summary>
    public enum GrayDecision
    {
        /// <summary>维持 latest 逻辑（不升级灰度版本）</summary>
        Stay,
        /// <summary>升级到 gray.version</summary>
        Upgrade
    }

    /// <summary>
    /// version.json 中 gray 块的配置模型。全部字段可选：
    /// gray 块缺失或字段缺失时构造出的对象不触发灰度（IsActive = false）。
    /// </summary>
    public class GrayConfig
    {
        public string Version { get; set; } = "";
        public int Percent { get; set; } = 0;
        public List<string> Whitelist { get; set; } = new List<string>();
        public List<string> Blacklist { get; set; } = new List<string>();

        /// <summary>true 时百分比灰度仅对本地 GrayOptIn=true 的机台生效（默认 true）</summary>
        public bool RequireOptIn { get; set; } = true;

        /// <summary>true 时白名单命中豁免 opt-in 直接升级（默认 true）</summary>
        public bool WhitelistOverridesOptIn { get; set; } = true;

        public string StartTime { get; set; } = "";

        /// <summary>是否构成一次有效的灰度发布（没有版本号等于没有灰度）</summary>
        public bool IsActive => !string.IsNullOrWhiteSpace(Version);

        /// <summary>从 JavaScriptSerializer 解析出的 dynamic 字典构造；任何异常都按"无灰度"处理</summary>
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
                // 解析失败按无灰度处理，绝不影响正常更新检查
                return new GrayConfig();
            }
            return cfg;
        }

        private static bool ParseBool(object value, bool defaultValue)
        {
            if (value == null) return defaultValue;
            bool b;
            if (value is bool) return (bool)value;
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
    /// 灰度判定（纯逻辑，无 IO，可单元测试）。
    /// 优先级固定：黑名单 &gt; 白名单(豁免 opt-in) &gt; requireOptIn 过滤 &gt; 百分比稳定分桶。
    /// </summary>
    public static class GrayPolicy
    {
        public static GrayDecision Decide(string machineId, bool optIn, GrayConfig gray)
        {
            if (gray == null || !gray.IsActive)
                return GrayDecision.Stay;
            if (string.IsNullOrWhiteSpace(machineId))
                return GrayDecision.Stay;

            // 1. 黑名单最高优先级：强制排除
            if (ContainsId(gray.Blacklist, machineId))
                return GrayDecision.Stay;

            // 2. 白名单命中且开启豁免：无视 opt-in 直接升级（保证内测机必中）
            if (gray.WhitelistOverridesOptIn && ContainsId(gray.Whitelist, machineId))
                return GrayDecision.Upgrade;

            // 3. 可选灰度：requireOptIn=true 时未报名的机台不参与后续百分比灰度
            //    （白名单未豁免的机器也落到此规则，与未报名机器同等对待）
            if (gray.RequireOptIn && !optIn)
                return GrayDecision.Stay;

            // 4. 百分比：稳定哈希分桶（同一 machineId 永远同一桶，放量只增不减）
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

        /// <summary>FNV-1a 32 位稳定哈希：内置可实现、跨机器/跨进程结果一致，不引入新依赖</summary>
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
