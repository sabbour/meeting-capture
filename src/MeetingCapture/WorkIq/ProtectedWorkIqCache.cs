using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Microsoft.Identity.Client;

namespace MeetingCapture.WorkIq;

/// <summary>Stores the Microsoft sign-in token cache for WorkIQ on disk in encrypted form.</summary>
public sealed class ProtectedWorkIqCache
{
    /// <summary>Gets the default per-user path for the protected Work IQ token cache.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingCapture", "workiq.msal.bin");
    private readonly string _path;
    private readonly ConditionalWeakTable<IPublicClientApplication, Snapshot> _snapshots = new();
    private sealed class Snapshot { public byte[]? Encrypted; }
    public ProtectedWorkIqCache(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Registers cache handlers that restore persisted token data on first access.</summary>
    public void Load(IPublicClientApplication app)
    {
        Attach(app, restore: true);
    }

    /// <summary>Registers callbacks to optionally restore and protect token-cache data for an application.</summary>
    public void Attach(IPublicClientApplication app, bool restore = false)
    {
        // MSAL invokes these callbacks around token-cache access; the per-app snapshot keeps the
        // encrypted bytes ready for an explicit Save after the operation finishes.
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
                    // DPAPI binds the file to this Windows user. Clear MSAL's cache before
                    // deserializing so stale in-memory tokens cannot survive restoration.
                    clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
                    args.TokenCache.DeserializeMsalV3(clear, shouldClearExistingCache: true);
                }
                loaded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or
                MsalException or System.Text.Json.JsonException or FormatException)
            {
                // Surface one safe cache error instead of silently signing in with a partial cache.
                throw new WorkIqException(WorkIqFailure.Cache);
            }
            // Plain token-cache bytes exist only for the duration of deserialization.
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            // MSAL serializes its current state after a token operation; encrypt immediately and
            // retain only ciphertext until the service saves it.
            var clear = args.TokenCache.SerializeMsalV3();
            try { snapshot.Encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new WorkIqException(WorkIqFailure.Cache); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        });
    }

    /// <summary>Persists the latest protected token-cache snapshot captured by the registered callback.</summary>
    public void Save(IPublicClientApplication app)
    {
        var temp = _path + ".tmp";
        try
        {
            // Write a sibling temporary file and replace the destination so a crash cannot leave
            // a half-written protected cache.
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

    /// <summary>Deletes the persisted token cache and any leftover temporary file.</summary>
    public void Clear()
    {
        try
        {
            // Remove both the last committed file and any interrupted save's temporary file.
            File.Delete(_path);
            File.Delete(_path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkIqException(WorkIqFailure.Cache);
        }
    }
}
