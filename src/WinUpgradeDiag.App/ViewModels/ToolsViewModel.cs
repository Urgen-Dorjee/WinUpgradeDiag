using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        private string _scriptFolder;
        private string _status = "";
        private string _output = "";
        private bool _isRunning;
        private ToolRow _selected;
        private CancellationTokenSource _cts;

        public ToolsViewModel(Func<string> auditFolderProvider)
        {
            _auditFolderProvider = auditFolderProvider;
            _scriptFolder = ToolCatalog.LocateScriptFolder() ?? "";

            foreach (var tool in ToolCatalog.All)
            {
                Tools.Add(new ToolRow(tool));
            }

            RunToolCommand = new RelayCommand<ToolRow>(async row => await RunAsync(row), row => !_isRunning && row != null);
            CancelToolCommand = new RelayCommand(() => _cts?.Cancel(), () => _isRunning);
            PreviewToolCommand = new RelayCommand<ToolRow>(PreviewTool, row => !_isRunning && row != null);
            BrowseScriptFolderCommand = new RelayCommand(BrowseScriptFolder, () => !_isRunning);
            OpenScriptFolderCommand = new RelayCommand(OpenScriptFolder, () => ScriptsFound);

            GroupedTools = System.Windows.Data.CollectionViewSource.GetDefaultView(Tools);
            GroupedTools.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription(nameof(ToolRow.Category)));

            _status = ScriptsFound
                ? "Recovery scripts found."
                : "Recovery scripts not found. Set the folder containing Check-UpgradeState.ps1.";
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
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool ScriptsFound =>
            !string.IsNullOrWhiteSpace(ScriptFolder) &&
            File.Exists(Path.Combine(ScriptFolder, "Check-UpgradeState.ps1"));

        public bool ScriptsMissing => !ScriptsFound;

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
            private set => Set(ref _output, value);
        }

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
                var prompt = new PromptWindow(
                    tool.Title,
                    tool.ParameterPrompt + ":",
                    SuggestParameter(tool),
                    tool.ParameterExample,
                    tool.ParameterHelp)
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
        private static string SuggestParameter(ToolDefinition tool)
        {
            if (string.Equals(tool.ParameterName, "ComputerName", StringComparison.OrdinalIgnoreCase))
            {
                return Environment.MachineName;
            }

            return string.Empty;
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
