using System.Collections.Generic;
using System.Linq;

namespace WinUIRichEditor.Documents;

/// <summary>Enforces the 32-level ceiling on nested tables across model mutations and importers.</summary>
internal static class TableNesting
{
    internal const int Max = 32;

    internal static int DepthAt(Paragraph? p)
    {
        if (p == null) return 0;
        int depth = 0;
        for (object? o = p.Parent; o != null;)
        {
            switch (o)
            {
                case TableCell c: o = c.Parent; break;
                case TableBlock t: depth++; o = t.Parent; break;
                case InlineTable it: o = it.Parent; break;
                case Paragraph host: o = host.Parent; break;
                default: o = null; break;
            }
        }
        return depth;
    }

    internal static int Height(object? content) => content switch
    {
        TableBlock t => 1 + t.Cells.SelectMany(row => row).Select(c => Height(c.Blocks)).DefaultIfEmpty(0).Max(),
        InlineTable it => Height(it.Table),
        Paragraph p => p.Inlines.OfType<InlineTable>().Select(Height).DefaultIfEmpty(0).Max(),
        IEnumerable<Block> blocks => blocks.Select(Height).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    internal static bool Fits(Paragraph? p, object? content) => DepthAt(p) + Height(content) <= Max;

    internal static void Flatten(IList<Block> blocks, int room)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case TableBlock t when room <= 0:
                    var content = t.LogicalCells().SelectMany(lc => lc.cell.Blocks).ToList();
                    blocks.RemoveAt(i);
                    for (int k = 0; k < content.Count; k++) blocks.Insert(i + k, content[k]);
                    i--;
                    break;
                case TableBlock t:
                    foreach (var row in t.Cells)
                        foreach (var cell in row)
                            Flatten(cell.Blocks, room - 1);
                    break;
                case Paragraph p:
                    FlattenParagraphInlines(p, room);
                    break;
            }
        }
    }

    internal static void FlattenParagraphInlines(Paragraph p, int room)
    {
        bool removedAny = false;
        for (int k = 0; k < p.Inlines.Count; k++)
        {
            if (p.Inlines[k] is not InlineTable it) continue;
            if (room > 0)
            {
                foreach (var row in it.Table.Cells)
                    foreach (var cell in row)
                        Flatten(cell.Blocks, room - 1);
            }
            else
            {
                removedAny = true;
                string txt = CellText(it.Table);
                if (txt.Length > 0)
                    p.Inlines[k] = new Run { Text = txt, Parent = p };
                else
                {
                    p.Inlines.RemoveAt(k);
                    k--;
                }
            }
        }
        if (removedAny && p.Inlines.Count == 0)
            p.Inlines.Add(new Run { Text = "", Parent = p });
    }

    private static string CellText(TableBlock t)
    {
        var lines = new List<string>();
        foreach (var (_, _, cell) in t.LogicalCells())
            foreach (var p in ParagraphsIn(cell.Blocks))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var inl in p.Inlines)
                    if (inl is Run r && !string.IsNullOrEmpty(r.Text))
                        sb.Append(r.Text);
                if (sb.Length > 0) lines.Add(sb.ToString());
            }
        return string.Join("\n", lines);
    }

    private static IEnumerable<Paragraph> ParagraphsIn(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Paragraph p)
            {
                yield return p;
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var cp in ParagraphsIn(cell.Blocks))
                                yield return cp;
            }
            else if (b is TableBlock tb)
            {
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var cp in ParagraphsIn(cell.Blocks))
                        yield return cp;
            }
        }
    }
}
