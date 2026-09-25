using System;
using System.IO;

namespace WinUpgradeDiag.Core.IO
{
    /// <summary>
    /// Decides which files the log viewer may render as text. miglog.xml is refused outright:
    /// it enumerates migrated file paths, which on a clinical workstation can include patient
    /// document names (AGENTS.md constraint #6). Binary artefacts are summarised, not rendered.
    /// </summary>
    public static class LogViewerPolicy
    {
        public static bool CanRender(string path, out string reason)
        {
            var name = Path.GetFileName(path ?? string.Empty);
            var ext = Path.GetExtension(name);

            if (string.Equals(name, "miglog.xml", StringComparison.OrdinalIgnoreCase))
            {
                reason = "miglog.xml can list patient document names; its contents are never displayed.";
                return false;
            }

            if (string.Equals(ext, ".dmp", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Crash dumps are binary and may hold memory contents; they are referenced by path only.";
                return false;
            }

            if (string.Equals(ext, ".evtx", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Exported event logs are binary; open them in Event Viewer.";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
