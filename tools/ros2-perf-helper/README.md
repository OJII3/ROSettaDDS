# rosettadds_ros2_perf_helper

ROS 2 helper node for ROSettaDDS Unity performance tests.

## Build

```sh
cd tools/ros2-perf-helper
colcon build
```

## Run

```sh
source install/setup.bash
ROS_LOCALHOST_ONLY=1 RMW_IMPLEMENTATION=rmw_fastrtps_cpp ROS_DOMAIN_ID=42 \
  ros2_perf_helper --mode sub --topic /rosettadds_perf --messages 1000 \
  --payload-bytes 1024 --rate-hz 0 --qos reliable \
  --ready-timeout-ms 5000 --idle-timeout-ms 5000
```

The helper writes JSON Lines to stdout and human-readable diagnostics to stderr.
