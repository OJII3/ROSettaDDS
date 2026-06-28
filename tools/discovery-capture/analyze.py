import csv
import io
from collections import Counter
from typing import Dict, List


def parse_tshark_csv(text: str) -> List[Dict[str, object]]:
    rows: List[Dict[str, object]] = []
    reader = csv.reader(io.StringIO(text), delimiter="\t")
    for parts in reader:
        if len(parts) < 6:
            continue
        rows.append({
            "ts": parts[0],
            "src": parts[1],
            "dst": parts[2],
            "sport": int(parts[3]) if parts[3] else 0,
            "dport": int(parts[4]) if parts[4] else 0,
            "sm_id": parts[5],
        })
    return rows


def summarize_ports(rows: List[Dict[str, object]]) -> Dict[int, int]:
    return dict(Counter(r["dport"] for r in rows))


def summarize_src(rows: List[Dict[str, object]]) -> Dict[str, int]:
    return dict(Counter(r["src"] for r in rows))


def extract_pcap(pcap_path: str) -> List[Dict[str, object]]:
    import subprocess
    out = subprocess.check_output([
        "tshark", "-r", pcap_path, "-Y", "rtps",
        "-T", "fields",
        "-e", "frame.time_epoch",
        "-e", "ip.src", "-e", "ip.dst",
        "-e", "udp.srcport", "-e", "udp.dstport",
        "-e", "rtps.sm.id",
    ], text=True)
    return parse_tshark_csv(out)
