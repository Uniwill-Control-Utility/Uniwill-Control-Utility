using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EcRegistryGen
{
    /// <summary>
    /// Reads the EC registry JSON and emits the C# vault. Deliberately dependency-free
    /// (System.Text.Json only) and free of clocks, environment state and culture-sensitive
    /// formatting, so the same registry always produces byte-identical output. The Core
    /// suite links this file and pins that the committed artifact equals this code's output.
    /// </summary>
    public static class EcRegistryGenerator
    {
        /// <summary>Header the writer stamps on the artifact (tests pin this marker).</summary>
        public const string GeneratedMarker = "Generated from Ec\\Registry\\EcAddresses.json by tools\\EcRegistryGen";

        private static readonly string[] SectionOrder = { "address", "value", "valueArray", "region", "driver" };

        private static readonly string[] IgnoredSections = { "_readme", "meta" };

        private static readonly Dictionary<string, string[]> RequiredFields = new(StringComparer.Ordinal)
        {
            ["address"] = new[] { "name", "value", "domain", "evidence" },
            ["value"] = new[] { "name", "value", "register", "evidence" },
            ["valueArray"] = new[] { "name", "values", "register", "evidence" },
            ["region"] = new[] { "name", "low", "high", "domain", "evidence" },
            ["driver"] = new[] { "name", "value", "type", "evidence" },
        };

        private static readonly Dictionary<string, string[]> AllowedFields = new(StringComparer.Ordinal)
        {
            ["address"] = new[] { "name", "value", "type", "domain", "access", "platform", "evidence" },
            ["value"] = new[] { "name", "value", "type", "register", "domain", "platform", "evidence" },
            ["valueArray"] = new[] { "name", "values", "type", "register", "domain", "platform", "evidence" },
            ["region"] = new[] { "name", "low", "high", "domain", "access", "platform", "evidence" },
            ["driver"] = new[] { "name", "value", "type", "domain", "evidence" },
        };

        private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        public sealed class Result
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public string Code { get; init; } = string.Empty;
        }

        public static Result Generate(string registryJson)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(registryJson, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            }
            catch (JsonException ex)
            {
                return Fail("registry is not valid JSON: " + ex.Message);
            }

            using (doc)
            {
                return Generate(doc.RootElement);
            }
        }

        private static Result Generate(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("registry root must be an object");

            var sections = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                string section = prop.Name;
                if (IgnoredSections.Contains(section, StringComparer.Ordinal))
                    continue;

                if (!RequiredFields.ContainsKey(section))
                    return Fail($"unknown registry section '{section}' (allowed: {string.Join(", ", SectionOrder)}, {string.Join(", ", IgnoredSections)})");

                if (prop.Value.ValueKind != JsonValueKind.Array)
                    return Fail($"section '{section}' must be an array of entries");

                var items = new List<Item>();
                int index = 0;
                foreach (JsonElement entry in prop.Value.EnumerateArray())
                {
                    index++;
                    Result parsed = Item.TryParse(section, index, entry, out Item? item);
                    if (!parsed.Success)
                        return parsed;
                    items.Add(item!);
                }

                sections[section] = items;
            }

            // Global name uniqueness: one identifier, one meaning.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string section in SectionOrder)
            {
                if (!sections.TryGetValue(section, out List<Item>? items))
                    continue;

                foreach (Item item in items)
                {
                    foreach (string id in item.EmittedNames)
                    {
                        if (!names.Add(id))
                            return Fail($"duplicate identifier '{id}' (section '{section}', entry '{item.Name}')");
                    }
                }
            }

            int total = SectionOrder.Where(sections.ContainsKey).Sum(s => sections[s].Count);
            var sb = new StringBuilder();
            sb.Append("// <auto-generated />").Append('\n');
            sb.Append("// ").Append(GeneratedMarker).Append('\n');
            sb.Append("// Source of truth: the registry JSON - do not edit this file, edit the registry.").Append('\n');
            sb.Append("// Entries: ").Append(total.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("// <auto-generated />").Append('\n');
            sb.Append('\n');
            sb.Append("namespace UCU.Core.Ec.Generated").Append('\n');
            sb.Append('{').Append('\n');
            sb.Append("    /// <summary>").Append('\n');
            sb.Append("    /// EC wiring constants (addresses, register encodings, address regions, driver IOCTLs).").Append('\n');
            sb.Append("    /// Generated from the EC registry; product code must not declare EC numbers of its own.").Append('\n');
            sb.Append("    /// </summary>").Append('\n');
            sb.Append("    public static class EcVault").Append('\n');
            sb.Append("    {").Append('\n');

            foreach (string section in SectionOrder)
            {
                if (!sections.TryGetValue(section, out List<Item>? items) || items.Count == 0)
                    continue;

                sb.Append($"        // --- {section} ({items.Count}) ---").Append('\n');
                foreach (Item item in items)
                {
                    sb.Append("        /// <summary>").Append(EscapeXml(item.Summary)).Append("</summary>").Append('\n');
                    foreach (string line in item.Emit())
                        sb.Append("        ").Append(line).Append('\n');
                    sb.Append('\n');
                }
            }

            sb.Append("    }").Append('\n');
            sb.Append('}').Append('\n');

            return new Result { Success = true, Code = sb.ToString().Replace("\n", "\r\n") };
        }

        private sealed class Item
        {
            public string Section = string.Empty;
            public string Name = string.Empty;
            public string Type = "byte";
            public string? Domain;
            public string? Register;
            public string Access = "rw";
            public string Platform = "all";
            public string Evidence = string.Empty;
            public string Literal = string.Empty;
            public string[] ArrayLiterals = Array.Empty<string>();
            public string LowLiteral = string.Empty;
            public string HighLiteral = string.Empty;

            public string[] EmittedNames => Section == "region"
                ? new[] { "REGION_" + Name + "_START", "REGION_" + Name + "_END" }
                : new[] { Name };

            public string Summary
            {
                get
                {
                    var parts = new List<string> { Section == "region" ? "region " + Name : Name };
                    if (Domain is not null) parts.Add("domain: " + Domain);
                    if (Register is not null) parts.Add("register: " + Register);
                    if (Section is "address" or "region") parts.Add("access: " + Access);
                    if (Platform != "all") parts.Add("platform: " + Platform);
                    parts.Add(Evidence);
                    return string.Join(" | ", parts);
                }
            }

            public IEnumerable<string> Emit()
            {
                switch (Section)
                {
                    case "region":
                        yield return $"public const ushort REGION_{Name}_START = {LowLiteral};";
                        yield return $"public const ushort REGION_{Name}_END = {HighLiteral};";
                        break;
                    case "valueArray":
                        yield return $"public static readonly {ElementType}[] {Name} = {{ {string.Join(", ", ArrayLiterals)} }};";
                        break;
                    default:
                        yield return $"public const {CsharpType} {Name} = {Literal};";
                        break;
                }
            }

            private string CsharpType => Type switch
            {
                "byte" => "byte",
                "int" => "int",
                "ushort" => "ushort",
                "uint" => "uint",
                _ => Type,
            };

            private string ElementType => Type == "int" ? "int" : "byte";

            public static Result TryParse(string section, int index, JsonElement entry, out Item? item)
            {
                item = null;

                if (entry.ValueKind != JsonValueKind.Object)
                    return Fail($"[{section}#{index}] entry must be an object");

                string where = $"[{section}#{index}]";
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty prop in entry.EnumerateObject())
                {
                    if (!AllowedFields[section].Contains(prop.Name, StringComparer.Ordinal))
                        return Fail($"{where} unknown field '{prop.Name}' (allowed: {string.Join(", ", AllowedFields[section])})");
                    fields[prop.Name] = prop.Value;
                }

                foreach (string required in RequiredFields[section])
                {
                    if (!fields.ContainsKey(required))
                        return Fail($"{where} missing required field '{required}'");
                }

                var parsed = new Item { Section = section };

                parsed.Name = Text(fields["name"]);
                if (parsed.Name.Length == 0 || !Identifier.IsMatch(parsed.Name))
                    return Fail($"{where} name '{parsed.Name}' is not a C# identifier");

                parsed.Evidence = Text(fields["evidence"]);
                if (parsed.Evidence.Trim().Length < 3)
                    return Fail($"{where} evidence must state where the number came from (provenance discipline)");

                parsed.Domain = fields.TryGetValue("domain", out JsonElement domain) ? Text(domain) : null;
                parsed.Register = fields.TryGetValue("register", out JsonElement register) ? Text(register) : null;
                parsed.Access = fields.TryGetValue("access", out JsonElement access) ? Text(access) : "rw";
                parsed.Platform = fields.TryGetValue("platform", out JsonElement platform) ? Text(platform) : "all";

                if (!new[] { "rw", "ro", "wo" }.Contains(parsed.Access, StringComparer.Ordinal))
                    return Fail($"{where} access '{parsed.Access}' must be rw, ro or wo");
                if (!new[] { "all", "amd", "intel" }.Contains(parsed.Platform, StringComparer.Ordinal))
                    return Fail($"{where} platform '{parsed.Platform}' must be all, amd or intel (EcPlatform vocabulary)");

                if (section == "region")
                {
                    parsed.Type = "ushort";
                    Result low = Number(fields["low"], "ushort", where + ".low", out string lowLiteral);
                    if (!low.Success) return low;
                    Result high = Number(fields["high"], "ushort", where + ".high", out string highLiteral);
                    if (!high.Success) return high;
                    parsed.LowLiteral = lowLiteral;
                    parsed.HighLiteral = highLiteral;
                    if (Convert.ToUInt64(lowLiteral, 16) > Convert.ToUInt64(highLiteral, 16))
                        return Fail($"{where} low is above high");
                    item = parsed;
                    return new Result { Success = true };
                }

                parsed.Type = fields.TryGetValue("type", out JsonElement type) ? Text(type) : DefaultType(section);
                if (!new[] { "byte", "ushort", "uint", "int" }.Contains(parsed.Type, StringComparer.Ordinal))
                    return Fail($"{where} type '{parsed.Type}' must be byte, ushort, uint or int");

                if (section == "address" && parsed.Type != "ushort")
                    return Fail($"{where} EC addresses are ushort");
                if (section == "driver" && parsed.Type != "uint")
                    return Fail($"{where} driver IOCTL codes are uint");

                if (section == "valueArray")
                {
                    if (fields["values"].ValueKind != JsonValueKind.Array)
                        return Fail($"{where}.values must be an array");

                    var literals = new List<string>();
                    int i = 0;
                    foreach (JsonElement value in fields["values"].EnumerateArray())
                    {
                        i++;
                        Result n = Number(value, parsed.Type, $"{where}.values[{i}]", out string literal);
                        if (!n.Success) return n;
                        literals.Add(literal);
                    }

                    if (literals.Count == 0)
                        return Fail($"{where}.values must not be empty");
                    parsed.ArrayLiterals = literals.ToArray();
                    item = parsed;
                    return new Result { Success = true };
                }

                Result number = Number(fields["value"], parsed.Type, where + ".value", out string valueLiteral);
                if (!number.Success) return number;
                parsed.Literal = valueLiteral;
                item = parsed;
                return new Result { Success = true };
            }

            private static string DefaultType(string section) => section switch
            {
                "address" => "ushort",
                "driver" => "uint",
                _ => "byte",
            };

            private static string Text(JsonElement element) =>
                element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();

            /// <summary>Parses 0x-hex or decimal into the canonical literal for the type.</summary>
            private static Result Number(JsonElement element, string type, string where, out string literal)
            {
                literal = string.Empty;

                string text = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonValueKind.Number => element.GetRawText(),
                    _ => string.Empty,
                };

                text = text.Trim();
                bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                ulong value;
                try
                {
                    value = hex
                        ? Convert.ToUInt64(text.Substring(2), 16)
                        : ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    return Fail($"{where} '{text}' is not a number (0x-hex or decimal)");
                }

                if (text.Length == 0)
                    return Fail($"{where} is empty");

                switch (type)
                {
                    case "byte":
                        if (value > byte.MaxValue) return Fail($"{where} {text} exceeds byte (0x100)");
                        literal = "0x" + value.ToString("X2", CultureInfo.InvariantCulture);
                        break;
                    case "ushort":
                        if (value > ushort.MaxValue) return Fail($"{where} {text} exceeds the EC address space (0x10000)");
                        literal = "0x" + value.ToString("X4", CultureInfo.InvariantCulture);
                        break;
                    case "uint":
                        if (value > uint.MaxValue) return Fail($"{where} {text} exceeds uint");
                        literal = "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
                        break;
                    case "int":
                        if (value > int.MaxValue) return Fail($"{where} {text} exceeds int");
                        literal = value.ToString(CultureInfo.InvariantCulture);
                        break;
                }

                return new Result { Success = true };
            }
        }

        private static string EscapeXml(string text)
        {
            return text.Replace("&", "&amp;", StringComparison.Ordinal)
                       .Replace("<", "&lt;", StringComparison.Ordinal)
                       .Replace(">", "&gt;", StringComparison.Ordinal);
        }

        private static Result Fail(string message) => new() { Success = false, Error = message };
    }
}
