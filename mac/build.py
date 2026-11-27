"""Build unsigned macOS launcher ZIPs around unmodified official Electron bundles."""

import argparse
import copy
import hashlib
from pathlib import Path
import plistlib
import shutil
import stat
import zipfile

VERSION = "v45.5.0"
RUNTIMES = {
    "arm64": "00d345b061064de7e6e2c02bec7ffa1adb7ef488fe4b673bd8ad3e059a182245",
    "x64": "9ce4b0e25ce95e743b2d8f04ebfbfd76027060ff8b47829de22334fbdbdd4cc4",
}
SOURCE = Path(__file__).resolve().parent


def put(archive, name, data, permission=0o644):
    entry = zipfile.ZipInfo(name)
    entry.create_system = 3
    entry.external_attr = (stat.S_IFREG | permission) << 16
    entry.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(entry, data)


def package(runtime_dir, release, arch):
    runtime = runtime_dir / f"electron-{VERSION}-darwin-{arch}.zip"
    with runtime.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != RUNTIMES[arch]:
            raise ValueError(f"Electron ZIP hash mismatch: {runtime}")
    release.mkdir(parents=True, exist_ok=True)
    archive = release / f"Stardew-WASM-Wallpaper-macOS-{arch}.zip"
    draft = archive.with_suffix(".tmp")
    contents = "Stardew Wallpaper.app/Contents/"
    resources = contents + "Resources/"
    with zipfile.ZipFile(runtime) as upstream, zipfile.ZipFile(draft, "w", zipfile.ZIP_DEFLATED, compresslevel=3) as output:
        upstream_binary = "Electron.app/Contents/MacOS/Electron"
        upstream.getinfo(upstream_binary)
        for entry in upstream.infolist():
            renamed = copy.copy(entry)
            renamed.filename = resources + entry.filename
            with upstream.open(entry) as source, output.open(renamed, "w") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
        put(output, contents + "Info.plist", (SOURCE / "Info.plist").read_bytes())
        launcher = (SOURCE / "launch").read_text(encoding="utf-8").encode("utf-8")
        put(output, contents + "MacOS/launch", launcher, 0o755)
        for name in ("package.json", "main.js", "game-host.js", "menubar.png"):
            put(output, resources + "app/" + name, (SOURCE / name).read_bytes())
        put(output, "开始使用.txt", (SOURCE / "开始使用.txt").read_bytes())

    with zipfile.ZipFile(runtime) as upstream, zipfile.ZipFile(draft) as output:
        if output.testzip() is not None:
            raise ValueError("macOS ZIP failed CRC validation")
        for entry in upstream.infolist():
            copy_entry = output.getinfo(resources + entry.filename)
            if (entry.CRC, entry.external_attr) != (copy_entry.CRC, copy_entry.external_attr):
                raise ValueError(f"Runtime content or mode changed: {entry.filename}")
        plist = plistlib.loads(output.read(contents + "Info.plist"))
        if plist["CFBundleExecutable"] != "launch" or not (output.getinfo(contents + "MacOS/launch").external_attr >> 16) & stat.S_IXUSR:
            raise ValueError("macOS launcher is missing or lacks execute permissions")
    draft.replace(archive)
    print(f"{archive.name}: {archive.stat().st_size:,} bytes verified", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--arch", choices=RUNTIMES, action="append")
    args = parser.parse_args()
    for architecture in args.arch or RUNTIMES:
        package(args.runtime_dir, args.out, architecture)
