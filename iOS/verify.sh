#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

# Use the installed Xcode, its SDK and an available iOS 17+ iPhone simulator.
xcodebuild -version
device="${RUXU_SIMULATOR_UDID:-}"
if [[ -z "$device" ]]; then
  device="$(xcrun simctl list devices available -j | python3 -c '
import json, sys
rows = []
for runtime, devices in json.load(sys.stdin)["devices"].items():
    if ".iOS-" not in runtime:
        continue
    version = tuple(map(int, runtime.rsplit(".iOS-", 1)[1].split("-")))
    if version < (17,):
        continue
    for device in devices:
        if device["name"].startswith("iPhone"):
            rows.append((device["state"] == "Booted", version, device["udid"]))
print(max(rows)[2] if rows else "")
')"
fi
if [[ -z "$device" ]]; then
  echo "请先在 Xcode 中安装 iOS 17 或更新的模拟器，并创建 iPhone 模拟设备。" >&2
  exit 1
fi
xcodebuild test -project RuxuReader.xcodeproj -scheme RuxuReader \
  -configuration Debug -destination "platform=iOS Simulator,id=$device" \
  -parallel-testing-enabled NO
