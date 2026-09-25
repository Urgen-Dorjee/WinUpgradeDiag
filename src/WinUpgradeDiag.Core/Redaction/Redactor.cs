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

        private readonly List<KeyValuePair<Regex, string>> _tokens = new List<KeyValuePair<Regex, string>>();

        public Redactor(string machineName, IEnumerable<string> userNames)
        {
            // Longest first so "jsmith-admin" is replaced before "jsmith" can split it.
            var users = (userNames ?? Enumerable.Empty<string>())
                .Where(u => !string.IsNullOrWhiteSpace(u) && u.Length >= 2 && !NonPersonalProfiles.Contains(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(u => u.Length);

            foreach (var user in users)
            {
                _tokens.Add(new KeyValuePair<Regex, string>(WholeToken(user), UserPlaceholder));
            }

            if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 2)
            {
                _tokens.Add(new KeyValuePair<Regex, string>(WholeToken(machineName), MachinePlaceholder));
            }
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
