# best-effort-8192 pcap 集計 (raw)

計測日時: Sat Jun 27 11:29:52 PM JST 2026
pcap: artifacts/discovery-capture/20260627-231205/host-best-effort_00001_20260627232823.pcap

## Packet counts
- Total packets: 1925
- RTPS packets: 1921
- Android→Host (192.168.0.22): 1821
- Host→Android (192.168.0.20): 100

## Android port distribution
   1800 12411
     15 12410
      5 12400
      1 12401

## Host port distribution
     88 12410
     12 12400

## helper 受信 (manifest.json から)
{
  "RunId": "20260627-142838",
  "Backend": "il2cpp",
  "BuildTarget": "Android",
  "CaptureFrames": 200,
  "Scenarios": [
    {
      "Name": "unity-to-ros2-best-effort-8192",
      "Direction": "unity_to_ros2",
      "MetricsPath": "/home/ojii3/src/github.com/ojii3/ROSettaDDS/artifacts/discovery-capture/20260627-231205/best-effort-8192/20260627-142838/unity-to-ros2-best-effort-8192/metrics.ndjson",
      "ProfilerPath": "/home/ojii3/src/github.com/ojii3/ROSettaDDS/artifacts/discovery-capture/20260627-231205/best-effort-8192/20260627-142838/unity-to-ros2-best-effort-8192/player.profiler.raw",
      "PlayerLogPath": "/home/ojii3/src/github.com/ojii3/ROSettaDDS/artifacts/discovery-capture/20260627-231205/best-effort-8192/20260627-142838/unity-to-ros2-best-effort-8192/player.log",
      "HelperStdoutPath": "/home/ojii3/src/github.com/ojii3/ROSettaDDS/artifacts/discovery-capture/20260627-231205/best-effort-8192/20260627-142838/unity-to-ros2-best-effort-8192/helper.stdout.ndjson",
      "HelperStderrPath": "/home/ojii3/src/github.com/ojii3/ROSettaDDS/artifacts/discovery-capture/20260627-231205/best-effort-8192/20260627-142838/unity-to-ros2-best-effort-8192/helper.stderr.log",
      "PlayerExitCode": 0,
      "HelperExitCode": 3
    }
  ]
}
### helper stdout
{"event":"ready","mode":"sub","topic":"/rosettadds_perf_unity_to_ros2_best_effort_8192"}
{"event":"done","received":162,"elapsed_ms":13262.6}
