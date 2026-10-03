using System;
using System.Collections.Generic;
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
    /// Since Windows 10 2004, Setup runs Microsoft's SetupDiag when an upgrade fails and saves the
    /// result on the machine. The tool never read it. These tests use a file shaped like Microsoft's
    /// documented example, namespace and all, plus a driver element.
    /// </summary>
    public class SetupDiagAnalyzerTests
    {
        private const string Results =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<SetupDiag xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"https://docs.microsoft.com/windows/deployment/upgrade/setupdiag\">\n" +
            "  <Version>1.7.0.0</Version>\n" +
            "  <ProfileName>DriverInstallFailure</ProfileName>\n" +
            "  <ProfileGuid>9F7E5E3D-0000-0000-0000-000000000000</ProfileGuid>\n" +
            "  <SystemInfo>\n" +
            "    <Manufacturer>HP</Manufacturer>\n" +
            "    <FilterDrivers>WdFilter,wcifs,secRMMfilter,luafv,</FilterDrivers>\n" +
            "  </SystemInfo>\n" +
            "  <FailureData>\n" +
            "    <ExtendedErrorCode>0x30018</ExtendedErrorCode>\n" +
            "    <ErrorCode>0xC1900101</ErrorCode>\n" +
            "    <Message>Error: SetupDiag reports driver install failure.</Message>\n" +
            "    <Message>Last change: Device Install for PCI\\VEN_10EC&amp;DEV_8168 failed.</Message>\n" +
            "  </FailureData>\n" +
            "  <FailureDetails>Err = 0xC1900101 - 0x30018, LastPhase = SECOND_BOOT</FailureDetails>\n" +
            "  <DeviceDriverInfo>\n" +
            "    <HardwareId>PCI\\VEN_10EC&amp;DEV_8168</HardwareId>\n" +
            "    <InfName>oem47.inf</InfName>\n" +
            "  </DeviceDriverInfo>\n" +
            "  <Remediation>\n" +
            "    <RemediationInfo>Update or remove the driver for the device named above.</RemediationInfo>\n" +
            "  </Remediation>\n" +
            "</SetupDiag>\n";

        private static SetupDiagResult Parse(string xml)
        {
            return SetupDiagResultsReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        }

        [Fact]
        public void The_conclusion_is_read_despite_the_namespace()
        {
            var r = Parse(Results);

            Assert.Equal("DriverInstallFailure", r.ProfileName);
            Assert.Equal("0xC1900101", r.ErrorCode);
            Assert.Equal("0x30018", r.ExtendedErrorCode);
            Assert.Equal(2, r.Messages.Count);
            Assert.Contains("SECOND_BOOT", r.FailureDetails, StringComparison.Ordinal);
            Assert.Contains(r.Remediation, x => x.Contains("Update or remove"));
        }

        /// <summary>
        /// The driver SetupDiag blames is collected; the inventory of every filter driver on the
        /// machine, under SystemInfo, is not - it is not a finding.
        /// </summary>
        [Fact]
        public void The_blamed_driver_is_picked_out_and_the_inventory_is_not()
        {
            var r = Parse(Results);

            Assert.Contains(r.DriverLines, l => l.Contains("PCI\\VEN_10EC&DEV_8168"));
            Assert.Contains(r.DriverLines, l => l.Contains("oem47.inf"));
            Assert.DoesNotContain(r.DriverLines, l => l.Contains("WdFilter"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not xml at all")]
        [InlineData("<SetupDiag><ProfileName>unterminated")]
        public void A_damaged_file_yields_nothing_rather_than_throwing(string xml)
        {
            Assert.Null(Parse(xml));
        }

        /// <summary>The file is read with DTDs refused, so it cannot be used to read other files.</summary>
        [Fact]
        public void A_file_carrying_a_DTD_is_refused()
        {
            const string hostile =
                "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>" +
                "<SetupDiag><ProfileName>&e;</ProfileName></SetupDiag>";

            Assert.Null(Parse(hostile));
        }

        [Fact]
        public void The_registry_copy_is_read_the_same_way()
        {
            var r = SetupDiagResultsReader.FromValues(new Dictionary<string, string>
            {
                { "ProfileName", "DriverInstallFailure" },
                { "FailureData", "Error: driver install failure.\nLast change: PCI\\VEN_10EC" },
                { "FailureDetails", "Err = 0xC1900101 - 0x30018" },
                { "HardwareId", "PCI\\VEN_10EC&DEV_8168" },
                { "Remediation", "Update the driver." }
            });

            Assert.True(r.HasConclusion);
            Assert.Equal(2, r.Messages.Count);
            Assert.Contains(r.DriverLines, l => l.StartsWith("HardwareId", StringComparison.Ordinal));
            Assert.Single(r.Remediation);
        }

        [Fact]
        public void A_saved_result_becomes_a_finding_with_Microsofts_wording()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("Logs", "SetupDiag", "SetupDiagResults.xml"), Results);
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 25, 22, 40, 0, DateTimeKind.Utc));

                var context = ContextWith(path, new DateTime(2026, 9, 28, 16, 0, 0, DateTimeKind.Utc));
                var finding = new SetupDiagAnalyzer().Analyze(context, null, CancellationToken.None).Single();

                Assert.Equal("SD-100", finding.Id);
                Assert.Equal(Severity.Critical, finding.Severity);
                Assert.Equal(Confidence.High, finding.Confidence);
                Assert.Contains("Driver install failure", finding.Title, StringComparison.Ordinal);
                Assert.Contains("Update or remove the driver", finding.Action, StringComparison.Ordinal);

                var quoted = string.Join("\n", finding.Evidence.Select(e => e.Text));
                Assert.Contains("0xC1900101", quoted, StringComparison.Ordinal);
                Assert.Contains("PCI\\VEN_10EC&DEV_8168", quoted, StringComparison.Ordinal);
            }
        }

        /// <summary>A result from months ago describes some other attempt and must not lead.</summary>
        [Fact]
        public void An_old_result_is_labelled_and_demoted()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File("SetupDiagResults.xml", Results);
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc));

                var finding = new SetupDiagAnalyzer()
                    .Analyze(ContextWith(path, new DateTime(2026, 9, 28, 16, 0, 0, DateTimeKind.Utc)), null, CancellationToken.None)
                    .Single();

                Assert.Equal(Severity.Warning, finding.Severity);
                Assert.Contains("earlier attempt", finding.Title, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_registry_copy_stands_in_when_the_file_is_gone()
        {
            var context = new DiagnosticContext
            {
                StartedAtUtc = new DateTime(2026, 9, 28, 16, 0, 0, DateTimeKind.Utc),
                Manifest = new List<LogManifestEntry>(),
                SystemState = new SystemState
                {
                    SetupDiag = SetupDiagResultsReader.FromValues(new Dictionary<string, string>
                    {
                        { "ProfileName", "BootFailureDetected" },
                        { "FailureData", "Error: boot failure detected." }
                    })
                }
            };

            var finding = new SetupDiagAnalyzer().Analyze(context, null, CancellationToken.None).Single();
            Assert.Contains("Boot failure detected", finding.Title, StringComparison.Ordinal);
        }

        [Fact]
        public void No_result_anywhere_reports_nothing()
        {
            var context = new DiagnosticContext
            {
                Manifest = new List<LogManifestEntry>(),
                SystemState = new SystemState()
            };

            Assert.Empty(new SetupDiagAnalyzer().Analyze(context, null, CancellationToken.None));
        }

        [Theory]
        [InlineData("DriverInstallFailure", "Driver install failure")]
        [InlineData("BootFailureDetected", "Boot failure detected")]
        [InlineData("DiskSpaceBlockInDownLevel", "Disk space block in down level")]
        public void Rule_names_read_as_words(string profile, string expected)
        {
            Assert.Equal(expected, SetupDiagAnalyzer.Humanise(profile));
        }

        [Fact]
        public void Discovery_looks_where_Windows_saves_the_result()
        {
            Assert.Contains(LogSourceCatalog.GetDefaultSources(), s =>
                (s.Path ?? "").EndsWith(Path.Combine("Logs", "SetupDiag", "SetupDiagResults.xml"),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static DiagnosticContext ContextWith(string resultsPath, DateTime runUtc)
        {
            return new DiagnosticContext
            {
                StartedAtUtc = runUtc,
                Manifest = new LogManifestBuilder().Build(new[]
                {
                    new LogSource("setupdiag", LogSourceCategory.SetupRollback, "SetupDiag results", "", resultsPath)
                }),
                SystemState = new SystemState()
            };
        }
    }
}
