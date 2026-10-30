"""Prepare a macOS bundle or create an explicitly unsigned preview archive.

Developer ID releases are produced only by signing/MacRelease.py on macOS,
after this script has finished every bundle resource and Info.plist change.
"""
import argparse
import hashlib
import io
import json
import os
import plistlib
import stat
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNTIMES = {"osx-arm64": 0x0100000C, "osx-x64": 0x01000007}
MACHO_MAGICS = (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf",
                b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca",
                b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca")


def version_from_project():
    version = ET.parse(ROOT / "src/MathGPT.Mac/MathGPT.Mac.csproj").findtext(".//Version")
    if not version or not all(c.isdigit() or c == "." for c in version):
        raise ValueError("Mac project must have a numeric Version")
    return version


def macho_architectures(path):
    with path.open("rb") as stream:
        header = stream.read(4096)
    if header[:4] not in MACHO_MAGICS:
        return set()
    if len(header) < 8:
        raise ValueError(f"Truncated Mach-O: {path.name}")
    magic = header[:4]
    if magic in MACHO_MAGICS[:2]:
        return {struct.unpack(("<" if magic == MACHO_MAGICS[0] else ">") + "I", header[4:8])[0]}
    endian = ">" if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf") else "<"
    count = struct.unpack(endian + "I", header[4:8])[0]
    stride = 32 if magic in (b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca") else 20
    if count < 1 or count > 16 or len(header) < 8 + count * stride:
        raise ValueError(f"Invalid fat Mach-O header: {path.name}")
    return {struct.unpack_from(endian + "I", header, 8 + index * stride)[0] for index in range(count)}


def validate_native_files(bundle, runtime):
    native = []
    for path in sorted(bundle.rglob("*")):
        if path.is_symlink():
            raise ValueError("This bundle layout does not support symbolic links")
        if not path.is_file():
            continue
        architectures = macho_architectures(path)
        if path.suffix == ".dylib" and not architectures:
            raise ValueError(f"Invalid native library: {path.relative_to(bundle)}")
        if architectures:
            if RUNTIMES[runtime] not in architectures:
                raise ValueError(f"Wrong Mach-O architecture: {path.relative_to(bundle)}")
            native.append(path)
    for name in ("MathGPT", "MathGPT.Bridge"):
        if bundle / "Contents/MacOS" / name not in native:
            raise ValueError(f"Missing native executable: {name}")
    if not any(path.name == "libcoreclr.dylib" for path in native):
        raise ValueError("Self-contained .NET runtime is missing")
    return native


def create_icon(target):
    source = ROOT / "src/MathGPT.App/Assets/MathGPT.png"
    if sys.platform == "darwin":
        # Use Apple's existing tools; the macOS runner needs no Pillow dependency.
        with tempfile.TemporaryDirectory(prefix="mathgpt-icon-") as folder:
            iconset = Path(folder) / "MathGPT.iconset"
            iconset.mkdir()
            for size in (16, 32, 128, 256, 512):
                for scale in (1, 2):
                    name = f"icon_{size}x{size}" + ("@2x" if scale == 2 else "") + ".png"
                    subprocess.run(["/usr/bin/sips", "-z", str(size * scale), str(size * scale),
                                    str(source), "--out", str(iconset / name)],
                                   check=True, capture_output=True)
            subprocess.run(["/usr/bin/iconutil", "-c", "icns", str(iconset), "-o", str(target)],
                           check=True, capture_output=True)
    else:
        # Retain the existing Windows preview dependency and transparent artwork.
        from PIL import Image
        icon = Image.open(source).convert("RGBA")
        if icon.getchannel("A").getextrema()[0] != 0:
            raise ValueError("App icon must retain transparency")
        chunks = []
        for tag, size in (("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)):
            output = io.BytesIO()
            icon.resize((size, size), Image.Resampling.LANCZOS).save(output, format="PNG")
            data = output.getvalue()
            chunks.append(tag.encode("ascii") + struct.pack(">I", len(data) + 8) + data)
        data = b"".join(chunks)
        target.write_bytes(b"icns" + struct.pack(">I", len(data) + 8) + data)


def usage_guide(version, preview):
    title = "预览版" if preview else "Developer ID 签名与公证版"
    warning = (
        "本预览包未进行 Developer ID 签名、公证或真实 Mac 验收。若系统提示无法验证开发者，"
        "请先核对 mathgpt.xyz 来源和 SHA256，再按 Apple 官方说明在“系统设置 → 隐私与安全性”"
        "决定是否“仍要打开”。\n"
        if preview else
        "本包的 Developer ID 签名、公证票据和 Gatekeeper 检查结果见同名 JSON 清单；"
        "这些检查不代表真实 Mac 上的全部功能或钥匙串流程已验收。\n"
    )
    return (
        f"MathGPT macOS {title} {version}\n\n"
        "1. 解压后打开 MathGPT.app，可将应用拖入“应用程序”。\n"
        "2. 点击“继续网页配置”，在浏览器核对相同代码并确认，承接网站已选应用、模型与密钥。\n"
        "3. 点击“安装并开始”，自动下载、配置并打开；已有应用会直接使用。"
        "后续在连接页选连接、模型与强度，点击“切换并打开”。无需安装 CC Switch 或 Node。\n\n"
        "支持 macOS 13+；厂商客户端可能要求 macOS 14+。\n" + warning +
        "不要关闭 Gatekeeper、SIP 或执行移除隔离属性的命令；"
        "若提示恶意软件或签名损坏，请停止并反馈。\n"
        "Apple 说明：https://support.apple.com/zh-cn/102445\n\n"
        "本机检查：在“客户端”点击“运行本机自检”。检查钥匙串、内置适配器和官网连接，"
        "不发起收费请求；报告保存在个人数据目录 Diagnostics，可发给客服排障。\n\n"
        "密钥使用系统钥匙串。DeepSeek Harness 官方 Mac 包目前仅支持 Apple 芯片。"
        "已有应用、会话和配置保留；客户端运行时请先正常退出，再应用新配置。"
        "桌面入口使用 Codex、Claude、DeepSeek Harness 原名。\n\n"
        "安装包与源码：https://mathgpt.xyz/tools/desktop\n"
        "说明：https://mathgpt.xyz/docs/index.html#mac\n"
    )


def prepare_bundle(bundle, runtime, preview=True):
    if (bundle / "Contents/_CodeSignature").exists():
        raise ValueError("Refusing to change an already signed bundle; build a fresh bundle")
    resources = bundle / "Contents/Resources"
    if not resources.is_dir():
        raise ValueError("Missing bundle Resources directory")
    version = version_from_project()
    native = validate_native_files(bundle, runtime)
    create_icon(resources / "MathGPT.icns")
    manifest = {
        "CFBundleExecutable": "MathGPT", "CFBundleName": "MathGPT",
        "CFBundleDisplayName": "MathGPT", "CFBundleIconFile": "MathGPT.icns",
        "CFBundleIdentifier": "xyz.mathgpt.desktop", "CFBundlePackageType": "APPL",
        "CFBundleVersion": version, "CFBundleShortVersionString": version,
        "LSMinimumSystemVersion": "13.0", "NSHighResolutionCapable": True,
    }
    (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps(manifest))
    (resources / "使用说明.txt").write_text(usage_guide(version, preview), encoding="utf-8")
    if sys.platform == "darwin":
        for path in native:
            path.chmod(0o755)
    return version, native


def write_preview(bundle, runtime, output):
    version, native = prepare_bundle(bundle, runtime)
    output.mkdir(parents=True, exist_ok=True)
    target = output / f"MathGPT-{version}-mac-{runtime.removeprefix('osx-')}-preview.zip"
    temporary = target.with_suffix(".zip.partial")
    check_script = (
        '#!/bin/sh\nset -u\nHERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1\n'
        '"$HERE/MathGPT.app/Contents/MacOS/MathGPT" --self-check\nRESULT=$?\n'
        'printf "\\n检查退出代码：%s（0 表示全部通过）。按回车关闭。\\n" "$RESULT"\n'
        'read -r REPLY\nexit "$RESULT"\n'
    )
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path in sorted(bundle.rglob("*")):
                if not path.is_file():
                    continue
                entry = zipfile.ZipInfo(path.relative_to(bundle.parent).as_posix())
                entry.create_system = 3
                entry.external_attr = (stat.S_IFREG | (0o755 if path in native else 0o644)) << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(entry, path.read_bytes())
            archive.writestr("使用说明.txt", usage_guide(version, True))
            entry = zipfile.ZipInfo("运行本机检查.command")
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | 0o755) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, check_script)
        with zipfile.ZipFile(temporary) as archive:
            for path in native:
                if not (archive.getinfo(path.relative_to(bundle.parent).as_posix()).external_attr >> 16) & stat.S_IXUSR:
                    raise ValueError("Preview archive lost executable permission")
            if archive.testzip() is not None:
                raise ValueError("Preview archive CRC verification failed")
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    with target.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    result = {"runtime": runtime, "version": version, "channel": "preview",
              "file": target.name, "bytes": target.stat().st_size, "sha256": digest,
              "macho": True, "native_file_count": len(native), "unix_executable_bits": True,
              "real_mac_tested": False, "signed_notarized": False}
    target.with_suffix(".json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def main():
    if sys.version_info < (3, 11):
        raise SystemExit("Mac packaging requires Python 3.11 or newer")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", choices=RUNTIMES)
    parser.add_argument("--mode", choices=("preview", "prepare"), default="preview")
    parser.add_argument("--bundle", type=Path)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/packages")
    args = parser.parse_args()
    bundle = args.bundle or ROOT / "build/mac" / args.runtime / "MathGPT.app"
    if args.mode == "prepare":
        version, native = prepare_bundle(bundle, args.runtime, preview=False)
        result = {"runtime": args.runtime, "version": version, "stage": "prepared",
                  "native_file_count": len(native), "signed_notarized": False}
    else:
        result = write_preview(bundle, args.runtime, args.output_dir)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
