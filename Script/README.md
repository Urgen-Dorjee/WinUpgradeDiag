# Get-UpgradeFailure.ps1

The script version of WinUpgradeDiag, for PCs where application control (WDAC, AppLocker or Smart
App Control) blocks the executable. It only reads; it changes nothing.

Run it elevated:

```powershell
powershell -ExecutionPolicy Bypass -File .\Get-UpgradeFailure.ps1
```

It writes a timestamped report to the desktop (or `-OutputFolder`) and prints it:

1. Which task sequence step failed, and with what code.
2. Why Software Center is not offering the upgrade: the PC's ConfigMgr identity and any previous
   one, provisioning mode, a leftover "task sequence running" record, when it last asked for
   policy, which task sequences are deployed to it, what Software Center lists, and past runs.
3. What Windows Setup recorded as an error.
4. What Microsoft's SetupDiag concluded.
5. Whether the machine crashed, with which stop code, and which device install the crash cut off.
6. Which device drivers failed to install.
7. Machine state, third-party driver packages and filter drivers.

Run it **before** wiping WMI, running ccmclean or reinstalling the client: those destroy the
evidence it reads.
