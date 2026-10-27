using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tolmach;

internal enum ItemKind
{
    // A line on its own: a menu entry, a label, a button.
    Line,
    // Several lines of one sentence or paragraph, wrapped.
    Paragraph,
    // "• text", "1. text", "- text".
    ListItem,
    // A title over the rest of its block.
    Heading,
}

internal enum CaseStyle
{
    Normal,
    // MAIN MENU
    Upper,
    // Main Menu
    Title,
}

// One unit of meaning: translated as a whole, shown on its own line.
internal sealed class TextItem
{
    public ItemKind Kind { get; set; }
    public string Marker { get; set; } = "";
    public string Text { get; set; } = "";
    public CaseStyle Case { get; set; }
    // A table cell to the right of the previous item, on the same row.
    public bool SameRow { get; set; }

    public override string ToString() => Marker.Length > 0 ? Marker + " " + Text : Text;
}

// Items that belong together: a paragraph, a list, a menu, a dialogue box.
internal sealed class DocBlock
{
    public List<TextItem> Items { get; } = new();
}

// Text the way it was laid out: blocks of items. Translated item by item
// and shown with the same structure.
internal sealed class TextDocument
{
    public List<DocBlock> Blocks { get; } = new();

    public IEnumerable<TextItem> Items => Blocks.SelectMany(b => b.Items);

    public bool IsEmpty => !Items.Any(i => i.Text.Trim().Length > 0);

    // Items one per line (table cells of a row apart by a tab), blocks apart
    // by an empty line.
    public string ToPlainText() =>
        string.Join("\n\n", Blocks.Select(b =>
            string.Concat(b.Items.Select((item, n) => (n == 0 ? "" : item.SameRow ? "\t" : "\n") + item))));

    public TextDocument CloneWithTexts(IReadOnlyList<string> texts)
    {
        var copy = new TextDocument();
        int index = 0;
        foreach (var block in Blocks)
        {
            var newBlock = new DocBlock();
            foreach (var item in block.Items)
            {
                newBlock.Items.Add(new TextItem { Kind = item.Kind, Marker = item.Marker, Case = item.Case, SameRow = item.SameRow, Text = texts[index++] });
            }
            copy.Blocks.Add(newBlock);
        }
        return copy;
    }
}

// Works out the structure of text read from the screen (from the positions
// of its lines) or copied (from its line breaks): which lines are wrapped
// parts of one sentence and which stand on their own.
internal static class Layout
{
    // "•", "–", "*", "1.", "2)", "a)" at the start of a line.
    static readonly Regex MarkerPattern = new(@"^\s*(?<m>[•·▪●○◦■□►▶➤✓✔\-–—*]|\d{1,2}[.)]|[A-Za-z][.)])\s+(?<t>.+)$", RegexOptions.Compiled);
    const string Terminal = ".!?:;…»\")";

    // ----------------------------------------------------------- screen

    sealed class Segment
    {
        public required string Text;
        public required RectangleF Bounds;
        // A table row: short pieces with wide gaps between them.
        public List<string>? Cells;
        public float Height => Bounds.Height;
    }

    public static TextDocument FromLines(List<OcrLine> lines) => Build(Blocks(lines));

    // The block under (or right next to) the point.
    public static TextDocument? BlockAt(List<OcrLine> lines, PointF point)
    {
        var blocks = Blocks(lines);
        var hit = blocks
            .Select(b => (block: b, distance: b.Min(s => Distance(s.Bounds, point)), height: b.Average(s => s.Height)))
            .Where(x => x.distance <= Math.Max(12, x.height * 0.8))
            .OrderBy(x => x.distance)
            .Select(x => x.block)
            .FirstOrDefault();
        return hit == null ? null : Build(new List<List<Segment>> { hit });
    }

    static List<List<Segment>> Blocks(List<OcrLine> lines)
    {
        var segments = MergeRows(lines.SelectMany(Split).ToList()).OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToList();
        var blocks = new List<List<Segment>>();
        foreach (var segment in segments)
        {
            List<Segment>? target = null;
            float bestGap = float.MaxValue;
            foreach (var block in blocks)
            {
                var last = block[^1];
                float height = Math.Max(last.Height, segment.Height);
                float gap = segment.Bounds.Top - last.Bounds.Bottom;
                bool similarSize = Math.Min(last.Height, segment.Height) >= height * 0.7f;
                // Below the previous line, not far, lined up with it: left
                // edges together, centres together (centred menus) or
                // overlapping (wrapped paragraph).
                bool lined = Math.Abs(segment.Bounds.Left - last.Bounds.Left) < height * 1.5f ||
                             Math.Abs(Center(segment.Bounds) - Center(last.Bounds)) < height * 1.5f ||
                             (segment.Bounds.Left < last.Bounds.Right && last.Bounds.Left < segment.Bounds.Right &&
                              Math.Abs(segment.Bounds.Left - last.Bounds.Left) < height * 4);
                // Table rows sit further apart than lines of a paragraph.
                float allowed = height * (segment.Cells != null && last.Cells != null ? 2.4f : 1.6f);
                if (gap > -height * 0.3f && gap < allowed && similarSize && lined && gap < bestGap)
                {
                    bestGap = gap;
                    target = block;
                }
            }
            if (target != null)
            {
                target.Add(segment);
            }
            else
            {
                blocks.Add(new List<Segment> { segment });
            }
        }

        // Reading order: top to bottom; blocks side by side (tops level
        // within half a line), left to right.
        var ordered = blocks.OrderBy(b => b[0].Bounds.Top).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            for (int j = i; j > 0; j--)
            {
                var (above, below) = (ordered[j - 1][0], ordered[j][0]);
                bool level = Math.Abs(above.Bounds.Top - below.Bounds.Top) < Math.Max(above.Height, below.Height) * 0.5f;
                if (!level || above.Bounds.Left <= below.Bounds.Left)
                {
                    break;
                }
                (ordered[j - 1], ordered[j]) = (ordered[j], ordered[j - 1]);
            }
        }
        return ordered;
    }

    // A wide gap between words is a column or a table cell, not a space.
    // Short pieces ("Top speed ... 120 km/h") are cells of one table row;
    // long ones are side-by-side columns of text, read separately.
    static IEnumerable<Segment> Split(OcrLine line)
    {
        var groups = new List<List<OcrWord>> { new() };
        foreach (var word in line.Words)
        {
            var current = groups[^1];
            if (current.Count > 0 && word.Bounds.Left - current[^1].Bounds.Right > Math.Max(line.Height, word.Bounds.Height) * 2.2f)
            {
                groups.Add(current = new List<OcrWord>());
            }
            current.Add(word);
        }
        groups.RemoveAll(g => g.Count == 0);
        if (groups.Count > 1 && groups.All(g => g.Count <= 5))
        {
            var row = ToSegment(groups.SelectMany(g => g).ToList());
            row.Cells = groups.Select(g => string.Join(' ', g.Select(w => w.Text))).ToList();
            yield return row;
            yield break;
        }
        foreach (var group in groups)
        {
            yield return ToSegment(group);
        }
    }

    // Short pieces level with each other are one table row, even when OCR
    // gave them as separate lines ("Top speed" ... "120 km/h").
    static List<Segment> MergeRows(List<Segment> segments)
    {
        static int Words(Segment s) => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var result = new List<Segment>();
        var used = new HashSet<Segment>();
        foreach (var segment in segments.OrderBy(s => s.Bounds.Top))
        {
            if (!used.Add(segment))
            {
                continue;
            }
            var row = new List<Segment> { segment };
            if (segment.Cells == null && Words(segment) <= 5)
            {
                foreach (var other in segments)
                {
                    if (used.Contains(other) || other.Cells != null || Words(other) > 5)
                    {
                        continue;
                    }
                    float height = Math.Max(segment.Height, other.Height);
                    bool level = Math.Abs((segment.Bounds.Top + segment.Bounds.Bottom) / 2 - (other.Bounds.Top + other.Bounds.Bottom) / 2) < height * 0.35f;
                    bool similar = Math.Min(segment.Height, other.Height) >= height * 0.7f;
                    if (level && similar)
                    {
                        row.Add(other);
                        used.Add(other);
                    }
                }
            }
            if (row.Count == 1)
            {
                result.Add(segment);
                continue;
            }
            row.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
            result.Add(new Segment
            {
                Text = string.Join("  ", row.Select(s => s.Text)),
                Bounds = RectangleF.FromLTRB(row.Min(s => s.Bounds.Left), row.Min(s => s.Bounds.Top), row.Max(s => s.Bounds.Right), row.Max(s => s.Bounds.Bottom)),
                Cells = row.Select(s => s.Text).ToList(),
            });
        }
        return result;
    }

    static Segment ToSegment(List<OcrWord> words) => new()
    {
        Text = string.Join(' ', words.Select(w => w.Text)),
        Bounds = RectangleF.FromLTRB(words.Min(w => w.Bounds.Left), words.Min(w => w.Bounds.Top),
            words.Max(w => w.Bounds.Right), words.Max(w => w.Bounds.Bottom)),
    };

    static float Center(RectangleF r) => r.Left + r.Width / 2;

    static double Distance(RectangleF r, PointF p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static TextDocument Build(List<List<Segment>> blocks)
    {
        var document = new TextDocument();
        var heights = new List<float>();
        foreach (var segments in blocks)
        {
            float maxWidth = segments.Max(s => s.Bounds.Width);
            float medianHeight = segments.Select(s => s.Height).OrderBy(h => h).ElementAt(segments.Count / 2);
            var lines = segments.Select(s => new LineInfo(s.Text, s.Bounds.Width / maxWidth, s.Height / medianHeight) { Cells = s.Cells }).ToList();
            document.Blocks.Add(BuildBlock(lines));
            heights.Add(medianHeight);
        }

        // A single line in bigger letters over the next block is its title
        // ("MAIN MENU" over the menu, a quest name over its text).
        for (int i = 0; i + 1 < document.Blocks.Count; i++)
        {
            var items = document.Blocks[i].Items;
            if (items.Count == 1 && items[0].Kind == ItemKind.Line && !items[0].SameRow &&
                (heights[i] >= heights[i + 1] * 1.2f || (items[0].Case == CaseStyle.Upper && heights[i] >= heights[i + 1] * 0.85f)))
            {
                items[0].Kind = ItemKind.Heading;
            }
        }
        return document;
    }

    // ------------------------------------------------------------- text

    // Copied text: lines and empty lines are all there is to go by.
    public static TextDocument FromText(string text)
    {
        var document = new TextDocument();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string chunk in Regex.Split(text, @"\n\s*\n"))
        {
            var raw = chunk.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (raw.Count == 0)
            {
                continue;
            }
            var lines = raw.Select(l =>
            {
                // Tabs are table cells (copied from Excel, a web table).
                var cells = l.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                string clean = Regex.Replace(l.Trim(), @"[ \t]{2,}", " ");
                return (clean, cells);
            }).ToList();
            int maxLength = lines.Max(l => l.clean.Length);
            document.Blocks.Add(BuildBlock(lines
                .Select(l => new LineInfo(l.clean, (float)l.clean.Length / maxLength, 1) { Cells = l.cells.Count > 1 ? l.cells : null })
                .ToList()));
        }
        return document;
    }

    // ----------------------------------------------------------- shared

    // relativeWidth: the line's width against the widest line of the block;
    // relativeHeight: its text size against the usual one.
    sealed record LineInfo(string Text, float RelativeWidth, float RelativeHeight)
    {
        public List<string>? Cells { get; init; }
    }

    static DocBlock BuildBlock(List<LineInfo> lines)
    {
        var block = new DocBlock();
        TextItem? current = null;
        LineInfo? previous = null;
        int itemLines = 0;
        foreach (var line in lines)
        {
            // A table row: each cell on its own, side by side, never joined.
            if (line.Cells is { Count: > 1 } cells)
            {
                Finish(current, block, itemLines);
                current = null;
                for (int i = 0; i < cells.Count; i++)
                {
                    block.Items.Add(new TextItem { Kind = ItemKind.Line, Text = cells[i], Case = CaseOf(cells[i]), SameRow = i > 0 });
                }
                previous = null;
                continue;
            }
            var match = MarkerPattern.Match(line.Text);
            bool startsItem = match.Success;
            if (current != null && previous != null && !startsItem && Continues(previous, line))
            {
                current.Text = Join(current.Text, line.Text);
                itemLines++;
                if (current.Kind == ItemKind.Line)
                {
                    current.Kind = ItemKind.Paragraph;
                }
            }
            else
            {
                Finish(current, block, itemLines);
                current = startsItem
                    ? new TextItem { Kind = ItemKind.ListItem, Marker = match.Groups["m"].Value, Text = match.Groups["t"].Value.Trim() }
                    : new TextItem { Kind = ItemKind.Line, Text = line.Text };
                itemLines = 1;
                // Noticeably bigger text over the rest: a title.
                if (!startsItem && block.Items.Count == 0 && line.RelativeHeight >= 1.25f && lines.Count > 1)
                {
                    current.Kind = ItemKind.Heading;
                }
            }
            previous = line;
        }
        Finish(current, block, itemLines);

        // A first short line in capitals or Title Case over a paragraph or a
        // list is a title too.
        if (block.Items.Count > 1 && block.Items[0].Kind == ItemKind.Line && block.Items[0].Case != CaseStyle.Normal &&
            block.Items.Skip(1).Any(i => i.Kind is ItemKind.Paragraph or ItemKind.ListItem))
        {
            block.Items[0].Kind = ItemKind.Heading;
        }
        return block;
    }

    static void Finish(TextItem? item, DocBlock block, int lines)
    {
        if (item == null)
        {
            return;
        }
        item.Case = CaseOf(item.Text);
        block.Items.Add(item);
    }

    // Does the line go on with the sentence above, or stand on its own?
    static bool Continues(LineInfo previous, LineInfo line)
    {
        string prev = previous.Text.TrimEnd();
        string next = line.Text.TrimStart();
        if (prev.Length == 0 || next.Length == 0)
        {
            return false;
        }
        char last = prev[^1];
        char first = next[0];
        // A word broken with a hyphen, or a sentence going on in lower case.
        if ((last == '-' || last == ',') && !Terminal.Contains(last))
        {
            return true;
        }
        if (Terminal.Contains(last))
        {
            return false;
        }
        // Lines in different case styles are separate entries ("PLAY" over
        // "Options").
        if (CaseOf(prev) != CaseOf(next) && (CaseOf(prev) == CaseStyle.Upper || CaseOf(next) == CaseStyle.Upper))
        {
            return false;
        }
        if (char.IsLower(first))
        {
            return true;
        }
        // A wrapped line fills the block's width; a short line ends an entry.
        return previous.RelativeWidth >= 0.75f && prev.Count(c => c == ' ') >= 2;
    }

    static string Join(string text, string next)
    {
        text = text.TrimEnd();
        next = next.TrimStart();
        if (text.EndsWith('-') && next.Length > 0 && char.IsLower(next[0]) && text.Length > 1 && char.IsLetter(text[^2]))
        {
            return text[..^1] + next;
        }
        return text + " " + next;
    }

    public static CaseStyle CaseOf(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 2)
        {
            return CaseStyle.Normal;
        }
        if (letters.All(char.IsUpper))
        {
            return CaseStyle.Upper;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Count(char.IsLetter) >= 3).ToList();
        if (words.Count >= 2 && words.All(w => char.IsUpper(w.First(char.IsLetter))))
        {
            return CaseStyle.Title;
        }
        return CaseStyle.Normal;
    }

    // ------------------------------------------------- restoring the case

    // The translation in the original's style: MAIN MENU → ГЛАВНОЕ МЕНЮ.
    // Title Case stays Title Case in English; Russian titles capitalise
    // only the first word.
    public static string ApplyCase(string text, CaseStyle style, string language)
    {
        var culture = CultureFor(language);
        switch (style)
        {
            case CaseStyle.Upper:
                return text.ToUpper(culture);
            case CaseStyle.Title when language.StartsWith("en", StringComparison.OrdinalIgnoreCase):
                return string.Join(' ', text.Split(' ').Select(w => w.Length > 0 && char.IsLetter(w[0]) ? char.ToUpper(w[0], culture) + w[1..] : w));
            case CaseStyle.Title:
                return text.Length > 0 ? char.ToUpper(text[0], culture) + text[1..] : text;
            default:
                return text;
        }
    }

    static CultureInfo CultureFor(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
