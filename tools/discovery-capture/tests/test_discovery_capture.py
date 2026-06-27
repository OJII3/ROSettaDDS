import pytest
from discovery_capture import parse_args


def test_parse_args_reliable_scenario():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-reliable-32",
        "--run-id", "20260627-180000",
    ])
    assert args.host_interface == "wlan0"
    assert args.android_device == "5HF6OVWCDECMJZ59"
    assert args.scenario == "unity-to-ros2-reliable-32"
    assert args.run_id == "20260627-180000"
    assert args.udp_portrange == "7400-12500"  # default


def test_parse_args_custom_portrange():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-reliable-32",
        "--run-id", "20260627-180000",
        "--udp-portrange", "7400-7500",
    ])
    assert args.udp_portrange == "7400-7500"


def test_parse_args_missing_android_device():
    with pytest.raises(SystemExit):
        parse_args([
            "--host-interface", "wlan0",
            "--scenario", "unity-to-ros2-reliable-32",
            "--run-id", "20260627-180000",
        ])
