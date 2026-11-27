"""Package small upgrade and installer assets without duplicating game data."""

import argparse
from pathlib import Path
import stat
import zipfile

PROJECT = Path(__file__).resolve().parents[1]


def add(archive, name, path, executable=False):
    entry = zipfile.ZipInfo(name)
    entry.create_system = 3
    entry.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
    entry.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(entry, path.read_bytes())


def make(assets, output):
    output.mkdir(parents=True, exist_ok=True)
    windows = output / "Stardew-WASM-Wallpaper-Windows-Host.zip"
    mac = output / "Stardew-WASM-Wallpaper-macOS-Installer.zip"
    with zipfile.ZipFile(assets) as full, zipfile.ZipFile(windows, "w", zipfile.ZIP_DEFLATED) as update:
        for entry in full.namelist():
            filename = entry.split("/")[-1]
            if entry.count("/") == 1 and (
                filename.endswith((".exe", ".dll")) or filename == "project.json" or filename.startswith("WebView2-")
            ):
                update.writestr(filename, full.read(entry))
        add(update, "开始使用.txt", PROJECT / "distribution/README-Windows.txt")
        add(update, "LICENSE", PROJECT / "LICENSE")
    with zipfile.ZipFile(mac, "w") as installer:
        add(installer, "install.command", PROJECT / "mac/install.command", executable=True)
        for name in ("launch", "Info.plist", "package.json", "main.js", "game-host.js", "menubar.png"):
            add(installer, "app/" + name, PROJECT / "mac" / name)
        add(installer, "开始使用.txt", PROJECT / "distribution/README-macOS.txt")
        add(installer, "LICENSE", PROJECT / "LICENSE")
    for output_file in (windows, mac):
        with zipfile.ZipFile(output_file) as archive:
            if archive.testzip() is not None:
                raise ValueError(f"Failed ZIP verification {output_file}")
        print(f"{output_file.name}: {output_file.stat().st_size:,} bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--windows-full-zip", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    make(args.windows_full_zip, args.out)
