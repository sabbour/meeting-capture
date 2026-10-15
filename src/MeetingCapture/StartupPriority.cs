using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MeetingCapture;

/// <summary>
/// Windows "Efficiency mode" (set by Task Manager or other tools, and also the Idle/Below-normal
/// priority a low-priority launcher passes on) puts the process at Idle priority and asks Windows
/// to run it slowly. Then every app thread waits behind ordinary work on the machine, garbage
/// collection cannot pause threads promptly, and the window and recording stall for seconds or
/// minutes. Turn it off at startup and keep it off if something switches it back on.
/// </summary>
internal static class StartupPriority
{
    internal const uint IdlePriorityClass = 0x40;
    internal const uint BelowNormalPriorityClass = 0x4000;
    internal const uint NormalPriorityClass = 0x20;
    internal const string LogFileName = "startup-priority.log";
    internal static readonly TimeSpan FastRecheckInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SlowRecheckInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FastRecheckPeriod = TimeSpan.FromSeconds(30);
    internal const string WatchdogThreadName = "MeetingCapture-scheduling-watchdog";

    private static readonly object s_logGate = new();
    private static Thread? s_watchdog;

    // Test hook: redirect the log file. Null uses %LOCALAPPDATA%\MeetingCapture\Diagnostics.
    internal static string? LogPathOverride { get; set; }

    internal static Thread? Watchdog => s_watchdog;

    /// <summary>The class to switch to, or null when the current class must be left alone.</summary>
    internal static uint? Replacement(uint currentClass) =>
        currentClass is IdlePriorityClass or BelowNormalPriorityClass ? NormalPriorityClass : null;

    /// <summary>1 s between checks for the first 30 s after start, then 5 s.</summary>
    internal static TimeSpan RecheckDelay(TimeSpan sinceStart) =>
        sinceStart < FastRecheckPeriod ? FastRecheckInterval : SlowRecheckInterval;

    /// <summary>Turns off Efficiency mode's slow-execution request for this process.</summary>
    internal static bool DisableEfficiencyMode()
    {
        // Clear only the execution-speed bit; leave unrelated process power-throttling settings untouched.
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = 0,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>True when Windows reports the slow-execution (Efficiency mode) request is on.</summary>
    internal static bool IsEfficiencyModeOn()
    {
        var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion };
        // Windows reports both whether the bit is controlled and whether it is enabled.
        return GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                   (uint)Marshal.SizeOf<PowerThrottlingState>())
               && (state.ControlMask & PowerThrottlingExecutionSpeed) != 0
               && (state.StateMask & PowerThrottlingExecutionSpeed) != 0;
    }

    /// <summary>Restores normal scheduling now. Logs any change or failure. Never throws.</summary>
    internal static void EnsureNormalScheduling()
    {
        try
        {
            // Read the error immediately after a failed native call before another call can replace it.
            var priority = GetPriorityClass(GetCurrentProcess());
            var priorityError = priority == 0 ? Marshal.GetLastPInvokeError() : 0;
            var efficiencyOn = IsEfficiencyModeOn();

            var changes = new List<string>();
            var failures = new List<string>();
            if (priority == 0)
                failures.Add($"GetPriorityClass failed (Win32 error {priorityError})");
            if (efficiencyOn)
            {
                if (DisableEfficiencyMode()) changes.Add("Efficiency mode turned off");
                else failures.Add($"Efficiency mode could not be turned off (Win32 error {Marshal.GetLastPInvokeError()})");
            }
            if (Replacement(priority) is { } replacement)
            {
                if (SetPriorityClass(GetCurrentProcess(), replacement))
                    changes.Add($"priority class 0x{priority:x} raised to 0x{replacement:x}");
                else
                    failures.Add($"priority class 0x{priority:x} could not be raised (Win32 error {Marshal.GetLastPInvokeError()})");
            }

            if (changes.Count == 0 && failures.Count == 0) return;
            // Record only actual repairs and failures; a normal process needs no diagnostic entry.
            Log($"found priorityClass=0x{priority:x} efficiencyMode={(efficiencyOn ? "on" : "off")}; " +
                (changes.Count > 0 ? "changed: " + string.Join(", ", changes) : "changed: nothing") +
                (failures.Count > 0 ? "; failed: " + string.Join(", ", failures) : string.Empty));
        }
        catch (Exception ex)
        {
            Log($"scheduling check failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Restores normal scheduling now, then keeps checking on a dedicated time-critical thread.
    /// A time-critical thread still runs promptly inside an Idle-class process, so the check is
    /// not starved by the condition it repairs (a thread-pool timer was).
    /// </summary>
    internal static void StartKeepingNormalScheduling()
    {
        EnsureNormalScheduling();
        try
        {
            if (s_watchdog is not null) return;
            // Recheck quickly during startup, then less often; the dedicated thread can still run if the app is demoted.
            var started = Stopwatch.StartNew();
            var watchdog = new Thread(() =>
            {
                RaiseCurrentThreadToTimeCritical();
                while (true)
                {
                    try { Thread.Sleep(RecheckDelay(started.Elapsed)); }
                    catch (ThreadInterruptedException) { }
                    EnsureNormalScheduling();
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = WatchdogThreadName,
            };
            if (Interlocked.CompareExchange(ref s_watchdog, watchdog, null) is not null) return;
            watchdog.Start();
        }
        catch (Exception ex)
        {
            Log($"scheduling watchdog could not start ({ex.GetType().Name})");
        }
    }

    // ThreadPriority.Highest maps to THREAD_PRIORITY_HIGHEST; raise the watchdog itself to
    // THREAD_PRIORITY_TIME_CRITICAL once it is running.
    private static void RaiseCurrentThreadToTimeCritical()
    {
        if (!SetThreadPriority(GetCurrentThread(), ThreadPriorityTimeCritical))
            Log($"watchdog thread could not be made time-critical (Win32 error {Marshal.GetLastPInvokeError()})");
    }

    internal static string LogPath() => LogPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingCapture", "Diagnostics", LogFileName);

    private static void Log(string message)
    {
        try
        {
            var path = LogPath();
            lock (s_logGate)
            {
                // Serialize appends and create the folder only when a diagnostic is actually needed.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) +
                    $" pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect startup or recording.
        }
    }

    private const int ThreadPriorityTimeCritical = 15;
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, uint size);

    // Test hooks: read the class and set the conditions Efficiency mode creates.
    internal static uint CurrentPriorityClassForTest() => GetPriorityClass(GetCurrentProcess());

    internal static bool SetPriorityClassForTest(uint priorityClass) =>
        SetPriorityClass(GetCurrentProcess(), priorityClass);

    internal static bool EnableEfficiencyModeForTest()
    {
        // Test hook mirrors the two bits Windows uses to request reduced execution speed.
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = PowerThrottlingExecutionSpeed,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
    }
}
