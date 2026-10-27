#if !NURANDROID
using System.Runtime.InteropServices;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services.RemoteSupport;

/// <summary>2026-10-26, владелец: «ПРИОРИТЕТ КЛИЕНТА ДОЛЖЕН БЫТЬ ВЫШЕ» — кассир всегда главнее оператора поддержки.
/// Узнаёт, что за кассой сейчас работает человек: мышь, клавиатура, сенсорный экран, экранная клавиатура osk.exe, сканер.
/// Способ: низкоуровневые хуки WH_MOUSE_LL и WH_KEYBOARD_LL в отдельном потоке с циклом сообщений. Свой ввод кассы
/// (<see cref="RemoteSupportHost"/> шлёт его через SendInput) помечен в dwExtraInfo меткой <see cref="OwnMarker"/>; всё, что
/// без метки, — ввод кассира. Флаг «injected» не используем: osk.exe, сенсорная мышь и RDP тоже «injected», а это
/// как раз кассир. Колбэк хука делает минимум (чтение двух полей и запись числа) и всегда зовёт CallNextHookEx — ввод
/// кассе не тормозится. Запасной способ (окно с повышенными правами, диспетчер задач, который открыл оператор, хук снят
/// системой): GetLastInputInfo — время последнего ввода в сеансе; если оно не совпало со временем нашего SendInput, это кассир.
/// Хуки живут только во время сеанса и только когда оператору дан ввод; снимаются в <see cref="Stop"/> и <see cref="Dispose"/>.
/// В колбэке код клавиши читается только для экстренного выхода Ctrl+Alt+Shift+Esc (<see cref="EmergencyStop"/>); нажатия
/// нигде не сохраняются и не передаются.</summary>
public sealed class ClientInputWatcher : IDisposable
{
    /// <summary>Метка в dwExtraInfo у ввода, который посылает сама касса по просьбе оператора ("NURS").</summary>
    public static readonly IntPtr OwnMarker = new(0x4E555253);

    /// <summary>Кассир нажал Ctrl+Alt+Shift+Esc (физически, не от оператора) — завершить сеанс. Вызывается из пула потоков.</summary>
    public event Action? EmergencyStop;

    // Окно, в которое запасной способ считает событие своим: ввод приходит в течение нескольких мс после SendInput.
    private const int OwnWindowAfterMs = 250, OwnWindowBeforeMs = 20;
    private const int MoveThresholdPx = 3; // дрожание датчика мыши (1–2 пикселя) вводом кассира не считается

    private readonly bool _simulated;
    private readonly object _gate = new();
    private long _lastClientTick = -1_000_000_000; // Environment.TickCount64 последнего ввода кассира
    private int _ownTick;                          // GetTickCount момента нашего SendInput
    private uint _seenLastInput;                   // последнее значение GetLastInputInfo
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private int _emergencyBusy;
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    private bool _hooksOk;
    // Делегаты хранятся в полях: иначе сборщик мусора уберёт их, пока хук жив.
    private HookProc? _mouseProc, _keyProc;
    private IntPtr _hMouse, _hKey;
    private bool _fallback;

    /// <param name="simulated">Стенд: хуки и GetLastInputInfo не используются, ввод кассира подаётся вызовом <see cref="NoteClientInput"/>.</param>
    public ClientInputWatcher(bool simulated = false) => _simulated = simulated;

    /// <summary>Хуки поставлены (иначе работает только запасной способ).</summary>
    public bool HooksInstalled => _hooksOk;

    /// <summary>Записать «сейчас кассир что-то сделал» (хуки, запасной способ, стенд).</summary>
    public void NoteClientInput() => Volatile.Write(ref _lastClientTick, Environment.TickCount64);

    /// <summary>Вызывается перед каждым нашим SendInput: запасной способ не примет его за ввод кассира.</summary>
    public void NoteOwnInput() => Volatile.Write(ref _ownTick, Environment.TickCount);

    /// <summary>Кассир работал не позже чем graceMs миллисекунд назад.</summary>
    public bool IsActive(int graceMs)
    {
        PollLastInput();
        return Environment.TickCount64 - Volatile.Read(ref _lastClientTick) < graceMs;
    }

    /// <summary>Начать наблюдение (в потоке не обязательно с циклом сообщений — свой поток создаётся здесь). Не бросает исключений.</summary>
    public void Start()
    {
        if (_simulated || !OperatingSystem.IsWindows())
            return;
        lock (_gate)
        {
            if (_thread is not null)
                return;
            try
            {
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (GetLastInputInfo(ref lii))
                {
                    _seenLastInput = lii.dwTime;
                    _fallback = true;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Удалённая помощь: GetLastInputInfo недоступен ({ex.Message}).", "WARNING");
            }
            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(HookThread) { IsBackground = true, Name = "RemoteSupportClientInput", Priority = ThreadPriority.Highest };
            _thread.Start();
        }
        _ready.Wait(3000);
    }

    /// <summary>Снять хуки и остановить поток. Можно вызывать много раз.</summary>
    public void Stop()
    {
        Thread? t;
        uint id;
        lock (_gate)
        {
            t = _thread;
            id = _threadId;
            _thread = null;
        }
        if (t is null)
            return;
        try
        {
            // Поток мог ещё не дойти до цикла сообщений — повторяем, пока он не завершится.
            for (var i = 0; i < 20 && t.IsAlive; i++)
            {
                if (id != 0)
                    PostThreadMessage(id, (uint)WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
                if (t.Join(100))
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: хуки ввода не сняты штатно ({ex.Message}).", "WARNING");
        }
        _hooksOk = false;
        _fallback = false;
    }

    public void Dispose() => Stop();

    // ---------- запасной способ ----------

    private void PollLastInput()
    {
        if (!_fallback)
            return;
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
                return;
            var t = lii.dwTime;
            if (t == Interlocked.Exchange(ref _seenLastInput, t))
                return;
            // Разница между временем ввода и временем нашего SendInput: около нуля — это мы, иначе — кассир.
            var diff = unchecked((int)(t - (uint)Volatile.Read(ref _ownTick)));
            if (diff < -OwnWindowBeforeMs || diff > OwnWindowAfterMs)
                NoteClientInput();
        }
        catch
        {
            // запасной способ недоступен — остаются хуки
        }
    }

    // ---------- хуки ----------

    private void HookThread()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            // Создаём очередь сообщений потока до того, как о нём узнают (PostThreadMessage без очереди не доходит).
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            var module = GetModuleHandle(null);
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            _hMouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _hKey = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            _hooksOk = _hMouse != IntPtr.Zero && _hKey != IntPtr.Zero;
            if (!_hooksOk)
                PosLogger.Log($"Удалённая помощь: хуки ввода не поставлены (мышь {(_hMouse != IntPtr.Zero ? "да" : "нет")}, клавиатура {(_hKey != IntPtr.Zero ? "да" : "нет")}) — работает запасной способ.", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: хуки ввода не поставлены ({ex.Message}) — работает запасной способ.", "WARNING");
        }
        finally
        {
            _ready?.Set();
        }
        if (_hMouse == IntPtr.Zero && _hKey == IntPtr.Zero)
            return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: цикл хуков ввода остановлен ({ex.Message}).", "WARNING");
        }
        finally
        {
            if (_hMouse != IntPtr.Zero)
                UnhookWindowsHookEx(_hMouse);
            if (_hKey != IntPtr.Zero)
                UnhookWindowsHookEx(_hKey);
            _hMouse = _hKey = IntPtr.Zero;
        }
    }

    // MSLLHOOKSTRUCT: pt(8) mouseData(4) flags(4) time(4) [выравнивание] dwExtraInfo — смещение 24 (x64) или 20 (x86).
    private static readonly int MouseExtraOffset = IntPtr.Size == 8 ? 24 : 20;

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            try
            {
                var x = Marshal.ReadInt32(lParam, 0);
                var y = Marshal.ReadInt32(lParam, 4);
                var moved = Math.Abs(x - _lastX) + Math.Abs(y - _lastY) >= MoveThresholdPx;
                _lastX = x;
                _lastY = y;
                if (Marshal.ReadIntPtr(lParam, MouseExtraOffset) != OwnMarker && ((int)wParam != WM_MOUSEMOVE || moved))
                    NoteClientInput();
            }
            catch
            {
                // исключение из колбэка уронило бы процесс
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // KBDLLHOOKSTRUCT: vkCode(4) scanCode(4) flags(4) time(4) dwExtraInfo — смещение 16.
    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            try
            {
                if (Marshal.ReadIntPtr(lParam, 16) != OwnMarker)
                {
                    NoteClientInput();
                    var msg = (int)wParam;
                    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && Marshal.ReadInt32(lParam, 0) == VK_ESCAPE
                        && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
                        && Interlocked.Exchange(ref _emergencyBusy, 1) == 0)
                    {
                        // Не делаем работу в колбэке: событие — из пула потоков.
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                EmergencyStop?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                PosLogger.Log($"Удалённая помощь: экстренный выход не сработал ({ex.Message}).", "WARNING");
                            }
                            finally
                            {
                                Volatile.Write(ref _emergencyBusy, 0);
                            }
                        });
                    }
                }
            }
            catch
            {
                // исключение из колбэка уронило бы процесс
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // ---------- Win32 ----------

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, HC_ACTION = 0;
    private const int WM_QUIT = 0x0012, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_MOUSEMOVE = 0x0200;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
#endif
