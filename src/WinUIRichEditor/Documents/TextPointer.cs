using System;
using System.Collections.Generic;

namespace WinUIRichEditor.Documents;

/// <summary>An immutable-by-convention position inside a <see cref="Paragraph"/>: the paragraph reference
/// plus a character offset. Inline images count as one logical character (the U+FFFC placeholder).</summary>
public class TextPointer : IComparable<TextPointer>
{
    /// <summary>The paragraph that contains this position.</summary>
    public Paragraph? Paragraph { get; set; }
    /// <summary>The character offset within <see cref="Paragraph"/>. Inline images count as 1.</summary>
    public int Offset { get; set; }

    /// <summary>Caret affinity at a soft-wrap boundary. At a wrap, "end of line k" and "start of line
    /// k+1" are the SAME logical offset; when this is true the caret renders after the last glyph of
    /// the earlier line instead of before the first glyph of the next. Set by End and by clicking the
    /// trailing half of a line's last glyph. Display-only: equality and ordering ignore it.</summary>
    public bool AtLineEnd { get; set; }

    /// <summary>Creates a new pointer at <paramref name="offset"/> inside <paramref name="paragraph"/>.</summary>
    public TextPointer(Paragraph? paragraph, int offset)
    {
        Paragraph = paragraph;
        Offset = offset;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TextPointer t && t.Paragraph == Paragraph && t.Offset == Offset;
    /// <inheritdoc/>
    public override int GetHashCode() => (Paragraph?.GetHashCode() ?? 0) ^ Offset.GetHashCode();
    /// <summary>Equality comparison.</summary>
    public static bool operator ==(TextPointer? a, TextPointer? b) => ReferenceEquals(a, null) ? ReferenceEquals(b, null) : a.Equals(b);
    /// <summary>Inequality comparison.</summary>
    public static bool operator !=(TextPointer? a, TextPointer? b) => !(a == b);

    /// <inheritdoc/>
    public int CompareTo(TextPointer? other)
    {
        if (other == null) return 1;
        if (ReferenceEquals(this.Paragraph, other.Paragraph))
        {
            return Offset.CompareTo(other.Offset);
        }

        // Different paragraphs: PositionOrder decides — a position inside an inline table sits between the table's
        // offset and the next in its host paragraph. Where that is not enough it asks for the paragraphs' place in
        // the document order, and only about the few paragraphs that can matter, so one walk locating those ends it.
        // A paragraph not found keeps index -1, so it sorts before any present one.
        var doc = GetFlowDocument(this.Paragraph);
        if (doc == null || this.Paragraph == null || other.Paragraph == null) return 0; // cannot compare without document

        // The editor's block order, from the walk that owns it (BlockWalk). Every block takes one index,
        // tables included — that is the historical numbering these comparisons and undo's caret
        // restoration both rely on. The walk is lazy, so locating every paragraph asked about ends it.
        var wanted = new HashSet<Paragraph>();
        PositionOrder.IndexedParagraphs(this.Paragraph, other.Paragraph, wanted);
        var found = new Dictionary<Paragraph, int>();
        int index = 0;
        foreach (var block in BlockWalk.DocumentOrder(doc.Blocks))
        {
            if (block is Paragraph p && wanted.Contains(p) && !found.ContainsKey(p)) found[p] = index;
            index++;
            if (found.Count == wanted.Count) break;
        }
        return PositionOrder.Compare(this.Paragraph, Offset, other.Paragraph, other.Offset,
            p => found.TryGetValue(p, out int i) ? i : -1);
    }

    private FlowDocument? GetFlowDocument(TextElement? element)
    {
        object? current = element;
        while (current != null)
        {
            if (current is FlowDocument doc) return doc;
            if (current is TextElement te) current = te.Parent;
            else break;
        }
        return null;
    }
}
