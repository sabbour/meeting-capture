# Meeting Capture

Meeting Capture is a Windows tray app for taking notes during meetings. It captures microphone and system audio, sends the audio to Azure Speech for transcription, and can use GitHub Copilot or Azure AI to create live topic notes and a final summary. You can start a recording yourself or confirm a meeting suggestion; the app does not start recording automatically.

Work IQ is optional. When you sign in, the app can use your work calendar and related Microsoft 365 content to add context. You do not need to sign in to record or save a transcript.

## Requirements

- Windows 10 or later on ARM64 or x64. The project targets Windows 10 build 19041 and declares a minimum build of 17763; Windows App SDK requirements also apply.
- An Azure Speech endpoint and key for transcription.
- For AI notes, either a GitHub account with Copilot access and the GitHub CLI, or an Azure AI endpoint, key, and deployments. AI notes are optional.
- A work account is needed only for optional Work IQ calendar and related-content features.

## Install

1. Open [GitHub Releases](https://github.com/sabbour/meeting-capture/releases/latest).
2. Download the signed archive for your device: **ARM64** for Windows on ARM, or **x64** for Intel and AMD 64-bit Windows PCs.
3. Unzip the archive, then run `MeetingCapture.exe`.

The app runs as your Windows user and does not need an installer.

## First-run setup

Open **Settings** in the app to configure the services you want to use:

1. **Transcription:** Enter your Azure Speech endpoint and key. The app uses Azure Speech for transcription. Audio capture and manual recording are available without a Work IQ sign-in, but transcription needs a working Speech service configuration.
2. **Meeting notes:** Choose **GitHub Copilot** or **Azure AI**. For GitHub Copilot, install the GitHub CLI if needed and sign in on this PC with `gh auth login` using an account that has Copilot access. Then select and test separate models for live insights and final notes. For Azure AI, enter the endpoint, key, API version, and deployment names, then test each model.
3. **Optional Work IQ:** Sign in with your work account to use calendar titles, attendee details, and related work content. Your organization may require consent.
4. Save your settings. Restart the app when it says a setting needs a restart; changes to the AI provider and models apply to new requests.

Both microphone and system audio are enabled by default. Check the audio settings before recording if you do not want to capture one of them.

Settings that you save in the app are stored in `%APPDATA%\MeetingCapture\settings.user.json`. This file is not encrypted. Keep it private, especially if it contains service keys.

## Notes and files

By default, notes are saved under `Microsoft Scout\Meetings` in your Windows Documents folder. You can change the folder in **Settings**. Each saved meeting has a Markdown file with the notes and recognized transcript, plus a JSON file with the topic-map data when it is available. The app does not save a copy of the raw audio. Files remain until you remove them; the app does not apply a retention or deletion policy.

## Privacy

- Microphone and system audio are sent to Azure Speech for transcription. The app does not keep a raw audio backup.
- Transcript text is sent to the selected GitHub Copilot or Azure AI service to create notes. Notes and transcript files are saved in the folder you configure.
- Work IQ requests use your signed-in work account and are optional. Work IQ sign-in is not needed to record.
- The app stores Work IQ sign-in data in a Windows-protected local cache. Service keys saved through Settings are stored in the user settings file described above.
- The Copilot runtime may keep its own session history. The app does not promise zero retention by an external service.

Read the [architecture guide](docs/ARCHITECTURE.md) for more about the components and data flow.

## Build from source

Build on Windows with the .NET 10 SDK. The default build targets ARM64:

```powershell
dotnet build src\MeetingCapture\MeetingCapture.csproj -c Release
```

To build for an x64 PC:

```powershell
dotnet build src\MeetingCapture\MeetingCapture.csproj -c Release -r win-x64
```

`src\MeetingCapture\appsettings.template.json` lists the available settings and uses placeholders for credentials and endpoints. The app does not load that template automatically. For development, configure the app in Settings or use local configuration, user secrets, or environment variables. Do not commit `appsettings.json`, service keys, or meeting files.

## Run the regression tests

The regression project is a Windows console test suite. Restore its dependencies, then run it from the repository root:

```powershell
dotnet restore tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project tests\MeetingCapture.Regression\MeetingCapture.Regression.csproj -c Release --no-restore
```

The suite uses synthetic audio and service responses, so it does not need a microphone or cloud credentials.

## Project layout

| Path | Contents |
|---|---|
| `src\MeetingCapture\` | WinUI 3 tray application |
| `src\MeetingCapture\AudioCapture\` | Microphone, system-audio, and audio-mixing code |
| `src\MeetingCapture\MeetingDetection\` | Meeting-activity suggestions |
| `src\MeetingCapture\Transcription\` | Azure Speech streaming transcription |
| `src\MeetingCapture\Pipeline\` | Recording, transcription, and save lifecycle |
| `src\MeetingCapture\Intelligence\` | Copilot/Azure AI requests and live meeting notes |
| `src\MeetingCapture\WorkIq\` | Optional work-calendar and related-content access |
| `src\MeetingCapture\Storage\` | Markdown notes and topic-map files |
| `src\MeetingCapture\TrayApp\` | Main window, tray menu, and presentation code |
| `tests\MeetingCapture.Regression\` | Offline regression tests |
| `docs\ARCHITECTURE.md` | Component overview and data-flow diagram |

## License

Meeting Capture is licensed under the [MIT License](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency credits and license terms.
