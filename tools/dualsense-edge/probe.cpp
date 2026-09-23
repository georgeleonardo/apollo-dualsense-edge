/**
 * @file tools/dualsense-edge/probe.cpp
 * @brief Exercise the Edge runtime and a temporary neutral controller.
 */
#include "src/logging.h"
#include "src/platform/windows/dualsense_edge.h"

#include <algorithm>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

boost::log::sources::severity_logger<int> verbose(0);
boost::log::sources::severity_logger<int> debug(1);
boost::log::sources::severity_logger<int> info(2);
boost::log::sources::severity_logger<int> warning(3);
boost::log::sources::severity_logger<int> error(4);
boost::log::sources::severity_logger<int> fatal(5);

int main(int argc, char **argv) {
  bool features = false;
  int index = 15;
  try {
    for (int argument = 1; argument < argc; ++argument) {
      const std::string option = argv[argument];
      if (option == "--features") {
        features = true;
      } else if (option == "--slot" && argument + 1 < argc) {
        const std::string value = argv[++argument];
        std::size_t parsed {};
        index = std::stoi(value, &parsed);
        if (parsed != value.size() || index < 0 || index >= 16) {
          throw std::invalid_argument("slot");
        }
      } else {
        throw std::invalid_argument("option");
      }
    }
  } catch (const std::exception &) {
    std::cerr << "Usage: dualsense-edge-probe.exe [--slot 0..15] [--features]\n";
    return 2;
  }
  if (!platf::dualsense_edge_t::available()) {
    std::cerr << "The controller component must be beside the probe in the controller directory.\n";
    return 1;
  }
  platf::dualsense_edge_t controller;
  auto mailbox = std::make_shared<safe::mail_raw_t>();
  auto feedback = mailbox->queue<platf::gamepad_feedback_msg_t>("controller-probe");
  const auto capabilities = features ? LI_CCAP_RUMBLE | LI_CCAP_RGB_LED : 0;
  // This private queue is never connected to a Moonlight client or a physical
  // device. A separate HID writer may safely test output routing through it.
  if (controller.allocate(index, feedback, 7, capabilities, features) != 0 || !controller.owns(index)) {
    return 1;
  }
  if (!controller.motion({{index, 7}, LI_MOTION_TYPE_ACCEL, 0, 9.80665f, 0}) || !controller.motion({{index, 7}, LI_MOTION_TYPE_GYRO, 0, 0, 0}) || !controller.touch({{index, 7}, LI_TOUCH_EVENT_CANCEL_ALL, 0, 0, 0, 0}) || !controller.battery({{index, 7}, LI_BATTERY_STATE_UNKNOWN, LI_BATTERY_PERCENTAGE_UNKNOWN})) {
    return 1;
  }
  std::vector<double> samples;
  const platf::gamepad_state_t neutral {};
  for (int i = 0; i < 1020; ++i) {
    auto start = std::chrono::steady_clock::now();
    if (!controller.update(index, neutral)) {
      return 1;
    }
    auto elapsed = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - start).count();
    if (i >= 20) {
      samples.push_back(elapsed);
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  // Give Steam and a separate HID reader time to inspect the neutral device.
  std::this_thread::sleep_for(std::chrono::seconds(3));
  if (features) {
    bool rumble {}, rgb {}, adaptive {};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (!(rumble && rgb && adaptive) && std::chrono::steady_clock::now() < deadline) {
      auto message = feedback->pop(std::chrono::milliseconds(100));
      if (!message || message->id != 7) {
        continue;
      }
      switch (message->type) {
        case platf::gamepad_feedback_e::rumble:
          rumble |= message->data.rumble.lowfreq == 51400 && message->data.rumble.highfreq == 30840;
          break;
        case platf::gamepad_feedback_e::set_rgb_led:
          rgb |= message->data.rgb_led.r == 10 && message->data.rgb_led.g == 20 && message->data.rgb_led.b == 30;
          break;
        case platf::gamepad_feedback_e::set_adaptive_triggers:
          adaptive |= message->data.adaptive_triggers.event_flags == 0x0C &&
                      message->data.adaptive_triggers.type_left == 0x25 && message->data.adaptive_triggers.type_right == 0x21 &&
                      message->data.adaptive_triggers.left[0] == 21 && message->data.adaptive_triggers.left[9] == 30 &&
                      message->data.adaptive_triggers.right[0] == 1 && message->data.adaptive_triggers.right[9] == 10;
          break;
        default:
          break;
      }
    }
    if (!(rumble && rgb && adaptive)) {
      std::cerr << "Feedback check incomplete: rumble=" << rumble << " RGB=" << rgb << " adaptive=" << adaptive << '\n';
      return 1;
    }
    std::cout << "PASS: HID output crossed the native callback with rumble, RGB, exact adaptive bytes, and client-relative index 7\n";
  }
  controller.free(index);
  if (controller.owns(index)) {
    std::cerr << "The controller slot was not released.\n";
    return 1;
  }
  // A second allocation checks that a later streaming session can reuse
  // the same slot after the first one has ended.
  if (controller.allocate(index) != 0 || !controller.update(index, neutral)) {
    return 1;
  }
  controller.free(index);
  std::sort(samples.begin(), samples.end());
  std::cout << "PASS: native runtime load, 1020 neutral updates, release, and slot reuse\n"
            << "Native-to-SDK submission microseconds: median=" << samples[500]
            << " p95=" << samples[949] << " max=" << samples.back() << '\n'
            << "These timings exclude USB/HID delivery, physical input, and video.\n";
  return 0;
}
