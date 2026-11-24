using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KDS.Dashboard.Services;

/// <summary>The chart owns the shape; the editor can only replace scalar values.</summary>
public sealed class DeployValues
{
    private readonly YamlStream _yaml = new();
    public List<ValueLine> Lines { get; } = [];

    public DeployValues(string yaml)
    {
        _yaml.Load(new StringReader(yaml));
        if (_yaml.Documents.Count != 1 || _yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("Values musi zawierać jedną mapę YAML.");
        Visit(root, 0, "");
    }

    private void Visit(YamlNode node, int depth, string path)
    {
        if (node is not YamlMappingNode map)
            throw new ArgumentException("Edytor wymaga map i wartości skalarnych w values.yaml.");
        foreach (var (key, value) in map.Children)
        {
            var name = ((YamlScalarNode)key).Value!;
            var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
            Lines.Add(new ValueLine(depth, name, fullPath, value as YamlScalarNode));
            if (value is not YamlScalarNode) Visit(value, depth + 1, fullPath);
        }
    }

    public string Serialize()
    {
        foreach (var line in Lines) line.Apply();
        using var writer = new StringWriter();
        _yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    public static void ValidateShape(string template, string submitted)
    {
        var expected = new DeployValues(template);
        var actual = new DeployValues(submitted);
        if (expected.Lines.Count != actual.Lines.Count)
            throw new ArgumentException("Nie można zmieniać kluczy ani struktury values.");
        foreach (var line in expected.Lines)
        {
            var match = actual.Lines.SingleOrDefault(x => x.Path == line.Path);
            if (match is null || match.IsValue != line.IsValue || match.Kind != line.Kind)
                throw new ArgumentException($"Nieprawidłowa struktura lub typ wartości: {line.Path}.");
        }
    }

    public sealed class ValueLine
    {
        private readonly YamlScalarNode? _node;
        public int Depth { get; }
        public string Name { get; }
        public string Path { get; }
        public bool IsValue => _node is not null;
        public string Kind { get; }
        public string Value { get; set; }

        public ValueLine(int depth, string name, string path, YamlScalarNode? node)
        {
            Depth = depth; Name = name; Path = path; _node = node;
            Value = node?.Value ?? "";
            Kind = node?.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted ? "string"
                : Value is "true" or "false" ? "boolean"
                : Regex.IsMatch(Value, @"\A-?(0|[1-9][0-9]*)\z") ? "integer"
                : decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "number"
                : "string";
        }

        public void Apply()
        {
            if (_node is null) return;
            var valid = Kind switch
            {
                "boolean" => Value is "true" or "false",
                "integer" => Regex.IsMatch(Value, @"\A-?(0|[1-9][0-9]*)\z"),
                "number" => decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                _ => true
            };
            if (!valid) throw new ArgumentException($"Nieprawidłowa wartość {Path} (typ: {Kind}).");
            _node.Value = Value;
            // Quote strings, even if the entered text looks like a YAML number or boolean.
            _node.Style = Kind == "string" ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
        }
    }
}
