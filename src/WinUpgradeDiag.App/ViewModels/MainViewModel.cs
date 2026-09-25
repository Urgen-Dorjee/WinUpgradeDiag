using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Report;

namespace WinUpgradeDiag.App.ViewModels
{
    public enum RunState
    {
        Idle,
        Running,
        Results
    }

    public sealed class MainViewModel : ObservableObject
    {
        private RunState _state = RunState.Idle;
        private string _currentStep = "";
        private CancellationTokenSource _cts;
        private DiagnosticContext _context;
        private string _summary = "";

        private ManifestRow _selectedManifestRow;
        private IReadOnlyList<string> _viewerAllLines = new List<string>();
        private string _viewerFilter = "";
        private string _viewerStatus = "Select a log above to view its most recent lines.";
        private int _viewerLoadVersion;

        private string _outputRoot = ReportExporter.DefaultOutputRoot;
        private bool _exportHtml = true;
        private bool _exportJson = true;
        private bool _exportZip;
        private bool _redact = true;
        private string _exportStatus = "";
        private string _lastExportDirectory;
        private bool _isExporting;

        public MainViewModel()
        {
            RunCommand = new RelayCommand(async () => await RunAsync(), () => State != RunState.Running);
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => State == RunState.Running);
            ExportCommand = new RelayCommand(async () => await ExportAsync(), () => _context != null && !_isExporting);
            OpenExportFolderCommand = new RelayCommand(OpenExportFolder, () => _lastExportDirectory != null);
        }

        public RelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand OpenExportFolderCommand { get; }

        public string ToolVersion => "v" + DiagnosticRunner.ToolVersion;

        // ---------------- Run state ----------------

        public RunState State
        {
            get => _state;
            private set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(HasResults));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsIdle => State == RunState.Idle;
        public bool IsRunning => State == RunState.Running;
        public bool HasResults => State == RunState.Results;

        public string CurrentStep
        {
            get => _currentStep;
            private set => Set(ref _currentStep, value);
        }

        public string Summary
        {
            get => _summary;
            private set => Set(ref _summary, value);
        }

        private async Task RunAsync()
        {
            _cts = new CancellationTokenSource();
            State = RunState.Running;
            CurrentStep = "Starting";

            var progress = new Progress<string>(step => CurrentStep = step);
            var token = _cts.Token;

            try
            {
                _context = await Task.Run(() => new DiagnosticRunner().Run(progress, token));
            }
            catch (Exception ex)
            {
                // Degrade, never crash: show what we have and say what failed.
                _context = new DiagnosticContext { StartedAtUtc = DateTime.UtcNow, FinishedAtUtc = DateTime.UtcNow };
                CurrentStep = "Run failed: " + ex.Message;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }

            PopulateResults();
            State = RunState.Results;
        }

        // ---------------- Results ----------------

        public ObservableCollection<ManifestRow> ManifestRows { get; } = new ObservableCollection<ManifestRow>();
        public ObservableCollection<KeyValuePair<string, string>> SystemFacts { get; } = new ObservableCollection<KeyValuePair<string, string>>();
        public ObservableCollection<ProcessInfo> Processes { get; } = new ObservableCollection<ProcessInfo>();
        public ObservableCollection<StorageHealthInfo> StorageHealth { get; } = new ObservableCollection<StorageHealthInfo>();
        public ObservableCollection<FilterDriverInfo> FilterDrivers { get; } = new ObservableCollection<FilterDriverInfo>();
        public ObservableCollection<EventRecordInfo> Events { get; } = new ObservableCollection<EventRecordInfo>();
        public ObservableCollection<string> CollectionErrors { get; } = new ObservableCollection<string>();

        private void PopulateResults()
        {
            ManifestRows.Clear();
            SystemFacts.Clear();
            Processes.Clear();
            StorageHealth.Clear();
            FilterDrivers.Clear();
            Events.Clear();
            CollectionErrors.Clear();

            foreach (var entry in _context.Manifest)
            {
                ManifestRows.Add(new ManifestRow(entry));
            }

            var s = _context.SystemState;
            if (s != null)
            {
                AddFact("Machine", s.MachineName);
                AddFact("OS", s.Os == null ? null : $"{s.Os.ProductName} {s.Os.DisplayVersion} (build {s.Os.CurrentBuildNumber}.{s.Os.Ubr})");
                AddFact("Edition", s.Os?.EditionId);
                AddFact("Running elevated", YesNo(s.IsElevated));
                AddFact("Backup privilege for protected logs", _context.PrivilegedReadEnabled ? "Enabled" : "Not available: " + _context.PrivilegedReadError);
                AddFact("Setup progress (registry)", s.SetupProgressPercent.HasValue ? s.SetupProgressPercent + "%" : null);
                AddFact("Pending reboot", s.PendingReboot == null ? null : YesNo(s.PendingReboot.Any));
                AddFact("Secure Boot", YesNo(s.SecureBootEnabled));
                AddFact("Memory Integrity (HVCI)", YesNo(s.MemoryIntegrityEnabled));
                AddFact("System drive free space", s.SystemDriveFreeBytes.HasValue
                    ? HtmlReportWriter.Size(s.SystemDriveFreeBytes.Value) + " of " + HtmlReportWriter.Size(s.SystemDriveTotalBytes ?? 0)
                    : null);
                AddFact("Task sequence execution request in WMI", s.TaskSequenceExecutionRequest == null ? null :
                    YesNo(s.TaskSequenceExecutionRequest.ExecutionRequestExists) +
                    (s.TaskSequenceExecutionRequest.PackageId != null ? " (package " + s.TaskSequenceExecutionRequest.PackageId + ")" : ""));
                foreach (var f in s.UpgradeFolders ?? new List<UpgradeFolderInfo>())
                {
                    AddFact("Folder " + f.Path, f.Exists
                        ? "Present, last write " + f.LastWriteTimeUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : "Not present");
                }

                foreach (var p in s.Processes?.Processes ?? new List<ProcessInfo>()) Processes.Add(p);
                foreach (var d in (s.StorageHealth ?? new List<StorageHealthInfo>()).Where(d => d.Error == null)) StorageHealth.Add(d);
                foreach (var f in s.FilterDrivers ?? new List<FilterDriverInfo>()) FilterDrivers.Add(f);
                foreach (var e in (s.Events ?? new List<EventRecordInfo>()).OrderByDescending(e => e.TimeCreatedUtc)) Events.Add(e);
                foreach (var err in s.CollectionErrors) CollectionErrors.Add(err);
            }

            var filesFound = _context.Manifest.Count(m => m.Exists);
            var locations = _context.Manifest.Select(m => m.Source.Id).Distinct().Count();
            var locationsWithFiles = _context.Manifest.Where(m => m.Exists).Select(m => m.Source.Id).Distinct().Count();
            var unreadable = _context.Manifest.Count(m => m.Exists && !m.Readable && !m.RequiresPrivilegedRead);
            var rollback = _context.Manifest.Any(m => m.Exists && m.Source.HighValue);

            Summary = _context.Cancelled
                ? "Run cancelled — the results below are incomplete."
                : $"Found {filesFound} log file(s) in {locationsWithFiles} of {locations} known locations" +
                  (unreadable > 0 ? $", {unreadable} unreadable" : "") +
                  (rollback ? ". Rollback logs are present — Setup reverted to the previous OS at least once." : ".") +
                  " No automated verdict in this version: review the Logs and System tabs.";
        }

        private void AddFact(string label, string value)
        {
            SystemFacts.Add(new KeyValuePair<string, string>(label, value ?? "unknown"));
        }

        private static string YesNo(bool value) => value ? "Yes" : "No";
        private static string YesNo(bool? value) => value.HasValue ? YesNo(value.Value) : null;

        // ---------------- Log viewer ----------------

        public ObservableCollection<string> ViewerLines { get; } = new ObservableCollection<string>();

        public ManifestRow SelectedManifestRow
        {
            get => _selectedManifestRow;
            set
            {
                if (Set(ref _selectedManifestRow, value))
                {
                    _ = LoadViewerAsync(value);
                }
            }
        }

        public string ViewerFilter
        {
            get => _viewerFilter;
            set
            {
                if (Set(ref _viewerFilter, value))
                {
                    ApplyViewerFilter();
                }
            }
        }

        public string ViewerStatus
        {
            get => _viewerStatus;
            private set => Set(ref _viewerStatus, value);
        }

        private async Task LoadViewerAsync(ManifestRow row)
        {
            var version = ++_viewerLoadVersion;
            _viewerAllLines = new List<string>();
            ViewerLines.Clear();

            if (row == null)
            {
                ViewerStatus = "Select a log above to view its most recent lines.";
                return;
            }
            if (!row.Entry.Exists)
            {
                ViewerStatus = "This log is not present on the machine.";
                return;
            }

            string reason;
            if (!LogViewerPolicy.CanRender(row.Path, out reason))
            {
                ViewerStatus = reason;
                return;
            }

            ViewerStatus = "Reading " + row.Path + " …";
            try
            {
                var result = await Task.Run(() => new TailReader().ReadTail(row.Path));
                if (version != _viewerLoadVersion)
                {
                    return; // a newer selection superseded this one
                }

                _viewerAllLines = result.Lines;
                ApplyViewerFilter();
                ViewerStatus = $"Showing the last {result.Lines.Count:N0} lines of {HtmlReportWriter.Size(result.FileSizeBytes)}" +
                               (result.WindowTruncatedFromStart || result.LineCountTruncated ? " (tail window; earlier content not loaded)." : ".");
            }
            catch (Exception ex)
            {
                if (version == _viewerLoadVersion)
                {
                    ViewerStatus = "Could not read this log: " + ex.Message;
                }
            }
        }

        private void ApplyViewerFilter()
        {
            ViewerLines.Clear();
            var filter = _viewerFilter?.Trim();
            foreach (var line in _viewerAllLines)
            {
                if (string.IsNullOrEmpty(filter) || line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ViewerLines.Add(line);
                }
            }
        }

        // ---------------- Export ----------------

        public string OutputRoot { get => _outputRoot; set => Set(ref _outputRoot, value); }
        public bool ExportHtml { get => _exportHtml; set => Set(ref _exportHtml, value); }
        public bool ExportJson { get => _exportJson; set => Set(ref _exportJson, value); }
        public bool ExportZip { get => _exportZip; set => Set(ref _exportZip, value); }
        public bool Redact { get => _redact; set => Set(ref _redact, value); }
        public string ExportStatus { get => _exportStatus; private set => Set(ref _exportStatus, value); }

        private async Task ExportAsync()
        {
            var artifacts = ExportArtifacts.None;
            if (ExportHtml) artifacts |= ExportArtifacts.Html;
            if (ExportJson) artifacts |= ExportArtifacts.Json;
            if (ExportZip) artifacts |= ExportArtifacts.EvidenceZip;

            if (artifacts == ExportArtifacts.None)
            {
                ExportStatus = "Choose at least one thing to export.";
                return;
            }

            _isExporting = true;
            ExportStatus = "Exporting…";
            try
            {
                var context = _context;
                var root = OutputRoot;
                var redact = Redact;
                var result = await Task.Run(() => ReportExporter.Export(context, root, artifacts, redact));
                _lastExportDirectory = result.OutputDirectory;

                var lines = new List<string> { "Exported to " + result.OutputDirectory };
                if (result.EvidenceZipPath != null)
                {
                    lines.Add("The evidence zip is NOT redacted — keep it on internal systems only.");
                    if (result.SkippedEvidence.Count > 0)
                    {
                        lines.Add(result.SkippedEvidence.Count + " file(s) left out of the zip; see README.txt inside it.");
                    }
                }
                ExportStatus = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                ExportStatus = "Export failed: " + ex.Message;
            }
            finally
            {
                _isExporting = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OpenExportFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _lastExportDirectory + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ExportStatus = "Could not open the folder: " + ex.Message;
            }
        }
    }
}
