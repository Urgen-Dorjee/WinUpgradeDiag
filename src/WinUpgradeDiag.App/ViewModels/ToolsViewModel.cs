using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Remediation;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>
    /// The technician's toolbox: the recovery scripts, what each does, and a gated way to run one.
    /// <para>
    /// Everything that changes the machine passes through
    /// <see cref="RemediationRunner.Preflight"/> immediately before launch and, when destructive,
    /// through a confirmation the operator has to type. Nothing here runs on its own.
    /// </para>
    /// </summary>
    public sealed class ToolsViewModel : ObservableObject
    {
        private readonly RemediationRunner _runner = new RemediationRunner();
        private readonly RemediationPreview _preview = new RemediationPreview();
        private readonly Func<string> _auditFolderProvider;
        private readonly Func<DiagnosticContext> _contextProvider;

        private string _scriptFolder;
        private string _status = "";
        private string _output = "";
        private bool _isRunning;
        private ToolRow _selected;
        private CancellationTokenSource _cts;

        public ToolsViewModel(Func<string> auditFolderProvider, Func<DiagnosticContext> contextProvider = null)
        {
            _auditFolderProvider = auditFolderProvider;
            // The collected state answers most of what the tools ask for. Without it the Tools tab
            // has to interrogate the operator for values sitting a tab away.
            _contextProvider = contextProvider ?? (() => null);
            _scriptFolder = ToolCatalog.LocateScriptFolder() ?? "";

            foreach (var tool in ToolCatalog.All)
            {
                Tools.Add(new ToolRow(tool));
            }

            RunToolCommand = new RelayCommand<ToolRow>(async row => await RunAsync(row), row => !_isRunning && row != null);
            CancelToolCommand = new RelayCommand(() => _cts?.Cancel(), () => _isRunning);
            PreviewToolCommand = new RelayCommand<ToolRow>(PreviewTool, row => !_isRunning && row != null);
            BrowseScriptFolderCommand = new RelayCommand(BrowseScriptFolder, () => !_isRunning);
            // The transcript pane had no way out: once a tool had written to it, it sat on screen
            // for the rest of the session with no control to dismiss it.
            ClearOutputCommand = new RelayCommand(ClearOutput, () => !_isRunning && HasOutput);
            CopyOutputCommand = new RelayCommand(CopyOutput, () => HasOutput);
            OpenScriptFolderCommand = new RelayCommand(OpenScriptFolder, () => ScriptsFound);

            GroupedTools = System.Windows.Data.CollectionViewSource.GetDefaultView(Tools);
            GroupedTools.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Category)));

            _status = ScriptsFound
                ? "Ready. Pick a tool, or Preview one to see exactly what it would run."
                : "No recovery scripts are available. Set the folder containing Check-UpgradeState.ps1.";
        }

        public ObservableCollection<ToolRow> Tools { get; } = new ObservableCollection<ToolRow>();

        /// <summary>
        /// The catalogue grouped by purpose, so the read-only checks are visually separate from
        /// the tools that change the machine. A flat list of eleven puts "delete the download"
        /// next to "report the state" with nothing between them.
        /// </summary>
        public System.ComponentModel.ICollectionView GroupedTools { get; private set; }

        public RelayCommand<ToolRow> RunToolCommand { get; }
        public RelayCommand CancelToolCommand { get; }
        public RelayCommand<ToolRow> PreviewToolCommand { get; }
        public RelayCommand BrowseScriptFolderCommand { get; }
        public RelayCommand ClearOutputCommand { get; }
        public RelayCommand CopyOutputCommand { get; }
        public RelayCommand OpenScriptFolderCommand { get; }

        /// <summary>Folder holding the recovery scripts. Editable, because deployments differ.</summary>
        public string ScriptFolder
        {
            get => _scriptFolder;
            set
            {
                if (Set(ref _scriptFolder, value))
                {
                    OnPropertyChanged(nameof(ScriptsFound));
                    OnPropertyChanged(nameof(ScriptsMissing));
                    OnPropertyChanged(nameof(ScriptsOnDisk));
                    OnPropertyChanged(nameof(ScriptSourceText));
                    OnPropertyChanged(nameof(HasScriptFolder));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// Whether the tools can actually run.
        /// <para>
        /// Every script ships inside the assembly, so on the single-file build this is always true
        /// and the folder is irrelevant. Checking only for a file on disk made the Tools tab open
        /// with "The recovery scripts were not found" on exactly the deployment the tool is meant
        /// for — a lone .exe copied onto a broken machine — while the tools underneath worked
        /// perfectly, because the runner prefers the embedded copy anyway.
        /// </para>
        /// </summary>
        public bool ScriptsFound => ToolCatalog.ScriptsAvailable(ScriptFolder);

        public bool ScriptsMissing => !ScriptsFound;

        /// <summary>Scripts carried inside this build. The normal case.</summary>
        public bool ScriptsEmbedded => ToolCatalog.ScriptsEmbedded;

        /// <summary>A folder was pointed at and holds the scripts. An override, not a requirement.</summary>
        public bool ScriptsOnDisk => ToolCatalog.ScriptsInFolder(ScriptFolder);

        /// <summary>Where the scripts that will run are coming from, in one line.</summary>
        public string ScriptSourceText
        {
            get
            {
                if (ScriptsOnDisk)
                {
                    return ScriptsEmbedded
                        ? "Overridden by the folder below. The embedded copies are being ignored."
                        : "Loaded from the folder below.";
                }

                if (ScriptsEmbedded)
                {
                    var count = EmbeddedScriptProvider.AvailableScripts.Count;
                    return count + " script(s) embedded in this build. Nothing needs to be copied alongside the .exe.";
                }

                return "No scripts are available. This build carries none and no folder has been set.";
            }
        }

        /// <summary>Clears the transcript so the pane can be dismissed once it has been read.</summary>
        public void ClearOutput()
        {
            Output = "";
        }

        public ToolRow SelectedTool
        {
            get => _selected;
            set => Set(ref _selected, value);
        }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        /// <summary>Live transcript of the running (or last) tool.</summary>
        public string Output
        {
            get => _output;
            private set
            {
                if (Set(ref _output, value))
                {
                    OnPropertyChanged(nameof(HasOutput));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>True once a folder override is set, so the path row can stay hidden until then.</summary>
        public bool HasScriptFolder => !string.IsNullOrWhiteSpace(ScriptFolder);

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasOutput => !string.IsNullOrEmpty(Output);


        /// <summary>
        /// Shows what the tool would touch, without running anything.
        /// <para>
        /// Most of these scripts have no -WhatIf, so a "simulation" would be fiction. Instead this
        /// inspects each thing the script acts on and reports its state now — whether the execution
        /// request actually exists, whether the folder is there and how large, whether the content
        /// id matches anything cached. That is checkable before committing.
        /// </para>
        /// </summary>
        private void PreviewTool(ToolRow row)
        {
            if (row == null)
            {
                return;
            }

            PreviewReport report;
            try
            {
                report = _preview.Build(row.Tool, row.ParameterValue);
            }
            catch (Exception ex)
            {
                Status = "Could not preview " + row.Tool.ScriptName + ": " + ex.Message;
                return;
            }

            var lines = report.Targets
                .Select(t => (t.WillChange ? "WILL CHANGE  " : "no change    ") + t.What +
                             "  —  " + t.State + "  —  " + t.Effect)
                .ToList();

            ActionDialog.Show(
                Application.Current?.MainWindow,
                report.ChangeCount == 0 ? DialogKind.Information : DialogKind.Warning,
                "Preview: " + row.Tool.Title,
                report.Summary + (report.HasCaveat ? Environment.NewLine + Environment.NewLine + report.Caveat : ""),
                "Nothing was run. This is the machine's state right now.");

            Status = report.Summary;
            Output = string.Join(Environment.NewLine, lines);
            OnPropertyChanged(nameof(HasOutput));
        }

        private async Task RunAsync(ToolRow row)
        {
            if (row == null || IsRunning)
            {
                return;
            }

            var tool = row.Tool;
            var parameterValue = row.ParameterValue;

            // 1. Ask for anything the script needs that we do not already have.
            //    Without this, launching a parameterised tool from the Tools menu is a dead end:
            //    the menu has no field to type into, so it could only ever refuse.
            if (tool.RequiresParameter && string.IsNullOrWhiteSpace(parameterValue))
            {
                var suggested = SuggestParameter(tool);

                // When nothing could be worked out, say where the value comes from rather than
                // leaving an empty box and an example of somebody else's site.
                var help = tool.ParameterHelp;
                if (string.IsNullOrWhiteSpace(suggested))
                {
                    var whereToFind = ParameterSuggestion.WhereToFind(tool, _contextProvider());
                    if (!string.IsNullOrWhiteSpace(whereToFind))
                    {
                        help = whereToFind + (string.IsNullOrWhiteSpace(help) ? "" : "\n\n" + help);
                    }
                }

                var prompt = new PromptWindow(
                    tool.Title,
                    tool.ParameterPrompt + ":",
                    suggested,
                    tool.ParameterExample,
                    help)
                {
                    Owner = Application.Current?.MainWindow
                };

                if (prompt.ShowDialog() != true)
                {
                    Status = tool.Title + " was cancelled — no " + tool.ParameterName + " supplied.";
                    return;
                }

                parameterValue = prompt.Value?.Trim();
                row.ParameterValue = parameterValue;   // remember it on the card too

                if (string.IsNullOrWhiteSpace(parameterValue))
                {
                    Status = tool.ParameterPrompt + " is required.";
                    return;
                }
            }

            // 2. Fresh preflight, taken now rather than when the list was built.
            var preflight = _runner.Preflight(tool, ScriptFolder, parameterValue);
            if (!preflight.IsAllowed)
            {
                Status = preflight.Message;
                ActionDialog.Show(
                    Application.Current?.MainWindow,
                    DialogKind.Warning,
                    "Cannot run " + tool.Title,
                    preflight.Message,
                    "Nothing was changed on this machine.");

                // A refusal is still recorded, so the audit trail shows what was attempted.
                _runner.Run(tool, preflight, parameterValue, _auditFolderProvider?.Invoke());
                return;
            }

            // 3. Confirmation, proportional to what the tool can destroy.
            if (!Confirm(tool, parameterValue))
            {
                Status = tool.Title + " was cancelled before it ran.";
                return;
            }

            // 4. Run, streaming output.
            IsRunning = true;
            Output = "";
            Status = "Running " + tool.ScriptName + "…";

            var cts = new CancellationTokenSource();
            _cts = cts;
            var lines = new List<string>();
            var progress = new Progress<string>(line =>
            {
                lines.Add(line);
                Output = string.Join(Environment.NewLine, lines);
                OnPropertyChanged(nameof(HasOutput));
            });

            try
            {
                var folder = _auditFolderProvider?.Invoke();
                var result = await Task.Run(
                    () => _runner.Run(tool, preflight, parameterValue, folder, progress, cts.Token), cts.Token);

                Output = result.Output ?? "";
                Status = result.Succeeded
                    ? tool.Title + " finished successfully." +
                      (result.AuditLogPath != null ? "  Audit: " + result.AuditLogPath : "")
                    : tool.Title + " finished with exit code " +
                      (result.ExitCode?.ToString() ?? "unknown") + ". Review the output above.";
            }
            catch (OperationCanceledException)
            {
                Status = tool.Title + " was cancelled.";
            }
            catch (Exception ex)
            {
                Status = "Could not run " + tool.ScriptName + ": " + ex.Message;
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                IsRunning = false;
                OnPropertyChanged(nameof(HasOutput));
            }
        }

        /// <summary>
        /// A sensible starting value for a script parameter. For the remote tools that means this
        /// machine's own name: it is almost never the right answer, but it shows the expected
        /// shape, which a blank box does not.
        /// </summary>
        private string SuggestParameter(ToolDefinition tool)
        {
            return ParameterSuggestion.For(tool, _contextProvider());
        }

        /// <summary>
        /// Fills each tool card's parameter box from what the run just collected.
        /// <para>
        /// Called when a diagnostic finishes, so the Tools tab shows the content id and package id
        /// already in place rather than a row of empty boxes a technician has to go and research.
        /// A value already typed is never overwritten — that would discard a deliberate correction.
        /// </para>
        /// </summary>
        public void RefreshSuggestions()
        {
            var context = _contextProvider();
            if (context == null)
            {
                return;
            }

            foreach (var row in Tools)
            {
                if (!row.Tool.RequiresParameter || !string.IsNullOrWhiteSpace(row.ParameterValue))
                {
                    continue;
                }

                var suggested = ParameterSuggestion.For(row.Tool, context);
                if (!string.IsNullOrWhiteSpace(suggested))
                {
                    row.ParameterValue = suggested;
                }
            }

            RefreshStandings();
        }

        /// <summary>
        /// Marks each tool as recommended, available, or not applicable to this machine, and sorts
        /// so the one the diagnosis points at is first. Nothing is hidden: a tool ruled out keeps
        /// its place with the reason, because "there is no Windows.old here" is worth knowing, and
        /// a technician who disagrees must still be able to run it.
        /// </summary>
        public void RefreshStandings()
        {
            var context = _contextProvider();

            foreach (var row in Tools)
            {
                row.Standing = ToolRelevance.For(row.Tool, context);
            }

            var ordered = ToolRelevance.Order(Tools.Select(r => r.Tool), context);
            for (var target = 0; target < ordered.Count; target++)
            {
                var current = Tools.IndexOf(Tools.First(r => ReferenceEquals(r.Tool, ordered[target])));
                if (current != target)
                {
                    Tools.Move(current, target);
                }
            }

            var recommended = Tools.FirstOrDefault(r => r.IsRecommended);
            Status = recommended != null
                ? "The diagnosis points at “" + recommended.Title + "”. It is first in the list."
                : "No single tool is indicated. Start with a read-only check.";
        }

        /// <summary>
        /// Asks before acting. A destructive tool requires the operator to type the word, so it
        /// cannot be dismissed by reflex the way a yes/no box can.
        /// </summary>
        /// <summary>
        /// Asks before acting, with the action's own steps and preconditions laid out as separate
        /// sections. Destructive tools additionally require the script name to be typed, so they
        /// cannot be dismissed by reflex the way a yes/no box can.
        /// </summary>
        private static bool Confirm(ToolDefinition tool, string parameterValue)
        {
            // Just the script name: Path.Combine rejects a segment containing < or > on .NET
            // Framework ("Illegal characters in path"), and a placeholder like "<scripts>" threw
            // before any dialog could appear. The real path is a per-run temp folder anyway, so
            // showing the bare name is both safe and more honest.
            var command = "powershell " + RemediationRunner.BuildArguments(
                tool.ScriptName, tool, parameterValue);

            return ActionDialog.Confirm(
                Application.Current?.MainWindow,
                tool.RequiresTypedConfirmation ? DialogKind.Destructive : DialogKind.Warning,
                tool.Title,
                tool.Purpose,
                tool.Steps,
                tool.Preconditions,
                command,
                tool.RequiresTypedConfirmation ? "Run it" : "Run " + tool.ScriptName,
                tool.RequiresTypedConfirmation ? tool.ScriptName : null,
                "Every run is written to an audit log.",
                tool.Acknowledgement);
        }

        private void BrowseScriptFolder()
        {
            // Deliberately a plain input rather than a shell folder-picker: the picker pulls in a
            // WinForms dependency for a field that is typed once and remembered.
            var dialog = new PromptWindow(
                "Recovery scripts folder",
                "Full path to the folder containing Check-UpgradeState.ps1:",
                ScriptFolder,
                @"e.g. C:\Tools\sccm-inplace-upgrade-scripts",
                "The folder holding Check-UpgradeState.ps1, Fix-A…, Fix-B… and the rest.")
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                ScriptFolder = dialog.Value?.Trim() ?? "";
                Status = ScriptsFound
                    ? "Recovery scripts found."
                    : "Check-UpgradeState.ps1 was not found in that folder.";
            }
        }

        /// <summary>Puts the transcript on the clipboard, which is where a ticket note comes from.</summary>
        private void CopyOutput()
        {
            try
            {
                Clipboard.SetText(Output ?? "");
                Status = "Output copied to the clipboard.";
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; that is not worth a dialog.
                Status = "Could not copy: " + ex.Message;
            }
        }

        private void OpenScriptFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ScriptFolder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status = "Could not open the folder: " + ex.Message;
            }
        }
    }
}
