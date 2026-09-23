using System.Runtime.InteropServices;

namespace Apollo.ControllerHost;

// Fixed-layout C ABI messages. Values use Moonlight's physical units and event
// identifiers; SDK-specific report encoding stays inside the managed backend.
[StructLayout(LayoutKind.Sequential)]
public struct ControllerMotion
{
    public uint Type;
    public float X, Y, Z;
}

[StructLayout(LayoutKind.Sequential)]
public struct ControllerTouch
{
    public uint Type, PointerId;
    public float X, Y, Pressure;
}

[StructLayout(LayoutKind.Sequential)]
public struct ControllerBattery
{
    public byte State, Percentage;
    public ushort Reserved;
}

[Flags]
public enum ControllerFeedbackKind : uint
{
    None = 0,
    Rumble = 1,
    Lightbar = 2,
    AdaptiveTriggers = 4,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ControllerFeedback
{
    public ControllerFeedbackKind Kind;
    public ushort LowFrequency, HighFrequency;
    public byte Red, Green, Blue, AdaptiveFlags;
    public fixed byte LeftTrigger[11];
    public fixed byte RightTrigger[11];
    public ushort Reserved;
}
