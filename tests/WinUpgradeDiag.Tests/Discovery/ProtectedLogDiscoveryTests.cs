using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Tests.Support;
using Xunit;

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
    /// </summary>
    public class ProtectedLogDiscoveryTests
    {
        [Fact]
        public void A_log_behind_an_unlistable_directory_is_reported_protected_not_absent()
        {
            using (var tmp = new TempDirectory())
            {
                var dir = Path.Combine(tmp.Path, "Rollback");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, "setupact.log");
                File.WriteAllText(logPath, "2026-09-18 12:10:17, Error SP  rollback evidence\n");

                if (!TryDenyAllAccess(dir))
                {
                    // Some environments will not let a test rewrite a DACL. Skipping beats a
                    // spurious failure, and the assertion below is meaningless without the deny.
                    return;
                }

                try
                {
                    // Precondition: this is the trap the production code has to work around.
                    Assert.False(File.Exists(logPath));

                    var source = new LogSource(
                        "setup-rollback-act", LogSourceCategory.SetupRollback,
                        "setupact.log (rollback)", "", logPath, highValue: true);

                    var entry = new LogManifestBuilder().Build(new[] { source }).Single();

                    // The point of the whole test: not silently "missing".
                    Assert.True(entry.RequiresPrivilegedRead);
                    Assert.False(entry.Readable);
                    Assert.NotNull(entry.AccessError);

                    // And the size must not be presented as a measured zero.
                    Assert.False(entry.SizeKnown);
                }
                finally
                {
                    RestoreAccess(dir);
                }
            }
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
        /// Returns true only once that is confirmed to have taken effect.
        /// </summary>
        private static bool TryDenyAllAccess(string directory)
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
                return true;
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
