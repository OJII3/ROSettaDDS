#include <algorithm>
#include <chrono>
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

int parse_int_arg(const std::string& name, const std::string& value)
{
  std::size_t consumed = 0;
  try {
    int parsed = std::stoi(value, &consumed);
    if (consumed != value.size()) {
      throw std::invalid_argument(name + " must be an integer");
    }
    return parsed;
  } catch (const std::exception&) {
    throw std::invalid_argument(name + " must be an integer");
  }
}

double parse_double_arg(const std::string& name, const std::string& value)
{
  std::size_t consumed = 0;
  try {
    double parsed = std::stod(value, &consumed);
    if (consumed != value.size()) {
      throw std::invalid_argument(name + " must be a number");
    }
    return parsed;
  } catch (const std::exception&) {
    throw std::invalid_argument(name + " must be a number");
  }
}

Options parse_options(int argc, char** argv)
{
  Options options;
  for (int i = 1; i < argc; ++i) {
    std::string arg = argv[i];
    if (arg == "--mode") options.mode = require_value(i, argc, argv);
    else if (arg == "--topic") options.topic = require_value(i, argc, argv);
    else if (arg == "--messages") options.messages = parse_int_arg(arg, require_value(i, argc, argv));
    else if (arg == "--payload-bytes") options.payload_bytes = parse_int_arg(arg, require_value(i, argc, argv));
    else if (arg == "--rate-hz") options.rate_hz = parse_double_arg(arg, require_value(i, argc, argv));
    else if (arg == "--qos") options.qos = require_value(i, argc, argv);
    else if (arg == "--ready-timeout-ms") options.ready_timeout_ms = parse_int_arg(arg, require_value(i, argc, argv));
    else if (arg == "--idle-timeout-ms") options.idle_timeout_ms = parse_int_arg(arg, require_value(i, argc, argv));
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

  (void)subscription;
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
