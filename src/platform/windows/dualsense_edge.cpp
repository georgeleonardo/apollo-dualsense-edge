/**
 * @file src/platform/windows/dualsense_edge.cpp
 * @brief Native calls into the bundled managed HIDMaestro controller component.
 */
#include "dualsense_edge.h"

#include "src/logging.h"
#include "third-party/dotnet-host/coreclr_delegates.h"
#include "third-party/dotnet-host/hostfxr.h"
#include "third-party/dotnet-host/nethost.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <filesystem>
#include <mutex>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
#include <windows.h>

namespace platf {
  namespace {
    // This explicit C ABI preserves every Moonlight button bit and both
    // 16-bit sticks. It does not depend on gamepad_state_t's native padding.
    struct controller_state_t {
      std::uint32_t buttons;
      std::int16_t left_x, left_y, right_x, right_y;
      std::uint8_t left_trigger, right_trigger;
      std::uint16_t reserved;
    };

    static_assert(sizeof(controller_state_t) == 16);
    static_assert(offsetof(controller_state_t, left_trigger) == 12);

    struct controller_motion_t {
      std::uint32_t type;
      float x, y, z;
    };

    struct controller_touch_t {
      std::uint32_t type, pointer_id;
      float x, y, pressure;
    };

    struct controller_battery_t {
      std::uint8_t state, percentage;
      std::uint16_t reserved;
    };

    struct controller_feedback_t {
      std::uint32_t kind;
      std::uint16_t low_frequency, high_frequency;
      std::uint8_t r, g, b, adaptive_flags;
      std::array<std::uint8_t, 11> left, right;
      std::uint16_t reserved;
    };

    static_assert(sizeof(controller_motion_t) == 16);
    static_assert(sizeof(controller_touch_t) == 20);
    static_assert(sizeof(controller_battery_t) == 4);
    static_assert(sizeof(controller_feedback_t) == 36);
    static_assert(offsetof(controller_feedback_t, left) == 12);
    static_assert(offsetof(controller_feedback_t, right) == 23);

    using feedback_callback_t = void(__cdecl *)(void *, const controller_feedback_t *);

    struct feedback_destination_t {
      feedback_queue_t queue;
      std::uint8_t client_index {};
      std::uint16_t capabilities {};
      bool adaptive_triggers {};

      void reset_effects() const {
        if (!queue) {
          return;
        }
        if (capabilities & LI_CCAP_RUMBLE) {
          queue->raise(gamepad_feedback_msg_t::make_rumble(client_index, 0, 0));
        }
        if (adaptive_triggers) {
          const std::array<std::uint8_t, 10> zero {};
          queue->raise(gamepad_feedback_msg_t::make_adaptive_triggers(client_index, 0x0C, 0x05, 0x05, zero, zero));
        }
      }
    };

    struct feedback_channel_t {
      std::mutex gate;
      feedback_destination_t destination;
      bool open {};

      void start(feedback_queue_t queue, std::uint8_t client_index, std::uint16_t capabilities, bool adaptive_triggers) {
        std::lock_guard lock(gate);
        destination = {std::move(queue), client_index, capabilities, adaptive_triggers};
        open = true;
      }

      feedback_destination_t close() {
        std::lock_guard lock(gate);
        open = false;
        return std::exchange(destination, {});
      }
    };

    void __cdecl send_feedback(void *context, const controller_feedback_t *report) {
      if (!context || !report || report->reserved != 0) {
        return;
      }
      try {
        auto &channel = *static_cast<feedback_channel_t *>(context);
        feedback_destination_t destination;
        {
          std::lock_guard lock(channel.gate);
          if (!channel.open || !channel.destination.queue) {
            return;
          }
          destination = channel.destination;
        }
        if ((report->kind & 1) && (destination.capabilities & LI_CCAP_RUMBLE)) {
          destination.queue->raise(gamepad_feedback_msg_t::make_rumble(destination.client_index, report->low_frequency, report->high_frequency));
        }
        if ((report->kind & 2) && (destination.capabilities & LI_CCAP_RGB_LED)) {
          destination.queue->raise(gamepad_feedback_msg_t::make_rgb_led(destination.client_index, report->r, report->g, report->b));
        }
        if ((report->kind & 4) && destination.adaptive_triggers) {
          std::array<std::uint8_t, 10> left, right;
          std::copy_n(report->left.begin() + 1, left.size(), left.begin());
          std::copy_n(report->right.begin() + 1, right.size(), right.begin());
          destination.queue->raise(gamepad_feedback_msg_t::make_adaptive_triggers(destination.client_index, report->adaptive_flags & 0x0C, report->left[0], report->right[0], left, right));
        }
      } catch (const std::exception &exception) {
        // A native exception must never unwind through the managed callback.
        BOOST_LOG(error) << "Controller feedback failed: " << exception.what();
      }
    }

    struct controller_api_t {
      std::uint32_t size, version;
      int(__cdecl *create)(int, feedback_callback_t, void *);
      int(__cdecl *update)(int, const controller_state_t *);
      int(__cdecl *free)(int);
      void(__cdecl *shutdown)();
      int(__cdecl *last_error)(char *, int);
      int(__cdecl *motion)(int, const controller_motion_t *);
      int(__cdecl *touch)(int, const controller_touch_t *);
      int(__cdecl *battery)(int, const controller_battery_t *);
    };

    static_assert(sizeof(controller_api_t) == 72);

    std::filesystem::path component_directory() {
      std::vector<wchar_t> path(32768);
      auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
      if (length == 0 || length >= path.size()) {
        throw std::runtime_error("Cannot resolve Apollo's controller component directory");
      }
      return std::filesystem::path(std::wstring(path.data(), length)).parent_path() / L"controller";
    }

    struct library_t {
      HMODULE handle {};

      ~library_t() {
        if (handle) {
          FreeLibrary(handle);
        }
      }

      void load(const std::filesystem::path &path) {
        handle = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!handle) {
          throw std::runtime_error("Cannot load " + path.string() + " (Windows error " + std::to_string(GetLastError()) + ")");
        }
      }

      template<class T>
      T function(const char *name) const {
        auto address = GetProcAddress(handle, name);
        if (!address) {
          throw std::runtime_error(std::string("Missing .NET hosting export: ") + name);
        }
        return reinterpret_cast<T>(address);
      }
    };
  }  // namespace

  struct dualsense_edge_t::impl_t {
    library_t nethost;
    library_t hostfxr;
    controller_api_t api {};
    std::once_flag initialize_once;
    bool initialized {};
    std::array<std::atomic_bool, MAX_GAMEPADS> active {};
    std::array<std::atomic_bool, MAX_GAMEPADS> update_failed {};
    std::array<feedback_channel_t, MAX_GAMEPADS> feedback;

    ~impl_t() {
      if (initialized) {
        api.shutdown();
      }
    }

    bool initialize() {
      std::call_once(initialize_once, [this]() {
        try {
          auto directory = component_directory();
          auto runtime = directory / L"runtime";
          auto assembly = directory / L"Apollo.ControllerHost.dll";
          auto runtime_config = directory / L"Apollo.ControllerHost.runtimeconfig.json";

          // Framework-dependent component, with a private runtime directory.
          // No system .NET installation or separate controller process.
          nethost.load(directory / L"nethost.dll");
          using get_path_t = int(NETHOST_CALLTYPE *)(char_t *, size_t *, const get_hostfxr_parameters *);
          auto get_path = nethost.function<get_path_t>("get_hostfxr_path");
          get_hostfxr_parameters parameters {sizeof(parameters), assembly.c_str(), runtime.c_str()};
          size_t path_size = 0;
          get_path(nullptr, &path_size, &parameters);
          if (path_size == 0 || path_size > 32768) {
            throw std::runtime_error("The private .NET runtime is missing or invalid");
          }
          std::vector<char_t> host_path(path_size);
          if (get_path(host_path.data(), &path_size, &parameters) != 0) {
            throw std::runtime_error("Cannot locate the private .NET hostfxr library");
          }
          hostfxr.load(host_path.data());
          auto initialize = hostfxr.function<hostfxr_initialize_for_runtime_config_fn>("hostfxr_initialize_for_runtime_config");
          auto get_delegate = hostfxr.function<hostfxr_get_runtime_delegate_fn>("hostfxr_get_runtime_delegate");
          auto close = hostfxr.function<hostfxr_close_fn>("hostfxr_close");
          hostfxr_initialize_parameters initialize_parameters {sizeof(initialize_parameters), nullptr, runtime.c_str()};
          hostfxr_handle context {};
          int result = initialize(runtime_config.c_str(), &initialize_parameters, &context);
          if (result < 0 || !context) {
            if (context) {
              close(context);
            }
            throw std::runtime_error("Cannot initialize the controller runtime (code " + std::to_string(result) + ")");
          }
          void *load_pointer {};
          result = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &load_pointer);
          close(context);
          if (result != 0 || !load_pointer) {
            throw std::runtime_error("Cannot obtain the .NET component loader");
          }
          auto load = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(load_pointer);
          void *get_api_pointer {};
          result = load(assembly.c_str(), L"Apollo.ControllerHost.NativeExports, Apollo.ControllerHost", L"GetApi", UNMANAGEDCALLERSONLY_METHOD, nullptr, &get_api_pointer);
          if (result != 0 || !get_api_pointer) {
            throw std::runtime_error("Cannot load the controller component (code " + std::to_string(result) + ")");
          }
          using get_api_t = int(__cdecl *)(controller_api_t *);
          auto get_api = reinterpret_cast<get_api_t>(get_api_pointer);
          api.size = sizeof(api);
          api.version = 1;
          if (get_api(&api) != 0 || !api.create || !api.update || !api.free || !api.shutdown || !api.last_error || !api.motion || !api.touch || !api.battery) {
            throw std::runtime_error("Controller component ABI mismatch");
          }
          initialized = true;
          BOOST_LOG(info) << "DualSense Edge backend loaded inside Apollo";
        } catch (const std::exception &exception) {
          BOOST_LOG(error) << "DualSense Edge backend initialization failed: " << exception.what();
        }
      });
      return initialized;
    }

    std::string last_error_message() const {
      std::array<char, 1024> message {};
      api.last_error(message.data(), static_cast<int>(message.size()));
      return message.data();
    }

    bool input_result(int index, int result, const char *operation) {
      if (result != 0) {
        if (!update_failed[index].exchange(true)) {
          BOOST_LOG(error) << "HID controller " << index << ' ' << operation << " failed: " << last_error_message();
        }
        return false;
      }
      update_failed[index] = false;
      return true;
    }
  };

  dualsense_edge_t::dualsense_edge_t():
      impl(std::make_unique<impl_t>()) {}

  dualsense_edge_t::~dualsense_edge_t() {
    for (int index = 0; index < MAX_GAMEPADS; ++index) {
      free(index);
    }
  }

  bool dualsense_edge_t::available() {
    try {
      auto directory = component_directory();
      return std::filesystem::is_regular_file(directory / L"Apollo.ControllerHost.dll") &&
             std::filesystem::is_regular_file(directory / L"Apollo.ControllerHost.runtimeconfig.json") &&
             std::filesystem::is_regular_file(directory / L"nethost.dll") &&
             std::filesystem::is_regular_file(directory / L"Profiles" / L"dualsense-edge-composite.json") &&
             std::filesystem::is_regular_file(directory / L"runtime" / L"dotnet.exe");
    } catch (const std::exception &) {
      return false;
    }
  }

  int dualsense_edge_t::allocate(int index, feedback_queue_t feedback, std::uint8_t client_index, std::uint16_t capabilities, bool adaptive_triggers) {
    if (index < 0 || index >= MAX_GAMEPADS || owns(index) || !impl->initialize()) {
      return -1;
    }
    impl->feedback[index].start(std::move(feedback), client_index, capabilities, adaptive_triggers);
    if (impl->api.create(index, send_feedback, &impl->feedback[index]) != 0) {
      impl->feedback[index].close().reset_effects();
      BOOST_LOG(error) << "Cannot create HID controller " << index << ": " << impl->last_error_message();
      return -1;
    }
    impl->active[index] = true;
    return 0;
  }

  bool dualsense_edge_t::owns(int index) const {
    return index >= 0 && index < MAX_GAMEPADS && impl->active[index].load();
  }

  bool dualsense_edge_t::update(int index, const gamepad_state_t &state) {
    if (!owns(index)) {
      return false;
    }
    const controller_state_t report {state.buttonFlags, state.lsX, state.lsY, state.rsX, state.rsY, state.lt, state.rt, 0};
    return impl->input_result(index, impl->api.update(index, &report), "input");
  }

  bool dualsense_edge_t::motion(const gamepad_motion_t &motion) {
    if (!owns(motion.id.globalIndex)) {
      return false;
    }
    const controller_motion_t report {motion.motionType, motion.x, motion.y, motion.z};
    return impl->input_result(motion.id.globalIndex, impl->api.motion(motion.id.globalIndex, &report), "motion");
  }

  bool dualsense_edge_t::touch(const gamepad_touch_t &touch) {
    if (!owns(touch.id.globalIndex)) {
      return false;
    }
    const controller_touch_t report {touch.eventType, touch.pointerId, touch.x, touch.y, touch.pressure};
    return impl->input_result(touch.id.globalIndex, impl->api.touch(touch.id.globalIndex, &report), "touch");
  }

  bool dualsense_edge_t::battery(const gamepad_battery_t &battery) {
    if (!owns(battery.id.globalIndex)) {
      return false;
    }
    const controller_battery_t report {battery.state, battery.percentage, 0};
    return impl->input_result(battery.id.globalIndex, impl->api.battery(battery.id.globalIndex, &report), "battery");
  }

  void dualsense_edge_t::free(int index) {
    if (index < 0 || index >= MAX_GAMEPADS || !impl->active[index].exchange(false)) {
      return;
    }
    auto feedback = impl->feedback[index].close();
    if (impl->api.free(index) != 0) {
      BOOST_LOG(error) << "HID controller " << index << " cleanup failed: " << impl->last_error_message();
    }
    // The managed disposal drains callbacks before the final stop, so an
    // in-flight rumble cannot race this reset or leak into a reused slot.
    feedback.reset_effects();
    impl->update_failed[index] = false;
  }
}  // namespace platf
