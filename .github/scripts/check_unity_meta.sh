#!/usr/bin/env bash
set -euo pipefail

# Project root
cd "$(dirname "$0")/../.."

TARGET_DIR="src/rosettadds"
ERROR=0

echo "Checking for missing .meta files..."
while IFS= read -r -d '' path; do
    if [[ "$path" == *.meta ]]; then
        continue
    fi
    if [ ! -f "${path}.meta" ]; then
        echo "::error file=$path::Missing .meta file for: $path"
        ERROR=1
    fi
done < <(find "$TARGET_DIR" -print0)

echo "Checking for orphan .meta files..."
if [ -f "${TARGET_DIR}.meta" ]; then
    if [ ! -e "$TARGET_DIR" ]; then
        echo "::error file=${TARGET_DIR}.meta::Orphan .meta file: ${TARGET_DIR}.meta"
        ERROR=1
    fi
fi

while IFS= read -r -d '' meta_path; do
    if [[ "$meta_path" != *.meta ]]; then
        continue
    fi
    orig_path="${meta_path%.meta}"
    if [ ! -e "$orig_path" ]; then
        echo "::error file=$meta_path::Orphan .meta file: $meta_path"
        ERROR=1
    fi
done < <(find "$TARGET_DIR" -name "*.meta" -print0)

if [ "$ERROR" -ne 0 ]; then
    echo "Unity meta file check failed!"
    exit 1
fi

echo "All Unity meta files are valid."
exit 0
