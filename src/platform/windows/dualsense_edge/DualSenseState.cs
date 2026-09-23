using HIDMaestro;

namespace Apollo.ControllerHost;

/// <summary>Retains independent Moonlight sensor/touch/battery channels between button packets.</summary>
public sealed class DualSenseState
{
    public const float EarthGravity = 9.80665f;
    private short gyroX, gyroY, gyroZ, accelX, accelY = 8192, accelZ;
    private readonly Contact[] contacts = [new(), new()];
    private byte nextTrackingId;
    private byte batteryLevel;
    private bool batteryCharging, batteryFull;

    private sealed class Contact
    {
        public bool Active;
        public uint PointerId;
        public byte TrackingId;
        public ushort X, Y;
    }

    public void Motion(in ControllerMotion motion)
    {
        if (motion.Type is not (1 or 2)) throw new ArgumentException("Unknown Moonlight motion type.");
        if (!float.IsFinite(motion.X) || !float.IsFinite(motion.Y) || !float.IsFinite(motion.Z))
            throw new ArgumentException("Non-finite Moonlight motion sample.");

        // The owned profile advertises neutral calibration (feature 0x05)
        // matching Sony's full range: 16 counts per deg/s and 8192 per g.
        // Moonlight already calibrated the physical sensor. Both ends use
        // SDL's PlayStation frame, so applying factory bias again is wrong.
        float scale = motion.Type == 1 ? 8192f / EarthGravity : 16f;
        short x = Scale(motion.X, scale), y = Scale(motion.Y, scale), z = Scale(motion.Z, scale);
        if (motion.Type == 1) (accelX, accelY, accelZ) = (x, y, z);
        else (gyroX, gyroY, gyroZ) = (x, y, z);
    }

    private static short Scale(float value, float scale) =>
        (short)Math.Clamp(Math.Round((double)value * scale), short.MinValue, short.MaxValue);

    public void Touch(in ControllerTouch touch)
    {
        if (touch.Type == 7) // Cancel all, including focus loss.
        {
            foreach (var contact in contacts) contact.Active = false;
            return;
        }
        Contact? existing = null;
        foreach (var contact in contacts)
            if (contact.Active && contact.PointerId == touch.PointerId) existing = contact;

        if (touch.Type is 2 or 4) // Up/cancel may omit coordinates entirely.
        {
            if (existing != null) existing.Active = false;
            return;
        }
        if (touch.Type is not (1 or 3)) throw new ArgumentException("Unsupported controller touch event.");
        if (!float.IsFinite(touch.X) || !float.IsFinite(touch.Y))
            throw new ArgumentException("Non-finite controller touch coordinates.");
        if (existing == null)
        {
            foreach (var contact in contacts)
                if (!contact.Active) { existing = contact; break; }
            if (existing == null) return; // The physical pad supports two contacts.
            existing.PointerId = touch.PointerId;
            existing.TrackingId = (byte)(nextTrackingId++ & 0x7F);
            existing.Active = true;
        }
        // SDL normalizes native pixels by 1920x1080. Invert that operation and
        // clamp to the actual last pixel, rather than shifting every contact.
        existing.X = (ushort)Math.Clamp(Math.Round((double)touch.X * 1920), 0, 1919);
        existing.Y = (ushort)Math.Clamp(Math.Round((double)touch.Y * 1080), 0, 1079);
    }

    public void Battery(in ControllerBattery battery)
    {
        if (battery.Reserved != 0 || battery.State > 5 || (battery.Percentage > 100 && battery.Percentage != 255))
            throw new ArgumentException("Invalid Moonlight battery event.");
        if (battery.Percentage != 255) batteryLevel = (byte)(battery.Percentage / 10);
        switch (battery.State)
        {
            case 2: // Discharging.
            case 4: // External power, not charging.
                batteryCharging = batteryFull = false;
                break;
            case 3:
                batteryCharging = true;
                batteryFull = false;
                break;
            case 5:
                batteryCharging = false;
                batteryFull = true;
                batteryLevel = 10;
                break;
            // Unknown / not present have no equivalent in the SDK's battery
            // surface. Keep the last known state instead of inventing a level.
        }
    }

    public HMGamepadState Merge(in HMGamepadState buttons, uint sensorTimestamp)
    {
        var state = buttons;
        state.GyroPitch = gyroX;
        state.GyroYaw = gyroY;
        state.GyroRoll = gyroZ;
        state.AccelX = accelX;
        state.AccelY = accelY;
        state.AccelZ = accelZ;
        state.SensorTimestamp = sensorTimestamp;
        state.TouchpadFinger0Active = contacts[0].Active;
        state.TouchpadFinger0Id = contacts[0].TrackingId;
        state.TouchpadFinger0X = contacts[0].X;
        state.TouchpadFinger0Y = contacts[0].Y;
        state.TouchpadFinger1Active = contacts[1].Active;
        state.TouchpadFinger1Id = contacts[1].TrackingId;
        state.TouchpadFinger1X = contacts[1].X;
        state.TouchpadFinger1Y = contacts[1].Y;
        state.BatteryLevel = batteryLevel;
        state.BatteryCharging = batteryCharging;
        state.BatteryFull = batteryFull;
        return state;
    }

    // DualSense's sensor clock runs at 3 MHz and wraps after about 24 minutes.
    // TimeSpan ticks are 100 ns, so this conversion retains sub-millisecond
    // resolution without using a wall clock that may jump during streaming.
    public static uint Timestamp(TimeSpan elapsed) => unchecked((uint)((ulong)elapsed.Ticks * 3 / 10));
}
