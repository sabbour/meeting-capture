using System.Xml.Linq;
using MeetingCapture.TrayApp;

namespace MeetingCapture.Regression;

internal static class AboutChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("About links are the author's verified https profiles", () =>
        {
            Check.Equal("https://github.com/sabbour|https://www.linkedin.com/in/ahmedsabbour/|https://x.com/sabbour",
                string.Join('|', AboutInfo.SocialLinks.Select(l => l.Url)));
            foreach (var url in AboutInfo.SocialLinks.Select(l => l.Url).Concat(AboutInfo.Credits.Select(c => c.Url)))
                Check.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, $"Not https: {url}");
            return Task.CompletedTask;
        });

        yield return ("About shows a non-empty version", () =>
        {
            Check.True(!string.IsNullOrWhiteSpace(AboutInfo.Version()), "version");
            Check.True(!AboutInfo.Version().Contains('+'), "build metadata stripped");
            return Task.CompletedTask;
        });

        yield return ("Every direct package reference is credited with a license", () =>
        {
            var csproj = FindUp(Path.Combine("src", "MeetingCapture", "MeetingCapture.csproj"));
            var packages = XDocument.Load(csproj).Descendants("PackageReference")
                .Select(p => (string?)p.Attribute("Include")).OfType<string>().ToArray();
            Check.True(packages.Length > 0, "no packages found");
            foreach (var id in packages)
            {
                var credit = AboutInfo.CreditFor(id);
                Check.True(credit is not null && credit.License.Length > 0, $"Uncredited package {id}");
            }
            return Task.CompletedTask;
        });

        yield return ("Third-party notices name every credited package", () =>
        {
            var notices = File.ReadAllText(FindUp("THIRD-PARTY-NOTICES.md"));
            foreach (var credit in AboutInfo.Credits)
                Check.True(notices.Contains(credit.PackagePrefix, StringComparison.OrdinalIgnoreCase), $"Missing {credit.PackagePrefix}");
            return Task.CompletedTask;
        });

        yield return ("Project MIT license and additional dependency notices are present", () =>
        {
            var license = File.ReadAllText(FindUp("LICENSE"));
            Check.True(license.StartsWith("MIT License", StringComparison.Ordinal), "MIT license");
            Check.True(license.Contains("Copyright (c) 2026 Ahmed Sabbour", StringComparison.Ordinal), "copyright");
            var notices = File.ReadAllText(FindUp("THIRD-PARTY-NOTICES.md"));
            foreach (var name in new[] { "Std.UriTemplate", "Microsoft.Web.WebView2",
                "Microsoft.Identity.Client.NativeInterop", "CopilotSkipCliDownload=true", "https://github.com/dotnet/runtime" })
                Check.True(notices.Contains(name, StringComparison.Ordinal), $"Missing {name}");
            return Task.CompletedTask;
        });
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }
}
