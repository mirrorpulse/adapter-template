#!/usr/bin/env sh
set -eu

if ! command -v pwsh >/dev/null 2>&1; then
    printf '%s\n' 'Install PowerShell 7 before building the Adapter.' >&2
    exit 1
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec pwsh -NoLogo -NoProfile -File "$script_dir/Build-Adapter.ps1" "$@"
