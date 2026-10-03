using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// What Microsoft's SetupDiag concluded about a failed upgrade.
    /// <para>
    /// Since Windows 10 2004, when an upgrade fails Windows Setup runs SetupDiag on its own logs and
    /// saves the result - to %windir%\Logs\SetupDiag\SetupDiagResults.xml and to the registry under
    /// HKLM\SYSTEM\Setup\SetupDiag\Results. It is Microsoft's own rule set for upgrade failures, it is
    /// often the most direct answer on the machine, and it was sitting there unread.
    /// </para>
    /// </summary>
    public sealed class SetupDiagResult
    {
        public string Source { get; set; }
        public DateTime? WrittenUtc { get; set; }

        /// <summary>The rule SetupDiag matched, e.g. "DriverInstallFailure". Empty when none matched.</summary>
        public string ProfileName { get; set; }

        public string ErrorCode { get; set; }
        public string ExtendedErrorCode { get; set; }

        /// <summary>SetupDiag's explanation, line by line.</summary>
        public IList<string> Messages { get; } = new List<string>();

        public string FailureDetails { get; set; }

        /// <summary>Anything SetupDiag said about a device or driver: hardware ids, inf names.</summary>
        public IList<string> DriverLines { get; } = new List<string>();

        public IList<string> Remediation { get; } = new List<string>();

        public bool HasConclusion =>
            !string.IsNullOrWhiteSpace(ProfileName) || Messages.Count > 0 || !string.IsNullOrWhiteSpace(FailureDetails);
    }

    /// <summary>
    /// Reads SetupDiag's results without depending on every detail of its schema.
    /// <para>
    /// The results file has changed between SetupDiag versions and carries an XML namespace that
    /// has moved with Microsoft's documentation site. Elements are matched by local name, and
    /// driver information is collected from any element whose name says it is about a driver,
    /// rather than from fixed paths that would silently stop matching on the next version.
    /// </para>
    /// </summary>
    public static class SetupDiagResultsReader
    {
        /// <summary>Where Setup writes the results when it runs SetupDiag automatically.</summary>
        public const string RegistryPath = @"SYSTEM\Setup\SetupDiag\Results";

        private static readonly string[] DriverWords = { "driver", "hardwareid", "infname", "inf", "device" };

        public static SetupDiagResult ReadFile(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var result = Read(stream);
                    if (result != null)
                    {
                        result.Source = path;
                        result.WrittenUtc = File.GetLastWriteTimeUtc(path);
                    }
                    return result;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static SetupDiagResult Read(Stream stream)
        {
            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                }))
                {
                    doc.Load(reader);
                }
            }
            catch (Exception)
            {
                return null;
            }

            var result = new SetupDiagResult
            {
                ProfileName = First(doc, "ProfileName"),
                ErrorCode = First(doc, "ErrorCode"),
                ExtendedErrorCode = First(doc, "ExtendedErrorCode"),
                FailureDetails = First(doc, "FailureDetails")
            };

            foreach (XmlNode message in doc.SelectNodes("//*[local-name()='FailureData']/*[local-name()='Message']"))
            {
                Add(result.Messages, message.InnerText);
            }

            // Leaf elements only: a parent's InnerText is every child run together.
            foreach (XmlElement leaf in doc.SelectNodes("//*[not(*)]").OfType<XmlElement>())
            {
                var name = leaf.LocalName;
                var lower = name.ToLowerInvariant();

                if (lower.Contains("remediation"))
                {
                    Add(result.Remediation, leaf.InnerText);
                    continue;
                }

                // SystemInfo lists every filter driver on the machine; that is inventory, not a
                // finding, and the tool reports filter drivers separately.
                if (IsUnder(leaf, "SystemInfo"))
                {
                    continue;
                }

                if (DriverWords.Any(w => lower.Contains(w)) && !string.IsNullOrWhiteSpace(leaf.InnerText))
                {
                    Add(result.DriverLines, name + ": " + leaf.InnerText.Trim());
                }
            }

            return result;
        }

        /// <summary>
        /// The registry copy of the result. Present on machines where the XML has been tidied away,
        /// and the same shape on every SetupDiag version: one value per field.
        /// </summary>
        public static SetupDiagResult ReadRegistry()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(RegistryPath))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in key.GetValueNames())
                    {
                        var value = key.GetValue(name);
                        var text = value as string ?? (value as string[] == null ? value?.ToString() : string.Join("\n", (string[])value));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            values[name] = text;
                        }
                    }

                    var result = FromValues(values);
                    if (result != null)
                    {
                        result.Source = @"Registry: HKLM\" + RegistryPath;
                    }
                    return result;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Builds a result from registry values. Separate so it can be tested without a registry.</summary>
        public static SetupDiagResult FromValues(IDictionary<string, string> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            string v;
            var result = new SetupDiagResult
            {
                ProfileName = values.TryGetValue("ProfileName", out v) ? v.Trim() : null,
                ErrorCode = values.TryGetValue("ErrorCode", out v) ? v.Trim() : null,
                ExtendedErrorCode = values.TryGetValue("ExtendedErrorCode", out v) ? v.Trim() : null,
                FailureDetails = values.TryGetValue("FailureDetails", out v) ? v.Trim() : null
            };

            if (values.TryGetValue("FailureData", out v))
            {
                foreach (var line in v.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Add(result.Messages, line);
                }
            }

            foreach (var pair in values)
            {
                var lower = pair.Key.ToLowerInvariant();
                if (lower.Contains("remediation"))
                {
                    Add(result.Remediation, pair.Value);
                }
                else if (DriverWords.Any(w => lower.Contains(w)))
                {
                    Add(result.DriverLines, pair.Key + ": " + pair.Value.Trim());
                }
            }

            return result;
        }

        private static string First(XmlDocument doc, string localName)
        {
            var node = doc.SelectSingleNode("//*[local-name()='" + localName + "']");
            var text = node?.InnerText?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static bool IsUnder(XmlNode node, string ancestorLocalName)
        {
            for (var p = node.ParentNode; p != null; p = p.ParentNode)
            {
                if (string.Equals(p.LocalName, ancestorLocalName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Add(IList<string> list, string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length > 0 && !list.Contains(t))
            {
                list.Add(t.Length <= 400 ? t : t.Substring(0, 400) + "…");
            }
        }
    }
}
