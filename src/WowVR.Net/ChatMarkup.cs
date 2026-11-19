using System.Text;

namespace WowVR.Net;

/// <summary>
/// WoW's chat markup as Godot's BBCode: <c>|cAARRGGBB</c> ... <c>|r</c> colors,
/// <c>|Hkind:id:...|h[text]|h</c> links as <c>[url=kind:id]</c>, <c>|T...|t</c> textures left out,
/// <c>|n</c> a new line and <c>||</c> a bar. Chat comes from other players, so nothing in it can
/// make a tag of its own: text is escaped, only the link kinds the game has with a numeric id
/// become links, and every tag opened is closed in order by the end of the line.
/// </summary>
public static class ChatMarkup
{
    private static readonly HashSet<string> LinkKinds = new(StringComparer.Ordinal)
    {
        "item", "spell", "quest", "enchant", "talent", "achievement", "trade", "glyph",
    };

    private enum Tag
    {
        Color,
        Link,
    }

    public static string ToBbcode(string raw)
    {
        var b = new StringBuilder(raw.Length + 32);
        var open = new List<(Tag Tag, string Opening)>();

        void Open(Tag tag, string opening)
        {
            b.Append(opening);
            open.Add((tag, opening));
        }

        // Closes the innermost tag of a kind, closing and reopening any opened inside it, so tags never cross.
        void Close(Tag tag)
        {
            var index = open.FindLastIndex(t => t.Tag == tag);
            if (index < 0)
            {
                return;
            }

            for (var i = open.Count - 1; i >= index; i--)
            {
                b.Append(open[i].Tag == Tag.Color ? "[/color]" : "[/url]");
            }

            var reopened = open.GetRange(index + 1, open.Count - index - 1);
            open.RemoveRange(index, open.Count - index);
            foreach (var (t, opening) in reopened)
            {
                Open(t, opening);
            }
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '[')
            {
                b.Append("[lb]");
                continue;
            }

            if (c != '|' || i + 1 >= raw.Length)
            {
                b.Append(c);
                continue;
            }

            var code = raw[i + 1];
            switch (code)
            {
                case '|':
                    b.Append('|');
                    i++;
                    break;
                case 'c' when i + 10 <= raw.Length && IsHex(raw.AsSpan(i + 2, 8)):
                    Open(Tag.Color, $"[color=#{raw.AsSpan(i + 4, 6)}]");
                    i += 9;
                    break;
                case 'r':
                    Close(Tag.Color);
                    i++;
                    break;
                case 'n':
                    b.Append('\n');
                    i++;
                    break;
                case 'H':
                {
                    // |Hitem:25:0:...|h opens a link; its text follows, then |h closes it.
                    var end = raw.IndexOf("|h", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        i++;
                        break;
                    }

                    var fields = raw[(i + 2)..end].Split(':');
                    if (fields.Length > 1 && LinkKinds.Contains(fields[0]) && fields[1].Length is > 0 and < 12 && fields[1].All(char.IsAsciiDigit))
                    {
                        Open(Tag.Link, $"[url={fields[0]}:{fields[1]}]");
                    }

                    i = end + 1;
                    break;
                }

                case 'h':
                    Close(Tag.Link);
                    i++;
                    break;
                case 'T':
                {
                    var end = raw.IndexOf("|t", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? i + 1 : end + 1;
                    break;
                }

                default:
                    b.Append(c);
                    break;
            }
        }

        for (var i = open.Count - 1; i >= 0; i--)
        {
            b.Append(open[i].Tag == Tag.Color ? "[/color]" : "[/url]");
        }

        return b.ToString();
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
