#!/usr/bin/env bash

interop_repo_root() {
  cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd
}

interop_require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "required command not found: $1" >&2
    exit 127
  fi
}

interop_wait_for_log() {
  local pattern="$1"
  local file="$2"
  local timeout_seconds="$3"
  local deadline=$((SECONDS + timeout_seconds))

  while (( SECONDS < deadline )); do
    if [[ -f "${file}" ]] && grep -qE "${pattern}" "${file}"; then
      return 0
    fi
    sleep 1
  done

  echo "timed out waiting for '${pattern}' in ${file}" >&2
  echo "--- ${file} ---" >&2
  [[ -f "${file}" ]] && tail -n 120 "${file}" >&2
  return 1
}

interop_cleanup_pids() {
  local pid
  for pid in "$@"; do
    if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
      kill "${pid}" 2>/dev/null || true
      wait "${pid}" 2>/dev/null || true
    fi
  done
}

interop_pick_domain_id() {
  if [[ -n "${ROS_DOMAIN_ID:-}" ]]; then
    echo "${ROS_DOMAIN_ID}"
    return
  fi

  echo $((70 + (RANDOM % 20)))
}

interop_configure_darwin_dyld_path() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    return
  fi

  local path
  local lib_dirs=()
  IFS=':' read -ra paths <<< "${NIXPKGS_CMAKE_PREFIX_PATH:-}"
  for path in "${paths[@]:-}"; do
    if [[ -f "${path}/lib/libyaml.dylib" ]]; then
      lib_dirs+=("${path}/lib")
    fi
  done

  if (( ${#lib_dirs[@]} > 0 )); then
    local joined
    joined="$(IFS=:; echo "${lib_dirs[*]}")"
    export DYLD_LIBRARY_PATH="${DYLD_LIBRARY_PATH:+${DYLD_LIBRARY_PATH}:}${joined}"
  fi
}
