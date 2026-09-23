# DualSense Edge on Windows

Apollo can optionally expose a streamed controller to Windows as a DualSense
Edge (`054c:0df2`). Steam Input can then bind both rear paddles and both Fn
buttons independently. The client sends the existing Moonlight button, motion,
touch and battery messages; no client protocol extension is required.

## Setup

1. Use a Windows x64 Apollo build compiled with
   `SUNSHINE_ENABLE_DUALSENSE_EDGE=ON`. This option defaults to `OFF`.
2. Install the signed [usbip-win2 driver](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.7.5)
   explicitly as an administrator. Version 0.9.7.5 is the tested transport.
   Follow its installer and reboot if requested. If another application already
   manages this driver, check its compatibility before replacing it.
3. In Apollo, choose **Configuration → Input → Emulated Gamepad Type →
   DualSense Edge (PS5)**, save and restart Apollo. The equivalent setting is
   `gamepad = dualsense-edge`.
4. Connect the Edge to the client, reconnect the stream and open Steam's
   controller input test on the host. Select the virtual DualSense Edge and
   check the four extra buttons separately. The client must expose and send
   those buttons; the host cannot recover controls discarded by the client.

Apollo packages a private .NET 10 runtime next to its executable. A system-wide
.NET installation is not needed. The signed USB/IP driver is a separate
prerequisite: a missing driver produces an error and does not silently fall
back to another controller type. No driver is installed, downgraded or repaired
when a controller connects. The USB/IP connection stays on loopback; physical
USB traffic is not forwarded over the streaming network.

To revert, choose **Automatic**, **DS4** or **X360**, save and restart Apollo.
Those modes continue to use the existing ViGEm backend. Apollo removes its Edge
devices on disconnection. Its installer/uninstaller does not remove a USB/IP
driver that other software may use; remove that driver separately through its
own uninstaller if it is no longer needed.

## Controls and limits

| Feature | Behavior |
| --- | --- |
| Buttons and axes | D-pad, face buttons, Options/Create, sticks, triggers, PS, touchpad click and mic button |
| Extra buttons | Right rear, left rear, right Fn, left Fn map to Moonlight's existing `PADDLE1` through `PADDLE4` flags |
| Motion | Gyro and accelerometer retain their coordinate frame and use matching virtual calibration |
| Touch | Two simultaneous contacts on the Edge touchpad |
| Battery | Client battery and charging state |
| Feedback | Rumble and RGB lightbar, respecting the client's capabilities and Apollo's rumble setting |
| Adaptive triggers | Standard Moonlight `0x5503` effects for PlayStation clients that implement them |

Moonlight Qt 6.1.0 already transports the extra buttons, motion, touch, battery,
rumble and lightbar. Its later upstream adaptive-trigger implementation is
needed for physical trigger effects. A firmware/profile editor, controller
audio, microphone, audio-based HD haptics, and player/mic LEDs are not
implemented. This backend reconstructs a virtual controller; it is not full USB
passthrough. The host selection is explicit because controller arrival messages
do not identify the client's exact physical VID/PID.

The virtual USB device exposes only the gamepad HID interface. It does not
create speaker or microphone endpoints that Windows could select as audio
defaults when a streamed controller connects.

## Build

Start with Apollo's normal Windows x64 build prerequisites, then add Python
3.11+, Git and the .NET 10 SDK to `PATH`. In the UCRT64 shell, for example:

```sh
cmake -S . -B cmake-build-edge -G Ninja -DSUNSHINE_ENABLE_DUALSENSE_EDGE=ON
cmake --build cmake-build-edge
cmake --install cmake-build-edge --prefix staging
```

The `sunshine` target builds the managed component and places it in
`cmake-build-edge/controller`. Installation and CPack include that directory. Keep it next
to the executable when copying a portable installation. With the option off,
the build does not discover .NET, download HIDMaestro or include the component.

The component can also be cross-built from a host with the .NET 10 SDK:

```sh
python tools/dualsense-edge/build.py --work cmake-build-edge/dualsense-edge --output cmake-build-edge/controller --test
```

The SDK source and runtime archives are hash checked. The small managed library is built from pinned
source, with the [documented SDK patches](../third-party/hidmaestro/README.md).
The private runtime accounts for most of the approximately 78 MiB uncompressed
component. The build does not require a WDK or unsigned/test-signed drivers.

## Validation

`cmake --build cmake-build-edge --target dualsense-edge-tests` checks complete Edge reports,
calibration, button mapping, motion, touch, battery, feedback decoding and the
native ABI. It also validates the SDK's HID-only USB configuration. It creates
no virtual device and does not install drivers.

With the driver installed, build `dualsense-edge-probe` and run it from the
build directory after ending active game/stream sessions. It creates a temporary
neutral Edge, exercises the actual native-to-managed boundary, releases the
device and reuses its slot. Its submission timings exclude USB/HID delivery,
physical input and video. `--features` additionally waits for a HID output
fixture with rumble, RGB and adaptive effects; it is intended for an external
HID diagnostic writer, and sends no feedback to a physical controller.
Running two probes concurrently with `--slot 14` and `--slot 15` exercises
separate processes that each allocate SDK slot zero, without sharing their
input mappings or detaching the other process's device.

Before submitting a build, manually test the streamed Edge in Steam: all four
Fn/rear controls, Options/Create, sticks/triggers, gyro, touch, rumble/lightbar,
and reconnect/session teardown. Check adaptive effects with a supporting
client. Also build with the feature disabled and confirm normal X360/DS4 input.
Automated checks do not establish physical response or end-to-end latency.
