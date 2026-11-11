using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Uniwill_Control_Utility.Tests.Views;

/// <summary>
/// A structural XAML guard that the compiler does not provide.
///
/// An attribute line that ends a tag (`Text="..." />`) followed by another attribute line
/// leaves the trailing attribute as a TEXT NODE of the parent element. The XAML compiler
/// accepts that (it is legal XML), and the failure only appears at runtime, when the BAML
/// loader tries to add a String to a UIElementCollection:
///
///   System.ArgumentException: Cannot add instance of type 'String' to a collection of
///   type 'UIElementCollection'. Only items of type 'UIElement' are allowed.
///
/// That is exactly the exception a caption edit shipped once (0.9.14, the Settings page),
/// so it is scanned for here instead of being trusted to review.
/// </summary>
public sealed class XamlStructureTests
{
    private static readonly Regex SelfClosingAttributeLine =
        new(@"^\s*[A-Za-z][A-Za-z0-9.]*=""[^""]*""\s*/>\s*$", RegexOptions.Compiled);

    private static readonly Regex AttributeLine =
        new(@"^\s*[A-Za-z][A-Za-z0-9.]*=""", RegexOptions.Compiled);

    [Fact]
    public void NoXamlFileLeavesAnAttributeAsAStrayTextNode()
    {
        string root = RepositoryRoot();
        string app = Path.Combine(root, "Uniwill Control Utility");

        List<string> offenders = new();

        foreach (string file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (SelfClosingAttributeLine.IsMatch(lines[i]) && AttributeLine.IsMatch(lines[i + 1]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} -> '{lines[i].Trim()}' then '{lines[i + 1].Trim()}'");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "An attribute follows a closed tag, which the runtime BAML loader rejects as a String in a UIElementCollection:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
