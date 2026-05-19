#!/usr/bin/env bash
set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ROOT_DIR="$(interop_repo_root)"
LOG_DIR="${ROOT_DIR}/artifacts/interop"
DOMAIN_ID="$(interop_pick_domain_id)"
RMW_IMPLEMENTATION="${RMW_IMPLEMENTATION:-rmw_fastrtps_cpp}"

interop_configure_darwin_dyld_path
mkdir -p "${LOG_DIR}"

ROS_LISTENER_LOG="${LOG_DIR}/fastdds_ros_listener.log"
RCLSHARP_TALKER_LOG="${LOG_DIR}/fastdds_rclsharp_talker.log"
ROS_TALKER_LOG="${LOG_DIR}/fastdds_ros_talker.log"
RCLSHARP_LISTENER_LOG="${LOG_DIR}/fastdds_rclsharp_listener.log"

ROS_LISTENER=(ros2 run demo_nodes_cpp listener)
ROS_TALKER=(ros2 run demo_nodes_cpp talker)
if [[ -x "${AMENT_PREFIX_PATH:-}/lib/demo_nodes_cpp/listener" && -x "${AMENT_PREFIX_PATH:-}/lib/demo_nodes_cpp/talker" ]]; then
  ROS_LISTENER=("${AMENT_PREFIX_PATH}/lib/demo_nodes_cpp/listener")
  ROS_TALKER=("${AMENT_PREFIX_PATH}/lib/demo_nodes_cpp/talker")
fi

PIDS=()

cleanup() {
  interop_cleanup_pids "${PIDS[@]:-}"
}
trap cleanup EXIT

interop_require_command dotnet
if [[ "${ROS_LISTENER[0]}" == "ros2" || "${ROS_TALKER[0]}" == "ros2" ]]; then
  interop_require_command ros2
fi

export ROS_DOMAIN_ID="${DOMAIN_ID}"
export ROS_LOCALHOST_ONLY="${ROS_LOCALHOST_ONLY:-1}"
export RMW_IMPLEMENTATION

rm -f "${ROS_LISTENER_LOG}" "${RCLSHARP_TALKER_LOG}" "${ROS_TALKER_LOG}" "${RCLSHARP_LISTENER_LOG}"

dotnet build "${ROOT_DIR}/samples/TalkerListener/TalkerListener.csproj" >/dev/null
if command -v ros2 >/dev/null 2>&1; then
  ros2 daemon stop >/dev/null 2>&1 || true
fi

echo "== Fast DDS interop: rclsharp talker -> ROS 2 listener =="
"${ROS_LISTENER[@]}" >"${ROS_LISTENER_LOG}" 2>&1 &
PIDS+=("$!")
sleep 3
dotnet run --no-build --project "${ROOT_DIR}/samples/TalkerListener" -- talker "${DOMAIN_ID}" 21 rclsharp_interop_talker \
  >"${RCLSHARP_TALKER_LOG}" 2>&1 &
PIDS+=("$!")
interop_wait_for_log "Hello rclsharp" "${ROS_LISTENER_LOG}" 30
cleanup
PIDS=()

if command -v ros2 >/dev/null 2>&1; then
  ros2 daemon stop >/dev/null 2>&1 || true
fi

echo "== Fast DDS interop: ROS 2 talker -> rclsharp listener =="
dotnet run --no-build --project "${ROOT_DIR}/samples/TalkerListener" -- listener "${DOMAIN_ID}" 22 rclsharp_interop_listener \
  >"${RCLSHARP_LISTENER_LOG}" 2>&1 &
PIDS+=("$!")
sleep 3
"${ROS_TALKER[@]}" >"${ROS_TALKER_LOG}" 2>&1 &
PIDS+=("$!")
interop_wait_for_log "I heard: '.*Hello" "${RCLSHARP_LISTENER_LOG}" 30

echo "Fast DDS interop succeeded. Logs: ${LOG_DIR}"
