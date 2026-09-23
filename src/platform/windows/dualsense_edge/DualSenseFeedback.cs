using HIDMaestro;

namespace Apollo.ControllerHost;

/// <summary>Decodes valid fields of Sony's USB effects report for Moonlight feedback.</summary>
public static unsafe class DualSenseFeedback
{
    public static bool TryDecode(in HMOutputPacket packet, out ControllerFeedback feedback)
    {
        feedback = default;
        // HMOutputPacket excludes the report-ID byte. Sony's complete effect
        // payload is 47 bytes; never interpret a feature read or short write
        // as a rumble/trigger command.
        if (packet.Source != HMOutputSource.HidOutput || packet.ReportId != 2 || packet.Data.Length < 47)
            return false;
        var data = packet.Data.Span;
        // Clearing the emulation flags restores audio haptics on a real pad;
        // it also stops any preceding emulated rumble. Forward that zero so
        // a stop packet cannot leave Moonlight's 30-second rumble active.
        feedback.Kind = ControllerFeedbackKind.Rumble;
        if ((data[0] & 0x01) != 0 || (data[38] & 0x04) != 0)
        {
            feedback.LowFrequency = (ushort)(data[3] * 257);
            feedback.HighFrequency = (ushort)(data[2] * 257);
        }
        if ((data[1] & 0x04) != 0)
        {
            feedback.Kind |= ControllerFeedbackKind.Lightbar;
            feedback.Red = data[44];
            feedback.Green = data[45];
            feedback.Blue = data[46];
        }
        feedback.AdaptiveFlags = (byte)(data[0] & 0x0C);
        if (feedback.AdaptiveFlags != 0)
        {
            feedback.Kind |= ControllerFeedbackKind.AdaptiveTriggers;
            fixed (byte* left = feedback.LeftTrigger, right = feedback.RightTrigger)
            {
                data.Slice(21, 11).CopyTo(new Span<byte>(left, 11));
                data.Slice(10, 11).CopyTo(new Span<byte>(right, 11));
            }
        }
        return feedback.Kind != ControllerFeedbackKind.None;
    }
}
