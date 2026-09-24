#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
runtime="${1:-linux-x64}"
case "$runtime" in
  linux-x64|win-x64) ;;
  *) echo "Usage: $0 [linux-x64|win-x64]" >&2; exit 2 ;;
esac

output_dir="${2:-$root_dir/artifacts/avalonia-$runtime}"
dotnet publish "$root_dir/src/NetHog.Avalonia/NetHog.Avalonia.csproj" \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -o "$output_dir"

echo "Avalonia $runtime build written to $output_dir"
