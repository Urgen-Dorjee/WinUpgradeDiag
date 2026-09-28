using System;
using System.Windows.Input;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>
    /// A command that receives the bound item as its parameter — used by the Tools list, where
    /// each row needs to run its own tool rather than a single selected one.
    /// </summary>
    public sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            if (_canExecute == null)
            {
                return true;
            }

            return parameter is T || parameter == null
                ? _canExecute(parameter is T ? (T)parameter : default(T))
                : false;
        }

        public void Execute(object parameter)
        {
            _execute(parameter is T ? (T)parameter : default(T));
        }
    }
}
