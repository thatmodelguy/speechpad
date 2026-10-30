# Speech Pad

A small always-on-top Windows dictation pad. Speak notes, then copy them wherever you need.

## Features

- Local Whisper speech recognition (`small.en`) — free, no account required
- Microphone picker
- Toggle dictation with a one-click mic button
- Copy to clipboard, optional auto-copy when you stop
- Clear notes
- Dark, compact UI

## Run (release)

Download the latest **SpeechPad-standalone** zip from [Releases](../../releases), extract it, and run `SpeechPad.exe`.

On first launch the Whisper model downloads once (~500 MB) into `%LocalAppData%\SpeechPad\models\`.

## Build from source

```powershell
dotnet publish src\SpeechPad\SpeechPad.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o standalone
```

Requires the .NET 8 SDK to build. The published folder is self-contained.

## Requirements

- Windows 10/11 x64
- Microphone access
