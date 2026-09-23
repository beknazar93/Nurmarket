using System;
using System.Windows.Input;
using NurMarketKassa.Services;

#nullable disable

namespace NurMarketKassa.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter)
        {
            try
            {
                _execute();
            }
            catch (Exception ex)
            {
                // Клик по кнопке идёт синхронно через диспетчер Avalonia; без catch
                // необработанное исключение здесь может тихо "проглотиться" UI-циклом
                // (кнопка выглядит нерабочей) вместо явного сообщения об ошибке.
                PosLogger.Log($"RelayCommand execute failed: {ex}", "ERROR");
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}