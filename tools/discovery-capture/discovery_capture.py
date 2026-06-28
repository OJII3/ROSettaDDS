import argparse
from dataclasses import dataclass


@dataclass
class CapturePaths:
    host_pcap: str
    android_pcap: str
    host_log: str
    android_log: str
    clock_pre_host: str
    clock_pre_android: str


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="両側 capture 起動 + pcap pull ヘルパ"
    )
    parser.add_argument("--host-interface", required=True)
    parser.add_argument("--android-device", required=True)
    parser.add_argument("--scenario", required=True,
                        choices=["unity-to-ros2-reliable-32",
                                 "unity-to-ros2-best-effort-8192",
                                 "ros2-to-unity-reliable-32",
                                 "ros2-to-unity-best-effort-32k"])
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--udp-portrange", default="7400-12500")
    return parser.parse_args(argv)


def build_paths(args, root: str) -> CapturePaths:
    tag = "reliable" if "reliable" in args.scenario else "best-effort"
    return CapturePaths(
        host_pcap=f"{root}/host-{tag}.pcap",
        android_pcap=f"{root}/android-{tag}.pcap",
        host_log=f"{root}/host-tshark.log",
        android_log=f"{root}/android-tcpdump.log",
        clock_pre_host=f"{root}/host-clock-pre.txt",
        clock_pre_android=f"{root}/android-clock-pre.txt",
    )


if __name__ == "__main__":
    args = parse_args()
    print(f"host={args.host_interface} android={args.android_device} "
          f"scenario={args.scenario} run_id={args.run_id}")
