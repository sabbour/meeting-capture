
MeetingCapture
==============

## Architecture

```
MeetingDetection (ProcessMeetingDetector)
    └─ polls every 10s for known processes (Teams, Zoom, etc.)
    └─ fires MeetingStarted / MeetingEnded events

MeetingPipeline (orchestrator)
    ├─ on MeetingStarted → StartCaptureAsync()
    │       ├─ MicrophoneCapture  (NAudio WaveInEvent, 16kHz mono)
    │       ├─ LoopbackCapture    (WASAPI loopback, resampled to 16kHz mono)
    │       └─ ITranscriptionService.StartAsync()
    │              └─ each DataAvailable → PushAudioAsync()
    │
    └─ on MeetingEnded → StopCaptureAndProcessAsync()
            ├─ ITranscriptionService.StopAsync() → TranscriptionResult
            ├─ INotesIntelligence.SynthesizeAsync() → MeetingNotes (Microsoft Foundry / GPT-4o)
            └─ INotesStorage.SaveAsync() → Markdown file in OneDrive

TrayApplicationContext (WinForms NotifyIcon)
    ├─ Shows status in tooltip / balloon
    ├─ Manual start/stop override
    └─ "Open Notes Folder" shortcut
```

## Key Interfaces

| Interface | Implementation | Swap Point |
|-----------|---------------|------------|
| `IMeetingDetector` | `ProcessMeetingDetector` | Bishop: add WASAPI session detector |
| `IAudioCapture` | `MicrophoneCapture`, `LoopbackCapture` | Vasquez: add AEC mixer |
| `ITranscriptionService` | `AzureSpeechTranscriptionService` | Vasquez: add NPU/Whisper impl |
| `INotesIntelligence` | `CopilotNotesService` | Apone: refine prompts / structure |
| `INotesStorage` | `MarkdownNotesStorage` | Apone: add JSON sidecar |

## Configuration (appsettings.json)

All settings read from `appsettings.json` + environment variable overrides:

| Setting | Env Override | Purpose |
|---------|-------------|---------|
| `Transcription:AzureSpeechKey` | `AZURE_SPEECH_KEY` | Azure Speech SDK key |
| `Transcription:AzureSpeechRegion` | — | Azure region (default: eastus) |
| `Intelligence:ApiKey` | `AZURE_OPENAI_API_KEY`, `AZURE_AI_FOUNDRY_API_KEY` | Microsoft Foundry / Azure OpenAI key |
| `Intelligence:Endpoint` | — | Foundry-compatible endpoint |
| `Intelligence:Model` | — | Default: gpt-4o |
| `Storage:NotesPath` | — | OneDrive notes output folder |

## Phase Roadmap

### Phase 1 (now — this skeleton)
- [x] WinForms tray app shell
- [x] Process-based meeting detection
- [x] Microphone capture (NAudio)
- [x] WASAPI loopback capture (system audio)
- [x] Azure Speech SDK transcription (streaming push)
- [x] Microsoft Foundry / GPT-4o note synthesis
- [x] Markdown note persistence to OneDrive

### Phase 2 (next — Bishop + Vasquez)
- [ ] WASAPI audio session notifications for detection (more reliable than process poll)
- [ ] Acoustic Echo Cancellation (mix mic + loopback with AEC)
- [ ] NPU transcription path (Windows ML / Whisper ONNX)
- [ ] Meeting app window title extraction for better note titles

### Phase 3 (later — Apone)
- [ ] Speaker diarization
- [ ] JSON sidecar alongside Markdown
- [ ] Incremental/live notes streaming
- [ ] Policy toggles (auto-start, record consent prompt)
