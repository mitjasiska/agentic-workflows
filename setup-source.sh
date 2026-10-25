#!/usr/bin/env bash
set -euo pipefail
if [[ "$(uname -s)" != Linux ]]; then
    echo 'Source setup requires Linux and a non-root systemd user service.' >&2
    exit 1
fi
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if (( $# > 1 )) || [[ "${1:-}" != '' && "${1:-}" != --remove ]]; then
    echo 'Usage: ./setup-source.sh [--remove]' >&2
    exit 2
fi
if ! command -v dotnet >/dev/null; then echo 'Install the .NET 10 SDK, then run ./build.sh.' >&2; exit 1; fi
# Running here applies global.json; never use dotnet run (which could build).
if ! dotnet --version >/dev/null; then echo 'Install the .NET SDK selected by global.json, then run ./build.sh.' >&2; exit 1; fi
helper=tools/AgenticWorkflows.SourceTools/bin/Debug/net10.0/AgenticWorkflows.SourceTools.dll
if [[ ! -f "$helper" ]]; then echo 'Built source support is missing. Run ./build.sh, then ./setup-source.sh.' >&2; exit 1; fi
exec dotnet "$helper" setup "$PWD" "$@"
