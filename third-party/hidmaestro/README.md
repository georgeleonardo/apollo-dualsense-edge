# HIDMaestro managed dependency

The optional Windows DualSense Edge backend builds the managed sources from
[HIDMaestro 1.9.0](https://github.com/hifihedgehog/HIDMaestro/tree/942e25aa9ce4e93dc601f59a796f7432809fc41b).
The commit, archive SHA-256 and private .NET runtime SHA-512 are pinned in
`dependencies.json`. Downloads and generated files stay in the build directory.

This project compiles the SDK's managed code without embedding its native
drivers, driver installers, signing tools, VR payloads or profile catalog.
Apollo loads its own Edge profile and uses an externally installed signed
usbip-win2 driver. Neither controller creation nor the Apollo installer installs,
repairs or replaces that driver. The compiled SDK and profile retain the MIT
license; the SDK's third-party notice accompanies the installed component.

Two source patches are applied to this exact revision:

- `serialize-input.patch` serializes the SDK's idle pump with live `SubmitState`
  calls. The idle-state selection, mutable encoder and shared-memory publication
  must be protected together; otherwise an older idle snapshot can overwrite a
  new button state. Apollo owns a fresh axis dictionary for every submitted
  state and does not use the SDK's raw-report submission APIs.
- `external-usbip.patch` adds an explicit `HMContext(manageDrivers: false)` mode,
  makes USB/IP shared sections and events private to the process, and limits
  recovery to the server's exclusively bound loopback port. It also releases
  mappings on failed attachment and closes the companion event on teardown.
  A different application using HIDMaestro must not have its controller input
  overwritten or its imported USB devices detached by Apollo.

These are local dependency patches, not claims that the changes have merged
upstream. No source change is applied to an installed driver. See
[the backend guide](../../docs/dualsense-edge.md) for build and validation steps.
