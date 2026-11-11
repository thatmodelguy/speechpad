using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpeechPad.Services;

namespace SpeechPad;

public partial class MainWindow : Window
{
    private static readonly Regex ScratchCommandRegex = new(
        @"^(?:please\s+)?(?:(?:scratch|undo|delete|ignore|forget)\s+(?:that|it|this|the\s+last\s+(?:phrase|sentence|dictation|line))|undo)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly DictationService _dictation = new();
    private readonly ClipboardService _clipboard = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _copyFeedbackTimer;

    private string _committedText = string.Empty;
    private readonly List<string> _appendedSegments = new();
    private bool _isToggling;
    private bool _shutdown;
    private bool _loadingMics;
    private bool _loadingModel;
    private Ellipse? _micBackground;

    public MainWindow()
    {
        InitializeComponent();

        _copyFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButton.Content = "Copy";
        };

        _dictation.FinalResultReceived += OnFinalResultReceived;
        _dictation.HypothesisReceived += OnHypothesisReceived;
        _dictation.StatusChanged += OnStatusChanged;
        _dictation.ListeningStateChanged += OnListeningStateChanged;
        _dictation.MicrophoneDeviceId = string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId)
            ? null
            : _settings.MicrophoneDeviceId;
        _dictation.ModelKind = ParseModelKind(_settings.Model);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        DictateButton.ApplyTemplate();
        _micBackground = DictateButton.Template.FindName("Bg", DictateButton) as Ellipse;
        AutoCopyCheckBox.IsChecked = _settings.AutoCopyOnStop;
        LoadMicrophones();
        LoadModelChoice();

        try
        {
            if (!WhisperModelService.IsModelReady(_dictation.ModelKind))
            {
                StatusText.Text = "Downloading Whisper model in the background (one-time)…";
            }

            await WhisperModelService.EnsureModelAsync(
                _dictation.ModelKind,
                new Progress<string>(msg => StatusText.Text = msg));

            if (!_dictation.IsListening)
            {
                StatusText.Text = "Ready — click the microphone to dictate";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Model download pending — will retry when you start dictation. " + ex.Message;
        }
    }

    private void LoadMicrophones()
    {
        _loadingMics = true;
        try
        {
            var mics = MicrophoneCatalog.GetMicrophones();
            MicrophoneCombo.ItemsSource = mics;

            var selected = mics.FirstOrDefault(m => m.DeviceId == _settings.MicrophoneDeviceId)
                           ?? mics[0];
            MicrophoneCombo.SelectedItem = selected;
            _dictation.MicrophoneDeviceId = selected.DeviceId;
        }
        finally
        {
            _loadingMics = false;
        }
    }

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMics || MicrophoneCombo.SelectedItem is not MicrophoneInfo mic)
        {
            return;
        }

        _dictation.MicrophoneDeviceId = mic.DeviceId;
        _settings.MicrophoneDeviceId = mic.DeviceId;
        _settings.Save();

        if (_dictation.IsListening)
        {
            StatusText.Text = "Microphone changed — stop and start dictation to use it";
        }
    }

    private void LoadModelChoice()
    {
        _loadingModel = true;
        try
        {
            ModelCombo.ItemsSource = WhisperModelService.Options;
            ModelCombo.SelectedItem = WhisperModelService.Options
                .First(o => o.Kind == _dictation.ModelKind);
        }
        finally
        {
            _loadingModel = false;
        }
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingModel || ModelCombo.SelectedItem is not WhisperModelOption option)
        {
            return;
        }

        _dictation.ModelKind = option.Kind;
        _settings.Model = option.Kind.ToString();
        _settings.Save();

        if (_dictation.IsListening)
        {
            StatusText.Text = "Model changed — stop and start dictation to use it";
        }
        else if (WhisperModelService.IsModelReady(option.Kind))
        {
            StatusText.Text = $"Using the {option.DisplayName} speech model";
        }
        else
        {
            StatusText.Text = $"{option.DisplayName} model downloads when you start dictation (one-time)";
        }
    }

    private static WhisperModelKind ParseModelKind(string? value) =>
        Enum.TryParse<WhisperModelKind>(value, ignoreCase: true, out var kind)
            ? kind
            : WhisperModelService.DefaultKind;

    private async void DictateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isToggling)
        {
            return;
        }

        _isToggling = true;
        DictateButton.IsEnabled = false;
        MicrophoneCombo.IsEnabled = false;
        ModelCombo.IsEnabled = false;

        try
        {
            if (_dictation.IsListening)
            {
                await _dictation.StopAsync();
                _committedText = NotesTextBox.Text ?? string.Empty;
                if (AutoCopyCheckBox.IsChecked == true)
                {
                    TryCopyNotes(auto: true);
                }
            }
            else
            {
                _committedText = NotesTextBox.Text ?? string.Empty;
                await _dictation.StartAsync();
            }
        }
        catch (Exception ex)
        {
            UpdateListeningUi(false);
            StatusText.Text = "Could not start dictation: " + ex.Message;
        }
        finally
        {
            DictateButton.IsEnabled = true;
            MicrophoneCombo.IsEnabled = !_dictation.IsListening;
            ModelCombo.IsEnabled = !_dictation.IsListening;
            _isToggling = false;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        NotesTextBox.Text = string.Empty;
        _committedText = string.Empty;
        _appendedSegments.Clear();
        StatusText.Text = "Notes cleared";
    }

    private void AutoCopyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoCopyOnStop = AutoCopyCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        TryCopyNotes(auto: false);
    }

    private void TryCopyNotes(bool auto)
    {
        if (!_clipboard.CopyText(NotesTextBox.Text ?? string.Empty))
        {
            StatusText.Text = auto
                ? "Stopped — nothing to copy yet"
                : "Nothing to copy yet — dictate some notes first";
            return;
        }

        CopyButton.Content = "Copied";
        StatusText.Text = auto ? "Stopped — copied to clipboard" : "Copied to clipboard";
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void OnFinalResultReceived(object? sender, string text)
    {
        RunOnUi(() => HandleDictatedText(text));
    }

    private void OnHypothesisReceived(object? sender, string text)
    {
        // Whisper emits finals after each phrase; partials unused for now.
    }

    private void OnStatusChanged(object? sender, string status)
    {
        RunOnUi(() => StatusText.Text = status);
    }

    private void OnListeningStateChanged(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            UpdateListeningUi(_dictation.IsListening);
            MicrophoneCombo.IsEnabled = !_dictation.IsListening;
            ModelCombo.IsEnabled = !_dictation.IsListening;
        });
    }

    private void HandleDictatedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Keep committed text aligned with any manual edits in the box.
        _committedText = NotesTextBox.Text ?? string.Empty;

        if (IsScratchCommand(text))
        {
            UndoLastDictation();
            return;
        }

        AppendFinalText(text.Trim());
    }

    private void AppendFinalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.Trim();

        // A spoken punctuation mark with no text to attach it to.
        if (_committedText.Length == 0 && !text.Any(char.IsLetterOrDigit))
        {
            return;
        }

        // Whisper capitalises the first word of every chunk. Keep it lowercase
        // when the chunk continues a sentence that is already in the box.
        var existing = _committedText.TrimEnd();
        if (existing.Length > 0 && existing[^1] is not ('.' or '!' or '?' or ':' or ';' or '\n'))
        {
            text = DictationTextFormatter.LowercaseSentenceStart(text);
        }

        string added;
        if (string.IsNullOrEmpty(_committedText))
        {
            added = text;
            _committedText = text;
        }
        else if (NeedsSpace(_committedText, text))
        {
            added = " " + text;
            _committedText += added;
        }
        else
        {
            added = text;
            _committedText += text;
        }

        _appendedSegments.Add(added);
        NotesTextBox.Text = _committedText;
        NotesTextBox.CaretIndex = NotesTextBox.Text.Length;
    }

    private void UndoLastDictation()
    {
        var removed = false;

        // A spoken punctuation mark ("period") arrives as its own segment but
        // belongs to the phrase before it, so remove that phrase too.
        while (_appendedSegments.Count > 0)
        {
            var segment = _appendedSegments[^1];
            if (!_committedText.EndsWith(segment, StringComparison.Ordinal))
            {
                // Manual edits may have changed the ending; still clear the undo history.
                _appendedSegments.Clear();
                StatusText.Text = "Couldn't match the last phrase to undo";
                return;
            }

            _committedText = _committedText[..^segment.Length];
            _appendedSegments.RemoveAt(_appendedSegments.Count - 1);
            removed = true;

            if (segment.Any(char.IsLetterOrDigit))
            {
                break;
            }
        }

        if (!removed)
        {
            StatusText.Text = "Nothing to scratch";
            return;
        }

        NotesTextBox.Text = _committedText;
        NotesTextBox.CaretIndex = NotesTextBox.Text.Length;
        StatusText.Text = "Scratched last dictation";
    }

    private static bool IsScratchCommand(string text)
    {
        var normalized = text.Trim();
        // Whisper sometimes wraps commands in quotes or adds filler commas.
        normalized = normalized.Trim('"', '\'', '“', '”');
        return ScratchCommandRegex.IsMatch(normalized);
    }

    private void UpdateListeningUi(bool listening)
    {
        MicIcon.Text = listening ? "\uE71A" : "\uE720";
        DictateButton.ToolTip = listening ? "Stop dictation" : "Start dictation";

        _micBackground ??= DictateButton.Template.FindName("Bg", DictateButton) as Ellipse;
        if (_micBackground is not null)
        {
            _micBackground.Fill = listening
                ? (Brush)FindResource("MicListenBrush")
                : (Brush)FindResource("MicIdleBrush");
        }
    }

    private static bool NeedsSpace(string text, string next)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var last = text[^1];
        if (char.IsWhiteSpace(last) || last is '-' or '\'')
        {
            return false;
        }

        // Spoken punctuation attaches to the word before it.
        return next.Length == 0
               || next[0] is not ('.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '\u201D');
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= MainWindow_Closed;
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _dictation.FinalResultReceived -= OnFinalResultReceived;
        _dictation.HypothesisReceived -= OnHypothesisReceived;
        _dictation.StatusChanged -= OnStatusChanged;
        _dictation.ListeningStateChanged -= OnListeningStateChanged;
        await _dictation.DisposeAsync();
    }
}
