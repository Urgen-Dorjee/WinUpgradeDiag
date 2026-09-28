using System;
using System.IO;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>Whether a tool's own preconditions hold, and why not if they do not.</summary>
    public sealed class PreconditionCheck
    {
        private PreconditionCheck(bool satisfied, string reason)
        {
            Satisfied = satisfied;
            Reason = reason;
        }

        public bool Satisfied { get; }

        /// <summary>Why the tool must not run, or null when it may.</summary>
        public string Reason { get; }

        public static PreconditionCheck Ok()
        {
            return new PreconditionCheck(true, null);
        }

        public static PreconditionCheck Refuse(string reason)
        {
            return new PreconditionCheck(false, reason);
        }
    }

    /// <summary>
    /// Conditions specific to individual tools, checked before anything runs.
    /// <para>
    /// The generic gates — elevation, no live upgrade, script present — apply to everything. These
    /// are the per-tool ones that stop an action being taken on a machine where it makes no sense,
    /// which is the cheapest possible protection against a mis-click: the tool simply cannot run.
    /// </para>
    /// </summary>
    public static class ToolPreconditions
    {
        internal const string WindowsOld = @"C:\Windows.old";
        internal const string SetupWorking = @"C:\$WINDOWS.~BT";
        internal const string RollbackEvidence = @"C:\$WINDOWS.~BT\Sources\Rollback";

        public static PreconditionCheck Check(ToolDefinition tool)
        {
            if (tool == null)
            {
                return PreconditionCheck.Ok();
            }

            switch (tool.Id)
            {
                case "REMOVE-LEFTOVERS": return CheckReclaimSpace();
                case "FIX-D": return CheckSetupLeftovers();
                default: return PreconditionCheck.Ok();
            }
        }

        /// <summary>
        /// Reclaiming space retires the rollback permanently. Two states make that wrong, and both
        /// are cheap to detect, so the tool refuses rather than relying on the operator reading a
        /// warning they have seen a dozen times.
        /// </summary>
        private static PreconditionCheck CheckReclaimSpace()
        {
            // 1. Nothing to reclaim. DISM /Remove-OSUninstall has nothing to do without Windows.old,
            //    so a click here is almost certainly a mistake.
            if (!SafeExists(WindowsOld))
            {
                return PreconditionCheck.Refuse(
                    "There is no rollback data to reclaim on this machine — C:\\Windows.old does not exist. " +
                    "This tool is only for a machine that has just upgraded successfully. " +
                    "Nothing has been changed.");
            }

            // 2. The machine rolled back rather than upgrading. Retiring the rollback set on a
            //    machine that reverted destroys the evidence of why it failed.
            if (SafeExists(RollbackEvidence))
            {
                return PreconditionCheck.Refuse(
                    "This machine has Windows Setup rollback logs, which means an upgrade reverted rather " +
                    "than succeeded. Retiring the rollback data now would delete the evidence of why it failed. " +
                    "Investigate the rollback first, and export the evidence bundle. Nothing has been changed.");
            }

            return PreconditionCheck.Ok();
        }

        /// <summary>
        /// Fix-D deletes the half-built Setup folder. The script itself refuses when Windows.old is
        /// present; checking here means the operator is told before confirming rather than after.
        /// </summary>
        private static PreconditionCheck CheckSetupLeftovers()
        {
            if (SafeExists(WindowsOld))
            {
                return PreconditionCheck.Refuse(
                    "C:\\Windows.old is present, so this machine may have upgraded successfully. " +
                    "Deleting the Setup folder on its own would break \"Go back\" while leaving Windows.old " +
                    "consuming space. Use \"Reclaim space after a successful upgrade\" instead. " +
                    "Nothing has been changed.");
            }

            if (!SafeExists(SetupWorking))
            {
                return PreconditionCheck.Refuse(
                    "There is no C:\\$WINDOWS.~BT folder to remove on this machine. Nothing has been changed.");
            }

            return PreconditionCheck.Ok();
        }

        private static bool SafeExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                // Unreadable is not the same as absent. Assume present, which is the cautious
                // answer: it makes a destructive tool refuse rather than proceed.
                return true;
            }
        }
    }
}
