using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): кнопки под карточкой чека в программе владельца
/// («Продажи и финансы» → «Чеки», «Возвраты», «История», отдельные «Продажи» и «Финансы», «Сводка»):
/// <list type="bullet">
/// <item>«Возврат» — то же окно возврата, что в кассе, сразу с этим чеком (ReturnSaleLauncher: право «Возврат», без кассовой смены — подтверждение,
/// полный/частичный возврат по строкам, причина, чек в долг — по правилам кассы, срок возврата, PIN старшего от суммы); у чека, возвращённого целиком, — неактивна с пояснением;</item>
/// <item>«Правка возвратов» — список возвратов этого чека с «Редактировать / Удалить / История» (24 ч, PIN руководителя, признак сервера pos_return_edit_24h — CustomerReturnsEntry).</item>
/// </list>
/// Только в программе владельца (<see cref="Enabled"/>): в кассе эти окна прежние (там возврат — из «Истории чеков» и меню кассы).</summary>
public sealed class ReceiptReturnActions
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Кнопки показываются только в программе владельца — касса не меняется.</summary>
    public static bool Enabled => AppMode.IsOwner;

    private readonly CustomerReturnsEntry _entry = new();
    private readonly TextBlock _fullHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "ReceiptReturnFullHint" };
    private string _saleId = "";
    private string _receipt = "";
    private bool _full;
    private bool _busy;

    public StackPanel View { get; } = new() { Spacing = 4, IsVisible = false, Margin = new Thickness(0, 0, 0, 8) };
    public Button ReturnButton { get; }

    /// <summary>Окно-владелец для окна возврата и списка возвратов (карточка чека — Popup, своего Window у неё нет).</summary>
    public Func<Window?>? OwnerWindow { get; set; }

    /// <summary>Перед открытием окна (закрыть всплывающую карточку чека, иначе она поверх окна возврата).</summary>
    public Action? BeforeOpen { get; set; }

    /// <summary>После возврата или правки возврата — перечитать список, итоги и долг.</summary>
    public Func<Task>? Changed { get; set; }

    public ReceiptReturnActions()
    {
        ReturnButton = new Button
        {
            Content = "↩  " + ReturnSaleLauncher.ButtonText,
            Name = "ReceiptReturnButton", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false,
        };
        ReturnButton.Classes.Add("sec-outline");
        ReturnButton.Click += async (_, _) => await OpenReturnAsync().ConfigureAwait(true);
        _fullHint.Bind(TextBlock.ForegroundProperty, _fullHint.GetResourceObservable("BrushTextSoft"));
        _entry.OwnerWindow = () => OwnerWindow?.Invoke();
        _entry.BeforeOpen = () => BeforeOpen?.Invoke();
        _entry.Changed = () => Changed?.Invoke() ?? Task.CompletedTask;
        _entry.View.Margin = new Thickness(0);
        // 2026-11-12 (аудит 12.11, Н-6): «Возврат» и «Правка возвратов» — в один ряд (было 5 рядов кнопок, чек на 1024×600 виден на ~260 px); пояснения — под рядом
        (_entry.Button.Parent as Panel)?.Children.Remove(_entry.Button);
        _row.Children.Add(ReturnButton);
        _row.Children.Add(_entry.Button);
        Grid.SetColumn(_entry.Button, 2);
        ReturnButton.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) LayoutRow(); };
        _entry.Button.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) LayoutRow(); };
        _noRight.Bind(TextBlock.ForegroundProperty, _noRight.GetResourceObservable("BrushTextSoft"));
        _noRight.Text = "ⓘ " + NoRightText;
        View.Children.Add(_row);
        View.Children.Add(_fullHint);
        View.Children.Add(_noRight);
        View.Children.Add(_entry.View);
        LayoutRow();
    }

    private readonly Grid _row = new() { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
    private readonly TextBlock _noRight = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "ReceiptReturnNoRight" };

    /// <summary>Видна одна кнопка — на всю ширину, обе — пополам.</summary>
    private void LayoutRow()
    {
        var both = ReturnButton.IsVisible && _entry.Button.IsVisible;
        Grid.SetColumnSpan(ReturnButton, both ? 1 : 3);
        Grid.SetColumn(_entry.Button, both ? 2 : 0);
        Grid.SetColumnSpan(_entry.Button, both ? 1 : 3);
        _row.IsVisible = ReturnButton.IsVisible || _entry.Button.IsVisible;
    }

    /// <summary>2026-11-12 (аудит 12.11, Н-9): нет права «Возврат» — раньше блок был пуст без пояснения.</summary>
    public static string NoRightText => T(
        "Возврат по чеку недоступен: у вашей учётной записи нет права «Возврат». Его включает владелец на сайте NurCRM (Сотрудники → права).",
        "Чек боюнча кайтаруу жеткиликсиз: каттоо эсебиңизде «Кайтаруу» укугу жок. Аны ээси NurCRM сайтында күйгүзөт (Кызматкерлер → укуктар).",
        "Returning a receipt is unavailable: your account has no “Return” permission. The owner enables it on the NurCRM website (Employees → permissions).",
        "Fiş iadesi kullanılamaz: hesabınızda «İade» yetkisi yok. Sahibi bunu NurCRM sitesinde açar (Çalışanlar → yetkiler).",
        "Chek bo'yicha qaytarish mavjud emas: hisobingizda «Qaytarish» huquqi yo'q. Uni egasi NurCRM saytida yoqadi (Xodimlar → huquqlar).");

    /// <summary>Кнопка «Правка возвратов» (для тестов и подписи).</summary>
    public Button ReturnsEditButton => _entry.Button;

    public void Hide()
    {
        _saleId = "";
        _receipt = "";
        _entry.Hide();
        View.IsVisible = ReturnButton.IsVisible = _fullHint.IsVisible = _noRight.IsVisible = false;
    }

    /// <summary>Показать кнопки для чека. <paramref name="status"/>/<paramref name="returnStatus"/> — как их отдал сервер (status, return_status).</summary>
    public void ShowFor(string? saleId, string? receiptNumber, string? status, string? returnStatus)
    {
        var id = (saleId ?? "").Trim();
        if (!Enabled || id.Length == 0 || id.StartsWith("local", StringComparison.Ordinal))
        {
            Hide();
            return;
        }
        _saleId = id;
        _receipt = string.IsNullOrWhiteSpace(receiptNumber) || receiptNumber == "—" ? "" : receiptNumber.Trim();
        _full = ReturnSaleLauncher.IsFullyReturned(status, returnStatus);
        var can = ReturnSaleLauncher.CanReturn();
        ReturnButton.IsVisible = can;
        ReturnButton.IsEnabled = !_full && !_busy;
        ToolTip.SetTip(ReturnButton, _full ? ReturnSaleLauncher.FullyReturnedHint : null);
        Avalonia.Automation.AutomationProperties.SetName(ReturnButton, ReturnSaleLauncher.ButtonText + (_receipt.Length > 0 ? " — " + _receipt : ""));
        _fullHint.Text = "ⓘ " + ReturnSaleLauncher.FullyReturnedHint;
        _fullHint.IsVisible = can && _full; // на сенсорном экране подсказки (ToolTip) нет — пояснение строкой
        _noRight.IsVisible = !can; // 2026-11-12 (Н-9)
        View.IsVisible = true;
        _ = _entry.LoadForAsync(_saleId, _receipt);
    }

    private async Task OpenReturnAsync()
    {
        if (_busy || _saleId.Length == 0 || OwnerWindow?.Invoke() is not { } owner)
            return;
        _busy = true;
        ReturnButton.IsEnabled = false;
        var saleId = _saleId;
        try
        {
            BeforeOpen?.Invoke();
            if (await OpenReturnAsync(owner, saleId, _receipt).ConfigureAwait(true) && Changed is { } changed)
                await changed().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Программа владельца: возврат чека {saleId} не открылся ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            _busy = false;
            ReturnButton.IsEnabled = !_full;
        }
    }

    /// <summary>Окно возврата сразу с этим чеком (общий ReturnSaleLauncher). true — окно было открыто (список перечитать).</summary>
    public static Task<bool> OpenReturnAsync(Window owner, string saleId, string? receiptNumber) =>
        ReturnSaleLauncher.OpenAsync(owner, saleId, string.IsNullOrWhiteSpace(receiptNumber) ? null : receiptNumber);

    /// <summary>«Возвраты по чеку» (правка/аннулирование 24 ч). Без признака сервера — пояснение, почему правки нет.</summary>
    public static async Task<bool> OpenReturnsListAsync(Window owner, string saleId, string? receiptNumber)
    {
        if (!ReturnEditGate.FeatureOn(ReturnDocKind.Customer))
        {
            await PosMessageBox.ShowModalAsync(owner, ReturnsEditUnavailableText, ReturnSaleLauncher.ButtonText, System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information).ConfigureAwait(true);
            return false;
        }
        return await ReturnEditFlows.ShowCustomerReturnsAsync(owner, saleId, receiptNumber ?? "").ConfigureAwait(true);
    }

    /// <summary>Пункт «Возвраты по чеку» (меню строки в «Сводке»).</summary>
    public static string ReturnsListText => T("Возвраты по чеку", "Чек боюнча кайтаруулар", "Returns on the receipt", "Fişteki iadeler", "Chek bo'yicha qaytarishlar");

    /// <summary>Правка возвратов на сервере не включена (нет признака pos_return_edit_24h).</summary>
    public static string ReturnsEditUnavailableText => T(
        "Правка и удаление возвратов (24 ч) пока не включены сервером NurCRM для вашей компании. Сам возврат по чеку работает.",
        "Кайтарууларды оңдоо жана өчүрүү (24 саат) NurCRM сервери тарабынан компанияңыз үчүн азырынча күйгүзүлгөн эмес. Чек боюнча кайтаруунун өзү иштейт.",
        "Editing and deleting returns (24 h) is not yet enabled by the NurCRM server for your company. Returning a receipt itself works.",
        "İadeleri düzenleme ve silme (24 sa) NurCRM sunucusu tarafından şirketiniz için henüz açılmadı. Fiş iadesinin kendisi çalışır.",
        "Qaytarishlarni tahrirlash va o'chirish (24 soat) NurCRM serveri tomonidan kompaniyangiz uchun hali yoqilmagan. Chek bo'yicha qaytarishning o'zi ishlaydi.");
}
