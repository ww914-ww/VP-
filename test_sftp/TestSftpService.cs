using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MoveImageForm.Services;

namespace TestSftpService
{
    /// <summary>
    /// 针对 SftpService 的测试：正常上传/跳过 + 半死连接模拟（挂起服务器进程）
    /// 验证 OperationTimeout=120s 超时兜底 → 主动断开 → 可重连
    /// 用法: TestSftpService.exe &lt;服务器PID&gt;
    /// </summary>
    class Program
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr h);

        static int passed = 0;
        static int failed = 0;

        static void Ok(string msg) { Console.WriteLine($"  [PASS] {msg}"); passed++; }
        static void Fail(string msg) { Console.WriteLine($"  [FAIL] {msg}"); failed++; }

        static void Main(string[] args)
        {
            string host = "127.0.0.1";
            int port = 2222;
            string user = "test_upload";
            string pwd = "test1234";
            string remoteRoot = "/aoi";
            string localFile = @"d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\test_sftp\local_source1\test_source1_1.txt";

            int serverPid = args.Length > 0 ? int.Parse(args[0]) : 0;

            Console.WriteLine("============================================================");
            Console.WriteLine("  TestSftpService: OperationTimeout 120s + 半死连接兜底");
            Console.WriteLine($"  服务器 PID: {(serverPid > 0 ? serverPid.ToString() : "(未提供，跳过半死测试)")}");
            Console.WriteLine("============================================================");

            // ====== 测试 1：正常连接 / 上传 / 跳过 / 增量判断 ======
            Console.WriteLine("\n--- 测试 1：正常功能回归 ---");
            using (var sftp = new SftpService(host, port, user, pwd, remoteRoot))
            {
                sftp.Connect();
                if (sftp.IsConnected) Ok("Connect 成功");
                else { Fail("Connect 失败"); return; }

                var r = sftp.UploadFile(localFile, "/op_timeout_test.txt");
                if (r.Success) Ok($"上传成功: {r.Detail}");
                else Fail($"上传失败: {r.Detail}");

                var r2 = sftp.UploadFile(localFile, "/op_timeout_test.txt", true);
                if (r2.Skipped) Ok($"appendOnly 跳过: {r2.Detail}");
                else Fail($"appendOnly 应跳过: {r2.Detail}");

                bool cur = sftp.IsTargetFileCurrent(localFile, "/op_timeout_test.txt");
                if (cur) Ok("IsTargetFileCurrent = TRUE（相同文件应跳过）");
                else Fail("IsTargetFileCurrent 应为 TRUE");

                bool cur2 = sftp.IsTargetFileCurrent(localFile, "/not_exist_op.txt");
                if (!cur2) Ok("IsTargetFileCurrent = FALSE（远端不存在应上传）");
                else Fail("IsTargetFileCurrent 应为 FALSE");
            }

            // ====== 测试 2：半死连接 → 120s 超时 → 主动断开 ======
            if (serverPid > 0)
            {
                Console.WriteLine("\n--- 测试 2：半死连接（挂起服务器进程）---");
                var sftp2 = new SftpService(host, port, user, pwd, remoteRoot);
                sftp2.Connect();
                if (!sftp2.IsConnected) { Fail("第二次 Connect 失败"); return; }
                Ok("第二次 Connect 成功");

                Console.WriteLine($"  挂起服务器进程 PID={serverPid}（模拟半死连接，TCP 不断但无响应）...");
                using (var p = Process.GetProcessById(serverPid))
                {
                    NtSuspendProcess(p.Handle);
                }

                var sw = Stopwatch.StartNew();
                bool result = sftp2.IsTargetFileCurrent(localFile, "/op_timeout_test.txt");
                sw.Stop();
                double secs = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"  IsTargetFileCurrent 返回 {result}，耗时 {secs:F1} 秒");

                if (secs >= 110 && secs <= 150)
                    Ok($"超时兜底生效：{secs:F1}s 后返回（期望 ≈120s）");
                else
                    Fail($"超时时间异常：{secs:F1}s（期望 ≈120s，实际 {result}）");

                if (!sftp2.IsConnected)
                    Ok("主动断开：IsConnected = false（触发上层重连逻辑）");
                else
                    Fail("连接类异常后应主动断开，IsConnected 应为 false");

                Console.WriteLine("  恢复服务器进程...");
                using (var p = Process.GetProcessById(serverPid))
                {
                    NtResumeProcess(p.Handle);
                }
                Thread.Sleep(1000);

                // ====== 测试 3：重连后恢复工作 ======
                Console.WriteLine("\n--- 测试 3：重连恢复 ---");
                sftp2.Connect();
                if (sftp2.IsConnected) Ok("重连成功");
                else Fail("重连失败");

                var r3 = sftp2.UploadFile(localFile, "/op_timeout_test.txt", true);
                if (r3.Success || r3.Skipped)
                    Ok($"重连后上传正常: {(r3.Skipped ? r3.Detail : "OK")}");
                else
                    Fail($"重连后上传失败: {r3.Detail}");

                // ====== 测试 4：增量记忆缓存（方案B）— 缓存命中不依赖网络 ======
                Console.WriteLine("\n--- 测试 4：增量记忆缓存（方案B）---");
                string cacheLocal = localFile.Replace("test_source1_1.txt", "cache_test.tmp");
                File.WriteAllText(cacheLocal, "cache test initial content");
                try
                {
                    var r4 = sftp2.UploadFile(cacheLocal, "/cache_test.txt", false);
                    if (r4.Success)
                    {
                        Ok("测试文件上传成功（缓存已记录）");

                        // 挂起服务器（模拟断网/半死）：缓存命中的判定不应依赖网络
                        using (var p = Process.GetProcessById(serverPid)) { NtSuspendProcess(p.Handle); }
                        var swc = Stopwatch.StartNew();
                        bool hit = sftp2.IsTargetFileCurrent(cacheLocal, "/cache_test.txt");
                        swc.Stop();
                        if (hit && swc.Elapsed.TotalSeconds < 5)
                            Ok($"挂起时缓存命中立即返回 true（{swc.Elapsed.TotalSeconds:F2}s，无网络依赖）");
                        else
                            Fail($"挂起时缓存命中应即时返回，实际 {swc.Elapsed.TotalSeconds:F2}s 结果={hit}");
                        using (var p = Process.GetProcessById(serverPid)) { NtResumeProcess(p.Handle); }
                        Thread.Sleep(1000);

                        // 修改本地文件 → 缓存失效 → 走远程比对（服务器已恢复）→ 返回 false（需上传）
                        File.AppendAllText(cacheLocal, "\nchanged content");
                        var swm = Stopwatch.StartNew();
                        bool miss = sftp2.IsTargetFileCurrent(cacheLocal, "/cache_test.txt");
                        swm.Stop();
                        if (!miss)
                            Ok($"文件变更后缓存失效→远程比对→false（需上传，{swm.Elapsed.TotalMilliseconds:F0} ms）");
                        else
                            Fail("文件变更后应返回 false（需上传）");

                        // 重新上传 → 缓存重新记录 → 再次命中
                        var r5 = sftp2.UploadFile(cacheLocal, "/cache_test.txt", false);
                        bool hit2 = r5.Success && sftp2.IsTargetFileCurrent(cacheLocal, "/cache_test.txt");
                        if (hit2) Ok("重新上传后缓存重新记录并命中");
                        else Fail($"重新上传后应缓存命中: 上传={r5.Success} 命中={hit2}");
                    }
                    else
                    {
                        Fail($"测试文件上传失败: {r4.Detail}");
                    }
                }
                finally
                {
                    try { File.Delete(cacheLocal); } catch { }
                }

                sftp2.Dispose();
            }
            else
            {
                Console.WriteLine("\n  [SKIP] 未提供服务器 PID，跳过半死连接测试");
            }

            // ====== 汇总 ======
            Console.WriteLine("\n============================================================");
            Console.WriteLine($"  结果: {passed}/{passed + failed} 通过, {failed} 失败");
            Console.WriteLine(passed + failed == 0 || failed == 0
                ? "  VERDICT: ALL TESTS PASSED"
                : $"  VERDICT: {failed} TEST(S) FAILED");
            Console.WriteLine("============================================================");
            Environment.Exit(failed > 0 ? 1 : 0);
        }
    }
}
