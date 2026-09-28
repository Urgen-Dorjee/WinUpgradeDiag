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
using WinUpgradeDiag.Core.Rules;

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

        private const int FilterDebounceMilliseconds = 180;

        private ManifestRow _selectedManifestRow;
        private TailReadResult _tail;
        private IReadOnlyList<LogViewLine> _viewerLines = new List<LogViewLine>();
        private string _viewerFilter = "";
        private string _viewerStatus = "Select a log above to view its most recent lines.";
        private int _viewerLoadVersion;

        /// <summary>True when the tail window happened to cover the entire file, which is the only
        /// case where the viewer can name absolute line numbers.</summary>
        private bool _tailIsWholeFile;

        private CancellationTokenSource _filterDebounceCts;
        private CancellationTokenSource _searchCts;
        private bool _isSearching;
        private double _searchProgress;

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
            CancelCommand = new RelayCommand(CancelRun, () => State == RunState.Running);
            ExportCommand = new RelayCommand(async () => await ExportAsync(), () => !_isExporting);
            OpenExportFolderCommand = new RelayCommand(OpenExportFolder, () => _lastExportDirectory != null);
            SearchWholeFileCommand = new RelayCommand(
                async () => await SearchWholeFileAsync(),
                () => !_isSearching && _selectedManifestRow != null && !string.IsNullOrWhiteSpace(_viewerFilter));
            CancelSearchCommand = new RelayCommand(() => _searchCts?.Cancel(), () => _isSearching);
            CopyCommandCommand = new RelayCommand(CopyCommand, () => HasRecommendedCommand);

            // The toolbox is available from the moment the app opens: a technician standing at a
            // broken machine should not have to run a diagnostic first to reach the fix scripts.
            Tools = new ToolsViewModel(() => _lastExportDirectory ?? ReportExporter.DefaultOutputRoot);

            AboutCommand = new RelayCommand(ShowAbout);
            ExitCommand = new RelayCommand(() => System.Windows.Application.Current?.Shutdown());
        }

        public RelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand OpenExportFolderCommand { get; }
        public RelayCommand SearchWholeFileCommand { get; }
        public RelayCommand CancelSearchCommand { get; }
        public RelayCommand CopyCommandCommand { get; }
        public RelayCommand AboutCommand { get; }
        public RelayCommand ExitCommand { get; }

        /// <summary>The recovery-script toolbox.</summary>
        public ToolsViewModel Tools { get; }

        /// <summary>Short form for the header: "v0.1.0 (f34aaba)".</summary>
        public string ToolVersion => "v" + DiagnosticRunner.DisplayVersion;

        /// <summary>Full build identity, shown on hover and written into reports.</summary>
        public string ToolVersionFull => DiagnosticRunner.ToolVersion;

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
                // Degrade, never crash. Keep any context the runner managed to return: replacing it
                // with an empty one throws away the manifest and system state that were collected
                // before the failure, which is exactly the evidence the technician still needs.
                if (_context == null)
                {
                    _context = new DiagnosticContext { StartedAtUtc = DateTime.UtcNow, FinishedAtUtc = DateTime.UtcNow };
                }
                _context.VerdictFailure = _context.VerdictFailure ?? (ex.GetType().Name + ": " + ex.Message);
                CurrentStep = "Run failed: " + ex.Message;
            }
            finally
            {
                // Null the field before disposing: CancelCommand reads it, and a Cancel click
                // landing between Dispose() and the null would throw ObjectDisposedException.
                var finished = _cts;
                _cts = null;
                finished.Dispose();
            }

            PopulateResults();
            LastRunText = "Last run " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            State = RunState.Results;
        }

        private void CancelRun()
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run completed between the button being enabled and the click arriving.
            }
        }

        // ---------------- Results ----------------

        public ObservableCollection<ManifestRow> ManifestRows { get; } = new ObservableCollection<ManifestRow>();
        public ObservableCollection<KeyValuePair<string, string>> SystemFacts { get; } = new ObservableCollection<KeyValuePair<string, string>>();
        public ObservableCollection<ProcessInfo> Processes { get; } = new ObservableCollection<ProcessInfo>();
        public ObservableCollection<StorageHealthInfo> StorageHealth { get; } = new ObservableCollection<StorageHealthInfo>();
        public ObservableCollection<FilterDriverInfo> FilterDrivers { get; } = new ObservableCollection<FilterDriverInfo>();
        public ObservableCollection<EventRecordInfo> Events { get; } = new ObservableCollection<EventRecordInfo>();
        public ObservableCollection<string> CollectionErrors { get; } = new ObservableCollection<string>();
        public ObservableCollection<TimelineEntry> TimelineEntries { get; } = new ObservableCollection<TimelineEntry>();

        public bool HasTimeline => TimelineEntries.Count > 0;

        // ---------------- Verdict ----------------

        public ObservableCollection<FindingRow> Findings { get; } = new ObservableCollection<FindingRow>();
        public ObservableCollection<string> Gaps { get; } = new ObservableCollection<string>();

        private string _verdictHeadline = "";
        private string _verdictDetail = "";
        private string _verdictKind = "";
        private string _verdictSeverity = "Info";
        private string _recommendedAction = "";
        private string _recommendedCommand = "";

        /// <summary>The one sentence a technician reads first.</summary>
        public string VerdictHeadline
        {
            get => _verdictHeadline;
            private set => Set(ref _verdictHeadline, value);
        }

        public string VerdictDetail
        {
            get => _verdictDetail;
            private set => Set(ref _verdictDetail, value);
        }

        public string VerdictKindText
        {
            get => _verdictKind;
            private set => Set(ref _verdictKind, value);
        }

        /// <summary>Drives the verdict card colour: Info, Warning or Critical.</summary>
        public string VerdictSeverity
        {
            get => _verdictSeverity;
            private set => Set(ref _verdictSeverity, value);
        }

        public string RecommendedAction
        {
            get => _recommendedAction;
            private set => Set(ref _recommendedAction, value);
        }

        public string RecommendedCommand
        {
            get => _recommendedCommand;
            private set => Set(ref _recommendedCommand, value);
        }

        public bool HasRecommendedAction => !string.IsNullOrWhiteSpace(RecommendedAction);
        public bool HasRecommendedCommand => !string.IsNullOrWhiteSpace(RecommendedCommand);
        public bool HasGaps => Gaps.Count > 0;
        public bool HasFindings => Findings.Count > 0;

        private void PopulateVerdict()
        {
            Findings.Clear();
            Gaps.Clear();

            var verdict = _context?.Verdict;
            if (verdict == null)
            {
                DescribeMissingVerdict();
            }
            else
            {
                VerdictHeadline = verdict.Headline;
                VerdictDetail = verdict.Detail;
                VerdictSeverity = verdict.DisplaySeverity.ToString();
                VerdictKindText = Humanise(verdict.Kind);

                var top = verdict.TopFinding;
                RecommendedAction = top?.Action ?? "";
                RecommendedCommand = top?.Command ?? "";

                // Only the leading finding starts open; the rest stay collapsed so the answer is
                // visible without scrolling (DESIGN.md §5).
                var first = true;
                foreach (var finding in verdict.Findings)
                {
                    Findings.Add(new FindingRow(finding, expanded: first));
                    first = false;
                }

                foreach (var gap in verdict.Gaps)
                {
                    Gaps.Add(gap);
                }
            }

            OnPropertyChanged(nameof(HasRecommendedAction));
            OnPropertyChanged(nameof(HasRecommendedCommand));
            OnPropertyChanged(nameof(HasGaps));
            OnPropertyChanged(nameof(HasFindings));
        }

        /// <summary>
        /// Says why there is no verdict, and what survived anyway. "Did not complete" on its own
        /// tells a technician nothing and hides the fact that the collected evidence is still
        /// sitting in the other tabs.
        /// </summary>
        private void DescribeMissingVerdict()
        {
            RecommendedAction = "";
            RecommendedCommand = "";

            var collected = new List<string>();
            if (_context?.Manifest != null && _context.Manifest.Count > 0)
            {
                collected.Add(_context.Manifest.Count(m => m.Exists) + " log file(s) in the Logs tab");
            }
            if (_context?.SystemState != null)
            {
                collected.Add("machine state in the System tab");
            }

            var survived = collected.Count > 0
                ? " What was collected is still available: " + string.Join(" and ", collected) + "."
                : " Nothing was collected.";

            if (_context != null && _context.Cancelled)
            {
                VerdictKindText = "Cancelled";
                VerdictSeverity = "Warning";
                VerdictHeadline = "The diagnostic was cancelled before it reached a verdict.";
                VerdictDetail = "Run it again and let it finish to get an answer." + survived;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_context?.VerdictFailure))
            {
                VerdictKindText = "Rules failed";
                VerdictSeverity = "Warning";
                VerdictHeadline = "Evidence was collected, but the rules could not be applied to it.";
                VerdictDetail = "The collection stage worked; the analysis stage failed with: " +
                                _context.VerdictFailure + "." + survived;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_context?.CollectionFailure))
            {
                VerdictKindText = "Incomplete";
                VerdictSeverity = "Warning";
                VerdictHeadline = "Collection stopped before it finished.";
                VerdictDetail = _context.CollectionFailure + "." + survived;
                return;
            }

            VerdictKindText = "Incomplete";
            VerdictSeverity = "Warning";
            VerdictHeadline = "The diagnostic did not produce a verdict.";
            VerdictDetail = "No reason was recorded, which is itself a defect worth reporting." + survived;
        }

        private static string Humanise(VerdictKind kind)
        {
            switch (kind)
            {
                case VerdictKind.InProgress: return "Upgrade in progress";
                case VerdictKind.CauseIdentified: return "Cause identified";
                case VerdictKind.Inconclusive: return "Inconclusive";
                case VerdictKind.NoFailureFound: return "No failure found";
                case VerdictKind.InsufficientEvidence: return "Insufficient evidence";
                default: return kind.ToString();
            }
        }

        // ---------------- status bar ----------------

        /// <summary>Machine and privilege, shown in the status bar from launch onwards.</summary>
        public string MachineName => Environment.MachineName;

        public string ElevationText => IsElevatedSession
            ? "Administrator"
            : "Not elevated — protected logs cannot be read";

        public bool IsElevatedSession
        {
            get
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

        private string _lastExportText = "";

        /// <summary>Shown in the status bar so the export leaves a trace after the dialog closes.</summary>
        public string LastExportText
        {
            get => _lastExportText;
            private set
            {
                if (Set(ref _lastExportText, value))
                {
                    OnPropertyChanged(nameof(HasExported));
                }
            }
        }

        public bool HasExported => !string.IsNullOrEmpty(_lastExportText);

        private string _lastRunText = "No diagnostic run yet";
        public string LastRunText
        {
            get => _lastRunText;
            private set => Set(ref _lastRunText, value);
        }

        private void ShowAbout()
        {
            ActionDialog.Show(
                System.Windows.Application.Current?.MainWindow,
                DialogKind.Information,
                "WinUpgradeDiag " + DiagnosticRunner.DisplayVersion,
                "Offline, read-only diagnostic for failed ConfigMgr Windows 10 to 11 in-place upgrades." +
                Environment.NewLine + Environment.NewLine +
                "Diagnosis never changes this machine. Recovery tools are separate, confirmed per action, " +
                "and written to an audit log.",
                "Build " + DiagnosticRunner.ToolVersion);
        }

        private void CopyCommand()
        {
            try
            {
                System.Windows.Clipboard.SetText(RecommendedCommand ?? "");
            }
            catch (Exception)
            {
                // The clipboard can be locked by another process; not worth interrupting the tech.
            }
        }

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

            PopulateVerdict();

            TimelineEntries.Clear();
            foreach (var entry in TimelineBuilder.Build(_context))
            {
                TimelineEntries.Add(entry);
            }
            OnPropertyChanged(nameof(HasTimeline));

            var s = _context.SystemState;
            if (s != null)
            {
                AddFact("Machine", s.MachineName);
                AddFact("OS", s.Os?.FullDescription);
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

        /// <summary>
        /// Swapped as one batch rather than mutated per item. Rebuilding a 5,000-line tail through
        /// an ObservableCollection one Add at a time raises 5,000 change notifications, and the
        /// filter box does that on every keystroke; replacing the list raises exactly one.
        /// </summary>
        public IReadOnlyList<LogViewLine> ViewerLines
        {
            get => _viewerLines;
            private set => Set(ref _viewerLines, value);
        }

        public ManifestRow SelectedManifestRow
        {
            get => _selectedManifestRow;
            set
            {
                if (Set(ref _selectedManifestRow, value))
                {
                    // SearchWholeFileCommand needs a selected log; re-evaluate rather than waiting
                    // for whatever UI gesture happens to raise RequerySuggested next.
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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
                    _ = DebounceFilterAsync();
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ViewerStatus
        {
            get => _viewerStatus;
            private set => Set(ref _viewerStatus, value);
        }

        public bool IsSearching
        {
            get => _isSearching;
            private set
            {
                if (Set(ref _isSearching, value))
                {
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Whole-file search progress, 0 to 1. Meaningful only while searching.</summary>
        public double SearchProgress
        {
            get => _searchProgress;
            private set => Set(ref _searchProgress, value);
        }

        private async Task LoadViewerAsync(ManifestRow row)
        {
            var version = ++_viewerLoadVersion;
            _tail = null;
            _tailIsWholeFile = false;
            ViewerLines = new List<LogViewLine>();

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

                _tail = result;
                _tailIsWholeFile = !result.WindowTruncatedFromStart && !result.LineCountTruncated;
                ApplyViewerFilter();

                if (_tailIsWholeFile)
                {
                    ViewerStatus = $"Showing all {result.Lines.Count:N0} lines of {HtmlReportWriter.Size(result.FileSizeBytes)}.";
                }
                else
                {
                    // Be explicit about how little of a huge log the tail covers, and point at the
                    // thing that does cover it. A 700 MB setupact.log keeps the failure thousands
                    // of lines before the end, and silently showing 0.3% of it invites the wrong
                    // conclusion (DESIGN.md §8).
                    var covered = result.FileSizeBytes > 0
                        ? (result.FileSizeBytes - result.WindowStartOffset) * 100.0 / result.FileSizeBytes
                        : 100.0;
                    ViewerStatus =
                        $"Showing the last {result.Lines.Count:N0} lines — about {covered:0.#}% of " +
                        $"{HtmlReportWriter.Size(result.FileSizeBytes)}. Earlier content is not loaded; " +
                        "use Search whole file to scan all of it.";
                }
            }
            catch (Exception ex)
            {
                if (version == _viewerLoadVersion)
                {
                    ViewerStatus = "Could not read this log: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Waits out a burst of typing before re-filtering. Without this every keystroke rebuilds
        /// the whole rendered list, which is plainly visible on a 5,000-line tail.
        /// </summary>
        private async Task DebounceFilterAsync()
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _filterDebounceCts, cts);
            if (previous != null)
            {
                // Cancelled but deliberately not disposed: the awaiting Task.Delay below may still
                // be unwinding on its own token registration. These are cheap and short-lived.
                previous.Cancel();
            }

            try
            {
                await Task.Delay(FilterDebounceMilliseconds, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a later keystroke
            }

            ApplyViewerFilter();
        }

        private void ApplyViewerFilter()
        {
            if (_tail == null)
            {
                ViewerLines = new List<LogViewLine>();
                return;
            }

            var filter = _viewerFilter?.Trim();
            var lines = LogSearchView.FromTail(_tail, filter);
            ViewerLines = lines;

            if (!string.IsNullOrEmpty(filter) && _selectedManifestRow != null)
            {
                var matches = 0;
                foreach (var line in lines)
                {
                    if (line.IsMatch)
                    {
                        matches++;
                    }
                }

                ViewerStatus = $"{matches:N0} line(s) in the loaded tail contain \"{filter}\"" +
                               (_tailIsWholeFile ? "." : " — use Search whole file to scan the rest.");
            }
        }

        // ---------------- Whole-file search ----------------

        private async Task SearchWholeFileAsync()
        {
            var row = _selectedManifestRow;
            var query = _viewerFilter?.Trim();

            if (row == null)
            {
                ViewerStatus = "Choose a log above first.";
                return;
            }
            if (string.IsNullOrEmpty(query))
            {
                ViewerStatus = "Type what to search for, then press Search whole file.";
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

            var cts = new CancellationTokenSource();
            _searchCts = cts;
            IsSearching = true;
            SearchProgress = 0;
            ViewerStatus = $"Searching all of {row.Size} for \"{query}\" …";

            var path = row.Path;
            var progress = new Progress<double>(p => SearchProgress = p);

            try
            {
                var result = await Task.Run(
                    () => new LogSearcher().Search(
                        path, query, LogSearcher.DefaultMaxMatches, LogSearcher.DefaultContextLines, progress, cts.Token));

                RenderSearchResult(result, query);
            }
            catch (Exception ex)
            {
                ViewerStatus = "Could not search this log: " + ex.Message;
            }
            finally
            {
                _searchCts = null;
                cts.Dispose();
                IsSearching = false;
                SearchProgress = 0;
            }
        }

        private void RenderSearchResult(LogSearchResult result, string query)
        {
            ViewerLines = LogSearchView.FromSearch(result);
            ViewerStatus = LogSearchView.DescribeSearch(result, query, LogSearcher.DefaultMaxMatches);
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
            // Export is reachable from the toolbar and the File menu, but its only feedback used to
            // be a line of text inside the Export tab. Pressed from anywhere else it wrote the
            // files and appeared to do nothing at all.
            if (_context == null)
            {
                ExportStatus = "Run a diagnostic first — there is nothing to export yet.";
                ActionDialog.Show(
                    System.Windows.Application.Current?.MainWindow,
                    DialogKind.Information,
                    "Nothing to export yet",
                    "Run a diagnostic first. The export contains the verdict, the findings and their " +
                    "evidence, the log manifest and the collected machine state — none of which exists " +
                    "until a run has happened.",
                    "Nothing was written.");
                return;
            }

            var artifacts = ExportArtifacts.None;
            if (ExportHtml) artifacts |= ExportArtifacts.Html;
            if (ExportJson) artifacts |= ExportArtifacts.Json;
            if (ExportZip) artifacts |= ExportArtifacts.EvidenceZip;

            if (artifacts == ExportArtifacts.None)
            {
                ExportStatus = "Choose at least one thing to export.";
                ActionDialog.Show(
                    System.Windows.Application.Current?.MainWindow,
                    DialogKind.Warning,
                    "Nothing selected to export",
                    "Every output is switched off. Choose the HTML report, the JSON, or the evidence " +
                    "zip on the Export tab, then try again.",
                    "Nothing was written.");
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
                    if (result.PartiallyCapturedEvidence.Count > 0)
                    {
                        lines.Add(result.PartiallyCapturedEvidence.Count +
                                  " log(s) were too large to include whole — only the end of each was captured. See README.txt inside the zip.");
                    }
                    if (result.SkippedEvidence.Count > 0)
                    {
                        lines.Add(result.SkippedEvidence.Count + " file(s) left out of the zip; see README.txt inside it.");
                    }
                }
                ExportStatus = string.Join(Environment.NewLine, lines);
                LastExportText = "Exported " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

                var written = new List<string>();
                if (result.HtmlPath != null) written.Add(System.IO.Path.GetFileName(result.HtmlPath) + " — the report for the ticket");
                if (result.JsonPath != null) written.Add(System.IO.Path.GetFileName(result.JsonPath) + " — machine-readable, for fleet aggregation");
                if (result.EvidenceZipPath != null) written.Add(System.IO.Path.GetFileName(result.EvidenceZipPath) + " — the collected logs, NOT redacted");

                // Offer the folder straight away: the path alone still leaves the technician to go
                // and find it, and the point of exporting is to attach the file to something.
                var openIt = ActionDialog.Confirm(
                    System.Windows.Application.Current?.MainWindow,
                    DialogKind.Success,
                    "Export complete",
                    result.OutputDirectory,
                    written,
                    result.EvidenceZipPath != null
                        ? new[] { "The evidence zip is NOT redacted. Keep it on internal systems only." }
                        : null,
                    null,
                    "Open folder",
                    null,
                    "Also available from File \u2192 Open last output folder.",
                    null,
                    "FILES WRITTEN",
                    "KEEP IN MIND");

                if (openIt)
                {
                    OpenExportFolder();
                }
            }
            catch (Exception ex)
            {
                ExportStatus = "Export failed: " + ex.Message;
                ActionDialog.Show(
                    System.Windows.Application.Current?.MainWindow,
                    DialogKind.Warning,
                    "Export failed",
                    ex.Message,
                    "Check the output folder on the Export tab is somewhere you can write to.");
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
