using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tolmach;

internal sealed record Translation(string Text, string SourceLanguage, string TargetLanguage, string Service);

// A service that can't help right now (blocked, out of quota, no key):
// the next one in line is tried.
internal sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message)
    {
    }
}

// Several services in a row, so one being blocked or out of quota doesn't
// stop the translation: DeepL (with the user's key) → Google → MyMemory.
internal static class Translator
{
    // No redirects: Google answers a blocked address with a redirect to
    // its captcha page, which must count as "unavailable", not as a reply.
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // A blocked service is skipped for a while instead of being asked (and
    // waited for) on every translation.
    static readonly Dictionary<string, DateTime> PausedUntil = new();
    static readonly TimeSpan Pause = TimeSpan.FromMinutes(30);

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "русский", ["en"] = "английский", ["uk"] = "украинский", ["be"] = "белорусский",
        ["de"] = "немецкий", ["fr"] = "французский", ["es"] = "испанский", ["it"] = "итальянский",
        ["pt"] = "португальский", ["pl"] = "польский", ["cs"] = "чешский", ["tr"] = "турецкий",
        ["ja"] = "японский", ["ko"] = "корейский", ["zh-CN"] = "китайский", ["zh"] = "китайский",
        ["ar"] = "арабский", ["kk"] = "казахский", ["nl"] = "нидерландский", ["sv"] = "шведский",
        ["fi"] = "финский", ["hu"] = "венгерский", ["ro"] = "румынский", ["el"] = "греческий",
    };

    public static string NameOf(string code) =>
        Names.TryGetValue(code, out var name) ? name : code;

    // Short code for the popup: "EN", "RU", "ZH".
    public static string ShortOf(string code) => code.Split('-')[0].ToUpperInvariant();

    // Into the target language, or into the second one when the text is
    // already in the target language. "to" overrides both.
    public static async Task<Translation> TranslateAsync(string text, string? to, CancellationToken ct)
    {
        var settings = Settings.Current;
        string target = to ?? settings.TargetLanguage;
        var result = await TranslateOnceAsync(text, target, ct);
        if (to == null && SameLanguage(result.SourceLanguage, target))
        {
            result = await TranslateOnceAsync(text, settings.SecondLanguage, ct);
        }
        return result;
    }

    static bool SameLanguage(string a, string b) =>
        string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    static async Task<Translation> TranslateOnceAsync(string text, string target, CancellationToken ct)
    {
        var settings = Settings.Current;
        var services = new List<(string name, Func<Task<Translation>> run)>();
        if (settings.DeepLKey.Trim().Length > 0)
        {
            services.Add(("DeepL", () => DeepLAsync(text, target, ct)));
        }
        services.Add(("Google", () => GoogleAsync(text, target, ct)));
        services.Add(("MyMemory", () => MyMemoryAsync(text, target, ct)));
        // The chosen service goes first.
        services = services.OrderBy(s => s.name.Equals(settings.Provider, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

        var problems = new List<string>();
        foreach (var (name, run) in services)
        {
            lock (PausedUntil)
            {
                if (PausedUntil.TryGetValue(name, out var until) && until > DateTime.UtcNow)
                {
                    continue;
                }
            }
            try
            {
                return await run();
            }
            catch (ServiceUnavailableException ex)
            {
                Log.Write($"{name} unavailable: {ex.Message}");
                problems.Add($"{name}: {ex.Message}");
                lock (PausedUntil)
                {
                    PausedUntil[name] = DateTime.UtcNow + Pause;
                }
            }
            // No answer (the address doesn't resolve, the connection hangs):
            // the next service, and this one again in a couple of minutes.
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Write($"{name} not answering: {ex.Message}");
                problems.Add($"{name}: нет ответа");
                lock (PausedUntil)
                {
                    PausedUntil[name] = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                }
            }
        }
        if (problems.Count == 0)
        {
            // Everything was paused: ask the last one anyway.
            lock (PausedUntil)
            {
                PausedUntil.Clear();
            }
            return await services[^1].run();
        }
        throw new InvalidOperationException("Сервисы перевода сейчас недоступны.\n" + string.Join("\n", problems));
    }

    // ---------------------------------------------------------------- Google

    // The free endpoint Google's own widgets use; no key needed. Google
    // blocks it for some addresses (VPNs, shared IPs).
    static async Task<Translation> GoogleAsync(string text, string target, CancellationToken ct)
    {
        const string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=auto&tl=";
        using var request = new HttpRequestMessage(HttpMethod.Post, url + Uri.EscapeDataString(target))
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) }),
        };
        using var response = await Http.SendAsync(request, ct);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400 or 429 or 403)
        {
            throw new ServiceUnavailableException("Google просит капчу для этого адреса (часто из-за VPN)");
        }
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // [[["перевод","source",...],...], null, "en", ...]
        var root = json.RootElement;
        var sb = new StringBuilder();
        if (root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var part in root[0].EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(part[0].GetString());
                }
            }
        }
        string source = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString()! : "auto";
        return new Translation(sb.ToString().Trim(), source, target, "Google");
    }

    // -------------------------------------------------------------- MyMemory

    // Free, no key, detects the language itself. 500 characters per request
    // (longer text goes in pieces) and a daily allowance per address.
    static async Task<Translation> MyMemoryAsync(string text, string target, CancellationToken ct)
    {
        var pieces = SplitForLimit(text, 480);
        var translated = new StringBuilder();
        string source = "auto";
        foreach (var (piece, separator) in pieces)
        {
            string url = "https://api.mymemory.translated.net/get?langpair=autodetect%7C" + Uri.EscapeDataString(target) +
                         "&q=" + Uri.EscapeDataString(piece);
            using var response = await Http.GetAsync(url, ct);
            if ((int)response.StatusCode == 429)
            {
                throw new ServiceUnavailableException("дневной лимит исчерпан");
            }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            string details = root.TryGetProperty("responseDetails", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
            {
                throw new ServiceUnavailableException("дневной лимит исчерпан");
            }
            var data = root.GetProperty("responseData");
            if (source == "auto" && data.TryGetProperty("detectedLanguage", out var detected) && detected.ValueKind == JsonValueKind.String)
            {
                source = detected.GetString()!;
            }
            // The text is already in the target language.
            if (details.Contains("DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase))
            {
                return new Translation(text, target, target, "MyMemory");
            }
            int status = root.TryGetProperty("responseStatus", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 200;
            if (status != 200)
            {
                throw new ServiceUnavailableException(details.Length > 0 ? details : $"ошибка {status}");
            }
            translated.Append(System.Net.WebUtility.HtmlDecode(data.GetProperty("translatedText").GetString() ?? ""));
            translated.Append(separator);
        }
        return new Translation(translated.ToString().Trim(), source, target, "MyMemory");
    }

    // Pieces no longer than the limit, cut after sentences (or words, for
    // a giant sentence), each with what separated it from the next.
    static List<(string piece, string separator)> SplitForLimit(string text, int limit)
    {
        var result = new List<(string, string)>();
        foreach (string paragraph in text.Split('\n'))
        {
            var current = new StringBuilder();
            foreach (string sentence in Regex.Split(paragraph, @"(?<=[.!?…])\s+"))
            {
                foreach (string part in sentence.Length <= limit ? new[] { sentence } : WrapWords(sentence, limit))
                {
                    if (current.Length > 0 && current.Length + 1 + part.Length > limit)
                    {
                        result.Add((current.ToString(), " "));
                        current.Clear();
                    }
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }
                    current.Append(part);
                }
            }
            if (current.Length > 0)
            {
                result.Add((current.ToString(), " "));
            }
            if (result.Count > 0)
            {
                result[^1] = (result[^1].Item1, "\n");
            }
        }
        return result.Where(p => p.Item1.Trim().Length > 0).ToList();
    }

    static IEnumerable<string> WrapWords(string sentence, int limit)
    {
        var current = new StringBuilder();
        foreach (string word in sentence.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > limit)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(word.Length > limit ? word[..limit] : word);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    // ----------------------------------------------------------------- DeepL

    static async Task<Translation> DeepLAsync(string text, string target, CancellationToken ct)
    {
        string key = Settings.Current.DeepLKey.Trim();
        // Free keys end in ":fx" and use their own host.
        string host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
        string deeplTarget = target.ToUpperInvariant() switch
        {
            "EN" => "EN-US",
            "PT" => "PT-BR",
            "ZH-CN" or "ZH" => "ZH-HANS",
            var other => other,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text = new[] { text }, target_lang = deeplTarget }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        using var response = await Http.SendAsync(request, ct);
        switch ((int)response.StatusCode)
        {
            case 401 or 403:
                throw new ServiceUnavailableException("ключ не принят, проверьте DeepLKey в настройках");
            case 456:
                throw new ServiceUnavailableException("месячный лимит исчерпан");
            case 429:
                throw new ServiceUnavailableException("слишком много запросов");
        }
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var first = json.RootElement.GetProperty("translations")[0];
        string source = first.TryGetProperty("detected_source_language", out var detected) ? detected.GetString()!.ToLowerInvariant() : "auto";
        return new Translation(first.GetProperty("text").GetString() ?? "", source, target, "DeepL");
    }

    // ------------------------------------------------------------ documents

    // Item by item, keeping the layout: markers, line structure and the
    // case style of each item. All items go in one request, one per line;
    // services keep line breaks, and if one doesn't, the items go one by one.
    public static async Task<(TextDocument document, Translation result)> TranslateDocumentAsync(
        TextDocument document, string? to, CancellationToken ct)
    {
        var items = document.Items.ToList();
        var texts = items.Select(i => i.Text.Replace('\n', ' ').Trim()).ToList();
        var result = await TranslateAsync(string.Join("\n", texts), to, ct);
        string target = result.TargetLanguage;

        var parts = result.Text.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count != texts.Count)
        {
            Log.Write($"Line count changed in translation ({texts.Count} → {parts.Count}), translating items one by one");
            parts = new List<string>();
            foreach (string text in texts)
            {
                parts.Add(text.Length == 0 ? "" : (await TranslateOnceAsync(text, target, ct)).Text.Replace('\n', ' ').Trim());
            }
        }

        for (int i = 0; i < parts.Count; i++)
        {
            string part = Layout.ApplyCase(parts[i], items[i].Case, target);
            // Services like to end every line with a full stop; a menu entry
            // or a list item without one stays without one.
            string source = items[i].Text.TrimEnd();
            if (part.EndsWith('.') && !part.EndsWith("..") && source.Length > 0 && !".!?…".Contains(source[^1]))
            {
                part = part[..^1];
            }
            // "Exit" → "выход": an item that started with a capital keeps it.
            if (items[i].Case == CaseStyle.Normal && items[i].Text.Length > 0 && char.IsUpper(items[i].Text[0]) &&
                part.Length > 0 && char.IsLower(part[0]))
            {
                part = char.ToUpper(part[0], System.Globalization.CultureInfo.InvariantCulture) + part[1..];
            }
            parts[i] = part;
        }

        var translated = document.CloneWithTexts(parts);
        return (translated, result with { Text = translated.ToPlainText() });
    }
}
