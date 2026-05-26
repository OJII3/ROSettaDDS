#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT_DIR = Path(__file__).resolve().parents[2]
DEFAULT_RESULTS_PATH = ROOT_DIR / "artifacts" / "unity" / "editmode-results.xml"
DEFAULT_README_PATH = ROOT_DIR / "README.md"
START_MARKER = "<!-- rclsharp-local-performance:start -->"
END_MARKER = "<!-- rclsharp-local-performance:end -->"
PERFORMANCE_RESULT_PREFIX = "##performancetestresult2:"
PERFORMANCE_RUN_INFO_PREFIX = "##performancetestruninfo2:"
THROUGHPUT_MESSAGES_PATTERN = re.compile(
    r"^rclsharp\.throughput\.(?P<payload>\d+)B\.messages_per_second$"
)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Update the README local performance section from Unity EditMode test results."
    )
    parser.add_argument(
        "results",
        nargs="?",
        default=DEFAULT_RESULTS_PATH,
        type=Path,
        help="Unity EditMode NUnit XML result path.",
    )
    parser.add_argument(
        "readme",
        nargs="?",
        default=DEFAULT_README_PATH,
        type=Path,
        help="README path to update.",
    )
    args = parser.parse_args(argv)

    performance_results, run_info = load_unity_performance_data(args.results)
    sample_groups = collect_sample_groups(performance_results)
    section = render_section(sample_groups, run_info)
    update_readme(args.readme, section)
    print(f"Updated {args.readme} from {args.results}")
    return 0


def load_unity_performance_data(results_path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if not results_path.is_file():
        raise RuntimeError(f"Unity test result file was not found: {results_path}")

    root = ET.parse(results_path).getroot()
    performance_results: list[dict[str, Any]] = []
    run_info: dict[str, Any] | None = None

    for output in iter_output_text(root):
        for line in output.splitlines():
            if line.startswith(PERFORMANCE_RESULT_PREFIX):
                performance_results.append(json.loads(line[len(PERFORMANCE_RESULT_PREFIX) :]))
            elif line.startswith(PERFORMANCE_RUN_INFO_PREFIX):
                run_info = json.loads(line[len(PERFORMANCE_RUN_INFO_PREFIX) :])

    if not performance_results:
        raise RuntimeError(f"Unity performance result JSON was not found in {results_path}")
    if run_info is None:
        raise RuntimeError(f"Unity performance run info JSON was not found in {results_path}")

    return performance_results, run_info


def iter_output_text(root: ET.Element) -> list[str]:
    outputs: list[str] = []
    for element in root.iter():
        if element.tag.endswith("output") and element.text:
            outputs.append(element.text)
    return outputs


def collect_sample_groups(performance_results: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    groups: dict[str, dict[str, Any]] = {}
    for result in performance_results:
        for group in result.get("SampleGroups", []):
            name = group.get("Name")
            if isinstance(name, str):
                groups[name] = group
    return groups


def render_section(sample_groups: dict[str, dict[str, Any]], run_info: dict[str, Any]) -> str:
    payloads = sorted(
        int(match.group("payload"))
        for name in sample_groups
        if (match := THROUGHPUT_MESSAGES_PATTERN.match(name))
    )
    if not payloads:
        raise RuntimeError("Throughput sample groups were not found.")

    lines = [
        START_MARKER,
        "## ローカル性能計測結果",
        "",
        "Unity EditMode のローカル計測結果です。実行環境や同時負荷で変動します。",
        "",
        *render_run_info(run_info),
        "",
        "### Throughput",
        "",
        "| Payload | Median messages/sec | Median serialized MiB/sec | Median mean ms/message | Samples |",
        "| --- | ---: | ---: | ---: | ---: |",
    ]

    for payload in payloads:
        messages_per_second = sample_group(sample_groups, f"rclsharp.throughput.{payload}B.messages_per_second")
        serialized_bytes_per_second = sample_group(
            sample_groups, f"rclsharp.throughput.{payload}B.serialized_bytes_per_second"
        )
        mean_message_ms = sample_group(sample_groups, f"rclsharp.throughput.{payload}B.mean_message_ms")
        lines.append(
            "| "
            + " | ".join(
                [
                    f"{payload} B",
                    format_number(float(messages_per_second["Median"])),
                    format_number(float(serialized_bytes_per_second["Median"]) / (1024.0 * 1024.0)),
                    format_number(float(mean_message_ms["Median"])),
                    str(len(messages_per_second.get("Samples", []))),
                ]
            )
            + " |"
        )

    lines.extend(
        [
            "",
            "### Leak Guard",
            "",
            "| Metric | Final retained | Max retained |",
            "| --- | ---: | ---: |",
            "| Managed heap | "
            + format_bytes(sample_median(sample_groups, "rclsharp.leak.managed_heap_retained_bytes"))
            + " | "
            + format_bytes(sample_median(sample_groups, "rclsharp.leak.managed_heap_max_retained_bytes"))
            + " |",
            "| Unity mono used | "
            + format_bytes(sample_median(sample_groups, "rclsharp.leak.unity_mono_used_retained_bytes"))
            + " | "
            + format_bytes(sample_median(sample_groups, "rclsharp.leak.unity_mono_used_max_retained_bytes"))
            + " |",
            "",
            END_MARKER,
        ]
    )
    return "\n".join(lines)


def render_run_info(run_info: dict[str, Any]) -> list[str]:
    editor = dict_value(run_info, "Editor")
    player = dict_value(run_info, "Player")
    hardware = dict_value(run_info, "Hardware")

    return [
        f"- 計測日時: {format_run_datetime(run_info.get('Date'))}",
        f"- Unity: {text_value(editor, 'Version')} ({text_value(player, 'Platform')}, {text_value(player, 'ScriptingBackend')})",
        "- 実行環境: "
        + f"{text_value(hardware, 'OperatingSystem')}, "
        + f"{text_value(hardware, 'ProcessorType')}, "
        + f"{format_memory_mb(hardware.get('SystemMemorySizeMB'))} RAM",
    ]


def dict_value(value: dict[str, Any], key: str) -> dict[str, Any]:
    child = value.get(key)
    if not isinstance(child, dict):
        raise RuntimeError(f"Unity performance run info is missing {key}.")
    return child


def text_value(value: dict[str, Any], key: str) -> str:
    child = value.get(key)
    if child is None:
        raise RuntimeError(f"Unity performance run info is missing {key}.")
    return str(child)


def sample_group(sample_groups: dict[str, dict[str, Any]], name: str) -> dict[str, Any]:
    group = sample_groups.get(name)
    if group is None:
        raise RuntimeError(f"Unity performance sample group was not found: {name}")
    return group


def sample_median(sample_groups: dict[str, dict[str, Any]], name: str) -> float:
    return float(sample_group(sample_groups, name)["Median"])


def format_run_datetime(value: Any) -> str:
    if not isinstance(value, (int, float)):
        raise RuntimeError("Unity performance run info Date must be a Unix epoch millisecond value.")
    dt = datetime.fromtimestamp(float(value) / 1000.0, timezone.utc)
    return dt.strftime("%Y-%m-%d %H:%M:%S UTC")


def format_memory_mb(value: Any) -> str:
    if isinstance(value, (int, float)):
        return f"{int(value):,} MB"
    raise RuntimeError("Unity performance run info SystemMemorySizeMB must be numeric.")


def format_number(value: float) -> str:
    if abs(value) >= 1000.0:
        return f"{value:,.0f}"
    if abs(value) >= 10.0:
        return f"{value:,.2f}"
    return f"{value:,.4f}"


def format_bytes(value: float) -> str:
    if abs(value) >= 1024.0 * 1024.0:
        return f"{value / (1024.0 * 1024.0):,.2f} MiB"
    if abs(value) >= 1024.0:
        return f"{value / 1024.0:,.1f} KiB"
    return f"{value:,.0f} B"


def update_readme(readme_path: Path, section: str) -> None:
    if not readme_path.is_file():
        raise RuntimeError(f"README was not found: {readme_path}")

    text = readme_path.read_text(encoding="utf-8")
    text_without_old_section = remove_existing_section(text)
    next_text = text_without_old_section.rstrip() + "\n\n" + section.rstrip() + "\n"
    if next_text != text:
        readme_path.write_text(next_text, encoding="utf-8")


def remove_existing_section(text: str) -> str:
    start = text.find(START_MARKER)
    end = text.find(END_MARKER)
    if start == -1 and end == -1:
        return text
    if start == -1 or end == -1 or end < start:
        raise RuntimeError("README local performance markers are malformed.")
    return text[:start] + text[end + len(END_MARKER) :]


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(f"update_readme_performance.py: {error}", file=sys.stderr)
        raise SystemExit(1)
