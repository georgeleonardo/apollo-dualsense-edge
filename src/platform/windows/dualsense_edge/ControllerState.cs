using System.Runtime.InteropServices;

namespace Apollo.ControllerHost;

/// <summary>The controller state shared by the C ABI, independent of Apollo's C++ layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ControllerState
{
    public uint Buttons;
    public short LeftX, LeftY, RightX, RightY;
    public byte LeftTrigger, RightTrigger;
    public ushort Reserved;
}
