#!/usr/bin/env python3
"""
ROS 2 (rclpy) を使って CDR fixture バイナリを生成するスクリプト。
nix develop 環境で実行: python3 generate_fixtures.py

生成されたファイルはテストの WireBitExactTests で使用される。
"""

import os
from pathlib import Path

from rclpy.serialization import serialize_message
from builtin_interfaces.msg import Time
from std_msgs.msg import ColorRGBA, Header, String

FIXTURE_DIR = Path(os.path.dirname(__file__))


def write_fixture(name: str, data: bytes) -> None:
    path = FIXTURE_DIR / name
    path.write_bytes(data)
    print(f"{name}: {data.hex(' ')}")


def main() -> None:
    # std_msgs/String: "Hello World"
    write_fixture(
        "std_msgs_String.bin",
        serialize_message(String(data="Hello World")),
    )

    # std_msgs/String: empty
    write_fixture(
        "std_msgs_String_empty.bin",
        serialize_message(String(data="")),
    )

    # builtin_interfaces/Time: sec=1234567890, nanosec=123456789
    write_fixture(
        "builtin_interfaces_Time.bin",
        serialize_message(Time(sec=1234567890, nanosec=123456789)),
    )

    # std_msgs/Header: stamp=(1234567890, 123456789), frame_id="map"
    msg = Header()
    msg.stamp.sec = 1234567890
    msg.stamp.nanosec = 123456789
    msg.frame_id = "map"
    write_fixture("std_msgs_Header.bin", serialize_message(msg))

    # std_msgs/Header: empty
    msg = Header()
    msg.stamp.sec = 0
    msg.stamp.nanosec = 0
    msg.frame_id = ""
    write_fixture("std_msgs_Header_empty.bin", serialize_message(msg))

    # std_msgs/ColorRGBA: r=1.0, g=0.5, b=0.25, a=0.75
    write_fixture(
        "std_msgs_ColorRGBA.bin",
        serialize_message(ColorRGBA(r=1.0, g=0.5, b=0.25, a=0.75)),
    )


if __name__ == "__main__":
    main()
