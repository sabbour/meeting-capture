using System.Diagnostics;

namespace MeetingCapture.Regression;

internal static class StartupPriorityChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Startup raises only Idle and Below-normal priority classes", () =>
        {
            Check.Equal<uint?>(0x20, StartupPriority.Replacement(0x40));
            Check.Equal<uint?>(0x20, StartupPriority.Replacement(0x4000));
            Check.Equal<uint?>(null, StartupPriority.Replacement(0x20));
            Check.Equal<uint?>(null, StartupPriority.Replacement(0x8000));
            Check.Equal<uint?>(null, StartupPriority.Replacement(0x80));
            Check.Equal<uint?>(null, StartupPriority.Replacement(0x100));
            Check.Equal<uint?>(null, StartupPriority.Replacement(0));
            return Task.CompletedTask;
        });

        yield return ("Scheduling checks every second for 30 seconds, then every 5 seconds", () =>
        {
            Check.Equal(TimeSpan.FromSeconds(1), StartupPriority.RecheckDelay(TimeSpan.Zero));
            Check.Equal(TimeSpan.FromSeconds(1), StartupPriority.RecheckDelay(TimeSpan.FromSeconds(29.9)));
            Check.Equal(TimeSpan.FromSeconds(5), StartupPriority.RecheckDelay(TimeSpan.FromSeconds(30)));
            Check.Equal(TimeSpan.FromSeconds(5), StartupPriority.RecheckDelay(TimeSpan.FromHours(3)));
            return Task.CompletedTask;
        });

        yield return ("Efficiency mode and Idle/Below-normal priority are turned off and logged", () =>
        {
            using var temp = new TemporaryDirectory();
            var original = StartupPriority.CurrentPriorityClassForTest();
            StartupPriority.LogPathOverride = Path.Combine(temp.Path, StartupPriority.LogFileName);
            try
            {
                foreach (var low in new uint[] { 0x40, 0x4000 })
                {
                    Check.True(StartupPriority.EnableEfficiencyModeForTest());
                    Check.True(StartupPriority.SetPriorityClassForTest(low));
                    Check.True(StartupPriority.IsEfficiencyModeOn());

                    StartupPriority.EnsureNormalScheduling();

                    Check.True(!StartupPriority.IsEfficiencyModeOn());
                    Check.Equal(0x20u, StartupPriority.CurrentPriorityClassForTest());
                    using var process = Process.GetCurrentProcess();
                    Check.Equal(ProcessPriorityClass.Normal, process.PriorityClass);
                }

                var lines = File.ReadAllLines(StartupPriority.LogPathOverride);
                Check.Equal(2, lines.Length);
                Check.True(lines[0].Contains("found priorityClass=0x40 efficiencyMode=on", StringComparison.Ordinal));
                Check.True(lines[0].Contains("Efficiency mode turned off", StringComparison.Ordinal));
                Check.True(lines[0].Contains("priority class 0x40 raised to 0x20", StringComparison.Ordinal));
                Check.True(lines[1].Contains("priority class 0x4000 raised to 0x20", StringComparison.Ordinal));
                Check.True(lines.All(line => line.Contains($"pid={Environment.ProcessId}", StringComparison.Ordinal)));
                Check.True(lines.All(line => !line.Contains("failed", StringComparison.Ordinal)));

                StartupPriority.EnsureNormalScheduling();
                Check.Equal(2, File.ReadAllLines(StartupPriority.LogPathOverride).Length);
            }
            finally
            {
                StartupPriority.DisableEfficiencyMode();
                StartupPriority.SetPriorityClassForTest(original);
                StartupPriority.LogPathOverride = null;
            }
            return Task.CompletedTask;
        });

        yield return ("Higher priority classes are left alone and not logged", () =>
        {
            using var temp = new TemporaryDirectory();
            var original = StartupPriority.CurrentPriorityClassForTest();
            StartupPriority.LogPathOverride = Path.Combine(temp.Path, StartupPriority.LogFileName);
            try
            {
                Check.True(StartupPriority.SetPriorityClassForTest(0x8000));
                StartupPriority.EnsureNormalScheduling();
                Check.Equal(0x8000u, StartupPriority.CurrentPriorityClassForTest());
                Check.True(!File.Exists(StartupPriority.LogPathOverride));
            }
            finally
            {
                StartupPriority.SetPriorityClassForTest(original);
                StartupPriority.LogPathOverride = null;
            }
            return Task.CompletedTask;
        });

        yield return ("Logging failures never throw", () =>
        {
            var original = StartupPriority.CurrentPriorityClassForTest();
            StartupPriority.LogPathOverride = "\0invalid";
            try
            {
                Check.True(StartupPriority.SetPriorityClassForTest(0x40));
                StartupPriority.EnsureNormalScheduling();
                Check.Equal(0x20u, StartupPriority.CurrentPriorityClassForTest());
            }
            finally
            {
                StartupPriority.SetPriorityClassForTest(original);
                StartupPriority.LogPathOverride = null;
            }
            return Task.CompletedTask;
        });

        yield return ("Watchdog thread starts once, in the background, and repairs Idle priority", async () =>
        {
            using var temp = new TemporaryDirectory();
            var original = StartupPriority.CurrentPriorityClassForTest();
            StartupPriority.LogPathOverride = Path.Combine(temp.Path, StartupPriority.LogFileName);
            try
            {
                StartupPriority.StartKeepingNormalScheduling();
                var watchdog = StartupPriority.Watchdog;
                Check.True(watchdog is not null);
                Check.True(watchdog!.IsBackground);
                Check.True(watchdog.IsAlive);
                Check.Equal(StartupPriority.WatchdogThreadName, watchdog.Name);
                StartupPriority.StartKeepingNormalScheduling();
                Check.True(ReferenceEquals(watchdog, StartupPriority.Watchdog));

                Check.True(StartupPriority.SetPriorityClassForTest(0x40));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (StartupPriority.CurrentPriorityClassForTest() != 0x20 && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                Check.Equal(0x20u, StartupPriority.CurrentPriorityClassForTest());
                Check.True(!(await ReadSharedAsync(StartupPriority.LogPathOverride))
                    .Contains("could not be made time-critical", StringComparison.Ordinal));
            }
            finally
            {
                StartupPriority.SetPriorityClassForTest(original);
                // The watchdog keeps running in this test process; never let it write the real log.
                StartupPriority.LogPathOverride = "\0disabled";
            }
        });

        yield return ("Program turns off Efficiency mode before any UI or audio startup", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Program.cs.txt"));
            var raise = source.IndexOf("StartupPriority.StartKeepingNormalScheduling();", StringComparison.Ordinal);
            var comWrappers = source.IndexOf("WinRT.ComWrappersSupport.InitializeComWrappers();", StringComparison.Ordinal);
            var appStart = source.IndexOf("Application.Start(", StringComparison.Ordinal);
            Check.True(raise >= 0);
            Check.True(raise < comWrappers && raise < appStart);
            return Task.CompletedTask;
        });
    }
    // The watchdog may be appending while the test reads; share access and retry briefly.
    private static async Task<string> ReadSharedAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }
}