#!/usr/bin/env bash
set -euo pipefail
if [[ "$(uname -s)" != Linux ]]; then
    echo 'Source builds require Linux.' >&2
    exit 1
fi
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
case "${1:-build}" in
    build|rebuild|test) action="${1:-build}" ;;
    *) echo 'Usage: ./build.sh [build|rebuild|test]' >&2; exit 2 ;;
esac
if (( $# > 1 )); then echo 'Usage: ./build.sh [build|rebuild|test]' >&2; exit 2; fi
# Invalidate the pair before any build, including a failed rebuild or test run.
rm -f -- artifacts/source-build.json
if [[ "$action" == rebuild ]]; then dotnet clean AgenticWorkflows.slnx; fi
dotnet build AgenticWorkflows.slnx
if [[ "$action" == test ]]; then dotnet test AgenticWorkflows.slnx --no-build --no-restore; fi
dotnet tools/AgenticWorkflows.SourceTools/bin/Debug/net10.0/AgenticWorkflows.SourceTools.dll seal "$PWD"
