# poco运维工具 v2 设计文档

**目标：** 在现有文件搬运工具基础上，增加进程监听和自我更新功能，重构为「文件搬运 + 进程监听 + 版本更新」三合一运维工具。

**架构：** Launcher.exe（启动器）负责启动时检查更新、下载、替换、拉起主程序。MoveImageForm.exe（主程序）负责文件搬运、进程监听、定时/手动检查版本更新。配置统一存储在 config.xml。

**技术栈：** WPF .NET Framework 4.7.2, SMB 网络共享, XML 序列化配置

---

## 1. 系统架构

```
客户机 D:\App\
├── Launcher.exe              ← 用户入口（桌面快捷方式）
├── MoveImageForm.exe         ← 主程序（Launcher 拉起）
├── config.xml                ← 统一配置文件
├── update.status             ← 更新状态标记（自动生成）
├── update.bat                ← 文件替换批处理（自动生成）
├── launcher.log              ← 启动器日志
├── Logs\                     ← 主程序运行日志
├── backup\                   ← 旧版本备份
└── versions\
    ├── 1.0.0\                ← 当前版本（完整发布包）
    └── .temp\                ← 下载临时目录

云端 \\192.168.1.x\AppUpdate\
├── version.json              ← 版本元信息
└── 1.0.1\                    ← 新版本文件夹
    ├── MoveImageForm.exe
    ├── Launcher.exe
    └── ... (所有依赖)
```

**启动流程：**
1. 用户双击 Launcher.exe
2. Launcher 读 update.status → 恢复上次中断的更新
3. Launcher 读 config.xml → 挂载 SMB 共享
4. Launcher 读 version.json → 比对本机最高版本
5. 有新版本 → 弹窗提示 → 下载 → 生成 bat → 退出 → bat 替换 → 重启
6. 无新版本 → 直接拉起 versions\X.X.X\MoveImageForm.exe

**主程序启动后：**
- Tab 1 文件搬运：后台监控循环（已有，不变）
- Tab 2 进程监听：后台 5 秒轮询进程存活状态
- Tab 3 版本更新：后台定时检查（默认 30 分钟）+ 手动检查按钮

---

## 2. UI 设计

窗口标题改为「poco运维工具」，托盘图标文字改为「poco运维工具 (后台运行中)」。

使用 TabControl 三个 Tab：

### Tab 1: 文件搬运
现有功能不变。路径配置、搬运规则、操作按钮、运行日志。

### Tab 2: 进程监听

**布局：**
- 上方添加栏：进程名输入框 + 启动路径输入框 + 「添加」按钮
- 中间进程列表（DataGrid）：
  - 进程名列 | 启动路径列 | 运行状态列（●运行中/●未运行）| 监控开关列（CheckBox）| 操作列（删除）
- 下方说明文字：每 5 秒轮询，进程挂掉 → 托盘气泡提醒 + 日志

**行为：**
- 启动时自动加载 config.xml 中的 WatchProcesses 列表
- 自动开始后台轮询（每 5 秒用 Process.GetProcessesByName 检查）
- 进程不在运行中 → 托盘气泡通知 + 写入日志
- 添加进程：校验进程名和路径不为空 → 写入列表 → 保存 config
- 删除进程：从列表移除 → 保存 config
- 监控开关：可单独关闭某个进程的监控 → 保存 config
- 不自动重启，只提醒

### Tab 3: 版本更新

**布局：**
- 版本卡片区（水平排列）：
  - 本地版本号（大字体）+ 标签「当前本地版本」
  - 云端版本号（大字体）+ 标签「云端最新版本」
- 新版本提示横幅（绿色背景，仅在发现新版本时显示）
- 上次检查时间 + 检查间隔
- 按钮：「立即检查」
- 开关：「启用定时检查（每 N 分钟）」
- 开关：「开机自动启动」

**行为：**
- 「立即检查」→ 去云端读 version.json → 比对 → 有新版本弹窗 / 无新版本更新「上次检查时间」
- 弹窗：显示版本号、发布日期、更新说明 + 「立即更新」「稍后提醒」按钮
- 「立即更新」→ 下载到 versions\.temp\ → 生成 update.bat → 程序退出 → bat 替换重启
- 「启用定时检查」→ 背后启动 Timer，按配置间隔检查
- 「开机自动启动」→ 勾选写注册表 Run 键，取消删除

---

## 3. 配置文件设计

config.xml 位于应用程序根目录（D:\App\），Launcher 和主程序共用：

```xml
<?xml version="1.0" encoding="utf-8"?>
<Config>
  <!-- ===== 文件搬运（已有，不变） ===== -->
  <SourcePath></SourcePath>
  <DestPath></DestPath>
  <SourcePath2></SourcePath2>
  <DestPath2></DestPath2>
  <TransferMode>Cut</TransferMode>
  <EnableTimeRule>true</EnableTimeRule>
  <TimeIntervalSeconds>60</TimeIntervalSeconds>
  <EnableSizeRule>false</EnableSizeRule>
  <SizeLimitMB>100</SizeLimitMB>
  <EnableCountRule>false</EnableCountRule>
  <CountLimit>1000</CountLimit>
  <EnableEmptyFolderRule>false</EnableEmptyFolderRule>
  <EmptyFolderHours>24</EmptyFolderHours>

  <!-- ===== 版本更新（新增） ===== -->
  <CloudPath>\\192.168.1.100\AppUpdate</CloudPath>
  <CloudUser></CloudUser>
  <CloudPassword></CloudPassword>
  <CheckIntervalMinutes>30</CheckIntervalMinutes>
  <AutoUpdate>false</AutoUpdate>
  <AutoStart>false</AutoStart>

  <!-- ===== 进程监听（新增） ===== -->
  <WatchProcesses>
    <Process>
      <Name>VisionPlus.exe</Name>
      <Path>D:\6SVI\VisionPlus.exe</Path>
      <Enabled>true</Enabled>
    </Process>
  </WatchProcesses>
</Config>
```

**配置项说明：**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| CloudPath | 空 | SMB 共享路径 |
| CloudUser | 空 | SMB 用户名（空=无认证） |
| CloudPassword | 空 | SMB 密码 |
| CheckIntervalMinutes | 30 | 定时检查间隔（分钟） |
| AutoUpdate | false | 发现新版本是否自动下载 |
| AutoStart | false | 是否开机自启动 |
| WatchProcesses | 空列表 | 受监控进程列表 |

WatchProcesses 每个 Process 节点：
- `Name`: 进程名（如 VisionPlus.exe）
- `Path`: 启动路径（如 D:\6SVI\VisionPlus.exe，当前版本只存储不使用）
- `Enabled`: 是否启用监控（true/false）

---

## 4. Launcher 更新流程

与已有文档方案一致：

1. 读 update.status — 崩溃恢复（downloading→删.temp重下; ready→跳过下载; installing→检查完整性）
2. 挂载 SMB — net use + CloudPath/CloudUser/CloudPassword
3. 读 version.json — 获取 latest 版本号
4. 获取本地最高版本 — 遍历 versions\ 下文件夹名，取最大版本号
5. 比对 — 云端<=本地直接启动，云端>本地继续
6. 下载 — 状态→downloading，拷贝到 versions\.temp\，完成后检查 exe 存在，状态→ready
7. 弹窗 — 展示版本号、日期、说明 +「立即更新」「稍后」
8. 安装 — 状态→installing，生成 update.bat，退出
9. bat 执行 — 杀进程 → 旧版→backup → .temp→新版号 → 启动 Launcher → 自删

**异常处理：**
- 断电/崩溃：靠 update.status 状态文件恢复
- 网络故障：跳过检查，直接启动本地版本
- 磁盘不足：捕获异常提示用户，状态回 idle
- 新版损坏：bat 内置校验，自动从 backup 恢复

---

## 5. 进程监听流程

1. 主程序启动 → 从 config.xml 加载 WatchProcesses
2. 后台启动 Timer（5 秒间隔）
3. 每次 Tick：遍历 WatchProcesses 中 Enabled=true 的项
4. Process.GetProcessesByName(name) → 检查是否在运行
5. 不运行 → 托盘气泡通知 + 写日志
6. 运行 → 仅更新 UI 状态显示（绿色/红色圆点）
7. UI 操作（添加/删除/开关）→ 保存到 config.xml

---

## 6. 文件结构

```
MoveImageForm.sln
├── MoveImageForm/               ← 主程序项目
│   ├── MainWindow.xaml          ← 改为 3 个 Tab
│   ├── MainWindow.xaml.cs       ← 新增加进程监听 + 版本检查逻辑
│   ├── App.xaml.cs              ← 不变
│   ├── AppConfig.cs             ← 扩展配置模型（新增字段）
│   └── MoveImageForm.csproj     ← 不变
└── Launcher/                    ← 启动器项目（新建）
    ├── MainWindow.xaml           ← 简单窗口：状态文字 + 进度条 + 更新提示
    ├── MainWindow.xaml.cs        ← 更新流程逻辑
    ├── App.xaml / App.xaml.cs    ← 应用入口
    └── Launcher.csproj           ← 引用 System.Windows.Forms
```

---

## 7. 命名规则

- 窗口标题：`poco运维工具`
- 托盘文字：`poco运维工具 (后台运行中)`
- 程序集名称：`MoveImageForm.exe`（不变）, `Launcher.exe`（新增）
- 快捷方式名称：`poco运维工具`
