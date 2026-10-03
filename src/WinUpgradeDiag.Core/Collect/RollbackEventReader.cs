using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using WinUpgradeDiag.Core.IO;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>One piece of evidence that the machine crashed, and the stop code where it carries one.</summary>
    public sealed class BugCheckEvidence
    {
        public BugCheckEvidence(string source, DateTime? timeUtc, uint? code, string text)
        {
            Source = source;
            TimeUtc = timeUtc;
            Code = code;
            Text = text;
        }

        public string Source { get; }
        public DateTime? TimeUtc { get; }

        /// <summary>The stop code, or null for evidence of an unclean restart that names none.</summary>
        public uint? Code { get; }

        public string Text { get; }
    }

    /// <summary>
    /// Reads crash evidence out of event logs — both the live ones and the copies Setup saves into
    /// the Rollback folder.
    /// <para>
    /// The copies are what matter. A machine that bugchecks during the second boot of an upgrade
    /// crashed inside the NEW Windows, and the rollback that follows puts the old Windows back —
    /// along with the old System log. The record of the crash went with the build that was thrown
    /// away, except for the .evtx files Setup preserves in Rollback. The tool listed those files and
    /// never opened them, and queried only the live log, which on a rolled-back machine cannot hold
    /// the event at all.
    /// </para>
    /// </summary>
    public static class RollbackEventReader
    {
        /// <summary>BugCheck (1001), Kernel-Power unclean restart (41) and unexpected shutdown (6008).</summary>
        private const string Query = "*[System[(EventID=41 or EventID=1001 or EventID=6008)]]";

        /// <summary>"The bugcheck was: 0x000001d5 (...)" — the code in the BugCheck event's text or data.</summary>
        private static readonly Regex BugCheckInText = new Regex(
            @"bug\s*check\s+was:?\s*0x(?<code>[0-9A-Fa-f]{1,8})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Kernel-Power 41 carries the code as a decimal data field.</summary>
        private static readonly Regex BugCheckInKernelPower = new Regex(
            @"<Data Name=['""]BugcheckCode['""]>(?<code>\d+)</Data>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The BugCheck event's first data item, when its text could not be rendered.</summary>
        private static readonly Regex BugCheckInData = new Regex(
            @"<Data[^>]*>\s*0x(?<code>[0-9A-Fa-f]{1,8})\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Crash evidence from one saved .evtx file.</summary>
        public static IReadOnlyList<BugCheckEvidence> ReadFile(string evtxPath, IList<string> errors)
        {
            var found = new List<BugCheckEvidence>();

            // EventLogReader opens the file itself, without backup semantics, so a TrustedInstaller-
            // owned copy under $WINDOWS.~BT is refused even when elevated. Copy it out through the
            // privileged opener first, then read the copy.
            var copy = Path.Combine(Path.GetTempPath(), "WinUpgradeDiag_" + Guid.NewGuid().ToString("N") + ".evtx");
            try
            {
                using (var source = TailReader.OpenForRead(evtxPath))
                using (var target = File.Create(copy))
                {
                    source.CopyTo(target);
                }

                var query = new EventLogQuery(copy, PathType.FilePath, Query);
                using (var reader = new EventLogReader(query))
                {
                    for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var evidence = FromRecord(record, Path.GetFileName(evtxPath));
                            if (evidence != null)
                            {
                                found.Add(evidence);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors?.Add("Could not read the saved event log " + Path.GetFileName(evtxPath) + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    File.Delete(copy);
                }
                catch (Exception)
                {
                    // A leftover temp copy is untidy, not dangerous.
                }
            }

            return found;
        }

        private static BugCheckEvidence FromRecord(EventRecord record, string fileName)
        {
            string text = null;
            try
            {
                text = record.FormatDescription();
            }
            catch (Exception)
            {
                // A saved log from another build can lack the message resources; the XML still has
                // the data.
            }

            string xml = null;
            try
            {
                xml = record.ToXml();
            }
            catch (Exception)
            {
            }

            var when = record.TimeCreated?.ToUniversalTime();
            var source = "Saved event log: " + fileName + " (event " + record.Id + ")";

            switch (record.Id)
            {
                case 1001:
                {
                    var code = ParseCode(BugCheckInText, text) ?? ParseCode(BugCheckInData, xml);
                    // Application error reporting also uses 1001; only a stop code makes it a crash.
                    if (code == null)
                    {
                        return null;
                    }
                    return new BugCheckEvidence(source, when, code, Trim(text ?? "BugCheck " + Describe(code.Value)));
                }

                case 41:
                {
                    var code = ParseDecimal(BugCheckInKernelPower, xml);
                    return new BugCheckEvidence(
                        source, when, code == 0 ? null : code,
                        code.HasValue && code.Value != 0
                            ? "The machine restarted without shutting down cleanly, after stop code " + Describe(code.Value) + "."
                            : "The machine restarted without shutting down cleanly.");
                }

                case 6008:
                    return new BugCheckEvidence(source, when, null, Trim(text ?? "The previous shutdown was unexpected."));

                default:
                    return null;
            }
        }

        /// <summary>
        /// Stop codes from the live System log's BugCheck events, which the event collector already
        /// gathers. On a machine that crashed in its current build — rather than in a rolled-back
        /// one — this is where the record is.
        /// </summary>
        public static BugCheckEvidence FromLiveEvent(EventRecordInfo e)
        {
            if (e == null || e.EventId != 1001 ||
                !string.Equals(e.LogName, "System", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var code = ParseCode(BugCheckInText, e.Message);
            return code == null
                ? null
                : new BugCheckEvidence("Event log: System (event 1001)", e.TimeCreatedUtc, code, Trim(e.Message));
        }

        public static uint? ParseCode(string text)
        {
            return ParseCode(BugCheckInText, text);
        }

        private static uint? ParseCode(Regex pattern, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var m = pattern.Match(text);
            uint value;
            return m.Success && uint.TryParse(m.Groups["code"].Value, NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture, out value) && value != 0
                ? value
                : (uint?)null;
        }

        private static uint? ParseDecimal(Regex pattern, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var m = pattern.Match(text);
            uint value;
            return m.Success && uint.TryParse(m.Groups["code"].Value, NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out value)
                ? value
                : (uint?)null;
        }

        private static string Describe(uint code)
        {
            return Rules.BugCheckNames.Describe(code);
        }

        private static string Trim(string text)
        {
            var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= 300 ? t : t.Substring(0, 300) + "…";
        }
    }
}
