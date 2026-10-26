namespace IxIFlow.Templates.Liquid;

// Fluid's AST does not retain source spans. Match member/filter references against Liquid
// tokens, excluding text, quoted literals, declarations, raw and comment bodies. Loop
// parameters have independent token ranges because Fluid visits them in a fixed order.
internal sealed class LiquidSourceLocations
{
    private readonly List<(string Text, int Line, int Column)> _tokens = [];
    private readonly Queue<(int End, IReadOnlyDictionary<string, LiquidSourceLocations> Expressions)> _loops = new();
    private int _next;

    private LiquidSourceLocations(IEnumerable<(string Text, int Line, int Column)> tokens) => _tokens.AddRange(tokens);

    internal LiquidSourceLocations(string source)
    {
        var lines = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++) if (source[index] == '\n') lines.Add(index + 1);
        var position = 0;
        string? skipped = null;
        while (position + 1 < source.Length)
        {
            if (skipped != null)
            {
                // A raw/comment body can contain unmatched quotes and tag-like text.
                // Only its closing tag participates in the Liquid grammar.
                position = source.IndexOf("{%", position, StringComparison.Ordinal);
                if (position < 0) break;
                var word = position + 2;
                if (word < source.Length && source[word] == '-') word++;
                while (word < source.Length && char.IsWhiteSpace(source[word])) word++;
                var closing = "end" + skipped;
                var tail = word + closing.Length;
                if (!source.AsSpan(word).StartsWith(closing, StringComparison.Ordinal) ||
                    tail < source.Length && !char.IsWhiteSpace(source[tail]) && source[tail] is not ('-' or '%'))
                { position += 2; continue; }
            }
            if (source[position] != '{' || source[position + 1] is not ('{' or '%')) { position++; continue; }
            var tag = source[position + 1] == '%';
            var start = position + 2;
            var end = start;
            char quote = '\0';
            for (; end + 1 < source.Length; end++)
            {
                var character = source[end];
                if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
                if (character is '\'' or '"') { quote = character; continue; }
                if (character == (tag ? '%' : '}') && source[end + 1] == '}') break;
            }
            var tokens = new List<(string Text, int Line, int Column)>();
            var parameters = new List<(string Name, int Token)>();
            for (var index = start; index < end;)
            {
                var character = source[index];
                if (character is '\'' or '"')
                {
                    var delimiter = character; index++;
                    while (index < end && source[index] != delimiter) index++;
                    index++; continue;
                }
                if (!char.IsLetter(character) && character != '_') { index++; continue; }
                var word = index++;
                while (index < end && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '?' or '-')) index++;
                var line = lines.BinarySearch(word);
                if (line < 0) line = ~line - 1;
                tokens.Add((source[word..index], line + 1, word - lines[line] + 1));
                var colon = index;
                while (colon < end && char.IsWhiteSpace(source[colon])) colon++;
                if (colon < end && source[colon] == ':' && tokens[^1].Text is "limit" or "offset" or "cols")
                    parameters.Add((tokens[^1].Text, tokens.Count - 1));
            }
            var command = tag ? tokens.FirstOrDefault().Text : null;
            if (skipped != null)
            {
                if (command == "end" + skipped) skipped = null;
            }
            else if (command is "raw" or "comment") skipped = command;
            else
            {
                // These names declare variables; they are not member reads in the AST.
                var omit = command is "assign" or "capture" ? 2 : command is "for" or "tablerow" ? 3 :
                    command != null && command is not ("include" or "render") ? 1 : 0;
                _tokens.AddRange(tokens.Skip(omit));
                if (command is "for" or "tablerow")
                {
                    var expressions = new Dictionary<string, LiquidSourceLocations>(StringComparer.Ordinal)
                    {
                        ["source"] = new(tokens.Skip(omit).Take((parameters.Count == 0 ? tokens.Count : parameters[0].Token) - omit)),
                        ["limit"] = new([]), ["offset"] = new([]), ["cols"] = new([])
                    };
                    for (var index = 0; index < parameters.Count; index++)
                    {
                        var (name, first) = parameters[index];
                        var last = index + 1 < parameters.Count ? parameters[index + 1].Token : tokens.Count;
                        expressions[name] = new(tokens.Skip(first + 1).Take(last - first - 1));
                    }
                    _loops.Enqueue((_tokens.Count, expressions));
                }
            }
            position = Math.Min(source.Length, end + 2);
        }
    }

    internal IReadOnlyDictionary<string, LiquidSourceLocations> Loop()
    {
        var loop = _loops.Dequeue();
        _next = loop.End;
        return loop.Expressions;
    }

    internal (int? Line, int? Column) Find(string name)
    {
        for (var index = _next; index < _tokens.Count; index++)
            if (_tokens[index].Text == name)
            {
                _next = index + 1;
                return (_tokens[index].Line, _tokens[index].Column);
            }
        return (null, null);
    }
}
