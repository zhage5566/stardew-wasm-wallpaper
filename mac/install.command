#!/bin/sh
set -eu

root=$(CDPATH= cd "$(dirname "$0")" && pwd -P)
cd "$root"
version=v45.5.0
if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || printf 0)" = 1 ]; then
    architecture=arm64
    checksum=00d345b061064de7e6e2c02bec7ffa1adb7ef488fe4b673bd8ad3e059a182245
else
    architecture=x64
    checksum=9ce4b0e25ce95e743b2d8f04ebfbfd76027060ff8b47829de22334fbdbdd4cc4
fi
app="Stardew Wallpaper.app"
if [ -e "$app" ]; then
    /usr/bin/open "$app"
    exit
fi
stage=$(/usr/bin/mktemp -d "$root/.stardew-install.XXXXXXXX")
runtime="electron-$version-darwin-$architecture.zip"
if [ -f "$root/$runtime" ]; then
    archive="$root/$runtime"
else
    archive="$stage/$runtime"
    printf '正在下载 macOS 运行时 (%s)\n' "$architecture"
    /usr/bin/curl --fail --location --retry 3 --connect-timeout 15 --output "$archive" \
        "https://github.com/electron/electron/releases/download/$version/$runtime"
fi
actual=$(/usr/bin/shasum -a 256 "$archive" | /usr/bin/awk '{print $1}')
if [ "$actual" != "$checksum" ]; then
    printf 'Electron 运行时校验失败。\n' >&2
    exit 1
fi
contents="$stage/$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources/app"
cp "$root/app/Info.plist" "$contents/Info.plist"
cp "$root/app/launch" "$contents/MacOS/launch"
chmod 755 "$contents/MacOS/launch"
for file in main.js game-host.js package.json menubar.png; do
    cp "$root/app/$file" "$contents/Resources/app/$file"
done
/usr/bin/ditto -x -k "$archive" "$contents/Resources"
mv "$stage/$app" "$root/$app"
if [ "$archive" = "$stage/$runtime" ]; then rm -f "$archive"; fi
rmdir "$stage"
/usr/bin/open "$root/$app"
