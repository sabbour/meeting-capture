using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace MeetingCapture.WorkIq;

/// <summary>How far a roster result can be trusted: verified live, not available, or unclear.</summary>
public enum ActiveRosterAvailability { VerifiedLive, Unavailable, Indeterminate }

/// <summary>Where a roster came from.</summary>
public enum ActiveRosterSource { WorkIq, TeamsUiAutomation }

/// <summary>A person seen in the live meeting roster.</summary>
public sealed record ActiveRosterMember(string DisplayName);

/// <summary>A list of people seen in a live meeting at one time. <c>Limitation</c> explains any gaps.</summary>
public sealed record ActiveRosterSnapshot(
    ActiveRosterAvailability Availability,
    ActiveRosterSource Source,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ActiveRosterMember> Members,
    string? MeetingReference,
    bool IsPartial,
    string Limitation);

/// <summary>Reads who is in the meeting that is happening now.</summary>
public interface IActiveRosterProvider
{
    /// <summary>Returns the current roster. Gaps are described in the snapshot instead of thrown.</summary>
    Task<ActiveRosterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>The title of the meeting that is happening now, if it could be verified. <c>Limitation</c> explains why not.</summary>
public sealed record ActiveMeetingIdentitySnapshot(string? Title, string Limitation)
{
    public bool IsVerified => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>Reads the title of the meeting that is happening now.</summary>
public interface IActiveMeetingIdentityProvider
{
    Task<ActiveMeetingIdentitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the active Teams meeting title through Windows UI Automation, with a short time limit.</summary>
public sealed class TeamsUiAutomationMeetingIdentityProvider : IActiveMeetingIdentityProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _timeout;
    private readonly Func<CancellationToken, Task<string>> _invokeHelper;

    public TeamsUiAutomationMeetingIdentityProvider()
        : this(DefaultTimeout, TeamsUiAutomationRosterProvider.RunHelperProcessAsync)
    {
    }

    internal TeamsUiAutomationMeetingIdentityProvider(
        TimeSpan timeout,
        Func<CancellationToken, Task<string>> invokeHelper)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _invokeHelper = invokeHelper;
    }

    public async Task<ActiveMeetingIdentitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var json = await _invokeHelper(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            return ParseHelperResult(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, "Teams meeting title inspection timed out.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new(null, "Teams meeting title could not be verified.");
        }
    }

    internal static ActiveMeetingIdentitySnapshot ParseHelperResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000)
            return new(null, "Teams accessibility helper returned an invalid response.");
        var result = JsonSerializer.Deserialize<IdentityHelperResult>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result?.CallControlsVerified != true || string.IsNullOrWhiteSpace(result.CallTitle) ||
            result.CallTitle.Length > 256)
            return new(null, result?.Limitation ?? "No unique active Teams meeting title was exposed.");
        return new(result.CallTitle.Trim(), "Title verified from the active Teams joined-call surface.");
    }

    private sealed record IdentityHelperResult(
        bool? CallControlsVerified,
        string? CallTitle,
        string? Limitation);
}

/// <summary>Reads the Teams participant list through Windows UI Automation, with a short time limit.</summary>
public sealed class TeamsUiAutomationRosterProvider : IActiveRosterProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _timeout;
    private readonly Func<CancellationToken, Task<string>> _invokeHelper;

    public TeamsUiAutomationRosterProvider()
        : this(DefaultTimeout, RunHelperProcessAsync)
    {
    }

    internal TeamsUiAutomationRosterProvider(
        TimeSpan timeout,
        Func<CancellationToken, Task<string>> invokeHelper)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _invokeHelper = invokeHelper;
    }

    public async Task<ActiveRosterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var json = await _invokeHelper(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            return ParseHelperResult(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility inspection timed out; no roster was inferred.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility helper failed; no roster was inferred.");
        }
    }

    internal static ActiveRosterSnapshot ParseHelperResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000)
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility helper returned an invalid response.");
        var result = JsonSerializer.Deserialize<HelperResult>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result is null)
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility helper returned an invalid response.");
        if (result.State == "unavailable")
            return Snapshot(ActiveRosterAvailability.Unavailable,
                result.Limitation ?? "No joined Teams meeting Participants surface is currently available.",
                result.ObservedAt);
        if (result.State != "verified" || result.CallControlsVerified != true ||
            result.RosterKind != "MeetingParticipants")
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility response did not prove a live meeting roster.",
                result.ObservedAt);
        if (result.Members is null || result.Members.Length == 0 || result.Members.Length > 200 ||
            result.Members.Any(member => string.IsNullOrWhiteSpace(member) || member.Length > 256) ||
            result.MeetingReference?.Length > 256)
            return Snapshot(ActiveRosterAvailability.Indeterminate,
                "Teams accessibility response exceeded roster bounds.",
                result.ObservedAt);
        return new ActiveRosterSnapshot(
            ActiveRosterAvailability.VerifiedLive,
            ActiveRosterSource.TeamsUiAutomation,
            result.ObservedAt ?? DateTimeOffset.UtcNow,
            result.Members.Select(member => new ActiveRosterMember(member.Trim())).ToArray(),
            string.IsNullOrWhiteSpace(result.MeetingReference) ? null : result.MeetingReference.Trim(),
            IsPartial: true,
            result.Limitation ??
                "Displayed Teams rows may be collapsed or virtualized and do not establish speaking identity.");
    }

    private static ActiveRosterSnapshot Snapshot(
        ActiveRosterAvailability availability,
        string limitation,
        DateTimeOffset? observedAt = null)
        => new(availability, ActiveRosterSource.TeamsUiAutomation,
            observedAt ?? DateTimeOffset.UtcNow, [], null, IsPartial: true, limitation);

    internal static async Task<string> RunHelperProcessAsync(CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Process path unavailable.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, "--teams-roster-helper")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Teams roster helper did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Teams roster helper failed.");
            await errors.ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record HelperResult(
        string State,
        DateTimeOffset? ObservedAt,
        bool? CallControlsVerified,
        string? CallTitle,
        string? RosterKind,
        string? MeetingReference,
        string[]? Members,
        string? Limitation);
}

/// <summary>Helper that reads the Teams window with UI Automation and tells a meeting roster from a chat roster.</summary>
internal static partial class TeamsUiAutomationRosterHelper
{
    private const string MeetingRosterKind = "MeetingParticipants";
    private static readonly Regex Leave = LeaveRegex();
    private static readonly Regex MicrophoneOrCamera = MicrophoneOrCameraRegex();
    private static readonly Regex MeetingRoster = MeetingRosterRegex();
    private static readonly Regex ChatRoster = ChatRosterRegex();
    private static readonly Regex CallTitleId = CallTitleIdRegex();
    private static readonly Regex CallTitleName = CallTitleNameRegex();

    public static string Inspect()
    {
        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            var teamsPids = Process.GetProcessesByName("ms-teams").Select(process => process.Id).ToHashSet();
            if (teamsPids.Count == 0)
                return Result("unavailable", observedAt,
                    limitation: "Teams is not running; no live roster is available.");
            var windows = EnumerateTeamsWindows(teamsPids);
            var live = windows.Select(InspectWindow).Where(window => window.LiveControls).ToArray();
            if (live.Length == 0)
                return Result("unavailable", observedAt,
                    limitation: "No Teams window exposes joined-call controls.");
            if (live.Length != 1)
                return Result("indeterminate", observedAt,
                    limitation: "Multiple Teams call-control windows are visible.");

            var window = live[0];
            if (window.Roster is null)
                return Result("unavailable", observedAt, callControlsVerified: true,
                    callTitle: window.CallTitle,
                    limitation: "A joined call is visible. Open the Teams Participants pane to make its displayed roster available.");
            return Result("verified", observedAt, callControlsVerified: true,
                callTitle: window.CallTitle, rosterKind: MeetingRosterKind,
                meetingReference: window.CallTitle,
                members: window.Roster,
                limitation: "Roster reflects displayed Teams rows only; collapsed or virtualized rows may be omitted.");
        }
        catch
        {
            return Result("indeterminate", observedAt,
                limitation: "Teams accessibility inspection failed before a roster could be verified.");
        }
    }

    private static WindowObservation InspectWindow(IntPtr handle)
    {
        var root = AutomationElement.FromHandle(handle);
        var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var leave = false;
        var microphoneOrCamera = false;
        string[]? roster = null;
        var titleCandidates = new List<string>();
        for (var index = 0; index < descendants.Count; index++)
        {
            var element = descendants[index];
            var current = element.Current;
            var name = current.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!current.IsOffscreen &&
                current.ControlType is not null &&
                (current.ControlType == ControlType.Text || current.ControlType == ControlType.Header) &&
                IsVerifiedCallTitleMetadata(name, current.AutomationId))
                titleCandidates.Add(NormalizeCallTitle(name));
            if (current.ControlType == ControlType.Button)
            {
                leave |= IsVisibleLeaveControl(name, current.IsOffscreen);
                microphoneOrCamera |= IsVisibleMediaControl(name, current.IsOffscreen);
            }
            if (current.ControlType != ControlType.List || current.IsOffscreen || ChatRoster.IsMatch(name) ||
                !MeetingRoster.IsMatch(name))
                continue;
            var items = element.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            roster = items.Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen)
                .Select(item => item.Current.Name?.Trim())
                .Where(member => !string.IsNullOrWhiteSpace(member))
                .Select(member => member!).Take(201).ToArray();
        }
        return new WindowObservation(handle, leave && microphoneOrCamera,
            SelectUniqueCallTitle(titleCandidates), roster);
    }

    internal static bool IsVisibleLeaveControl(string name, bool isOffscreen)
        => !isOffscreen && Leave.IsMatch(name);

    internal static bool IsVisibleMediaControl(string name, bool isOffscreen)
        => !isOffscreen && MicrophoneOrCamera.IsMatch(name);

    internal static bool IsMeetingRosterName(string name)
        => !ChatRoster.IsMatch(name) && MeetingRoster.IsMatch(name);

    internal static bool IsVerifiedCallTitleMetadata(string name, string? automationId)
    {
        var normalized = NormalizeCallTitle(name);
        if (normalized.Length is < 2 or > 256 || IsGenericCallTitle(normalized))
            return false;
        return !string.IsNullOrWhiteSpace(automationId) && CallTitleId.IsMatch(automationId) ||
            CallTitleName.IsMatch(name);
    }

    internal static string? SelectUniqueCallTitle(IEnumerable<string> candidates)
    {
        var titles = candidates.Select(NormalizeCallTitle)
            .Where(title => title.Length is >= 2 and <= 256 && !IsGenericCallTitle(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return titles.Length == 1 ? titles[0] : null;
    }

    private static string NormalizeCallTitle(string name)
    {
        var value = string.Join(" ", name.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        var separator = value.IndexOf(':');
        if (separator > 0 && CallTitleName.IsMatch(value[..(separator + 1)]))
            value = value[(separator + 1)..].Trim();
        return value;
    }

    private static bool IsGenericCallTitle(string title) =>
        title.Equals("Meeting", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("Call", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("Teams", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("participants", StringComparison.OrdinalIgnoreCase) ||
        Leave.IsMatch(title) || MicrophoneOrCamera.IsMatch(title);

    private static IReadOnlyList<IntPtr> EnumerateTeamsWindows(IReadOnlySet<int> teamsPids)
    {
        var windows = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (!teamsPids.Contains((int)processId) || !IsWindowVisible(handle)) return true;
            var className = new StringBuilder(128);
            GetClassName(handle, className, className.Capacity);
            if (className.ToString() == "TeamsWebView") windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string Result(
        string state,
        DateTimeOffset observedAt,
        bool? callControlsVerified = null,
        string? callTitle = null,
        string? rosterKind = null,
        string? meetingReference = null,
        string[]? members = null,
        string? limitation = null)
        => JsonSerializer.Serialize(new
        {
            state,
            observedAt,
            callControlsVerified,
            callTitle,
            rosterKind,
            meetingReference,
            members,
            limitation
        });

    private sealed record WindowObservation(
        IntPtr Handle,
        bool LiveControls,
        string? CallTitle,
        string[]? Roster);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [GeneratedRegex(@"^(leave(?!\s+(chat|group|channel))|hang up|disconnect call)(\b|,)", RegexOptions.IgnoreCase)]
    private static partial Regex LeaveRegex();

    [GeneratedRegex(@"(^|\b)(microphone|mic|mute|unmute|camera|turn video|video)(\b|,)", RegexOptions.IgnoreCase)]
    private static partial Regex MicrophoneOrCameraRegex();

    [GeneratedRegex(@"^(in this meeting|people in this meeting|participants in this meeting|meeting participants)(\b|,)", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingRosterRegex();

    [GeneratedRegex(@"chat participants|add people and agents|invite(d)? (people|participants|attendees)|^attendees(\b|,)", RegexOptions.IgnoreCase)]
    private static partial Regex ChatRosterRegex();

    [GeneratedRegex(@"(^|[-_.])(meeting|call)[-_.]?(title|name|header)($|[-_.])|(^|[-_.])(title|name|header)[-_.]?(meeting|call)($|[-_.])", RegexOptions.IgnoreCase)]
    private static partial Regex CallTitleIdRegex();

    [GeneratedRegex(@"^(meeting|call)\s+title\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CallTitleNameRegex();
}
