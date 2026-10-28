r"""Post-configure the Velopack-generated MSI so the Control Panel / Settings
uninstall fully removes UCU, not just its registration.

Why: the Control Panel routes an uninstall to msiexec by product code, and the
vpk-generated MSI's removal is a ~1 s no-op: it drops the product registration
and ARP entry, then leaves the engine service, the layout in Program Files,
the running app, the scheduled tasks, and the ARP husk behind.

Mechanism: inject two synchronous exe CustomActions, conditioned on
REMOVE="ALL" and sequenced between InstallValidate (1400) and RemoveFiles
(3500). In a per-machine uninstall the execute sequence runs as LocalSystem,
which the engine service DACL always grants, so the removal works on machines
whose policy re-hardens the service DACL as well:

  1453 UCUEngineRemove      the engine exe, --uninstall-engine verb
                           (sc stop + sc delete + app kill + scheduled tasks
                           + HKLM ARP husk + %ProgramData% logs - the verb is
                           the single full-cleanup entry point, shared with
                           the Uninstall.exe shim route)
  1454 UCUVelopackUninstall Update.exe --uninstall (Velopack's own
                           uninstaller: deletes the whole layout, kills the
                           app, removes Velopack's ARP entry)

Type 626 = 32 (source is an exe) | 16 (source column is a file-spec format
string) | 2 (synchronous) | 64 (64-bit) | 512 (hidden window). Field-proven
encoding notes (earlier builds failed 2715/2723/2727):

  * Deferred (1024) launches of exe CAs fire asynchronously: MSI reports 1
    immediately while the wizard races ahead; the launch is invisible. Never
    defer - the execute sequence is already LocalSystem for per-machine MSIs.
  * Type bit 0x10 (16) source = the Source column is a file-spec string,
    the bare File-table key (a "[#...]" wrapper resolves too); paths with
    format codes like [INSTALLFOLDER]/[System64Folder] are NOT valid there
    (2727: treated as a Directory-table entry). The File-table keys are
    random per pack, so pack.ps1 queries them via the Windows Installer COM
    API and passes them here.
  * 0x20 (32) is mandatory: without it the type is rejected (2723).
  * Setter (type 51) properties + [TARGETDIR] were dead ends: vpk roots its
    Directory tree at the volume, so TARGETDIR resolves to "C:\".

Uses only INSERT statements through msilib direct mode; the record plumbing
on this interpreter supports INSERT/UPDATE, not SELECT.

Usage: python build/msi-uninstall-ca.py package.msi <engineExeFileKey> <updateExeFileKey>
"""

import sys

import msilib

# 32 source is an exe | 16 source column is a file-spec string (the bare
# File-table key) | 2 synchronous | 64 64-bit | 512 hidden window.
SYNCHRONOUS_EXE_FILESPEC = 32 | 16 | 2 | 64 | 512

UNINSTALL_CONDITION = 'REMOVE="ALL"'


def insert(db, table, columns, row):
    quoted = ", ".join(f"`{column}`" for column in columns)
    placeholders = ", ".join("?" for _ in columns)
    view = db.OpenView(f"INSERT INTO `{table}` ({quoted}) VALUES ({placeholders})")
    record = msilib.CreateRecord(len(columns))
    for index, (column, value) in enumerate(zip(columns, row), start=1):
        if column in ("Type", "Sequence"):
            record.SetInteger(index, int(value))
        else:
            record.SetString(index, str(value))
    view.Execute(record)
    view.Close()


def main(msi_path, engine_key, update_key):
    db = msilib.OpenDatabase(msi_path, msilib.MSIDBOPEN_DIRECT)

    actions = [
        ("UCUEngineRemove", engine_key, "--uninstall-engine", 1453),
        ("UCUVelopackUninstall", update_key, "--uninstall", 1454),
    ]
    for action, key, target, sequence in actions:
        insert(db, "CustomAction", ["Action", "Type", "Source", "Target"],
               [action, SYNCHRONOUS_EXE_FILESPEC, key, target])
        insert(db, "InstallExecuteSequence", ["Action", "Condition", "Sequence"],
               [action, UNINSTALL_CONDITION, sequence])

    db.Commit()
    print(f"Uninstall CustomActions injected into {msi_path}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
