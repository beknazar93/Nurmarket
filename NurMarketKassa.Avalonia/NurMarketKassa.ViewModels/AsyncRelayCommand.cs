using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels
{
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private int _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            Volatile.Read(ref _isExecuting) == 0 && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter) ||
                Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
            {
                return;
            }

            RaiseCanExecuteChanged();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                // async void: без catch исключение уходит в SynchronizationContext и роняет приложение.
                PosLogger.Log($"AsyncRelayCommand execute failed: {ex}", "ERROR");
            }
            finally
            {
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
