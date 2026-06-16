# rosettadds_ros2_perf_helper

ROS 2 helper node for ROSettaDDS Unity performance tests.

## Build

Nix devShell (`flake.nix` の `rosEnv`) 配下では `ament_cmake_core` などが
CMake から解決できるため、`colcon build` がそのまま通る。

```sh
scripts/ros2/build_helper.sh        # nix develop / direnv 環境前提
```

手動で build する場合:

```sh
cd tools/ros2-perf-helper
colcon build
```

## Run

```sh
source install/setup.bash
ROS_LOCALHOST_ONLY=1 RMW_IMPLEMENTATION=rmw_fastrtps_cpp ROS_DOMAIN_ID=42 \
  ros2 run rosettadds_ros2_perf_helper ros2_perf_helper --mode sub \
  --topic /rosettadds_perf --messages 1000 \
  --payload-bytes 1024 --rate-hz 0 --qos reliable \
  --ready-timeout-ms 5000 --idle-timeout-ms 5000
```

The helper writes JSON Lines to stdout and human-readable diagnostics to stderr.

## Linux smoke

Unity Editor が無い Linux 環境でも helper 自体の振る舞いは
`scripts/ros2/verify_helper.sh` で 6 ケース確認できる。reliable / best\_effort
pub<->sub、1 pub vs 4 subs fanout、idle timeout、invalid arg、32 KiB payload を
すべて helper の stdout JSON Lines で検証する。

```sh
scripts/ros2/verify_helper.sh
```
