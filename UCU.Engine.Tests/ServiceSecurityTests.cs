namespace UCU.Engine.Tests;

/// <summary>
/// The engine service's DACL is the contract the de-elevated product rests on: the app
/// runs asInvoker on purpose, and its ability to keep the hardware service alive is
/// decided by this descriptor, not by code.
///
/// The first shipped SDDL gave Authenticated Users <c>CCLCSWLOCRRC</c>, a mask chosen
/// by eyeballing SDDL letters. It omitted <c>RP</c>/SERVICE_START, so an installed app
/// could never start the service it owns (<c>ServiceController.Start</c> answered
/// "Cannot open 'UCUEngine' service on computer '.'. Access is denied") and silently
/// lost every EC-fed metric until the user opened services.msc - while granting
/// <c>LC</c>/SERVICE_CHANGE_CONFIG, which lets any authenticated user re-point binPath
/// and get code execution as LocalSystem. These assertions pin the two halves of the
/// contract: the right the app needs, and the rights nobody may have.
/// </summary>
public sealed class ServiceSecurityTests
{
    private const string Sddl = EngineInstaller.ServiceSddl;

    [Fact]
    public void AuthenticatedUsersCanStartTheService()
    {
        // The right that makes an unelevated app viable: EngineLifecycle.EnsureAsync
        // starts a stopped engine with no prompt. Without RP the app's whole
        // self-heal is an access-denied log line.
        Assert.Contains("RP", RightsOf("AU"));
        // ...and it can see the state it is repairing: query config + query status.
        Assert.Contains("CC", RightsOf("AU"));
        Assert.Contains("SW", RightsOf("AU"));
    }

    [Fact]
    public void AuthenticatedUsersGetNoControlRightsOverTheService()
    {
        HashSet<string> rights = RightsOf("AU");

        // SERVICE_CHANGE_CONFIG: re-point binPath / start type / dependencies, i.e.
        // arbitrary code as LocalSystem. Delete, write-DAC and write-owner are the
        // same class of power.
        Assert.DoesNotContain("LC", rights);
        Assert.DoesNotContain("DT", rights);
        Assert.DoesNotContain("SD", rights);
        Assert.DoesNotContain("WD", rights);
        Assert.DoesNotContain("WO", rights);
        Assert.DoesNotContain("FA", rights);
        Assert.DoesNotContain("GA", rights);

        // Stopping the service is administrator-only: the update shim and the
        // uninstall shim run requireAdministrator, and the app has no reason to
        // stop the hardware out from under another session.
        Assert.DoesNotContain("LO", rights);
    }

    [Fact]
    public void SystemAndAdministratorsKeepFullControl()
    {
        Assert.Contains("FA", RightsOf("SY"));
        Assert.Contains("FA", RightsOf("BA"));
    }

    [Fact]
    public void DescriptorIsProtectedFromInheritedAces()
    {
        // "D:P": without the protected flag, an inherited ace from the machine's
        // service security template can widen access to the descriptor nobody sees.
        Assert.StartsWith("D:P", Sddl);
    }

    /// <summary>The 2-letter rights of one access-ace trustee.</summary>
    private static HashSet<string> RightsOf(string trustee)
    {
        string suffix = ";;;" + trustee + ")";

        foreach (string ace in Sddl.Split("(A;;", StringSplitOptions.RemoveEmptyEntries))
        {
            int end = ace.IndexOf(suffix, StringComparison.Ordinal);
            if (end < 0)
                continue;

            string mask = ace[..end];
            Assert.Equal(0, mask.Length % 2);

            var rights = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < mask.Length; i += 2)
                rights.Add(mask.Substring(i, 2));

            return rights;
        }

        throw new InvalidOperationException($"No access ace for {trustee} in '{Sddl}'");
    }
}
