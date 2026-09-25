using System;

namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// What the discovery stage found (or didn't) for one resolved path. This is the manifest
    /// entry shown in the UI's Logs tab and written into the JSON export — it answers "which
    /// phase did it die in" before a single line is parsed.
    /// </summary>
    public sealed class LogManifestEntry
    {
        public LogSource Source { get; }

        /// <summary>The concrete file path this entry describes (one per matched file).</summary>
        public string ResolvedPath { get; }

        public bool Exists { get; }
        public long SizeBytes { get; }
        public DateTime? LastWriteTimeUtc { get; }

        /// <summary>True if the file could be opened for a standard, unprivileged read.</summary>
        public bool Readable { get; }

        /// <summary>
        /// True when the file exists but a standard read failed with access denied — the usual
        /// signature of a TrustedInstaller-owned log that needs SeBackupPrivilege.
        /// </summary>
        public bool RequiresPrivilegedRead { get; }

        /// <summary>Human-readable reason the file could not be read, or null if it could.</summary>
        public string AccessError { get; }

        private LogManifestEntry(
            LogSource source,
            string resolvedPath,
            bool exists,
            long sizeBytes,
            DateTime? lastWriteTimeUtc,
            bool readable,
            bool requiresPrivilegedRead,
            string accessError)
        {
            Source = source;
            ResolvedPath = resolvedPath;
            Exists = exists;
            SizeBytes = sizeBytes;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Readable = readable;
            RequiresPrivilegedRead = requiresPrivilegedRead;
            AccessError = accessError;
        }

        public static LogManifestEntry Missing(LogSource source, string resolvedPath)
        {
            return new LogManifestEntry(source, resolvedPath, false, 0, null, false, false, null);
        }

        /// <summary>Something is (or may be) there, but not even its metadata could be read.</summary>
        public static LogManifestEntry Unresolved(LogSource source, string resolvedPath, bool requiresPrivilegedRead, string error)
        {
            return new LogManifestEntry(source, resolvedPath, false, 0, null, false, requiresPrivilegedRead, error);
        }

        public static LogManifestEntry Found(
            LogSource source,
            string resolvedPath,
            long sizeBytes,
            DateTime lastWriteTimeUtc,
            bool readable,
            bool requiresPrivilegedRead,
            string accessError)
        {
            return new LogManifestEntry(
                source, resolvedPath, true, sizeBytes, lastWriteTimeUtc, readable, requiresPrivilegedRead, accessError);
        }
    }
}
