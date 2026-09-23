using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HIDMaestro;

namespace Apollo.ControllerHost;

/// <summary>Loaded in Apollo by the supported .NET native hosting API.</summary>
public static unsafe class NativeExports
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Api
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<int, delegate* unmanaged[Cdecl]<nint, ControllerFeedback*, void>, nint, int> Create;
        public delegate* unmanaged[Cdecl]<int, ControllerState*, int> Update;
        public delegate* unmanaged[Cdecl]<int, int> Free;
        public delegate* unmanaged[Cdecl]<void> Shutdown;
        public delegate* unmanaged[Cdecl]<byte*, int, int> LastError;
        public delegate* unmanaged[Cdecl]<int, ControllerMotion*, int> Motion;
        public delegate* unmanaged[Cdecl]<int, ControllerTouch*, int> Touch;
        public delegate* unmanaged[Cdecl]<int, ControllerBattery*, int> Battery;
    }

    private static readonly object Lifecycle = new();
    private static readonly ConcurrentDictionary<int, Session> Sessions = new();
    private static HMContext? context;
    [ThreadStatic] private static string? lastError;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApi(Api* api)
    {
        if (api == null || api->Size != sizeof(Api) || api->Version != 1) return -1;
        api->Create = &Create;
        api->Update = &Update;
        api->Free = &Free;
        api->Shutdown = &Shutdown;
        api->LastError = &LastError;
        api->Motion = &Motion;
        api->Touch = &Touch;
        api->Battery = &Battery;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Create(int index,
        delegate* unmanaged[Cdecl]<nint, ControllerFeedback*, void> feedback, nint feedbackContext)
    {
        try
        {
            if (index is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(index));
            lock (Lifecycle)
            {
                if (Sessions.ContainsKey(index)) throw new InvalidOperationException("Controller slot already exists.");
                var (backend, profile) = GetBackend();
                var mapper = new MoonlightControllerMapper(profile);
                // A stable identity retains per-controller Steam bindings
                // when the same streaming slot is used in a later session.
                var controller = backend.CreateController(profile, $"apollo:dualsense-edge:{index}");
                var session = new Session(controller, mapper, feedback, feedbackContext);
                try { session.Update(default); Sessions[index] = session; }
                catch { session.Dispose(); throw; }
            }
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    // Caller holds Lifecycle, including while the SDK allocates an index.
    private static (HMContext, HMProfile) GetBackend()
    {
        const string profileId = "dualsense-edge-usb";
        if (!HMContext.IsUsbipBackendAvailable)
            throw new InvalidOperationException("Install and start the signed usbip-win2 driver before selecting DualSense Edge. See docs/dualsense-edge.md.");
        if (context == null)
        {
            // Driver setup is a separate administrator action. This mode
            // neither installs nor repairs drivers, and loads no native SDK
            // payloads. Only the in-process USB/IP backend is used.
            var candidate = new HMContext(manageDrivers: false);
            try
            {
                string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                candidate.LoadProfilesFromDirectory(Path.Combine(directory, "Profiles"));
                context = candidate;
            }
            catch { candidate.Dispose(); throw; }
        }
        return (context, context.GetProfile(profileId) ??
            throw new InvalidOperationException($"The bundled {profileId} profile is missing."));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Update(int index, ControllerState* state)
    {
        try
        {
            if (state == null || state->Reserved != 0) throw new ArgumentException("Invalid controller state ABI.");
            if (!Sessions.TryGetValue(index, out var session)) throw new InvalidOperationException("Controller slot is not allocated.");
            session.Update(*state);
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Motion(int index, ControllerMotion* motion)
    {
        try
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            if (!Sessions.TryGetValue(index, out var session)) throw new InvalidOperationException("Controller slot is not allocated.");
            session.Motion(*motion);
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Touch(int index, ControllerTouch* touch)
    {
        try
        {
            if (touch == null) throw new ArgumentNullException(nameof(touch));
            if (!Sessions.TryGetValue(index, out var session)) throw new InvalidOperationException("Controller slot is not allocated.");
            session.Touch(*touch);
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Battery(int index, ControllerBattery* battery)
    {
        try
        {
            if (battery == null) throw new ArgumentNullException(nameof(battery));
            if (!Sessions.TryGetValue(index, out var session)) throw new InvalidOperationException("Controller slot is not allocated.");
            session.Battery(*battery);
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Free(int index)
    {
        try
        {
            lock (Lifecycle)
                if (Sessions.TryRemove(index, out var session)) session.Dispose();
            return 0;
        }
        catch (Exception error) { return Fail(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Shutdown()
    {
        lock (Lifecycle)
        {
            foreach (int index in Sessions.Keys)
            {
                if (!Sessions.TryRemove(index, out var session)) continue;
                try { session.Dispose(); } catch (Exception error) { Fail(error); }
            }
            try { context?.Dispose(); } catch (Exception error) { Fail(error); }
            context = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LastError(byte* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0) return 0;
        // A native caller asks on the same thread as the failed operation.
        // Leave a terminator even if an unusually long message is truncated.
        byte[] bytes = Encoding.UTF8.GetBytes(lastError ?? "Unknown controller backend error.");
        int length = Math.Min(bytes.Length, capacity - 1);
        bytes.AsSpan(0, length).CopyTo(new Span<byte>(buffer, length));
        buffer[length] = 0;
        return length;
    }

    private static int Fail(Exception error) { lastError = error.Message; return -1; }

    private sealed class Session : IDisposable
    {
        private readonly HMController controller;
        private readonly MoonlightControllerMapper mapper;
        private readonly DualSenseState extended = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private HMGamepadState buttons;
        private readonly delegate* unmanaged[Cdecl]<nint, ControllerFeedback*, void> feedback;
        private readonly nint feedbackContext;
        private readonly object feedbackGate = new();
        private readonly object gate = new();
        private bool disposed;
        private bool feedbackClosed;

        public Session(HMController controller, MoonlightControllerMapper mapper,
            delegate* unmanaged[Cdecl]<nint, ControllerFeedback*, void> feedback, nint feedbackContext)
        {
            this.controller = controller;
            this.mapper = mapper;
            this.feedback = feedback;
            this.feedbackContext = feedbackContext;
            if (feedback != null) controller.OutputReceived += OutputReceived;
        }

        private void OutputReceived(HMController sender, HMOutputPacket packet)
        {
            if (!DualSenseFeedback.TryDecode(packet, out var report)) return;
            lock (feedbackGate)
            {
                if (!feedbackClosed) feedback(feedbackContext, &report);
            }
        }

        // Caller holds gate. Each channel changes only its own state, so a
        // button packet cannot erase motion, touch contacts, or battery data.
        private void Submit() => controller.SubmitState(
            extended.Merge(buttons, DualSenseState.Timestamp(clock.Elapsed)));

        public void Update(ControllerState state)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                buttons = mapper.Map(state);
                Submit();
            }
        }

        public void Motion(ControllerMotion motion)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                extended.Motion(motion);
                Submit();
            }
        }

        public void Touch(ControllerTouch touch)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                extended.Touch(touch);
                Submit();
            }
        }

        public void Battery(ControllerBattery battery)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                extended.Battery(battery);
                Submit();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                // Finish any callback before the native context can be freed.
                // Do not hold feedbackGate while joining the SDK reader.
                lock (feedbackGate) feedbackClosed = true;
                controller.OutputReceived -= OutputReceived;
                try { controller.SubmitState(mapper.Map(default)); }
                finally { controller.Dispose(); }
            }
        }
    }
}
