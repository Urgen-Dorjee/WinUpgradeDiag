using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Native;
using WinUpgradeDiag.Core.Rules;

namespace WinUpgradeDiag.Core.Orchestration
{
    /// <summary>
    /// Runs the pipeline: Discover → Collect → Verdict. Read-only: nothing here writes anywhere;
    /// exporting is a separate, explicit step.
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

        /// <summary>
        /// The full build identity, including the source revision the SDK appends
        /// (<c>0.1.0+&lt;40-char sha&gt;</c>). Written into reports, because when a technician sends
        /// a report back the exact commit that produced it is worth having.
        /// </summary>
        public static string ToolVersion =>
            typeof(DiagnosticRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticRunner).Assembly.GetName().Version.ToString();

        /// <summary>
        /// The same identity trimmed for display: <c>0.1.0 (f34aaba)</c>. A 40-character commit
        /// hash in a window header is noise — it tells the operator nothing and crowds out the part
        /// that does, the release number.
        /// </summary>
        public static string DisplayVersion => ShortenVersion(ToolVersion);

        internal static string ShortenVersion(string informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return "unknown";
            }

            var plus = informationalVersion.IndexOf('+');
            if (plus < 0)
            {
                return informationalVersion;
            }

            var version = informationalVersion.Substring(0, plus);
            var revision = informationalVersion.Substring(plus + 1);

            if (revision.Length == 0)
            {
                return version;
            }

            // Seven characters is the length git itself abbreviates to, and is enough to find the
            // commit again.
            var shortRevision = revision.Length > 7 ? revision.Substring(0, 7) : revision;
            return version + " (" + shortRevision + ")";
        }

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
            catch (Exception ex)
            {
                // Collection failed partway. Keep whatever was gathered rather than losing the run.
                context.CollectionFailure = ex.Message;
            }

            // The rules stage is deliberately separated. It runs over what collection produced, so
            // if it fails — or is cancelled midway through scanning a 700 MB log — the manifest and
            // system state are still worth showing. Losing them too would turn a partial answer
            // into no answer at all, which is the opposite of "degrade, never crash".
            if (!context.Cancelled)
            {
                try
                {
                    progress?.Report("Applying diagnostic rules");
                    context.Verdict = new RuleEngine().Evaluate(context, progress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    context.Cancelled = true;
                }
                catch (Exception ex)
                {
                    context.VerdictFailure = ex.GetType().Name + ": " + ex.Message;
                }
            }

            context.FinishedAtUtc = DateTime.UtcNow;
            return context;
        }
    }
}
