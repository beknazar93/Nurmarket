// ShiftService.cs
using System;
using System.ComponentModel;

namespace NurMarketKassa.Services
{
    public static class ShiftService
    {
        private static bool _isShiftOpen = false;
        public static event Action<bool>? ShiftStateChanged;

        public static bool IsShiftOpen
        {
            get => _isShiftOpen;
            set
            {
                if (_isShiftOpen != value)
                {
                    _isShiftOpen = value;
                    ShiftStateChanged?.Invoke(_isShiftOpen);
                }
            }
        }
    }
}