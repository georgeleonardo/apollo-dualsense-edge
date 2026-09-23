/**
 * @file src/platform/windows/dualsense_edge.h
 * @brief Optional Windows DualSense Edge emulation hosted inside Apollo.
 */
#pragma once

#include "src/platform/common.h"

#include <cstdint>
#include <memory>

namespace platf {
  class dualsense_edge_t {
  public:
    dualsense_edge_t();
    ~dualsense_edge_t();

    dualsense_edge_t(const dualsense_edge_t &) = delete;
    dualsense_edge_t &operator=(const dualsense_edge_t &) = delete;

    static bool available();
    int allocate(int index, feedback_queue_t feedback = {}, std::uint8_t client_index = 0, std::uint16_t capabilities = 0, bool adaptive_triggers = false);
    bool owns(int index) const;
    bool update(int index, const gamepad_state_t &state);
    bool motion(const gamepad_motion_t &motion);
    bool touch(const gamepad_touch_t &touch);
    bool battery(const gamepad_battery_t &battery);
    void free(int index);

  private:
    struct impl_t;
    std::unique_ptr<impl_t> impl;
  };
}  // namespace platf
