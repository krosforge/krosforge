namespace Krosforge.Core;

public sealed record DropCriterion(string Code, char Operator, IReadOnlyList<long> Arguments);

public sealed class DropCondition
{
    private abstract record Node
    {
        internal abstract bool? Evaluate(Func<DropCriterion, bool?> facts);
    }
    private sealed record Atom(DropCriterion Criterion) : Node
    {
        internal override bool? Evaluate(Func<DropCriterion, bool?> facts) => facts(Criterion);
    }
    private sealed record Logical(Node Left, Node Right, bool And) : Node
    {
        internal override bool? Evaluate(Func<DropCriterion, bool?> facts)
        {
            var a = Left.Evaluate(facts); var b = Right.Evaluate(facts);
            if (And) return a == false || b == false ? false : a == true && b == true ? true : null;
            return a == true || b == true ? true : a == false && b == false ? false : null;
        }
    }
    private readonly Node? root;
    private DropCondition(Node? root) => this.root = root;
    public bool Matches(Func<DropCriterion, bool?> facts) => root is null ||
        (root.Evaluate(facts) ?? throw new InvalidOperationException("Drop eligibility contains unresolved facts."));
    public static DropCondition Parse(string text)
    {
        if (text.Length > 4096) throw new ArgumentException("Drop condition is too long.");
        if (text.Length == 0) return new(null);
        var at = 0; var atoms = 0;
        Node Expression(int depth)
        {
            var left = Conjunction(depth);
            while (at < text.Length && text[at] == '|') { at++; left = new Logical(left, Conjunction(depth), false); }
            return left;
        }
        Node Conjunction(int depth)
        {
            var left = Term(depth);
            while (at < text.Length && text[at] == '&') { at++; left = new Logical(left, Term(depth), true); }
            return left;
        }
        Node Term(int depth)
        {
            if (depth > 32 || ++atoms > 256) throw new ArgumentException("Drop condition is too complex.");
            if (at < text.Length && text[at] == '(')
            {
                at++; var expression = Expression(depth + 1);
                if (at >= text.Length || text[at++] != ')') throw new ArgumentException("Unclosed drop condition.");
                return expression;
            }
            var start = at;
            while (at < text.Length && char.IsAsciiLetter(text[at])) at++;
            if (at == start || at >= text.Length || text[at] is not ('=' or '!' or '<' or '>')) throw new ArgumentException("Invalid drop criterion.");
            var code = text[start..at]; var op = text[at++]; var values = new List<long>();
            while (true)
            {
                start = at;
                if (at < text.Length && text[at] == '-') at++;
                while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
                if (!long.TryParse(text.AsSpan(start, at - start), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("Invalid drop criterion value.");
                values.Add(value);
                if (values.Count > 8) throw new ArgumentException("Too many criterion arguments.");
                if (at >= text.Length || text[at] != ',') break;
                at++;
            }
            return new Atom(new(code, op, values.ToArray()));
        }
        var root = Expression(0);
        if (at != text.Length) throw new ArgumentException("Invalid drop condition suffix.");
        return new(root);
    }
}
