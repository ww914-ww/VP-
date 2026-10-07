using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace UpdateSystemTests
{
    /// <summary>
    /// 灰度更新体系测试入口（无头断言，进程退出码 = 失败用例数）。
    ///
    /// 参数：
    ///   --sftp              启用 HeartbeatService 真实 SFTP 测试（需先启动 test_sftp\standalone_sftp_server.py）
    ///   --launcher <path>   构建后的 Launcher.exe 路径（启用 --apply 端到端安装测试）
    ///   --fakemain <path>   FakeMainApp 产出的 MoveImageForm.exe 路径（启用 --apply 端到端安装测试）
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> _failures = new List<string>();
        private static string _tmpRoot;
        private static bool _enableSftp;
        private static string _launcherPath;
        private static string _fakeMainPath;

        private static int Main(string[] args)
        {
            _enableSftp = args.Contains("--sftp");
            _launcherPath = GetArgValue(args, "--launcher");
            _fakeMainPath = GetArgValue(args, "--fakemain");

            _tmpRoot = Path.Combine(Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location), "..", "..", "tmp");
            _tmpRoot = Path.GetFullPath(_tmpRoot);
            ResetDir(_tmpRoot);

            Section("A. GrayPolicy 灰度判定（Launcher 版）");
            TestGrayPolicyMatrix();
            TestGrayPolicyPriority();
            TestGrayPolicyOptIn();
            TestGrayPolicyBoundaries();
            TestGrayPolicyMonotonic();
            TestGrayPolicyConsistency();

            Section("B. MachineIdStore 机器标识");
            TestMachineIdStore();

            Section("C. ManifestVerifier 完整性校验");
            TestManifestVerifier();

            Section("D. UpdateCheckCore 更新检查决策（模拟服务器）");
            TestUpdateCheckCore();

            Section("E. UpdateCheckService 运行中检查（主程序侧）");
            TestUpdateCheckService();

            if (!string.IsNullOrEmpty(_launcherPath) && !string.IsNullOrEmpty(_fakeMainPath))
            {
                Section("F. UpdateInstaller --apply 端到端安装");
                TestApplySuccess();
                TestApplyFailureAndFuse();
            }
            else
            {
                Section("F. UpdateInstaller --apply 端到端安装（跳过：未提供 --launcher/--fakemain）");
            }

            if (_enableSftp)
            {
                Section("G. HeartbeatService 心跳上报（真实 SFTP 127.0.0.1:2222）");
                TestHeartbeat();
            }
            else
            {
                Section("G. HeartbeatService 心跳上报（跳过：未指定 --sftp）");
            }

            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine($"通过 {_passed}，失败 {_failed}");
            foreach (var f in _failures) Console.WriteLine("  失败: " + f);
            Console.WriteLine("========================================");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetFullPath(Path.Combine(_tmpRoot, "..")),
                    "test-results.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 通过 {_passed}，失败 {_failed}\r\n" +
                    string.Join("\r\n", _failures.Select(x => "失败: " + x)) + "\r\n");
            }
            catch { }
            return _failed;
        }

        private static string GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        // ==================== A. GrayPolicy ====================

        private static Launcher.GrayConfig Gray(string version = "1.0.6", int percent = 20,
            string[] whitelist = null, string[] blacklist = null,
            bool requireOptIn = true, bool whitelistOverrides = true)
        {
            return new Launcher.GrayConfig
            {
                Version = version,
                Percent = percent,
                Whitelist = whitelist?.ToList() ?? new List<string>(),
                Blacklist = blacklist?.ToList() ?? new List<string>(),
                RequireOptIn = requireOptIn,
                WhitelistOverridesOptIn = whitelistOverrides
            };
        }

        private static void TestGrayPolicyMatrix()
        {
            // 判定矩阵（方案书 2.2.4）：gray.version=1.0.6, percent=20, requireOptIn=true
            var gray = Gray(percent: 20, whitelist: new[] { "wl-1" }, blacklist: new[] { "bl-1" });

            Check("黑名单命中 → Stay（即便 opt-in）",
                Launcher.GrayPolicy.Decide("bl-1", true, gray) == Launcher.GrayDecision.Stay);
            Check("白名单命中（豁免开启）→ Upgrade（即便未 opt-in）",
                Launcher.GrayPolicy.Decide("wl-1", false, gray) == Launcher.GrayDecision.Upgrade);

            // 找两个确定分桶的 id 用于百分比行
            string hitId = FindIdWithBucketBelow(20);
            string missId = FindIdWithBucketAtLeast(20);
            Check("opt-in 且分桶<20 → Upgrade",
                Launcher.GrayPolicy.Decide(hitId, true, gray) == Launcher.GrayDecision.Upgrade);
            Check("opt-in 且分桶>=20 → Stay",
                Launcher.GrayPolicy.Decide(missId, true, gray) == Launcher.GrayDecision.Stay);
            Check("未 opt-in 且分桶<20 → Stay（requireOptIn 过滤）",
                Launcher.GrayPolicy.Decide(hitId, false, gray) == Launcher.GrayDecision.Stay);
        }

        private static void TestGrayPolicyPriority()
        {
            // 黑名单 > 白名单：同一 id 同时在两个名单 → Stay
            var gray = Gray(percent: 100, whitelist: new[] { "x" }, blacklist: new[] { "x" });
            Check("同 id 黑白名单同在 → 黑名单优先 Stay",
                Launcher.GrayPolicy.Decide("x", true, gray) == Launcher.GrayDecision.Stay);

            // whitelistOverridesOptIn=false：白名单不豁免，未 opt-in → Stay
            var gray2 = Gray(percent: 0, whitelist: new[] { "x" }, whitelistOverrides: false);
            Check("白名单豁免关闭 + 未 opt-in → Stay",
                Launcher.GrayPolicy.Decide("x", false, gray2) == Launcher.GrayDecision.Stay);
            Check("白名单豁免关闭 + 已 opt-in（percent=0）→ Stay",
                Launcher.GrayPolicy.Decide("x", true, gray2) == Launcher.GrayDecision.Stay);

            // 空 gray / 无版本号 / 空 machineId → Stay
            Check("gray=null → Stay",
                Launcher.GrayPolicy.Decide("x", true, null) == Launcher.GrayDecision.Stay);
            Check("gray.version 为空 → Stay",
                Launcher.GrayPolicy.Decide("x", true, Gray(version: "", percent: 100)) == Launcher.GrayDecision.Stay);
            Check("machineId 为空 → Stay",
                Launcher.GrayPolicy.Decide("", true, Gray(percent: 100)) == Launcher.GrayDecision.Stay);
        }

        private static void TestGrayPolicyOptIn()
        {
            // requireOptIn=false：未 opt-in 的机器也参与百分比灰度
            string hitId = FindIdWithBucketBelow(20);
            var gray = Gray(percent: 20, requireOptIn: false);
            Check("requireOptIn=false 时未 opt-in 分桶命中 → Upgrade",
                Launcher.GrayPolicy.Decide(hitId, false, gray) == Launcher.GrayDecision.Upgrade);

            // opt-in 状态不影响 latest 逻辑（Stay 与 optIn 无关，由 Decide 只决定是否命中 gray）
            var gray0 = Gray(percent: 0);
            Check("percent=0 已 opt-in → Stay（等待白名单或放量）",
                Launcher.GrayPolicy.Decide(hitId, true, gray0) == Launcher.GrayDecision.Stay);
        }

        private static void TestGrayPolicyBoundaries()
        {
            Check("percent=0 → 全部 Stay",
                Launcher.GrayPolicy.Decide(FindIdWithBucketBelow(1), true, Gray(percent: 0))
                    == Launcher.GrayDecision.Stay);
            Check("percent=100 且 opt-in → Upgrade",
                Launcher.GrayPolicy.Decide(FindIdWithBucketAtLeast(99), true, Gray(percent: 100))
                    == Launcher.GrayDecision.Upgrade);
            Check("percent=100 未 opt-in（requireOptIn）→ Stay",
                Launcher.GrayPolicy.Decide("any", false, Gray(percent: 100))
                    == Launcher.GrayDecision.Stay);
            Check("同一 machineId 分桶结果 100 次一致", StableBucketRepeatable());
        }

        private static bool StableBucketRepeatable()
        {
            var gray = Gray(percent: 37);
            string id = "stability-test-id";
            var first = Launcher.GrayPolicy.Decide(id, true, gray);
            for (int i = 0; i < 100; i++)
                if (Launcher.GrayPolicy.Decide(id, true, gray) != first) return false;
            return true;
        }

        private static void TestGrayPolicyMonotonic()
        {
            // 放量单调性：1000 个 id，percent 20→50→100，命中集合严格只增不减
            var ids = Enumerable.Range(0, 1000).Select(i => "machine-" + i).ToList();
            var hit20 = ids.Where(id => Launcher.GrayPolicy.Decide(id, true, Gray(percent: 20)) == Launcher.GrayDecision.Upgrade).ToList();
            var hit50 = ids.Where(id => Launcher.GrayPolicy.Decide(id, true, Gray(percent: 50)) == Launcher.GrayDecision.Upgrade).ToList();
            var hit100 = ids.Where(id => Launcher.GrayPolicy.Decide(id, true, Gray(percent: 100)) == Launcher.GrayDecision.Upgrade).ToList();

            Check("percent=20 命中率在 5%~35% 之间（FNV 分布 sanity）",
                hit20.Count > 50 && hit20.Count < 350);
            Check("20→50 命中集合严格扩大（只增不减）",
                hit20.All(hit50.Contains) && hit50.Count >= hit20.Count);
            Check("50→100 命中集合严格扩大",
                hit50.All(hit100.Contains) && hit100.Count == 1000);
        }

        private static void TestGrayPolicyConsistency()
        {
            // Launcher 与主程序两侧实现必须一致（分桶哈希一致，否则运行中检查与启动检查会打架）
            bool same = true;
            foreach (var id in Enumerable.Range(0, 200).Select(i => "consistency-" + i))
            {
                foreach (var pct in new[] { 0, 13, 50, 99, 100 })
                {
                    var g1 = Gray(percent: pct);
                    var g2 = new MoveImageForm.Services.GrayConfig
                    {
                        Version = g1.Version, Percent = g1.Percent,
                        RequireOptIn = g1.RequireOptIn,
                        WhitelistOverridesOptIn = g1.WhitelistOverridesOptIn
                    };
                    bool optIn = (id.Length % 2 == 0);
                    var d1 = Launcher.GrayPolicy.Decide(id, optIn, g1);
                    var d2 = MoveImageForm.Services.GrayPolicy.Decide(id, optIn, g2);
                    if ((d1 == Launcher.GrayDecision.Upgrade) != (d2 == MoveImageForm.Services.GrayDecision.Upgrade))
                    { same = false; break; }
                }
                if (!same) break;
            }
            Check("Launcher 与主程序 GrayPolicy 判定完全一致（200 id × 5 percent × optIn 两种）", same);
        }

        private static string FindIdWithBucketBelow(int percent)
        {
            for (int i = 0; ; i++)
            {
                string id = "bucket-search-" + i;
                if (Launcher.GrayPolicy.StableHash(id) % 100 < percent) return id;
            }
        }

        private static string FindIdWithBucketAtLeast(int percent)
        {
            for (int i = 0; ; i++)
            {
                string id = "bucket-search-hi-" + i;
                if (Launcher.GrayPolicy.StableHash(id) % 100 >= percent) return id;
            }
        }

        // ==================== B. MachineIdStore ====================

        private static void TestMachineIdStore()
        {
            string dir = NewSandbox("machineid");
            string id1 = Launcher.MachineIdStore.GetOrCreate(dir);
            Check("首次生成 GUID 格式合法", Guid.TryParse(id1, out _));
            string id2 = Launcher.MachineIdStore.GetOrCreate(dir);
            Check("二次读取与首次一致", id1 == id2);

            // 内容损坏 → 重建
            File.WriteAllText(Path.Combine(dir, "machine.id"), "not-a-guid");
            string id3 = Launcher.MachineIdStore.GetOrCreate(dir);
            Check("文件损坏后重建为合法 GUID", Guid.TryParse(id3, out _) && id3 != "not-a-guid");

            // 主程序侧读取同一文件结果一致
            string id4 = MoveImageForm.Services.MachineIdStore.GetOrCreate(dir);
            Check("主程序侧读取同一 machine.id 一致", id3 == id4);
        }

        // ==================== C. ManifestVerifier ====================

        private static void TestManifestVerifier()
        {
            string dir = NewSandbox("manifest");
            WriteText(dir, "a.txt", "hello");
            WriteText(dir, "sub\\b.txt", "world");
            WriteManifest(dir, "1.0.6");

            var ok = Launcher.ManifestVerifier.VerifyDirectory(dir);
            Check("完整目录校验通过", ok.Success);

            // 篡改文件内容（保持大小一致更能体现 SHA256 价值）
            WriteText(dir, "a.txt", "HELLO");
            var tampered = Launcher.ManifestVerifier.VerifyDirectory(dir);
            Check("篡改文件内容（同大小）→ SHA256 检出失败",
                !tampered.Success && tampered.Error.Contains("SHA256"));
            WriteText(dir, "a.txt", "hello");

            File.Delete(Path.Combine(dir, "sub", "b.txt"));
            var missing = Launcher.ManifestVerifier.VerifyDirectory(dir);
            Check("缺失文件 → 检出失败", !missing.Success && missing.Error.Contains("缺失"));
            WriteText(dir, "sub\\b.txt", "world");

            File.Delete(Path.Combine(dir, "manifest.json"));
            Check("无 manifest → HasManifest=false", !Launcher.ManifestVerifier.HasManifest(dir));
            var noManifest = Launcher.ManifestVerifier.VerifyDirectory(dir);
            Check("无 manifest → 校验失败（拒绝安装非正式包）", !noManifest.Success);

            // 路径穿越防护
            WriteManifest(dir, "1.0.6", extraEntry: "../evil.txt");
            var traversal = Launcher.ManifestVerifier.VerifyDirectory(dir);
            Check("manifest 含 ../ 路径穿越 → 拒绝", !traversal.Success && traversal.Error.Contains("非法路径"));
        }

        // ==================== D. UpdateCheckCore ====================

        private static void TestUpdateCheckCore()
        {
            // 场景1：无 gray，latest 更高 → Upgrade
            string app1 = NewSandbox("ucc1");
            MakeLocalVersion(app1, "1.0.5");
            string srv1 = NewSandbox("srv1");
            WriteVersionJson(srv1, latest: "1.0.6");
            var r1 = Launcher.UpdateCheckCore.Evaluate(srv1, "m-1", false, app1);
            Check("latest 更高 → Upgrade，target=latest",
                r1.Action == Launcher.UpdateCheckCore.UpdateAction.Upgrade &&
                r1.TargetVersion == "1.0.6" && r1.TargetSource == "latest");

            // 场景2：gray 白名单命中 → target=gray.version
            string srv2 = NewSandbox("srv2");
            WriteVersionJson(srv2, latest: "1.0.5",
                gray: "\"gray\":{\"version\":\"1.0.6\",\"percent\":20,\"whitelist\":[\"wl-9\"],\"blacklist\":[],\"requireOptIn\":true,\"whitelistOverridesOptIn\":true},");
            var r2 = Launcher.UpdateCheckCore.Evaluate(srv2, "wl-9", false, app1);
            Check("gray 白名单命中（豁免 opt-in）→ target=gray",
                r2.Action == Launcher.UpdateCheckCore.UpdateAction.Upgrade &&
                r2.TargetVersion == "1.0.6" && r2.TargetSource == "gray" && r2.PolicyPercent == 20);

            // 场景3：requireOptIn + 未 opt-in → 走 latest（1.0.5 == local）→ None
            string hitId = FindIdWithBucketBelow(20);
            var r3 = Launcher.UpdateCheckCore.Evaluate(srv2, hitId, false, app1);
            Check("未 opt-in → 不参与灰度，latest==local → None",
                r3.Action == Launcher.UpdateCheckCore.UpdateAction.None);

            // 场景4：同 id opt-in → Upgrade(gray)
            var r4 = Launcher.UpdateCheckCore.Evaluate(srv2, hitId, true, app1);
            Check("opt-in 分桶命中 → Upgrade(gray)",
                r4.Action == Launcher.UpdateCheckCore.UpdateAction.Upgrade && r4.TargetSource == "gray");

            // 场景5：熔断 → Fused
            File.WriteAllText(Path.Combine(app1, "update.failed"), "version=1.0.6\ncount=2\nlastError=x");
            var r5 = Launcher.UpdateCheckCore.Evaluate(srv2, hitId, true, app1);
            Check("同版本失败2次 → Fused", r5.Action == Launcher.UpdateCheckCore.UpdateAction.Fused);
            File.Delete(Path.Combine(app1, "update.failed"));

            // 场景6：dismissed → Dismissed
            Launcher.UpdateInstaller.WriteDismissedVersion(app1, "1.0.6");
            var r6 = Launcher.UpdateCheckCore.Evaluate(srv2, hitId, true, app1);
            Check("用户已跳过该版本 → Dismissed",
                r6.Action == Launcher.UpdateCheckCore.UpdateAction.Dismissed);
            File.Delete(Path.Combine(app1, "update.dismissed"));

            // 场景7：本地已是灰度版本，gray 块删除 → target=latest(1.0.5) < local(1.0.6) → Downgrade
            string app2 = NewSandbox("ucc2");
            MakeLocalVersion(app2, "1.0.6");
            string srv3 = NewSandbox("srv3");
            WriteVersionJson(srv3, latest: "1.0.5");
            var r7 = Launcher.UpdateCheckCore.Evaluate(srv3, hitId, true, app2);
            Check("gray 收敛后目标低于本地 → Downgrade",
                r7.Action == Launcher.UpdateCheckCore.UpdateAction.Downgrade && r7.TargetVersion == "1.0.5");

            // 场景8：半成品本地目录（有目录无 exe 的高版本）→ 回退到最近可用版本
            string app3 = NewSandbox("ucc3");
            MakeLocalVersion(app3, "1.0.5");
            Directory.CreateDirectory(Path.Combine(app3, "versions", "1.0.9")); // 半成品：无 exe
            var r8 = Launcher.UpdateCheckCore.Evaluate(srv1, "m-8", false, app3);
            Check("半成品高版本目录被排除，local 回退 1.0.5 → Upgrade 到 1.0.6",
                r8.LocalVersion == "1.0.5" && r8.Action == Launcher.UpdateCheckCore.UpdateAction.Upgrade);

            // 场景9：version.json 不存在 → Error
            var r9 = Launcher.UpdateCheckCore.Evaluate(NewSandbox("srv-empty"), "m-9", false, app1);
            Check("version.json 不存在 → Error（调用方直接启动本地版）",
                r9.Action == Launcher.UpdateCheckCore.UpdateAction.Error);

            // 场景10：serverPath 为空 → Error
            var r10 = Launcher.UpdateCheckCore.Evaluate("", "m-10", false, app1);
            Check("未配置更新服务器 → Error", r10.Action == Launcher.UpdateCheckCore.UpdateAction.Error);
        }

        // ==================== E. UpdateCheckService ====================

        private static void TestUpdateCheckService()
        {
            string appRoot = NewSandbox("ucs-app");
            string baseDir = NewSandbox("ucs-base");
            string srv = NewSandbox("ucs-srv");

            // local 版本 1.0.5（version.txt）
            File.WriteAllText(Path.Combine(baseDir, "version.txt"), "1.0.5");

            var config = new MoveImageForm.AppConfig { UpdateServerPath = srv, GrayOptIn = true };
            var logs = new List<string>();
            var svc = new MoveImageForm.Services.UpdateCheckService(() => config, appRoot, baseDir, logs.Add);

            // 1. latest 更高 → pending
            WriteVersionJson(srv, latest: "1.0.6");
            svc.Check();
            var st = MoveImageForm.Services.GrayState.Read(appRoot);
            Check("运行中检查发现新版本 → gray.state pending",
                st.Decision == "pending" && st.Target == "1.0.6" && st.Source == "latest");
            Check("首次发现写一条提示日志", logs.Any(l => l.Contains("1.0.6")));

            // 2. 本地已升级 → stay
            File.WriteAllText(Path.Combine(baseDir, "version.txt"), "1.0.6");
            svc.Check();
            st = MoveImageForm.Services.GrayState.Read(appRoot);
            Check("本地已是目标版本 → stay", st.Decision == "stay");

            // 3. latest 回退 → rollback
            WriteVersionJson(srv, latest: "1.0.5");
            svc.Check();
            st = MoveImageForm.Services.GrayState.Read(appRoot);
            Check("目标低于本地 → rollback", st.Decision == "rollback" && st.Target == "1.0.5");

            // 4. gray 命中（opt-in + percent=100）→ pending 且 source=gray
            File.WriteAllText(Path.Combine(baseDir, "version.txt"), "1.0.5");
            WriteVersionJson(srv, latest: "1.0.5",
                gray: "\"gray\":{\"version\":\"1.0.6\",\"percent\":100,\"whitelist\":[],\"blacklist\":[],\"requireOptIn\":true,\"whitelistOverridesOptIn\":true},");
            svc.Check();
            st = MoveImageForm.Services.GrayState.Read(appRoot);
            Check("opt-in 命中灰度 → pending(source=gray, percent=100)",
                st.Decision == "pending" && st.Source == "gray" && st.OptIn && st.PolicyPercent == 100);

            // 5. 熔断版本 → failed
            File.WriteAllText(Path.Combine(appRoot, "update.failed"), "version=1.0.6\ncount=2\nlastError=x");
            svc.Check();
            st = MoveImageForm.Services.GrayState.Read(appRoot);
            Check("目标版本已熔断 → failed", st.Decision == "failed");
        }

        // ==================== F. --apply 端到端 ====================

        private static void TestApplySuccess()
        {
            string appRoot = NewSandbox("apply-ok");
            // 旧版本 1.0.0（无 manifest 的老目录，兼容放行）
            MakeLocalVersion(appRoot, "1.0.0");
            // 新版本 9.9.1 下载到 .temp（FakeMainApp 可应答 --version）
            MakeTempVersion(appRoot, "9.9.1", probeVersion: "9.9.1");
            // Launcher.exe 放到 appRoot（--apply 以 exe 所在目录为 appRoot）
            File.Copy(_launcherPath, Path.Combine(appRoot, "Launcher.exe"), true);
            File.WriteAllText(Path.Combine(appRoot, "update.pending"), "9.9.1");

            int rc = RunProcess(Path.Combine(appRoot, "Launcher.exe"), "--apply \"9.9.1\"", appRoot, 60000);
            Check("--apply 退出码 0", rc == 0);
            Check("新版本目录就位 versions\\9.9.1",
                File.Exists(Path.Combine(appRoot, "versions", "9.9.1", "MoveImageForm.exe")));
            // apply 终态为 done；随后自动重启的 Launcher 会将其归位为 idle（两者均视为成功终态）
            string st1 = Launcher.UpdateInstaller.ReadStatus(appRoot);
            Check("update.status 到达成功终态（done 或被重启 Launcher 归位 idle）",
                st1 == "done" || st1 == "idle");
            Check("update.log 有完整步骤记录",
                File.ReadAllText(Path.Combine(appRoot, "update.log")).Contains("安装完成"));
            Check("无熔断残留", !File.Exists(Path.Combine(appRoot, "update.failed")));
            Check(".temp 已清空", !Directory.Exists(Path.Combine(appRoot, "versions", ".temp")));
            Check("旧版本 1.0.0 未受影响",
                File.Exists(Path.Combine(appRoot, "versions", "1.0.0", "MoveImageForm.exe")));

            CleanupSpawnedProcesses();
        }

        private static void TestApplyFailureAndFuse()
        {
            string appRoot = NewSandbox("apply-fail");
            MakeLocalVersion(appRoot, "1.0.0");
            // 探活版本号不符（version.txt 写 8.8.8，目标 9.9.2）→ 探活失败 → 回滚
            MakeTempVersion(appRoot, "9.9.2", probeVersion: "8.8.8");
            File.Copy(_launcherPath, Path.Combine(appRoot, "Launcher.exe"), true);

            int rc1 = RunProcess(Path.Combine(appRoot, "Launcher.exe"), "--apply \"9.9.2\"", appRoot, 60000);
            Check("探活失败 → --apply 退出码 1", rc1 == 1);
            Check("失败后半成品目录已回滚清理",
                !Directory.Exists(Path.Combine(appRoot, "versions", "9.9.2")));
            string stFail = Launcher.UpdateInstaller.ReadStatus(appRoot);
            Check("update.status 到达失败终态（failed 或被重启 Launcher 归位 idle）",
                stFail == "failed" || stFail == "idle");
            Check("旧版本仍可启动（回滚保护）",
                Launcher.VersionDirs.GetLocalLatestVersion(appRoot) == "1.0.0");
            Launcher.UpdateInstaller.ReadFailedInfo(appRoot, out string v1, out int c1, out _);
            Check("失败计数=1", v1 == "9.9.2" && c1 == 1);
            Check("未达2次 → 未熔断", !Launcher.UpdateInstaller.IsVersionFused(appRoot, "9.9.2"));

            // 第二次失败 → 熔断
            MakeTempVersion(appRoot, "9.9.2", probeVersion: "8.8.8");
            int rc2 = RunProcess(Path.Combine(appRoot, "Launcher.exe"), "--apply \"9.9.2\"", appRoot, 60000);
            Launcher.UpdateInstaller.ReadFailedInfo(appRoot, out _, out int c2, out _);
            Check("第二次失败退出码 1 且计数=2", rc2 == 1 && c2 == 2);
            Check("同版本失败2次 → 熔断", Launcher.UpdateInstaller.IsVersionFused(appRoot, "9.9.2"));

            // 熔断后启动检查跳过该版本
            string srv = NewSandbox("fuse-srv");
            WriteVersionJson(srv, latest: "9.9.2");
            var r = Launcher.UpdateCheckCore.Evaluate(srv, "m-fuse", false, appRoot);
            Check("熔断后 UpdateCheckCore → Fused（不再尝试）",
                r.Action == Launcher.UpdateCheckCore.UpdateAction.Fused);

            // 损坏 manifest 的下载应被拒绝（校验环节）
            string appRoot2 = NewSandbox("apply-badmanifest");
            MakeLocalVersion(appRoot2, "1.0.0");
            MakeTempVersion(appRoot2, "9.9.3", probeVersion: "9.9.3");
            // 篡改 .temp 中一个文件
            File.AppendAllText(Path.Combine(appRoot2, "versions", ".temp", "version.txt"), "tampered");
            File.Copy(_launcherPath, Path.Combine(appRoot2, "Launcher.exe"), true);
            int rc3 = RunProcess(Path.Combine(appRoot2, "Launcher.exe"), "--apply \"9.9.3\"", appRoot2, 60000);
            Check("manifest 校验不过 → 安装拒绝（退出码 1）", rc3 == 1);
            Check("manifest 校验不过 → versions\\9.9.3 未创建",
                !Directory.Exists(Path.Combine(appRoot2, "versions", "9.9.3")));

            CleanupSpawnedProcesses();
        }

        // ==================== G. HeartbeatService ====================

        private static void TestHeartbeat()
        {
            string appRoot = NewSandbox("hb-app");
            string baseDir = NewSandbox("hb-base");
            File.WriteAllText(Path.Combine(baseDir, "version.txt"), "1.0.6");
            // 模拟 Launcher 已判定升级灰度版本
            MoveImageForm.Services.GrayState.Write(appRoot, "upgrade", "1.0.6", "gray", true, 20);

            var config = new MoveImageForm.AppConfig
            {
                GrayOptIn = true,
                HeartbeatEnabled = true,
                HeartbeatIntervalMinutes = 5
            };
            config.SftpProfiles.Add(new MoveImageForm.Models.SftpProfile
            {
                Name = "测试上传",
                Role = "upload",
                TransportType = "SFTP",
                Host = "127.0.0.1",
                Port = 2222,
                Username = "test_upload",
                Password = "test1234",
                RemoteRoot = "/"
            });

            var session = new MoveImageForm.Services.SessionManager();
            var sftp = new MoveImageForm.Services.SftpService("127.0.0.1", 2222, "test_upload", "test1234", "/");
            sftp.Connect();
            session.RegisterSession(config.SftpProfiles[0], sftp, "test1234");

            var logs = new List<string>();
            var hb = new MoveImageForm.Services.HeartbeatService(session, () => config,
                appRoot, baseDir, logs.Add, TimeSpan.FromMilliseconds(1));
            hb.Beat();

            string machineId = MoveImageForm.Services.MachineIdStore.GetOrCreate(appRoot);
            // 测试 exe 位于 tests\UpdateSystemTests\bin\Release → 上溯 4 级到仓库根
            string remoteRoot = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "..", "..", "..", "..", "test_sftp", "remote"));
            string hbFile = Path.Combine(remoteRoot, "heartbeat", machineId + ".json");

            Check("heartbeat 文件已上传", File.Exists(hbFile));
            if (File.Exists(hbFile))
            {
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(hbFile));
                Check("machineId 正确", json.ContainsKey("machineId") && (string)json["machineId"] == machineId);
                Check("sftpUser=test_upload", json.ContainsKey("sftpUser") && (string)json["sftpUser"] == "test_upload");
                Check("version=1.0.6（version.txt 真相源）", json.ContainsKey("version") && (string)json["version"] == "1.0.6");
                Check("grayStatus=applied（decision=upgrade 且版本==target）",
                    json.ContainsKey("grayStatus") && (string)json["grayStatus"] == "applied");
                Check("optIn=true", json.ContainsKey("optIn") && (bool)json["optIn"]);
                Check("hostname 非空", json.ContainsKey("hostname") && !string.IsNullOrEmpty((string)json["hostname"]));
            }

            // 覆盖写：decision 改 stay 后再拍，文件被覆盖而非新增
            MoveImageForm.Services.GrayState.Write(appRoot, "stay", "1.0.6", "latest", true, 0);
            hb.Beat();
            if (File.Exists(hbFile))
            {
                var json2 = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(hbFile));
                Check("一机一文件覆盖写（grayStatus 更新为 stay）",
                    json2.ContainsKey("grayStatus") && (string)json2["grayStatus"] == "stay");
            }

            // readonly 账号 → 跳过不上报
            var configRo = new MoveImageForm.AppConfig { HeartbeatEnabled = true };
            configRo.SftpProfiles.Add(new MoveImageForm.Models.SftpProfile
            {
                Name = "只读", Role = "readonly", TransportType = "SFTP",
                Host = "127.0.0.1", Port = 2222, Username = "test_upload", RemoteRoot = "/"
            });
            var sessionRo = new MoveImageForm.Services.SessionManager();
            sessionRo.RegisterSession(configRo.SftpProfiles[0], sftp, "test1234");
            string appRootRo = NewSandbox("hb-ro");
            var hbRo = new MoveImageForm.Services.HeartbeatService(sessionRo, () => configRo,
                appRootRo, baseDir, logs.Add, TimeSpan.FromMilliseconds(1));
            string roId = MoveImageForm.Services.MachineIdStore.GetOrCreate(appRootRo);
            hbRo.Beat();
            Check("仅 readonly 账号 → 不上报（无心跳文件）",
                !File.Exists(Path.Combine(remoteRoot, "heartbeat", roId + ".json")));
            Check("readonly 跳过有本地日志",
                File.ReadAllText(Path.Combine(baseDir, "Logs", "heartbeat.log")).Contains("跳过"));

            session.Logout();
        }

        // ==================== 测试工具 ====================

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + name);
            }
            else
            {
                _failed++;
                _failures.Add(name);
                Console.WriteLine("  [FAIL] " + name);
            }
        }

        private static string NewSandbox(string name)
        {
            string dir = Path.Combine(_tmpRoot, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void ResetDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
        }

        private static void WriteText(string dir, string rel, string content)
        {
            string path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        /// <summary>生成与 pack_release.py 同构的 manifest.json</summary>
        private static void WriteManifest(string dir, string version, string extraEntry = null)
        {
            var files = new List<Dictionary<string, object>>();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f) == "manifest.json") continue;
                string rel = f.Substring(dir.Length).TrimStart('\\', '/').Replace("\\", "/");
                files.Add(new Dictionary<string, object>
                {
                    ["path"] = rel,
                    ["sha256"] = Sha256(f),
                    ["size"] = new FileInfo(f).Length
                });
            }
            if (extraEntry != null)
                files.Add(new Dictionary<string, object>
                { ["path"] = extraEntry, ["sha256"] = "00", ["size"] = 1 });

            string json = new JavaScriptSerializer().Serialize(
                new Dictionary<string, object> { ["version"] = version, ["files"] = files });
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>构造本地旧版本目录（老版本：无 manifest，兼容放行；exe 优先用 FakeMainApp 占位）</summary>
        private static void MakeLocalVersion(string appRoot, string version)
        {
            string dir = Path.Combine(appRoot, "versions", version);
            Directory.CreateDirectory(dir);
            string exePath = Path.Combine(dir, "MoveImageForm.exe");
            if (!string.IsNullOrEmpty(_fakeMainPath) && File.Exists(_fakeMainPath))
                File.Copy(_fakeMainPath, exePath, true);
            else
                File.WriteAllBytes(exePath, new byte[] { 1, 2, 3 });
        }

        /// <summary>构造 versions\.temp 下载目录（FakeMainApp + version.txt + manifest）</summary>
        private static void MakeTempVersion(string appRoot, string version, string probeVersion)
        {
            string temp = Path.Combine(appRoot, "versions", ".temp");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            File.Copy(_fakeMainPath, Path.Combine(temp, "MoveImageForm.exe"), true);
            File.WriteAllText(Path.Combine(temp, "version.txt"), probeVersion + "\n");
            WriteManifest(temp, version);
        }

        private static void WriteVersionJson(string serverDir, string latest, string gray = "")
        {
            string json = "{\n" + gray +
                "\"latest\":\"" + latest + "\",\n" +
                "\"versions\":{\"" + latest + "\":{\"date\":\"2026-10-04\",\"note\":\"测试版本\"}}\n}";
            File.WriteAllText(Path.Combine(serverDir, "version.json"), json);
        }

        private static int RunProcess(string exe, string args, string workDir, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }

        /// <summary>--apply 完成后会自动重启 Launcher（UI 模式）并可能拉起假主程序，测试后清理</summary>
        private static void CleanupSpawnedProcesses()
        {
            foreach (var name in new[] { "Launcher", "MoveImageForm" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); } catch { }
                }
            }
        }
    }
}
