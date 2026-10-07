using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Launcher
{
    /// <summary>
    /// 更新安装器（Launcher --apply &lt;version&gt; 无界面安装模式）。
    /// 替代旧 update.bat：同步代码执行 备份 → 复制 → 校验 → 探活 →（失败）回滚，
    /// 每一步写 update.log，终态写 update.status（done / failed+原因），
    /// 同一版本失败计数写入 update.failed（≥2 次熔断，由启动检查跳过该版本）。
    /// </summary>
    public static class UpdateInstaller
    {
        // ==================== 共享状态文件读写（供 MainWindow 与 Installer 共同使用） ====================

        public static string StatusPath(string appRoot) => Path.Combine(appRoot, "update.status");
        public static string FailedPath(string appRoot) => Path.Combine(appRoot, "update.failed");
        public static string DismissedPath(string appRoot) => Path.Combine(appRoot, "update.dismissed");
        public static string GrayStatePath(string appRoot) => Path.Combine(appRoot, "gray.state");
        public static string LogPath(string appRoot) => Path.Combine(appRoot, "update.log");

        public static string ReadStatus(string appRoot)
        {
            try
            {
                if (File.Exists(StatusPath(appRoot)))
                    return File.ReadAllText(StatusPath(appRoot)).Trim();
            }
            catch { }
            return "idle";
        }

        public static void WriteStatus(string appRoot, string status)
        {
            try { AtomicWrite(StatusPath(appRoot), status); } catch { }
        }

        /// <summary>读取用户跳过的版本号（该版本不再弹更新提示，直到云端目标版本变化）</summary>
        public static string ReadDismissedVersion(string appRoot)
        {
            try
            {
                if (File.Exists(DismissedPath(appRoot)))
                    return File.ReadAllText(DismissedPath(appRoot)).Trim();
            }
            catch { }
            return "";
        }

        public static void WriteDismissedVersion(string appRoot, string version)
        {
            try { AtomicWrite(DismissedPath(appRoot), version ?? ""); } catch { }
        }

        /// <summary>读取熔断信息：返回 (版本号, 失败次数, 最近原因)。无熔断记录时 version 为空</summary>
        public static void ReadFailedInfo(string appRoot, out string version, out int count, out string lastError)
        {
            version = ""; count = 0; lastError = "";
            try
            {
                string path = FailedPath(appRoot);
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "version") version = val;
                    else if (key == "count") { int c; if (int.TryParse(val, out c)) count = c; }
                    else if (key == "lastError") lastError = val;
                }
            }
            catch { }
        }

        /// <summary>记录一次失败（计数 +1），返回累计次数。版本变化时计数重置</summary>
        public static int IncrementFailedCount(string appRoot, string version, string error)
        {
            string oldVer; int count; string oldErr;
            ReadFailedInfo(appRoot, out oldVer, out count, out oldErr);
            count = string.Equals(oldVer, version, StringComparison.OrdinalIgnoreCase) ? count + 1 : 1;
            try
            {
                AtomicWrite(FailedPath(appRoot),
                    "version=" + version + "\ncount=" + count + "\nlastError=" + (error ?? "").Replace("\n", " ").Replace("\r", " "));
            }
            catch { }
            return count;
        }

        /// <summary>清除熔断记录（安装成功或人工解除时调用）</summary>
        public static void ClearFailedInfo(string appRoot)
        {
            try { if (File.Exists(FailedPath(appRoot))) File.Delete(FailedPath(appRoot)); } catch { }
        }

        /// <summary>该版本是否已被熔断（同一版本失败 ≥2 次）</summary>
        public static bool IsVersionFused(string appRoot, string version)
        {
            string v; int c; string e;
            ReadFailedInfo(appRoot, out v, out c, out e);
            return c >= 2 && string.Equals(v, version, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>写 gray.state 判定轨迹（主程序心跳读取上报）</summary>
        public static void WriteGrayState(string appRoot, string decision, string target, string source, bool optIn, int policyPercent)
        {
            try
            {
                AtomicWrite(GrayStatePath(appRoot),
                    "decision=" + decision +
                    "\ntarget=" + target +
                    "\nsource=" + source +
                    "\noptIn=" + (optIn ? "true" : "false") +
                    "\ntime=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") +
                    "\npolicyPercent=" + policyPercent);
            }
            catch { }
        }

        public static void Log(string appRoot, string message)
        {
            try
            {
                File.AppendAllText(LogPath(appRoot),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }

        private static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ==================== --apply 安装主流程 ====================

        /// <summary>
        /// 执行安装：versions\.temp → versions\{version}。
        /// 返回进程退出码：0 成功，1 失败（已尽力回滚）。
        /// 无论成败都会尝试重启 Launcher（无参）回到正常启动流程。
        /// </summary>
        public static int Apply(string appRoot, string version)
        {
            bool success = false;
            string failReason = "";
            string versionsDir = Path.Combine(appRoot, "versions");
            string tempDir = Path.Combine(versionsDir, ".temp");
            string newVerDir = Path.Combine(versionsDir, version);
            string backupDir = Path.Combine(appRoot, "backup");
            string backupVerDir = Path.Combine(backupDir, version + ".bak");
            bool backedUp = false;

            WriteStatus(appRoot, "installing");
            Log(appRoot, "========== 开始安装版本 " + version + " ==========");

            try
            {
                // 1. 等待主程序退出（最多 30 秒；超时后强制结束残留进程，避免文件锁导致复制失败）
                WaitMainAppExit(appRoot, TimeSpan.FromSeconds(30));

                // 2. 安装前再次校验下载内容（.temp 必须带 manifest 且逐文件通过）
                var preCheck = ManifestVerifier.VerifyDirectory(tempDir);
                if (!preCheck.Success)
                    throw new InstallException("下载内容校验失败: " + preCheck.Error);
                Log(appRoot, "下载内容校验通过（" + preCheck.FileCount + " 个文件）");

                // 3. 备份现有同版本目录（降级安装时目标版本目录可能已存在）
                if (Directory.Exists(newVerDir))
                {
                    Directory.CreateDirectory(backupDir);
                    if (Directory.Exists(backupVerDir)) Directory.Delete(backupVerDir, true);
                    Directory.Move(newVerDir, backupVerDir);
                    backedUp = true;
                    Log(appRoot, "已备份现有 " + version + " 到 backup\\" + version + ".bak");
                }

                // 4. 移动新版本就位
                Directory.Move(tempDir, newVerDir);
                Log(appRoot, "新版本文件已就位: versions\\" + version);

                // 5. 就位后再校验一次（移动过程可能中断/被干扰）
                var postCheck = ManifestVerifier.VerifyDirectory(newVerDir);
                if (!postCheck.Success)
                    throw new InstallException("就位后校验失败: " + postCheck.Error);

                // 6. 探活：主程序 --version 探测，确认可启动且版本正确
                string probeError;
                if (!ProbeMainApp(appRoot, newVerDir, version, out probeError))
                    throw new InstallException("主程序探活失败: " + probeError);
                Log(appRoot, "主程序探活通过（版本 " + version + "）");

                success = true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                Log(appRoot, "安装失败: " + failReason);
            }

            if (!success)
            {
                // 回滚：移除半成品目录，恢复备份
                try
                {
                    if (Directory.Exists(newVerDir))
                    {
                        Directory.Delete(newVerDir, true);
                        Log(appRoot, "已清理半成品目录 versions\\" + version);
                    }
                    if (backedUp && Directory.Exists(backupVerDir))
                    {
                        Directory.Move(backupVerDir, newVerDir);
                        Log(appRoot, "已从备份恢复 versions\\" + version);
                    }
                }
                catch (Exception rbEx)
                {
                    Log(appRoot, "回滚过程异常（旧版本目录仍在，启动时会回退到最近可用版本）: " + rbEx.Message);
                }
            }

            // 终态与熔断计数
            if (success)
            {
                WriteStatus(appRoot, "done");
                ClearFailedInfo(appRoot);
                WriteDismissedVersion(appRoot, ""); // 已成功安装，清除跳过记录
                Log(appRoot, "========== 安装完成: " + version + " ==========");
            }
            else
            {
                WriteStatus(appRoot, "failed");
                int count = IncrementFailedCount(appRoot, version, failReason);
                Log(appRoot, "========== 安装失败（第 " + count + " 次）: " + version + " ==========" +
                    (count >= 2 ? "，该版本已熔断，人工删除 update.failed 前不再尝试" : ""));
            }

            // 回到正常启动流程（重启 Launcher 无参实例）
            try
            {
                string launcherPath = Path.Combine(appRoot, "Launcher.exe");
                if (File.Exists(launcherPath))
                {
                    Process.Start(new ProcessStartInfo(launcherPath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = appRoot
                    });
                }
            }
            catch (Exception ex)
            {
                Log(appRoot, "重启 Launcher 失败（请手动启动）: " + ex.Message);
            }

            return success ? 0 : 1;
        }

        private class InstallException : Exception
        {
            public InstallException(string message) : base(message) { }
        }

        /// <summary>等待主程序退出；超时后强制结束残留 MoveImageForm 进程</summary>
        private static void WaitMainAppExit(string appRoot, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var procs = Process.GetProcessesByName("MoveImageForm");
                if (procs.Length == 0) return;
                System.Threading.Thread.Sleep(500);
            }

            Log(appRoot, "主程序未在等待期内退出，强制结束残留进程");
            foreach (var proc in Process.GetProcessesByName("MoveImageForm"))
            {
                try { proc.Kill(); } catch { }
            }
            // 等待文件句柄释放
            System.Threading.Thread.Sleep(2000);
        }

        /// <summary>
        /// 探活：以 --version --probe-out 启动主程序（不写界面、不受单实例限制），
        /// 30 秒内退出且回报版本与目标一致 → 通过。
        /// </summary>
        private static bool ProbeMainApp(string appRoot, string verDir, string expectedVersion, out string error)
        {
            error = "";
            string exePath = Path.Combine(verDir, "MoveImageForm.exe");
            if (!File.Exists(exePath))
            {
                error = "主程序文件缺失";
                return false;
            }

            string probeOut = Path.Combine(Path.GetTempPath(), "vp_probe_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var psi = new ProcessStartInfo(exePath,
                    "--version --probe-out \"" + probeOut + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = verDir
                };
                var proc = Process.Start(psi);
                if (proc == null)
                {
                    error = "无法启动探测进程";
                    return false;
                }

                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch { }
                    error = "探测进程 30 秒未退出";
                    return false;
                }

                if (!File.Exists(probeOut))
                {
                    error = "探测进程未回报版本";
                    return false;
                }

                string reported = File.ReadAllText(probeOut).Trim();
                if (!string.Equals(reported, expectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    error = "版本不符（期望 " + expectedVersion + "，实际 " + reported + "）";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(probeOut)) File.Delete(probeOut); } catch { }
            }
        }
    }
}
