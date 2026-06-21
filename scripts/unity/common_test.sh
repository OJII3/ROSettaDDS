#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

cat >"$TMP_DIR/uloop" <<'SCRIPT'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"$ULOOP_CALLS"
if [[ "$1" == "execute-dynamic-code" ]]; then
  exit "${ULOOP_EXECUTE_EXIT:-1}"
fi
exit 0
SCRIPT
chmod +x "$TMP_DIR/uloop"

export PATH="$TMP_DIR:$PATH"
export ULOOP_CALLS="$TMP_DIR/uloop.calls"
export ULOOP_EXECUTE_EXIT=1

source "$ROOT_DIR/scripts/unity/common.sh"

if uloop_editor_available; then
  echo "uloop_editor_available should fail when execute-dynamic-code cannot reach Editor" >&2
  exit 1
fi

if ! grep -q '^execute-dynamic-code ' "$ULOOP_CALLS"; then
  echo "uloop_editor_available should probe Editor with execute-dynamic-code" >&2
  exit 1
fi

printf 'common.sh tests passed\n'
