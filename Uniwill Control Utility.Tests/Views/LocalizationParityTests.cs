using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Uniwill_Control_Utility.Tests.Views;

/// <summary>
/// The localization gate the app does not provide at compile time.
///
/// English is the lookup key: <c>LocalizationService.Get("Thermal failsafe")</c> resolves to the
/// shipped catalog value, and an unknown key silently renders as its English argument. That silence
/// is why a whole card (the thermal failsafe) shipped with strings that were not keys at all, while
/// every catalog was also missing them - nothing fails, the UI just stays English.
///
/// So: every user-visible literal must have an en-US key, and every shipped catalog must carry the
/// same key set as en-US.
/// </summary>
public sealed class LocalizationParityTests
{
    /// <summary>Literals that are not prose and never need a catalog entry.</summary>
    private static readonly HashSet<string> NotProse = new(StringComparer.Ordinal)
    {
        // Units, ranges and status fragments rendered as XAML defaults.
        "150 W", "45 W", "5 W", "60 W", "75 W", "85 W", "95 °C",
        "0 MHz", "0 mV", "11 V", "7 V", "8 V",
        "°C → ", "-- GHz", "-- W", "-- mA", "-- mV", "-- °C", "--°C",
        "Cycle 0/8", "RPM",
        // Legal notice on the bundled installer, redistributed verbatim.
        "This installer may be redistributed unmodified. Copyright © 2026 namazso <admin@namazso.eu>.",
    };

    private static readonly Regex Prose = new("[a-z]{2}", RegexOptions.Compiled);

    [Fact]
    public void EveryShippedCatalogHasTheSameKeysAsEnglish()
    {
        string localization = Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Localization");
        HashSet<string> english = ReadCatalog(Path.Combine(localization, "en-US.json")).Keys.ToHashSet(StringComparer.Ordinal);

        List<string> offenders = new();

        foreach (string file in Directory.EnumerateFiles(localization, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string culture = Path.GetFileNameWithoutExtension(file);
            if (culture is "en-US")
            {
                continue;
            }

            Dictionary<string, string> catalog = ReadCatalog(file);
            string[] missing = english.Where(key => !catalog.ContainsKey(key)).ToArray();

            if (missing.Length > 0)
            {
                offenders.Add($"{culture}: {missing.Length} keys absent from {culture}.json - {string.Join(" | ", missing.Take(5))}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A catalog is missing keys that en-US defines, so those strings can never be translated:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryUserVisibleLiteralHasAnEnglishKey()
    {
        string root = RepositoryRoot();
        string app = Path.Combine(root, "Uniwill Control Utility");
        HashSet<string> english = ReadCatalog(Path.Combine(app, "Localization", "en-US.json")).Keys.ToHashSet(StringComparer.Ordinal);

        Regex xamlAttribute = new("\\b(?:Text|Content|Header|Title|ToolTip|PlaceholderText|Watermark)=\"([^\"]{3,})\"", RegexOptions.Compiled);
        Regex localizationCall = new("LocalizationService\\.(?:Get|Format)\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        Regex codeAssignment = new("\\.(?:Text|Content|Header|Title)\\s*=\\s*\"([A-Za-z][^\"]{2,})\"", RegexOptions.Compiled);

        // XAML files (attribute literals) and C# files (catalog lookups and code-assigned literals).
        var sources = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Select(file => (File: file, Pattern: xamlAttribute))
            .Concat(Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .SelectMany(file => new[] { (File: file, Pattern: localizationCall), (File: file, Pattern: codeAssignment) }));

        List<string> offenders = new();

        foreach ((string file, Regex pattern) in sources)
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            bool csharp = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            string text = File.ReadAllText(file);
            foreach (Match match in pattern.Matches(text))
            {
                string literal = match.Groups[1].Value;

                if (csharp && IsConcatenatedFragment(text, match))
                {
                    continue;   // one piece of a message assembled with '+'; the whole text is the key
                }

                if (literal.Contains('{') || literal.Contains("&#x") || literal.Contains("&amp;"))
                {
                    continue;   // binding/format expression, glyph entity, escaped markup
                }

                // XAML escapes its markup and C# escapes its newlines; the catalog key holds the
                // real characters, so compare the decoded form.
                literal = System.Net.WebUtility.HtmlDecode(literal);
                if (csharp)
                {
                    literal = literal.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"");
                }

                if (!Prose.IsMatch(literal) || NotProse.Contains(literal))
                {
                    continue;   // units, numbers, bare acronyms
                }

                if (english.Contains(literal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)} -> \"{literal}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "A user-visible literal has no en-US catalog key, so it can never be translated. " +
            "Add the English string as a key in Localization/en-US.json (and the other catalogs):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct()));
    }

    // A literal glued to a neighbouring one with '+' is only a piece of the final text.
    private static bool IsConcatenatedFragment(string text, Match match)
    {
        int after = match.Index + match.Length;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after < text.Length && text[after] == '+') return true;

        int before = match.Index - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
        return before >= 0 && text[before] == '+';
    }

    private static Dictionary<string, string> ReadCatalog(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
        ?? throw new InvalidOperationException("Unreadable catalog: " + file);

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    // Resolved from this source file's compiled-in path, so an out-of-tree build directory
    // (--artifacts-path) cannot break the lookup the way a BaseDirectory walk does.
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + source);
    }
}
