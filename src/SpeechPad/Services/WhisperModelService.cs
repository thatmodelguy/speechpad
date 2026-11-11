using System.IO;
using Whisper.net.Ggml;

namespace SpeechPad.Services;

public enum WhisperModelKind
{
    /// <summary>base.en — about 140 MB, roughly a third of the transcription time.</summary>
    Fast,

    /// <summary>small.en — about 490 MB, better with names and unusual words.</summary>
    Accurate
}

public sealed record WhisperModelOption(WhisperModelKind Kind, string DisplayName);

/// <summary>
/// Ensures the selected English Whisper model is on disk (downloaded once).
/// </summary>
public static class WhisperModelService
{
    public const WhisperModelKind DefaultKind = WhisperModelKind.Accurate;

    public static readonly WhisperModelOption[] Options =
    {
        new(WhisperModelKind.Fast, "Fast (base.en)"),
        new(WhisperModelKind.Accurate, "Accurate (small.en)"),
    };

    public static string ModelDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeechPad",
            "models");

    public static GgmlType TypeFor(WhisperModelKind kind) =>
        kind == WhisperModelKind.Fast ? GgmlType.BaseEn : GgmlType.SmallEn;

    public static string FileNameFor(WhisperModelKind kind) =>
        kind == WhisperModelKind.Fast ? "ggml-base.en.bin" : "ggml-small.en.bin";

    public static string ModelPath(WhisperModelKind kind) =>
        Path.Combine(ModelDirectory, FileNameFor(kind));

    public static bool IsModelReady(WhisperModelKind kind)
    {
        var path = ModelPath(kind);
        return File.Exists(path) && new FileInfo(path).Length > 1_000_000;
    }

    public static async Task EnsureModelAsync(
        WhisperModelKind kind,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelDirectory);

        var modelPath = ModelPath(kind);
        if (IsModelReady(kind))
        {
            progress?.Report("Speech model ready");
            return;
        }

        var label = kind == WhisperModelKind.Fast ? "base.en" : "small.en";
        var size = kind == WhisperModelKind.Fast ? "~140 MB" : "~490 MB";
        progress?.Report($"Downloading Whisper {label} model (one-time, {size})…");

        var tempPath = modelPath + ".download";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await using (var modelStream =
                         await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                             TypeFor(kind),
                             cancellationToken: cancellationToken))
            await using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream, cancellationToken);
            }

            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }

            File.Move(tempPath, modelPath);
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
