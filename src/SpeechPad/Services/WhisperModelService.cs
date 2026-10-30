using System.IO;
using Whisper.net.Ggml;

namespace SpeechPad.Services;

/// <summary>
/// Ensures the English small Whisper model is on disk (downloaded once).
/// Small.en is a good accuracy/speed balance for live dictation on CPU.
/// </summary>
public static class WhisperModelService
{
    public const GgmlType ModelType = GgmlType.SmallEn;
    public const string ModelFileName = "ggml-small.en.bin";

    public static string ModelDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeechPad",
            "models");

    public static string ModelPath => Path.Combine(ModelDirectory, ModelFileName);

    public static bool IsModelReady() =>
        File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 1_000_000;

    public static async Task EnsureModelAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelDirectory);

        if (IsModelReady())
        {
            progress?.Report("Speech model ready");
            return;
        }

        progress?.Report("Downloading Whisper small.en model (one-time, ~500 MB)…");

        var tempPath = ModelPath + ".download";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await using (var modelStream =
                         await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                             ModelType,
                             cancellationToken: cancellationToken))
            await using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream, cancellationToken);
            }

            if (File.Exists(ModelPath))
            {
                File.Delete(ModelPath);
            }

            File.Move(tempPath, ModelPath);
            progress?.Report("Speech model downloaded");
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // ignore cleanup
            }

            throw;
        }
    }
}
