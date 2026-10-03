using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// setupapi.dev.log is the record of which driver failed, and therefore the answer to a
    /// DRIVER_PNP_WATCHDOG. The catalogue knew exactly one copy of it — the one inside the Rollback
    /// folder, which is frequently not written — so on a machine that bugchecked on a driver the
    /// tool had no driver log at all, and still told the technician to go and read it.
    /// </summary>
    public class DriverInstallAnalyzerTests
    {
        /// <summary>The shape setupapi.dev.log actually has, markers and all.</summary>
        private const string FailedDeviceInstall =
            ">>>  [Device Install (Hardware initiated) - PCI\\VEN_10EC&DEV_8168&SUBSYS_86771043&REV_15\\4&2e4d2d5e&0&00E5]\r\n" +
            ">>>  Section start 2026/09/25 22:19:41.123\r\n" +
            "     ump: Creating Install Process: DrvInst.exe 22:19:41.130\r\n" +
            "     ndv: Retrieving device info...\r\n" +
            "!!!  ndv: Device install failed for device\r\n" +
            "!!!  ndv: Error 0x800f0203: The specified driver package could not be found.\r\n" +
            "<<<  Section end 2026/09/25 22:19:45.456\r\n" +
            "<<<  [Exit status: FAILURE(0x800f0203)]\r\n";

        private const string SuccessfulDeviceInstall =
            ">>>  [Device Install (Hardware initiated) - ACPI\\PNP0C0C\\2&daba3ff&0]\r\n" +
            ">>>  Section start 2026/09/25 22:18:03.001\r\n" +
            "     ndv: Installing NULL driver.\r\n" +
            "<<<  Section end 2026/09/25 22:18:03.900\r\n" +
            "<<<  [Exit status: SUCCESS]\r\n";

        private const string FailedPackageInstall =
            ">>>  [Device Install (DiInstallDriver) - C:\\WINDOWS\\INF\\oem47.inf]\r\n" +
            ">>>  Section start 2026/09/25 22:20:11.004\r\n" +
            "!    ndv: Failed to install driver package oem47.inf: 0x800f0247\r\n" +
            "<<<  Section end 2026/09/25 22:20:13.900\r\n" +
            "<<<  [Exit status: FAILURE(0x800f0247)]\r\n";

        private static DiagnosticContext ContextFor(params LogManifestEntry[] entries)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 9, 28, 16, 11, 0, DateTimeKind.Utc),
                PrivilegedReadEnabled = true,
                Manifest = entries,
                SystemState = new SystemState { MachineName = "CM-TEST-0001", IsElevated = true }
            };
        }

        private static LogManifestEntry Entry(string path, LogSourceCategory category, string display)
        {
            var source = new LogSource("drv-" + display, category, display, "", path, highValue: true);
            return new LogManifestBuilder().Build(new[] { source }).Single();
        }

        private static IReadOnlyList<Finding> Run(DiagnosticContext context)
        {
            return new DriverInstallAnalyzer().Analyze(context, null, CancellationToken.None);
        }

        [Fact]
        public void The_device_that_failed_to_install_is_named_with_its_hardware_id()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(
                    Path.Combine("INF", "setupapi.dev.log"),
                    SuccessfulDeviceInstall + FailedDeviceInstall);

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Equal("DR-100", finding.Id);
                Assert.Equal(Severity.Critical, finding.Severity);

                // The hardware id is what identifies the device in Device Manager.
                Assert.Contains("PCI\\VEN_10EC&DEV_8168", finding.Meaning, StringComparison.Ordinal);
                Assert.Contains("PCI\\VEN_10EC&DEV_8168", finding.Action, StringComparison.Ordinal);

                // The setupapi error line is quoted, not just the exit status.
                var quoted = string.Join("\n", finding.Evidence.Select(e => e.Text));
                Assert.Contains("0x800f0203", quoted, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_successful_install_is_not_reported_as_a_failure()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("INF", "setupapi.dev.log"), SuccessfulDeviceInstall);

                Assert.Empty(Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))));
            }
        }

        /// <summary>
        /// An oem*.inf is a third-party driver package, and removing it is a concrete action a
        /// technician can take — unlike "check setupapi.dev.log".
        /// </summary>
        [Fact]
        public void A_third_party_driver_package_gets_the_command_that_removes_it()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("INF", "setupapi.dev.log"), FailedPackageInstall);

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Contains("oem47.inf", finding.Meaning, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("pnputil /delete-driver oem47.inf", finding.Action, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void The_rollback_copy_is_preferred_over_the_live_one()
        {
            using (var tmp = new TempDirectory())
            {
                var live = tmp.File(Path.Combine("INF", "setupapi.dev.log"), FailedPackageInstall);
                var rollbackCopy = tmp.File(Path.Combine("Rollback", "setupapi.dev.log"), FailedDeviceInstall);

                var finding = Run(ContextFor(
                    Entry(live, LogSourceCategory.SetupCurrent, "setupapi.dev.log (device installs)"),
                    Entry(rollbackCopy, LogSourceCategory.SetupRollback, "setupapi.dev.log (rollback)"))).Single();

                // The rollback copy describes the failed attempt directly.
                Assert.Contains("PCI\\VEN_10EC", finding.Meaning, StringComparison.Ordinal);
                Assert.DoesNotContain("oem47.inf", finding.Meaning, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Repeated_failures_for_one_device_are_reported_once()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(
                    Path.Combine("INF", "setupapi.dev.log"),
                    string.Concat(Enumerable.Repeat(FailedDeviceInstall, 6)));

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Equal("A device driver failed to install", finding.Title);
            }
        }

        /// <summary>
        /// Running this against a real machine's setupapi.dev.log returned six rows of
        /// SetupUninstallOEMInf failing with "cannot find the path specified" — Windows tidying
        /// packages that were already gone. That is normal housekeeping, and it crowded out the
        /// device install anyone would care about.
        /// </summary>
        [Fact]
        public void Uninstall_housekeeping_does_not_crowd_out_a_real_device_failure()
        {
            const string uninstallNoise =
                ">>>  [SetupUninstallOEMInf - oem126.inf]\r\n" +
                ">>>  Section start 2026/09/25 22:30:00.000\r\n" +
                "!!!  inf: Error 3: The system cannot find the path specified.\r\n" +
                "<<<  [Exit status: FAILURE(0x00000003)]\r\n";

            using (var tmp = new TempDirectory())
            {
                // The noise comes after the real failure, so "newest first" would surface it.
                var path = tmp.File(
                    Path.Combine("INF", "setupapi.dev.log"),
                    FailedDeviceInstall + string.Concat(Enumerable.Repeat(uninstallNoise, 6)));

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Contains("PCI\\VEN_10EC", finding.Meaning, StringComparison.Ordinal);

                var quoted = string.Join("\n", finding.Evidence.Select(e => e.Text));
                Assert.DoesNotContain("SetupUninstallOEMInf", quoted, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData("Device Install (Hardware initiated) - PCI\\VEN_10EC&DEV_8168", true)]
        [InlineData("Device Install (DiInstallDriver) - C:\\WINDOWS\\INF\\oem47.inf", true)]
        [InlineData("SetupUninstallOEMInf - oem126.inf", false)]
        [InlineData("Delete Device - SWD\\PRINTENUM", false)]
        public void Install_sections_are_told_apart_from_removals(string section, bool isInstall)
        {
            Assert.Equal(isInstall, DriverInstallAnalyzer.IsDeviceInstall(section));
        }

        /// <summary>
        /// This replaces a test that asserted the opposite, and the original was wrong.
        /// <para>
        /// Reasoning that "reporting something beats reporting nothing" produced, on a healthy
        /// machine, a Cause-identified verdict reading "5 device drivers failed to install" with a
        /// recommendation to run pnputil /delete-driver - built entirely out of Disk Cleanup
        /// running cleanmgr /autocleanstoragesense and removing driver packages that were already
        /// gone. That machine's log held 458 uninstall sections and 8 device installs. Silence is
        /// the correct output when there is nothing to say.
        /// </para>
        /// </summary>
        [Fact]
        public void Uninstall_housekeeping_alone_reports_nothing()
        {
            using (var tmp = new TempDirectory())
            {
                var housekeeping = string.Concat(Enumerable.Range(0, 20).Select(i =>
                    ">>>  [SetupUninstallOEMInf - oem" + (100 + i) + ".inf]\r\n" +
                    ">>>  Section start 2026/08/13 09:35:19\r\n" +
                    "      cmd: \"C:\\WINDOWS\\system32\\cleanmgr.exe\" /autocleanstoragesense /d C:\r\n" +
                    "!!!  inf: Error 3: The system cannot find the path specified.\r\n" +
                    "<<<  [Exit status: FAILURE(0x00000003)]\r\n"));

                var path = tmp.File(Path.Combine("INF", "setupapi.dev.log"), housekeeping);

                Assert.Empty(Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))));
            }
        }

        /// <summary>
        /// A device install that failed six weeks ago did not cause an upgrade to fail today, so it
        /// must not arrive as Critical with High confidence and become the verdict.
        /// </summary>
        [Fact]
        public void An_old_device_failure_is_demoted_and_labelled()
        {
            var longAgo = DateTime.Now.AddDays(-75).ToString("yyyy/MM/dd HH:mm:ss");

            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("INF", "setupapi.dev.log"),
                    ">>>  [Device Install (Hardware initiated) - PCI\\VEN_10EC&DEV_8168]\r\n" +
                    ">>>  Section start " + longAgo + "\r\n" +
                    "!!!  ndv: Device install failed for device\r\n" +
                    "<<<  [Exit status: FAILURE(0x800f0203)]\r\n");

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Equal(Severity.Warning, finding.Severity);
                Assert.Equal(Confidence.Low, finding.Confidence);
                Assert.Contains("over a month ago", finding.Title, StringComparison.Ordinal);
                Assert.Contains("history", finding.Meaning, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_evidence_says_when_each_failure_happened()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("INF", "setupapi.dev.log"), FailedDeviceInstall);

                var finding = Run(ContextFor(
                    Entry(path, LogSourceCategory.SetupCurrent, "setupapi.dev.log"))).Single();

                Assert.Contains("2026-09-25 22:19:41", finding.Evidence[0].Text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void An_unreadable_log_is_skipped_rather_than_crashing_the_run()
        {
            var missing = Entry(
                Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N"), "setupapi.dev.log"),
                LogSourceCategory.SetupCurrent, "setupapi.dev.log");

            Assert.Empty(Run(ContextFor(missing)));
        }

        /// <summary>
        /// The discovery gap this was built for: the driver log has to be found where Windows
        /// actually keeps it, not only in the Rollback folder that is often absent.
        /// </summary>
        [Fact]
        public void The_catalogue_looks_for_the_driver_log_where_Windows_keeps_it()
        {
            var sources = LogSourceCatalog.GetDefaultSources();

            // The live log, always present.
            Assert.Contains(sources, s => (s.Path ?? "").IndexOf(Path.Combine("INF", "setupapi.dev.log"),
                StringComparison.OrdinalIgnoreCase) >= 0);

            // The failed attempt's logs, searched through subfolders: Setup writes them into a
            // setupapi folder under Rollback, which a fixed path beside setupact.log never found.
            var rollback = sources.Single(s => s.Id == "setup-rollback-apilog");
            Assert.True(rollback.Recursive);
            Assert.Equal("setupapi*.log", rollback.SearchPattern);

            var current = sources.Single(s => s.Id == "setup-current-apilog");
            Assert.True(current.Recursive);
        }
    }
}
