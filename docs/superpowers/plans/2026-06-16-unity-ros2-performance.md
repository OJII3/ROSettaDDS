# Unity ROS 2 Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unity PlayMode から ROS 2 Humble / Fast DDS の C++ helper node を起動し、同一マシン loopback の ROSettaDDS 性能指標を Unity Performance Testing に記録する。

**Architecture:** ROS 2 側は `tools/ros2-perf-helper` の C++ / rclcpp package として追加し、stdout の JSON Lines で Unity harness と同期する。Unity 側は専用 PlayMode test assembly `ROSettaDDS.UnityRos2Perf.Tests` に parser、process wrapper、performance scenario を閉じ込め、通常の PlayMode / Soak から分離する。

**Tech Stack:** C++17, ROS 2 Humble, rclcpp, std_msgs, Unity 6000.3 PlayMode tests, NUnit, Unity.PerformanceTesting, ROSettaDDS

---

## File Structure

- Create: `tools/ros2-perf-helper/package.xml`
  - ROS 2 package metadata。依存は `rclcpp` と `std_msgs` のみ。
- Create: `tools/ros2-perf-helper/CMakeLists.txt`
  - `ros2_perf_helper` executable を build / install する。
- Create: `tools/ros2-perf-helper/src/ros2_perf_helper.cpp`
  - `pub` / `sub` mode、CLI parsing、QoS 作成、JSON Lines 出力を持つ helper。
- Create: `tools/ros2-perf-helper/README.md`
  - helper の build / manual run 手順。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf.meta`
  - Unity folder meta。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef`
  - 専用 PlayMode performance test assembly。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef.meta`
  - asmdef meta。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperEvent.cs`
  - JSON Lines event model / parser。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcess.cs`
  - helper executable 解決、process 起動、stdout/stderr 収集、cleanup。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfScenarios.cs`
  - QoS / payload / fanout scenario 定義と metric 名生成。
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs`
  - Unity PlayMode performance scenarios。
- Create: corresponding `.meta` files under `Ros2Unity/Assets/Tests/Ros2Perf/`
  - Unity import 用。GUID は `uuidgen | tr '[:upper:]' '[:lower:]'` で生成する。
- Modify: `docs/unity-verification.md`
  - ROS 2 perf test の前提、build、実行、sample group を追記する。
- Modify: `docs/interop.md`
  - 疎通確認と性能計測の役割分担を追記する。

---

### Task 1: ROS 2 helper package skeleton

**Files:**
- Create: `tools/ros2-perf-helper/package.xml`
- Create: `tools/ros2-perf-helper/CMakeLists.txt`
- Create: `tools/ros2-perf-helper/README.md`
- Create: `tools/ros2-perf-helper/src/ros2_perf_helper.cpp`

- [ ] **Step 1: Write the initial helper package files**

Create `tools/ros2-perf-helper/package.xml`:

```xml
<?xml version="1.0"?>
<package format="3">
  <name>rosettadds_ros2_perf_helper</name>
  <version>0.0.1</version>
  <description>ROS 2 helper node for ROSettaDDS Unity performance measurements.</description>
  <maintainer email="ojii3@example.invalid">OJII3</maintainer>
  <license>MIT</license>

  <buildtool_depend>ament_cmake</buildtool_depend>
  <depend>rclcpp</depend>
  <depend>std_msgs</depend>

  <test_depend>ament_lint_auto</test_depend>
  <test_depend>ament_lint_common</test_depend>

  <export>
    <build_type>ament_cmake</build_type>
  </export>
</package>
```

Create `tools/ros2-perf-helper/CMakeLists.txt`:

```cmake
cmake_minimum_required(VERSION 3.8)
project(rosettadds_ros2_perf_helper)

if(CMAKE_COMPILER_IS_GNUCXX OR CMAKE_CXX_COMPILER_ID MATCHES "Clang")
  add_compile_options(-Wall -Wextra -Wpedantic)
endif()

find_package(ament_cmake REQUIRED)
find_package(rclcpp REQUIRED)
find_package(std_msgs REQUIRED)

add_executable(ros2_perf_helper src/ros2_perf_helper.cpp)
target_compile_features(ros2_perf_helper PRIVATE cxx_std_17)
ament_target_dependencies(ros2_perf_helper rclcpp std_msgs)

install(TARGETS ros2_perf_helper
  DESTINATION lib/${PROJECT_NAME})

ament_package()
```

Create `tools/ros2-perf-helper/README.md`:

```markdown
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
```

Create `tools/ros2-perf-helper/src/ros2_perf_helper.cpp` with a stub that intentionally fails the validation smoke:

```cpp
#include <iostream>

int main(int argc, char** argv)
{
  (void)argc;
  (void)argv;
  std::cerr << "ros2_perf_helper is not implemented yet" << std::endl;
  return 2;
}
```

- [ ] **Step 2: Run build to verify skeleton compiles**

Run:

```bash
cd tools/ros2-perf-helper
colcon build
```

Expected: PASS. `install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper` exists.

- [ ] **Step 3: Run validation smoke to verify behavior is still missing**

Run:

```bash
cd tools/ros2-perf-helper
./install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper --mode sub
```

Expected: exit code `2`, stderr contains `not implemented yet`.

- [ ] **Step 4: Commit**

```bash
git add tools/ros2-perf-helper
git commit -m "feat: ROS 2 性能計測 helper の骨組みを追加"
```

---

### Task 2: ROS 2 helper CLI and JSON event contract

**Files:**
- Modify: `tools/ros2-perf-helper/src/ros2_perf_helper.cpp`

- [ ] **Step 1: Replace helper stub with CLI implementation**

Replace `tools/ros2-perf-helper/src/ros2_perf_helper.cpp` with:

```cpp
#include <chrono>
#include <algorithm>
#include <cstdlib>
#include <iostream>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>

#include "rclcpp/rclcpp.hpp"
#include "std_msgs/msg/string.hpp"

using namespace std::chrono_literals;

namespace
{
struct Options
{
  std::string mode;
  std::string topic;
  int messages = 0;
  int payload_bytes = 0;
  double rate_hz = 0.0;
  std::string qos = "reliable";
  int ready_timeout_ms = 5000;
  int idle_timeout_ms = 5000;
};

std::string json_escape(const std::string& value)
{
  std::ostringstream output;
  for (char c : value) {
    switch (c) {
      case '\\': output << "\\\\"; break;
      case '"': output << "\\\""; break;
      case '\n': output << "\\n"; break;
      case '\r': output << "\\r"; break;
      case '\t': output << "\\t"; break;
      default: output << c; break;
    }
  }
  return output.str();
}

void write_error(const std::string& message)
{
  std::cout << "{\"event\":\"error\",\"message\":\"" << json_escape(message) << "\"}" << std::endl;
  std::cerr << message << std::endl;
}

void write_ready(const Options& options)
{
  std::cout << "{\"event\":\"ready\",\"mode\":\"" << json_escape(options.mode)
            << "\",\"topic\":\"" << json_escape(options.topic) << "\"}" << std::endl;
}

void write_progress(const std::string& key, int value)
{
  std::cout << "{\"event\":\"progress\",\"" << key << "\":" << value << "}" << std::endl;
}

void write_done_received(int received, double elapsed_ms)
{
  std::cout << "{\"event\":\"done\",\"received\":" << received
            << ",\"elapsed_ms\":" << elapsed_ms << "}" << std::endl;
}

void write_done_sent(int sent, double elapsed_ms)
{
  std::cout << "{\"event\":\"done\",\"sent\":" << sent
            << ",\"elapsed_ms\":" << elapsed_ms << "}" << std::endl;
}

std::string require_value(int& index, int argc, char** argv)
{
  if (index + 1 >= argc) {
    throw std::invalid_argument(std::string("missing value for ") + argv[index]);
  }
  ++index;
  return argv[index];
}

Options parse_options(int argc, char** argv)
{
  Options options;
  for (int i = 1; i < argc; ++i) {
    std::string arg = argv[i];
    if (arg == "--mode") options.mode = require_value(i, argc, argv);
    else if (arg == "--topic") options.topic = require_value(i, argc, argv);
    else if (arg == "--messages") options.messages = std::stoi(require_value(i, argc, argv));
    else if (arg == "--payload-bytes") options.payload_bytes = std::stoi(require_value(i, argc, argv));
    else if (arg == "--rate-hz") options.rate_hz = std::stod(require_value(i, argc, argv));
    else if (arg == "--qos") options.qos = require_value(i, argc, argv);
    else if (arg == "--ready-timeout-ms") options.ready_timeout_ms = std::stoi(require_value(i, argc, argv));
    else if (arg == "--idle-timeout-ms") options.idle_timeout_ms = std::stoi(require_value(i, argc, argv));
    else throw std::invalid_argument("unknown argument: " + arg);
  }

  if (options.mode != "pub" && options.mode != "sub") throw std::invalid_argument("--mode must be pub or sub");
  if (options.topic.empty() || options.topic[0] != '/') throw std::invalid_argument("--topic must be an absolute ROS topic");
  if (options.messages <= 0) throw std::invalid_argument("--messages must be positive");
  if (options.payload_bytes <= 0) throw std::invalid_argument("--payload-bytes must be positive");
  if (options.rate_hz < 0.0) throw std::invalid_argument("--rate-hz must be zero or positive");
  if (options.qos != "reliable" && options.qos != "best_effort") {
    throw std::invalid_argument("--qos must be reliable or best_effort");
  }
  if (options.ready_timeout_ms <= 0) throw std::invalid_argument("--ready-timeout-ms must be positive");
  if (options.idle_timeout_ms <= 0) throw std::invalid_argument("--idle-timeout-ms must be positive");
  return options;
}

rclcpp::QoS make_qos(const Options& options)
{
  auto qos = rclcpp::QoS(rclcpp::KeepLast(static_cast<size_t>(std::max(1, options.messages))));
  if (options.qos == "best_effort") {
    qos.best_effort();
  } else {
    qos.reliable();
  }
  qos.durability_volatile();
  return qos;
}

std_msgs::msg::String make_message(const Options& options, int sequence)
{
  std_msgs::msg::String msg;
  std::string prefix = "rosettadds-perf-" + std::to_string(sequence) + "-";
  if (static_cast<int>(prefix.size()) >= options.payload_bytes) {
    msg.data = prefix.substr(0, static_cast<size_t>(options.payload_bytes));
  } else {
    msg.data = prefix + std::string(static_cast<size_t>(options.payload_bytes - prefix.size()), 'x');
  }
  return msg;
}

int run_subscriber(const Options& options)
{
  auto node = rclcpp::Node::make_shared("rosettadds_perf_sub");
  int received = 0;
  auto first_receive = std::optional<std::chrono::steady_clock::time_point>();
  auto last_receive = std::chrono::steady_clock::now();

  auto subscription = node->create_subscription<std_msgs::msg::String>(
    options.topic,
    make_qos(options),
    [&](std_msgs::msg::String::ConstSharedPtr msg) {
      (void)msg;
      if (!first_receive.has_value()) first_receive = std::chrono::steady_clock::now();
      last_receive = std::chrono::steady_clock::now();
      ++received;
      if (received % 1000 == 0) write_progress("received", received);
    });

  write_ready(options);
  auto start = std::chrono::steady_clock::now();
  auto idle_timeout = std::chrono::milliseconds(options.idle_timeout_ms);
  while (rclcpp::ok() && received < options.messages) {
    rclcpp::spin_some(node);
    auto now = std::chrono::steady_clock::now();
    if (first_receive.has_value() && now - last_receive > idle_timeout) {
      break;
    }
    if (!first_receive.has_value() && now - start > std::chrono::milliseconds(options.ready_timeout_ms)) {
      break;
    }
    std::this_thread::sleep_for(1ms);
  }

  auto end = std::chrono::steady_clock::now();
  double elapsed_ms = std::chrono::duration<double, std::milli>(end - start).count();
  write_done_received(received, elapsed_ms);
  return received == options.messages ? 0 : 3;
}

int run_publisher(const Options& options)
{
  auto node = rclcpp::Node::make_shared("rosettadds_perf_pub");
  auto publisher = node->create_publisher<std_msgs::msg::String>(options.topic, make_qos(options));
  write_ready(options);

  auto start = std::chrono::steady_clock::now();
  auto interval = options.rate_hz > 0.0
    ? std::chrono::duration<double>(1.0 / options.rate_hz)
    : std::chrono::duration<double>(0.0);

  for (int i = 0; rclcpp::ok() && i < options.messages; ++i) {
    publisher->publish(make_message(options, i));
    rclcpp::spin_some(node);
    if ((i + 1) % 1000 == 0) write_progress("sent", i + 1);
    if (interval.count() > 0.0) std::this_thread::sleep_for(interval);
  }

  auto end = std::chrono::steady_clock::now();
  double elapsed_ms = std::chrono::duration<double, std::milli>(end - start).count();
  write_done_sent(options.messages, elapsed_ms);
  return 0;
}
}

int main(int argc, char** argv)
{
  try {
    Options options = parse_options(argc, argv);
    rclcpp::init(argc, argv);
    int result = options.mode == "sub" ? run_subscriber(options) : run_publisher(options);
    rclcpp::shutdown();
    return result;
  } catch (const std::exception& ex) {
    write_error(ex.what());
    return 2;
  }
}
```

- [ ] **Step 2: Build helper**

Run:

```bash
cd tools/ros2-perf-helper
colcon build
```

Expected: PASS.

- [ ] **Step 3: Verify CLI validation fails correctly**

Run:

```bash
cd tools/ros2-perf-helper
./install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper --mode sub
```

Expected: exit code `2`, stdout contains `{"event":"error"` and `--topic must be an absolute ROS topic`.

- [ ] **Step 4: Verify publisher emits ready and done**

Run:

```bash
cd tools/ros2-perf-helper
ROS_LOCALHOST_ONLY=1 RMW_IMPLEMENTATION=rmw_fastrtps_cpp ROS_DOMAIN_ID=251 \
  ./install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper \
  --mode pub --topic /rosettadds_helper_smoke --messages 3 --payload-bytes 32 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 1000 --idle-timeout-ms 1000
```

Expected: exit code `0`, stdout has a `ready` event and a `done` event with `"sent":3`.

- [ ] **Step 5: Verify subscriber timeout emits done with partial count**

Run:

```bash
cd tools/ros2-perf-helper
ROS_LOCALHOST_ONLY=1 RMW_IMPLEMENTATION=rmw_fastrtps_cpp ROS_DOMAIN_ID=252 \
  ./install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper \
  --mode sub --topic /rosettadds_helper_smoke --messages 3 --payload-bytes 32 \
  --rate-hz 0 --qos reliable --ready-timeout-ms 250 --idle-timeout-ms 250
```

Expected: exit code `3`, stdout has `ready` and `done` with `"received":0`.

- [ ] **Step 6: Commit**

```bash
git add tools/ros2-perf-helper/src/ros2_perf_helper.cpp
git commit -m "feat: ROS 2 性能計測 helper の JSON 契約を実装"
```

---

### Task 3: Unity Ros2Perf assembly and event parser

**Files:**
- Create: `Ros2Unity/Assets/Tests/Ros2Perf.meta`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef.meta`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperEvent.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperEvent.cs.meta`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperParserTests.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperParserTests.cs.meta`

- [ ] **Step 1: Create Unity folder and asmdef**

Create `Ros2Unity/Assets/Tests/Ros2Perf.meta`:

```yaml
fileFormatVersion: 2
guid: 35cfe701b6ae4a8fbc1b09f0b3bbc10c
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef`:

```json
{
    "name": "ROSettaDDS.UnityRos2Perf.Tests",
    "rootNamespace": "ROSettaDDS.UnityRos2Perf.Tests",
    "references": [
        "ROSettaDDS",
        "Unity.PerformanceTesting",
        "UnityEngine.TestRunner"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDS.UnityRos2Perf.Tests.asmdef.meta`:

```yaml
fileFormatVersion: 2
guid: 15ef9ad2a3224e6bb789043e5a226c18
AssemblyDefinitionImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 2: Write failing parser tests**

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperParserTests.cs`:

```csharp
using NUnit.Framework;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class Ros2PerfHelperParserTests
    {
        [Test]
        public void TryParse_は_ready_event_を読む()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"ready\",\"mode\":\"sub\",\"topic\":\"/chatter\"}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Ready, parsed.Kind);
            Assert.AreEqual("sub", parsed.Mode);
            Assert.AreEqual("/chatter", parsed.Topic);
        }

        [Test]
        public void TryParse_は_done_event_の_received_と_elapsed_ms_を読む()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"done\",\"received\":42,\"elapsed_ms\":12.5}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Done, parsed.Kind);
            Assert.AreEqual(42, parsed.Received);
            Assert.AreEqual(12.5d, parsed.ElapsedMilliseconds);
        }

        [Test]
        public void TryParse_は_unknown_event_を失敗させる()
        {
            Assert.IsFalse(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"mystery\"}",
                out _,
                out var error));

            StringAssert.Contains("unknown event", error);
        }
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperParserTests.cs.meta`:

```yaml
fileFormatVersion: 2
guid: a1ae31c6b70a4f1087ba5da39466c992
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 3: Run parser tests and verify they fail**

Run:

```bash
scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value Ros2PerfHelperParserTests
```

Expected: FAIL because `Ros2PerfHelperEvent` is not defined.

- [ ] **Step 4: Implement parser**

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperEvent.cs`:

```csharp
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal enum Ros2PerfHelperEventKind
    {
        Ready,
        Progress,
        Done,
        Error,
    }

    internal readonly struct Ros2PerfHelperEvent
    {
        private static readonly Regex StringPropertyPattern = new Regex(
            "\"(?<name>[^\"]+)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.Compiled);

        private static readonly Regex NumberPropertyPattern = new Regex(
            "\"(?<name>[^\"]+)\"\\s*:\\s*(?<value>-?[0-9]+(?:\\.[0-9]+)?)",
            RegexOptions.Compiled);

        private Ros2PerfHelperEvent(
            Ros2PerfHelperEventKind kind,
            string mode,
            string topic,
            string message,
            int received,
            int sent,
            double elapsedMilliseconds)
        {
            Kind = kind;
            Mode = mode;
            Topic = topic;
            Message = message;
            Received = received;
            Sent = sent;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        internal Ros2PerfHelperEventKind Kind { get; }
        internal string Mode { get; }
        internal string Topic { get; }
        internal string Message { get; }
        internal int Received { get; }
        internal int Sent { get; }
        internal double ElapsedMilliseconds { get; }

        internal static bool TryParse(string line, out Ros2PerfHelperEvent parsed, out string error)
        {
            parsed = default;
            error = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                error = "empty JSON line";
                return false;
            }

            string eventName = ReadString(line, "event");
            if (eventName == null)
            {
                error = "missing event property";
                return false;
            }

            Ros2PerfHelperEventKind kind;
            switch (eventName)
            {
                case "ready":
                    kind = Ros2PerfHelperEventKind.Ready;
                    break;
                case "progress":
                    kind = Ros2PerfHelperEventKind.Progress;
                    break;
                case "done":
                    kind = Ros2PerfHelperEventKind.Done;
                    break;
                case "error":
                    kind = Ros2PerfHelperEventKind.Error;
                    break;
                default:
                    error = "unknown event: " + eventName;
                    return false;
            }

            parsed = new Ros2PerfHelperEvent(
                kind,
                ReadString(line, "mode"),
                ReadString(line, "topic"),
                ReadString(line, "message"),
                ReadInt(line, "received"),
                ReadInt(line, "sent"),
                ReadDouble(line, "elapsed_ms"));
            return true;
        }

        private static string ReadString(string line, string name)
        {
            foreach (Match match in StringPropertyPattern.Matches(line))
            {
                if (match.Groups["name"].Value == name)
                {
                    return Unescape(match.Groups["value"].Value);
                }
            }
            return null;
        }

        private static int ReadInt(string line, string name)
        {
            string value = ReadNumber(line, name);
            return value == null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
        }

        private static double ReadDouble(string line, string name)
        {
            string value = ReadNumber(line, name);
            return value == null ? 0d : double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string ReadNumber(string line, string name)
        {
            foreach (Match match in NumberPropertyPattern.Matches(line))
            {
                if (match.Groups["name"].Value == name)
                {
                    return match.Groups["value"].Value;
                }
            }
            return null;
        }

        private static string Unescape(string value)
            => value.Replace("\\\"", "\"")
                    .Replace("\\\\", "\\")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t");
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperEvent.cs.meta`:

```yaml
fileFormatVersion: 2
guid: 71c41034f71b412f9658e5a8a2f62b25
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 5: Run parser tests and verify they pass**

Run:

```bash
scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value Ros2PerfHelperParserTests
```

Expected: PASS.

- [ ] **Step 6: Check Unity meta files**

Run:

```bash
.github/scripts/check_unity_meta.sh
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Ros2Unity/Assets/Tests/Ros2Perf Ros2Unity/Assets/Tests/Ros2Perf.meta
git commit -m "feat: Unity ROS 2 性能計測 parser を追加"
```

---

### Task 4: Unity helper process wrapper

**Files:**
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcess.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcess.cs.meta`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcessTests.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcessTests.cs.meta`

- [ ] **Step 1: Write failing path resolution tests**

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcessTests.cs`:

```csharp
using NUnit.Framework;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class Ros2PerfHelperProcessTests
    {
        [Test]
        public void ResolveExecutable_は_env_を優先する()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, "/tmp/ros2_perf_helper");
                Assert.AreEqual("/tmp/ros2_perf_helper", Ros2PerfHelperProcess.ResolveExecutablePath());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, original);
            }
        }
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcessTests.cs.meta`:

```yaml
fileFormatVersion: 2
guid: fcd69e451bfb411784d67debba2d9a6d
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 2: Run process tests and verify they fail**

Run:

```bash
scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value Ros2PerfHelperProcessTests
```

Expected: FAIL because `Ros2PerfHelperProcess` is not defined.

- [ ] **Step 3: Implement process wrapper**

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcess.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal sealed class Ros2PerfHelperProcess : IDisposable
    {
        private const string HelperEnvKey = "ROSETTADDS_ROS2_PERF_HELPER";
        private readonly Process _process;
        private readonly List<string> _stdout = new List<string>();
        private readonly List<string> _stderr = new List<string>();
        private readonly object _gate = new object();

        private Ros2PerfHelperProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_gate) _stdout.Add(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_gate) _stderr.Add(e.Data);
            };
        }

        internal static string ResolveExecutablePath()
        {
            string fromEnv = Environment.GetEnvironmentVariable(HelperEnvKey);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }

            string cwd = Directory.GetCurrentDirectory();
            string candidate = Path.GetFullPath(Path.Combine(
                cwd,
                "..",
                "tools",
                "ros2-perf-helper",
                "install",
                "rosettadds_ros2_perf_helper",
                "lib",
                "rosettadds_ros2_perf_helper",
                "ros2_perf_helper"));
            return candidate;
        }

        internal static bool IsAvailable()
            => File.Exists(ResolveExecutablePath());

        internal static Ros2PerfHelperProcess Start(
            string arguments,
            int domainId,
            string qos)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveExecutablePath(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["ROS_LOCALHOST_ONLY"] = "1";
            startInfo.Environment["RMW_IMPLEMENTATION"] = "rmw_fastrtps_cpp";
            startInfo.Environment["ROS_DOMAIN_ID"] = domainId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var wrapper = new Ros2PerfHelperProcess(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return wrapper;
        }

        internal bool TryWaitForEvent(
            Ros2PerfHelperEventKind kind,
            TimeSpan timeout,
            out Ros2PerfHelperEvent found,
            out string error)
        {
            var stopwatch = Stopwatch.StartNew();
            int parsedIndex = 0;
            while (stopwatch.Elapsed < timeout)
            {
                List<string> snapshot;
                lock (_gate)
                {
                    snapshot = new List<string>(_stdout);
                }

                for (; parsedIndex < snapshot.Count; parsedIndex++)
                {
                    if (!Ros2PerfHelperEvent.TryParse(snapshot[parsedIndex], out var parsed, out error))
                    {
                        found = default;
                        return false;
                    }
                    if (parsed.Kind == Ros2PerfHelperEventKind.Error)
                    {
                        found = parsed;
                        error = parsed.Message;
                        return false;
                    }
                    if (parsed.Kind == kind)
                    {
                        found = parsed;
                        error = null;
                        return true;
                    }
                }
                Thread.Sleep(10);
            }

            found = default;
            error = "Timed out waiting for " + kind + ". Output tail:\n" + OutputTail();
            return false;
        }

        internal string OutputTail()
        {
            lock (_gate)
            {
                var builder = new StringBuilder();
                builder.AppendLine("stdout:");
                AppendTail(builder, _stdout);
                builder.AppendLine("stderr:");
                AppendTail(builder, _stderr);
                return builder.ToString();
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static void AppendTail(StringBuilder builder, List<string> lines)
        {
            int start = Math.Max(0, lines.Count - 20);
            for (int i = start; i < lines.Count; i++)
            {
                builder.AppendLine(lines[i]);
            }
        }
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfHelperProcess.cs.meta`:

```yaml
fileFormatVersion: 2
guid: aef93fd80c7c41f9b47667e0a8ba796f
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 4: Run process tests and verify they pass**

Run:

```bash
scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value Ros2PerfHelperProcessTests
```

Expected: PASS.

- [ ] **Step 5: Check Unity meta files**

Run:

```bash
.github/scripts/check_unity_meta.sh
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Ros2Unity/Assets/Tests/Ros2Perf
git commit -m "feat: Unity ROS 2 helper process wrapper を追加"
```

---

### Task 5: Unity ROS 2 performance scenarios

**Files:**
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfScenarios.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfScenarios.cs.meta`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs`
- Create: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs.meta`

- [ ] **Step 1: Add scenario definitions**

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfScenarios.cs`:

```csharp
namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal enum Ros2PerfDirection
    {
        UnityToRos2,
        Ros2ToUnity,
    }

    internal enum Ros2PerfQos
    {
        Reliable,
        BestEffort,
    }

    internal readonly struct Ros2PerfScenario
    {
        internal Ros2PerfScenario(Ros2PerfDirection direction, Ros2PerfQos qos, int payloadBytes, int fanout, int messageCount)
        {
            Direction = direction;
            Qos = qos;
            PayloadBytes = payloadBytes;
            Fanout = fanout;
            MessageCount = messageCount;
        }

        internal Ros2PerfDirection Direction { get; }
        internal Ros2PerfQos Qos { get; }
        internal int PayloadBytes { get; }
        internal int Fanout { get; }
        internal int MessageCount { get; }

        internal string QosArgument => Qos == Ros2PerfQos.Reliable ? "reliable" : "best_effort";
        internal string DirectionName => Direction == Ros2PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity";
        internal string FanoutName => Direction == Ros2PerfDirection.UnityToRos2 ? "subscribers_" + Fanout : "publishers_" + Fanout;
        internal string GroupPrefix => "rosettadds.ros2perf." + DirectionName + "." + QosArgument + "." + PayloadBytes + "B." + FanoutName + ".";
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/Ros2PerfScenarios.cs.meta`:

```yaml
fileFormatVersion: 2
guid: c76ea8f5cead4b3ea863d7b1cecd5856
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 2: Add performance tests**

Create `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using Unity.PerformanceTesting;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class ROSettaDDSUnityRos2PerfTests
    {
        private const int BaseDomainId = 180;
        private static int s_domainSequence;
        private static int s_topicSequence;

        private static readonly Ros2PerfScenario[] Scenarios =
        {
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.Reliable, 32, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.Reliable, 1024, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.BestEffort, 8192, 2, 200),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.Reliable, 32, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.Reliable, 1024, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.BestEffort, 8192, 2, 200),
        };

        [UnityTest]
        [Performance]
        public IEnumerator ROS_2_loopback_perf_を記録する()
        {
            if (!Ros2PerfHelperProcess.IsAvailable())
            {
                Assert.Ignore("ROS 2 perf helper not found: " + Ros2PerfHelperProcess.ResolveExecutablePath());
            }

            for (int i = 0; i < Scenarios.Length; i++)
            {
                yield return RunScenario(Scenarios[i]);
            }
        }

        private static IEnumerator RunScenario(Ros2PerfScenario scenario)
        {
            if (scenario.Direction == Ros2PerfDirection.UnityToRos2)
            {
                yield return RunUnityToRos2(scenario);
            }
            else
            {
                yield return RunRos2ToUnity(scenario);
            }
        }

        private static IEnumerator RunUnityToRos2(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            var helpers = new List<Ros2PerfHelperProcess>();
            try
            {
                for (int i = 0; i < scenario.Fanout; i++)
                {
                    string args = "--mode sub --topic " + topic
                        + " --messages " + scenario.MessageCount
                        + " --payload-bytes " + scenario.PayloadBytes
                        + " --rate-hz 0 --qos " + scenario.QosArgument
                        + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
                    helpers.Add(Ros2PerfHelperProcess.Start(args, domainId, scenario.QosArgument));
                }

                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Ready, TimeSpan.FromSeconds(10), out _, out var error), error);
                }

                ForceFullCollection();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                long monoBefore = Profiler.GetMonoUsedSizeLong();

                using var participant = CreateParticipant(domainId, "unity_pub");
                using var publisher = participant.CreatePublisher<StringMessage>(
                    topic,
                    StringMessageSerializer.Instance,
                    ToReliability(scenario.Qos),
                    DurabilityQos.Volatile);
                participant.Start();

                var message = CreatePayloadMessage(scenario.PayloadBytes);
                int serializedBytes = publisher.SerializeWithEncapsulation(message).Length;
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < scenario.MessageCount; i++)
                {
                    publisher.PublishAsync(message).GetAwaiter().GetResult();
                }

                int received = 0;
                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Done, TimeSpan.FromSeconds(20), out var done, out var error), error);
                    received += done.Received;
                }
                stopwatch.Stop();

                ForceFullCollection();
                RecordMetrics(scenario, stopwatch.Elapsed, scenario.MessageCount * scenario.Fanout, serializedBytes, managedBefore, monoBefore);
                Assert.AreEqual(scenario.MessageCount * scenario.Fanout, received);
            }
            finally
            {
                for (int i = helpers.Count - 1; i >= 0; i--) helpers[i].Dispose();
            }
            yield return null;
        }

        private static IEnumerator RunRos2ToUnity(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            int received = 0;
            var helpers = new List<Ros2PerfHelperProcess>();

            ForceFullCollection();
            long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
            long monoBefore = Profiler.GetMonoUsedSizeLong();

            using var participant = CreateParticipant(domainId, "unity_sub");
            using var subscription = participant.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                _ => Interlocked.Increment(ref received),
                reliability: ToReliability(scenario.Qos));
            participant.Start();

            try
            {
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < scenario.Fanout; i++)
                {
                    string args = "--mode pub --topic " + topic
                        + " --messages " + scenario.MessageCount
                        + " --payload-bytes " + scenario.PayloadBytes
                        + " --rate-hz 0 --qos " + scenario.QosArgument
                        + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
                    helpers.Add(Ros2PerfHelperProcess.Start(args, domainId, scenario.QosArgument));
                }

                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Done, TimeSpan.FromSeconds(20), out _, out var error), error);
                }

                int expected = scenario.MessageCount * scenario.Fanout;
                yield return WaitUntil(() => Volatile.Read(ref received) >= expected, TimeSpan.FromSeconds(20));
                stopwatch.Stop();

                ForceFullCollection();
                RecordMetrics(scenario, stopwatch.Elapsed, expected, scenario.PayloadBytes + 4, managedBefore, monoBefore);
                Assert.AreEqual(expected, Volatile.Read(ref received));
            }
            finally
            {
                for (int i = helpers.Count - 1; i >= 0; i--) helpers[i].Dispose();
            }
        }

        private static DomainParticipant CreateParticipant(int domainId, string entityName)
            => new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = domainId,
                ParticipantId = 0,
                EntityName = "rosettadds_ros2_perf_" + entityName,
                LocalhostOnly = true,
                SpdpInterval = TimeSpan.FromMilliseconds(100),
                SedpInterval = TimeSpan.FromMilliseconds(100),
                UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(100),
            });

        private static ReliabilityQos ToReliability(Ros2PerfQos qos)
            => qos == Ros2PerfQos.BestEffort ? ReliabilityQos.BestEffort : ReliabilityQos.Reliable;

        private static int NextDomainId()
            => BaseDomainId + Interlocked.Increment(ref s_domainSequence);

        private static StringMessage CreatePayloadMessage(int payloadBytes)
        {
            string prefix = "unity-";
            string data = prefix + new string('x', Math.Max(1, payloadBytes - prefix.Length));
            return new StringMessage(data);
        }

        private static void RecordMetrics(
            Ros2PerfScenario scenario,
            TimeSpan elapsed,
            int deliveredMessages,
            int serializedBytesPerMessage,
            long managedBefore,
            long monoBefore)
        {
            double elapsedMs = Math.Max(0.001d, elapsed.TotalMilliseconds);
            double elapsedSeconds = Math.Max(0.000001d, elapsed.TotalSeconds);
            string prefix = scenario.GroupPrefix;
            Measure.Custom(new SampleGroup(prefix + "elapsed_ms", SampleUnit.Millisecond, false), elapsedMs);
            Measure.Custom(new SampleGroup(prefix + "messages_per_second", SampleUnit.Undefined, true), deliveredMessages / elapsedSeconds);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_second", SampleUnit.Undefined, true), deliveredMessages * serializedBytesPerMessage / elapsedSeconds);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_message", SampleUnit.Byte, false), serializedBytesPerMessage);
            Measure.Custom(new SampleGroup(prefix + "managed_heap_delta_bytes", SampleUnit.Byte, false), PositiveDelta(GC.GetTotalMemory(forceFullCollection: true), managedBefore));
            Measure.Custom(new SampleGroup(prefix + "unity_mono_used_delta_bytes", SampleUnit.Byte, false), PositiveDelta(Profiler.GetMonoUsedSizeLong(), monoBefore));
        }

        private static IEnumerator WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + timeout.TotalSeconds;
            while (UnityEngine.Time.realtimeSinceStartupAsDouble < deadline && !condition())
            {
                yield return null;
            }
        }

        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static long PositiveDelta(long after, long before)
            => Math.Max(0L, after - before);
    }
}
```

Create `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs.meta`:

```yaml
fileFormatVersion: 2
guid: f28fab5eda0c47949317338f6f3108c5
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

- [ ] **Step 3: Run Ros2Perf assembly without helper and verify it skips**

Run:

```bash
ROSETTADDS_ROS2_PERF_HELPER=/path/that/does/not/exist \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

Expected: test result is skipped/ignored with message containing `ROS 2 perf helper not found`.

- [ ] **Step 4: Run Ros2Perf assembly with helper**

Run:

```bash
ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper" \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

Expected: PASS when ROS 2 Humble / Fast DDS is sourced and helper is built. `artifacts/unity/playmode-results.xml` contains `rosettadds.ros2perf.` sample groups.

- [ ] **Step 5: Check Unity meta files**

Run:

```bash
.github/scripts/check_unity_meta.sh
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Ros2Unity/Assets/Tests/Ros2Perf
git commit -m "feat: Unity ROS 2 性能計測シナリオを追加"
```

---

### Task 6: Documentation

**Files:**
- Modify: `docs/unity-verification.md`
- Modify: `docs/interop.md`

- [ ] **Step 1: Update Unity verification docs**

Add this section after the existing PlayMode / Soak execution section in `docs/unity-verification.md`:

```markdown
## ROS 2 performance tests

`ROSettaDDS.UnityRos2Perf.Tests` は Unity PlayMode から ROS 2 C++ helper process を起動し、
同一マシン loopback の Fast DDS 相互通信性能を記録する。通常の PlayMode / Soak には含めず、
明示指定したときだけ実行する。

前提:

- ROS 2 Humble が source 済みであること
- `rmw_fastrtps_cpp` が利用できること
- `tools/ros2-perf-helper` が build 済みであること

helper build:

```sh
cd tools/ros2-perf-helper
colcon build
```

実行:

```sh
ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper" \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

この test は `ROS_LOCALHOST_ONLY=1`、`RMW_IMPLEMENTATION=rmw_fastrtps_cpp`、
scenario ごとの `ROS_DOMAIN_ID` を helper process に設定する。helper が見つからない環境では
`Assert.Ignore` し、通常の Unity 検証を壊さない。

主な sample group:

- `rosettadds.ros2perf.unity_to_ros2.<qos>.<payload>B.subscribers_<n>.elapsed_ms`
- `rosettadds.ros2perf.unity_to_ros2.<qos>.<payload>B.subscribers_<n>.messages_per_second`
- `rosettadds.ros2perf.ros2_to_unity.<qos>.<payload>B.publishers_<n>.elapsed_ms`
- `rosettadds.ros2perf.ros2_to_unity.<qos>.<payload>B.publishers_<n>.messages_per_second`
- `rosettadds.ros2perf.*.managed_heap_delta_bytes`
- `rosettadds.ros2perf.*.unity_mono_used_delta_bytes`
```

- [ ] **Step 2: Update interop docs**

Add this section near the end of `docs/interop.md` before "次に追加する検証":

```markdown
## Unity ROS 2 performance tests

疎通確認は `demo_nodes_cpp` や ROS 2 CLI で実 message の到達を確認する。
性能計測は別系統として、`tools/ros2-perf-helper` と
`ROSettaDDS.UnityRos2Perf.Tests` を使う。

性能計測では Unity PlayMode test が C++ helper process を起動し、
JSON Lines の `ready` / `done` event で同期する。対象は Humble + Fast DDS
(`rmw_fastrtps_cpp`) の同一マシン loopback 通信で、マシン間通信や Cyclone DDS は初期対象外。
```

- [ ] **Step 3: Verify docs contain expected commands**

Run:

```bash
rg -n "ROSettaDDS.UnityRos2Perf.Tests|tools/ros2-perf-helper|ROSETTADDS_ROS2_PERF_HELPER" docs/unity-verification.md docs/interop.md
```

Expected: matches in both files.

- [ ] **Step 4: Commit**

```bash
git add docs/unity-verification.md docs/interop.md
git commit -m "docs: Unity ROS 2 性能計測手順を追加"
```

---

### Task 7: Full verification and PR preparation

**Files:**
- No code changes expected.

- [ ] **Step 1: Verify git branch**

Run:

```bash
git status --short --branch
```

Expected: branch is `feat/unity-ros2-performance`; working tree is clean.

- [ ] **Step 2: Run .NET regression**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --nologo
```

Expected: PASS.

- [ ] **Step 3: Run Unity Ros2Perf parser/process tests**

Run:

```bash
scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value "Ros2PerfHelper(Parser|Process)Tests"
```

Expected: PASS.

- [ ] **Step 4: Run Unity Ros2Perf skip path**

Run:

```bash
ROSETTADDS_ROS2_PERF_HELPER=/path/that/does/not/exist \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

Expected: no failures; Ros2Perf scenario is ignored/skipped.

- [ ] **Step 5: Run Unity Ros2Perf real helper path when ROS 2 is available**

Run only if ROS 2 Humble / Fast DDS is available in the shell:

```bash
cd tools/ros2-perf-helper
colcon build
cd ../..
ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper" \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

Expected: PASS and `artifacts/unity/playmode-results.xml` contains `rosettadds.ros2perf.` sample groups.

If ROS 2 is not available, record that this step was not run and why.

- [ ] **Step 6: Check Unity meta files**

Run:

```bash
.github/scripts/check_unity_meta.sh
```

Expected: PASS.

- [ ] **Step 7: Push and create PR**

Run:

```bash
git status --short
git push -u origin feat/unity-ros2-performance
```

Then create a PR targeting `main` with:

```markdown
## Summary

- add a ROS 2 C++ perf helper for Unity interop measurements
- add a dedicated Unity PlayMode Ros2Perf test assembly
- document build and execution steps for Humble + Fast DDS loopback performance tests

## Verification

- [ ] dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --nologo
- [ ] scripts/unity/run_playmode.sh --batch --filter-type regex --filter-value "Ros2PerfHelper(Parser|Process)Tests"
- [ ] ROSETTADDS_ROS2_PERF_HELPER=/path/that/does/not/exist scripts/unity/run_playmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityRos2Perf.Tests
- [ ] real ROS 2 helper run, or explanation if ROS 2 was unavailable
- [ ] .github/scripts/check_unity_meta.sh
```

---

## Self-Review

- Spec coverage:
  - C++ ROS 2 helper: Task 1 and Task 2.
  - Unity dedicated PlayMode assembly: Task 3.
  - JSON Lines parser and process management: Task 3 and Task 4.
  - Unity performance scenarios and sample groups: Task 5.
  - Documentation: Task 6.
  - Verification and PR: Task 7.
- Scope control:
  - Cyclone DDS, machine-to-machine communication, strict one-way latency, UniTask, and ROSettaDDS optimization are not implemented in this plan.
  - Throughput thresholds are not introduced.
- Type consistency:
  - `Ros2PerfHelperEvent`, `Ros2PerfHelperProcess`, `Ros2PerfScenario`, and `ROSettaDDSUnityRos2PerfTests` are introduced before use.
  - Sample group names match the approved design prefix `rosettadds.ros2perf.`.
