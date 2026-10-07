# -*- coding: utf-8 -*-
"""生成《VP运维工具 灰度更新功能实施总结与使用说明》docx"""
import os
from docx import Document
from docx.shared import Pt, Cm, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml.ns import qn

doc = Document()
section = doc.sections[0]
section.top_margin = Cm(2); section.bottom_margin = Cm(2)
section.left_margin = Cm(2.5); section.right_margin = Cm(2.5)

style = doc.styles['Normal']
style.font.name = '微软雅黑'
style.element.rPr.rFonts.set(qn('w:eastAsia'), '微软雅黑')
style.font.size = Pt(10.5)
for level in range(1, 4):
    h = doc.styles['Heading %d' % level]
    h.font.name = '微软雅黑'
    h.element.rPr.rFonts.set(qn('w:eastAsia'), '微软雅黑')
    h.font.color.rgb = RGBColor(0x1A, 0x1A, 0x1A)

FONT = '微软雅黑'; CODE_FONT = 'Consolas'
GRAY = RGBColor(0x88, 0x88, 0x88); DARK = RGBColor(0x33, 0x33, 0x33)


def set_font(run, name=FONT, size=None, bold=False, color=None):
    run.font.name = name
    run._element.rPr.rFonts.set(qn('w:eastAsia'), name)
    if size: run.font.size = Pt(size)
    run.bold = bold
    if color: run.font.color.rgb = color


def p(text, bold=False, size=None, indent=0, align=None, gap=6, color=None):
    par = doc.add_paragraph()
    if align is not None: par.alignment = align
    par.paragraph_format.space_after = Pt(gap)
    par.paragraph_format.left_indent = Cm(indent)
    r = par.add_run(text)
    set_font(r, size=size, bold=bold, color=color)
    return par


def code(text):
    for line in text.strip().split('\n'):
        cp = doc.add_paragraph()
        cp.paragraph_format.space_after = Pt(0)
        cp.paragraph_format.space_before = Pt(0)
        cp.paragraph_format.left_indent = Cm(0.5)
        r = cp.add_run(line)
        set_font(r, name=CODE_FONT, size=9, color=DARK)
    doc.add_paragraph()


def bullet(text, indent=0.5):
    bp = doc.add_paragraph(style='List Bullet')
    bp.clear()
    r = bp.add_run(text)
    set_font(r, size=10)
    bp.paragraph_format.left_indent = Cm(indent)


def tbl(headers, rows):
    t = doc.add_table(rows=len(rows) + 1, cols=len(headers))
    t.style = 'Light Grid Accent 1'
    for i, h in enumerate(headers):
        cell = t.rows[0].cells[i]; cell.text = ''
        set_font(cell.paragraphs[0].add_run(h), size=9, bold=True)
    for ri, row in enumerate(rows):
        for ci, val in enumerate(row):
            cell = t.rows[ri + 1].cells[ci]; cell.text = ''
            set_font(cell.paragraphs[0].add_run(val), size=9)
    doc.add_paragraph()


def heading(text, level=1):
    doc.add_heading(text, level=level)


# ===== 封面 =====
for _ in range(6): doc.add_paragraph()
p('VP运维工具', bold=True, size=26, align=WD_ALIGN_PARAGRAPH.CENTER, gap=12)
p('灰度更新功能 实施总结与使用说明', bold=True, size=20, align=WD_ALIGN_PARAGRAPH.CENTER, gap=30)
p('对应方案：《VP运维工具_灰度更新项目规划方案书》V2.0', size=12, align=WD_ALIGN_PARAGRAPH.CENTER, gap=6, color=GRAY)
p('版本：Launcher 1.1.0 / MoveImageForm 1.1.0', size=12, align=WD_ALIGN_PARAGRAPH.CENTER, gap=6, color=GRAY)
p('编制日期：2026 年 10 月 4 日', size=12, align=WD_ALIGN_PARAGRAPH.CENTER, gap=6, color=GRAY)
doc.add_page_break()

# ===== 一、实施概览 =====
heading('一、实施概览', 1)
p('本次严格按照方案书 V2.0 完成全部五个阶段的功能实现，未添加方案书之外的任何功能，未做无关重构；原有接口、配置格式与操作行为保持不变（仅新增可选项）。')
tbl(['阶段', '方案书内容', '落地情况'], [
    ['阶段0', '更新链路可靠性修复', '已完成：版本真相源统一为 versions\\ 目录名 + 包内 version.txt；manifest.json（SHA256）逐文件校验；废弃 update.bat 改为 Launcher --apply 安装模式（全程 update.log）；失败熔断 update.failed（同版本≥2次）；dismissedVersion 提示去重；启动时必查（6 秒超时）；Launcher.new 自更新'],
    ['阶段1', '灰度判定核心库', '已完成：GrayPolicy（黑名单>白名单豁免>requireOptIn 过滤>FNV-1a 百分比分桶）、MachineIdStore、GrayConfig 模型，纯逻辑可单测'],
    ['阶段2', 'Launcher 集成', '已完成：解析 gray 块、读本地 GrayOptIn、写 gray.state、目标低于本地时自动降级回滚、更新面板区分灰度/正式文案、熔断版本提示暂停、无 gray 时行为与旧版完全一致'],
    ['阶段3', '主程序心跳与运行中检查', '已完成：HeartbeatService（SFTP 上传 heartbeat\\{machineId}.json）、UpdateCheckService（每 4 小时后台检查写 pending）、AppConfig 五字段扩展、账号管理 Tab"参与灰度测试"开关、主程序 --version 探活应答'],
    ['阶段4', 'SOP 与验证', '已完成：tools/pack_release.py 打包脚本（自动注入版本号+生成 manifest）、tests/ 测试工程与一键脚本、本文档'],
])

# ===== 二、新增文件 =====
heading('二、新增文件清单', 1)
tbl(['文件', '说明'], [
    ['Launcher\\GrayPolicy.cs', '灰度判定：GrayConfig 模型（version/percent/whitelist/blacklist/requireOptIn/whitelistOverridesOptIn/startTime 全可选）+ GrayPolicy.Decide(machineId, optIn, gray)，FNV-1a 稳定分桶'],
    ['Launcher\\MachineIdStore.cs', '机器标识：首次生成 GUID 落盘 machine.id，损坏自动重建，临时文件+替换写入'],
    ['Launcher\\ManifestVerifier.cs', 'manifest.json 解析与逐文件校验（存在性+大小+SHA256+防路径穿越）'],
    ['Launcher\\UpdateCheckCore.cs', '无 UI 依赖的检查决策核心：读 version.json→灰度判定→目标版本与动作（Upgrade/Downgrade/None/Fused/Dismissed/Error）；含 VersionDirs 本地版本目录扫描（半成品排除、老版本兼容）'],
    ['Launcher\\UpdateInstaller.cs', '--apply 安装器：等待主程序退出→下载内容校验→备份→就位→再校验→探活→失败回滚；update.log 逐步记录；update.status/dismissed/failed/gray.state 共享状态读写'],
    ['MoveImageForm\\Services\\GrayPolicy.cs', '主程序侧灰度判定（与 Launcher 侧规则与哈希完全一致）'],
    ['MoveImageForm\\Services\\MachineIdStore.cs', '主程序侧机器标识读取（同一 machine.id）'],
    ['MoveImageForm\\Services\\GrayState.cs', 'gray.state 判定轨迹文件读写（原子写）'],
    ['MoveImageForm\\Services\\VersionInfo.cs', '版本真相源：version.txt > versions\\ 目录名；appRoot 定位（向上两级且含 config.xml）'],
    ['MoveImageForm\\Services\\HeartbeatService.cs', 'SFTP 心跳上报：选可写 SFTP 账号→构造 JSON→上传 heartbeat\\{machineId}.json（覆盖写）；失败静默写 Logs\\heartbeat.log'],
    ['MoveImageForm\\Services\\UpdateCheckService.cs', '运行中更新检查：每 4 小时读 version.json 判定并写 gray.state（pending/rollback/stay/failed），首次发现记一条日志'],
    ['tools\\pack_release.py', '版本打包：复制构建产物+写 version.txt+可选 Launcher.new+生成 manifest.json+可选直传服务器'],
    ['tests\\UpdateSystemTests\\', '测试工程（链接生产源码编译）：判定矩阵/优先级/opt-in/边界/单调性/双端一致性/manifest/检查决策/运行中检查/--apply 端到端/心跳端到端'],
    ['tests\\FakeMainApp\\', '假主程序（产物名 MoveImageForm.exe）：应答 --version 探测，供 --apply 端到端测试'],
    ['tests\\run_tests.cmd', '一键构建+测试脚本'],
])

# ===== 三、改动文件 =====
heading('三、改动文件清单', 1)
tbl(['文件', '改动内容'], [
    ['Launcher\\App.xaml.cs', '新增 --apply <version> 命令行拦截：无界面执行 UpdateInstaller.Apply 后按退出码退出；正常启动路径不变'],
    ['Launcher\\MainWindow.xaml.cs', '重写更新主流程：删除"本地可用即跳过检查"（启动必查，6s 超时）；状态机扩展（done/failed 归位、ready 断点续装）；接入 UpdateCheckCore 决策；灰度/正式更新面板文案；降级自动执行；下载后 manifest 校验；废弃 update.bat 改启动 --apply；用户跳过写 dismissedVersion'],
    ['Launcher\\Launcher.csproj', '纳入 5 个新源码文件'],
    ['Launcher\\Properties\\AssemblyInfo.cs', '版本 1.0.0.0 → 1.1.0.0（仅标识，不参与更新比较）'],
    ['MoveImageForm\\AppConfig.cs', '新增 5 个可选配置字段：GrayOptIn(false)、HeartbeatEnabled(true)、HeartbeatIntervalMinutes(5)、HeartbeatProfileName("")、UpdateCheckIntervalHours(4)；旧 config.xml 无这些元素时取默认值，完全兼容'],
    ['MoveImageForm\\App.xaml.cs', '新增 --version --probe-out 探活应答（在单实例 mutex 之前处理，不弹窗不建窗口）'],
    ['MoveImageForm\\MainWindow.xaml', '账号管理 Tab 新增"参与灰度测试"复选框及说明文字（布局增加一行，其余不变）'],
    ['MoveImageForm\\MainWindow.xaml.cs', 'Window_Loaded：定位 appRoot、执行 Launcher.new 自更新、启动心跳与运行中检查两个服务；新增 ChkGrayOptIn_Changed（勾选即存 config）；UpdateUIFromConfig 回填开关；托盘完全退出时停止两个服务。其余逻辑零改动'],
    ['MoveImageForm\\MoveImageForm.csproj', '纳入 7 个新源码文件'],
    ['MoveImageForm\\Properties\\AssemblyInfo.cs', '版本 1.0.3.0 → 1.1.0.0（仅标识；更新比较不再使用程序集版本号）'],
])

# ===== 四、关键机制 =====
heading('四、关键机制说明', 1)

heading('4.1 更新主流程（修复后）', 2)
code(r'''
Launcher 启动
  → 恢复上次未完成状态（installing→校验/备份恢复；ready→断点续装；downloading→清临时目录）
  → 读 config.xml（UpdateServerPath、GrayOptIn）+ machine.id
  → UpdateCheckCore.Evaluate（6 秒超时）：
      version.json(latest+gray) → GrayPolicy 判定 → 目标版本
      → 更高：熔断?跳过?→ 显示面板（灰度/正式文案）
      → 更低：自动降级（灰度收敛/摘除/回滚）
      → 相等/熔断/已跳过/异常：直接启动本地版
  → 点「立即更新」：下载到 versions\.temp → manifest 逐文件校验
  → 启动 Launcher.exe --apply <版本> 并退出
      --apply：等主程序退出 → 再校验 → 备份旧版 → 就位 → 三重复核
        （目录存在 + manifest 通过 + 主程序 --version 探活回报一致）
      → 成功 done / 失败回滚+failed（计数+1，≥2 熔断）
  → 自动重启 Launcher → 进入新版本主程序
''')

heading('4.2 灰度判定与可选灰度', 2)
bullet('判定优先级：黑名单 > 白名单（默认豁免 opt-in）> requireOptIn 过滤 > 百分比 FNV-1a 稳定分桶；同一 machineId 永远同一桶，放量只增不减。')
bullet('本地 opt-in：账号管理 Tab"参与灰度测试"复选框（config.xml GrayOptIn，默认关）；云端 gray.requireOptIn=true（默认）时仅勾选机台参与百分比灰度；whitelistOverridesOptIn=true（默认）时白名单无视开关直接命中。')
bullet('opt-out 不自动降级（避免横跳）；需要回退时运维把 machineId 加入黑名单或删除 gray 块，Launcher 下次启动自动降级。')

heading('4.3 心跳上报', 2)
bullet('主程序启动 30 秒内首拍，之后每 HeartbeatIntervalMinutes（默认 5）分钟一拍；复用已登录的第一个可写 SFTP 账号（可用 HeartbeatProfileName 显式指定），不上报 readonly/SMB/S3 账号。')
bullet('内容：machineId / sftpUser / hostname / version / grayStatus(eligible|applied|stay|rollback|failed) / optIn / time；上传 heartbeat\\{machineId}.json 一机一文件覆盖写。')
bullet('失败静默：仅写 Logs\\heartbeat.log，不弹窗、不阻塞、不影响搬运；未登录账号时本周期跳过，重新登录后自动恢复。')

heading('4.4 运行中更新检查', 2)
bullet('主程序运行 10 分钟后首查，之后每 UpdateCheckIntervalHours（默认 4）小时一次；命中写 gray.state(pending)，日志区提示一条"将在下次启动时更新"；安装恒由 Launcher 下次启动执行。')

heading('4.5 Launcher 自更新', 2)
bullet('版本包含 Launcher.new 时，主程序启动早期（Launcher 已退出）备份 Launcher.exe 为 Launcher.old 后替换；失败自动还原、下次重试。')

# ===== 五、使用说明 =====
heading('五、使用说明', 1)

heading('5.1 构建', 2)
code(r'''
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    MoveImageForm.sln /t:Build /p:Configuration=Release /m
# 产物自动复制到 out-bin\（MoveImageForm.exe、Launcher.exe）
''')

heading('5.2 打包与发布（运维）', 2)
code(r'''
# 1. 打包（自动写 version.txt + 生成 manifest.json；需要更新 Launcher 时带 --launcher-new）
python tools\pack_release.py --version 1.0.6 --source out-bin --out release-out `
    --launcher-new out-bin\Launcher.exe

# 2. 直传服务器（可选，也可手工拷贝 release-out\1.0.6 整个目录）
python tools\pack_release.py --version 1.0.6 --source out-bin --out release-out `
    --server "\\172.16.22.138\Shared data\vp运维工具"

# 3. 人工编辑服务器 version.json（脚本不改策略）
''')
p('version.json 配置示例：')
code(r'''
// 灰度发布（仅自愿参与的机台，20%）
{ "latest": "1.0.5",
  "gray": { "version": "1.0.6", "percent": 20,
            "whitelist": ["<内测机 machineId>"],
            "blacklist": [],
            "requireOptIn": true, "whitelistOverridesOptIn": true },
  "versions": { "1.0.6": { "date": "2026-10-10", "note": "灰度说明" } } }

// 放量：percent 20 → 50 → 100；需覆盖未报名机台时把 requireOptIn 改 false
// 全量收敛：latest 改为 1.0.6 并删除 gray 块
// 紧急回滚：删除 gray 块（或 gray.version 改回旧版本号），已升级机台下次启动自动降级
// 解除单机熔断：删除该机应用根目录 update.failed 文件
''')

heading('5.3 客户端配置（config.xml 新字段，全部可选）', 2)
tbl(['字段', '默认', '说明'], [
    ['GrayOptIn', 'false', '本机是否参与灰度测试（界面开关即此值）'],
    ['HeartbeatEnabled', 'true', '是否启用 SFTP 心跳上报'],
    ['HeartbeatIntervalMinutes', '5', '心跳间隔（分钟）'],
    ['HeartbeatProfileName', '空', '显式指定心跳账号（Profile.Name）；为空自动选第一个可写 SFTP 账号'],
    ['UpdateCheckIntervalHours', '4', '运行中更新检查间隔（小时，1~72）'],
    ['UpdateServerPath', '（原有）', 'SMB 更新共享路径，心跳之外运行中检查也依赖它'],
])

heading('5.4 查看灰度进度（运维）', 2)
bullet('SFTP 服务器 heartbeat\\ 目录：每机一个 {machineId}.json，含版本、grayStatus、optIn、最后心跳时间；grayStatus=failed 即熔断机台需人工排查。')
bullet('机台本地：update.log（安装逐步日志）、gray.state（最近判定轨迹）、launcher_debug.log（启动诊断）、Logs\\heartbeat.log（心跳失败记录）。')

heading('5.5 运行测试', 2)
code(r'''
# 一键构建 + 全部测试（单测 + --apply 端到端 + 真实 SFTP 心跳端到端）
tests\run_tests.cmd
# 退出码=失败用例数；结果另存 tests\test-results.log
''')

# ===== 六、兼容性与影响分析 =====
heading('六、兼容性与原有功能影响分析', 1)
tbl(['项', '结论'], [
    ['旧 config.xml', '完全兼容：新字段缺省取默认值；XmlSerializer 忽略未知旧字段；首次保存自动补全新字段'],
    ['旧 version.json（无 gray 块）', 'GrayConfig 解析不到 gray 即不触发灰度，行为与旧版完全一致（回归基准）'],
    ['旧版 Launcher 混布', '旧 Launcher 不识别 gray 字段，继续按 latest 工作，不报错'],
    ['老版本目录（无 manifest）', '本地扫描兼容放行（exe 存在即可用）；服务器侧无 manifest 的版本目录不允许下载安装（防止装半成品，新版本均由 pack_release.py 产出）'],
    ['文件搬运业务', '零改动：MonitorLoop、SessionManager、Sftp/Smb/S3Service、角色权限、进程监听均未触碰；心跳与运行中检查为独立后台服务，失败静默'],
    ['开机自启/自动登录/自动搬运', '零改动：EnsureAutoStart、TryAutoLogin 行为保持 v1.0.3 语义'],
    ['程序集版本号', '仅作标识，不再参与任何更新比较（版本真相源=versions\\ 目录名+version.txt）'],
])

# ===== 七、测试覆盖 =====
heading('七、测试覆盖清单', 1)
tbl(['分组', '用例'], [
    ['A 灰度判定', '判定矩阵 5 行、黑白名单优先级、白名单豁免开/关、requireOptIn 开/关、percent 0/100 边界、分桶 100 次稳定、1000 机放量单调只增不减、Launcher 与主程序双端判定一致性（200 id × 5 档）'],
    ['B 机器标识', '首次生成、二次一致、损坏重建、双端读取一致'],
    ['C manifest 校验', '完整通过、篡改检出（SHA256）、缺失检出、无 manifest 拒绝、路径穿越拒绝'],
    ['D 检查决策', 'latest 升级、灰度白名单命中、未 opt-in 不参与、opt-in 命中、熔断 Fused、跳过 Dismissed、灰度收敛自动降级、半成品目录回退、无 version.json/未配置服务器容错'],
    ['E 运行中检查', 'pending/stay/rollback 状态写入、灰度 source 与 optIn 透传、熔断版本写 failed'],
    ['F --apply 端到端', '成功安装（目录就位/done/update.log/无熔断残留/旧版不受影响）、探活失败回滚+计数、二次失败熔断、熔断后检查跳过、manifest 损坏拒绝安装'],
    ['G 心跳端到端', '真实 SFTP 上传、字段完整性（machineId/sftpUser/version/grayStatus/optIn/hostname）、一机一文件覆盖写、readonly 账号跳过+本地日志'],
])

out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   'VP运维工具_灰度更新实施总结与使用说明.docx')
doc.save(out)
print('saved:', out)
