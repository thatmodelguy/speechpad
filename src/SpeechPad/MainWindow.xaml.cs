using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpeechPad.Services;

namespace SpeechPad;

public partial class MainWindow : Window
{
    private readonly DictationService _dictation = new();
    private readonly ClipboardService _clipboard = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _copyFeedbackTimer;

    private string _committedText = string.Empty;
    private bool _isToggling;
    private bool _shutdown;
    private bool _loadingMics;
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

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        DictateButton.ApplyTemplate();
        _micBackground = DictateButton.Template.FindName("Bg", DictateButton) as Ellipse;
        AutoCopyCheckBox.IsChecked = _settings.AutoCopyOnStop;
        LoadMicrophones();

        try
        {
            if (!WhisperModelService.IsModelReady())
            {
                StatusText.Text = "Downloading Whisper model in the background (one-time)…";
            }

            await WhisperModelService.EnsureModelAsync(
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

    private async void DictateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isToggling)
        {
            return;
        }

        _isToggling = true;
        DictateButton.IsEnabled = false;
        MicrophoneCombo.IsEnabled = false;

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
            _isToggling = false;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        NotesTextBox.Text = string.Empty;
        _committedText = string.Empty;
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
        RunOnUi(() => AppendFinalText(text));
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
        });
    }

    private void AppendFinalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (string.IsNullOrEmpty(_committedText))
        {
            _committedText = text;
        }
        else if (NeedsSpace(_committedText))
        {
            _committedText += " " + text;
        }
        else
        {
            _committedText += text;
        }

        NotesTextBox.Text = _committedText;
        NotesTextBox.CaretIndex = NotesTextBox.Text.Length;
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

    private static bool NeedsSpace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var last = text[^1];
        return !char.IsWhiteSpace(last) && last is not ('-' or '\'');
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
