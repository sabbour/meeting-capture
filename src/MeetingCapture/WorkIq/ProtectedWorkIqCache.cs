using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Microsoft.Identity.Client;

namespace MeetingCapture.WorkIq;

public sealed class ProtectedWorkIqCache
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingCapture", "workiq.msal.bin");
    private readonly string _path;
    private readonly ConditionalWeakTable<IPublicClientApplication, Snapshot> _snapshots = new();
    private sealed class Snapshot { public byte[]? Encrypted; }
    public ProtectedWorkIqCache(string? path = null) => _path = path ?? DefaultPath;

    public void Load(IPublicClientApplication app)
    {
        Attach(app, restore: true);
    }

    public void Attach(IPublicClientApplication app, bool restore = false)
    {
        var snapshot = _snapshots.GetOrCreateValue(app);
        var loaded = false;
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (loaded) return;
            byte[]? clear = null;
            try
            {
                if (restore && File.Exists(_path))
                {
                    clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
                    args.TokenCache.DeserializeMsalV3(clear, shouldClearExistingCache: true);
                }
                loaded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or
                MsalException or System.Text.Json.JsonException or FormatException)
            {
                throw new WorkIqException(WorkIqFailure.Cache);
            }
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            var clear = args.TokenCache.SerializeMsalV3();
            try { snapshot.Encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new WorkIqException(WorkIqFailure.Cache); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        });
    }

    public void Save(IPublicClientApplication app)
    {
        var temp = _path + ".tmp";
        try
        {
            if (!_snapshots.TryGetValue(app, out var snapshot) || snapshot.Encrypted is null)
                throw new WorkIqException(WorkIqFailure.Cache);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temp, snapshot.Encrypted);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkIqException(WorkIqFailure.Cache);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
            File.Delete(_path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkIqException(WorkIqFailure.Cache);
        }
    }
}
