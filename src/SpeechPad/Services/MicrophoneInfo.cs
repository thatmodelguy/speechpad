namespace SpeechPad.Services;

/// <param name="DeviceId">WASAPI device id, or null for the Windows default capture device.</param>
public sealed record MicrophoneInfo(string? DeviceId, string Name)
{
    public bool IsDefault => DeviceId is null;

    public override string ToString() => Name;
}
