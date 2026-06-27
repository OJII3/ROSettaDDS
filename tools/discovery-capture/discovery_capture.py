import argparse


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


if __name__ == "__main__":
    args = parse_args()
    print(f"host={args.host_interface} android={args.android_device} "
          f"scenario={args.scenario} run_id={args.run_id}")
