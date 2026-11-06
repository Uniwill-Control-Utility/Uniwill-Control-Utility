using Microsoft.Extensions.Hosting;

namespace UCU.Engine
{
    /// <summary>
    /// Entry point. Installed, the SCM starts this with no arguments and the host
    /// runs in service mode (<see cref="Microsoft.Extensions.Hosting.WindowsServices"/>);
    /// a developer runs <c>--console</c> to see the log on disk at a user-writable
    /// path and stop with Ctrl+C. <c>--install-engine</c> / <c>--uninstall-engine</c>
    /// run the SCM lifecycle pass (elevated) instead of the service host;
    /// <c>--remove-service</c> removes only the SCM registration (the MSI's
    /// remove-side CustomAction — the sweep verb must never run in-script).
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Contains("--install-engine", StringComparer.OrdinalIgnoreCase))
                return await EngineInstaller.InstallAsync().ConfigureAwait(false);

            if (args.Contains("--uninstall-engine", StringComparer.OrdinalIgnoreCase))
                return await EngineInstaller.UninstallAsync().ConfigureAwait(false);

            // MSI remove-side CustomAction verb: service-only removal, safe to
            // run inside an uninstall script (the sweep verb above is not).
            if (args.Contains("--remove-service", StringComparer.OrdinalIgnoreCase))
                return await EngineInstaller.RemoveServiceAsync().ConfigureAwait(false);

            bool isService = !args.Contains("--console", StringComparer.OrdinalIgnoreCase);

            using IHost host = EngineHostFactory.CreateHost(args, isService);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
    }
}
