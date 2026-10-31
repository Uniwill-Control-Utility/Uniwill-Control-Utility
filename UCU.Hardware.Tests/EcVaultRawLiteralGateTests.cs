using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UCU.Hardware.Tests;

/// <summary>
/// The U-109 gate: EC wiring lives in the registry
/// (UCU.Core\Ec\Registry\EcAddresses.json → UCU.Core\Ec\Generated\EcAddresses.g.cs),
/// so product code that touches the EC must not carry EC numbers of its own.
///
/// The rule is deliberately mechanical, in the spirit of the AGENTS.md ban this port
/// imports: in the EC-facing sources, after comments and string literals are masked out,
/// (a) no hex literal may land inside the EC register address window 0x0100-0x0FFF,
/// (b) the driver's IOCTL codes may appear nowhere but the registry, and
/// (c) a byte/ushort const may not be given a hex value - its value comes from the vault.
///
/// Masking comments is what keeps this a gate about code, not prose: the diagnostic copy
/// and the doc comments name registers in hex on purpose (they are the operator's evidence
/// trail), and byte-splitting masks (0xFF) and zero writes stay legal because they are not
/// EC addresses. 0x07A6, the battery charge-mode register the story names, gets its own pin.
/// </summary>
public sealed class EcVaultRawLiteralGateTests
{
    private static readonly string[] EngineFiles =
    {
        @"UCU.Hardware\Ec\EcEngine.cs",
        @"UCU.Hardware\Ec\EcDriverTransport.cs",
        @"UCU.Engine\EcEngineGate.cs",
        @"UCU.Engine\EcRawWriteGuard.cs",
    };

    private static readonly string[] CodecFiles =
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), @"UCU.Core\Ec"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"Ec\Generated", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepositoryRoot(), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private const ushort EcWindowLow = 0x0100;
    private const ushort EcWindowHigh = 0x0FFF;

    [Fact]
    public void TheGateScansTheWholeEcFacingSurface()
    {
        // A new EC-facing source must be added to the scan, not slip past it.
        Assert.Contains(@"UCU.Core\Ec\EcWriteGatePolicy.cs", CodecFiles);
        Assert.Contains(@"UCU.Core\Ec\EcWordCodec.cs", CodecFiles);
        Assert.Contains(@"UCU.Core\Ec\EcFanRegisters.cs", CodecFiles);
        Assert.True(CodecFiles.Length >= 12, $"expected the codec set, found {CodecFiles.Length} files");
    }

    [Fact]
    public void NoProductCodeDeclaresAnEcAddressLiteral()
    {
        List<string> offences = new();

        foreach (string file in EngineFiles.Concat(CodecFiles))
        {
            string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), file));

            for (int i = 0; i < lines.Length; i++)
            {
                string code = MaskCommentsAndStrings(lines[i]);
                foreach (Match match in Regex.Matches(code, "0x[0-9A-Fa-f]+"))
                {
                    int value = Convert.ToInt32(match.Value.Substring(2), 16);
                    if (value is >= EcWindowLow and <= EcWindowHigh)
                        offences.Add($"{file}:{i + 1}: EC address literal {match.Value}");
                }
            }
        }

        Assert.True(offences.Count == 0, "EC addresses belong in the registry: " + string.Join(" | ", offences));
    }

    [Fact]
    public void NoProductCodeDeclaresTheEcDriverIoctlCodes()
    {
        // Declared once in the registry (driver section) and read from the vault.
        List<string> offences = new();

        foreach (string file in EngineFiles.Concat(CodecFiles))
        {
            string text = File.ReadAllText(Path.Combine(RepositoryRoot(), file));
            string code = string.Join("\n", text.Split('\n').Select(MaskCommentsAndStrings));

            foreach (string ioctl in new[] { "0x9C40A488", "0x9C40A48C" })
            {
                if (code.Contains(ioctl, StringComparison.OrdinalIgnoreCase))
                    offences.Add($"{file}: {ioctl}");
            }
        }

        Assert.True(offences.Count == 0, "the IOCTL codes live in the registry: " + string.Join(" | ", offences));
    }

    [Fact]
    public void NoByteOrUshortConstIsGivenARawHexValue()
    {
        List<string> offences = new();
        Regex hexConst = new(@"\bconst\s+(?:ushort|byte)\s+[A-Za-z0-9_]+\s*=\s*0x[0-9A-Fa-f]+", RegexOptions.Compiled);

        foreach (string file in EngineFiles.Concat(CodecFiles))
        {
            string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), file));

            for (int i = 0; i < lines.Length; i++)
            {
                Match offence = hexConst.Match(MaskCommentsAndStrings(lines[i]));
                if (offence.Success)
                    offences.Add($"{file}:{i + 1}: {offence.Value}");
            }
        }

        Assert.True(
            offences.Count == 0,
            "a byte/ushort const takes its value from the vault (EcVault.*): " + string.Join(" | ", offences));
    }

    [Fact]
    public void TheBatteryModeRegisterIsNeverAHardcodedNumberInEcEngine()
    {
        // The story's pin: 0x07A6 is reached through the vault, never written out.
        string engine = Read(@"UCU.Hardware\Ec\EcEngine.cs");
        string code = string.Join("\n", engine.Split('\n').Select(MaskCommentsAndStrings));

        Assert.DoesNotContain("0x07A6", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REG_BATTERY_MODE = EcVault.REG_BATTERY_MODE", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void EcEngineReadsItsRegisterMapFromTheGeneratedVault()
    {
        string engine = Read(@"UCU.Hardware\Ec\EcEngine.cs");

        Assert.Contains("using UCU.Core.Ec.Generated;", engine, StringComparison.Ordinal);
        Assert.Contains("public const ushort REG_MANUAL_FAN_CTRL = EcVault.REG_MANUAL_FAN_CTRL;", engine, StringComparison.Ordinal);
        Assert.Contains("public const ushort REG_BATTERY_MODE = EcVault.REG_BATTERY_MODE;", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedArtifactIsMarkedAutoGeneratedAndNotHandWritten()
    {
        string artifact = Read(@"UCU.Core\Ec\Generated\EcAddresses.g.cs");

        Assert.StartsWith("// <auto-generated />", artifact, StringComparison.Ordinal);
        Assert.Contains("do not edit this file, edit the registry", artifact, StringComparison.Ordinal);
    }

    /// <summary>Removes line comments and quoted spans so the gate inspects code only.</summary>
    private static string MaskCommentsAndStrings(string line)
    {
        string text = line.TrimStart();

        int comment = IndexOutsideQuotes(text, "//");
        if (comment >= 0)
            text = text.Substring(0, comment);

        var builder = new System.Text.StringBuilder(text.Length);
        bool inString = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                inString = !inString;
                builder.Append(' ');
                continue;
            }

            builder.Append(inString ? ' ' : c);
        }

        return builder.ToString();
    }

    private static int IndexOutsideQuotes(string text, string needle)
    {
        bool inString = false;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '"')
                inString = !inString;
            else if (!inString && text[i] == needle[0] && text[i + 1] == needle[1])
                return i;
        }

        return -1;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "UCU.Hardware")) &&
                Directory.Exists(Path.Combine(directory.FullName, "UCU.Core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with the EC sources not found.");
    }
}
