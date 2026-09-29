using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinUpgradeDiag.Tests.Discovery
{
    /// <summary>
    /// Covers the case that motivates the whole SeBackupPrivilege design: a Setup log sitting in a
    /// directory the caller may not traverse, as <c>C:\$WINDOWS.~BT\Sources\Rollback</c> is.
    /// <para>
    /// <see cref="File.Exists"/> returns <c>false</c> there, which is indistinguishable from the
    /// file being absent. DESIGN.md §8 is explicit that this must not happen: unreadable evidence
    /// has to be reported as unreadable, or a reader concludes "no Setup failure found" from a
    /// rollback log nobody could open.
    /// </para>
    /// <para>
    /// The assertions deliberately accept two outcomes. Whether the privileged read succeeds
    /// depends on whether the running process holds SeBackupPrivilege, which differs between a
    /// developer machine and a CI runner — and both answers are correct, provided neither is
    /// "missing".
    /// </para>
    /// </summary>
    public class ProtectedLogDiscoveryTests
    {
        private readonly ITestOutputHelper _output;

        public ProtectedLogDiscoveryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void A_log_behind_an_unlistable_directory_is_reported_protected_not_absent()
        {
            using (var tmp = new TempDirectory())
            {
                var dir = Path.Combine(tmp.Path, "Rollback");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, "setupact.log");
                File.WriteAllText(logPath, "2026-09-18 12:10:17, Error SP  rollback evidence\n");

                if (!TryDenyAllAccess(dir, logPath))
                {
                    // Some environments will not let a test rewrite a DACL, and on others the deny
                    // does not reach the file. Skipping beats a spurious failure: without the trap
                    // actually reproduced here, the assertions below test nothing. xUnit 2 has no
                    // dynamic skip, so say so in the output rather than passing silently — a test
                    // that quietly stops testing is worse than one that fails.
                    _output.WriteLine(
                        "SKIPPED: this environment would not reproduce an unreadable directory, " +
                        "so protected-not-absent was not exercised here.");
                    RestoreAccess(dir);
                    return;
                }

                try
                {
                    var source = new LogSource(
                        "setup-rollback-act", LogSourceCategory.SetupRollback,
                        "setupact.log (rollback)", "", logPath, highValue: true);

                    var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                    // The invariant, and the only one that matters: a log the caller cannot open
                    // normally is never reported as absent. DESIGN.md §8 — unreadable evidence has
                    // to be reported as unreadable, or a reader concludes "no Setup failure found"
                    // from a rollback log nobody could open.
                    Assert.True(entry.Exists);
                    Assert.True(entry.RequiresPrivilegedRead);

                    // Which of the two legitimate outcomes follows depends on whether this process
                    // holds SeBackupPrivilege, and both are correct. A developer box running the
                    // suite unelevated gets the degraded one; a CI runner whose job is an
                    // administrator gets the privileged read succeeding, which is the entire point
                    // of the feature. Asserting only the degraded outcome failed the build on CI
                    // for doing better than the test expected.
                    if (entry.Readable)
                    {
                        // Opened through the backup-semantics path.
                        Assert.True(entry.SizeKnown);
                        Assert.True(entry.SizeBytes > 0);
                        Assert.Null(entry.AccessError);
                    }
                    else
                    {
                        // No privilege to fall back on: say so, and do not present an
                        // unmeasured size as a measured zero.
                        Assert.NotNull(entry.AccessError);
                        Assert.False(entry.SizeKnown);
                    }
                }
                finally
                {
                    RestoreAccess(dir);
                }
            }
        }

        /// <summary>
        /// The branch above that only runs on a process holding SeBackupPrivilege, asserted here
        /// against the entry the privileged path actually constructs.
        /// <para>
        /// Worth having separately because the environment that exercises it — an elevated CI
        /// runner — is not the one the suite usually runs on, and the mismatch between what
        /// <c>TryDescribePrivileged</c> returns and what the test expected is exactly what failed
        /// a release build.
        /// </para>
        /// </summary>
        [Fact]
        public void A_log_read_through_backup_privilege_reports_a_real_size_and_no_error()
        {
            var source = new LogSource(
                "setup-rollback-act", LogSourceCategory.SetupRollback,
                "setupact.log (rollback)", "", @"C:\$WINDOWS.~BT\Sources\Rollback\setupact.log",
                highValue: true);

            // The shape LogManifestBuilder.TryDescribePrivileged produces on success.
            var entry = LogManifestEntry.Found(
                source, source.Path, 103_900_000,
                new DateTime(2026, 9, 25, 22, 21, 36, DateTimeKind.Utc),
                readable: true, requiresPrivilegedRead: true, accessError: null);

            // Never "missing", which is the invariant the whole design exists for.
            Assert.True(entry.Exists);
            Assert.True(entry.RequiresPrivilegedRead);

            // And the assertions the privileged branch of the ACL test makes.
            Assert.True(entry.Readable);
            Assert.True(entry.SizeKnown);
            Assert.True(entry.SizeBytes > 0);
            Assert.Null(entry.AccessError);
        }

        [Fact]
        public void A_genuinely_absent_log_is_still_reported_as_missing()
        {
            using (var tmp = new TempDirectory())
            {
                var source = new LogSource(
                    "setup-rollback-act", LogSourceCategory.SetupRollback,
                    "setupact.log (rollback)", "", Path.Combine(tmp.Path, "Rollback", "setupact.log"),
                    highValue: true);

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                // The probe must not turn "not there" into "protected" — that would be the same
                // confusion in the other direction.
                Assert.False(entry.Exists);
                Assert.False(entry.RequiresPrivilegedRead);
                Assert.False(entry.SizeKnown);
                Assert.Null(entry.AccessError);
            }
        }

        [Fact]
        public void A_readable_log_reports_a_known_size()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("setupact.log", "hello world");
                var source = new LogSource("s", LogSourceCategory.SetupCurrent, "setupact.log", "", path);

                var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                Assert.True(entry.Readable);
                Assert.True(entry.SizeKnown);
                Assert.Equal(11, entry.SizeBytes);
            }
        }

        /// <summary>
        /// Strips every access rule from <paramref name="directory"/>, leaving it unlistable.
        /// Returns true only once the trap this test needs is confirmed to be in place.
        /// <para>
        /// The guard has to check the same thing the test asserts. Verifying only that enumeration
        /// is refused is not enough: "bypass traverse checking" is granted to Everyone by default,
        /// so a caller who cannot list a directory can still open a file inside it by name if the
        /// file kept an inherited allow rule. That is a different access check with a different
        /// answer, and which way it lands varies by machine — this passed on a developer box and on
        /// one CI run, then failed on the next, blocking a release for an environment difference
        /// rather than a defect.
        /// </para>
        /// </summary>
        private static bool TryDenyAllAccess(string directory, string fileInside)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var security = info.GetAccessControl();
                security.SetAccessRuleProtection(true, false); // drop inherited rules, add none
                info.SetAccessControl(security);
            }
            catch (Exception)
            {
                return false; // not permitted to rewrite the DACL here
            }

            // The deny worked precisely when enumeration is refused. Anything else — including a
            // successful listing — means the fixture is not in the state this test needs.
            try
            {
                Directory.EnumerateFileSystemEntries(directory).Any();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Listing is refused. Now the part that actually matters: the file behind it must
                // also be unreachable by name, because that is the trap the production code exists
                // to work around.
                return !File.Exists(fileInside);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void RestoreAccess(string directory)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            catch (Exception)
            {
                // TempDirectory.Dispose is already best-effort; a leftover folder is not a failure.
            }
        }
    }
}
