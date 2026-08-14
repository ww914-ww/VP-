# VP运维工具 云端版本灰度更新 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 VP运维工具的云端版本更新增加灰度发布能力：客户端按机器ID确定性分桶 + 白/黑名单判定，心跳经 SFTP/S3 上报灰度进度，服务器保持纯 SMB 零代码。

**Architecture:** 灰度策略写在 version.json 的 `gray` 字段中，客户端 Launcher 启动时自行判定"该不该升级"（黑名单 > 白名单 > SHA256 分桶百分比），判定结果写入本地 `gray.state`；主程序 MoveImageForm 复用现有 SftpService/S3Service 把本机版本信息心跳上报到远程 `heartbeat/` 目录。灰度期间 `latest` 保持指向稳定版，`gray.target` 只暴露给中签客户端，旧版 Launcher 天然不受影响。

**Tech Stack:** C# / .NET Framework 4.7.2（WPF：Launcher、MoveImageForm）、Renci.SshNet、Minio S3 SDK、JavaScriptSerializer、PowerShell 5.1+（测试脚本与运维脚本）。

**设计文档:** `docs/superpowers/specs/2026-08-12-gray-update-design.md`

---

## 文件结构

| 文件 | 操作 | 职责 |
|---|---|---|
| `Launcher/GrayPolicy.cs` | 新建 | 灰度判定纯逻辑（无 IO/UI 依赖，可单测） |
| `Launcher/MachineIdStore.cs` | 新建 | 机器ID 生成与 config.xml 持久化 |
| `Launcher/Launcher.csproj` | 修改 | 注册两个新 .cs 文件 |
| `Launcher/MainWindow.xaml.cs` | 修改 | 灰度判定集成、gray.state、版本解析重构 |
| `MoveImageForm/AppConfig.cs` | 修改 | 新增 MachineId / HeartbeatProfile / HeartbeatIntervalHours |
| `MoveImageForm/Services/HeartbeatService.cs` | 新建 | 心跳上报（复用传输服务） |
| `MoveImageForm/MainWindow.xaml.cs` | 修改 | 启动挂载心跳 |
| `tools/gray-policy-tests.ps1` | 新建 | 灰度逻辑单元测试（PowerShell Add-Type） |
| `tools/heartbeat-summary.ps1` | 新建 | 运维心跳汇总脚本 |
| `Deploy-Server/部署说明_服务器端.txt` | 修改 | 灰度发布/回滚操作说明 |
| `Deploy-Client/部署说明.txt` | 修改 | 心跳配置说明 |
| `D:\FakeServer\AppUpdate\` | 模拟目录（不入库） | 本地模拟更新服务器（Task 0，离线替代 SMB） |
| `D:\AppTest\`（及 -B、-C） | 模拟目录（不入库） | 模拟客户机安装目录（Task 0，三机演练用） |

> **无内网测试约束（已确认）：** 开发期无云端内网（SMB 服务器、SFTP/S3 均不可达）。经代码确认 Launcher 更新链路全部是纯文件 IO（`File.Exists` + `CopyDirectory`，见 MainWindow.xaml.cs L151/L262/L291），`UpdateServerPath` 填本地路径与 UNC 路径走同一条代码路径 → **用本地目录模拟服务器即可完整演练灰度流程**（Task 0）。心跳真实传输联调**推迟到上内网后**，开发期只验证逻辑（Task 4 Step 5）。

---

## Task 0: 离线测试环境搭建（无内网替代方案）

> 本任务只需一次，后续所有任务（Task 2 集成验证、Task 6 端到端演练）都使用这里的环境。
> 原理：Launcher 的更新链路全部是纯文件 IO（`File.Exists` 检查 version.json / 完整性、`CopyDirectory` 下载、`Path.Combine` 拼路径），代码里没有任何 UNC/SMB 专用 API。把 `UpdateServerPath` 指向本地目录 `D:\FakeServer\AppUpdate`，与真实 `\\server\share` 走完全相同的代码路径 —— 模拟服务器上改 version.json 即时生效，灰度推进/回滚演练反而比真实 SMB 更顺。
> 跳过项（用户已确认）：本机不部署 OpenSSH/MinIO，心跳真实传输联调推迟到上内网后（Task 4 Step 5 只验逻辑）。

**Files:**
- Create: `D:\FakeServer\AppUpdate\version.json`（模拟服务器，不入库）
- Create: `D:\FakeServer\AppUpdate\versions\1.0.5\`（模拟云端稳定版目录）
- Create: `D:\AppTest\`（模拟客户机，不入库）

- [ ] **Step 1: 编译当前代码（确保有 Release 产物）**

Run: `msbuild MoveImageForm\MoveImageForm.sln /p:Configuration=Release /v:m`
Expected: `Build succeeded`（若之前已编译过可跳过）。

- [ ] **Step 2: 搭建模拟更新服务器**

创建目录结构：

```
D:\FakeServer\AppUpdate\
├── version.json
└── versions\
    └── 1.0.5\
        ├── MoveImageForm.exe
        ├── MoveImageForm.exe.config
        ├── Renci.SshNet.dll
        ├── Microsoft.Bcl.AsyncInterfaces.dll
        ├── System.Runtime.CompilerServices.Unsafe.dll
        └── System.Threading.Tasks.Extensions.dll
```

1. 创建 `D:\FakeServer\AppUpdate\versions\1.0.5\`，把 `MoveImageForm\bin\Release\` 下全部文件复制进去（与真实 Deploy-Server 版本目录结构一致）
2. 创建 `D:\FakeServer\AppUpdate\version.json`，内容与真实服务器格式一致：

```json
{
    "latest": "1.0.5",
    "versions": {
        "1.0.5": { "date": "2026-08-01", "note": "离线模拟用稳定版" }
    }
}
```

- [ ] **Step 3: 搭建模拟客户机**

创建 `D:\AppTest\`（结构与真实客户机一致）：

```
D:\AppTest\
├── Launcher.exe
├── Launcher.exe.config
├── config.xml              ← ★ UpdateServerPath 指向本地模拟服务器
├── update.status           ← 空文件
└── versions\
    └── 1.0.5\
        ├── MoveImageForm.exe
        └── ...（同模拟服务器版本目录）
```

1. `Launcher.exe` / `Launcher.exe.config` 从 `Launcher\bin\Release\` 复制
2. `versions\1.0.5\` 直接从模拟服务器目录复制
3. `config.xml` 内容（心跳**先不配置**，HeartbeatProfile 留空 = 心跳禁用 = 主程序零网络依赖）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<Config>
  <UpdateServerPath>D:\FakeServer\AppUpdate</UpdateServerPath>
</Config>
```

- [ ] **Step 4: 验证环境可用**

Run: `D:\AppTest\Launcher.exe`
Expected: 状态栏显示"已是最新版本 (1.0.5)，正在启动..."，随后主程序 MoveImageForm 正常打开，无"无法连接云端"提示、无异常。

- [ ] **Step 5: 复制出三机演练用的 B、C 目录**

```bash
cp -r D:/AppTest D:/AppTest-B
cp -r D:/AppTest D:/AppTest-C
```

删除 `D:\AppTest-B\config.xml`、`D:\AppTest-C\config.xml` 中已有的 `<MachineId>` 节点（若 Task 2 完成前尚无此节点则跳过此步）—— 三台"机器"首次启动各自生成不同机器ID。

---

## Task 1: 灰度判定纯逻辑 + 机器ID（TDD）

**Files:**
- Create: `Launcher/GrayPolicy.cs`
- Create: `Launcher/MachineIdStore.cs`
- Modify: `Launcher/Launcher.csproj`（ItemGroup 中增加两个 Compile）
- Test: `tools/gray-policy-tests.ps1`

- [ ] **Step 1: 写失败测试脚本**

创建 `tools/gray-policy-tests.ps1`：

```powershell
# 灰度逻辑单元测试 — Windows PowerShell 5.1+，无需编译，直接运行
# 用法: powershell -ExecutionPolicy Bypass -File tools\gray-policy-tests.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Add-Type -Path (Join-Path $root "Launcher\GrayPolicy.cs")
Add-Type -Path (Join-Path $root "Launcher\MachineIdStore.cs")

$pass = 0; $fail = 0
function Assert-True($name, $cond) {
    if ($cond) { $pass++; Write-Host "PASS  $name" -ForegroundColor Green }
    else       { $fail++; Write-Host "FAIL  $name" -ForegroundColor Red }
}
function New-StringList([string[]]$items) {
    $list = New-Object System.Collections.Generic.List[string]
    if ($items) { $items | ForEach-Object { $list.Add($_) } }
    return $list
}

# ---- 1. 分桶确定性 ----
$b1 = [Launcher.GrayPolicy]::BucketValue("MACHINE-A", "1.0.7")
$b2 = [Launcher.GrayPolicy]::BucketValue("MACHINE-A", "1.0.7")
Assert-True "同机器同版本分桶值一致" ($b1 -eq $b2)
Assert-True "分桶值在 0..99 内" ($b1 -ge 0 -and $b1 -le 99)

# ---- 2. 黑白名单优先级（黑 > 白 > 百分比） ----
$g = [Launcher.GrayPolicy]::Decide("MACHINE-X", 100, (New-StringList @()), (New-StringList @("MACHINE-X")), "1.0.7")
Assert-True "黑名单优先于百分比(percent=100)" ($g -eq [Launcher.GrayDecision]::Blocked)
$g = [Launcher.GrayPolicy]::Decide("MACHINE-Y", 0, (New-StringList @("MACHINE-Y")), (New-StringList @()), "1.0.7")
Assert-True "白名单优先于百分比(percent=0)" ($g -eq [Launcher.GrayDecision]::Allowed)

# ---- 3. 百分比边界 ----
$g = [Launcher.GrayPolicy]::Decide("ANY-MACHINE", 0, (New-StringList @()), (New-StringList @()), "1.0.7")
Assert-True "percent=0 全部不中签" ($g -eq [Launcher.GrayDecision]::NotSelected)
$g = [Launcher.GrayPolicy]::Decide("ANY-MACHINE", 100, (New-StringList @()), (New-StringList @()), "1.0.7")
Assert-True "percent=100 全部中签" ($g -eq [Launcher.GrayDecision]::Selected)

# ---- 4. 版本比较 ----
Assert-True "1.0.7 比 1.0.6 新" ([Launcher.GrayPolicy]::IsNewer("1.0.7", "1.0.6"))
Assert-True "1.0.6 不比 1.0.7 新" (-not [Launcher.GrayPolicy]::IsNewer("1.0.6", "1.0.7"))

# ---- 5. 机器ID 持久化 ----
$tmpDir = Join-Path $env:TEMP ("graytest-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$tmp = Join-Path $tmpDir "config.xml"
@'
<Config><UpdateServerPath>\\192.168.1.100\AppUpdate</UpdateServerPath></Config>
'@ | Set-Content -Path $tmp -Encoding UTF8
$id1 = [Launcher.MachineIdStore]::GetOrCreate($tmp)
$id2 = [Launcher.MachineIdStore]::GetOrCreate($tmp)
Assert-True "首次生成非空机器ID" (-not [string]::IsNullOrWhiteSpace($id1))
Assert-True "二次读取返回同一机器ID" ($id1 -eq $id2)
$doc = [System.Xml.Linq.XDocument]::Load($tmp)
Assert-True "config.xml 已写入 MachineId 节点" ($doc.Root.Element("MachineId").Value -eq $id1)
Remove-Item -Recurse -Force $tmpDir

Write-Host ""
Write-Host "结果: $pass 通过, $fail 失败"
if ($fail -gt 0) { exit 1 } else { exit 0 }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `powershell -ExecutionPolicy Bypass -File tools\gray-policy-tests.ps1`
Expected: FAIL — Add-Type 报错 `Cannot find path ... GrayPolicy.cs`（文件不存在），脚本退出非 0。

- [ ] **Step 3: 实现 GrayPolicy.cs**

创建 `Launcher/GrayPolicy.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Launcher
{
    /// <summary>灰度判定结果</summary>
    public enum GrayDecision
    {
        Blocked,     // 黑名单 → 不升级
        Allowed,     // 白名单 → 升级
        Selected,    // 百分比命中 → 升级
        NotSelected  // 百分比未命中 → 不升级
    }

    /// <summary>
    /// 灰度发布纯逻辑 — 无 UI/IO 依赖，可用 PowerShell Add-Type 单测。
    /// 注意：本文件与 MachineIdStore.cs 需保持 C# 5 语法兼容（Add-Type 使用
    /// .NET Framework 的 CodeDom 编译器，不支持 ?. 等 C# 6 新语法）。
    /// 分桶确定性：同一机器ID + 同一目标版本恒得同一桶值；
    /// 目标版本参与哈希，每次灰度（不同版本）选中的机器集合不同。
    /// </summary>
    public static class GrayPolicy
    {
        /// <summary>确定性分桶值：0..99</summary>
        public static int BucketValue(string machineId, string targetVersion)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(machineId + "|" + targetVersion));
                return (int)(BitConverter.ToUInt32(hash, 0) % 100);
            }
        }

        /// <summary>判定本机是否应升级到 target。优先级：黑名单 &gt; 白名单 &gt; 百分比</summary>
        public static GrayDecision Decide(string machineId, int percent,
            List<string> allowlist, List<string> blocklist, string targetVersion)
        {
            if (blocklist != null && blocklist.Contains(machineId)) return GrayDecision.Blocked;
            if (allowlist != null && allowlist.Contains(machineId)) return GrayDecision.Allowed;
            return BucketValue(machineId, targetVersion) < percent
                ? GrayDecision.Selected : GrayDecision.NotSelected;
        }

        /// <summary>版本比较（与 Launcher 原 IsNewerVersion 语义一致）</summary>
        public static bool IsNewer(string cloudVer, string localVer)
        {
            try
            {
                var cv = new Version(cloudVer);
                var lv = new Version(localVer);
                return cv > lv;
            }
            catch
            {
                return string.Compare(cloudVer, localVer, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }
    }
}
```

- [ ] **Step 4: 实现 MachineIdStore.cs**

创建 `Launcher/MachineIdStore.cs`：

```csharp
using System;
using System.IO;
using System.Xml;

namespace Launcher
{
    /// <summary>
    /// 机器ID：持久化在根目录 config.xml 的 &lt;MachineId&gt; 节点。
    /// 首次调用生成 GUID 并写入；写失败返回空串（调用方按"不进入灰度"处理）。
    /// </summary>
    public static class MachineIdStore
    {
        private const string NodeName = "MachineId";

        public static string GetOrCreate(string configPath)
        {
            string existing = Read(configPath);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;

            string id = Guid.NewGuid().ToString("N").ToUpper();
            Write(configPath, id);
            return id;
        }

        public static string Read(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return "";
                var doc = new XmlDocument();
                doc.Load(configPath);
                XmlNode node = doc.SelectSingleNode("//" + NodeName);
                return node == null ? "" : node.InnerText.Trim();
            }
            catch { return ""; }
        }

        public static void Write(string configPath, string id)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                var doc = new XmlDocument();
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//" + NodeName);
                if (node == null)
                {
                    var root = doc.DocumentElement;
                    if (root == null) return;
                    node = doc.CreateElement(NodeName);
                    root.AppendChild(node);
                }
                node.InnerText = id;
                doc.Save(configPath);
            }
            catch { /* 写失败静默，调用方按空机器ID处理 */ }
        }
    }
}
```

- [ ] **Step 5: 注册到 Launcher.csproj**

在 `Launcher/Launcher.csproj` 的 `<ItemGroup>`（含 `<Compile Include="App.xaml.cs">` 的那组）中追加两行：

```xml
    <Compile Include="GrayPolicy.cs" />
    <Compile Include="MachineIdStore.cs" />
```

- [ ] **Step 6: 运行测试确认通过**

Run: `powershell -ExecutionPolicy Bypass -File tools\gray-policy-tests.ps1`
Expected: 全部 PASS，末尾输出 `结果: N 通过, 0 失败`，退出码 0。

- [ ] **Step 7: 确认项目可编译**

Run: `msbuild Launcher\Launcher.csproj /p:Configuration=Release /v:m`
（若无 msbuild 环境变量，用 VS 开发者命令行或 VS 内编译）
Expected: `Build succeeded`，`Launcher\bin\Release\Launcher.exe` 生成。

- [ ] **Step 8: 提交**

```bash
git add Launcher/GrayPolicy.cs Launcher/MachineIdStore.cs Launcher/Launcher.csproj tools/gray-policy-tests.ps1
git commit -m "feat: 灰度判定纯逻辑 + 机器ID存储，附带 PowerShell 单元测试"
```

---

## Task 2: Launcher 灰度判定集成

**Files:**
- Modify: `Launcher/MainWindow.xaml.cs`

- [ ] **Step 1: 替换 CheckForUpdate 方法**

把 `MainWindow.xaml.cs` 中 `CheckForUpdate()` 的整个方法体（约 L147-205，从 `private void CheckForUpdate()` 到对应右花括号）替换为：

```csharp
        private void CheckForUpdate()
        {
            try
            {
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (!File.Exists(versionFile))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到云端版本信息，直接启动...");
                    StartMainApp();
                    return;
                }

                string json = File.ReadAllText(versionFile);
                string machineId = MachineIdStore.GetOrCreate(_configPath);
                string target = GetTargetVersion(json, machineId, out bool hasGray);
                string localVer = GetLocalLatestVersion();

                if (string.IsNullOrEmpty(target))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = hasGray
                        ? $"灰度更新未开放给本机，正在启动当前版本 ({localVer})..."
                        : $"已是最新版本 ({localVer})，正在启动...");
                    StartMainApp();
                    return;
                }

                string cloudDate = GetVersionMeta(json, target, "date");
                string cloudNote = GetVersionMeta(json, target, "note");

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = $"发现新版本 {target}";
                    txtUpdateInfo.Text = $"版本: {target}\n日期: {cloudDate}\n\n{cloudNote}\n\n当前本地版本: {localVer}";
                    panelUpdate.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = "无法连接云端，直接启动...");
                StartMainApp();
            }
        }
```

- [ ] **Step 2: 新增灰度判定方法组**

在 `// ==================== 版本号 ====================` 分区之前插入以下方法（放在 CheckForUpdate 之后）：

```csharp
        // ==================== 灰度判定 ====================

        /// <summary>
        /// 计算本机应升级到的目标版本；无更新返回 ""。
        /// 无 gray 字段时行为与旧版完全一致（比较 latest）；
        /// 有 gray 字段时按灰度策略判定（黑名单 &gt; 白名单 &gt; 百分比）。
        /// 判定结果写入根目录 gray.state，供主程序心跳上报。
        /// </summary>
        private string GetTargetVersion(string json, string machineId, out bool hasGray)
        {
            var jss = new JavaScriptSerializer();
            var data = jss.Deserialize<dynamic>(json);
            string latest = data["latest"]?.ToString() ?? "";
            string localVer = GetLocalLatestVersion();

            hasGray = data["gray"] != null;
            if (!hasGray)
            {
                WriteGrayState("stable");
                return (latest.Length > 0 && IsNewerVersion(latest, localVer)) ? latest : "";
            }

            var gray = data["gray"] as System.Collections.Generic.Dictionary<string, object>;
            string target = gray != null && gray.ContainsKey("target") ? gray["target"]?.ToString() ?? "" : "";
            if (target.Length == 0) { WriteGrayState("stable"); return ""; }

            if (!IsNewerVersion(target, localVer)) { WriteGrayState("taken " + target); return ""; }

            int percent = 0;
            if (gray != null && gray.ContainsKey("percent"))
                int.TryParse(gray["percent"]?.ToString() ?? "0", out percent);

            var allow = ToStringList(gray, "allowlist");
            var block = ToStringList(gray, "blocklist");

            if (string.IsNullOrEmpty(machineId))
            {
                WriteGrayState("no_machine_id " + target);
                return "";   // 机器ID 缺失 → 不进入灰度（安全侧）
            }

            var decision = GrayPolicy.Decide(machineId, percent, allow, block, target);
            switch (decision)
            {
                case GrayDecision.Blocked:
                    WriteGrayState("blocked " + target);
                    return "";
                case GrayDecision.NotSelected:
                    WriteGrayState("not_selected " + target);
                    return "";
                default: // Allowed / Selected
                    WriteGrayState("selected " + target);
                    return target;
            }
        }

        private static List<string> ToStringList(System.Collections.Generic.Dictionary<string, object> obj, string key)
        {
            var list = new List<string>();
            if (obj != null && obj.ContainsKey(key))
            {
                var arr = obj[key] as object[];
                if (arr != null)
                    foreach (var item in arr)
                        list.Add(item?.ToString() ?? "");
            }
            return list;
        }

        /// <summary>读取版本元数据（date/note）</summary>
        private string GetVersionMeta(string json, string version, string key)
        {
            try
            {
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                var versions = data["versions"] as System.Collections.Generic.Dictionary<string, object>;
                if (versions != null && versions.ContainsKey(version))
                {
                    var verInfo = versions[version] as System.Collections.Generic.Dictionary<string, object>;
                    if (verInfo != null && verInfo.ContainsKey(key))
                        return verInfo[key]?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>把灰度状态写入根目录 gray.state（与 update.status 同级）</summary>
        private void WriteGrayState(string state)
        {
            try { File.WriteAllText(Path.Combine(_appRoot, "gray.state"), state); } catch { }
        }
```

- [ ] **Step 3: DownloadAndInstall 改用 GetTargetVersion**

把 `DownloadAndInstall()` 中这一段（约 L259-269，读 version.json 取 latest 的 if 块）：

```csharp
                // 获取最新版本号
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (File.Exists(versionFile))
                {
                    string json = File.ReadAllText(versionFile);
                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<dynamic>(json);
                    latestVersion = data["latest"]?.ToString() ?? "";
                }
```

替换为：

```csharp
                // 获取目标版本号（灰度判定与 CheckForUpdate 一致）
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (File.Exists(versionFile))
                {
                    string json = File.ReadAllText(versionFile);
                    latestVersion = GetTargetVersion(json, MachineIdStore.GetOrCreate(_configPath), out _);
                }
```

- [ ] **Step 4: InstallUpdate 改用 GetTargetVersion**

把 `InstallUpdate()` 中读 version.json 的对应 if 块（约 L335-343，结构同上）替换为同一段代码：

```csharp
                // 获取目标版本号（灰度判定与 CheckForUpdate 一致）
                string versionFile = Path.Combine(_updateServerPath, "version.json");
                if (File.Exists(versionFile))
                {
                    string json = File.ReadAllText(versionFile);
                    latestVersion = GetTargetVersion(json, MachineIdStore.GetOrCreate(_configPath), out _);
                }
```

- [ ] **Step 5: 编译验证**

Run: `msbuild Launcher\Launcher.csproj /p:Configuration=Release /v:m`
Expected: `Build succeeded`，无编译错误。

- [ ] **Step 6: 手工回归（无 gray 字段行为不变）**

1. 准备一个测试 SMB 共享（或本地目录 + UNC 路径），放入旧的 `version.json`（无 gray 字段）和一个 `versions\` 目录
2. 客户机 `config.xml` 的 `UpdateServerPath` 指向它，本地装一个旧版本
3. 启动 Launcher → 应弹出"发现新版本"面板（行为与改造前完全一致）
4. 点"跳过" → 正常启动主程序；确认根目录生成了 `gray.state`，内容为 `stable`
5. 点"更新" → 正常下载安装重启

- [ ] **Step 7: 提交**

```bash
git add Launcher/MainWindow.xaml.cs
git commit -m "feat: Launcher 集成灰度判定，三处版本解析统一走 GetTargetVersion，写入 gray.state"
```

---

## Task 3: MoveImageForm 配置模型扩展

**Files:**
- Modify: `MoveImageForm/AppConfig.cs`

- [ ] **Step 1: 新增配置属性**

在 `AppConfig.cs` 的 `// ===== 版本更新 =====` 分区之后（`ProcessCheckIntervalSeconds` 之前）插入：

```csharp
        // ===== 灰度更新 & 心跳上报 =====
        /// <summary>机器唯一ID（由 Launcher 生成写入，主程序只读）</summary>
        [XmlElement]
        public string MachineId { get; set; } = "";

        /// <summary>心跳上报账号（SFTP/S3，可选；未配置则禁用心跳）</summary>
        [XmlElement]
        public SftpProfile HeartbeatProfile { get; set; } = null;

        /// <summary>心跳上报间隔（小时），默认 24</summary>
        [XmlElement]
        public int HeartbeatIntervalHours { get; set; } = 24;
```

- [ ] **Step 2: 编译验证**

Run: `msbuild MoveImageForm\MoveImageForm.csproj /p:Configuration=Release /v:m`
Expected: `Build succeeded`。（旧 config.xml 无这三个元素时 XmlSerializer 自动跳过，加载行为不变。）

- [ ] **Step 3: 提交**

```bash
git add MoveImageForm/AppConfig.cs
git commit -m "feat: AppConfig 新增 MachineId / HeartbeatProfile / HeartbeatIntervalHours"
```

---

## Task 4: 心跳上报服务

**Files:**
- Create: `MoveImageForm/Services/HeartbeatService.cs`
- Modify: `MoveImageForm/MainWindow.xaml.cs`

- [ ] **Step 1: 新建 HeartbeatService.cs**

创建 `MoveImageForm/Services/HeartbeatService.cs`：

```csharp
using System;
using System.IO;
using MoveImageForm.Models;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 灰度心跳上报：把本机版本信息通过已配置的 SFTP/S3 账号上传到
    /// 远程 heartbeat/ 目录，供运维统计灰度进度。全部异常静默、下轮重试。
    /// 未配置 HeartbeatProfile 时自动禁用。
    /// </summary>
    public class HeartbeatService
    {
        private readonly AppConfig _config;
        private readonly Func<string> _versionProvider;

        public HeartbeatService(AppConfig config, Func<string> versionProvider)
        {
            _config = config;
            _versionProvider = versionProvider;
        }

        public bool IsEnabled => _config != null && _config.HeartbeatProfile != null
            && !string.IsNullOrWhiteSpace(_config.HeartbeatProfile.Host);

        public void SendOnce()
        {
            try
            {
                if (!IsEnabled) return;
                var profile = _config.HeartbeatProfile;

                string machineId = _config.MachineId;
                if (string.IsNullOrWhiteSpace(machineId)) machineId = Environment.MachineName;

                string json = "{\"machineId\":\"" + machineId
                    + "\",\"hostname\":\"" + Environment.MachineName
                    + "\",\"version\":\"" + (_versionProvider() ?? "")
                    + "\",\"time\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "\",\"grayStatus\":\"" + ReadGrayState() + "\"}";

                string tmpFile = Path.Combine(Path.GetTempPath(), "heartbeat_" + machineId + ".json");
                File.WriteAllText(tmpFile, json, new System.Text.UTF8Encoding(false));

                using (var transport = CreateTransport(profile))
                {
                    transport.Connect();
                    transport.EnsureDirectoryExists("heartbeat");
                    transport.UploadFile(tmpFile, "heartbeat/" + machineId + ".json");
                    transport.Disconnect();
                }

                try { File.Delete(tmpFile); } catch { }
            }
            catch { /* 心跳失败静默，下轮重试 */ }
        }

        /// <summary>读取 Launcher 写入的灰度状态（根目录 gray.state）</summary>
        private static string ReadGrayState()
        {
            try
            {
                string appRoot = AppDomain.CurrentDomain.BaseDirectory;
                string rootDir = Path.GetFullPath(Path.Combine(appRoot, "..", ".."));
                string stateFile = Path.Combine(rootDir, "gray.state");
                if (File.Exists(stateFile)) return File.ReadAllText(stateFile).Trim();
            }
            catch { }
            return "unknown";
        }

        private static IFileTransferService CreateTransport(SftpProfile profile)
        {
            string password = profile.GetPlainPassword();
            if (profile.IsS3)
                return new S3Service(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
            return new SftpService(profile.Host, profile.Port, profile.Username, password, profile.RemoteRoot);
        }
    }
}
```

- [ ] **Step 2: 注册到 MoveImageForm.csproj**

在 `MoveImageForm/MoveImageForm.csproj` 的 `<ItemGroup>` 中（`Services\SessionManager.cs` 那一行附近）追加：

```xml
    <Compile Include="Services\HeartbeatService.cs" />
```

- [ ] **Step 3: MainWindow 启动挂载**

在 `MoveImageForm/MainWindow.xaml.cs` 中：

1. 类字段区新增两行（在现有字段附近）：

```csharp
        private Services.HeartbeatService _heartbeatService;
        private System.Timers.Timer _heartbeatTimer;
```

2. `Window_Loaded` 中 `LoadConfig();`（约 L342）之后插入一行：

```csharp
            LoadConfig();
            StartHeartbeat();
```

3. 在类内新增两个方法（放在 Configuration Management 分区之前均可）：

```csharp
        // ==================== 灰度心跳 ====================

        private void StartHeartbeat()
        {
            try
            {
                _heartbeatService = new Services.HeartbeatService(_config, GetRunningVersion);
                _heartbeatService.SendOnce();

                int hours = Math.Max(1, _config.HeartbeatIntervalHours);
                _heartbeatTimer = new System.Timers.Timer(TimeSpan.FromHours(hours).TotalMilliseconds);
                _heartbeatTimer.AutoReset = true;
                _heartbeatTimer.Elapsed += (s, e) =>
                {
                    try { _heartbeatService.SendOnce(); } catch { }
                };
                _heartbeatTimer.Start();
            }
            catch { /* 心跳异常不影响主程序 */ }
        }

        /// <summary>当前运行版本：优先取版本目录名（如 1.0.5），取不到回退程序集版本</summary>
        private string GetRunningVersion()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string dirName = Path.GetFileName(baseDir);
                if (Version.TryParse(dirName, out _)) return dirName;
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            }
            catch { return ""; }
        }
```

- [ ] **Step 4: 编译验证**

Run: `msbuild MoveImageForm\MoveImageForm.csproj /p:Configuration=Release /v:m`
Expected: `Build succeeded`。若缺 `using System.Reflection;` 编译报错，在文件头部 using 区补上。

- [ ] **Step 5: 离线逻辑验证（真实传输联调推迟到上内网后）**

> 无内网期间跳过真实 SFTP/S3 联调（已确认，不部署本机 OpenSSH/MinIO）。此步骤只验证心跳的"安全逻辑"：禁用路径 + 失败静默 + grayStatus 读取。

1. **禁用路径**：Task 0 的 `D:\AppTest\config.xml` 不配置 HeartbeatProfile → 启动主程序 → 正常打开、无异常、无网络请求（IsEnabled=false）
2. **失败静默路径**：`D:\AppTest\config.xml` 的 `<Config>` 根下添加（指向本机必然不可达的地址；密码可填明文，程序自动兼容）：

```xml
  <HeartbeatProfile>
    <Name>心跳上报</Name>
    <TransportType>SFTP</TransportType>
    <Host>127.0.0.1</Host>
    <Port>22</Port>
    <Username>heartbeat-user</Username>
    <Password>明文密码</Password>
    <RemoteRoot>/</RemoteRoot>
  </HeartbeatProfile>
  <HeartbeatIntervalHours>24</HeartbeatIntervalHours>
```

3. 重启主程序 → 本机 22 端口无监听 → 连接必然失败 → 确认主程序**无任何异常、继续正常运行**（catch-all 静默验证；此路径同时验证 grayStatus 读取：Launcher 已写 gray.state 时取到实际值，否则 unknown）
4. 恢复 config.xml（删除 HeartbeatProfile）→ 回到禁用状态
5. 真实传输联调（上传 `heartbeat\<机器ID>.json` 到测试 SFTP/S3 + heartbeat-summary.ps1 观察）列入**上内网后验收清单**（见附）

- [ ] **Step 6: 提交**

```bash
git add MoveImageForm/Services/HeartbeatService.cs MoveImageForm/MoveImageForm.csproj MoveImageForm/MainWindow.xaml.cs
git commit -m "feat: 主程序心跳上报（SFTP/S3），启动即上报 + 周期上报，失败静默"
```

---

## Task 5: 服务器端（零代码 + 运维脚本 + 文档）

**Files:**
- Create: `tools/heartbeat-summary.ps1`
- Modify: `Deploy-Server/部署说明_服务器端.txt`
- Modify: `Deploy-Client/部署说明.txt`

- [ ] **Step 1: 新建运维汇总脚本**

创建 `tools/heartbeat-summary.ps1`：

```powershell
# 灰度心跳汇总 — 统计各版本机器数、未上报机器
# 用法: powershell -ExecutionPolicy Bypass -File tools\heartbeat-summary.ps1 -HeartbeatDir D:\heartbeat [-DaysInactive 3]
param(
    [Parameter(Mandatory = $true)][string]$HeartbeatDir,
    [int]$DaysInactive = 3
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $HeartbeatDir)) { Write-Host "目录不存在: $HeartbeatDir"; exit 1 }

$files = Get-ChildItem $HeartbeatDir -Filter *.json
Write-Host ("共 {0} 台机器上报" -f $files.Count)

$byVersion = @{}
$byGray = @{}
foreach ($f in $files) {
    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
    if (-not $byVersion.ContainsKey($j.version)) { $byVersion[$j.version] = 0 }
    $byVersion[$j.version]++
    $gs = $j.grayStatus.Split(" ")[0]
    if (-not $byGray.ContainsKey($gs)) { $byGray[$gs] = 0 }
    $byGray[$gs]++
}
Write-Host "--- 版本分布 ---"
$byVersion.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("  v{0}: {1} 台" -f $_.Key, $_.Value)
}
Write-Host "--- 灰度状态分布 ---"
$byGray.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0}: {1} 台" -f $_.Key, $_.Value)
}

$cutoff = (Get-Date).AddDays(-$DaysInactive)
$stale = @()
foreach ($f in $files) {
    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $t = [datetime]::ParseExact($j.time, "yyyy-MM-dd HH:mm:ss", $null)
    if ($t -lt $cutoff) { $stale += $j }
}
Write-Host ("--- 超过 {0} 天未上报: {1} 台 ---" -f $DaysInactive, $stale.Count)
$stale | ForEach-Object {
    Write-Host ("  {0} ({1}) 最后上报 {2} 版本 {3}" -f $_.machineId, $_.hostname, $_.time, $_.version)
}
```

- [ ] **Step 2: 更新服务器端部署说明**

在 `Deploy-Server/部署说明_服务器端.txt` 末尾追加：

```text
=========================================
五、灰度发布（可选）
=========================================

灰度发布让部分客户机先升级，验证无误后再全量。

1. 把新版本文件夹（如 1.0.7\）放到共享下（与普通发布相同）

2. version.json 增加 gray 字段，latest 保持指向当前稳定版：

   {
       "latest": "1.0.6",
       "versions": { ... },
       "gray": {
           "target": "1.0.7",
           "percent": 10,
           "allowlist": ["<机器ID>"],
           "blocklist": []
       }
   }

   - target：灰度目标版本（必须与版本文件夹名一致）
   - percent：0~100，未命中名单的机器中此百分比进入灰度
   - allowlist：白名单机器ID，强制先升级
   - blocklist：黑名单机器ID，强制不升级（优先级最高）

3. 查看机器ID：客户机根目录 config.xml 的 <MachineId> 节点

4. 推进灰度：确认无问题后逐步调大 percent（10 → 50 → 100）

5. 全量发布：确认无误后 latest 改为 target 并删除 gray 字段，
   未升级的机器下次启动会收到正常更新提示

6. 回滚：
   - 未升级机器：删除 gray 字段或 percent 改为 0 即可停发
   - 已升级机器：用心跳汇总定位清单（tools\heartbeat-summary.ps1），
     在客户机上从 versions\backup\ 恢复上一版本（见客户机部署说明"从旧版本升级"）
```

- [ ] **Step 3: 更新客户机部署说明**

在 `Deploy-Client/部署说明.txt` 末尾追加：

```text
=========================================
六、心跳上报（可选，灰度期间建议开启）
=========================================

主程序启动后会把自己的版本信息上报到运维的 SFTP/S3 服务器，
用于统计灰度进度。在 D:\App\config.xml 的 <Config> 根节点下增加：

   <MachineId>xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx</MachineId>
   <HeartbeatProfile>
       <Name>心跳上报</Name>
       <TransportType>SFTP</TransportType>
       <Host>运维SFTP服务器IP</Host>
       <Port>22</Port>
       <Username>心跳账号</Username>
       <Password>密码（可明文，程序读取时自动兼容）</Password>
       <RemoteRoot>/</RemoteRoot>
   </HeartbeatProfile>
   <HeartbeatIntervalHours>24</HeartbeatIntervalHours>

- MachineId 由 Launcher 自动生成，无需手工填写
- HeartbeatProfile 未配置时心跳自动禁用，不影响其他功能
- 上报文件：远程 heartbeat\<机器ID>.json，每次启动和每 24 小时覆盖写一次
```

- [ ] **Step 4: 提交**

```bash
git add tools/heartbeat-summary.ps1 "Deploy-Server/部署说明_服务器端.txt" "Deploy-Client/部署说明.txt"
git commit -m "docs: 灰度发布/回滚操作说明 + 心跳配置说明 + 运维汇总脚本"
```

---

## Task 6: 端到端演练与发布打包

**Files:**
- 无代码改动（纯验证与打包）

- [ ] **Step 1: 单元测试全绿**

Run: `powershell -ExecutionPolicy Bypass -File tools\gray-policy-tests.ps1`
Expected: 全部 PASS，`结果: N 通过, 0 失败`。

- [ ] **Step 2: 三机灰度模拟（离线环境，Task 0 已搭建）**

使用 Task 0 的 `D:\AppTest\`、`D:\AppTest-B\`、`D:\AppTest-C\` 三个模拟客户机目录 + 模拟服务器 `D:\FakeServer\AppUpdate\`：

1. 各目录先启动一次 Launcher（让机器ID 生成），读取各目录 `config.xml` 的 `<MachineId>`，记为 A、B、C
2. 把**测试版本**（如 1.0.6）的版本文件夹放入模拟服务器 `D:\FakeServer\AppUpdate\versions\1.0.6\`（从 `MoveImageForm\bin\Release\` 复制）
3. 模拟服务器 `version.json` 配置灰度：机器A 加入 `allowlist`，机器C 加入 `blocklist`，percent 33：

```json
{
    "latest": "1.0.5",
    "versions": {
        "1.0.5": { "date": "2026-08-01", "note": "离线模拟用稳定版" },
        "1.0.6": { "date": "2026-08-14", "note": "灰度测试版" }
    },
    "gray": {
        "target": "1.0.6",
        "percent": 33,
        "allowlist": ["<机器A的ID>"],
        "blocklist": ["<机器C的ID>"]
    }
}
```

4. 依次启动三个 Launcher：
   - A、B 启动 → 弹"发现新版本 1.0.6"面板（A 因白名单、B 因 percent 中签或补进 allowlist）
   - C 启动 → 无面板，状态显示"灰度更新未开放给本机"，根目录生成 `gray.state` 内容 `blocked 1.0.6`
   - 已升级的机器再次启动 → 不再提示，`gray.state` 为 `taken 1.0.6`
5. 注意验证 `latest` 仍为 1.0.5 时旧逻辑不被触发（灰度未中签机器全部静默）

- [ ] **Step 3: 灰度推进与全量**

1. percent 提到 100 → 剩余机器启动时均提示
2. `latest` 改为 1.0.6、删除 gray 字段 → 全部机器启动时走普通更新提示，`gray.state` 变回 `stable`
3. **离线替代心跳观察**：全程检查各客户机根目录 `gray.state` 内容与版本目录变化（灰度进度 = 各目录 `versions\` 下实际版本）；真实心跳进度观察（heartbeat-summary.ps1）列入上内网后验收

- [ ] **Step 4: 回滚演练（在模拟服务器 `D:\FakeServer\AppUpdate\version.json` 上操作）**

1. 灰度期发现问题：删除 gray 字段（或 percent=0）→ 未升级机器不再提示（确认 C 等机器静默启动）
2. 人工回滚已升级机器：删除 `D:\AppTest\versions\<灰度版本>\`，从 `D:\AppTest\versions\backup\<版本>.bak` 恢复（按现有 update.bat 备份结构），启动确认回到旧版

- [ ] **Step 5: Bootstrap 发布打包**

按现有发布流程（Deploy-Server 目录即更新服务器内容）：
1. 编译 Release 两个项目
2. 新建 `Deploy-Server\versions\1.0.6\`，放入 `Launcher.exe`、`MoveImageForm.exe` 及全部 DLL（从 `Launcher\bin\Release\` 与 `MoveImageForm\bin\Release\` 复制，与既有版本目录结构一致）
3. `version.json` 增加 1.0.6 条目、`latest` 改 1.0.6（**不加 gray 字段**——这就是 Bootstrap 全量发布）
4. 打包产物提交后，推送到真实服务器 + 确认全部机器升级到 1.0.6 且功能正常（含心跳默认禁用）为**上内网后验收项**（见附）
5. 之后 1.0.7 起即可按 Task 5 文档执行灰度发布

- [ ] **Step 6: 提交打包产物**

```bash
git add Deploy-Server/versions/1.0.6/ Deploy-Server/version.json
git commit -m "release: 1.0.6 Bootstrap 全量发布（含灰度能力，灰度未启用）"
```

---

## 附：验收标准对照

| 设计文档要求 | 验收点 | 对应任务 |
|---|---|---|
| 灰度期间 latest 保持稳定版 | 灰度时旧 Launcher 不触发更新、新 Launcher 只对中签机器提示 | Task 2（GetTargetVersion） |
| 黑名单 > 白名单 > 百分比 | 单元测试用例 | Task 1 |
| 确定性分桶、不同版本重分桶 | 单元测试用例 | Task 1 |
| 机器ID 持久化 | 单元测试用例 | Task 1 |
| gray.state 供心跳使用 | 集成后主程序心跳含 grayStatus | Task 2 / Task 4 |
| 心跳经 SFTP/S3、失败静默、未配置禁用 | 手工验证清单 | Task 4 |
| 服务器零代码 | 服务器端仅文档+脚本 | Task 5 |
| 向后兼容（无 gray 字段行为不变） | 手工回归清单 | Task 2 Step 6 |
| 已升级机器不重复提示 | 三机演练 | Task 6 |
| 回滚（停发+人工恢复） | 回滚演练 | Task 6 |
| 无内网环境完整演练 | 本地目录模拟 SMB 服务器（纯文件 IO 依据）跑通灰度/推进/全量/回滚 | Task 0 / Task 6 |

## 附：上内网后验收清单（无内网期间推迟项）

| 项目 | 验证内容 |
|---|---|
| 心跳真实传输 | 配置 HeartbeatProfile 指向真实测试 SFTP/S3 → 远程出现 `heartbeat\<机器ID>.json`，内容含 hostname/version/grayStatus |
| 心跳周期上报 | 24h 后再次上报（或临时调小 HeartbeatIntervalHours 验证 Timer 触发） |
| heartbeat-summary.ps1 | 对真实心跳目录跑汇总脚本，版本/灰度状态/未上报统计正确 |
| 真实 SMB 更新 | UpdateServerPath 指向真实共享，走一次普通更新（SMB 只读账号环境） |
| 灰度上线演练 | 在真实环境按 Task 5 文档执行一次灰度发布 → 推进 → 全量（或回滚） |
