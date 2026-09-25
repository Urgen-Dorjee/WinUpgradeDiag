using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Native;

namespace WinUpgradeDiag.Core.Orchestration
{
    /// <summary>
    /// Runs the phase 1 pipeline: Discover → Collect. Parse, Correlate and Verdict arrive in
    /// phase 2. Read-only: nothing here writes anywhere; exporting is a separate, explicit step.
    /// </summary>
    public sealed class DiagnosticRunner
    {
        private readonly Func<IReadOnlyList<LogSource>> _sourceProvider;

        public DiagnosticRunner() : this(LogSourceCatalog.GetDefaultSources)
        {
        }

        public DiagnosticRunner(Func<IReadOnlyList<LogSource>> sourceProvider)
        {
            _sourceProvider = sourceProvider;
        }

        public static string ToolVersion =>
            typeof(DiagnosticRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticRunner).Assembly.GetName().Version.ToString();

        public DiagnosticContext Run(IProgress<string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var context = new DiagnosticContext
            {
                ToolVersion = ToolVersion,
                StartedAtUtc = DateTime.UtcNow
            };

            try
            {
                progress?.Report("Enabling backup privilege for protected logs");
                string privilegeError;
                context.PrivilegedReadEnabled = SeBackupPrivilege.TryEnable(out privilegeError);
                context.PrivilegedReadError = privilegeError;
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report("Discovering log locations");
                var sources = _sourceProvider();
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report("Building log manifest");
                context.Manifest = new LogManifestBuilder().Build(sources);
                cancellationToken.ThrowIfCancellationRequested();

                context.SystemState = new SystemStateCollector().Collect(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                context.Cancelled = true;
            }
            finally
            {
                context.FinishedAtUtc = DateTime.UtcNow;
            }

            return context;
        }
    }
}
