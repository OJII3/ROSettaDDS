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
