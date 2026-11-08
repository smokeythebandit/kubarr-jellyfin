#!/bin/sh
# Copy the plugin into Jellyfin's plugin folder, replacing any other Kubarr
# version, so exactly one copy loads. Run as Jellyfin's user with its config
# volume mounted. PLUGIN_DIR defaults to the linuxserver image's layout.
set -eu
PLUGIN_DIR="${PLUGIN_DIR:-/config/data/plugins}"
version=$(sed -n 's/.*"version": "\([^"]*\)".*/\1/p' /plugin/meta.json)
target="$PLUGIN_DIR/Kubarr_$version"
mkdir -p "$PLUGIN_DIR"
for old in "$PLUGIN_DIR"/Kubarr_*; do
  [ -e "$old" ] || continue
  [ "$old" = "$target" ] && continue
  rm -rf "$old"
done
mkdir -p "$target"
cp /plugin/Jellyfin.Plugin.Kubarr.dll "$target/"
# Keep Jellyfin's own status edits (it rewrites meta.json) unless the version changed.
[ -f "$target/meta.json" ] || cp /plugin/meta.json "$target/"
echo "Installed Kubarr plugin $version to $target"
