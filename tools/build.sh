#!/bin/zsh
# 编译「公众号链接采集.app」到 output/
#   tools/build.sh           本机用：只编 Apple 芯片版
#   tools/build.sh release   发给别的 Mac：Apple 芯片 + Intel 通用版，再打成 output/WXLinks-mac.dmg
# 版本号：VERSION=1.2.0 tools/build.sh release（GitHub Actions 按 tag 传进来）
# 签名：默认临时签名（ad-hoc）。以后有了证书，设 SIGN_ID="证书名" 就用证书签（更新后不用重新授权）
# 证书签名的好处：以后更新重新编译，系统里给它开的权限不会失效；临时签名每次重编都要重新授权。
# 签名标识必须是英文：中文标识会让系统文字识别一直报错。
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -d /Applications/Xcode.app ]] && export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer

VERSION="${VERSION:-1.0.0}"
NAME="公众号链接采集"
BUNDLE_ID="io.github.carey314.wxlinks"
# 默认临时签名（ad-hoc）：不需要证书，别的 Mac 第一次打开时在「系统设置 → 隐私与安全性」里点「仍要打开」即可
DEFAULT_SIGN="-"
SIGN="${SIGN_ID:-$DEFAULT_SIGN}"
APP="output/$NAME.app"
TMP=".build"
ARCHS=(arm64)
[[ "${1:-}" == release ]] && ARCHS=(arm64 x86_64)

mkdir -p "$TMP" output
# 图标用仓库里的 Resources/AppIcon.icns；改了 tools/make-icon.swift 要重新生成：删掉它再编译一次
if [[ -f Resources/AppIcon.icns ]]; then
  cp Resources/AppIcon.icns "$TMP/AppIcon.icns"
elif [[ ! -f "$TMP/AppIcon.icns" ]]; then
  rm -rf "$TMP/AppIcon.iconset"
  swift tools/make-icon.swift "$TMP/AppIcon.iconset"
  iconutil -c icns "$TMP/AppIcon.iconset" -o "$TMP/AppIcon.icns"
  cp "$TMP/AppIcon.icns" Resources/AppIcon.icns
fi

for arch in $ARCHS; do
  mkdir -p "$TMP/$arch"
  echo "编译界面（$arch）…"
  swiftc -O -swift-version 5 -parse-as-library -target "$arch-apple-macos14.0" -o "$TMP/$arch/wxlinks" Sources/App/*.swift
  echo "编译采集引擎（$arch）…"
  swiftc -O -swift-version 5 -target "$arch-apple-macos14.0" -o "$TMP/$arch/wxlinks-engine" Sources/engine.swift
done

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/zh-Hans.lproj"
for bin in wxlinks wxlinks-engine; do
  inputs=()
  for arch in $ARCHS; do inputs+=("$TMP/$arch/$bin"); done
  lipo -create "${inputs[@]}" -output "$APP/Contents/MacOS/$bin"
done
cp "$TMP/AppIcon.icns" "$APP/Contents/Resources/"
# 强调色（侧栏选中、按钮、分段选择器）用应用的绿色；系统强调色是「多色」时生效，用户自己选了别的颜色就跟随用户
if xcrun --find actool >/dev/null 2>&1; then
  xcrun actool Resources/Assets.xcassets --compile "$APP/Contents/Resources" --platform macosx \
    --minimum-deployment-target 14.0 --output-partial-info-plist "$TMP/assets-info.plist" >/dev/null
else
  echo "⚠️ 没找到 actool（要装 Xcode），这次不带绿色强调色"
fi
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>wxlinks</string>
  <key>CFBundleName</key><string>$NAME</string>
  <key>CFBundleDisplayName</key><string>$NAME</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>NSAccentColorName</key><string>AccentColor</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$(date +%Y%m%d%H%M)</string>
  <key>CFBundleDevelopmentRegion</key><string>zh-Hans</string>
  <key>CFBundleLocalizations</key><array><string>zh-Hans</string></array>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License · github.com/carey314/wechat-article-links</string>
</dict>
</plist>
PLIST

# 先签里面的引擎，再签整个应用
sign() {
  local req=()
  # 临时签名默认按「这一次编译的指纹」认人，每次重编系统都当成新应用、权限失效；
  # 改成只认程序标识，更新版本后「辅助功能」「录屏」权限还在
  [[ "$1" == "-" ]] && req=(-r="designated => identifier \"$BUNDLE_ID\"")
  codesign -f -s "$1" --timestamp=none -i "$BUNDLE_ID.engine" "$APP/Contents/MacOS/wxlinks-engine" &&
    codesign -f -s "$1" --timestamp=none "${req[@]}" "$APP"
}
if ! sign "$SIGN"; then
  # 没指定 SIGN_ID 时，证书签不了（比如过期）就退回临时签名，不让编译失败
  [[ -n "${SIGN_ID:-}" || "$SIGN" == "-" ]] && exit 1
  echo "⚠️ 证书签不了（多半过期了），这次改用临时签名"
  SIGN="-"
  sign "$SIGN"
fi
codesign --verify --strict "$APP"
echo "已编译：$APP（$(lipo -archs "$APP/Contents/MacOS/wxlinks")，签名：$SIGN）"

if [[ "${1:-}" == release ]]; then
  STAGE="$TMP/dmg"
  rm -rf "$STAGE"; mkdir -p "$STAGE"
  ditto "$APP" "$STAGE/$NAME.app"
  ln -s /Applications "$STAGE/应用程序"
  cp tools/第一次打开.txt "$STAGE/"
  # 文件名用英文：下载链接里不出现中文，GitHub 的「最新版」固定链接也靠这个名字
  DMG="output/WXLinks-mac.dmg"
  rm -f "$DMG"
  # hdiutil 偶尔报「资源暂时不可用」，重试几次
  for i in 1 2 3 4 5; do
    hdiutil create -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null && break
    echo "hdiutil 失败，第 $i 次重试…"; sleep 3
  done
  [[ -f "$DMG" ]] || { echo "打包 dmg 失败"; exit 1; }
  echo "已打包：$DMG（$(du -h "$DMG" | cut -f1)）"
fi
