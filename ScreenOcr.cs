using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Tolmach;

internal sealed record OcrWord(string Text, RectangleF Bounds);

internal sealed record OcrLine(string Text, RectangleF Bounds, List<OcrWord> Words)
{
    public float Height => Bounds.Height;
}

// Text from the screen through the OCR built into Windows (the one the
// Snipping Tool uses). Works on games, pictures, videos — anything visible.
//
// One reading is easily thrown off by a stylised font, light text on a dark
// background or a gradient behind it. So the picture is read several ways —
// as it is, as grey with stretched contrast, as black and white — and the
// reading with the most real words wins; the words left over are then fixed
// against the dictionary (SPEEO → SPEED).
internal static class ScreenOcr
{
    // Physical pixels, the way the screen really is.
    public static Bitmap Capture(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    static List<OcrEngine>? _engines;

    // One engine per installed OCR language that matters here.
    static List<OcrEngine> Engines()
    {
        if (_engines != null)
        {
            return _engines;
        }
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        var wanted = new List<string> { "en", Settings.Current.TargetLanguage, Settings.Current.SecondLanguage, "ru" };
        var engines = new List<OcrEngine>();
        foreach (string code in wanted.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var language = available.FirstOrDefault(l => l.LanguageTag.Split('-')[0].Equals(code.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (language != null && engines.All(e => e.RecognizerLanguage.LanguageTag != language.LanguageTag))
            {
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null)
                {
                    engines.Add(engine);
                }
            }
        }
        if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback)
        {
            engines.Add(fallback);
        }
        Log.Write("OCR languages: " + string.Join(", ", engines.Select(e => e.RecognizerLanguage.LanguageTag)));
        _engines = engines;
        return engines;
    }

    public static IReadOnlyList<string> Languages => Engines().Select(e => e.RecognizerLanguage.LanguageTag).ToList();

    // For checking: which reading won, and how long it all took.
    public static string LastReport { get; private set; } = "";

    // Lines with their words, positioned in the bitmap's pixels.
    public static async Task<List<OcrLine>> RecognizeAsync(Bitmap bitmap, bool correct = true)
    {
        var engines = Engines();
        if (engines.Count == 0)
        {
            throw new InvalidOperationException("В Windows не установлено распознавание текста. Параметры → Время и язык → Язык → добавьте английский или русский язык.");
        }
        var clock = Stopwatch.StartNew();

        // Small text reads far better enlarged; a small capture can afford
        // three times, a big one twice.
        int maxSide = Math.Max(bitmap.Width, bitmap.Height);
        int limit = (int)OcrEngine.MaxImageDimension;
        int factor = bitmap.Width * bitmap.Height <= 700_000 && maxSide * 3 <= limit ? 3 : maxSide * 2 <= limit ? 2 : 1;
        using var scaled = factor == 1 ? (Bitmap)bitmap.Clone() : Scale(bitmap, factor);
        byte[] color = Pixels(scaled, out int width, out int height);

        // 1. The picture as it is, by every language: which language it is.
        OcrEngine? bestEngine = null;
        List<OcrLine>? best = null;
        double bestScore = double.MinValue;
        string bestName = "";
        foreach (var engine in engines)
        {
            var lines = await ReadAsync(engine, color, width, height, factor);
            double score = Lexicon.Score(lines.Select(l => l.Text)) + ScriptScore(lines) * 0.25;
            if (score > bestScore)
            {
                (bestScore, best, bestEngine, bestName) = (score, lines, engine, "color/" + engine.RecognizerLanguage.LanguageTag);
            }
        }

        // 2. Cleaned-up versions of the picture by that language, and the
        // picture at another size: each reading gets some lines right.
        var readings = new List<(string name, List<OcrLine> lines)> { (bestName, best!) };
        var gray = Grayscale(color, width, height);
        readings.Add(("contrast", await ReadAsync(bestEngine!, ToBgra(Stretch(gray), width, height), width, height, factor)));
        readings.Add(("binary", await ReadAsync(bestEngine!, ToBgra(Binarize(gray, width, height, Math.Max(15, 21 * factor / 2) | 1), width, height), width, height, factor)));
        if (factor == 3)
        {
            using var smaller = Scale(bitmap, 2);
            byte[] pixels = Pixels(smaller, out int w2, out int h2);
            readings.Add(("color x2", await ReadAsync(bestEngine!, pixels, w2, h2, 2)));
        }

        // 3. Line by line, the best version of each line from any reading.
        var merged = Merge(readings.Select(r => r.lines));
        var result = correct ? merged.Select(CorrectLine).ToList() : merged;
        LastReport = $"readings: {string.Join(", ", readings.Select(r => $"{r.name} {Lexicon.Score(r.lines.Select(l => l.Text)):0}"))}; " +
                     $"merged {Lexicon.Score(merged.Select(l => l.Text)):0}, x{factor}, {clock.ElapsedMilliseconds} ms";
        return result;
    }

    // Every reading's lines, best first; a line is taken unless a better
    // version of it (overlapping it on the picture) already was.
    static List<OcrLine> Merge(IEnumerable<List<OcrLine>> readings)
    {
        var candidates = readings
            .SelectMany(lines => lines)
            .Select(line => (line, score: Lexicon.Score(new[] { line.Text }) + line.Text.Count(char.IsLetterOrDigit) * 0.05))
            .OrderByDescending(c => c.score)
            .ToList();
        var taken = new List<OcrLine>();
        foreach (var (line, _) in candidates)
        {
            if (!taken.Any(t => Overlap(t.Bounds, line.Bounds)))
            {
                taken.Add(line);
            }
        }
        return taken.OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left).ToList();
    }

    // The same line read twice: mostly on the same height and the same span.
    static bool Overlap(RectangleF a, RectangleF b)
    {
        float vertical = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        float horizontal = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return vertical > Math.Min(a.Height, b.Height) * 0.5f && horizontal > Math.Min(a.Width, b.Width) * 0.3f;
    }

    static async Task<List<OcrLine>> ReadAsync(OcrEngine engine, byte[] bgra, int width, int height, int factor)
    {
        using var software = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(software);
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new OcrWord(w.Text, new RectangleF(
                    (float)w.BoundingRect.X / factor, (float)w.BoundingRect.Y / factor,
                    (float)w.BoundingRect.Width / factor, (float)w.BoundingRect.Height / factor)))
                .ToList();
            if (words.Count == 0)
            {
                continue;
            }
            var bounds = RectangleF.FromLTRB(words.Min(w => w.Bounds.Left), words.Min(w => w.Bounds.Top),
                words.Max(w => w.Bounds.Right), words.Max(w => w.Bounds.Bottom));
            lines.Add(new OcrLine(string.Join(' ', words.Select(w => w.Text)), bounds, words));
        }
        return lines;
    }

    static OcrLine CorrectLine(OcrLine line)
    {
        var words = line.Words.Select(w => w with { Text = Lexicon.CorrectLine(w.Text) }).ToList();
        return line with { Words = words, Text = string.Join(' ', words.Select(w => w.Text)) };
    }

    // The engine of the wrong language produces mixed-alphabet garbage.
    static double ScriptScore(List<OcrLine> lines)
    {
        double score = 0;
        foreach (string word in lines.SelectMany(l => l.Words).Select(w => w.Text))
        {
            var letters = word.Where(char.IsLetter).ToList();
            if (letters.Count < 2)
            {
                continue;
            }
            bool latin = letters.All(c => c < 0x250);
            bool cyrillic = letters.All(c => c >= 0x400 && c < 0x530);
            bool other = letters.All(c => c >= 0x530);
            score += latin || cyrillic || other ? letters.Count : -letters.Count;
        }
        return score;
    }

    // ---------------------------------------------------------- pictures

    static Bitmap Scale(Bitmap source, int factor)
    {
        var scaled = new Bitmap(source.Width * factor, source.Height * factor, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        return scaled;
    }

    // BGRA bytes, fully opaque: the screen has no transparency.
    static byte[] Pixels(Bitmap bitmap, out int width, out int height)
    {
        width = bitmap.Width;
        height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * width * 4, width * 4);
            }
            for (int i = 3; i < bytes.Length; i += 4)
            {
                bytes[i] = 255;
            }
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // Brightness, turned so the text is dark on light: OCR is trained on
    // pages, games mostly write light on dark.
    static byte[] Grayscale(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        var histogram = new int[256];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            int value = (bgra[p] * 114 + bgra[p + 1] * 587 + bgra[p + 2] * 299) / 1000;
            gray[i] = (byte)value;
            histogram[value]++;
        }
        // The background is what most of the picture is.
        int median = 0;
        for (int sum = 0; median < 256 && (sum += histogram[median]) < gray.Length / 2; median++)
        {
        }
        if (median < 128)
        {
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(255 - gray[i]);
            }
        }
        return gray;
    }

    // The darkest 1% to black, the lightest 1% to white.
    static byte[] Stretch(byte[] gray)
    {
        var histogram = new int[256];
        foreach (byte value in gray)
        {
            histogram[value]++;
        }
        int low = 0, high = 255;
        for (int sum = 0; low < 255 && (sum += histogram[low]) < gray.Length / 100; low++)
        {
        }
        for (int sum = 0; high > 0 && (sum += histogram[high]) < gray.Length / 100; high--)
        {
        }
        if (high <= low)
        {
            return gray;
        }
        var result = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            result[i] = (byte)Math.Clamp((gray[i] - low) * 255 / (high - low), 0, 255);
        }
        return result;
    }

    // Black and white by the neighbourhood of each pixel (Sauvola): text on
    // gradients, glows and textured panels comes out clean.
    static byte[] Binarize(byte[] gray, int width, int height, int window)
    {
        var sum = new long[(width + 1) * (height + 1)];
        var squares = new long[(width + 1) * (height + 1)];
        int stride = width + 1;
        for (int y = 0; y < height; y++)
        {
            long rowSum = 0, rowSquares = 0;
            for (int x = 0; x < width; x++)
            {
                int v = gray[y * width + x];
                rowSum += v;
                rowSquares += v * v;
                sum[(y + 1) * stride + x + 1] = sum[y * stride + x + 1] + rowSum;
                squares[(y + 1) * stride + x + 1] = squares[y * stride + x + 1] + rowSquares;
            }
        }

        var result = new byte[gray.Length];
        int half = window / 2;
        const double k = 0.2;
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - half), y1 = Math.Min(height, y + half + 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - half), x1 = Math.Min(width, x + half + 1);
                int count = (x1 - x0) * (y1 - y0);
                long s = sum[y1 * stride + x1] - sum[y0 * stride + x1] - sum[y1 * stride + x0] + sum[y0 * stride + x0];
                long q = squares[y1 * stride + x1] - squares[y0 * stride + x1] - squares[y1 * stride + x0] + squares[y0 * stride + x0];
                double mean = (double)s / count;
                double deviation = Math.Sqrt(Math.Max(0, (double)q / count - mean * mean));
                double threshold = mean * (1 + k * (deviation / 128 - 1));
                result[y * width + x] = gray[y * width + x] > threshold ? (byte)255 : (byte)0;
            }
        }
        return result;
    }

    static byte[] ToBgra(byte[] gray, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            bgra[p] = bgra[p + 1] = bgra[p + 2] = gray[i];
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    // ------------------------------------------------------------ cursor

    // The text near the cursor: a band of the screen around it, laid out
    // into blocks, and the block under the cursor.
    public static async Task<TextDocument?> TextUnderCursorAsync()
    {
        Native.GetCursorPos(out var cursor);
        var (scale, bounds, _) = Native.MonitorAt(cursor);
        int width = (int)(1100 * scale);
        int height = (int)(320 * scale);
        var area = new Rectangle(cursor.X - width / 2, cursor.Y - height / 2, width, height);
        area.Intersect(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        if (area.Width < 10 || area.Height < 10)
        {
            return null;
        }
        using var bitmap = Capture(area);
        var lines = await RecognizeAsync(bitmap);
        return Layout.BlockAt(lines, new PointF(cursor.X - area.Left, cursor.Y - area.Top));
    }
}
