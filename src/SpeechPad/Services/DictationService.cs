using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace SpeechPad.Services;

/// <summary>
/// Continuous dictation with local Whisper (small.en).
/// Captures via WASAPI and converts to 16 kHz mono floats without MediaFoundation.
/// </summary>
public sealed class DictationService : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int MinSpeechMs = 400;
    private const int SilenceMsToEndPhrase = 600;
    private const int ForceChunkMs = 4000;
    private const int PrefillMs = 250;
    private const float TargetSpeechRms = 0.08f;
    private const float PeakCeiling = 0.95f;
    private const float MaxGain = 12f;

    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private readonly List<float> _nativeMono = new();

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    private readonly ConcurrentQueue<float[]> _pendingPhrases = new();
    private readonly List<float> _phraseBuffer = new();
    private readonly object _bufferLock = new();

    private bool _inSpeech;
    private int _silenceSamples;
    private int _speechSamples;
    private float _noiseFloor = 0.002f;
    private int _calibrationSamples;
    private bool _calibrated;
    private bool _isStarting;
    private bool _disposed;
    private DateTime _lastLevelStatusUtc = DateTime.MinValue;
    private float _peakSinceStatus;

    public string? MicrophoneDeviceId { get; set; }

    public WhisperModelKind ModelKind { get; set; } = WhisperModelService.DefaultKind;

    public bool IsListening { get; private set; }

    public event EventHandler<string>? FinalResultReceived;
#pragma warning disable CS0067
    public event EventHandler<string>? HypothesisReceived;
#pragma warning restore CS0067
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? ListeningStateChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsListening || _isStarting)
        {
            return;
        }

        _isStarting = true;
        _cts = new CancellationTokenSource();

        try
        {
            StatusChanged?.Invoke(this, "Preparing speech model…");
            await WhisperModelService.EnsureModelAsync(
                ModelKind,
                new Progress<string>(msg => StatusChanged?.Invoke(this, msg)),
                _cts.Token);

            StatusChanged?.Invoke(this, "Loading speech model…");
            _factory = WhisperFactory.FromPath(WhisperModelService.ModelPath(ModelKind));
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                // Whisper keeps scaling with cores until about 16, so don't stop at a third of them.
                .WithThreads(Math.Clamp(Environment.ProcessorCount * 3 / 4, 2, 16))
                .Build();

            ResetVadState();
            _calibrated = false;
            _calibrationSamples = 0;
            _noiseFloor = 0.002f;
            _peakSinceStatus = 0;
            _nativeMono.Clear();

            StartMicrophone();
            _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token), _cts.Token);

            IsListening = true;
            ListeningStateChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Listening… speak, then pause briefly");
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            StatusChanged?.Invoke(this, FriendlyError(ex));
            throw;
        }
        finally
        {
            _isStarting = false;
        }
    }

    public async Task StopAsync()
    {
        if (!IsListening && _capture is null && _processor is null)
        {
            return;
        }

        try
        {
            float[]? trailing = null;
            lock (_bufferLock)
            {
                FlushNativeIntoPhraseBuffer();
                if (_phraseBuffer.Count >= SampleRate * MinSpeechMs / 1000)
                {
                    trailing = _phraseBuffer.ToArray();
                }

                ResetVadState();
            }

            if (trailing is { Length: > 0 })
            {
                _pendingPhrases.Enqueue(trailing);
            }

            StopMicrophone();

            var drainDeadline = DateTime.UtcNow.AddSeconds(45);
            while (!_pendingPhrases.IsEmpty && DateTime.UtcNow < drainDeadline)
            {
                await Task.Delay(50);
            }

            _cts?.Cancel();

            if (_workerTask is not null)
            {
                try
                {
                    await _workerTask.WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch
                {
                    // ignore
                }
            }

            while (_pendingPhrases.TryDequeue(out var leftover) && _processor is not null)
            {
                await TranscribeAsync(leftover, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, FriendlyError(ex));
        }
        finally
        {
            await CleanupAsync();
            IsListening = false;
            ListeningStateChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Ready");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
    }

    private void StartMicrophone()
    {
        WasapiCapture capture;
        try
        {
            if (string.IsNullOrWhiteSpace(MicrophoneDeviceId))
            {
                capture = new WasapiCapture();
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(MicrophoneDeviceId);
                capture = new WasapiCapture(device);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not open the microphone. Pick another mic in the list. " + ex.Message,
                ex);
        }

        _capture = capture;
        _captureFormat = capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    private void StopMicrophone()
    {
        if (_capture is null)
        {
            return;
        }

        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // ignore
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _captureFormat is null)
        {
            return;
        }

        AppendCaptureBytes(e.Buffer, e.BytesRecorded);

        float[]? completedPhrase = null;
        float rmsForStatus = 0;
        lock (_bufferLock)
        {
            var produced = DownsampleNativeTo16k();
            if (produced.Length > 0)
            {
                rmsForStatus = RootMeanSquare(produced);
                _peakSinceStatus = Math.Max(_peakSinceStatus, rmsForStatus);
                UpdateCalibration(produced.Length, rmsForStatus);
                AppendSamples(produced, rmsForStatus, ref completedPhrase);
            }
        }

        MaybeReportLevel();

        if (completedPhrase is not null)
        {
            _pendingPhrases.Enqueue(completedPhrase);
            StatusChanged?.Invoke(this, "Transcribing…");
        }
    }

    private void AppendCaptureBytes(byte[] buffer, int bytesRecorded)
    {
        var format = _captureFormat!;
        var channels = Math.Max(1, format.Channels);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var bytesPerFrame = 4 * channels;
            var frames = bytesRecorded / bytesPerFrame;
            for (var f = 0; f < frames; f++)
            {
                var offset = f * bytesPerFrame;
                var mono = 0f;
                for (var c = 0; c < channels; c++)
                {
                    mono += BitConverter.ToSingle(buffer, offset + (c * 4));
                }

                _nativeMono.Add(mono / channels);
            }

            return;
        }

        if (format.BitsPerSample == 16)
        {
            var bytesPerFrame = 2 * channels;
            var frames = bytesRecorded / bytesPerFrame;
            for (var f = 0; f < frames; f++)
            {
                var offset = f * bytesPerFrame;
                var mono = 0f;
                for (var c = 0; c < channels; c++)
                {
                    var i = offset + (c * 2);
                    short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                    mono += s / 32768f;
                }

                _nativeMono.Add(mono / channels);
            }

            return;
        }

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Pcm)
        {
            var bytesPerFrame = 4 * channels;
            var frames = bytesRecorded / bytesPerFrame;
            for (var f = 0; f < frames; f++)
            {
                var offset = f * bytesPerFrame;
                var mono = 0f;
                for (var c = 0; c < channels; c++)
                {
                    var sample = BitConverter.ToInt32(buffer, offset + (c * 4));
                    mono += sample / 2147483648f;
                }

                _nativeMono.Add(mono / channels);
            }
        }
    }

    private float[] DownsampleNativeTo16k()
    {
        var sourceRate = _captureFormat?.SampleRate ?? SampleRate;
        if (sourceRate <= 0)
        {
            sourceRate = SampleRate;
        }

        if (sourceRate == SampleRate)
        {
            if (_nativeMono.Count == 0)
            {
                return Array.Empty<float>();
            }

            var copy = _nativeMono.ToArray();
            _nativeMono.Clear();
            return copy;
        }

        // Produce as many 16 kHz samples as we can from the buffered native audio.
        var outCount = _nativeMono.Count * SampleRate / sourceRate;
        if (outCount <= 0)
        {
            return Array.Empty<float>();
        }

        var output = new float[outCount];
        for (var i = 0; i < outCount; i++)
        {
            var srcIndex = i * sourceRate / SampleRate;
            if (srcIndex >= _nativeMono.Count)
            {
                srcIndex = _nativeMono.Count - 1;
            }

            output[i] = _nativeMono[srcIndex];
        }

        var consumed = outCount * sourceRate / SampleRate;
        if (consumed > 0 && consumed <= _nativeMono.Count)
        {
            _nativeMono.RemoveRange(0, consumed);
        }
        else
        {
            _nativeMono.Clear();
        }

        return output;
    }

    private void FlushNativeIntoPhraseBuffer()
    {
        var produced = DownsampleNativeTo16k();
        if (produced.Length > 0)
        {
            float[]? ignored = null;
            AppendSamples(produced, RootMeanSquare(produced), ref ignored);
        }
    }

    private void UpdateCalibration(int sampleCount, float rms)
    {
        if (_calibrated)
        {
            if (!_inSpeech && rms < _noiseFloor * 1.5f)
            {
                _noiseFloor = (_noiseFloor * 0.95f) + (rms * 0.05f);
                _noiseFloor = Math.Clamp(_noiseFloor, 0.0002f, 0.02f);
            }

            return;
        }

        _calibrationSamples += sampleCount;
        _noiseFloor = (_noiseFloor * 0.7f) + (Math.Max(rms, 0.0002f) * 0.3f);
        if (_calibrationSamples >= SampleRate)
        {
            _noiseFloor = Math.Clamp(_noiseFloor * 1.4f, 0.0005f, 0.015f);
            _calibrated = true;
        }
    }

    private void AppendSamples(float[] samples, float rms, ref float[]? completedPhrase)
    {
        var speechThreshold = SpeechThreshold;
        var isLoud = rms >= speechThreshold;
        var silenceNeeded = SampleRate * SilenceMsToEndPhrase / 1000;
        var minSpeech = SampleRate * MinSpeechMs / 1000;
        var forceChunk = SampleRate * ForceChunkMs / 1000;
        var prefill = SampleRate * PrefillMs / 1000;

        if (isLoud)
        {
            if (!_inSpeech)
            {
                _inSpeech = true;
                _speechSamples = 0;
                if (_phraseBuffer.Count > prefill)
                {
                    _phraseBuffer.RemoveRange(0, _phraseBuffer.Count - prefill);
                }
            }

            _phraseBuffer.AddRange(samples);
            _speechSamples += samples.Length;
            _silenceSamples = 0;
        }
        else if (_inSpeech)
        {
            _phraseBuffer.AddRange(samples);
            _silenceSamples += samples.Length;

            if (_silenceSamples >= silenceNeeded && _speechSamples >= minSpeech)
            {
                completedPhrase = _phraseBuffer.ToArray();
                ResetVadState();
            }
        }
        else
        {
            _phraseBuffer.AddRange(samples);
            if (_phraseBuffer.Count > prefill)
            {
                _phraseBuffer.RemoveRange(0, _phraseBuffer.Count - prefill);
            }
        }

        if (_inSpeech && _speechSamples >= forceChunk)
        {
            completedPhrase = _phraseBuffer.ToArray();
            ResetVadState();
        }
    }

    private void MaybeReportLevel()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLevelStatusUtc).TotalMilliseconds < 700)
        {
            return;
        }

        var peak = _peakSinceStatus;
        _peakSinceStatus = 0;
        _lastLevelStatusUtc = now;

        if (!_pendingPhrases.IsEmpty)
        {
            return;
        }

        var pct = LevelPercent(peak);
        var hearing = peak >= SpeechThreshold;
        StatusChanged?.Invoke(
            this,
            hearing
                ? $"Listening… hearing voice (level {pct}%)"
                : $"Listening… mic level {pct}% — raise this mic's input level in Windows if it stays low");
    }

    private void ResetVadState()
    {
        _phraseBuffer.Clear();
        _inSpeech = false;
        _silenceSamples = 0;
        _speechSamples = 0;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_pendingPhrases.TryDequeue(out var phrase))
            {
                try
                {
                    await Task.Delay(40, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                StatusChanged?.Invoke(this, "Transcribing…");
                await TranscribeAsync(phrase, cancellationToken);
                if (IsListening && !cancellationToken.IsCancellationRequested && _pendingPhrases.IsEmpty)
                {
                    StatusChanged?.Invoke(this, "Listening… speak, then pause briefly");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, "Transcription error: " + ex.Message);
            }
        }
    }

    private async Task TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (_processor is null || samples.Length < SampleRate * MinSpeechMs / 1000)
        {
            return;
        }

        var parts = new List<string>();
        await foreach (var segment in _processor.ProcessAsync(NormalizeLevel(samples), cancellationToken))
        {
            var text = CleanWhisperText(segment.Text);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        if (parts.Count == 0)
        {
            StatusChanged?.Invoke(this, "Listening… (caught audio, but no words — try speaking clearer)");
            return;
        }

        // Drop Whisper's guessed punctuation and non-speech labels, and turn
        // spoken punctuation words ("period", "comma", …) into symbols.
        var formatted = DictationTextFormatter.Format(string.Join(" ", parts));
        if (formatted.Length == 0)
        {
            // The chunk was only background noise or a non-speech label.
            return;
        }

        FinalResultReceived?.Invoke(this, formatted);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke(this, "Microphone error: " + e.Exception.Message);
        }
    }

    private async Task CleanupAsync()
    {
        if (_capture is not null)
        {
            var capture = _capture;
            _capture = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _captureFormat = null;
        _nativeMono.Clear();

        if (_cts is not null)
        {
            _cts.Dispose();
            _cts = null;
        }

        _workerTask = null;

        if (_processor is not null)
        {
            await _processor.DisposeAsync();
            _processor = null;
        }

        _factory?.Dispose();
        _factory = null;

        while (_pendingPhrases.TryDequeue(out _))
        {
        }

        lock (_bufferLock)
        {
            ResetVadState();
        }
    }

    /// <summary>RMS level that counts as speech, above the calibrated room noise.</summary>
    private float SpeechThreshold => Math.Max(_noiseFloor * 2.2f, 0.0015f);

    /// <summary>
    /// Maps RMS to a 0-100 meter on a decibel scale, so quiet speech still reads
    /// as a usable level instead of a couple of percent.
    /// </summary>
    private static int LevelPercent(float rms)
    {
        if (rms <= 0f)
        {
            return 0;
        }

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((int)Math.Round((db + 60d) / 60d * 100d), 0, 100);
    }

    private static float Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }

    /// <summary>
    /// Quiet microphones leave Whisper with very little signal, which makes it
    /// slower and less accurate. Lift each phrase to a comfortable level without
    /// ever attenuating it or clipping the peaks.
    /// </summary>
    private static float[] NormalizeLevel(float[] samples)
    {
        var rms = RootMeanSquare(samples);
        if (rms <= 0.00001f)
        {
            return samples;
        }

        var peak = Peak(samples);
        var rmsGain = TargetSpeechRms / rms;
        var peakGain = peak > 0.00001f ? PeakCeiling / peak : MaxGain;
        var gain = Math.Clamp(Math.Min(rmsGain, peakGain), 1f, MaxGain);
        if (gain <= 1.02f)
        {
            return samples;
        }

        var boosted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            boosted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }

        return boosted;
    }

    private static float RootMeanSquare(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static string CleanWhisperText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        if (text is "[BLANK_AUDIO]" or "[Silence]" or "(silence)" or "." or "..."
            or "Thank you." or "Thanks for watching!")
        {
            return string.Empty;
        }

        return text;
    }

    private static string FriendlyError(Exception ex) =>
        ex switch
        {
            UnauthorizedAccessException =>
                "Microphone access denied. Allow mic access for desktop apps in Windows Settings.",
            InvalidOperationException => ex.Message,
            HttpRequestException =>
                "Could not download the speech model. Check your internet connection and try again.",
            IOException => "Could not save the speech model. Check disk space and try again.",
            _ => $"Dictation error: {ex.Message}"
        };
}
