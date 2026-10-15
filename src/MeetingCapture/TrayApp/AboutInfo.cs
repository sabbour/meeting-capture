using System.Reflection;

namespace MeetingCapture.TrayApp;

/// <summary>A link shown in the About dialog.</summary>
internal sealed record AboutLink(string Label, string Url);

/// <summary>A third-party package credit with its license, shown in the About dialog.</summary>
internal sealed record AboutCredit(string Name, string PackagePrefix, string License, string Url);

/// <summary>Static content for the About dialog: no UI types so it can be checked offline.</summary>
internal static class AboutInfo
{
    internal const string AppName = "Meeting Capture";
    internal const string Description =
        "A tray app that watches for meetings, captures and transcribes audio, and drafts AI notes.";
    internal const string Author = "Ahmed Sabbour";
    internal const string Copyright = "Copyright © 2026 Ahmed Sabbour";

    internal static readonly AboutLink[] SocialLinks =
    [
        new("GitHub", "https://github.com/sabbour"),
        new("LinkedIn", "https://www.linkedin.com/in/ahmedsabbour/"),
        new("X", "https://x.com/sabbour"),
    ];

    // PackagePrefix matches the PackageReference ids in MeetingCapture.csproj; licenses come from each package's nuspec.
    internal static readonly AboutCredit[] Credits =
    [
        new("Windows App SDK", "Microsoft.WindowsAppSDK", "Microsoft Software License Terms", "https://github.com/microsoft/windowsappsdk"),
        new("H.NotifyIcon", "H.NotifyIcon", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
        new("NAudio", "NAudio", "MIT", "https://github.com/naudio/NAudio"),
        new("Microsoft Cognitive Services Speech SDK", "Microsoft.CognitiveServices.Speech", "Microsoft Software License Terms", "https://aka.ms/csspeech/license"),
        new("GitHub Copilot SDK", "GitHub.Copilot.SDK", "MIT", "https://github.com/github/copilot-sdk"),
        new("Microsoft Authentication Library (MSAL)", "Microsoft.Identity.Client", "MIT", "https://github.com/AzureAD/microsoft-authentication-library-for-dotnet"),
        new("Microsoft Graph SDK", "Microsoft.Graph", "MIT", "https://github.com/microsoftgraph/msgraph-sdk-dotnet"),
        new("Kiota", "Microsoft.Kiota", "MIT", "https://github.com/microsoft/kiota-dotnet"),
        new(".NET Extensions (DI, configuration, hosting, logging)", "Microsoft.Extensions", "MIT", "https://github.com/dotnet/runtime"),
        new("Markdig", "Markdig", "BSD-2-Clause", "https://github.com/xoofx/markdig"),
    ];

    internal static string Version()
    {
        var assembly = typeof(AboutInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() : informational;
        if (string.IsNullOrWhiteSpace(version)) return "Development build";
        var build = version.IndexOf('+');
        return build > 0 ? version[..build] : version;
    }

    /// <summary>The credit that covers a package id, or null when the dependency is uncredited.</summary>
    internal static AboutCredit? CreditFor(string packageId)
        => Credits.FirstOrDefault(c => packageId.StartsWith(c.PackagePrefix, StringComparison.OrdinalIgnoreCase));
}
