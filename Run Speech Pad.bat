@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0standalone\SpeechPad.exe" (
  start "" "%~dp0standalone\SpeechPad.exe"
  exit /b 0
)

if exist "%~dp0publish\SpeechPad.exe" (
  start "" "%~dp0publish\SpeechPad.exe"
  exit /b 0
)

echo No standalone build found. Building one now...
dotnet publish "src\SpeechPad\SpeechPad.csproj" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "standalone"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

start "" "%~dp0standalone\SpeechPad.exe"
