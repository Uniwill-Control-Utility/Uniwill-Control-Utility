using System;
using System.IO;
using System.Text;

namespace EcRegistryGen
{
    /// <summary>
    /// Build-time entry point: <c>EcRegistryGen &lt;registry.json&gt; &lt;output.cs&gt;</c>.
    /// Rewrites the artifact only when its content would change, so the MSBuild
    /// Inputs/Outputs stamp stays quiet on no-op builds.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: EcRegistryGen <ec-registry.json> <output.cs>");
                return 2;
            }

            string registryPath = args[0];
            string outputPath = args[1];

            if (!File.Exists(registryPath))
            {
                Console.Error.WriteLine($"EcRegistryGen: registry not found: {registryPath}");
                return 2;
            }

            EcRegistryGenerator.Result result = EcRegistryGenerator.Generate(File.ReadAllText(registryPath));
            if (!result.Success)
            {
                Console.Error.WriteLine("EcRegistryGen: " + result.Error);
                return 1;
            }

            if (File.Exists(outputPath) && File.ReadAllText(outputPath) == result.Code)
                return 0;

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputPath, result.Code, new UTF8Encoding(false));
            Console.WriteLine("EcRegistryGen: wrote " + outputPath);
            return 0;
        }
    }
}
