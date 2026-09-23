using HIDMaestro;

namespace Apollo.ControllerHost;

/// <summary>Maps Moonlight's logical inputs to DualSense Edge axes and buttons.</summary>
public sealed class MoonlightControllerMapper
{
    private readonly HMAxis leftX, leftY, rightX, rightY, leftTrigger, rightTrigger;

    public MoonlightControllerMapper(HMProfile profile)
    {
        if (profile.Id != "dualsense-edge-usb")
            throw new ArgumentException("Expected the DualSense Edge profile.", nameof(profile));
        var sticks = profile.Sticks;
        var triggers = profile.Triggers;
        if (sticks.Count != 2 || triggers.Count != 2)
            throw new ArgumentException("The output profile must expose two sticks and two triggers.", nameof(profile));
        (leftX, leftY) = (sticks[0].XAxis, sticks[0].YAxis);
        (rightX, rightY) = (sticks[1].XAxis, sticks[1].YAxis);
        (leftTrigger, rightTrigger) = (triggers[0].Axis, triggers[1].Axis);
    }

    public HMGamepadState Map(in ControllerState source)
    {
        uint flags = source.Buttons;
        HMButton buttons = HMButton.None;
        if ((flags & 0x0010) != 0) buttons |= HMButton.Start;
        if ((flags & 0x0020) != 0) buttons |= HMButton.Back;
        if ((flags & 0x0040) != 0) buttons |= HMButton.LeftStick;
        if ((flags & 0x0080) != 0) buttons |= HMButton.RightStick;
        if ((flags & 0x0100) != 0) buttons |= HMButton.LeftBumper;
        if ((flags & 0x0200) != 0) buttons |= HMButton.RightBumper;
        if ((flags & 0x0400) != 0) buttons |= HMButton.Guide;
        if ((flags & 0x1000) != 0) buttons |= HMButton.A;
        if ((flags & 0x2000) != 0) buttons |= HMButton.B;
        if ((flags & 0x4000) != 0) buttons |= HMButton.X;
        if ((flags & 0x8000) != 0) buttons |= HMButton.Y;
        // Moonlight's PADDLE1..4 order is right rear, left rear, right Fn, left Fn.
        if ((flags & 0x010000) != 0) buttons |= HMButton.RightPaddle;
        if ((flags & 0x020000) != 0) buttons |= HMButton.LeftPaddle;
        if ((flags & 0x040000) != 0) buttons |= HMButton.RightPaddle2;
        if ((flags & 0x080000) != 0) buttons |= HMButton.LeftPaddle2;
        if ((flags & 0x100000) != 0) buttons |= HMButton.Touchpad;
        if ((flags & 0x200000) != 0) buttons |= HMButton.Misc1;

        // Moonlight uses positive Y upward; Sony uses positive Y down.
        // Resolve the target's actual axis usages once, then retain the full
        // incoming stick precision until the SDK encodes the native report.
        float ly = NormalizeStick(source.LeftY), ry = NormalizeStick(source.RightY);
        return new HMGamepadState
        {
            Buttons = buttons,
            Hat = MapHat(flags & 0xF),
            // The SDK retains snapshots for its idle pump. Every submission
            // owns a fresh dictionary that later updates cannot mutate.
            Axes = new Dictionary<HMAxis, float>(6)
            {
                [leftX] = NormalizeStick(source.LeftX), [leftY] = 1f - ly,
                [rightX] = NormalizeStick(source.RightX), [rightY] = 1f - ry,
                [leftTrigger] = source.LeftTrigger / 255f, [rightTrigger] = source.RightTrigger / 255f,
            },
        };
    }

    private static float NormalizeStick(short value) =>
        value <= 0 ? 0.5f + value / 65536f : 0.5f + value / 65534f;

    private static HMHat MapHat(uint flags) => flags switch
    {
        0x1 => HMHat.North, 0x9 => HMHat.NorthEast, 0x8 => HMHat.East,
        0xA => HMHat.SouthEast, 0x2 => HMHat.South, 0x6 => HMHat.SouthWest,
        0x4 => HMHat.West, 0x5 => HMHat.NorthWest, _ => HMHat.None,
    };
}
