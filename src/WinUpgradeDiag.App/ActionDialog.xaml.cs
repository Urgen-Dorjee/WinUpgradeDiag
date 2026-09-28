using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WinUpgradeDiag.App.Controls;

namespace WinUpgradeDiag.App
{
    /// <summary>How serious the dialog is, which drives its accent, glyph and button colour.</summary>
    public enum DialogKind
    {
        Information,
        Success,
        Warning,
        Destructive
    }

    /// <summary>
    /// The one dialog the application uses.
    /// <para>
    /// It replaces every <c>MessageBox.Show</c> call. A stock message box can only render an
    /// undifferentiated blob of text, so "what this does" and "when you must not do it" arrive as
    /// the same grey paragraph — for an action that stops services and deletes gigabytes, those are
    /// different questions and deserve different weight. This presents them as separate, styled
    /// sections, shows the exact command, and can demand a typed confirmation.
    /// </para>
    /// </summary>
    public partial class ActionDialog : Window
    {
        private string _expectedConfirmation;
        private bool _needsAcknowledgement;

        private ActionDialog()
        {
            InitializeComponent();
        }

        /// <summary>Text the operator must type before the primary button enables.</summary>
        private void RequireTypedConfirmation(string expected)
        {
            _expectedConfirmation = expected;
            ConfirmPanel.Visibility = Visibility.Visible;
            ConfirmPrompt.Text = "To continue, type the script name exactly:  " + expected;
            PlaceholderService.SetText(ConfirmInput, expected);
            UpdateMatchState();
        }

        private void ConfirmInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateMatchState();
        }

        private void Acknowledge_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMatchState();
        }

        /// <summary>
        /// Requires the operator to tick a specific statement before proceeding. Used where the
        /// decision rests on something the tool cannot check for itself — whether a person has
        /// actually confirmed the machine works. Making that an explicit claim turns a warning,
        /// which is read past, into a decision, which is made.
        /// </summary>
        private void RequireAcknowledgement(string statement)
        {
            _needsAcknowledgement = true;
            AcknowledgeText.Text = statement;
            AcknowledgePanel.Visibility = Visibility.Visible;
            UpdateMatchState();
        }

        private bool Acknowledged => !_needsAcknowledgement || AcknowledgeCheck.IsChecked == true;

        private void UpdateMatchState()
        {
            var typed = ConfirmInput.Text?.Trim() ?? string.Empty;
            var matches = _expectedConfirmation == null ||
                          string.Equals(typed, _expectedConfirmation, StringComparison.OrdinalIgnoreCase);

            // Both gates must pass: the script name typed AND the statement ticked.
            PrimaryButton.IsEnabled = matches && Acknowledged;

            if (_expectedConfirmation == null)
            {
                return;
            }

            if (typed.Length == 0)
            {
                MatchText.Text = "Type the script name above to enable the button.";
                MatchText.Foreground = Brush("Ink500");
            }
            else if (matches && !Acknowledged)
            {
                MatchText.Text = "Name matches. Tick the statement above to enable the button.";
                MatchText.Foreground = Brush("WarningFg");
            }
            else if (matches)
            {
                MatchText.Text = "Name matches — this will run " + _expectedConfirmation + ".";
                MatchText.Foreground = Brush("GoodFg");
            }
            else
            {
                MatchText.Text = "Does not match " + _expectedConfirmation + " yet.";
                MatchText.Foreground = Brush("WarningFg");
            }
        }

        private Brush Brush(string key)
        {
            return (Brush)FindResource(key);
        }

        private void Apply(DialogKind kind)
        {
            string accent, background, glyph, kindLabel;

            switch (kind)
            {
                case DialogKind.Destructive:
                    accent = "CriticalFg"; background = "CriticalBg"; glyph = "\uE7BA";
                    kindLabel = "DESTRUCTIVE — CANNOT BE UNDONE";
                    break;
                case DialogKind.Warning:
                    accent = "WarningFg"; background = "WarningBg"; glyph = "\uE7BA";
                    kindLabel = null;
                    break;
                case DialogKind.Success:
                    accent = "GoodFg"; background = "GoodBg"; glyph = "\uE930";
                    kindLabel = null;
                    break;
                default:
                    accent = "InfoFg"; background = "InfoBg"; glyph = "\uE946";
                    kindLabel = null;
                    break;
            }

            AccentBar.Background = Brush(accent);
            GlyphChip.Background = Brush(background);
            GlyphText.Foreground = Brush(accent);
            GlyphText.Text = glyph;
            PrimaryButton.Background = Brush(accent);

            if (kindLabel != null)
            {
                KindChip.Visibility = Visibility.Visible;
                KindChip.Background = Brush(background);
                KindChip.BorderBrush = Brush(accent);
                KindText.Foreground = Brush(accent);
                KindText.Text = kindLabel;
            }
        }

        private void SetSection(
            FrameworkElement panel, System.Windows.Controls.ItemsControl list, IReadOnlyList<string> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            list.ItemsSource = items;
            panel.Visibility = Visibility.Visible;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>A message with a single acknowledgement button.</summary>
        public static void Show(
            Window owner, DialogKind kind, string title, string body, string footerNote = null)
        {
            var dialog = Build(owner, kind, title, body, null, null, null, footerNote, null, null);
            dialog.CancelButton.Visibility = Visibility.Collapsed;
            dialog.PrimaryButton.Content = "Close";
            dialog.ShowDialog();
        }

        /// <summary>
        /// Asks whether to proceed, laying out what the action does and the conditions under which
        /// it must not be run. Returns true only if the operator confirmed.
        /// </summary>
        public static bool Confirm(
            Window owner,
            DialogKind kind,
            string title,
            string body,
            IReadOnlyList<string> steps,
            IReadOnlyList<string> conditions,
            string command,
            string primaryLabel,
            string typedConfirmation = null,
            string footerNote = null,
            string acknowledgement = null,
            string stepsHeading = null,
            string conditionsHeading = null)
        {
            var dialog = Build(owner, kind, title, body, steps, conditions, command, footerNote,
                               stepsHeading, conditionsHeading);
            dialog.PrimaryButton.Content = primaryLabel ?? "Run it";

            if (!string.IsNullOrWhiteSpace(acknowledgement))
            {
                dialog.RequireAcknowledgement(acknowledgement);
            }

            if (!string.IsNullOrWhiteSpace(typedConfirmation))
            {
                dialog.RequireTypedConfirmation(typedConfirmation);
                dialog.ConfirmInput.Focus();
            }
            else if (!string.IsNullOrWhiteSpace(acknowledgement))
            {
                dialog.UpdateMatchState();
            }

            return dialog.ShowDialog() == true;
        }

        private static ActionDialog Build(
            Window owner,
            DialogKind kind,
            string title,
            string body,
            IReadOnlyList<string> steps,
            IReadOnlyList<string> conditions,
            string command,
            string footerNote,
            string stepsHeading,
            string conditionsHeading)
        {
            var dialog = new ActionDialog();
            dialog.TitleText.Text = title ?? string.Empty;
            dialog.Title = title ?? "WinUpgradeDiag";

            // Owner is set defensively. WPF throws if it is this dialog itself, and also if the
            // candidate parent has never been shown — which happens when something fails during
            // startup, including inside the unhandled-exception handler, precisely the moment a
            // dialog matters most. A dialog that cannot open is worse than an unparented one.
            var parent = owner ?? Application.Current?.MainWindow;
            if (parent != null && !ReferenceEquals(parent, dialog) && IsShown(parent))
            {
                dialog.Owner = parent;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            dialog.Apply(kind);

            if (!string.IsNullOrWhiteSpace(body))
            {
                dialog.BodyText.Text = body;
                dialog.BodyText.Visibility = Visibility.Visible;
            }

            // The default headings describe a remediation. Reused verbatim for, say, an export
            // result they read as nonsense — "IT WILL: report.html" — so callers can relabel them.
            if (!string.IsNullOrWhiteSpace(stepsHeading))
            {
                dialog.StepsHeading.Text = stepsHeading;
            }
            if (!string.IsNullOrWhiteSpace(conditionsHeading))
            {
                dialog.ConditionsHeading.Text = conditionsHeading;
            }

            dialog.SetSection(dialog.StepsPanel, dialog.StepsList, steps);
            dialog.SetSection(dialog.ConditionsPanel, dialog.ConditionsList, conditions);

            // "Cancel" is wrong next to a result that has already happened.
            if (kind == DialogKind.Success)
            {
                dialog.CancelButton.Content = "Close";
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                dialog.CommandText.Text = command;
                dialog.CommandPanel.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(footerNote))
            {
                dialog.FooterNote.Text = footerNote;
                dialog.FooterNote.Visibility = Visibility.Visible;
            }

            return dialog;
        }

        /// <summary>
        /// Whether a window has actually been shown. A Window only acquires a native handle when it
        /// is displayed, and assigning an un-shown window as an Owner throws.
        /// </summary>
        private static bool IsShown(Window window)
        {
            try
            {
                return new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Splits a paragraph into lines, for callers that only have free text.</summary>
        internal static IReadOnlyList<string> Lines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new string[0];
            }

            return text
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
    }
}
