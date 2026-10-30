using System.Windows;

namespace SpeechPad.Services;

public sealed class ClipboardService
{
    public bool CopyText(string text)
    {
        // WPF Clipboard.SetText throws on null/empty.
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        Clipboard.SetText(text);
        return true;
    }
}
