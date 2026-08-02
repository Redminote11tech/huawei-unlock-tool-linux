#!/bin/sh
# Wrapper: run the tool from a writable per-user data dir so its working files
# (UnlockFiles/, Logs/, config.json, downloaded loaders, qc_boot/) don't land in
# $PWD or the read-only install prefix.
PREFIX=/usr/lib/huawei-unlock-tool
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/huawei-unlock-tool"
mkdir -p "$DATA"

# The core resolves Languages/ (and other data dirs) relative to the working
# directory, exactly like the Windows original. Seed the bundled language files
# into the data dir on first run (the upstream app does the equivalent via its
# embedded-resource extraction).
if [ ! -d "$DATA/Languages" ] && [ -d "$PREFIX/Languages" ]; then
    mkdir -p "$DATA/Languages"
    cp -n "$PREFIX"/Languages/*.ini "$DATA/Languages/" 2>/dev/null || true
fi

cd "$DATA" || exit 1
exec "$PREFIX/HuaweiUnlock" "$@"
