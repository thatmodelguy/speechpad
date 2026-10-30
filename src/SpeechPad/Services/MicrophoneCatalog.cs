using NAudio.CoreAudioApi;

namespace SpeechPad.Services;

public static class MicrophoneCatalog
{
    public static IReadOnlyList<MicrophoneInfo> GetMicrophones()
    {
        var list = new List<MicrophoneInfo>
        {
            new(null, "Default microphone")
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in devices)
            {
                var name = string.IsNullOrWhiteSpace(device.FriendlyName)
                    ? "Microphone"
                    : device.FriendlyName.Trim();
                list.Add(new MicrophoneInfo(device.ID, name));
            }
        }
        catch
        {
            // Fall back to default-only if WASAPI enumeration fails.
        }

        return list;
    }
}
