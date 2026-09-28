using System.Windows;
using WinUpgradeDiag.App.Controls;

namespace WinUpgradeDiag.App
{
    /// <summary>
    /// A single-line prompt for a value the operator has to supply — a machine name, a content id,
    /// a package id. Shows an example of the expected shape and, where it is not obvious, where to
    /// go and find the real value.
    /// </summary>
    public partial class PromptWindow : Window
    {
        public PromptWindow(
            string title,
            string prompt,
            string initialValue,
            string example = null,
            string help = null)
        {
            InitializeComponent();

            Title = title;
            PromptText.Text = prompt;
            ValueInput.Text = initialValue ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(example))
            {
                PlaceholderService.SetText(ValueInput, example);
            }

            if (!string.IsNullOrWhiteSpace(help))
            {
                HelpText.Text = help;
                HelpText.Visibility = Visibility.Visible;
            }

            ValueInput.TextChanged += (s, e) => Validate();
            Validate();

            ValueInput.Focus();
            ValueInput.SelectAll();
        }

        /// <summary>What the operator typed, valid only when the dialog returned true.</summary>
        public string Value { get; private set; }

        /// <summary>
        /// Blocks OK on an empty value rather than accepting it and letting the runner refuse a
        /// moment later. Failing at the point of entry is easier to act on than failing after.
        /// </summary>
        private void Validate()
        {
            var empty = string.IsNullOrWhiteSpace(ValueInput.Text);
            OkButton.IsEnabled = !empty;
            ValidationText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ValidationText.Text = empty ? "A value is required to continue." : string.Empty;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ValueInput.Text))
            {
                return;
            }

            Value = ValueInput.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
