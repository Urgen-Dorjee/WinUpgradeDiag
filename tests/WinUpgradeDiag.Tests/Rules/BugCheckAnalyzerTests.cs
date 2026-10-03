using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    /// A machine showed DRIVER_PNP_WATCHDOG on its crash screen during an upgrade, rolled back, and
    /// the tool found nothing. The name on the screen is never written to a Setup log; the crash
    /// event went into a System log the rollback discarded; the dump was noted but never read; and
    /// the driver log was looked for at the wrong path and searched only for failures a crash never
    /// writes. These tests build that machine's evidence and require the tool to name the crash and
    /// the device.
    /// </summary>
    public class BugCheckAnalyzerTests
    {
        // ---------------------------------------------------------------- dump headers

        /// <summary>A DUMP_HEADER64: "PAGE" "DU64", stop code at 0x38, parameters from 0x40.</summary>
        private static byte[] KernelDump64(uint code, params ulong[] parameters)
        {
            var header = new byte[0x2000];
            Encoding.ASCII.GetBytes("PAGE").CopyTo(header, 0);
            Encoding.ASCII.GetBytes("DU64").CopyTo(header, 4);
            BitConverter.GetBytes(code).CopyTo(header, 0x38);
            for (var i = 0; i < parameters.Length && i < 4; i++)
            {
                BitConverter.GetBytes(parameters[i]).CopyTo(header, 0x40 + i * 8);
            }
            return header;
        }

        [Fact]
        public void The_stop_code_is_read_from_a_64_bit_kernel_dump()
        {
            var info = CrashDumpReader.Read(new MemoryStream(KernelDump64(0x1D5, 0x1, 0xFFFFC00012345678, 0x3, 0x4)));

            Assert.Equal(DumpKind.Kernel64, info.Kind);
            Assert.True(info.IsBugCheck);
            Assert.Equal(0x1D5u, info.BugCheckCode);
            Assert.Equal(0xFFFFC00012345678UL, info.Parameters[1]);
        }

        [Fact]
        public void The_stop_code_is_read_from_a_32_bit_kernel_dump()
        {
            var header = new byte[0x1000];
            Encoding.ASCII.GetBytes("PAGE").CopyTo(header, 0);
            Encoding.ASCII.GetBytes("DUMP").CopyTo(header, 4);
            BitConverter.GetBytes(0x9Fu).CopyTo(header, 0x28);
            BitConverter.GetBytes(0x3u).CopyTo(header, 0x2C);

            var info = CrashDumpReader.Read(new MemoryStream(header));

            Assert.Equal(DumpKind.Kernel32, info.Kind);
            Assert.Equal(0x9Fu, info.BugCheckCode);
            Assert.Equal(3UL, info.Parameters[0]);
        }

        /// <summary>A process dump is not a crash of the machine and must not be reported as one.</summary>
        [Fact]
        public void A_user_mode_dump_is_not_mistaken_for_a_bugcheck()
        {
            var header = new byte[0x100];
            Encoding.ASCII.GetBytes("MDMP").CopyTo(header, 0);

            var info = CrashDumpReader.Read(new MemoryStream(header));

            Assert.Equal(DumpKind.UserMode, info.Kind);
            Assert.False(info.IsBugCheck);
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 2, 3 })]
        [InlineData(new byte[] { 0x50, 0x41, 0x47, 0x45, 0x58, 0x58, 0x58, 0x58, 0, 0, 0, 0 })]
        public void Anything_else_is_reported_as_unrecognised_and_never_throws(byte[] content)
        {
            var info = CrashDumpReader.Read(new MemoryStream(content));

            Assert.False(info.IsBugCheck);
            Assert.NotNull(info.Error);
        }

        [Fact]
        public void The_watchdog_stop_code_has_its_name()
        {
            Assert.Equal("DRIVER_PNP_WATCHDOG", BugCheckNames.Name(0x1D5));
            Assert.Equal("DRIVER_PNP_WATCHDOG (0x000001D5)", BugCheckNames.Describe(0x1D5));

            // Unknown codes are reported by number, never given an invented name.
            Assert.Null(BugCheckNames.Name(0xDEAD));
            Assert.Equal("stop code 0x0000DEAD", BugCheckNames.Describe(0xDEAD));
        }

        // ---------------------------------------------------------------- the event text

        [Fact]
        public void The_stop_code_is_read_from_the_BugCheck_event_text()
        {
            const string message =
                "The computer has rebooted from a bugcheck.  The bugcheck was: 0x000001d5 " +
                "(0xffffc00012345678, 0x0000000000000001, 0x0000000000000000, 0x0000000000000000). " +
                "A dump was saved in: C:\\Windows\\MEMORY.DMP.";

            Assert.Equal(0x1D5u, RollbackEventReader.ParseCode(message));

            var live = RollbackEventReader.FromLiveEvent(new EventRecordInfo
            {
                LogName = "System", EventId = 1001, Message = message,
                TimeCreatedUtc = new DateTime(2026, 9, 25, 22, 21, 30, DateTimeKind.Utc)
            });
            Assert.Equal(0x1D5u, live.Code);
        }

        /// <summary>Application error reporting also uses event 1001; only a stop code makes it a crash.</summary>
        [Fact]
        public void An_application_error_1001_is_not_a_crash()
        {
            Assert.Null(RollbackEventReader.FromLiveEvent(new EventRecordInfo
            {
                LogName = "Application", EventId = 1001, Message = "Fault bucket 2111525315961788241, type 5"
            }));
        }

        // ---------------------------------------------------------------- the cut-off install

        private const string CutOffInstall =
            "     >>>  Section end 2026/09/25 22:18:59.010\r\n" +
            ">>>  [Device Install (Hardware initiated) - PCI\\VEN_10EC&DEV_8168&SUBSYS_86771043&REV_15\\4&2e4d2d5e&0&00E5]\r\n" +
            ">>>  Section start 2026/09/25 22:19:41.123\r\n" +
            "     ump: Creating Install Process: DrvInst.exe 22:19:41.130\r\n" +
            "     ndv: Retrieving device info...\r\n" +
            "     dvi: Starting device...\r\n" +
            "[Boot Session: 2026/09/25 22:21:05.500]\r\n" +
            ">>>  [Device Install (Hardware initiated) - ACPI\\PNP0C0C\\2&daba3ff&0]\r\n" +
            ">>>  Section start 2026/09/25 22:21:10.001\r\n" +
            "<<<  Section end 2026/09/25 22:21:10.900\r\n" +
            "<<<  [Exit status: SUCCESS]\r\n";

        [Fact]
        public void An_install_cut_off_by_a_restart_is_found()
        {
            var found = DriverInstallAnalyzer.FindInterrupted(CutOffInstall.Split(new[] { "\r\n" }, StringSplitOptions.None));

            var cut = Assert.Single(found);
            Assert.Contains("PCI\\VEN_10EC&DEV_8168", cut.Section, StringComparison.Ordinal);
            Assert.NotNull(cut.StartedUtc);
            Assert.NotNull(cut.NextBootUtc);
            Assert.True(cut.NextBootUtc > cut.StartedUtc);

            Assert.Equal("PCI\\VEN_10EC&DEV_8168&SUBSYS_86771043&REV_15\\4&2e4d2d5e&0&00E5",
                DriverInstallAnalyzer.HardwareIdOf(cut.Section));
        }

        [Fact]
        public void A_finished_install_is_not_reported_as_cut_off()
        {
            var lines = new[]
            {
                ">>>  [Device Install (Hardware initiated) - PCI\\VEN_8086&DEV_1234]",
                ">>>  Section start 2026/09/25 22:00:00.000",
                "<<<  Section end 2026/09/25 22:00:02.000",
                "<<<  [Exit status: SUCCESS]",
                "[Boot Session: 2026/09/25 22:05:00.500]"
            };

            Assert.Empty(DriverInstallAnalyzer.FindInterrupted(lines));
        }

        /// <summary>Open at the end of the file means in progress now, not cut off.</summary>
        [Fact]
        public void An_install_still_running_is_not_reported_as_cut_off()
        {
            var lines = new[]
            {
                ">>>  [Device Install (Hardware initiated) - PCI\\VEN_8086&DEV_1234]",
                ">>>  Section start 2026/09/25 22:00:00.000",
                "     dvi: Starting device..."
            };

            Assert.Empty(DriverInstallAnalyzer.FindInterrupted(lines));
        }

        [Fact]
        public void A_cut_off_uninstall_is_not_a_device_install()
        {
            var lines = new[]
            {
                ">>>  [SetupUninstallOEMInf - oem126.inf]",
                ">>>  Section start 2026/08/13 09:35:19.250",
                "[Boot Session: 2026/08/13 09:40:00.500]"
            };

            Assert.Empty(DriverInstallAnalyzer.FindInterrupted(lines));
        }

        // ---------------------------------------------------------------- the whole machine

        /// <summary>
        /// Builds the failed attempt as Setup leaves it - a dump and the driver logs in a setupapi
        /// subfolder of Rollback - and requires discovery to find them through the real catalogue
        /// shape, and the engine to name the crash and the device.
        /// </summary>
        [Fact]
        public void A_watchdog_crash_is_named_with_the_device_that_caused_it()
        {
            using (var tmp = new TempDirectory())
            {
                var rollback = Path.Combine(tmp.Path, "$WINDOWS.~BT", "Sources", "Rollback");
                Directory.CreateDirectory(Path.Combine(rollback, "setupapi"));

                var log = Path.Combine(rollback, "setupapi", "setupapi.dev.log");
                File.WriteAllText(log, CutOffInstall);

                var dump = Path.Combine(rollback, "setupmem.dmp");
                File.WriteAllBytes(dump, KernelDump64(0x1D5, 0x1, 0x2, 0x3, 0x4));

                // The dump is written at the boot after the crash.
                var nextBoot = DateTime.ParseExact("2026/09/25 22:21:05", "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).ToUniversalTime();
                File.SetLastWriteTimeUtc(dump, nextBoot.AddSeconds(31));

                var sources = new[]
                {
                    new LogSource("rb-api", LogSourceCategory.SetupRollback, "setupapi logs (rollback)", "",
                        rollback, LogSourceKind.DirectoryGlob, "setupapi*.log", highValue: true, recursive: true),
                    new LogSource("rb-dmp", LogSourceCategory.SetupRollback, "setupmem.dmp (rollback)", "",
                        dump, highValue: true)
                };

                var context = new DiagnosticContext
                {
                    ToolVersion = "0.1.0-test",
                    StartedAtUtc = new DateTime(2026, 9, 28, 16, 11, 0, DateTimeKind.Utc),
                    PrivilegedReadEnabled = true,
                    Manifest = new LogManifestBuilder().Build(sources),
                    SystemState = new SystemState { MachineName = "CM-TEST-0001", IsElevated = true }
                };

                // Discovery reached into the subfolder.
                Assert.Contains(context.Manifest, m => m.Exists &&
                    m.ResolvedPath.EndsWith(Path.Combine("setupapi", "setupapi.dev.log"), StringComparison.OrdinalIgnoreCase));

                var finding = new BugCheckAnalyzer()
                    .Analyze(context, null, CancellationToken.None)
                    .Single();

                Assert.Equal("BC-100", finding.Id);
                Assert.Equal(Severity.Critical, finding.Severity);
                Assert.Equal(Confidence.High, finding.Confidence);
                Assert.Contains("DRIVER_PNP_WATCHDOG", finding.Title, StringComparison.Ordinal);
                Assert.Contains("PCI\\VEN_10EC&DEV_8168", finding.Title, StringComparison.Ordinal);
                Assert.Contains("never finished", finding.Meaning, StringComparison.Ordinal);
                Assert.Contains("Device Manager", finding.Action, StringComparison.Ordinal);

                // And through the real engine it becomes the verdict.
                var verdict = new RuleEngine().Evaluate(context);
                Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);
                Assert.Contains("DRIVER_PNP_WATCHDOG", verdict.Headline, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A dump with no matching cut-off install still confirms the crash, but says plainly that
        /// the logs do not name the driver rather than guessing one.
        /// </summary>
        [Fact]
        public void A_crash_without_a_cut_off_install_says_where_the_driver_is_named()
        {
            using (var tmp = new TempDirectory())
            {
                var dump = Path.Combine(tmp.Path, "setupmem.dmp");
                File.WriteAllBytes(dump, KernelDump64(0x1D5));

                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[]
                    {
                        new LogSource("rb-dmp", LogSourceCategory.SetupRollback, "setupmem.dmp", "", dump)
                    }),
                    SystemState = new SystemState()
                };

                var finding = new BugCheckAnalyzer().Analyze(context, null, CancellationToken.None).Single();

                Assert.Equal(Confidence.Medium, finding.Confidence);
                Assert.Contains("not named by the logs", finding.Meaning, StringComparison.Ordinal);
                Assert.Contains("!analyze -v", finding.Action, StringComparison.Ordinal);
            }
        }

        /// <summary>An install cut off on another day explains another restart, not this crash.</summary>
        [Fact]
        public void A_cut_off_install_on_a_different_day_is_not_tied_to_the_crash()
        {
            using (var tmp = new TempDirectory())
            {
                var log = tmp.File(Path.Combine("setupapi", "setupapi.dev.log"), CutOffInstall);
                var dump = Path.Combine(tmp.Path, "MEMORY.DMP");
                File.WriteAllBytes(dump, KernelDump64(0x1D5));
                File.SetLastWriteTimeUtc(dump, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[]
                    {
                        new LogSource("api", LogSourceCategory.SetupCurrent, "setupapi.dev.log", "", log),
                        new LogSource("dmp", LogSourceCategory.CrashDump, "MEMORY.DMP", "", dump)
                    }),
                    SystemState = new SystemState()
                };

                var finding = new BugCheckAnalyzer().Analyze(context, null, CancellationToken.None).Single();

                Assert.DoesNotContain("PCI\\VEN_10EC", finding.Title, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_crash_evidence_and_no_cut_off_install_reports_nothing()
        {
            using (var tmp = new TempDirectory())
            {
                var log = tmp.File("setupapi.dev.log",
                    ">>>  [Device Install (Hardware initiated) - ACPI\\PNP0C0C\\2&daba3ff&0]\r\n" +
                    "<<<  [Exit status: SUCCESS]\r\n");

                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[]
                    {
                        new LogSource("api", LogSourceCategory.SetupCurrent, "setupapi.dev.log", "", log)
                    }),
                    SystemState = new SystemState()
                };

                Assert.Empty(new BugCheckAnalyzer().Analyze(context, null, CancellationToken.None));
            }
        }

        /// <summary>
        /// With no stop code anywhere, a cut-off install inside the failed attempt is still reported
        /// - as a suspect, not a cause.
        /// </summary>
        [Fact]
        public void A_cut_off_install_in_the_failed_attempt_is_reported_as_a_suspect()
        {
            using (var tmp = new TempDirectory())
            {
                var log = tmp.File(Path.Combine("$WINDOWS.~BT", "Sources", "Rollback", "setupapi", "setupapi.dev.log"),
                    CutOffInstall);

                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[]
                    {
                        new LogSource("api", LogSourceCategory.SetupRollback, "setupapi.dev.log", "", log)
                    }),
                    SystemState = new SystemState()
                };

                var finding = new BugCheckAnalyzer().Analyze(context, null, CancellationToken.None).Single();

                Assert.Equal("DR-101", finding.Id);
                Assert.Equal(Severity.Warning, finding.Severity);
                Assert.Contains("PCI\\VEN_10EC&DEV_8168", finding.Title, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_catalogue_searches_the_rollback_folder_and_its_subfolders_for_driver_logs()
        {
            var rollbackApi = LogSourceCatalog.GetDefaultSources().Single(s => s.Id == "setup-rollback-apilog");

            Assert.Equal(LogSourceKind.DirectoryGlob, rollbackApi.Kind);
            Assert.True(rollbackApi.Recursive);
            Assert.EndsWith("Rollback", rollbackApi.Path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
