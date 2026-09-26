using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinUpgradeDiag.Core.Redaction
{
    /// <summary>
    /// Removes usernames, profile paths and the machine name from text headed for an HTML or
    /// JSON export (docs/SECURITY.md, control #1). On by default; the evidence zip is the only
    /// artefact that is never redacted.
    /// </summary>
    public sealed class Redactor
    {
        public const string UserPlaceholder = "<user>";
        public const string MachinePlaceholder = "<machine>";

        private static readonly Regex ProfilePath = new Regex(
            @"(?i)(\\(?:Users|Documents and Settings)\\)([^\\/:*?""<>|\r\n\s]+)",
            RegexOptions.Compiled);

        private static readonly HashSet<string> NonPersonalProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Public", "Default", "Default User", "All Users", "defaultuser0", "defaultuser100000"
        };

        /// <summary>
        /// Shortest name accepted as a redactable token. A user token is matched everywhere in the
        /// text, not just in paths, so a very short one does real damage: a profile folder called
        /// "IT" or "svc" would rewrite those letters throughout the report. Four characters is long
        /// enough to be a plausible account name and short enough to catch real ones; anything
        /// shorter is still covered inside profile paths by <see cref="ProfilePath"/>.
        /// </summary>
        public const int MinimumTokenLength = 4;

        /// <summary>
        /// Words that look like account names but are structural parts of the logs. Panther writes
        /// its component in a fixed column — SP, MIG, CONX and friends — and ConfigMgr names its
        /// own components the same way. A machine with a profile folder called "MIG" must not end
        /// up with every Panther component column replaced by "&lt;user&gt;": that destroys exactly
        /// the evidence the report exists to carry.
        /// </summary>
        private static readonly HashSet<string> ReservedLogTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Panther / Setup components (docs/DESIGN.md §4.3)
            "SP", "MIG", "CONX", "PANTHR", "IBSLIB", "DISM", "CSI", "CBS", "MOUPG", "MOSETUP",
            "SETUP", "SETUPACT", "SETUPERR", "WINDOWS", "SYSTEM", "PANTHER", "ROLLBACK",
            // ConfigMgr / task sequence components
            "TSMANAGER", "SMSTS", "EXECMGR", "CCMEXEC", "CCMSETUP", "APPENFORCE", "POLICYAGENT",
            "CAS", "CCM", "SMS", "TRUSTEDINSTALLER", "SETUPHOST", "SETUPPREP",
            // Generic words that appear constantly in log text
            "ERROR", "WARNING", "INFO", "ADMIN", "ADMINISTRATOR", "USER", "USERS", "PUBLIC",
            "DEFAULT", "TEMP", "LOCAL", "SERVICE", "NETWORK"
        };

        private readonly List<KeyValuePair<Regex, string>> _tokens = new List<KeyValuePair<Regex, string>>();

        public Redactor(string machineName, IEnumerable<string> userNames)
        {
            // Longest first so "jsmith-admin" is replaced before "jsmith" can split it.
            var users = (userNames ?? Enumerable.Empty<string>())
                .Where(IsRedactableToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(u => u.Length);

            foreach (var user in users)
            {
                _tokens.Add(new KeyValuePair<Regex, string>(WholeToken(user), UserPlaceholder));
            }

            // The machine name is held to the same floor. It is the operator's own hostname, so a
            // two-letter one is vanishingly rare, and the cost of a bad match is the same.
            if (IsRedactableToken(machineName))
            {
                _tokens.Add(new KeyValuePair<Regex, string>(WholeToken(machineName), MachinePlaceholder));
            }
        }

        /// <summary>
        /// Whether a name is safe to replace wherever it appears. Rejecting a name here does not
        /// expose a profile path — <see cref="ProfilePath"/> still rewrites the segment after
        /// <c>\Users\</c> regardless of length — it only declines to rewrite those letters in the
        /// middle of unrelated log text.
        /// </summary>
        private static bool IsRedactableToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Length >= MinimumTokenLength
                   && !NonPersonalProfiles.Contains(value)
                   && !ReservedLogTokens.Contains(value);
        }

        /// <summary>Builds a redactor from the current machine: its name, the current user, and every profile folder.</summary>
        public static Redactor ForCurrentMachine()
        {
            var users = new List<string> { Environment.UserName, Environment.UserDomainName };

            try
            {
                var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar;
                var usersRoot = Path.Combine(systemDrive, "Users");
                if (Directory.Exists(usersRoot))
                {
                    users.AddRange(Directory.GetDirectories(usersRoot).Select(Path.GetFileName));
                }
            }
            catch (Exception)
            {
                // Profile enumeration is a best-effort extra; the path regex still covers profiles.
            }

            // The domain name equal to the machine name (workgroup machines) is redacted as the machine.
            users.RemoveAll(u => string.Equals(u, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            return new Redactor(Environment.MachineName, users);
        }

        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = ProfilePath.Replace(text, m =>
                NonPersonalProfiles.Contains(m.Groups[2].Value) ? m.Value : m.Groups[1].Value + UserPlaceholder);

            foreach (var token in _tokens)
            {
                result = token.Key.Replace(result, token.Value);
            }

            return result;
        }

        private static Regex WholeToken(string value)
        {
            return new Regex(
                @"(?<![A-Za-z0-9_])" + Regex.Escape(value) + @"(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
