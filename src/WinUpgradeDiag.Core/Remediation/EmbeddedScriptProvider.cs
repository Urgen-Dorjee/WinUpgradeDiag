using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>Where the script that is about to run came from.</summary>
    public enum ScriptOrigin
    {
        /// <summary>Extracted from inside this assembly. Covered by the assembly's own signature.</summary>
        Embedded,

        /// <summary>Read from a folder on disk. Integrity is not guaranteed.</summary>
        Disk
    }

    /// <summary>A script made ready to execute, with the provenance needed to audit it.</summary>
    public sealed class PreparedScript : IDisposable
    {
        private readonly string _scratchFolder;

        internal PreparedScript(string path, ScriptOrigin origin, string sha256, string scratchFolder)
        {
            Path = path;
            Origin = origin;
            Sha256 = sha256;
            _scratchFolder = scratchFolder;
        }

        /// <summary>Full path to the file PowerShell will be pointed at.</summary>
        public string Path { get; }

        public ScriptOrigin Origin { get; }

        /// <summary>SHA-256 of the exact bytes that will run, recorded in the audit log.</summary>
        public string Sha256 { get; }

        public string OriginText =>
            Origin == ScriptOrigin.Embedded
                ? "embedded in the signed application"
                : "loaded from disk (integrity not verified)";

        /// <summary>Removes the extracted copy. Harmless for a disk-sourced script.</summary>
        public void Dispose()
        {
            if (_scratchFolder == null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(_scratchFolder))
                {
                    Directory.Delete(_scratchFolder, true);
                }
            }
            catch (Exception)
            {
                // A leftover temp folder is untidy, not dangerous.
            }
        }
    }

    /// <summary>
    /// Supplies the recovery scripts from inside the assembly.
    /// <para>
    /// A folder of loose <c>.ps1</c> files next to an elevated executable is a writable execution
    /// path: whoever can drop a file there decides what runs as administrator, and it is exactly
    /// the shape endpoint security teams look for. Embedded, the scripts are covered by whatever
    /// signature the assembly carries, so signing one binary protects all of them and tampering
    /// means tampering with the signed assembly itself.
    /// </para>
    /// </summary>
    public static class EmbeddedScriptProvider
    {
        private const string ResourcePrefix = "WinUpgradeDiag.Core.Scripts.";

        /// <summary>Script names carried inside the assembly.</summary>
        public static IReadOnlyList<string> AvailableScripts =>
            typeof(EmbeddedScriptProvider).Assembly
                .GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .Select(n => n.Substring(ResourcePrefix.Length))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static bool Contains(string scriptName)
        {
            return scriptName != null &&
                   AvailableScripts.Any(n => string.Equals(n, scriptName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The bytes of an embedded script, or null if it is not carried.</summary>
        public static byte[] Read(string scriptName)
        {
            if (string.IsNullOrWhiteSpace(scriptName))
            {
                return null;
            }

            var assembly = typeof(EmbeddedScriptProvider).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => string.Equals(n, ResourcePrefix + scriptName, StringComparison.OrdinalIgnoreCase));

            if (name == null)
            {
                return null;
            }

            using (var stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        /// <summary>SHA-256 of an embedded script, for comparing against a copy on disk.</summary>
        public static string HashOf(string scriptName)
        {
            var bytes = Read(scriptName);
            return bytes == null ? null : Hash(bytes);
        }

        /// <summary>
        /// Writes the embedded script to a fresh folder under the user's temp area, locked down to
        /// administrators, and returns the path. The folder is removed when the result is disposed.
        /// </summary>
        public static PreparedScript Extract(string scriptName)
        {
            var bytes = Read(scriptName);
            if (bytes == null)
            {
                return null;
            }

            var folder = Path.Combine(
                Path.GetTempPath(),
                "WinUpgradeDiag_" + Guid.NewGuid().ToString("N"));

            CreateRestricted(folder);

            var path = Path.Combine(folder, scriptName);
            File.WriteAllBytes(path, bytes);

            return new PreparedScript(path, ScriptOrigin.Embedded, Hash(bytes), folder);
        }

        /// <summary>Describes a script already sitting on disk, so the two paths audit alike.</summary>
        public static PreparedScript FromDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            string hash = null;
            try
            {
                hash = Hash(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                // An unreadable file will fail loudly when PowerShell opens it; no hash is fine.
            }

            return new PreparedScript(path, ScriptOrigin.Disk, hash, null);
        }

        /// <summary>
        /// True when the file on disk is byte-identical to the embedded copy. Lets the UI tell an
        /// operator whether the folder they pointed at holds the scripts this build was tested with.
        /// </summary>
        public static bool MatchesEmbedded(string scriptName, string diskPath)
        {
            var embedded = HashOf(scriptName);
            if (embedded == null || string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
            {
                return false;
            }

            try
            {
                return string.Equals(embedded, Hash(File.ReadAllBytes(diskPath)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Creates the scratch folder already carrying a restricted DACL — inheritance off, only
        /// Administrators, SYSTEM and the current user.
        /// <para>
        /// Created-then-tightened would leave a window in which the folder is writable by anyone
        /// who can reach the temp directory, and that window sits precisely between writing the
        /// script and executing it. Creating it restricted closes that window entirely.
        /// </para>
        /// </summary>
        private static void CreateRestricted(string folder)
        {
            try
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);   // drop inherited access

                var principals = new List<IdentityReference>
                {
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
                };

                // The running account too, or a non-elevated session could not write its own
                // scratch copy and every read-only tool would stop working.
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity?.User != null)
                    {
                        principals.Add(identity.User);
                    }
                }

                foreach (var principal in principals)
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        principal,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                }

                Directory.CreateDirectory(folder, security);
            }
            catch (Exception)
            {
                // Hardening, not a precondition: if the DACL cannot be applied, a normally
                // permissioned temp folder still beats refusing to run the tool at all.
                Directory.CreateDirectory(folder);
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                var text = new StringBuilder(digest.Length * 2);
                foreach (var b in digest)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }
    }
}
