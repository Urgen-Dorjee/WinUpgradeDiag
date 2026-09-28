using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using WinUpgradeDiag.Core.Collect;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>Why a tool was refused, or that it was allowed.</summary>
    public enum PreflightResult
    {
        Allowed,

        /// <summary>An upgrade is running right now. Nothing that changes the machine may proceed.</summary>
        BlockedUpgradeLive,

        /// <summary>The script file is not where it should be.</summary>
        BlockedScriptMissing,

        /// <summary>A required parameter was not supplied.</summary>
        BlockedMissingParameter,

        /// <summary>The session is not elevated and the script needs it.</summary>
        BlockedNotElevated
    }

    /// <summary>Outcome of the checks run immediately before launching a tool.</summary>
    public sealed class PreflightReport
    {
        public PreflightReport(PreflightResult result, string message, string scriptPath, IReadOnlyList<string> observations)
        {
            Result = result;
            Message = message;
            ScriptPath = scriptPath;
            Observations = observations ?? new List<string>();
        }

        public PreflightResult Result { get; }
        public string Message { get; }
        public string ScriptPath { get; }

        /// <summary>What the check actually saw, recorded in the audit log.</summary>
        public IReadOnlyList<string> Observations { get; }

        public bool IsAllowed => Result == PreflightResult.Allowed;
    }

    /// <summary>What happened when a tool ran.</summary>
    public sealed class ToolRunResult
    {
        public ToolRunResult(bool started, int? exitCode, string output, string auditLogPath, string error)
        {
            Started = started;
            ExitCode = exitCode;
            Output = output;
            AuditLogPath = auditLogPath;
            Error = error;
        }

        public bool Started { get; }
        public int? ExitCode { get; }
        public string Output { get; }
        public string AuditLogPath { get; }
        public string Error { get; }

        public bool Succeeded => Started && ExitCode == 0;
    }

    /// <summary>
    /// Runs a recovery script under gates.
    /// <para>
    /// This is the one part of the tool that changes the machine, so it is deliberately the most
    /// constrained. Before anything launches it re-checks that no upgrade is in flight — the
    /// scripts check that once at their own start, but a fix takes over a minute to run and
    /// nothing re-examines it, so an upgrade resuming mid-fix would turn a recoverable machine
    /// into a broken one. Every run, allowed or refused, is written to an audit log beside the
    /// diagnostic output.
    /// </para>
    /// </summary>
    public sealed class RemediationRunner
    {
        private readonly ProcessCollector _processes = new ProcessCollector();

        /// <summary>How long to let a tool run before giving up on it.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

        /// <summary>
        /// Re-checks, at this instant, whether the tool may run. Call it immediately before
        /// launching — the value of the check is that it is fresh.
        /// </summary>
        public PreflightReport Preflight(ToolDefinition tool, string scriptFolder, string parameterValue)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            var observations = new List<string>();

            // The embedded copy is the default source, so a missing or wrong folder on disk is no
            // longer a reason to refuse: the scripts travel inside the signed assembly.
            var embedded = EmbeddedScriptProvider.Contains(tool.ScriptName);
            var diskPath = string.IsNullOrWhiteSpace(scriptFolder)
                ? null
                : Path.Combine(scriptFolder, tool.ScriptName);
            var onDisk = diskPath != null && File.Exists(diskPath);

            if (!embedded && !onDisk)
            {
                return new PreflightReport(
                    PreflightResult.BlockedScriptMissing,
                    tool.ScriptName + " is neither embedded in this build nor present in " +
                    (scriptFolder ?? "any configured folder") + ".",
                    null, observations);
            }

            var scriptPath = embedded ? "(embedded) " + tool.ScriptName : diskPath;
            observations.Add(embedded
                ? "Source: embedded in the signed application, SHA-256 " + EmbeddedScriptProvider.HashOf(tool.ScriptName)
                : "Source: " + diskPath + " (on disk; integrity not verified)");

            if (embedded && onDisk && !EmbeddedScriptProvider.MatchesEmbedded(tool.ScriptName, diskPath))
            {
                // Worth recording: the operator's folder holds a different version from the one this
                // build was tested against. The embedded copy still wins.
                observations.Add("Note: the copy in " + scriptFolder +
                                 " differs from the embedded one; the embedded copy is used.");
            }

            if (tool.RequiresParameter && string.IsNullOrWhiteSpace(parameterValue))
            {
                return new PreflightReport(
                    PreflightResult.BlockedMissingParameter,
                    tool.ParameterPrompt + " is required before this can run.",
                    scriptPath, observations);
            }

            if (tool.Risk != RemediationRisk.ReadOnly && !IsElevated())
            {
                return new PreflightReport(
                    PreflightResult.BlockedNotElevated,
                    "This tool changes the machine and needs an elevated session. Restart as administrator.",
                    scriptPath, observations);
            }

            if (tool.BlockedByLiveUpgrade && tool.RequiresLocalMachine)
            {
                var snapshot = _processes.Collect();
                var live = new List<string>();
                foreach (var name in new[] { "TSManager", "SetupHost", "setupprep" })
                {
                    if (snapshot.IsRunning(name))
                    {
                        live.Add(name);
                    }
                }

                if (live.Count > 0)
                {
                    return new PreflightReport(
                        PreflightResult.BlockedUpgradeLive,
                        "An upgrade is running on this machine right now (" + string.Join(", ", live) + "). " +
                        "Running this would break a healthy upgrade. Wait for it to finish.",
                        scriptPath,
                        new List<string> { "Live upgrade processes: " + string.Join(", ", live) });
                }

                observations.Add("No upgrade processes running at " +
                                 DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + ".");
            }

            return new PreflightReport(PreflightResult.Allowed, null, scriptPath, observations);
        }

        /// <summary>
        /// Runs the tool, capturing its output. <paramref name="preflight"/> must be an allowed
        /// report obtained moments earlier — passing it in rather than re-deriving it keeps the
        /// decision and the action auditable as one event.
        /// </summary>
        public ToolRunResult Run(
            ToolDefinition tool,
            PreflightReport preflight,
            string parameterValue,
            string auditFolder,
            IProgress<string> output = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }
            if (preflight == null)
            {
                throw new ArgumentNullException(nameof(preflight));
            }

            var transcript = new StringBuilder();
            var startedAt = DateTime.Now;

            if (!preflight.IsAllowed)
            {
                var refusedPath = WriteAudit(tool, preflight, parameterValue, auditFolder, startedAt,
                    "REFUSED: " + preflight.Message, null, null);
                return new ToolRunResult(false, null, preflight.Message, refusedPath, preflight.Message);
            }

            // Materialise the script that will actually run. Embedded is preferred; the disk copy
            // is only a fallback for a build that does not carry it.
            PreparedScript script;
            try
            {
                script = EmbeddedScriptProvider.Contains(tool.ScriptName)
                    ? EmbeddedScriptProvider.Extract(tool.ScriptName)
                    : EmbeddedScriptProvider.FromDisk(preflight.ScriptPath);
            }
            catch (Exception ex)
            {
                var failure = "Could not prepare " + tool.ScriptName + ": " + ex.Message;
                var failPath = WriteAudit(tool, preflight, parameterValue, auditFolder, startedAt, failure, null, null);
                return new ToolRunResult(false, null, failure, failPath, failure);
            }

            if (script == null)
            {
                var missing = tool.ScriptName + " could not be prepared for execution.";
                var missPath = WriteAudit(tool, preflight, parameterValue, auditFolder, startedAt, missing, null, null);
                return new ToolRunResult(false, null, missing, missPath, missing);
            }

            using (script)
            {
            var arguments = BuildArguments(script.Path, tool, parameterValue);

            try
            {
                var info = new ProcessStartInfo("powershell.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(script.Path) ?? Environment.CurrentDirectory
                };

                using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
                {
                    DataReceivedEventHandler onData = (s, e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }
                        lock (transcript)
                        {
                            transcript.AppendLine(e.Data);
                        }
                        output?.Report(e.Data);
                    };

                    process.OutputDataReceived += onData;
                    process.ErrorDataReceived += onData;

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // A streaming tool has no natural end, so a timeout would just be an arbitrary
                    // kill. Those run until the operator cancels; everything else is bounded.
                    var deadline = tool.RunsUntilStopped ? (DateTime?)null : DateTime.UtcNow + DefaultTimeout;

                    while (!process.HasExited)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            TryKill(process, transcript, output, "Stopped by the operator.");
                            break;
                        }
                        if (deadline.HasValue && DateTime.UtcNow > deadline.Value)
                        {
                            TryKill(process, transcript, output,
                                "Timed out after " + DefaultTimeout.TotalMinutes + " minutes.");
                            break;
                        }
                        process.WaitForExit(250);
                    }

                    int? exitCode = null;
                    if (process.HasExited)
                    {
                        process.WaitForExit();       // flush the async output readers
                        exitCode = process.ExitCode;
                    }

                    var text = transcript.ToString();
                    var auditPath = WriteAudit(tool, preflight, parameterValue, auditFolder, startedAt,
                        text, exitCode, script);
                    return new ToolRunResult(true, exitCode, text, auditPath, null);
                }
            }
            catch (Exception ex)
            {
                var message = "Could not run " + tool.ScriptName + ": " + ex.Message;
                lock (transcript)
                {
                    transcript.AppendLine(message);
                }
                var auditPath = WriteAudit(tool, preflight, parameterValue, auditFolder, startedAt,
                    transcript.ToString(), null, script);
                return new ToolRunResult(false, null, transcript.ToString(), auditPath, message);
            }
            }
        }

        private static void TryKill(Process process, StringBuilder transcript, IProgress<string> output, string why)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // It may have exited between the check and the kill; nothing to do.
            }

            lock (transcript)
            {
                transcript.AppendLine(why);
            }
            output?.Report(why);
        }

        /// <summary>
        /// Builds the PowerShell command line. Public so the argument handling can be tested
        /// directly — this is the boundary where an operator-supplied value meets a shell, and
        /// it is worth proving a value cannot escape its own parameter.
        /// </summary>
        public static string BuildArguments(string scriptPath, ToolDefinition tool, string parameterValue)
        {
            var arguments = new StringBuilder();
            arguments.Append("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"")
                     .Append(scriptPath).Append('"');

            if (tool.RequiresParameter && !string.IsNullOrWhiteSpace(parameterValue))
            {
                // Quote the value and strip quotes from it, so a parameter can never terminate the
                // argument and append something of its own.
                var safe = parameterValue.Replace("\"", string.Empty).Trim();
                arguments.Append(" -").Append(tool.ParameterName).Append(" \"").Append(safe).Append('"');
            }

            return arguments.ToString();
        }

        /// <summary>
        /// Records what was run, by whom, against what state, and what came back. A remediation
        /// tool that changes a clinical endpoint without leaving a record is not defensible after
        /// the fact, whether it worked or not.
        /// </summary>
        private static string WriteAudit(
            ToolDefinition tool,
            PreflightReport preflight,
            string parameterValue,
            string auditFolder,
            DateTime startedAt,
            string transcript,
            int? exitCode,
            PreparedScript script)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(auditFolder))
                {
                    return null;
                }

                Directory.CreateDirectory(auditFolder);
                var fileName = "action_" + startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                               "_" + tool.Id + ".log";
                var path = Path.Combine(auditFolder, fileName);

                var log = new StringBuilder();
                log.AppendLine("WinUpgradeDiag remediation audit");
                log.AppendLine("================================");
                log.AppendLine("Tool          : " + tool.Id + " (" + tool.ScriptName + ")");
                log.AppendLine("Title         : " + tool.Title);
                log.AppendLine("Risk          : " + tool.RiskText);
                log.AppendLine("Started       : " + startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                log.AppendLine("Machine       : " + Environment.MachineName);
                log.AppendLine("Operator      : " + Environment.UserDomainName + "\\" + Environment.UserName);
                log.AppendLine("Elevated      : " + IsElevated());
                log.AppendLine("Script path   : " + (preflight.ScriptPath ?? "(not resolved)"));
                if (script != null)
                {
                    log.AppendLine("Script source : " + script.OriginText);
                    log.AppendLine("Script SHA256 : " + (script.Sha256 ?? "(not computed)"));
                    log.AppendLine("Ran from      : " + script.Path);
                }
                if (tool.RequiresParameter)
                {
                    log.AppendLine("Parameter     : -" + tool.ParameterName + " " + (parameterValue ?? "(none)"));
                }
                log.AppendLine("Preflight     : " + preflight.Result);
                foreach (var observation in preflight.Observations)
                {
                    log.AppendLine("  observed    : " + observation);
                }
                log.AppendLine("Exit code     : " + (exitCode.HasValue ? exitCode.Value.ToString(CultureInfo.InvariantCulture) : "(did not run)"));
                log.AppendLine("Finished      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                log.AppendLine();
                log.AppendLine("--- transcript ---");
                log.AppendLine(transcript ?? string.Empty);

                File.WriteAllText(path, log.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                // Failing to write the audit must not also fail the run; the caller still gets the
                // transcript on screen.
                return null;
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(identity)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
