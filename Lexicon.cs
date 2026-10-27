using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Tolmach;

// The 50 000 most frequent English and Russian words (hermitdave/
// FrequencyWords, from film subtitles, CC BY-SA 4.0). They tell real words
// from OCR misreadings and pick the fix: "SPEEO" isn't a word, "SPEED" is.
internal static class Lexicon
{
    static Dictionary<string, int>? _english;
    static Dictionary<string, int>? _russian;
    static readonly object Gate = new();

    static Dictionary<string, int> English => Load(ref _english, "en");
    static Dictionary<string, int> Russian => Load(ref _russian, "ru");

    // Word → its frequency rank (0 = most frequent).
    static Dictionary<string, int> Load(ref Dictionary<string, int>? field, string language)
    {
        if (field != null)
        {
            return field;
        }
        lock (Gate)
        {
            if (field != null)
            {
                return field;
            }
            var words = new Dictionary<string, int>(60_000, StringComparer.Ordinal);
            using var stream = typeof(Lexicon).Assembly.GetManifestResourceStream($"Tolmach.Dictionaries.{language}.txt.gz");
            if (stream != null)
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                string? line;
                int rank = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    int space = line.IndexOf(' ');
                    string word = space > 0 ? line[..space] : line;
                    if (word.Length > 0 && word.All(char.IsLetter))
                    {
                        words.TryAdd(word, rank++);
                    }
                }
            }
            field = words;
            return words;
        }
    }

    // Loads both lists ahead of the first translation.
    public static void Warmup()
    {
        _ = English.Count + Russian.Count;
    }

    static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';
    static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    static Dictionary<string, int> DictionaryFor(string word) =>
        word.Count(IsCyrillic) > word.Count(IsLatin) ? Russian : English;

    public static bool IsWord(string word)
    {
        string lower = word.ToLowerInvariant().Replace('ё', 'е');
        return DictionaryFor(lower).ContainsKey(lower) ||
               (lower.Contains('е') && Russian.ContainsKey(lower));
    }

    static int Rank(string lowerWord) =>
        DictionaryFor(lowerWord).TryGetValue(lowerWord, out int rank) ? rank : int.MaxValue;

    // How much of the text consists of real words: picks the best of
    // several readings of the same picture.
    public static double Score(IEnumerable<string> lines)
    {
        double score = 0;
        foreach (string token in lines.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            string core = Core(token, out _, out _);
            int letters = core.Count(char.IsLetter);
            if (letters < 2)
            {
                continue;
            }
            if (core.All(char.IsLetter) && IsWord(core))
            {
                score += letters;
            }
            else if (Correct(core) != null)
            {
                score += letters * 0.5;
            }
            else
            {
                score -= letters * 0.3;
            }
        }
        return score;
    }

    // ------------------------------------------------------------ correcting

    // What OCR mixes up: each character with the ones it's taken for.
    static readonly Dictionary<char, string> Confusions = new()
    {
        ['O'] = "DQC0", ['D'] = "O", ['Q'] = "O", ['C'] = "GO", ['G'] = "CO6",
        ['E'] = "F", ['F'] = "EP", ['P'] = "FR", ['R'] = "PB", ['B'] = "8RE",
        ['I'] = "l1LT", ['L'] = "I", ['U'] = "VOH", ['V'] = "UY", ['S'] = "5",
        ['Z'] = "2", ['N'] = "HMA", ['M'] = "N", ['H'] = "NU", ['K'] = "X",
        ['X'] = "K", ['T'] = "IY", ['Y'] = "VT", ['A'] = "4",
        ['o'] = "aec0", ['a'] = "oe", ['e'] = "co", ['c'] = "eo", ['l'] = "iI1t",
        ['i'] = "lj", ['j'] = "i", ['n'] = "hur", ['h'] = "nb", ['b'] = "h6",
        ['u'] = "nv", ['v'] = "uy", ['y'] = "v", ['t'] = "fl", ['f'] = "t", ['r'] = "n",
        ['g'] = "q9", ['q'] = "g", ['m'] = "n", ['s'] = "5",
        ['0'] = "OoD", ['1'] = "Ili", ['5'] = "Ss", ['8'] = "B", ['2'] = "Zz",
        ['6'] = "Gb", ['4'] = "A", ['7'] = "T", ['9'] = "g", ['|'] = "Il", ['!'] = "lI",
        ['$'] = "S", ['@'] = "a",
        ['ш'] = "щ", ['щ'] = "ш", ['и'] = "йн", ['й'] = "и", ['н'] = "и", ['п'] = "л", ['л'] = "п",
        ['б'] = "6", ['З'] = "3", ['з'] = "3", ['О'] = "0С", ['о'] = "0с", ['с'] = "ое", ['е'] = "сё",
        ['ь'] = "ы", ['ы'] = "ь", ['Ш'] = "Щ", ['Щ'] = "Ш", ['И'] = "ЙН", ['Н'] = "И", ['П'] = "Л", ['Л'] = "П",
    };

    // Pairs of letters taken for one and back.
    static readonly (string from, string to)[] Merges =
    {
        ("rn", "m"), ("m", "rn"), ("cl", "d"), ("d", "cl"), ("vv", "w"), ("w", "vv"),
        ("VV", "W"), ("W", "VV"), ("ii", "u"), ("ri", "n"),
    };

    // Latin and Cyrillic letters that look the same.
    const string LatinTwins = "AaBCcEeHKkMOoPpTXxy";
    const string CyrillicTwins = "АаВСсЕеНКкМОоРрТХху";

    // The word OCR most likely meant, or null when the token is fine or
    // nothing better is found. Keeps the case pattern of the original.
    public static string? Correct(string original)
    {
        if (original.Length < 2 || original.All(c => char.IsDigit(c) || char.IsPunctuation(c)))
        {
            return null;
        }
        // A lower-case letter inside a word in capitals is a capital the
        // font drew oddly ("ROnD" in Bauhaus is ROAD).
        string token = original;
        int upper = token.Count(char.IsUpper);
        int lowerCount = token.Count(char.IsLower);
        if (upper > lowerCount && lowerCount > 0)
        {
            token = token.ToUpperInvariant();
        }
        // Letters of the wrong alphabet in a word ("CТОП" with a Latin C).
        string unified = UnifyScript(token);
        if (unified.All(char.IsLetter) && IsWord(unified))
        {
            return unified == original ? null : unified;
        }
        // Short tokens are too easy to "fix" into another word.
        if (token.Count(char.IsLetter) < 3 && token.All(char.IsLetter))
        {
            return null;
        }

        int maxEdits = unified.Length >= 5 ? 2 : 1;
        var seen = new HashSet<string> { unified };
        var frontier = new List<string> { unified };
        for (int edits = 1; edits <= maxEdits; edits++)
        {
            var next = new List<string>();
            string? best = null;
            int bestRank = int.MaxValue;
            foreach (string word in frontier)
            {
                foreach (string candidate in Variants(word))
                {
                    if (!seen.Add(candidate))
                    {
                        continue;
                    }
                    next.Add(candidate);
                    if (!candidate.All(char.IsLetter))
                    {
                        continue;
                    }
                    string lower = candidate.ToLowerInvariant();
                    int rank = Rank(lower);
                    // Two changes only for fairly common words: a rare word
                    // two edits away is more likely a coincidence.
                    if (rank < bestRank && (edits == 1 || rank < 20_000))
                    {
                        best = candidate;
                        bestRank = rank;
                    }
                }
            }
            if (best != null)
            {
                return MatchCase(best, token);
            }
            if (next.Count > 4000)
            {
                break;
            }
            frontier = next;
        }
        return null;
    }

    static IEnumerable<string> Variants(string word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (Confusions.TryGetValue(word[i], out var alternatives))
            {
                foreach (char alternative in alternatives)
                {
                    yield return string.Concat(word.AsSpan(0, i), alternative.ToString(), word.AsSpan(i + 1));
                }
            }
        }
        foreach (var (from, to) in Merges)
        {
            for (int i = word.IndexOf(from, StringComparison.Ordinal); i >= 0; i = word.IndexOf(from, i + 1, StringComparison.Ordinal))
            {
                yield return string.Concat(word.AsSpan(0, i), to, word.AsSpan(i + from.Length));
            }
        }
    }

    static string UnifyScript(string word)
    {
        int cyrillic = word.Count(IsCyrillic);
        int latin = word.Count(IsLatin);
        if (cyrillic == 0 || latin == 0)
        {
            return word;
        }
        var chars = word.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (cyrillic >= latin)
            {
                int at = LatinTwins.IndexOf(chars[i]);
                if (at >= 0)
                {
                    chars[i] = CyrillicTwins[at];
                }
            }
            else
            {
                int at = CyrillicTwins.IndexOf(chars[i]);
                if (at >= 0)
                {
                    chars[i] = LatinTwins[at];
                }
            }
        }
        return new string(chars);
    }

    // SPEED for SPEEO, Speed for Speeo, speed for speeo.
    static string MatchCase(string word, string original)
    {
        var letters = original.Where(char.IsLetter).ToList();
        if (letters.Count > 1 && letters.Count(char.IsUpper) * 2 > letters.Count)
        {
            return word.ToUpper(CultureInfo.InvariantCulture);
        }
        if (letters.Count > 0 && char.IsUpper(letters[0]) && letters.Skip(1).All(c => !char.IsUpper(c)))
        {
            string lower = word.ToLower(CultureInfo.InvariantCulture);
            return char.ToUpper(lower[0], CultureInfo.InvariantCulture) + lower[1..];
        }
        if (letters.All(c => !char.IsUpper(c)))
        {
            return word.ToLower(CultureInfo.InvariantCulture);
        }
        return word;
    }

    // "(SPEEO!" → core "SPEEO", prefix "(", suffix "!".
    public static string Core(string token, out string prefix, out string suffix)
    {
        int start = 0;
        int end = token.Length;
        // Digit-looking punctuation (| ! $ @) stays part of the word: it may
        // be a misread letter.
        static bool Edge(char c) => !char.IsLetterOrDigit(c) && "|!$@".IndexOf(c) < 0;
        while (start < end && Edge(token[start]))
        {
            start++;
        }
        while (end > start && Edge(token[end - 1]))
        {
            end--;
        }
        // A trailing "!" after letters is punctuation, not a misread "l".
        while (end > start + 1 && token[end - 1] == '!' && char.IsLetter(token[end - 2]))
        {
            end--;
        }
        prefix = token[..start];
        suffix = token[end..];
        return token[start..end];
    }

    // Fixes every word of a line read from the screen.
    public static string CorrectLine(string line)
    {
        var tokens = line.Split(' ');
        for (int i = 0; i < tokens.Length; i++)
        {
            string core = Core(tokens[i], out string prefix, out string suffix);
            if (core.Length == 0 || core.All(char.IsDigit))
            {
                continue;
            }
            bool isWord = core.All(char.IsLetter) && IsWord(core);
            if (!isWord && Correct(core) is { } fixedWord)
            {
                tokens[i] = prefix + fixedWord + suffix;
            }
        }
        return string.Join(' ', tokens);
    }
}
