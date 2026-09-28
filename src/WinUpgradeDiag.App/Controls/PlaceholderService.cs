using System.Windows;
using System.Windows.Controls;

namespace WinUpgradeDiag.App.Controls
{
    /// <summary>
    /// Adds placeholder text to a <see cref="TextBox"/>, which WPF has no built-in support for.
    /// <para>
    /// An empty box with a label above it tells the operator what the field is for, but not what a
    /// valid value looks like. For fields like a ConfigMgr content id or a task sequence package
    /// id, the shape of the value is the part people get wrong, and getting it wrong here feeds a
    /// bad argument to a destructive script.
    /// </para>
    /// </summary>
    public static class PlaceholderService
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(PlaceholderService),
                new PropertyMetadata(string.Empty));

        public static string GetText(DependencyObject element)
        {
            return (string)element.GetValue(TextProperty);
        }

        public static void SetText(DependencyObject element, string value)
        {
            element.SetValue(TextProperty, value);
        }

        /// <summary>
        /// True while the box is empty, so the template can show the placeholder. Maintained by the
        /// box itself rather than a converter, because a binding on Text.Length cannot see an
        /// empty string and a null interchangeably.
        /// </summary>
        public static readonly DependencyProperty IsEmptyProperty =
            DependencyProperty.RegisterAttached(
                "IsEmpty",
                typeof(bool),
                typeof(PlaceholderService),
                new PropertyMetadata(true));

        public static bool GetIsEmpty(DependencyObject element)
        {
            return (bool)element.GetValue(IsEmptyProperty);
        }

        public static void SetIsEmpty(DependencyObject element, bool value)
        {
            element.SetValue(IsEmptyProperty, value);
        }

        /// <summary>
        /// Attach to keep <see cref="IsEmptyProperty"/> in step with the box's contents. Set
        /// automatically for every TextBox by the style in Theme.xaml.
        /// </summary>
        public static readonly DependencyProperty MonitorProperty =
            DependencyProperty.RegisterAttached(
                "Monitor",
                typeof(bool),
                typeof(PlaceholderService),
                new PropertyMetadata(false, OnMonitorChanged));

        public static bool GetMonitor(DependencyObject element)
        {
            return (bool)element.GetValue(MonitorProperty);
        }

        public static void SetMonitor(DependencyObject element, bool value)
        {
            element.SetValue(MonitorProperty, value);
        }

        private static void OnMonitorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = d as TextBox;
            if (box == null)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                box.TextChanged += OnTextChanged;
                SetIsEmpty(box, string.IsNullOrEmpty(box.Text));
            }
            else
            {
                box.TextChanged -= OnTextChanged;
            }
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var box = (TextBox)sender;
            SetIsEmpty(box, string.IsNullOrEmpty(box.Text));
        }
    }
}
