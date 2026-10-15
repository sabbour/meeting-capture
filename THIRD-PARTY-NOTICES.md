# Third-party notices

Meeting Capture is created by Ahmed Sabbour.
Meeting Capture's own source is licensed under the MIT License (see LICENSE).

Meeting Capture uses the following packages. License identifiers come from each package's NuGet metadata
(versions are referenced in `src/MeetingCapture/MeetingCapture.csproj` or its resolved dependencies). The full license text ships inside
each package and is available from the linked project.

| Package | License | Project |
| --- | --- | --- |
| Microsoft.WindowsAppSDK | Microsoft Software License Terms (in package `license.txt`) | https://github.com/microsoft/windowsappsdk |
| H.NotifyIcon.WinUI | MIT | https://github.com/HavenDV/H.NotifyIcon |
| NAudio | MIT, Copyright 2020 Mark Heath | https://github.com/naudio/NAudio |
| Microsoft.CognitiveServices.Speech | Microsoft Speech SDK license terms | https://aka.ms/csspeech/license |
| GitHub.Copilot.SDK | MIT | https://github.com/github/copilot-sdk |
| Microsoft.Identity.Client, Microsoft.Identity.Client.Broker | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.Identity.Client.NativeInterop | Microsoft Software License Terms (in package `LICENSE`) | https://www.nuget.org/packages/Microsoft.Identity.Client.NativeInterop |
| Microsoft.Web.WebView2 | BSD-3-Clause (in package `LICENSE.txt`; additional notices in `NOTICE.txt`) | https://aka.ms/webview |
| Microsoft.Graph | MIT (Copyright 2019 Microsoft Graph) | https://github.com/microsoftgraph/msgraph-sdk-dotnet |
| Microsoft.Kiota.Abstractions, Microsoft.Kiota.Http.HttpClientLibrary | MIT | https://github.com/microsoft/kiota-dotnet |
| Std.UriTemplate | Apache-2.0 | https://www.nuget.org/packages/Std.UriTemplate |
| Microsoft.Extensions.* (Configuration, DependencyInjection, Hosting, Logging) | MIT | https://github.com/dotnet/runtime |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |

Transitive dependencies carry their own licenses in their packages.

The app runs on .NET, licensed under MIT: https://github.com/dotnet/runtime.
Self-contained distributions also need the runtime's third-party notices.

The GitHub Copilot CLI is downloaded under its own terms unless the app is built with
`CopilotSkipCliDownload=true`. The CLI's terms are separate from the MIT-licensed Copilot SDK:
https://www.npmjs.com/package/@github/copilot.
