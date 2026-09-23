using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels
{
    public sealed class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private readonly Func<T?, bool>? _canExecute;
        private int _isExecuting;

        public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            Volatile.Read(ref _isExecuting) == 0 && (_canExecute?.Invoke(parameter is T t ? t : default) ?? true);

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
                await _execute(parameter is T t ? t : default);
            }
            catch (Exception ex)
            {
                // async void: без catch исключение уходит в SynchronizationContext и роняет приложение.
                PosLogger.Log($"AsyncRelayCommand<T> execute failed: {ex}", "ERROR");
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
