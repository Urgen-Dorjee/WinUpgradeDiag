using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;
using WinUpgradeDiag.Core.Rules;

namespace WinUpgradeDiag.Core.Report
{
    /// <summary>
    /// Flattens a <see cref="DiagnosticContext"/> into plain dictionaries/lists with ISO-8601
    /// timestamps, applying redaction to every string on the way. Both the JSON and HTML writers
    /// consume this, so redaction happens in exactly one place.
    /// </summary>
    public static class ReportModelBuilder
    {
        public const int SchemaVersion = 1;

        public static Dictionary<string, object> Build(DiagnosticContext context, Redactor redactor)
        {
            Func<string, string> r = s => redactor == null ? s : redactor.Redact(s);
            var state = context.SystemState ?? new SystemState();

            return new Dictionary<string, object>
            {
                ["schemaVersion"] = SchemaVersion,
                ["toolVersion"] = context.ToolVersion,
                ["redacted"] = redactor != null,
                ["startedAtUtc"] = Iso(context.StartedAtUtc),
                ["finishedAtUtc"] = Iso(context.FinishedAtUtc),
                ["cancelled"] = context.Cancelled,
                ["privilegedRead"] = new Dictionary<string, object>
                {
                    ["enabled"] = context.PrivilegedReadEnabled,
                    ["error"] = context.PrivilegedReadError
                },
                ["verdict"] = context.Verdict == null ? null : new Dictionary<string, object>
                {
                    ["kind"] = context.Verdict.Kind.ToString(),
                    ["headline"] = r(context.Verdict.Headline),
                    ["detail"] = r(context.Verdict.Detail),
                    ["severity"] = context.Verdict.DisplaySeverity.ToString(),
                    ["criticalCount"] = context.Verdict.CriticalCount,
                    ["warningCount"] = context.Verdict.WarningCount,
                    ["gaps"] = context.Verdict.Gaps.Select(r).ToList()
                },
                ["findings"] = (context.Verdict?.Findings ?? new List<Finding>())
                    .Select(f => (object)new Dictionary<string, object>
                    {
                        ["id"] = f.Id,
                        ["title"] = r(f.Title),
                        ["severity"] = f.Severity.ToString(),
                        ["confidence"] = f.Confidence.ToString(),
                        ["meaning"] = r(f.Meaning),
                        ["action"] = r(f.Action),
                        ["command"] = r(f.Command),
                        ["evidence"] = f.Evidence.Select(e => (object)new Dictionary<string, object>
                        {
                            ["source"] = r(e.Source),
                            ["line"] = e.LineNumber,
                            ["text"] = r(e.Text)
                        }).ToList()
                    }).ToList(),
                ["system"] = BuildSystem(state, r),
                ["manifest"] = context.Manifest.Select(e => (object)new Dictionary<string, object>
                {
                    ["sourceId"] = e.Source.Id,
                    ["category"] = e.Source.Category.ToString(),
                    ["name"] = e.Source.DisplayName,
                    ["highValue"] = e.Source.HighValue,
                    ["path"] = r(e.ResolvedPath),
                    ["exists"] = e.Exists,
                    ["sizeBytes"] = e.SizeKnown ? (object)e.SizeBytes : null,
                    ["lastWriteUtc"] = Iso(e.LastWriteTimeUtc),
                    ["readable"] = e.Readable,
                    ["requiresPrivilegedRead"] = e.RequiresPrivilegedRead,
                    ["accessError"] = r(e.AccessError)
                }).ToList(),
                ["collectionErrors"] = state.CollectionErrors.Select(r).ToList()
            };
        }

        private static Dictionary<string, object> BuildSystem(SystemState s, Func<string, string> r)
        {
            return new Dictionary<string, object>
            {
                ["machineName"] = r(s.MachineName),
                ["collectedAtUtc"] = Iso(s.CollectedAtUtc),
                ["isElevated"] = s.IsElevated,
                ["os"] = s.Os == null ? null : new Dictionary<string, object>
                {
                    ["productName"] = s.Os.DisplayName,
                    ["productNameRaw"] = s.Os.ProductName,
                    ["isWindows11"] = s.Os.IsWindows11,
                    ["edition"] = s.Os.EditionId,
                    ["displayVersion"] = s.Os.DisplayVersion,
                    ["build"] = s.Os.CurrentBuildNumber,
                    ["ubr"] = s.Os.Ubr
                },
                ["setupProgressPercent"] = s.SetupProgressPercent,
                ["pendingReboot"] = s.PendingReboot == null ? null : new Dictionary<string, object>
                {
                    ["any"] = s.PendingReboot.Any,
                    ["componentBasedServicing"] = s.PendingReboot.ComponentBasedServicing,
                    ["windowsUpdate"] = s.PendingReboot.WindowsUpdate,
                    ["pendingFileRenameOperations"] = s.PendingReboot.PendingFileRenameOperations
                },
                ["secureBootEnabled"] = s.SecureBootEnabled,
                ["memoryIntegrityEnabled"] = s.MemoryIntegrityEnabled,
                ["systemDriveFreeBytes"] = s.SystemDriveFreeBytes,
                ["systemDriveTotalBytes"] = s.SystemDriveTotalBytes,
                ["upgradeFolders"] = (s.UpgradeFolders ?? new List<UpgradeFolderInfo>()).Select(f => (object)new Dictionary<string, object>
                {
                    ["path"] = r(f.Path),
                    ["exists"] = f.Exists,
                    ["lastWriteUtc"] = Iso(f.LastWriteTimeUtc)
                }).ToList(),
                ["processes"] = (s.Processes?.Processes ?? new List<ProcessInfo>()).Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["running"] = p.IsRunning,
                    ["pid"] = p.ProcessId,
                    ["startedUtc"] = Iso(p.StartTimeUtc)
                }).ToList(),
                ["taskSequenceExecutionRequest"] = s.TaskSequenceExecutionRequest == null ? null : new Dictionary<string, object>
                {
                    ["exists"] = s.TaskSequenceExecutionRequest.ExecutionRequestExists,
                    ["packageId"] = s.TaskSequenceExecutionRequest.PackageId,
                    ["advertisementId"] = s.TaskSequenceExecutionRequest.AdvertisementId,
                    ["error"] = r(s.TaskSequenceExecutionRequest.Error)
                },
                ["storage"] = (s.StorageHealth ?? new List<StorageHealthInfo>()).Where(d => d.Error == null).Select(d => (object)new Dictionary<string, object>
                {
                    ["deviceId"] = d.DeviceId,
                    ["friendlyName"] = d.FriendlyName,
                    ["healthStatus"] = d.HealthStatus,
                    ["operationalStatus"] = d.OperationalStatus,
                    ["wear"] = d.Wear,
                    ["readErrorsUncorrected"] = d.ReadErrorsUncorrected,
                    ["writeErrorsUncorrected"] = d.WriteErrorsUncorrected,
                    ["powerOnHours"] = d.PowerOnHours
                }).ToList(),
                ["filterDrivers"] = (s.FilterDrivers ?? new List<FilterDriverInfo>()).Select(f => (object)new Dictionary<string, object>
                {
                    ["service"] = f.ServiceName,
                    ["displayName"] = f.DisplayName,
                    ["imagePath"] = r(f.ImagePath),
                    ["altitudeGroup"] = f.AltitudeGroup,
                    ["startMode"] = f.StartMode,
                    ["startModeName"] = f.StartModeName
                }).ToList(),
                ["events"] = (s.Events ?? new List<EventRecordInfo>()).Select(e => (object)new Dictionary<string, object>
                {
                    ["log"] = e.LogName,
                    ["provider"] = e.ProviderName,
                    ["id"] = e.EventId,
                    ["timeUtc"] = Iso(e.TimeCreatedUtc),
                    ["level"] = e.Level,
                    ["message"] = r(e.Message)
                }).ToList()
            };
        }

        private static string Iso(DateTime? value)
        {
            return value.HasValue && value.Value != default(DateTime)
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("o")
                : null;
        }
    }
}
