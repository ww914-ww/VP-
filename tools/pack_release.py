# -*- coding: utf-8 -*-
"""pack_release.py — VP运维工具版本打包脚本（灰度更新体系配套）

用法：
  python tools/pack_release.py --version 1.0.6 --source out-bin --out release-out
  python tools/pack_release.py --version 1.0.6 --source out-bin --out release-out \
      --launcher-new out-bin/Launcher.exe --server "\\\\172.16.22.138\\Shared data\\vp运维工具"

行为：
  1. 创建 <out>/{version}/，复制 --source 下的主程序文件（MoveImageForm.exe 及依赖 dll/config）
  2. 写入 version.txt（版本真相源之一，包内嵌）
  3. 可选：复制 --launcher-new 为 Launcher.new（主程序首启时自更新 Launcher）
  4. 生成 manifest.json（文件清单 + SHA256 + 大小；Launcher 下载后逐文件校验，杜绝半成品）
  5. 可选 --server：把版本目录复制到更新服务器 versions 根（即 {server}/{version}/）

注意：version.json 的 latest/gray 策略由运维人工修改（见灰度发布 SOP），本脚本不改动。
"""
import argparse
import hashlib
import json
import os
import shutil
import sys

# 打包时排除的文件（构建副产物 / 运行时产物，不属于版本包）
EXCLUDE_NAMES = {
    "manifest.json", "version.txt", "launcher.new",
    "launcher.exe", "launcher.old", "update.bat",
    "update.status", "update.failed", "update.dismissed", "update.pending",
    "gray.state", "machine.id", "launcher_debug.log", "update.log",
}
EXCLUDE_EXTS = {".pdb", ".tmp", ".log", ".bak"}
EXCLUDE_DIRS = {"logs", "backup", ".temp"}


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def collect_files(source_dir):
    """返回 (相对 posix 路径, 绝对路径) 列表，递归但跳过排除项。"""
    items = []
    for root, dirs, files in os.walk(source_dir):
        dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_DIRS]
        for name in files:
            lower = name.lower()
            if lower in EXCLUDE_NAMES or os.path.splitext(lower)[1] in EXCLUDE_EXTS:
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, source_dir).replace("\\", "/")
            items.append((rel, full))
    items.sort()
    return items


def main():
    ap = argparse.ArgumentParser(description="VP运维工具版本打包（含 manifest.json 与 version.txt）")
    ap.add_argument("--version", required=True, help="版本号，如 1.0.6")
    ap.add_argument("--source", required=True, help="构建输出目录（含 MoveImageForm.exe 与依赖）")
    ap.add_argument("--out", required=True, help="打包输出根目录（其下创建 {version}/ 子目录）")
    ap.add_argument("--launcher-new", default="", help="可选：新版 Launcher.exe 路径，打入包内 Launcher.new")
    ap.add_argument("--server", default="", help="可选：更新服务器 versions 根目录（本地路径或 UNC），打包后复制过去")
    args = ap.parse_args()

    version = args.version.strip()
    parts = version.split(".")
    if len(parts) != 3 or not all(p.isdigit() for p in parts):
        print("错误：版本号必须是 X.Y.Z 形式，得到: " + version)
        return 2

    source = os.path.abspath(args.source)
    if not os.path.isdir(source):
        print("错误：--source 目录不存在: " + source)
        return 2
    if not os.path.isfile(os.path.join(source, "MoveImageForm.exe")):
        print("错误：--source 下缺少 MoveImageForm.exe: " + source)
        return 2

    out_dir = os.path.join(os.path.abspath(args.out), version)
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)

    # 1. 复制主程序文件
    items = collect_files(source)
    if not items:
        print("错误：--source 下没有可打包的文件")
        return 2
    for rel, full in items:
        dest = os.path.join(out_dir, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copy2(full, dest)

    # 2. version.txt（版本真相源，包内嵌）
    with open(os.path.join(out_dir, "version.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(version + "\n")

    # 3. 可选 Launcher.new
    if args.launcher_new:
        if not os.path.isfile(args.launcher_new):
            print("错误：--launcher-new 文件不存在: " + args.launcher_new)
            return 2
        shutil.copy2(args.launcher_new, os.path.join(out_dir, "Launcher.new"))

    # 4. manifest.json（排除自身；含 version.txt / Launcher.new）
    files = []
    for root, dirs, names in os.walk(out_dir):
        for name in names:
            if name == "manifest.json":
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, out_dir).replace("\\", "/")
            files.append({
                "path": rel,
                "sha256": sha256_of(full),
                "size": os.path.getsize(full),
            })
    files.sort(key=lambda x: x["path"])
    manifest = {"version": version, "files": files}
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    print("打包完成: " + out_dir)
    print("  文件数: {}（含 version.txt{}）".format(
        len(files), "、Launcher.new" if args.launcher_new else ""))

    # 5. 可选：复制到更新服务器
    if args.server:
        server_dir = os.path.join(args.server, version)
        if os.path.isdir(server_dir):
            shutil.rmtree(server_dir)
        shutil.copytree(out_dir, server_dir)
        print("已上传到更新服务器: " + server_dir)
        print("下一步（人工）：编辑服务器 version.json —— 灰度发布在 gray 块登记 "
              "version={}；全量发布改 latest={}，并在 versions 块补充日期与说明。".format(version, version))
    else:
        print("下一步：把 {} 整个目录上传到更新服务器 versions 根，".format(out_dir))
        print("然后人工编辑 version.json（gray 块灰度发布 / latest 全量发布）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
