using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Diagnoses the ConfigMgr client itself, rather than the upgrade it was supposed to run.
    /// <para>
    /// A broken client and a broken upgrade present almost identically — Software Center stuck,
    /// nothing deploying, the Configuration Manager applet refusing to open — and the tool could
    /// only see the second. Working the first out by hand takes a service check, a WMI provider
    /// query, a registry read and four logs, one command at a time, which is a long afternoon.
    /// </para>
    /// <para>
    /// The ordering matters and is not arbitrary: a dead WMI provider explains every other symptom
    /// downstream of it, so it has to be reported as the cause rather than alongside the things it
    /// caused.
    /// </para>
    /// </summary>
    public sealed class CcmClientAnalyzer
    {
        private const int LogWindowBytes = 4 * 1024 * 1024;

        private readonly TailReader _reader = new TailReader();

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            var health = context?.SystemState?.CcmClient;
            if (health == null)
            {
                return findings;
            }

            progress?.Report("Checking the ConfigMgr client");

            var provider = FromProvider(health);
            if (provider != null)
            {
                findings.Add(provider);
            }

            var service = FromService(health, provider != null);
            if (service != null)
            {
                findings.Add(service);
            }

            var assignment = FromAssignment(context, health, cancellationToken);
            if (assignment != null)
            {
                findings.Add(assignment);
            }

            var provisioning = FromProvisioningMode(context.SystemState, health);
            if (provisioning != null)
            {
                findings.Add(provisioning);
            }

            return findings;
        }

        private static readonly string[] UpgradeWorkers = { "TSManager", "SetupHost", "setupprep" };

        /// <summary>
        /// The client was left in provisioning mode, so Software Center offers nothing.
        /// <para>
        /// An in-place upgrade task sequence switches provisioning mode on before Windows Setup
        /// runs, and relies on a command Setup runs after a rollback to switch it off. When the
        /// rollback is forced by a crash or a power cut, that command never runs. The machine is
        /// back on Windows 10 with a client that is installed, running and registered - and that
        /// ignores every new policy, so the upgrade never reappears. Wiping WMI and reinstalling
        /// the client "fixes" it only because a new client starts with provisioning mode off.
        /// </para>
        /// </summary>
        private static Finding FromProvisioningMode(SystemState state, CcmClientHealthInfo health)
        {
            if (health.ProvisioningMode != true)
            {
                return null;
            }

            // During a live upgrade provisioning mode is supposed to be on.
            if (state?.Processes != null && UpgradeWorkers.Any(state.Processes.IsRunning))
            {
                return null;
            }

            var evidence = new List<Evidence>
            {
                new Evidence(@"Registry: HKLM\SOFTWARE\Microsoft\CCM\CcmExec", null, "ProvisioningMode = true")
            };
            if (!string.IsNullOrWhiteSpace(health.SystemTaskExcludes))
            {
                evidence.Add(new Evidence(@"Registry: HKLM\SOFTWARE\Microsoft\CCM\CcmExec", null,
                    "SystemTaskExcludes = " + health.SystemTaskExcludes));
            }
            if (state?.TaskSequenceExecutionRequest?.ExecutionRequestExists == true)
            {
                evidence.Add(new Evidence(@"WMI: root\ccm\SoftMgmtAgent", null,
                    "A task sequence execution request is still present, though no task sequence is running"));
            }
            evidence.Add(new Evidence("Running processes", null, "No task sequence or Windows Setup is running"));

            return new Finding(
                "CC-106",
                "Software Center offers nothing because the client was left in provisioning mode",
                Severity.Critical,
                Confidence.High,
                "The upgrade task sequence puts the ConfigMgr client into provisioning mode before Windows " +
                "Setup starts, and relies on Setup to switch it back off after a rollback. If the rollback was " +
                "forced by a crash (such as DRIVER_PNP_WATCHDOG) or a power cut, that step never runs. The " +
                "client is installed, running and registered, but it ignores all new policy, so the upgrade " +
                "never reappears in Software Center. Wiping WMI and reinstalling the client only works because " +
                "a new client starts with provisioning mode off.",
                "Use \"Bring the upgrade back to Software Center\" on the Tools tab. It switches provisioning " +
                "mode off, clears the leftover task sequence record, refreshes policy and then tells you whether " +
                "the upgrade is offered. No WMI rebuild or client reinstall is needed.",
                evidence,
                command: "Invoke-CimMethod -Namespace root\\ccm -ClassName SMS_Client -MethodName SetClientProvisioningMode " +
                         "-Arguments @{ bEnable = $false }");
        }

        /// <summary>
        /// The client is installed but its WMI provider will not load. This is the cause, not a
        /// symptom: the control panel applet, Software Center and ccmsetup all query root\ccm, so
        /// when it is dead they all fail at once and none of them says why.
        /// </summary>
        private static Finding FromProvider(CcmClientHealthInfo health)
        {
            if (!health.InstalledButUnresponsive)
            {
                return null;
            }

            var evidence = new List<Evidence>
            {
                new Evidence("WMI: root\\ccm", null,
                    "SELECT * FROM SMS_Client failed" +
                    (health.CcmNamespaceHResult != null ? " with " + health.CcmNamespaceHResult : "") +
                    (health.CcmNamespaceError != null ? " — " + health.CcmNamespaceError.Trim() : ""))
            };

            foreach (var ns in health.Namespaces.Where(n => !n.Value))
            {
                evidence.Add(new Evidence("WMI namespace", null, ns.Key + " is not present"));
            }

            if (health.WmiRepositoryConsistent == true)
            {
                // Worth stating: it rules out the repair everyone reaches for first.
                evidence.Add(new Evidence("WMI: root\\cimv2", null,
                    "The core WMI repository answers normally, so this is the client's own provider, " +
                    "not a broken WMI repository"));
            }

            var isProviderLoadFailure = string.Equals(
                health.CcmNamespaceHResult, "0x80041013", StringComparison.OrdinalIgnoreCase);

            return new Finding(
                "CC-100",
                "The ConfigMgr client is installed but its WMI provider will not load",
                Severity.Critical,
                Confidence.High,
                (isProviderLoadFailure
                    ? "0x80041013 is a provider load failure. "
                    : "") +
                "The client's own WMI classes under root\\ccm cannot be queried. The Configuration " +
                "Manager control panel applet, Software Center and ccmsetup itself all go through " +
                "that provider, so they fail together and none of them reports the reason. " +
                "A client in this state will also appear to start and then stop.",
                "A repair will not fix this, and neither will re-running ccmsetup: ccmsetup queries the " +
                "same broken provider and fails with the same code. The client has to be removed by hand — " +
                "services, WMI namespaces, C:\\Windows\\CCM, ccmcache, SMSCFG.ini, the registry keys and the " +
                "SMS certificate store — then reinstalled. Use \"Reinstall the ConfigMgr client\" on the Tools tab, " +
                "which does exactly that and reboots between the two halves.",
                evidence);
        }

        private static Finding FromService(CcmClientHealthInfo health, bool providerAlreadyReported)
        {
            if (!health.ClientFolderExists)
            {
                return new Finding(
                    "CC-105",
                    "The ConfigMgr client is not installed",
                    Severity.Warning,
                    Confidence.High,
                    "There is no C:\\Windows\\CCM folder, so nothing on this machine can receive a " +
                    "deployment or report to the site.",
                    "Install the client with both the site code and the management point: " +
                    "ccmsetup.exe /mp:<MP FQDN> SMSSITECODE=<SITE> SMSMP=<MP FQDN>",
                    new[] { new Evidence("File system", null, "C:\\Windows\\CCM does not exist") });
            }

            if (!health.CcmExecInstalled)
            {
                return new Finding(
                    "CC-101",
                    "The client folder exists but the SMS Agent Host service does not",
                    Severity.Critical,
                    Confidence.High,
                    "C:\\Windows\\CCM is present but CcmExec is not registered as a service, which is what " +
                    "a partially removed or partially installed client looks like.",
                    "Finish the removal and reinstall cleanly. \"Reinstall the ConfigMgr client\" on the Tools tab does both halves.",
                    new[] { new Evidence("Services", null, "CcmExec is not installed") });
            }

            if (string.Equals(health.CcmExecState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // A stopped service under a dead provider is the same fault reported twice.
            if (providerAlreadyReported)
            {
                return null;
            }

            return new Finding(
                "CC-102",
                "The SMS Agent Host service is not running",
                Severity.Critical,
                Confidence.High,
                "CcmExec is installed but currently " + (health.CcmExecState ?? "not running").ToLowerInvariant() +
                ". Nothing deploys and Software Center will not open while it is down. A service that starts " +
                "and then stops on its own usually means the client failed to initialise rather than that " +
                "somebody stopped it.",
                "Start it and watch whether it stays up: Start-Service CcmExec. If it stops again, the cause " +
                "is in C:\\Windows\\CCM\\Logs\\CcmExec.log — and a client that cannot initialise needs the full " +
                "removal and reinstall rather than a restart.",
                new[]
                {
                    new Evidence("Services", null,
                        "CcmExec state=" + (health.CcmExecState ?? "unknown") +
                        ", start mode=" + (health.CcmExecStartMode ?? "unknown"))
                });
        }

        /// <summary>
        /// The client installed and runs, but never registered. Reads the two logs that say why.
        /// </summary>
        private Finding FromAssignment(
            DiagnosticContext context, CcmClientHealthInfo health, CancellationToken cancellationToken)
        {
            if (!health.ClientFolderExists || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var evidence = new List<Evidence>();
            var noLookupMp = false;
            var refreshFailing = false;

            foreach (var log in ClientLogs(context))
            {
                IReadOnlyList<string> lines;
                try
                {
                    lines = _reader.ReadTail(log.ResolvedPath, LogWindowBytes).Lines;
                }
                catch (Exception)
                {
                    continue;
                }

                var name = Path.GetFileName(log.ResolvedPath);

                // "Assignment Site Code []" — the client asked for its site and got nothing back.
                var empty = lines.LastOrDefault(l =>
                    l.IndexOf("Assignment Site Code []", StringComparison.OrdinalIgnoreCase) >= 0);
                if (empty != null)
                {
                    noLookupMp = true;
                    evidence.Add(new Evidence(name, null, Trim(empty)));
                }

                var noMp = lines.LastOrDefault(l =>
                    l.IndexOf("Failed to get lookup MP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("No MP found in AD", StringComparison.OrdinalIgnoreCase) >= 0);
                if (noMp != null)
                {
                    noLookupMp = true;
                    evidence.Add(new Evidence(name, null, Trim(noMp)));
                }

                var refresh = lines.LastOrDefault(l =>
                    l.IndexOf("Failed to refresh site code", StringComparison.OrdinalIgnoreCase) >= 0);
                if (refresh != null)
                {
                    refreshFailing = true;
                    evidence.Add(new Evidence(name, null, Trim(refresh)));
                }
            }

            var siteMissing = string.IsNullOrWhiteSpace(health.SiteCode);

            if (!noLookupMp && !refreshFailing && !siteMissing)
            {
                return null;
            }

            if (siteMissing || refreshFailing)
            {
                evidence.Add(new Evidence("Registry: SMS\\Mobile Client", null,
                    "Assigned site code = " +
                    (siteMissing ? "(none)" : health.SiteCode)));
            }

            return new Finding(
                "CC-103",
                noLookupMp
                    ? "The client cannot find a management point, so it never finishes registering"
                    : "The client is installed but has not registered with a site",
                Severity.Critical,
                Confidence.High,
                (noLookupMp
                    ? "The client has no way to look a management point up. Active Directory is not " +
                      "published for the site and there is no DNS SRV record, so an install that only " +
                      "passed /mp: has nothing to fall back on — that switch sets where the client " +
                      "downloads from, not which management point it is assigned to. "
                    : "The client is running but has no site assignment, so it retrieves no policy and " +
                      "appears in Software Center with only a handful of actions. ") +
                "Until it registers it will retry the site-code refresh every few minutes, indefinitely, " +
                "and a reboot does not change that.",
                "Reinstall passing the management point explicitly as well as the site code:\n" +
                "ccmsetup.exe /mp:<MP FQDN> SMSSITECODE=<SITE> SMSMP=<MP FQDN>\n" +
                "SMSMP= is the part that is usually missing. Longer term, publishing the site to Active " +
                "Directory or adding the DNS SRV record removes the need for it on every install.",
                evidence);
        }

        private static IEnumerable<LogManifestEntry> ClientLogs(DiagnosticContext context)
        {
            if (context?.Manifest == null)
            {
                return new LogManifestEntry[0];
            }

            return context.Manifest
                .Where(m => m.Exists && m.Readable)
                .Where(m =>
                {
                    var name = Path.GetFileName(m.ResolvedPath);
                    return name.StartsWith("ClientIDManagerStartup", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("LocationServices", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("ClientLocation", StringComparison.OrdinalIgnoreCase);
                })
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
        }

        private static string Trim(string line)
        {
            var trimmed = (line ?? "").Trim();
            return trimmed.Length <= 400 ? trimmed : trimmed.Substring(0, 400) + "…";
        }
    }
}
