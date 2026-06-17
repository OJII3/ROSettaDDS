#!/usr/bin/env bash
# tools/ros2-perf-helper の Linux smoke 検証。
# ROS_LOCALHOST_ONLY=1 / RMW_IMPLEMENTATION=rmw_fastrtps_cpp / ROS_DOMAIN_ID で
# 同一マシン loopback 上で helper の pub / sub / fanout / idle timeout /
# invalid arg / 32 KiB payload を確認する。
#
# nix develop / direnv 環境、helper が build 済みであることを前提とする。
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HELPER="$ROOT_DIR/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper"

if [[ ! -x "$HELPER" ]]; then
  echo "helper not built: $HELPER" >&2
  echo "scripts/ros2/build_helper.sh を先に実行してください" >&2
  exit 1
fi

export ROS_LOCALHOST_ONLY="${ROS_LOCALHOST_ONLY:-1}"
export RMW_IMPLEMENTATION="${RMW_IMPLEMENTATION:-rmw_fastrtps_cpp}"
export ROS_DOMAIN_ID="${ROS_DOMAIN_ID:-50}"

LOG_DIR="${LOG_DIR:-/tmp/opencode/ros2_perf_full}"
mkdir -p "$LOG_DIR"

log() { printf '[verify] %s\n' "$*"; }

assert_done_received() {
  local log="$1" want="$2"
  local got
  got=$(grep -E '"event":"done"' "$log" | tail -1 | sed -nE 's/.*"received":([0-9]+).*/\1/p')
  if [[ -z "$got" ]]; then
    echo "[verify] FAIL: no done event in $log"; cat "$log"; return 1
  fi
  if [[ "$want" != "*" && "$got" != "$want" ]]; then
    echo "[verify] FAIL: want received=$want got=$got in $log"
    cat "$log"; return 1
  fi
  log "ok received=$got ($log)"
}

assert_error() {
  local log="$1" want_msg="$2"
  local event
  event=$(grep -E '"event":"error"' "$log" | tail -1 || true)
  if [[ -z "$event" ]]; then
    echo "[verify] FAIL: no error event in $log"; cat "$log"; return 1
  fi
  if ! printf '%s' "$event" | grep -q "$want_msg"; then
    echo "[verify] FAIL: error event does not contain '$want_msg'"
    cat "$log"; return 1
  fi
  log "ok error: $event"
}

wait_ready() {
  local log="$1" budget_ms="$2"
  local steps=$((budget_ms / 100))
  for _ in $(seq 1 "$steps"); do
    if grep -q '"event":"ready"' "$log" 2>/dev/null; then return 0; fi
    sleep 0.1
  done
  echo "[verify] FAIL: ready timeout in $log"; cat "$log"; return 1
}

# --- 1) pub <-> sub, reliable, 1000 messages, 64 B ---------------------
log "case 1: reliable pub<->sub, 1000x64B"
T="/rosettadds_v_pub_sub_$$"
LOG_S="$LOG_DIR/case1_sub.log"
LOG_P="$LOG_DIR/case1_pub.log"
"$HELPER" --mode sub --topic "$T" --messages 1000 --payload-bytes 64 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 5000 --idle-timeout-ms 5000 \
  >"$LOG_S" 2>&1 &
PID=$!
wait_ready "$LOG_S" 5000
"$HELPER" --mode pub --topic "$T" --messages 1000 --payload-bytes 64 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 5000 --idle-timeout-ms 5000 \
  >"$LOG_P" 2>&1
wait "$PID"
assert_done_received "$LOG_S" 1000

# --- 2) pub <-> sub, best_effort, 500 messages, 128 B ------------------
log "case 2: best_effort pub<->sub, 500x128B"
T="/rosettadds_v_be_$$"
LOG_S="$LOG_DIR/case2_sub.log"
LOG_P="$LOG_DIR/case2_pub.log"
"$HELPER" --mode sub --topic "$T" --messages 500 --payload-bytes 128 \
  --rate-hz 0 --qos best_effort --ready-timeout-ms 5000 --idle-timeout-ms 5000 \
  >"$LOG_S" 2>&1 &
PID=$!
wait_ready "$LOG_S" 5000
"$HELPER" --mode pub --topic "$T" --messages 500 --payload-bytes 128 \
  --rate-hz 0 --qos best_effort --ready-timeout-ms 5000 --idle-timeout-ms 5000 \
  >"$LOG_P" 2>&1
wait "$PID"
assert_done_received "$LOG_S" 500

# --- 3) fanout: 1 pub vs 4 subs, reliable, 250 messages, 32 B ----------
log "case 3: fanout 1 pub to 4 subs, 250x32B"
T="/rosettadds_v_fanout_$$"
LOG_PUB="$LOG_DIR/case3_pub.log"
declare -a PIDS=()
# Fast DDS discovery で 4 participant 分の SPDP/SEDP 交換が直列になり、
# 最初の sub は ready を出すまでに 5s 以上かかる場合があるため ready-timeout を伸ばす。
for i in 0 1 2 3; do
  LOG_SUB="$LOG_DIR/case3_sub_$i.log"
  "$HELPER" --mode sub --topic "$T" --messages 250 --payload-bytes 32 \
    --rate-hz 0 --qos reliable --ready-timeout-ms 15000 --idle-timeout-ms 5000 \
    >"$LOG_SUB" 2>&1 &
  PIDS+=($!)
done
for i in 0 1 2 3; do
  wait_ready "$LOG_DIR/case3_sub_$i.log" 15000
done
"$HELPER" --mode pub --topic "$T" --messages 250 --payload-bytes 32 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 15000 --idle-timeout-ms 5000 \
  >"$LOG_PUB" 2>&1
for PID in "${PIDS[@]}"; do wait "$PID"; done
for i in 0 1 2 3; do
  assert_done_received "$LOG_DIR/case3_sub_$i.log" 250
done

# --- 4) idle timeout: pub stops short, sub ends via idle --------------
log "case 4: idle timeout (pub 10 / sub expects 1000 / idle 2s)"
T="/rosettadds_v_idle_$$"
LOG_I="$LOG_DIR/case4_sub.log"
LOG_IP="$LOG_DIR/case4_pub.log"
"$HELPER" --mode sub --topic "$T" --messages 1000 --payload-bytes 32 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 5000 --idle-timeout-ms 2000 \
  >"$LOG_I" 2>&1 &
PID=$!
wait_ready "$LOG_I" 5000
"$HELPER" --mode pub --topic "$T" --messages 10 --payload-bytes 32 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 5000 --idle-timeout-ms 5000 \
  >"$LOG_IP" 2>&1
wait "$PID" || true
GOT=$(grep -E '"event":"done"' "$LOG_I" | tail -1 | sed -nE 's/.*"received":([0-9]+).*/\1/p')
if [[ -z "$GOT" || "$GOT" -ge 1000 ]]; then
  echo "[verify] FAIL: idle case expected received<1000, got '$GOT'"
  cat "$LOG_I"; exit 1
fi
log "ok: idle received=$GOT (< 1000)"

# --- 5) invalid arg -----------------------------------------------------
log "case 5: invalid --mode"
LOG_E="$LOG_DIR/case5_err.log"
"$HELPER" --mode bogus --topic /x --messages 1 --payload-bytes 1 \
  --rate-hz 0 --qos reliable >"$LOG_E" 2>&1 || true
assert_error "$LOG_E" "must be pub or sub"

# --- 6) large payload (32 KiB x 100) -----------------------------------
log "case 6: large payload 100x32KiB"
T="/rosettadds_v_large_$$"
LOG_S="$LOG_DIR/case6_sub.log"
LOG_P="$LOG_DIR/case6_pub.log"
# 大型 payload の最初の message が discovery + fragmentation 完了前に
# ready timeout を超えることがあるので伸ばす。
"$HELPER" --mode sub --topic "$T" --messages 100 --payload-bytes 32768 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 15000 --idle-timeout-ms 5000 \
  >"$LOG_S" 2>&1 &
PID=$!
wait_ready "$LOG_S" 15000
"$HELPER" --mode pub --topic "$T" --messages 100 --payload-bytes 32768 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 15000 --idle-timeout-ms 5000 \
  >"$LOG_P" 2>&1
wait "$PID"
assert_done_received "$LOG_S" 100

log "all cases passed"
