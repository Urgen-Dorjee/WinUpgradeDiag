using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Native;

namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// Turns a list of candidate <see cref="LogSource"/> locations into a manifest of what is
    /// actually on disk. Never throws on a missing or inaccessible file — AGENTS.md is explicit
    /// that access denied is normal here, not an error: report the gap and keep going.
    /// </summary>
    public sealed class LogManifestBuilder
    {
        public IReadOnlyList<LogManifestEntry> Build(IEnumerable<LogSource> sources)
        {
            var entries = new List<LogManifestEntry>();

            foreach (var source in sources)
            {
                try
                {
                    entries.AddRange(BuildForSource(source));
                }
                catch (Exception ex)
                {
                    // A single misbehaving source (e.g. a malformed path) must not stop the rest
                    // of discovery. Record it as an unreadable, non-existent entry and move on.
                    entries.Add(LogManifestEntry.Unresolved(
                        source, source.Path, false, "Could not resolve this source: " + ex.Message));
                }
            }

            return entries;
        }

        private static List<LogManifestEntry> BuildForSource(LogSource source)
        {
            if (source.Kind == LogSourceKind.File)
            {
                return new List<LogManifestEntry> { BuildFileEntry(source, source.Path) };
            }

            if (!Directory.Exists(source.Path))
            {
                return new List<LogManifestEntry> { LogManifestEntry.Missing(source, source.Path) };
            }

            string[] matches;
            try
            {
                matches = Directory.GetFiles(source.Path, source.SearchPattern ?? "*.*");
            }
            catch (UnauthorizedAccessException ex)
            {
                return new List<LogManifestEntry>
                {
                    LogManifestEntry.Unresolved(source, source.Path, true, "Access denied listing directory: " + ex.Message)
                };
            }

            if (matches.Length == 0)
            {
                // Explicit "nothing here" entry rather than silently omitting the source:
                // absent evidence must be said plainly, never implied (DESIGN.md §8).
                return new List<LogManifestEntry> { LogManifestEntry.Missing(source, source.Path) };
            }

            // Most recently written first: several smsts.log files are different runs.
            return matches
                .Select(path => BuildFileEntry(source, path))
                .OrderByDescending(e => e.LastWriteTimeUtc ?? DateTime.MinValue)
                .ToList();
        }

        private static LogManifestEntry BuildFileEntry(LogSource source, string path)
        {
            if (!File.Exists(path))
            {
                // File.Exists returns false both for "not there" and for "you may not traverse the
                // parent directory", and $WINDOWS.~BT is the second case on a machine where the
                // rollback logs matter most. Ask the privileged probe which one it is before
                // recording the log as absent — reporting unreadable evidence as "nothing found"
                // is the specific failure DESIGN.md §8 warns about.
                return ProbeForHiddenFile(source, path);
            }

            var info = new FileInfo(path);
            long size;
            DateTime lastWrite;
            try
            {
                size = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                return LogManifestEntry.Unresolved(source, path, true,
                    "Access denied reading file metadata: " + ex.Message);
            }

            bool readable;
            bool requiresPrivilegedRead;
            string accessError;
            TryStandardOpen(path, out readable, out requiresPrivilegedRead, out accessError);

            return LogManifestEntry.Found(source, path, size, lastWrite, readable, requiresPrivilegedRead, accessError);
        }

        /// <summary>
        /// Distinguishes a genuinely absent file from one hidden behind a directory we cannot list.
        /// </summary>
        private static LogManifestEntry ProbeForHiddenFile(LogSource source, string path)
        {
            string probeError;
            ProtectedFileReader.ProbeResult probe;
            try
            {
                probe = ProtectedFileReader.Probe(path, out probeError);
            }
            catch (Exception ex)
            {
                // The probe itself must never be the thing that breaks discovery.
                return LogManifestEntry.Unresolved(source, path, false, "Could not probe this path: " + ex.Message);
            }

            switch (probe)
            {
                case ProtectedFileReader.ProbeResult.NotFound:
                    return LogManifestEntry.Missing(source, path);

                case ProtectedFileReader.ProbeResult.AccessDenied:
                    return LogManifestEntry.PresentButUnreadable(source, path,
                        "Present but unreadable — " + (probeError ?? "access denied"));

                case ProtectedFileReader.ProbeResult.Readable:
                    // Readable only via the privileged path: the file is really there, even though
                    // File.Exists said otherwise, so record it rather than dropping it.
                    return TryDescribePrivileged(source, path);

                default:
                    return LogManifestEntry.Unresolved(source, path, false, probeError);
            }
        }

        /// <summary>
        /// Reads size and timestamp for a file only reachable with backup privilege.
        /// </summary>
        private static LogManifestEntry TryDescribePrivileged(LogSource source, string path)
        {
            try
            {
                using (var stream = ProtectedFileReader.OpenForPrivilegedRead(path))
                {
                    var lastWrite = DateTime.MinValue;
                    try
                    {
                        lastWrite = File.GetLastWriteTimeUtc(path);
                    }
                    catch (Exception)
                    {
                        // Size alone is still worth reporting.
                    }

                    return LogManifestEntry.Found(source, path, stream.Length, lastWrite, true, true, null);
                }
            }
            catch (Exception ex)
            {
                return LogManifestEntry.Unresolved(source, path, true,
                    "Present but could not be opened: " + ex.Message);
            }
        }

        private static void TryStandardOpen(
            string path, out bool readable, out bool requiresPrivilegedRead, out string accessError)
        {
            try
            {
                using (new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    readable = true;
                    requiresPrivilegedRead = false;
                    accessError = null;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The expected shape for $WINDOWS.~BT\Sources\Panther and \Rollback, which are
                // TrustedInstaller-owned. Not a bug — the tail reader retries these with
                // SeBackupPrivilege (see Native/ProtectedFileReader).
                readable = false;
                requiresPrivilegedRead = true;
                accessError = "Access denied — likely TrustedInstaller-owned; requires privileged read.";
            }
            catch (IOException ex)
            {
                readable = false;
                requiresPrivilegedRead = false;
                accessError = "In use or locked: " + ex.Message;
            }
        }
    }
}
