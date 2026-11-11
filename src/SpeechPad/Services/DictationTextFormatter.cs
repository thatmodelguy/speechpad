using System.Text;
using System.Text.RegularExpressions;

namespace SpeechPad.Services;

/// <summary>
/// Cleans raw Whisper output before it reaches the notes box.
///
/// Whisper guesses punctuation from pauses, which drops stray periods,
/// commas and exclamation marks into the middle of a sentence. It also labels
/// non-speech sounds ("[BLANK_AUDIO]", "(background noise)"). This formatter
/// removes both, and writes punctuation only where the speaker says it out
/// loud ("period", "comma", "question mark", "exclamation mark", "new line", …).
/// </summary>
public static class DictationTextFormatter
{
    private const char CommandStart = '\uE002';
    private const char CommandEnd = '\uE003';
    private const string AutoBoundary = "\uE000B\uE001";

    /// <summary>Spoken phrase -> symbol inserted. Longer phrases are matched first.</summary>
    private static readonly (string Phrase, string Symbol)[] SpokenPunctuation =
    {
        ("new paragraph", "\n\n"),
        ("new line", "\n"),
        ("exclamation mark", "!"),
        ("exclamation point", "!"),
        ("question mark", "?"),
        ("open parenthesis", "("),
        ("close parenthesis", ")"),
        ("open quote", "\u201C"),
        ("close quote", "\u201D"),
        ("full stop", "."),
        ("semicolon", ";"),
        ("semi colon", ";"),
        ("exclamation", "!"),
        ("period", "."),
        ("comma", ","),
        ("colon", ":"),
    };

    private static readonly string[] CommandSymbols =
        SpokenPunctuation.Select(p => p.Symbol).Distinct().ToArray();

    private static readonly Dictionary<string, string> CommandLookup =
        SpokenPunctuation.ToDictionary(p => p.Phrase, p => p.Symbol, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Words that are never proper nouns. Only these are lowercased when a
    /// phrase continues a sentence, so names keep their capital letter.
    /// </summary>
    private static readonly HashSet<string> CommonContinuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "actually", "after", "again", "all", "all right", "also", "although", "always",
        "an", "and", "another", "any", "anyway", "as", "at", "basically", "because", "before", "but",
        "by", "can", "could", "did", "do", "does", "during", "even", "for", "from", "he", "her",
        "here", "his", "how", "however", "i'd", "i'll", "i'm", "i've", "if", "in", "into", "it",
        "it's", "its", "just", "let's", "like", "maybe", "more", "most", "my", "never", "next",
        "no", "not", "now", "of", "oh", "ok", "okay", "on", "one", "or", "other", "our", "over",
        "probably", "she", "should", "since", "so", "some", "sometimes", "still", "than", "that",
        "that's", "the", "their", "them", "then", "there", "there's", "these", "they", "they're",
        "this", "those", "though", "through", "to", "under", "unless", "until", "usually", "was",
        "we", "we're", "were", "what", "when", "where", "which", "while", "who", "why", "will",
        "with", "would", "yes", "you", "your",
    };

    private static readonly HashSet<string> NoProperNouns = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex SentenceEndRegex =
        new(@"(?<!\d)[.!?]+", RegexOptions.Compiled);

    private static readonly Regex OtherPunctuationRegex =
        new(@"[,;:…\(\)\[\]\{\}""“”«»—–•·*]", RegexOptions.Compiled);

    private static readonly Regex SpacedHyphenRegex =
        new(@"\s-+\s", RegexOptions.Compiled);

    private static readonly Regex StrayApostropheRegex =
        new(@"(?<![A-Za-z])['’‘]|['’‘](?![A-Za-z])", RegexOptions.Compiled);

    private static readonly Regex AnnotationRegex =
        new(@"[\[\(\{<][^\]\)\}>]{0,80}[\]\)\}>]", RegexOptions.Compiled);

    private static readonly Regex AsteriskAnnotationRegex =
        new(@"\*[^*\n]{0,80}\*", RegexOptions.Compiled);

    private static readonly Regex WhisperTokenRegex =
        new(@"<\|[^|>]*\|>", RegexOptions.Compiled);

    private static readonly Regex NoiseWordsRegex = new(
        @"\b(?:blank[_\s-]?audio|silence|silent|music|musical|noise|noisy|background|ambient|inaudible|unintelligible|indistinct|applause|laugh(?:ing|s|ter)?|cough(?:ing|s)?|sigh(?:ing|s)?|sneeze|static|beep(?:ing|s)?|wind|breath(?:ing)?|rustl(?:e|ing)|chatter|crosstalk|murmur(?:ing)?|sound\s+effect|dog\s+barking|car\s+horn)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A whole phrase that is nothing but a non-speech label.</summary>
    private static readonly Regex NoiseOnlyRegex = new(
        @"^(?:(?:no|some|a\s+lot\s+of)\s+)?(?:(?:audible|ambient|background)\s+)*(?:noise|music|static|silence|wind|chatter|applause|laughter)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpokenCommandRegex = BuildSpokenCommandRegex();

    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = RemoveAnnotations(raw);
        text = WhisperTokenRegex.Replace(text, " ");
        text = NormalizeSpaces(text);

        if (text.Length == 0 || NoiseOnlyRegex.IsMatch(text))
        {
            return string.Empty;
        }

        // Pauses that Whisper punctuated on its own become invisible boundaries:
        // they only tell us the next word probably starts a new sentence.
        text = SentenceEndRegex.Replace(text, $" {AutoBoundary} ");
        text = OtherPunctuationRegex.Replace(text, " ");
        text = SpacedHyphenRegex.Replace(text, " ");
        text = StrayApostropheRegex.Replace(text, " ");

        // Replace spoken punctuation words after stripping, so the symbols survive.
        text = SpokenCommandRegex.Replace(text, ReplaceCommand);
        text = NormalizeSpaces(text);

        if (text.Length == 0)
        {
            return string.Empty;
        }

        return Rebuild(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Lowercases the first word when a new phrase continues a sentence that is
    /// already in the notes box. Proper nouns and names keep their capital.
    /// </summary>
    public static string LowercaseSentenceStart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var end = text.IndexOf(' ');
        var first = end < 0 ? text : text[..end];
        if (!ShouldLowercase(first, NoProperNouns))
        {
            return text;
        }

        return WithFirstLetter(text, upper: false);
    }

    private static string RemoveAnnotations(string text)
    {
        text = AnnotationRegex.Replace(text, m => AnnotationReplacement(m.Value[1..^1]));
        text = AsteriskAnnotationRegex.Replace(text, m => AnnotationReplacement(m.Value[1..^1]));
        return text;
    }

    private static string AnnotationReplacement(string inner) =>
        NoiseWordsRegex.IsMatch(inner) ? " " : $" {inner} ";

    private static Regex BuildSpokenCommandRegex()
    {
        var patterns = SpokenPunctuation
            .Select(p => p.Phrase)
            .OrderByDescending(p => p.Length)
            .Select(p => Regex.Escape(p).Replace("\\ ", @"\s+"));

        return new Regex(
            @"\b(?:" + string.Join("|", patterns) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static string ReplaceCommand(Match match)
    {
        var phrase = NormalizeSpaces(match.Value);
        if (!CommandLookup.TryGetValue(phrase, out var symbol))
        {
            return match.Value;
        }

        var index = Array.IndexOf(CommandSymbols, symbol);
        return index < 0 ? match.Value : $" {CommandStart}{index}{CommandEnd} ";
    }

    private static string Rebuild(string[] tokens)
    {
        var properNouns = CollectProperNouns(tokens);
        var builder = new StringBuilder();
        var capitalizeNext = false;
        var lowercaseNext = false;

        foreach (var token in tokens)
        {
            if (token == AutoBoundary)
            {
                lowercaseNext = true;
                capitalizeNext = false;
                continue;
            }

            if (TryReadCommand(token, out var symbol))
            {
                AppendSymbol(builder, symbol);
                if (symbol is "." or "!" or "?" or "\n" or "\n\n")
                {
                    capitalizeNext = true;
                    lowercaseNext = false;
                }

                continue;
            }

            var word = token;
            if (capitalizeNext)
            {
                word = WithFirstLetter(word, upper: true);
            }
            else if (lowercaseNext && ShouldLowercase(word, properNouns))
            {
                word = WithFirstLetter(word, upper: false);
            }

            capitalizeNext = false;
            lowercaseNext = false;
            AppendWord(builder, word);
        }

        return builder.ToString().Trim();
    }

    private static bool TryReadCommand(string token, out string symbol)
    {
        symbol = string.Empty;
        if (token.Length < 3 || token[0] != CommandStart || token[^1] != CommandEnd)
        {
            return false;
        }

        if (!int.TryParse(token[1..^1], out var index) ||
            index < 0 || index >= CommandSymbols.Length)
        {
            return false;
        }

        symbol = CommandSymbols[index];
        return true;
    }

    private static HashSet<string> CollectProperNouns(string[] tokens)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0 || token == AutoBoundary || TryReadCommand(token, out _))
            {
                continue;
            }

            var previous = tokens[i - 1];
            if (previous == AutoBoundary || TryReadCommand(previous, out _))
            {
                continue;
            }

            if (!char.IsUpper(token[0]) || IsShouting(token))
            {
                continue;
            }

            result.Add(token.Trim('\''));
        }

        return result;
    }

    private static bool ShouldLowercase(string word, HashSet<string> properNouns)
    {
        if (word.Length == 0 || !char.IsUpper(word[0]))
        {
            return false;
        }

        if (properNouns.Contains(word) || IsShouting(word))
        {
            return false;
        }

        if (word.Any(char.IsDigit))
        {
            return false;
        }

        var apostrophe = word.IndexOf('\'');
        var head = apostrophe >= 0 ? word[..apostrophe] : word;
        if (head.Equals("I", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CommonContinuationWords.Contains(word.Trim('\''));
    }

    private static bool IsShouting(string word)
    {
        var letters = 0;
        foreach (var c in word)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (!char.IsUpper(c))
            {
                return false;
            }
        }

        return letters > 1;
    }

    private static string WithFirstLetter(string word, bool upper)
    {
        var index = 0;
        while (index < word.Length && !char.IsLetter(word[index]))
        {
            index++;
        }

        if (index >= word.Length)
        {
            return word;
        }

        var replacement = upper
            ? char.ToUpperInvariant(word[index])
            : char.ToLowerInvariant(word[index]);

        if (replacement == word[index])
        {
            return word;
        }

        return word[..index] + replacement + word[(index + 1)..];
    }

    private static void AppendWord(StringBuilder builder, string word)
    {
        if (builder.Length > 0 && builder[^1] is not (' ' or '\n' or '(' or '[' or '\u201C'))
        {
            builder.Append(' ');
        }

        builder.Append(word);
    }

    private static void AppendSymbol(StringBuilder builder, string symbol)
    {
        switch (symbol)
        {
            case "\n":
            case "\n\n":
                TrimTrailingSpaces(builder);
                builder.Append(symbol);
                return;

            case "(":
            case "[":
            case "\u201C":
                if (builder.Length > 0 && builder[^1] is not (' ' or '\n'))
                {
                    builder.Append(' ');
                }

                builder.Append(symbol);
                return;

            case ")":
            case "]":
            case "\u201D":
                TrimTrailingSpaces(builder);
                builder.Append(symbol);
                return;

            case "\u2014":
            case "-":
                if (builder.Length > 0 && builder[^1] is not (' ' or '\n'))
                {
                    builder.Append(' ');
                }

                builder.Append(symbol);
                builder.Append(' ');
                return;

            default:
                TrimTrailingSpaces(builder);
                if (builder.Length > 0 && builder[^1] == symbol[0])
                {
                    return;
                }

                builder.Append(symbol);
                return;
        }
    }

    private static void TrimTrailingSpaces(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }
    }

    private static string NormalizeSpaces(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}
