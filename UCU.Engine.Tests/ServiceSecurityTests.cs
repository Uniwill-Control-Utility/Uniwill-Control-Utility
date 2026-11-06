namespace UCU.Engine.Tests;

/// <summary>
/// The engine service's DACL is the contract the de-elevated product rests on: the app
/// runs asInvoker on purpose, and its ability to keep the hardware service alive is
/// decided by this descriptor, not by code.
///
/// The first shipped SDDL gave Authenticated Users <c>CCLCSWLOCRRC</c>, a mask chosen
/// by eyeballing SDDL letters. It omitted <c>RP</c>/SERVICE_START, so an installed app
/// could never start the service it owns and silently lost every EC-fed metric until
/// the user opened services.msc - while granting <c>LO</c>/SERVICE_CHANGE_CONFIG (any
/// authenticated user re-points binPath and gets code execution as LocalSystem) and
/// <c>LC</c>/SERVICE_QUERY_INTERROGATE (the token-duplication elevation hole on a
/// LocalSystem service). These assertions pin the two halves of the contract: the
/// rights the app needs, and the rights nobody may have.
///
/// A second field-incident lesson lives here: .NET's <c>ServiceController</c> cannot
/// query a service under this descriptor. Its read-open requests
/// SERVICE_QUERY_ALL_ACCESS, which includes the interrogate and enumerate-dependents
/// rights the descriptor withholds, so every status query answered Access Denied and
/// the app's self-heal escalated to the elevated DACL-repair pass on every launch.
/// The app's SCM access is <c>ScmNative</c> (query-status and start only), which is
/// why no assertion here widens the mask to satisfy a .NET convenience type.
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
        // SW/SERVICE_QUERY_STATUS is what ScmNative.QueryState opens for; an app
        // that cannot read Running takes the elevated repair path on every launch.
        Assert.Contains("CC", RightsOf("AU"));
        Assert.Contains("SW", RightsOf("AU"));
    }

    [Fact]
    public void AuthenticatedUsersGetNoControlRightsOverTheService()
    {
        HashSet<string> rights = RightsOf("AU");

        // LO/SERVICE_CHANGE_CONFIG: re-point binPath / start type / dependencies,
        // i.e. arbitrary code as LocalSystem. LC/SERVICE_QUERY_INTERROGATE is the
        // same class of power by another route: on a LocalSystem service,
        // InterrogateService lets the caller open the service process's token.
        // Delete, write-DAC and write-owner are the same class as well.
        Assert.DoesNotContain("LO", rights);
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
        // (DT/SERVICE_STOP is asserted above; the app's SCM access is
        // query-status + start, nothing else.)
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

    [Fact]
    public void SddlReadBackRecognizesTheContractAndRejectsTheTemplate()
    {
        // The install pass verifies the descriptor it wrote actually committed
        // (the field incident: sc create's default SD lands in the registry after
        // sc.exe returns, silently reverting the pass's sdset - the app then
        // cannot open its own service and elevates every launch). The read-back
        // must accept exactly the contract ace and reject what create stamps.
        string contract = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;CCSWRPRC;;;AU)";
        string createTemplate =
            "D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;KR;;;AU)";

        Assert.True(EngineInstaller.SddlCarriesAppContract(contract));
        Assert.False(EngineInstaller.SddlCarriesAppContract(createTemplate));
        Assert.False(EngineInstaller.SddlCarriesAppContract(null));
        Assert.False(EngineInstaller.SddlCarriesAppContract("[SC] OpenService FAILED 5:"));

        // The shipped SDDL itself must satisfy the predicate, or the verification
        // loop can never converge.
        Assert.True(EngineInstaller.SddlCarriesAppContract(Sddl));
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
