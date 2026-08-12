# VP运维工具 云端版本灰度更新 — 设计文档

日期：2026-08-12
状态：已确认（客户端改造 + 纯 SMB 服务器零代码）

## 1. 背景与目标

VP运维工具（MoveImageForm + Launcher）通过 SMB 共享从服务器读取 `version.json` 实现版本更新。当前发布方式是"全量"：`latest` 指向新版本后，所有客户一体机都会收到更新提示，无法控制发布范围、无法排除特定机器。

目标：把云端版本更新做成**灰度发布**，支持：
- 按百分比放量（客户端自判定）
- 白名单强制先更 / 黑名单紧急排除
- 灰度进度可见（心跳上报）
- 灰度期零风险回滚（未升级机器停发 + 已升级机器人工恢复）

## 2. 现状与约束

### 2.1 现有更新机制（Launcher/MainWindow.xaml.cs）

1. 启动时读根目录 `config.xml` 的 `UpdateServerPath`（SMB 共享路径）
2. 读 `\\server\AppUpdate\version.json`，取 `latest` 与本地版本比较
3. 云端更新 → 弹窗提示 → 用户确认 → SMB 复制到 `versions\.temp` → `update.bat` 替换并重启
4. 内置安全网：`backup\` 备份、VerifyCurrentVersion、RecoverFromBackup 崩溃恢复

### 2.2 关键约束

- **服务器是纯文件共享，零计算能力**——"该不该更新"的判定必须在客户端
- **`latest` 字段会被所有已部署的旧版 Launcher 读取**——灰度期间必须保持 `latest` 指向稳定版，新版本只通过 `gray.target` 暴露，否则旧客户端会全量升级
- **SMB 账号权限已回收，客户端对服务器只读**——任何需要写服务器的机制都不能走 SMB（影响面：仅心跳上报，已改走 SFTP/S3）
- 更新是用户手动确认的，不是强制推送
- 部署规模：20-100 台客户一体机
- 已部署旧版 Launcher 不认识 gray 字段 → 需向后兼容（新增字段自动忽略）

### 2.3 代码事实（影响实现位置）

- `Launcher.csproj` **不引用**任何 SFTP/S3 库，只读 SMB + 本地文件操作
- `MoveImageForm`（主程序）有完整传输能力：`Services/SftpService.cs`、`S3Service.cs`、`SmbService.cs`、`Models/SftpProfile.cs`、`Services/DpapiHelper.cs`
- Launcher 与主程序**共用同一个根目录 config.xml**：Launcher 直接读 `D:\App\config.xml`（XmlDocument），主程序从 `versions\<ver>\` 向上两级读同一文件（MoveImageForm/MainWindow.xaml.cs:1936，XmlSerializer AppConfig）

## 3. 方案选择

| 方案 | 说明 | 结论 |
|---|---|---|
| A：version.json 灰度字段 + 客户端机器ID判定 + SFTP/S3 心跳 | 服务器零代码，改文件即控制灰度 | **采用** |
| B：纯手工分批 | 非真正灰度，所有机器都会收到提示，不可控 | 拒绝 |
| C：服务器端轻量服务 | 控制力最强但需新增服务、端口、运维 | 拒绝（20-100 台规模不值得） |

## 4. 设计

### 4.1 version.json 扩展格式

```json
{
    "latest": "1.0.6",
    "versions": {
        "1.0.0": { "date": "2026-07-28", "note": "..." }
    },
    "gray": {
        "target": "1.0.7",
        "percent": 10,
        "allowlist": ["<机器ID>", "..."],
        "blocklist": ["<机器ID>"]
    }
}
```

- `gray` 字段**只在灰度期间存在**，全量发布后删除
- **灰度期间 `latest` 保持指向稳定版**（向后兼容关键，见 2.2）
- `gray.percent` 取值 0~100
- `gray.target` 对应版本文件夹必须存在且含 `MoveImageForm.exe`（复用现有完整性检查）

### 4.2 客户端判定逻辑（Launcher 改造）

**判定优先级：黑名单 > 白名单 > 百分比**（安全第一：紧急排除优先于强制进入）

```
解析 version.json → latest, gray
if gray 不存在 → 走现有逻辑（比较 latest），行为与现在完全一致
if 本地版本 >= gray.target → 正常启动（已升级，不再提示）
else if 机器ID ∈ blocklist → 静默启动当前版本
else if 机器ID ∈ allowlist → 弹窗提示升级到 gray.target
else if SHA256(机器ID + "|" + target)前4字节 % 100 < percent → 弹窗提示升级
else → 静默启动当前版本
```

- **确定性分桶**：同一台机器同一版本永远同一桶；不同版本带 target 参与哈希，**每次灰度选中不同机器集合**，避免"同一批小白鼠"
- 未中签机器**静默不提示**
- 判定完成后 Launcher 将结果写入根目录本地小文件 `gray.state`（与 `update.status` 同级，如 `taken 1.0.7` / `not_selected 1.0.7` / `blocked 1.0.7` / `stable`），供主程序心跳上报使用

**代码改造点**（Launcher/MainWindow.xaml.cs）：
- 提取 `GetTargetVersion(json)` 方法，统一现有三处各自解析 version.json 的逻辑（L149、L260、L336），CheckForUpdate / DownloadAndInstall / InstallUpdate 共用
- 新增机器ID读写（见 4.3）
- 新增灰度判定 + gray.state 写入（约 +100 行）

### 4.3 机器ID

- 根目录 `config.xml` 增加 `<MachineId>` 节点
- Launcher 首次运行检测不到则生成 GUID 写入（XmlDocument 读写），永久不变
- 主程序 AppConfig 类同步增加 `MachineId` 属性（XmlSerializer 未知元素自动跳过，旧配置兼容）
- 机器ID 是分桶和黑白名单的唯一锚点；生成失败时退化为**不进入灰度**（安全侧）

### 4.4 心跳上报（主程序 MoveImageForm 改造）

因 SMB 只读，心跳改走**已有 SFTP/S3 通道**，实现在主程序（传输能力的所在地）。

- **配置**：根 config.xml 增加可选 `<HeartbeatProfile>`（复用 SftpProfile 模型：类型 SFTP/S3、host、port、username、DPAPI 加密密码、远程根目录）+ `<HeartbeatIntervalHours>`（默认 24）
- **未配置 → 心跳自动禁用**，旧配置零影响
- **触发**：主程序启动成功后立即上报一次 + 后台 Timer 每 HeartbeatIntervalHours 一次（一体机 24×7 运行也能定期上报）
- **内容**：`{machineId, hostname, version, time, grayStatus}`，写入远程 `heartbeat\<机器ID>.json`（每台机器一个文件，覆盖写，无并发问题）
  - machineId 来自共享 config.xml；grayStatus 来自 Launcher 写的 gray.state；version 取运行目录版本号
- **实现**：复用 SftpService / S3Service（EnsureDirectoryExists + 上传），**失败静默、下轮重试**，绝不阻塞主程序
- **服务器端**：运维自己的 SFTP/S3 上建 `heartbeat\` 目录 + PowerShell 汇总脚本（统计各版本机器数、N 天未上报机器）

### 4.5 服务器端改动（零代码）

- 无需修改任何服务器程序/脚本
- 新增：SFTP/S3 上 heartbeat 目录、汇总脚本（放运维电脑）、发布说明文档更新

## 5. 发布与灰度操作流程

| 阶段 | 操作 |
|---|---|
| **Bootstrap**（必须） | 新版 Launcher（含灰度能力）先走一次**正常全量发布**（如 1.0.6，gray 字段不启用）→ 所有机器升级后才具备灰度能力。旧 Launcher 无法识别 gray 字段，此为必经步骤 |
| **启动灰度** | 上传 `1.0.7\` 版本文件夹 → version.json 加 `gray: {target:"1.0.7", percent:10}` → 中签机器启动时提示更新 |
| **推进** | 观察心跳确认无问题 → percent 10 → 50 → 100（此时全部机器都会收到提示） |
| **全量** | 确认 OK → `latest` 改 1.0.7 → 删除 `gray` 字段 → 未中签机器下次启动收到正常更新提示 |
| **回滚（灰度期发现问题）** | ① 未升级机器：删除 gray 字段或 percent=0，立即停发；② 已升级机器：心跳定位具体机器（灰度期最多 10%，2-10 台），按人工回滚手册恢复（`backup\` 自动备份 + 恢复步骤） |

## 6. 边界情况

- **已升级机器二次启动**：`localVer >= gray.target` 直接启动，不重复提示
- **percent 从 10 提到 50**：原中签机器保持中签（桶值 <10 ⊆ <50），已升级者不再判定，无抖动
- **机器ID 无法生成/无权限写 config.xml**：退化为不进入灰度（安全侧）
- **心跳失败**：静默忽略，下轮重试，不影响主程序
- **旧 Launcher 兼容**：gray 为新增字段，JavaScriptSerializer 反序列化 dynamic 自动忽略；config.xml 新增元素 XmlSerializer 自动跳过
- **版本文件夹不完整**：复用现有完整性检查（无 MoveImageForm.exe 报错不更新）
- **心跳期间无 SMB 写操作**：整个设计对 SMB 只读零依赖

## 7. 测试要点

- 分桶确定性：同机器ID+版本恒同结果；percent 边界 0 / 99 / 100
- 白名单强制进、黑名单强制退、黑名单优先于白名单
- 灰度期间 `latest` 指向稳定版时旧逻辑不被触发（模拟旧 Launcher 行为）
- 无 gray 字段时行为与现有版本逐字节一致（回归）
- 已升级机器二次启动不提示
- 心跳：写入成功、失败静默、间隔触发、未配置禁用
- 3 台机器模拟灰度全流程：1 台白名单、1 台中签（percent=33 精确控制）、1 台黑名单

## 8. 工作量评估

- Launcher：MainWindow.xaml.cs 重构 + 灰度判定 + 机器ID + gray.state ≈ 半天
- MoveImageForm：心跳上报（复用传输服务）≈ 半天
- 服务器：零代码，建目录 + 汇总脚本 + 文档 ≈ 2 小时
- 总工期约 1-2 天，风险点仅在客户端更新判定路径（无 gray 字段时完全走旧路径，回归风险可控）

## 9. 明确不做（YAGNI）

- 不做自动降级/回滚（灰度期已升级机器人工处理，心跳可定位）
- 不做未中签提示文案（静默）
- 不做服务器端服务（保持纯 SMB）
- 不做灰度策略服务端实时调整（改 version.json 已满足 20-100 台规模）
