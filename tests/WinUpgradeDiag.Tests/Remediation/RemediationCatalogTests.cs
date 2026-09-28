using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Remediation;
using WinUpgradeDiag.Core.Rules;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// Choosing between the recovery scripts is a decision with consequences: Fix-A discards a
    /// multi-gigabyte download, Fix-B keeps it, and Fix-D deletes a folder that Remove-Leftovers
    /// should have retired instead. These lock the decision down.
    /// </summary>
    public class RemediationCatalogTests
    {
        private static DiagnosticContext Context(
            bool setupFolderExists = false,
            bool windowsOldExists = false,
            CcmCacheSnapshot cache = null)
        {
            var folders = new List<UpgradeFolderInfo>
            {
                new UpgradeFolderInfo { Path = @"C:\$WINDOWS.~BT", Exists = setupFolderExists },
                new UpgradeFolderInfo { Path = @"C:\Windows.old", Exists = windowsOldExists }
            };

            return new DiagnosticContext
            {
                SystemState = new SystemState { UpgradeFolders = folders, CcmCache = cache }
            };
        }

        private static CcmCacheSnapshot CacheWith(params CcmCacheElement[] elements)
        {
            return new CcmCacheSnapshot { Elements = elements.ToList() };
        }

        private static CcmCacheElement Element(string id, long sizeBytes, bool folderExists = true)
        {
            return new CcmCacheElement
            {
                ContentId = id,
                SizeKilobytes = sizeBytes / 1024,
                Location = @"C:\Windows\ccmcache\" + id,
                FolderExists = folderExists
            };
        }

        // ---------------------------------------------------------------- which script

        [Fact]
        public void Interrupted_during_setup_keeps_the_download_and_chooses_fix_b()
        {
            // $WINDOWS.~BT exists, so Setup had started: the image was already downloaded.
            var context = Context(setupFolderExists: true,
                                  cache: CacheWith(Element("ABC00123", 5L * 1024 * 1024 * 1024)));

            var action = RemediationCatalog.For("TS-001", context);

            Assert.Equal("FIX-B", action.Id);
            Assert.Equal("Fix-B-SetupInterrupted.ps1", action.ScriptName);
            Assert.Equal(RemediationRisk.Disruptive, action.Risk);
            Assert.True(action.IsReady);
            // The distinguishing behaviour: Fix-B keeps the multi-gigabyte download.
            Assert.Contains(action.Steps, s => s.IndexOf("cache untouched", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(action.Steps, s => s.IndexOf("Delete cache item", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Interrupted_while_downloading_chooses_fix_a_and_supplies_the_content_id()
        {
            // No $WINDOWS.~BT, but a multi-gigabyte cache item: never got past downloading.
            var context = Context(setupFolderExists: false,
                                  cache: CacheWith(
                                      Element("ABC00123", 5L * 1024 * 1024 * 1024),
                                      Element("SMALLAPP", 40L * 1024 * 1024)));

            var action = RemediationCatalog.For("TS-001", context);

            Assert.Equal("FIX-A", action.Id);
            Assert.Equal(RemediationRisk.Destructive, action.Risk);
            Assert.True(action.IsReady);

            // The whole point: the technician does not have to go and find this id by eye.
            Assert.Contains("-ContentId ABC00123", action.CommandLine);
            Assert.DoesNotContain("SMALLAPP", action.CommandLine);
        }

        [Fact]
        public void Fix_a_is_blocked_rather_than_guessed_when_the_cache_cannot_be_read()
        {
            var context = Context(setupFolderExists: false,
                                  cache: new CcmCacheSnapshot { Error = "WMI access denied" });

            var action = RemediationCatalog.For("TS-001", context);

            Assert.Equal("FIX-A", action.Id);
            Assert.False(action.IsReady);
            Assert.Null(action.CommandLine);
            // Say what is missing rather than offering a command with a wrong or empty id.
            Assert.Contains("content id", action.BlockedReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_application_sized_cache_item_is_never_mistaken_for_the_os_image()
        {
            // Only a 200 MB item cached: that is an app, not an in-place upgrade image.
            var context = Context(setupFolderExists: false,
                                  cache: CacheWith(Element("SMALLAPP", 200L * 1024 * 1024)));

            var action = RemediationCatalog.For("TS-001", context);

            Assert.False(action.IsReady);
            Assert.Contains("large enough", action.BlockedReason);
        }

        // ---------------------------------------------------------------- the dangerous one

        [Fact]
        public void Low_space_with_windows_old_present_chooses_the_rollback_aware_script()
        {
            // Windows.old means the machine may have upgraded successfully. Deleting $WINDOWS.~BT
            // alone would strip half the rollback set and leave the other half wasting space.
            var context = Context(setupFolderExists: true, windowsOldExists: true);

            var action = RemediationCatalog.For("SU-006", context);

            Assert.Equal("REMOVE-LEFTOVERS", action.Id);
            Assert.Equal("Remove-UpgradeLeftovers.ps1", action.ScriptName);
            Assert.NotEqual("Fix-D-SetupLeftovers.ps1", action.ScriptName);
            // Offered in preview mode first, because it cannot be undone.
            Assert.Contains("-WhatIf", action.CommandLine);
            Assert.Contains(action.Preconditions, p => p.Contains("Go back"));
        }

        [Fact]
        public void Low_space_without_windows_old_chooses_fix_d_and_warns_about_evidence()
        {
            var context = Context(setupFolderExists: true, windowsOldExists: false);

            var action = RemediationCatalog.For("SU-006", context);

            Assert.Equal("FIX-D", action.Id);
            Assert.Equal(RemediationRisk.Destructive, action.Risk);
            // Deleting the Setup folder destroys the logs, so export evidence first.
            Assert.Contains(action.Preconditions, p => p.IndexOf("evidence", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(action.Preconditions, p => p.Contains("Windows.old must NOT exist"));
        }

        // ---------------------------------------------------------------- general contract

        [Fact]
        public void Stale_cache_records_map_to_fix_c()
        {
            var context = Context(cache: CacheWith(Element("GONE0001", 1024L * 1024, folderExists: false)));

            var action = RemediationCatalog.For("CT-002", context);

            Assert.Equal("FIX-C", action.Id);
            Assert.Equal("Fix-C-CacheCleared.ps1", action.ScriptName);
        }

        [Fact]
        public void A_finding_with_no_scripted_fix_returns_nothing_rather_than_a_vague_one()
        {
            Assert.Null(RemediationCatalog.For("HW-001", Context()));
            Assert.Null(RemediationCatalog.For("BC-006", Context()));
        }

        [Fact]
        public void Every_action_states_its_preconditions_and_what_it_will_do()
        {
            var contexts = new[]
            {
                Context(setupFolderExists: true),
                Context(setupFolderExists: false, cache: CacheWith(Element("ABC00123", 5L * 1024 * 1024 * 1024))),
                Context(setupFolderExists: true, windowsOldExists: true),
                Context(cache: CacheWith(Element("GONE0001", 1024L * 1024, folderExists: false)))
            };
            var ids = new[] { "TS-001", "TS-001", "SU-006", "CT-002" };

            for (int i = 0; i < ids.Length; i++)
            {
                var action = RemediationCatalog.For(ids[i], contexts[i]);
                Assert.NotNull(action);
                Assert.False(string.IsNullOrWhiteSpace(action.Title));
                Assert.False(string.IsNullOrWhiteSpace(action.Summary));
                Assert.NotEmpty(action.Steps);
                Assert.False(string.IsNullOrWhiteSpace(action.ScriptName));
            }
        }

        [Fact]
        public void Every_destructive_action_names_the_live_upgrade_precondition()
        {
            // The one rule the whole script set shares: never run a fix on a live upgrade.
            var destructive = new[]
            {
                RemediationCatalog.For("TS-001", Context(setupFolderExists: true)),
                RemediationCatalog.For("TS-001", Context(cache: CacheWith(Element("A", 5L * 1024 * 1024 * 1024)))),
                RemediationCatalog.For("CT-002", Context(cache: CacheWith(Element("B", 1024L * 1024, folderExists: false)))),
                RemediationCatalog.For("SU-006", Context(setupFolderExists: true))
            };

            Assert.All(destructive, a =>
                Assert.Contains(a.Preconditions, p =>
                    p.IndexOf("TSManager", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [Fact]
        public void A_null_or_unknown_finding_is_handled_without_throwing()
        {
            Assert.Null(RemediationCatalog.For(null, Context()));
            Assert.Null(RemediationCatalog.For("TS-001", null));
            Assert.Null(RemediationCatalog.For("NOPE-999", Context()));
        }

        // ---------------------------------------------------------------- end to end

        [Fact]
        public void The_rules_engine_attaches_the_fix_to_the_finding()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    IsElevated = true,
                    Processes = new ProcessSnapshot(new List<ProcessInfo>
                    {
                        new ProcessInfo("TSManager", false, null, null),
                        new ProcessInfo("SetupHost", false, null, null)
                    }),
                    TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", "ABC20001"),
                    UpgradeFolders = new List<UpgradeFolderInfo>
                    {
                        new UpgradeFolderInfo { Path = @"C:\$WINDOWS.~BT", Exists = true }
                    },
                    PendingReboot = new PendingRebootState(),
                    SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024,
                    Os = new OsIdentity { ProductName = "Windows 10 Enterprise", CurrentBuildNumber = "19045" }
                }
            };

            var verdict = new RuleEngine().Evaluate(context);

            var orphan = verdict.Findings.Single(f => f.Id == "TS-001");
            Assert.True(orphan.HasRemediation);
            Assert.Equal("Fix-B-SetupInterrupted.ps1", orphan.Remediation.ScriptName);
        }
    }
}
