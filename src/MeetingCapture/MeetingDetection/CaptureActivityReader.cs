using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingCapture.MeetingDetection;

/// <summary>Observes capture-session state only. It never opens a microphone or reads audio samples.</summary>
internal static class CaptureActivityReader
{
    public static HashSet<int> ReadActiveProcessIds()
    {
        var active = new HashSet<int>();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    if (session.State == AudioSessionState.AudioSessionStateActive &&
                        session.GetProcessID is > 0 and <= int.MaxValue)
                        active.Add((int)session.GetProcessID);
                }
            }
        }
        return active;
    }
}
