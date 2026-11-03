// msi-edit: read/patch the packed UCU MSI so its Control Panel story is ONE
// entry that runs the complete-sweep shim:
//   1. ARPSYSTEMCOMPONENT=1  ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the MSI never creates its own GUID-keyed
//      Add/Remove Programs entry (so no thin `msiexec /x` route exists in
//      Control Panel at all),
//   2. Registry rows publishing our own ARP entry (non-GUID key
//      `Uniwill-Control-Utility`, UninstallString = Uninstall.exe) ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the
//      Control Panel shell-executes the shim; a non-GUID key carrying no
//      WindowsInstaller value is never routed by product code.
//
// Compiled by build\pack.ps1 with csc.exe + a TlbImp interop of msi.dll
// (the IDatabase interface is vtable-only: no late-bound IDispatch route
// exists, which is why this is a compiled tool, not a PowerShell script).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WindowsInstaller;

static class MsiEdit
{
    static Installer _installer;

    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "inspect";
        string msi = args.Length > 1 ? args[1] : "";
        string uninstallTarget = args.Length > 2 && !string.IsNullOrEmpty(args[2])
            ? args[2]
            : "\"[INSTALLFOLDER]Uninstall.exe\"";
        string arpKey = args.Length > 3 ? args[3] : "";

        if (string.IsNullOrEmpty(msi))
        {
            Console.Error.WriteLine("usage: msi-edit inspect|suppress|addca|export <msi> [uninstallTarget [arpKey]]  (export: 3rd = table, 4th = folder)");
            Environment.Exit(2);
        }

        _installer = (Installer)Activator.CreateInstance(
            Type.GetTypeFromProgID("WindowsInstaller.Installer"));

        bool write = mode == "suppress" || mode == "addca";
        var db = _installer.OpenDatabase(msi, write ? 1 : 0); // 1 = transact, 0 = read-only
        try
        {
            if (mode == "inspect")
            {
                Inspect(db);
            }
            else if (mode == "export")
            {
                // db.Export(table, folder, file): MSI's own ISTG text form of
                // a table (column-name / type / key lines + tab-separated
                // rows). Ground truth for any authoring discussion; U-111's
                // service-table plan was retired with the evidence recorded
                // in pack.ps1.
                if (args.Length < 4) throw new Exception("export mode needs table and folder");
                db.Export(args[2], args[3], args[2] + ".idt");
                Console.WriteLine("exported " + args[2]);
            }
            else if (mode == "suppress")
            {
                Suppress(db, uninstallTarget, arpKey);
                db.Commit();
                Console.WriteLine("COMMITTED");
            }
            else if (mode == "addca")
            {
                AddServiceCustomActions(db);
                AddCloseRunningAppCustomActions(db);
                db.Commit();
                Console.WriteLine("COMMITTED");
            }
            else
            {
                Console.Error.WriteLine("unknown mode: " + mode);
                Environment.Exit(2);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("MSI-EDIT ERROR (" + mode + "): " + e.Message);
            Environment.Exit(1);
        }
        finally
        {
            if (db != null) Marshal.ReleaseComObject(db);
            if (_installer != null) Marshal.ReleaseComObject(_installer);
        }
    }

    // ---------- read side ----------
    static void Inspect(Database db)
    {
        Section("Tables");
        foreach (var r in Query(db, "SELECT `Name` FROM `_Tables`"))
            Console.WriteLine("  " + Join(r));

        Section("Key Properties");
        foreach (var p in new[] { "ProductCode", "ProductVersion", "UpgradeCode", "Manufacturer",
                                  "ProductName", "ARPSYSTEMCOMPONENT", "ALLUSERS", "MSIINSTALLPERUSER" })
        {
            var rows = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = '" + p + "'");
            Console.WriteLine("  " + p + " = " + (rows.Count > 0 ? rows[0][0] : "(absent)"));
        }

        Section("Directories");
        foreach (var r in Query(db, "SELECT * FROM `Directory`"))
            Console.WriteLine("  " + Join(r));

        Section("Components");
        foreach (var r in Query(db, "SELECT `Component`, `KeyPath`, `Directory_` FROM `Component`"))
            Console.WriteLine("  " + Join(r));

        Section("File rows (exe only)");
        foreach (var r in Query(db, "SELECT `Component_`, `FileName` FROM `File`"))
            if (r[1].IndexOf("exe", StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine("  " + Join(r));

        Section("Schema: Directory / Registry / Feature / FeatureComponents / File");
        foreach (var t in new[] { "Directory", "Registry", "Feature", "FeatureComponents", "File" })
        {
            foreach (var r in Query(db, "SELECT * FROM `_Columns` WHERE `Table` = '" + t + "'"))
                Console.WriteLine("  " + Join(r));
        }

        Section("Features");
        foreach (var r in Query(db, "SELECT * FROM `FeatureComponents`"))
            Console.WriteLine("  " + Join(r));

        Section("Registry rows (existing)");
        DumpRows(db, "SELECT * FROM `Registry`");

        Section("CustomAction rows (existing)");
        DumpRows(db, "SELECT * FROM `CustomAction`");

        Section("InstallExecuteSequence");
        foreach (var r in Query(db, "SELECT * FROM `InstallExecuteSequence`"))
            Console.WriteLine("  " + Join(r));
    }

    static void DumpRows(Database db, string sql)
    {
        var rows = Query(db, sql);
        if (rows.Count == 0) { Console.WriteLine("  (none)"); return; }
        foreach (var r in rows) Console.WriteLine("  " + Join(r));
    }

    static void Section(string title) { Console.WriteLine("=== " + title + " ==="); }
    static string Join(List<string> r) { return string.Join(" | ", r); }

    static List<List<string>> Query(Database db, string sql)
    {
        var result = new List<List<string>>();
        // MSI's SQL failure is terse ("OpenView,Sql"); name the statement, so
        // a pack-time failure is diagnosable from the log alone.
        View view;
        try { view = db.OpenView(sql); }
        catch (Exception e) { throw new Exception("SELECT rejected [" + sql + "]: " + e.Message, e); }
        try
        {
            view.Execute(null);
            while (true)
            {
                Record rec = view.Fetch();
                if (rec == null) break;
                var row = new List<string>();
                int count = rec.FieldCount;
                for (int i = 1; i <= count; i++) row.Add(rec.get_StringData(i) ?? "");
                result.Add(row);
                Marshal.ReleaseComObject(rec);
            }
        }
        finally
        {
            view.Close();
            Marshal.ReleaseComObject(view);
        }
        return result;
    }

    // ---------- write side ----------
    //
    // The packed Velopack MSI already publishes its ONE Control Panel entry
    // itself (ARPSYSTEMCOMPONENT=1 suppresses the Windows Installer engine's
    // GUID-keyed entry ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the one carrying WindowsInstaller=1, the entry that
    // routes by product code to the thin `msiexec /x`), and the entry it
    // publishes is a Registry-table component under
    //   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:Uniwill-Control-Utility
    // with UninstallString = "msiexec.exe /x [ProductCode]": also thin. The
    // engine service is registered by the app, not the MSI, so the thin route
    // leaves the service, the layout and the entry behind.
    //
    // This patch rebinds those rows at pack time ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the values are authored
    // into the package, so no runtime writer touches the registry and no AV
    // anti-hijack enforcer class applies:
    //   UninstallString / QuietUninstallString -> "[INSTALLFOLDER]Uninstall.exe"
    // The shim is requireAdministrator and performs the complete sweep (app
    // kill, service stop+delete, tasks, MSI /x, ARP entries, install root).
    // The rows belong to component `ArpRegistration` whose KeyPath is the
    // uninstall subkey, so Windows Installer creates the entry on install and
    // deletes the whole key on removal: husk-free by construction.
    static void Suppress(Database db, string uninstallTarget, string arpKey)
    {
        // Confirm/force the display suppression of the engine's own entry.
        var arp = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'ARPSYSTEMCOMPONENT'");
        if (arp.Count == 0 || arp[0][0] != "1")
        {
            SetProp(db, "ARPSYSTEMCOMPONENT", "1");
            Console.WriteLine("Property ARPSYSTEMCOMPONENT set to 1");
        }
        else
        {
            Console.WriteLine("Property ARPSYSTEMCOMPONENT already 1 (engine ARP entry suppressed)");
        }

        // Discover the published ARP key when not given: the key of any
        // Registry row under the Uninstall tree carrying our DisplayName.
        var keyRows = Query(db,
            "SELECT `Key` FROM `Registry` WHERE `Name` = 'DisplayName' AND `Value` = 'Uniwill Control Utility'");
        if (keyRows.Count == 0)
            throw new Exception("no published ARP DisplayName row found: template drift, refusing to patch blindly");
        string key = keyRows[0][0];
        if (!string.IsNullOrEmpty(arpKey)) key = arpKey;

        // Name the published key with the documented Control Panel MSI
        // convention: `MSI:{product-code}`, the REAL code (the Control Panel
        // routes Uninstall subkeys named "MSI:<x>" to the Windows Installer
        // APIs using the suffix as the product code, and an ARP anti-hijack
        // enforcer re-adds WindowsInstaller=1 - the value that selects that
        // route - to any plain key on guarded boxes, so the name cannot be
        // avoided, it must satisfy the rule. Field-proven through both
        // failures of the alternatives:
        //   vpk's `MSI:Uniwill-Control-Utility` (2026-10-28) and the renamed
        // plain `Uniwill-Control-Utility` (2026-10-29, enforcer-forced route)
        // both made the code lookup use a NON-code, producing ERROR_UNKNOWN_
        // PRODUCT and "An error occurred while trying to uninstall ... It
        // may have already been uninstalled. Would you like to remove ...
        // from the Programs and Features list?" - Yes deletes the entry and
        // leaves the product installed. With name = real code BOTH routes
        // complete: the shell route (no WindowsInstaller value, clean boxes)
        // ignores the key name and shell-executes UninstallString verbatim -
        // the requireAdministrator sweep shim, UAC consent included; the
        // product-code route (WindowsInstaller re-added by the enforcer)
        // resolves the registered code, runs the genuine MSI uninstall, and
        // its vanishing registration trips the sentinel's SYSTEM sweep verb.
        var codeRows = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'");
        if (codeRows.Count == 0)
            throw new Exception("ProductCode property missing: template drift, refusing to patch blindly");
        string code = codeRows[0][0].Trim();
        int lastSep = key.LastIndexOf('\\');
        string leaf = key.Substring(lastSep + 1);
        string targetLeaf = "MSI:" + code;
        if (!string.Equals(leaf, targetLeaf, StringComparison.OrdinalIgnoreCase))
        {
            string target = key.Substring(0, lastSep + 1) + targetLeaf;
            Execute(db, "UPDATE `Registry` SET `Key` = '" + target + "' WHERE `Key` = '" + key + "'");
            Console.WriteLine("renamed published ARP key " + leaf + " -> " + targetLeaf +
                " (MSI: + REAL product code: the product-code route resolves, the shell route ignores the name)");
            key = target;
        }

        // Rebind the uninstall commands (idempotent: UPDATE to the same value
        // is a no-op, so repacking an already-stamped MSI is safe).
        Execute(db, "UPDATE `Registry` SET `Value` = '" + uninstallTarget + "' WHERE `Key` = '" + key +
                    "' AND `Name` = 'UninstallString'");
        Execute(db, "UPDATE `Registry` SET `Value` = '" + uninstallTarget + "' WHERE `Key` = '" + key +
                    "' AND `Name` = 'QuietUninstallString'");
        Console.WriteLine("ARP key " + key + ": UninstallString/QuietUninstallString -> " + uninstallTarget);

        // The product code INSIDE the ARP entry. Guarded machines (field-
        // proven: Emsisoft box) hide the Windows Installer registration tree
        // from in-process readers ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â MsiEnumProductsW enumerates every other
        // product on the machine and never ours, elevated included ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â while
        // the component-published ARP entry stays visible (Control Panel
        // reads it). The shim and the sentinel therefore take the code from
        // this row: [ProductCode] expands at install time, the row is
        // deleted with the product by Windows Installer. appwiz.cpl ignores
        // unknown ARP values; NoModify/NoRepair hide Modify/Repair.
        Execute(db, "DELETE FROM `Registry` WHERE `Key` = '" + key + "' AND `Name` = 'UCUProductCode'");
        Execute(db, "INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) " +
                    "VALUES ('ARPUCCode', 2, '" + key + "', 'UCUProductCode', '[ProductCode]', 'ArpRegistration')");
        Console.WriteLine("ARP row UCUProductCode = [ProductCode] (the code inside the visible entry)");

        // Ensure the metadata rows the Control Panel surfaces. Component_ =
        // ArpRegistration: the same component gates the published rows.
        var publisher = Query(db, "SELECT `Value` FROM `Registry` WHERE `Key` = '" + key + "' AND `Name` = 'Publisher'");
        string manufacturer = publisher.Count > 0 ? publisher[0][0] : "Uniwill-Control-Utility";
        var extra = new List<string[]>
        {
            new[] { "Publisher", manufacturer },
            new[] { "HelpLink", "https://github.com/jgudec/Uniwill-Control-Utility" },
            new[] { "URLInfoAbout", "https://github.com/jgudec/Uniwill-Control-Utility" },
            new[] { "URLUpdateInfo", "https://github.com/jgudec/Uniwill-Control-Utility/releases" },
        };

        var insert = "INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) " +
                     "VALUES ('ARPUCHelp', 2, '" + key + "', 'HelpLink', 'https://github.com/jgudec/Uniwill-Control-Utility', 'ArpRegistration')";
        var urlAbout = "INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) " +
                       "VALUES ('ARPUCAbout', 2, '" + key + "', 'URLInfoAbout', 'https://github.com/jgudec/Uniwill-Control-Utility', 'ArpRegistration')";
        var urlUpdate = "INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) " +
                        "VALUES ('ARPUCUpdate', 2, '" + key + "', 'URLUpdateInfo', 'https://github.com/jgudec/Uniwill-Control-Utility/releases', 'ArpRegistration')";
        foreach (var sql in new[] { insert, urlAbout, urlUpdate })
        {
            try { Execute(db, sql); }
            catch (Exception e) { Console.WriteLine("  (metadata row present, skipped: " + e.Message + ")"); }
        }
        Console.WriteLine("metadata rows ensured (Publisher/HelpLink/URLInfoAbout/URLUpdateInfo)");
    }

    /// <summary>
    /// U-111b: author the deferred exe-CustomActions that register the engine
    /// service inside the install, so a fresh machine pays ZERO UAC at first
    /// app launch (the elevation is spent in the MSI, the XMG CC mechanism).
    ///
    /// Pure DML on the existing CustomAction + InstallExecuteSequence tables
    /// (MSI's SQL has no DDL, and no new tables are needed for exe CAs).
    /// Encoding pinned from the SDK and field-proven on this package 2026-10-31
    /// (full /i -> /x -> /i -> /x cycle, exit 0 at every pass):
    ///   3090 = 18 (EXE installed with the package; Source = File-table key)
    ///        + 1024 (InScript/deferred) + 2048 (NoImpersonate = runs as SYSTEM)
    /// The Source is the engine exe's File row under current\Service\ -
    /// resolved at pack time (File keys are content-derived: every pack
    /// re-keys them), never hardcoded. Targets are the engine's own verbs:
    /// --install-engine registers (it is script-safe: no file deletion), and
    /// --remove-service removes ONLY the SCM object. The sweep verb
    /// --uninstall-engine must NEVER appear here: it runs nested msiexec /x
    /// passes and destroys the layout the script is unwinding (1603 cascade,
    /// field-proven the hard way).
    ///
    /// SEQUENCING is load-bearing, and the field taught it (pack #7
    /// 2026-10-31: Control Panel "already been uninstalled" regression): a
    /// deferred CA sequenced at 4099 executes INSIDE InstallFinalize's commit
    /// script, interleaved after InstallFiles' FileCopy ops but BEFORE the
    /// Registry-table ARP rows are written (verbose log: registry ops
    /// 26 ms after the belt ran) and before the SCM commits the service's
    /// security descriptor (every sc sdset lands in the create-commit window
    /// and the transaction restores the create template at finalize). So:
    ///   - Install side: deferred CA at 6000 with its rollback pair at 5999.
    ///     A deferred CA may only schedule between InstallInitialize and
    ///     InstallFinalize (proven: 6601 -> error 2762), and the script runs
    ///     queued deferred CAs concurrently with the remaining component ops,
    ///     so 4099 launched the belt BEFORE the ARP registry rows were
    ///     written. 6000 launches after the registry batch and RegisterProduct
    ///     (files, registry, ARP entries all on disk/registry by then): the
    ///     belt places the root shim and applies the WindowsInstaller redirect
    ///     IN-SCRIPT, and the SCM create-commit restore (field-proven six
    ///     seconds after sc create inside a script) lands while the
    ///     twenty-attempt sdset convergence loop is still running. No mid-
    ///     script rollback gap: 5999 (3346 = 3090 + 256 Rollback,
    ///     --remove-service) unwinds the service if anything after it fails
    ///     (field-proven: the rollback row fires cleanly, registration
    ///     self-cleans, no ghost).
    ///   - Remove side: 3498 - before RemoveFiles (3500: the CA launches the
    ///     installed image, so files must still exist) and before Velopack's
    ///     UninstallHookDeferred (3499, whose sweep verb then sees the
    ///     service gone and tolerates 1060). Condition REMOVE="ALL".
    /// </summary>
    static void AddServiceCustomActions(Database db)
    {
        // Resolve the engine exe's File key in current\Service\: File ->
        // Component_ -> Directory_, then walk the Directory parent chain
        // (Service under current). Fail closed on any template drift.
        var dirRows = Query(db, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`");
        var dirs = new Dictionary<string, string[]>();
        foreach (var r in dirRows) dirs[r[0]] = new[] { r[1], LongName(r[2]) };
        string serviceDir = null;
        foreach (var kv in dirs)
            if (kv.Value[1] == "Service" && dirs.ContainsKey(kv.Value[0]) && dirs[kv.Value[0]][1] == "current")
                serviceDir = kv.Key;
        if (serviceDir == null)
            throw new Exception("no 'Service' directory under 'current' found: template drift, refusing to author CAs");

        // MSI SQL on this OS rejects LIKE with any quoting we probed
        // (OpenView,Sql), so filter the File rows in C#: the long name
        // (after the '|') must be the engine exe.
        var fileRows = new List<List<string>>();
        foreach (var r in Query(db, "SELECT `File`, `Component_`, `FileName` FROM `File`"))
            if (string.Equals(LongName(r[2]), "Uniwill Control Utility Engine.exe", StringComparison.OrdinalIgnoreCase))
                fileRows.Add(new List<string> { r[0], r[1] });
        var compToDir = new Dictionary<string, string>();
        foreach (var r in Query(db, "SELECT `Component`, `Directory_` FROM `Component`"))
            compToDir[r[0]] = r[1];
        var engineFiles = new List<string>();
        foreach (var r in fileRows)
        {
            string d = compToDir.ContainsKey(r[1]) ? compToDir[r[1]] : null;
            if (d == serviceDir) engineFiles.Add(r[0]);
        }
        if (engineFiles.Count != 1)
            throw new Exception("expected exactly one 'Uniwill Control Utility Engine.exe' File row in current\\Service\\, found " +
                                engineFiles.Count + ": template drift, refusing to author CAs");
        string fk = engineFiles[0];

        // Sequence anchors (absent rows mean the standard template defaults).
        // The install CA (6000) and its rollback pair (5999) must sit BELOW
        // InstallFinalize (deferred CAs cannot schedule past it: 2762), and
        // the remove CA (3498) below RemoveFiles (the CA launches the
        // installed image).
        int installFinalize = SeqOf(db, "InstallFinalize", 6600);
        int removeFiles = SeqOf(db, "RemoveFiles", 3500);
        if (installFinalize <= 6000)
            throw new Exception("InstallFinalize is sequenced at " + installFinalize +
                                ": the install CA (6000) must run before it and after the registry batch");
        if (removeFiles <= 3498)
            throw new Exception("RemoveFiles is sequenced at " + removeFiles + ": the remove CA (3498) must run before it");

        foreach (var act in new[] { "UcuEngineInstallDeferred", "UcuEngineInstallRollback", "UcuEngineRemoveDeferred" })
        {
            Execute(db, "DELETE FROM `CustomAction` WHERE `Action` = '" + act + "'");
            Execute(db, "DELETE FROM `InstallExecuteSequence` WHERE `Action` = '" + act + "'");
        }

        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuEngineInstallDeferred', 3090, '" + fk + "', '--install-engine')");
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuEngineInstallRollback', 3346, '" + fk + "', '--remove-service')");
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuEngineRemoveDeferred', 3090, '" + fk + "', '--remove-service')");

        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuEngineInstallRollback', 'NOT REMOVE', 5999)");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuEngineInstallDeferred', 'NOT REMOVE', 6000)");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuEngineRemoveDeferred', 'REMOVE=\"ALL\"', 3498)");

        Console.WriteLine("engine service CustomActions authored (File key " + fk +
                          "): 3090 --install-engine @6000, 3346 --remove-service rollback @5999, " +
                          "3090 --remove-service @3498 (InstallFinalize=" + installFinalize + ", RemoveFiles=" + removeFiles + ")");
    }

    /// <summary>
    /// Force-close the running app (and stop the engine service) BEFORE
    /// InstallValidate, so the installer never finds its own payload locked.
    ///
    /// Field 2026-11-02 (tester laptop): the MSI was run while UCU was up.
    /// Windows cannot replace a running image, so the install "succeeded" with
    /// the OLD exe still on disk and the new DLLs beside it - a mixed build that
    /// then failed every preset apply with MissingMethodException. The app now
    /// detects that state and refuses to start, but the install has to be
    /// correct in the first place.
    ///
    /// Type 50 runs an executable whose path comes from a property (Source =
    /// property name, Target = command line), so no helper binary and no Binary
    /// table travel with the package: taskkill.exe and sc.exe are resolved from
    /// [SystemFolder] by a type-51 SetProperty pair. Both run in the execute
    /// sequence as an elevated per-machine install, which is what lets taskkill
    /// end the user's app and sc stop the LocalSystem engine.
    /// </summary>
    static void AddCloseRunningAppCustomActions(Database db)
    {
        int installValidate = SeqOf(db, "InstallValidate", 1400);

        // Sequence the pair below InstallValidate but above CostFinalize (1000):
        // the in-use detection that decides to schedule a reboot happens at
        // InstallValidate, so the app must be gone before it runs.
        int setKill = Math.Min(installValidate - 4, 1350);
        int setSc = setKill + 1;
        // Engine FIRST: its AppSupervision relaunches a killed app, so killing the
        // app before the service is down just brings it back (and the install can
        // meet a freshly locked exe). Stopping the service ends the supervision.
        int stopEngine = setKill + 2;
        int killEngine = setKill + 3;
        int closeApp = setKill + 4;
        if (setKill <= 1000)
            throw new Exception("InstallValidate is sequenced at " + installValidate +
                                ": no room to close the app before it");

        foreach (var act in new[] { "UcuSetTaskkillPath", "UcuSetScPath", "UcuCloseApp", "UcuStopEngine", "UcuKillEngine" })
        {
            Execute(db, "DELETE FROM `CustomAction` WHERE `Action` = '" + act + "'");
            Execute(db, "DELETE FROM `InstallExecuteSequence` WHERE `Action` = '" + act + "'");
        }

        // Type 51: a property set to a formatted value ([SystemFolder] resolves
        // at run time, so no path is baked into the package).
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuSetTaskkillPath', 51, 'UcuTaskkillExe', '[SystemFolder]taskkill.exe')");
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuSetScPath', 51, 'UcuScExe', '[SystemFolder]sc.exe')");

        // Type 50 + 64 (continue): exe from the property named in Source, arguments
        // in Target, and a non-zero exit does NOT abort the install - the fresh-
        // install case for both commands (no UCUEngine service yet -> sc 1060;
        // nothing to kill -> taskkill 128). Field-proven: without the flag a first
        // install died with error 1722 at UcuStopEngine.
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuCloseApp', 114, 'UcuTaskkillExe', '/f /im \"Uniwill Control Utility.exe\" /t')");
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuStopEngine', 114, 'UcuScExe', 'stop UCUEngine')");
        Execute(db, "INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) " +
                    "VALUES ('UcuKillEngine', 114, 'UcuTaskkillExe', '/f /im \"Uniwill Control Utility Engine.exe\"')");

        // Install and upgrade only: an uninstall runs the shim, which stops both
        // itself.
        const string condition = "NOT (REMOVE=\"ALL\")";
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuSetTaskkillPath', '" + condition + "', " + setKill + ")");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuSetScPath', '" + condition + "', " + setSc + ")");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuKillEngine', '" + condition + "', " + killEngine + ")");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuCloseApp', '" + condition + "', " + closeApp + ")");
        Execute(db, "INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) " +
                    "VALUES ('UcuStopEngine', '" + condition + "', " + stopEngine + ")");

        Console.WriteLine("close-before-install CustomActions authored: taskkill + sc stop at " +
                          setKill + "-" + stopEngine + " (InstallValidate=" + installValidate + ")");
    }

    static int SeqOf(Database db, string action, int standardDefault)
    {
        var rows = Query(db, "SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = '" + action + "'");
        return rows.Count == 0 ? standardDefault : int.Parse(rows[0][0]);
    }

    static string LongName(string shortLongOrLong)
    {
        int bar = shortLongOrLong.IndexOf('|');
        return bar >= 0 ? shortLongOrLong.Substring(bar + 1) : shortLongOrLong;
    }

    static void SetProp(Database db, string name, string value)
    {
        var existing = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = '" + name + "'");
        if (existing.Count > 0)
            Execute(db, "DELETE FROM `Property` WHERE `Property` = '" + name + "'");
        var view = db.OpenView("INSERT INTO `Property` (`Property`, `Value`) VALUES (?, ?)");
        try
        {
            var rec = _installer.CreateRecord(2);
            rec.set_StringData(1, name);
            rec.set_StringData(2, value);
            view.Modify(MsiViewModify.msiViewModifyInsert, rec);
            Marshal.ReleaseComObject(rec);
        }
        finally
        {
            view.Close();
            Marshal.ReleaseComObject(view);
        }
    }

    static void Execute(Database db, string sql)
    {
        View view;
        try { view = db.OpenView(sql); }
        catch (Exception e) { throw new Exception("SQL rejected [" + sql + "]: " + e.Message, e); }
        try { view.Execute(null); }
        finally { view.Close(); Marshal.ReleaseComObject(view); }
    }
}