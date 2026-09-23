using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Apollo.ControllerHost;
using HIDMaestro;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

using var catalog = new HMContext(manageDrivers: false);
catalog.LoadProfilesFromDirectory(Path.Combine(AppContext.BaseDirectory, "Profiles"));
var edgeProfile = catalog.GetProfile("dualsense-edge-composite") ?? throw new Exception("Edge profile missing");
var edgeMapper = new MoonlightControllerMapper(edgeProfile);
Check(edgeProfile.VendorId == 0x054C && edgeProfile.ProductId == 0x0DF2,
    "Edge uses Sony's actual vendor and Edge product identity");
Check(edgeProfile.ProductString == "DualSense Edge Wireless Controller" &&
      edgeProfile.GetDescriptorBytes()?.Length == 389 && edgeProfile.InputReportSize == 64,
    "Bundled Edge identity and native HID report contract");
Check(edgeProfile.RequiresUsbipBackend, "Edge uses the externally installed USB/IP transport");
Check(typeof(HMContext).Assembly.GetManifestResourceNames().Length == 0,
    "The managed SDK contains no native driver or installer payload");
Check(Marshal.SizeOf<ControllerState>() == 16, "Native state size");
Check(Marshal.OffsetOf<ControllerState>(nameof(ControllerState.LeftTrigger)) == 12, "Native trigger offset");
Check(Marshal.OffsetOf<ControllerState>(nameof(ControllerState.Reserved)) == 14, "Native reserved offset");

(uint Flag, HMButton Button)[] buttons = [
    (0x10, HMButton.Start), (0x20, HMButton.Back),
    (0x40, HMButton.LeftStick), (0x80, HMButton.RightStick),
    (0x100, HMButton.LeftBumper), (0x200, HMButton.RightBumper), (0x400, HMButton.Guide),
    (0x1000, HMButton.A), (0x2000, HMButton.B), (0x4000, HMButton.X), (0x8000, HMButton.Y),
    (0x10000, HMButton.RightPaddle), (0x20000, HMButton.LeftPaddle),
    (0x40000, HMButton.RightPaddle2), (0x80000, HMButton.LeftPaddle2),
    (0x100000, HMButton.Touchpad), (0x200000, HMButton.Misc1),
];
var allPaddles = HMButton.LeftPaddle | HMButton.RightPaddle | HMButton.LeftPaddle2 | HMButton.RightPaddle2;
uint[] hats = [0, 1, 9, 8, 10, 2, 6, 4, 5];
var edgeNeutral = edgeMapper.Map(default);
Check(edgeNeutral.Buttons == HMButton.None && edgeNeutral.Hat == HMHat.None,
    "Edge neutral buttons and hat");
Check(edgeNeutral.Axes![HMAxis.X] == .5f && edgeNeutral.Axes[HMAxis.Y] == .5f &&
      edgeNeutral.Axes[HMAxis.Z] == .5f && edgeNeutral.Axes[HMAxis.Rz] == .5f &&
      edgeNeutral.Axes[HMAxis.Rx] == 0 && edgeNeutral.Axes[HMAxis.Ry] == 0,
    "Edge centers both sticks and releases its distinct trigger usages");
var edgeLimits = edgeMapper.Map(new ControllerState { LeftX = short.MinValue, LeftY = short.MaxValue,
    RightX = short.MaxValue, RightY = short.MinValue, LeftTrigger = 255, RightTrigger = 128 });
Check(edgeLimits.Axes![HMAxis.X] == 0 && edgeLimits.Axes[HMAxis.Y] == 0 &&
      edgeLimits.Axes[HMAxis.Z] == 1 && edgeLimits.Axes[HMAxis.Rz] == 1,
    "Edge uses Sony's stick axes and reverses Moonlight's upward-positive Y");
Check(edgeLimits.Axes[HMAxis.Rx] == 1 && edgeLimits.Axes[HMAxis.Ry] == 128 / 255f,
    "Edge analog triggers do not alias its right stick");
foreach (var (flag, button) in buttons)
    Check(edgeMapper.Map(new ControllerState { Buttons = flag }).Buttons == button,
        $"Independent Edge input for Moonlight flag {flag:x}");
Check(edgeMapper.Map(new ControllerState { Buttons = 0xF0000 }).Buttons == allPaddles,
    "Edge keeps both Fn buttons and both rear paddles simultaneous and independent");
for (int i = 0; i < hats.Length; i++)
    Check((int)edgeMapper.Map(new ControllerState { Buttons = hats[i] }).Hat == i,
        $"Edge d-pad direction {i}");
var edgeSmall = edgeMapper.Map(new ControllerState { LeftX = 1, LeftY = -1 });
Check(edgeSmall.Axes![HMAxis.X] > .5f && edgeSmall.Axes[HMAxis.X] < .501f &&
      edgeSmall.Axes[HMAxis.Y] > .5f && edgeSmall.Axes[HMAxis.Y] < .501f,
    "Keep sub-byte stick precision until native report encoding");
Check(edgeNeutral.Axes![HMAxis.X] == .5f && edgeNeutral.Axes[HMAxis.Rx] == 0,
    "Later reports cannot mutate saved snapshots");

// Exercise the real SDK encoder and its advertised feature calibration. These
// tests inspect complete wire reports, not a duplicate of the bridge mapper.
var sdk = typeof(HMContext).Assembly;
var codec = sdk.GetType("HIDMaestro.Internal.VendorBlobCodec", throwOnError: true)!;
var encoderState = Activator.CreateInstance(codec.GetNestedType("EncoderState")!)!;
var encode = codec.GetMethod("EncodeInput", BindingFlags.Public | BindingFlags.Static)!;
byte[] EncodeEdge(HMGamepadState state)
{
    byte[] report = new byte[64];
    var axes = state.Axes!;
    encode.Invoke(null, [edgeProfile.ExtendedReport!, state,
        axes[HMAxis.X], axes[HMAxis.Y], axes[HMAxis.Z], axes[HMAxis.Rz],
        axes[HMAxis.Rx], axes[HMAxis.Ry], report, encoderState]);
    return report;
}
short I16(byte[] bytes, int at) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2));
using var profileJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Profiles", "dualsense-edge-composite.json")));
var calibrationReply = profileJson.RootElement.GetProperty("featureStubs").GetProperty("reports")[0];
var calibrationBytes = Convert.FromHexString(calibrationReply.GetProperty("data").GetString()!);
var calibration = calibrationBytes.AsSpan(1).ToArray();
Check(calibrationBytes[0] == 5 && calibrationReply.GetProperty("size").GetInt32() == 41,
    "Owned calibration uses Sony feature report 0x05 and its full wire length");
Check(edgeProfile.ExtendedReport!.AlwaysArmed, "Edge emits the complete sensor report from its first frame");
Check(calibration.Length == 34, "Known Sony calibration report shape");

var sensors = new DualSenseState();
var restReport = EncodeEdge(sensors.Merge(edgeNeutral, 3000));
Check(restReport[0] == 1 && restReport[8] == 8 && restReport[9] == 0 && restReport[10] == 0,
    "Complete Edge encoder retains neutral report identity and buttons");
Check(restReport[33] == 0x80 && restReport[37] == 0x80 && restReport[49] == 0x80,
    "Complete Edge reports keep lifted contacts and the normal hardware profile");
Check(I16(restReport, 24) == 8192 && BinaryPrimitives.ReadUInt32LittleEndian(restReport.AsSpan(28)) == 3000,
    "Stationary gravity and sensor clock are encoded in actual HID bytes");

sensors.Motion(new ControllerMotion { Type = 2, X = 100, Y = -50, Z = 12.25f });
sensors.Motion(new ControllerMotion { Type = 1, X = DualSenseState.EarthGravity,
    Y = 2 * DualSenseState.EarthGravity, Z = -.5f * DualSenseState.EarthGravity });
var moving = sensors.Merge(edgeMapper.Map(new ControllerState { Buttons = 0xF0030,
    LeftX = short.MinValue, RightX = short.MaxValue, LeftTrigger = 255, RightTrigger = 128 }), 6000);
var movingReport = EncodeEdge(moving);
Check(movingReport[10] == 0xF0 && (movingReport[9] & 0x30) == 0x30,
    "All four extra controls and Options/Create survive full sensor encoding");
Check(movingReport[1] == 0 && movingReport[3] == 255 && movingReport[5] == 255 && movingReport[6] == 128,
    "Full Edge encoder preserves the independent stick and trigger ranges");
double[] degrees = [100, -50, 12.25];
double[] gravity = [1, 2, -.5];
for (int axis = 0; axis < 3; axis++)
{
    double speed = I16(calibration, 18) + I16(calibration, 20);
    double gyroRange = I16(calibration, 6 + axis * 4) - I16(calibration, 8 + axis * 4);
    double decodedGyro = (I16(movingReport, 16 + axis * 2) - I16(calibration, axis * 2)) * speed / gyroRange;
    Check(Math.Abs(decodedGyro - degrees[axis]) < .051, $"Gyro axis {axis} round-trips through advertised calibration");
    double plus = I16(calibration, 22 + axis * 4), minus = I16(calibration, 24 + axis * 4);
    double bias = (plus + minus) / 2;
    double decodedG = (I16(movingReport, 22 + axis * 2) - bias) * 2 / (plus - minus);
    Check(Math.Abs(decodedG - gravity[axis]) < .00011, $"Acceleration axis {axis} round-trips in the SDL coordinate frame");
}
Check(DualSenseState.Timestamp(TimeSpan.FromMilliseconds(1)) == 3000 &&
      DualSenseState.Timestamp(TimeSpan.FromTicks(10)) == 3,
    "DualSense clock uses 0.33-microsecond ticks, not milliseconds or wall time");
Check(DualSenseState.Timestamp(TimeSpan.FromSeconds(1500)) == unchecked((uint)4_500_000_000UL),
    "Sensor clock wraps with the native 32-bit Sony counter");
var releasedReport = EncodeEdge(sensors.Merge(edgeNeutral, 9000));
Check(releasedReport[10] == 0 && I16(releasedReport, 16) == 1600 && I16(releasedReport, 24) == 16384,
    "A later button release keeps the most recent gyro and accelerometer samples");
try { sensors.Motion(new ControllerMotion { Type = 2, X = float.NaN }); Check(false, "Reject NaN motion"); }
catch (ArgumentException) { Check(sensors.Merge(edgeNeutral, 0).GyroPitch == 1600, "Invalid motion cannot poison retained state"); }
sensors.Motion(new ControllerMotion { Type = 2, X = 2000, Y = -2000 });
Check(sensors.Merge(edgeNeutral, 0).GyroPitch == 32000 && sensors.Merge(edgeNeutral, 0).GyroYaw == -32000,
    "Full physical gyro range survives without premature saturation");
sensors.Motion(new ControllerMotion { Type = 2, X = float.MaxValue, Y = -float.MaxValue });
Check(sensors.Merge(edgeNeutral, 0).GyroPitch == short.MaxValue && sensors.Merge(edgeNeutral, 0).GyroYaw == short.MinValue,
    "Out-of-range motion saturates without integer overflow");

sensors.Touch(new ControllerTouch { Type = 1, PointerId = 42, X = .25f, Y = .75f });
sensors.Touch(new ControllerTouch { Type = 1, PointerId = 99, X = 1, Y = 1 });
var touching = sensors.Merge(moving, 12000);
var touchReport = EncodeEdge(touching);
Check(touching.TouchpadFinger0Active && touching.TouchpadFinger1Active &&
      touching.TouchpadFinger0Id != touching.TouchpadFinger1Id, "Two fingers retain separate native tracking IDs");
Check(touching.TouchpadFinger0X == 480 && touching.TouchpadFinger0Y == 810 &&
      touching.TouchpadFinger1X == 1919 && touching.TouchpadFinger1Y == 1079,
    "Touch coordinates invert SDL normalization and clamp to physical pixels");
Check((touchReport[33] & 0x80) == 0 && (touchReport[37] & 0x80) == 0 &&
      touchReport[34] == 0xE0 && touchReport[35] == 0xA1 && touchReport[36] == 0x32,
    "Actual report packs the first finger's 12-bit X/Y correctly");
sensors.Touch(new ControllerTouch { Type = 1, PointerId = 500, X = .5f, Y = .5f });
Check(sensors.Merge(edgeNeutral, 0).TouchpadFinger0X == 480, "A third contact cannot overwrite either physical finger");
sensors.Touch(new ControllerTouch { Type = 2, PointerId = 42, X = float.NaN, Y = float.NaN });
var lifted = sensors.Merge(edgeNeutral, 0);
Check(!lifted.TouchpadFinger0Active && lifted.TouchpadFinger1Active, "Finger-up releases only its own contact even without coordinates");
sensors.Touch(new ControllerTouch { Type = 3, PointerId = 500, X = -.1f, Y = 2 });
var reused = sensors.Merge(edgeNeutral, 0);
Check(reused.TouchpadFinger0Active && reused.TouchpadFinger0Id != touching.TouchpadFinger0Id &&
      reused.TouchpadFinger1Id == touching.TouchpadFinger1Id && reused.TouchpadFinger0X == 0 && reused.TouchpadFinger0Y == 1079,
    "A new contact reuses a released slot without changing the other finger");
sensors.Touch(new ControllerTouch { Type = 7 });
var cancelled = EncodeEdge(sensors.Merge(edgeNeutral, 0));
Check((cancelled[33] & 0x80) != 0 && (cancelled[37] & 0x80) != 0, "Focus-loss cancellation releases both native contacts");
bool trackingIdsStayValid = true;
for (int i = 0; i < 140; i++)
{
    sensors.Touch(new ControllerTouch { Type = 1, PointerId = 42 });
    trackingIdsStayValid &= sensors.Merge(edgeNeutral, 0).TouchpadFinger0Id < 128;
    sensors.Touch(new ControllerTouch { Type = 4, PointerId = 42 });
}
Check(trackingIdsStayValid, "Tracking IDs wrap without aliasing the lifted flag");
sensors.Battery(new ControllerBattery { State = 2, Percentage = 90 });
Check(EncodeEdge(sensors.Merge(edgeNeutral, 0))[53] == 9, "Physical battery level reaches the native Sony status byte");
sensors.Battery(new ControllerBattery { State = 3, Percentage = 255 });
Check(EncodeEdge(sensors.Merge(edgeNeutral, 0))[53] == 0x19, "Charging with unknown percentage preserves the last known level");
sensors.Battery(new ControllerBattery { State = 5, Percentage = 255 });
Check(EncodeEdge(sensors.Merge(edgeNeutral, 0))[53] == 0x2A, "Full charge uses Sony's complete status");

unsafe
{
    byte[] effects = new byte[47];
    effects[0] = 0x0D; effects[1] = 4;
    effects[2] = 120; effects[3] = 200;
    effects[10] = 0x21; effects[21] = 0x25;
    for (int i = 1; i < 11; i++) { effects[10+i] = (byte)i; effects[21+i] = (byte)(20+i); }
    effects[44] = 10; effects[45] = 20; effects[46] = 30;
    var output = new HMOutputPacket(HMOutputSource.HidOutput, 2, effects, 1);
    Check(DualSenseFeedback.TryDecode(output, out var feedback) &&
          feedback.Kind == (ControllerFeedbackKind.Rumble | ControllerFeedbackKind.Lightbar | ControllerFeedbackKind.AdaptiveTriggers),
        "Combined Sony effect report retains all enabled feedback channels");
    Check(feedback.LowFrequency == 51400 && feedback.HighFrequency == 30840 &&
          feedback.Red == 10 && feedback.Green == 20 && feedback.Blue == 30,
        "Rumble channels retain full amplitude and RGB bytes remain ordered");
    Check(feedback.AdaptiveFlags == 0x0C && feedback.LeftTrigger[0] == 0x25 && feedback.LeftTrigger[10] == 30 &&
          feedback.RightTrigger[0] == 0x21 && feedback.RightTrigger[10] == 10,
        "Both adaptive-trigger effect types and ten-byte parameters retain their exact side and bytes");
    effects[0] = 0; effects[1] = 0; effects[38] = 4;
    Check(DualSenseFeedback.TryDecode(output, out feedback) && feedback.LowFrequency == 51400,
        "Improved DualSense rumble flag is supported");
    effects[38] = 0;
    Check(DualSenseFeedback.TryDecode(output, out feedback) && feedback.Kind == ControllerFeedbackKind.Rumble &&
          feedback.LowFrequency == 0 && feedback.HighFrequency == 0,
        "Leaving rumble mode sends a stop even when stale motor bytes remain");
    Check(!DualSenseFeedback.TryDecode(new HMOutputPacket(HMOutputSource.HidFeature, 2, effects, 2), out _) &&
          !DualSenseFeedback.TryDecode(new HMOutputPacket(HMOutputSource.HidOutput, 2, effects.AsMemory(0, 4), 3), out _),
        "Feature traffic and truncated reports cannot create feedback");
}

unsafe
{
    Check(sizeof(NativeExports.Api) == 72, "Native function table size");
    Check(sizeof(ControllerMotion) == 16 && sizeof(ControllerTouch) == 20 && sizeof(ControllerBattery) == 4 &&
          sizeof(ControllerFeedback) == 36, "Fixed layout for extended input and feedback messages");
    Check(Marshal.OffsetOf<ControllerFeedback>(nameof(ControllerFeedback.LeftTrigger)) == 12 &&
          Marshal.OffsetOf<ControllerFeedback>(nameof(ControllerFeedback.RightTrigger)) == 23,
        "Native feedback preserves both eleven-byte trigger blocks without padding");
    delegate* unmanaged[Cdecl]<NativeExports.Api*, int> getApi = &NativeExports.GetApi;
    NativeExports.Api api = new() { Size = (uint)sizeof(NativeExports.Api), Version = 0 };
    Check(getApi(&api) != 0, "Reject incompatible ABI version");
    api.Version = 1;
    api.Size--;
    Check(getApi(&api) != 0, "Reject an incompatible function table size");
    api.Size++;
    Check(getApi(&api) == 0 && api.Create != null && api.Update != null && api.Free != null &&
          api.Shutdown != null && api.LastError != null &&
          api.Motion != null && api.Touch != null && api.Battery != null, "Publish complete native function table");
    Check(api.Create(-1, null, 0) != 0 && api.Create(16, null, 0) != 0,
        "Invalid slots are rejected before device creation");
    ControllerState state = default;
    Check(api.Update(0, &state) != 0, "Missing controller is an error across the native boundary");
    Check(api.Motion(0, null) != 0 && api.Touch(0, null) != 0 && api.Battery(0, null) != 0,
        "Null extended input pointers are rejected at the native boundary");
    byte* message = stackalloc byte[256];
    Check(api.LastError(message, 256) > 0, "Native failure has a diagnostic");
    message[0] = 255;
    Check(api.LastError(message, 1) == 0 && message[0] == 0, "Even a truncated error has a null terminator");
}

Console.WriteLine($"PASS: {checks} Edge report, calibration, mapping and native ABI checks (no virtual device created)");
