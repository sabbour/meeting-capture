# Contributing

Contributions and bug reports are welcome. Keep changes focused, follow the existing code style, and include a regression test when a change affects behavior.

## Build and test

Build the Windows app with the .NET 10 SDK:

```powershell
dotnet build src\MeetingCapture\MeetingCapture.csproj -c Release
```

Run the offline regression suite from the repository root:

```powershell
dotnet restore tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj -c Release --no-restore
```

The regression suite uses synthetic audio and service responses. Changes that depend on actual audio devices, Windows UI behavior, or cloud accounts may also need a manual check on Windows.

## Keep private data out of Git

Do not commit `appsettings.json`, service keys, local settings, logs, or meeting recordings and notes. Use the placeholder `src\MeetingCapture\appsettings.template.json` for settings documentation; do not put real credentials in it.
