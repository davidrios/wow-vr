using System.Globalization;
using System.Text;

namespace WowVR.Formats.Database;

/// <summary>
/// Fills in the codes in a spell's description and aura text, as the client does: <c>$s1</c> an
/// effect's value (or "min to max"), <c>$m1</c> its least, <c>$o1</c> the whole of a periodic
/// effect, <c>$t1</c> its period, <c>$d</c> the duration, <c>$a1</c> a radius, <c>$h</c> the proc
/// chance, <c>$n</c> the charges, <c>$u</c> the stacks, <c>$x1</c> the chain targets, <c>$e1</c> the
/// multiple value, <c>$r</c> the range; any of them after a spell id reads that spell
/// (<c>$12345s1</c>). <c>${...}</c> is arithmetic over those, <c>$/10;s1</c> and <c>$*2;s1</c> divide
/// or multiply one, <c>$lone:many;</c> agrees with the number before it, <c>$gmale:female;</c> takes
/// the first, <c>$?s123[a][b]</c> takes the last branch (the spell without a talent), and
/// <c>$@spelldesc123</c> puts in another spell's description. Unknown codes are left out.
/// </summary>
public static class SpellText
{
    /// <summary>How deep variables and embedded descriptions may nest: real chains run six deep (Rejuvenation's mult), and cycles stop here.</summary>
    private const int MaxDepth = 12;

    public static string Format(string text, SpellRecord spell, Func<int, SpellRecord?> lookup) => Format(text, spell, lookup, 0);

    private static string Format(string text, SpellRecord spell, Func<int, SpellRecord?> lookup, int depth)
    {
        if (text.IndexOf('$') < 0)
        {
            return text;
        }

        var b = new StringBuilder(text.Length);
        double? lastNumber = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '$' || i + 1 >= text.Length)
            {
                b.Append(c);
                i++;
                continue;
            }

            i++;
            var next = text[i];
            if (next == '$')
            {
                b.Append('$');
                i++;
            }
            else if (next == '{')
            {
                // Arithmetic, as ${$m1/10} or ${$s1*$d/1000}.
                var end = text.IndexOf('}', i);
                if (end < 0)
                {
                    break;
                }

                var inner = text[(i + 1)..end];
                i = end + 1;
                var decimals = 0;
                if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    decimals = text[i + 1] - '0';
                    i += 2;
                }

                var value = Evaluate(Format(inner, spell, lookup, depth), out var ok);
                if (ok)
                {
                    lastNumber = value;
                    b.Append(Number(value, decimals));
                }
            }
            else if (next is '/' or '*')
            {
                // $/10;s1 divides an effect's value, $*2;s1 multiplies it.
                var semicolon = text.IndexOf(';', i);
                if (semicolon < 0 || !double.TryParse(text.AsSpan(i + 1, semicolon - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var by))
                {
                    i++;
                    continue;
                }

                i = semicolon + 1;
                if (ReadValue(text, ref i, spell, lookup, depth, out _) is { } value)
                {
                    var result = next == '/' ? value.Min / by : value.Min * by;
                    lastNumber = result;
                    b.Append(Number(result, 0));
                }
            }
            else if (next is 'l' or 'L' or 'g' or 'G')
            {
                // $lday:days; agrees with the last number; $gman:woman; is the first form.
                var semicolon = text.IndexOf(';', i);
                if (semicolon < 0)
                {
                    i++;
                    continue;
                }

                var forms = text[(i + 1)..semicolon].Split(':');
                i = semicolon + 1;
                b.Append(next is 'l' or 'L' && forms.Length > 1 && lastNumber is { } n && Math.Abs(n - 1) > 1e-9 ? forms[1] : forms[0]);
            }
            else if (next == '?')
            {
                // $?s1234[with the talent][without]: the last branch, the spell as learned.
                i = SkipCondition(text, i + 1, out var branch);
                b.Append(Format(branch, spell, lookup, depth));
            }
            else if (next == '@')
            {
                var start = i + 1;
                var j = start;
                while (j < text.Length && char.IsLetter(text[j]))
                {
                    j++;
                }

                var digits = j;
                while (digits < text.Length && char.IsDigit(text[digits]))
                {
                    digits++;
                }

                var kind = text[start..j];
                i = digits;
                if (depth < MaxDepth && int.TryParse(text.AsSpan(j, digits - j), out var other) && lookup(other) is { } embedded)
                {
                    b.Append(Format(kind == "spelltooltip" ? embedded.AuraText : embedded.Description, embedded, lookup, depth + 1));
                }
            }
            else if (ReadValue(text, ref i, spell, lookup, depth, out var shown) is { } value)
            {
                lastNumber = value.Max;
                b.Append(shown);
            }
        }

        return b.ToString();
    }

    /// <summary>Reads one value code after its '$', as <c>s1</c>, <c>d</c> or <c>12345o2</c>, moving past it.</summary>
    private static (double Min, double Max)? ReadValue(string text, ref int i, SpellRecord spell, Func<int, SpellRecord?> lookup, int depth, out string shown)
    {
        shown = "";
        var start = i;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        var source = spell;
        if (i > start)
        {
            if (!int.TryParse(text.AsSpan(start, i - start), out var id))
            {
                return null;
            }

            source = lookup(id) ?? spell;
        }

        // A named variable, from the spell's SpellDescriptionVariables row.
        if (i < text.Length && text[i] == '<')
        {
            var close = text.IndexOf('>', i);
            if (close < 0)
            {
                return null;
            }

            var name = text[(i + 1)..close];
            i = close + 1;
            if (!source.Variables.TryGetValue(name, out var expression))
            {
                return null;
            }

            if (depth >= MaxDepth)
            {
                return null;
            }

            var filled = Format(expression, source, lookup, depth + 1);
            var number = Evaluate(filled.TrimStart('$', '{').TrimEnd('}'), out var ok);
            if (!ok)
            {
                shown = filled;
                return null;
            }

            shown = Number(number, 0);
            return (number, number);
        }

        if (i >= text.Length || !char.IsLetter(text[i]))
        {
            return null;
        }

        // A character's stat, as $AP, $SP or $MWB: not known here, so 0, which leaves a spell's own part.
        if (i + 1 < text.Length && char.IsLetter(text[i + 1]) && char.IsUpper(text[i]) == char.IsUpper(text[i + 1]))
        {
            while (i < text.Length && char.IsLetter(text[i]))
            {
                i++;
            }

            shown = "0";
            return (0, 0);
        }

        var code = char.ToLowerInvariant(text[i]);
        i++;
        var index = 0;
        if (i < text.Length && text[i] is >= '1' and <= '3')
        {
            index = text[i] - '1';
            i++;
        }

        var effect = index < source.Effects.Length ? source.Effects[index] : null;
        switch (code)
        {
            case 's':
                if (effect is null)
                {
                    return null;
                }

                var (min, max) = effect.Value;
                (min, max) = (Math.Abs(min), Math.Abs(max));
                shown = min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{Math.Min(min, max)} to {Math.Max(min, max)}";
                return (min, max);
            case 'm':
                return Shown(effect is null ? null : Math.Abs(effect.Value.Min), out shown);
            case 'o':
                if (effect is null)
                {
                    return null;
                }

                var ticks = effect.AmplitudeMs > 0 && source.DurationMs > 0 ? source.DurationMs / effect.AmplitudeMs : 1;
                return Shown(Math.Abs(effect.Value.Min) * ticks, out shown);
            case 't':
                return Shown(effect is null ? null : effect.AmplitudeMs / 1000.0, out shown);
            case 'd':
                shown = Duration(source.DurationMs);
                return (source.DurationMs / 1000.0, source.DurationMs / 1000.0);
            case 'a':
                return Shown(effect?.Radius, out shown);
            case 'h':
                return Shown(source.ProcChance, out shown);
            case 'n':
                return Shown(source.ProcCharges, out shown);
            case 'u':
                return Shown(source.StackAmount, out shown);
            case 'x':
                return Shown(effect?.ChainTargets, out shown);
            case 'e':
                return Shown(effect?.MultipleValue, out shown);
            case 'r':
                return Shown(source.Range, out shown);
            case 'q':
                return Shown(effect?.MiscValue, out shown);
            default:
                return null;
        }
    }

    private static (double, double)? Shown(double? value, out string shown)
    {
        shown = value is { } v ? Number(v, v == Math.Floor(v) ? 0 : 1) : "";
        return value is { } x ? (x, x) : null;
    }

    private static string Number(double value, int decimals) =>
        Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);

    /// <summary>A duration as the tooltips say it: "10 sec", "2 min", "1 hour"; "until cancelled" for -1.</summary>
    public static string Duration(int ms)
    {
        if (ms < 0)
        {
            return "until cancelled";
        }

        var seconds = ms / 1000.0;
        return seconds < 60 ? $"{Number(seconds, 1)} sec"
            : seconds < 3600 ? $"{Number(seconds / 60, 1)} min"
            : seconds < 86400 ? $"{Number(seconds / 3600, 1)} hour{(seconds >= 7200 ? "s" : "")}"
            : $"{Number(seconds / 86400, 1)} day{(seconds >= 172800 ? "s" : "")}";
    }

    /// <summary>Past a condition and its bracketed branches, giving back the last branch.</summary>
    private static int SkipCondition(string text, int i, out string branch)
    {
        branch = "";
        while (i < text.Length && text[i] != '[')
        {
            i++;
        }

        while (i < text.Length && text[i] == '[')
        {
            var depth = 0;
            var start = i + 1;
            for (; i < text.Length; i++)
            {
                if (text[i] == '[')
                {
                    depth++;
                }
                else if (text[i] == ']' && --depth == 0)
                {
                    break;
                }
            }

            branch = text[start..Math.Min(i, text.Length)];
            i++;

            // Another condition may chain, as $?a[..]?b[..][..]: past its test to its branch.
            if (i < text.Length && text[i] == '?')
            {
                while (i < text.Length && text[i] != '[')
                {
                    i++;
                }
            }
        }

        return i;
    }

    /// <summary>+, -, *, / and brackets over numbers, as what's left of a ${...} once its codes are filled in.</summary>
    private static double Evaluate(string expression, out bool ok)
    {
        var position = 0;
        ok = true;
        var nesting = 0;
        var result = Sum(expression, ref position, ref ok, ref nesting);
        return ok ? result : 0;
    }

    /// <summary>Brackets and minus signs this deep give up, rather than overflow the stack on odd data.</summary>
    private const int MaxNesting = 32;

    private static double Sum(string s, ref int p, ref bool ok, ref int nesting)
    {
        var value = Product(s, ref p, ref ok, ref nesting);
        while (ok)
        {
            Skip(s, ref p);
            if (p < s.Length && s[p] is '+' or '-')
            {
                var op = s[p++];
                var right = Product(s, ref p, ref ok, ref nesting);
                value = op == '+' ? value + right : value - right;
            }
            else
            {
                break;
            }
        }

        return value;
    }

    private static double Product(string s, ref int p, ref bool ok, ref int nesting)
    {
        var value = Atom(s, ref p, ref ok, ref nesting);
        while (ok)
        {
            Skip(s, ref p);
            if (p < s.Length && s[p] is '*' or '/')
            {
                var op = s[p++];
                var right = Atom(s, ref p, ref ok, ref nesting);
                value = op == '*' ? value * right : right == 0 ? 0 : value / right;
            }
            else
            {
                break;
            }
        }

        return value;
    }

    private static double Atom(string s, ref int p, ref bool ok, ref int nesting)
    {
        Skip(s, ref p);
        if (++nesting > MaxNesting)
        {
            ok = false;
            return 0;
        }

        if (p < s.Length && s[p] == '(')
        {
            p++;
            var inner = Sum(s, ref p, ref ok, ref nesting);
            Skip(s, ref p);
            if (p < s.Length && s[p] == ')')
            {
                p++;
            }

            return inner;
        }

        if (p < s.Length && s[p] == '-')
        {
            p++;
            return -Atom(s, ref p, ref ok, ref nesting);
        }

        var start = p;
        while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.'))
        {
            p++;
        }

        // "10 to 12" from a ranged $s: the least.
        if (p > start && double.TryParse(s.AsSpan(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var rest = p;
            Skip(s, ref rest);
            if (s.AsSpan(rest).StartsWith("to "))
            {
                rest += 3;
                Skip(s, ref rest);
                while (rest < s.Length && (char.IsDigit(s[rest]) || s[rest] == '.'))
                {
                    rest++;
                }

                p = rest;
            }

            return number;
        }

        ok = false;
        return 0;
    }

    private static void Skip(string s, ref int p)
    {
        while (p < s.Length && s[p] == ' ')
        {
            p++;
        }
    }
}
