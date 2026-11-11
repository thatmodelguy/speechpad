# Speech Pad

A small always-on-top Windows dictation pad. Speak notes, then copy them wherever you need.

## Features

- Local Whisper speech recognition — free, no account required
- **Speech model picker**: *Fast* (`base.en`, ~140 MB, transcribes roughly 3× sooner) or *Accurate* (`small.en`, ~490 MB, better with names and unusual words)
- Uses most of your CPU cores while transcribing, so text appears sooner
- Microphone picker
- Toggle dictation with a one-click mic button
- Copy to clipboard, optional auto-copy when you stop
- Clear notes
- Say **“undo that”** or **“scratch that”** (also “undo”, “delete that”, “undo the last sentence”) to remove the last dictated phrase, including a spoken punctuation mark that followed it
- Punctuation is never guessed: say **“period”**, **“comma”**, **“question mark”**, **“exclamation mark”**, **“colon”**, **“semicolon”**, **“new line”** or **“new paragraph”** to insert it
- Whisper’s non-speech labels (“[BLANK_AUDIO]”, “(background noise)”, “(music)”, …) are dropped instead of pasted
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

## Troubleshooting

If the mic level in the status line stays in single digits while you talk, raise the input level for that microphone in Windows sound settings (or pick a different mic above). Very quiet input makes Whisper both slower and less accurate.
