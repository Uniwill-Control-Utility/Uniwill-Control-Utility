using Microsoft.Extensions.Hosting;

namespace UCU.Engine
{
    /// <summary>
    /// Entry point. Installed, the SCM starts this with no arguments and the host
    /// runs in service mode (<see cref="Microsoft.Extensions.Hosting.WindowsServices"/>);
    /// a developer runs <c>--console</c> to see the log on disk at a user-writable
    /// path and stop with Ctrl+C.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            bool isService = !args.Contains("--console", StringComparer.OrdinalIgnoreCase);

            using IHost host = EngineHostFactory.CreateHost(args, isService);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
    }
}
