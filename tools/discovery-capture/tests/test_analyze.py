import pytest
from analyze import parse_tshark_csv, summarize_ports


SAMPLE_CSV = """1700000000.123456\t192.168.0.22\t239.255.0.1\t12345\t7400\t0x01
1700000000.234567\t192.168.0.20\t239.255.0.1\t54321\t7400\t0x01
1700000000.345678\t192.168.0.22\t239.255.0.1\t12345\t7410\t0x07
1700000001.000000\t192.168.0.22\t239.255.0.1\t12345\t7401\t0x15
"""


def test_parse_tshark_csv_basic():
    rows = parse_tshark_csv(SAMPLE_CSV)
    assert len(rows) == 4
    assert rows[0]["src"] == "192.168.0.22"
    assert rows[0]["dport"] == 7400
    assert rows[2]["sm_id"] == "0x07"


def test_summarize_ports():
    rows = parse_tshark_csv(SAMPLE_CSV)
    summary = summarize_ports(rows)
    assert summary[7400] == 2
    assert summary[7410] == 1
    assert summary[7401] == 1


def test_summarize_src():
    from analyze import summarize_src

    rows = parse_tshark_csv(SAMPLE_CSV)
    summary = summarize_src(rows)
    assert summary["192.168.0.22"] == 3
    assert summary["192.168.0.20"] == 1
