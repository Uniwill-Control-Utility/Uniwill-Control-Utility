// msi-edit: read/patch the packed UCU MSI so its Control Panel story is ONE
// entry that runs the complete-sweep shim:
//   1. ARPSYSTEMCOMPONENT=1  — the MSI never creates its own GUID-keyed
//      Add/Remove Programs entry (so no thin `msiexec /x` route exists in
//      Control Panel at all),
//   2. Registry rows publishing our own ARP entry (non-GUID key
//      `Uniwill-Control-Utility`, UninstallString = Uninstall.exe) — the
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
            Console.Error.WriteLine("usage: msi-edit inspect|suppress <msi> [uninstallTarget [arpKey]]");
            Environment.Exit(2);
        }

        _installer = (Installer)Activator.CreateInstance(
            Type.GetTypeFromProgID("WindowsInstaller.Installer"));

        bool write = mode == "suppress";
        var db = _installer.OpenDatabase(msi, write ? 1 : 0); // 1 = transact, 0 = read-only
        try
        {
            if (mode == "inspect")
            {
                Inspect(db);
            }
            else if (mode == "suppress")
            {
                Suppress(db, uninstallTarget, arpKey);
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
        View view = db.OpenView(sql);
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
    // GUID-keyed entry — the one carrying WindowsInstaller=1, the entry that
    // routes by product code to the thin `msiexec /x`), and the entry it
    // publishes is a Registry-table component under
    //   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:Uniwill-Control-Utility
    // with UninstallString = "msiexec.exe /x [ProductCode]": also thin. The
    // engine service is registered by the app, not the MSI, so the thin route
    // leaves the service, the layout and the entry behind.
    //
    // This patch rebinds those rows at pack time — the values are authored
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

        // Rebind the uninstall commands (idempotent: UPDATE to the same value
        // is a no-op, so repacking an already-stamped MSI is safe).
        Execute(db, "UPDATE `Registry` SET `Value` = '" + uninstallTarget + "' WHERE `Key` = '" + key +
                    "' AND `Name` = 'UninstallString'");
        Execute(db, "UPDATE `Registry` SET `Value` = '" + uninstallTarget + "' WHERE `Key` = '" + key +
                    "' AND `Name` = 'QuietUninstallString'");
        Console.WriteLine("ARP key " + key + ": UninstallString/QuietUninstallString -> " + uninstallTarget);

        // The product code INSIDE the ARP entry. Guarded machines (field-
        // proven: Emsisoft box) hide the Windows Installer registration tree
        // from in-process readers — MsiEnumProductsW enumerates every other
        // product on the machine and never ours, elevated included — while
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
        var view = db.OpenView(sql);
        try { view.Execute(null); }
        finally { view.Close(); Marshal.ReleaseComObject(view); }
    }
}
