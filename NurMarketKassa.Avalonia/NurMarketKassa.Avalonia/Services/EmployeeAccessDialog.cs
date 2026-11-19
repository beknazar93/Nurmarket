using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-21, по прямой просьбе владельца ("клик по сотруднику открывал редактор
/// доступов, как «Управление доступами» на сайте"): редактирование доступов УЖЕ созданного
/// сотрудника — в отличие от AddEmployeeDialog (создание), здесь Email/Имя/Роль не меняются,
/// только чек-лист can_view_*. Тот же общий чек-лист, что и в AddEmployeeDialog, см.
/// EmployeeAccessChecklist. Текущие значения передаёт вызывающий код (EmployeesSettingsView) —
/// свежим GET api/users/employees/ и поиском по ServerId, см. doc-comment там же.
/// 2026-11-19 (запрос владельца, п. 3): если передан <c>saveAsync</c> — окно само отправляет доступы и показывает ход:
/// «Сохраняю…» (кнопка заблокирована), зелёная плашка «Доступы сохранены ✓» (окно закрывается само через <see cref="CloseDelayMs"/>)
/// или понятная причина с кнопкой «Повторить». Пока есть несохранённые правки — плашка «Есть несохранённые изменения» и яркая кнопка.</summary>
public sealed class EmployeeAccessDialog : Window
{
    /// <summary>Через сколько после успешного сохранения окно закрывается само.</summary>
    public static int CloseDelayMs = 1200;

    public EmployeeAccessFlags AccessFlags { get; private set; } = new();

    /// <summary>true — доступы отправлены на сервер самим окном (<c>saveAsync</c>), вызывающему повторять отправку не нужно.</summary>
    public bool Saved { get; private set; }

    private readonly EmployeeAccessChecklist.Boxes _accessBoxes;
    private readonly EmployeeAccessFlags _initial;
    private readonly Func<EmployeeAccessFlags, Task>? _saveAsync;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private bool _saving;

    public EmployeeAccessDialog(string employeeName, EmployeeAccessFlags current, Func<EmployeeAccessFlags, Task>? saveAsync = null)
    {
        _initial = current;
        _saveAsync = saveAsync;
        Title = Tr.T($"Доступы: {employeeName}", $"Укуктар: {employeeName}", $"Permissions: {employeeName}", $"Erişimler: {employeeName}", $"Huquqlar: {employeeName}");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (M 672) вместо системной рамки 560×620 — кнопки были 28 px,
        // Esc не закрывал. Чек-лист прокручивается в теле, «Отмена» и «Сохранить доступы» закреплены внизу; Esc и ✕ — «Отмена».
        SectionCards.AsModal(this, 760);

        var (accessView, accessBoxes) = EmployeeAccessChecklist.Build(current);
        _accessBoxes = accessBoxes;
        // Доступы — в общей прокрутке тела окна: своя прокрутка чек-листа внутри неё не нужна.
        var accessPanel = (Control)accessView.Content!;
        accessView.Content = null;

        _btnCancel = SectionCards.Cancel(CancelText);
        _btnCancel.Name = "AccessCancelButton";
        _btnCancel.Click += (_, _) => Close(Saved);
        _btnSave = SectionCards.Primary(SaveText, isDefault: true);
        _btnSave.Name = "AccessSaveButton";
        _btnSave.Click += async (_, _) => await SaveAsync();

        _bannerText = new TextBlock { FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _banner = new Border { Name = "AccessBanner", CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false, Child = _bannerText };

        _accessBoxes.Changed += RefreshDirty;
        RefreshDirty();

        var footer = new StackPanel { Spacing = 10, Children = { _banner, SectionCards.Buttons(_btnCancel, _btnSave) } };
        var shell = SectionCards.Shell(Title,
            Tr.T("Что сотрудник видит на сайте app.nurcrm.kg и в кассе.", "Кызматкер app.nurcrm.kg сайтында жана кассада эмнени көрөт.",
                "What the employee can see on app.nurcrm.kg and at the till.", "Personelin app.nurcrm.kg sitesinde ve kasada neleri göreceği.",
                "Xodim app.nurcrm.kg saytida va kassada nimani ko'radi."),
            accessPanel, footer);
        shell.InitialFocus = _accessBoxes.Cashbox;
        Content = shell;
    }

    private static string CancelText => Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
    private static string SaveText => Tr.T("Сохранить доступы", "Укуктарды сактоо", "Save permissions", "Erişimleri kaydet", "Huquqlarni saqlash");

    /// <summary>Есть ли правки относительно того, что пришло с сервера.</summary>
    public bool HasChanges => EmployeeAccessChecklist.Read(_accessBoxes).ChangedFields(_initial).Count > 0;

    private void RefreshDirty()
    {
        if (_saving || Saved)
            return;
        var dirty = HasChanges;
        _btnSave.Opacity = dirty ? 1 : 0.55; // «Сохранить» горит, пока есть что сохранять
        if (dirty)
            ShowBanner(Tr.T("Есть несохранённые изменения. Нажмите «Сохранить доступы».", "Сакталбаган өзгөрүүлөр бар. «Укуктарды сактоо» баскычын басыңыз.",
                "You have unsaved changes. Press “Save permissions”.", "Kaydedilmemiş değişiklikler var. «Erişimleri kaydet» düğmesine basın.", "Saqlanmagan o'zgarishlar bor. «Huquqlarni saqlash» tugmasini bosing."), "Warning");
        else
            _banner.IsVisible = false;
    }

    /// <summary>Плашка состояния: «Warning» (есть правки), «Info» (сохраняю), «Success», «Error». Цвета — токены темы.</summary>
    private void ShowBanner(string text, string kind)
    {
        _bannerText.Text = text;
        _banner.Tag = kind;
        _banner.IsVisible = true;
        var (bg, fg) = kind switch
        {
            "Success" => ("BrushSuccessSoft", "BrushSuccess"),
            "Error" => ("BrushDangerSoft", "BrushDangerOnSoft"),
            "Warning" => ("BrushWarningSoft", "BrushText"),
            _ => ("BrushPanelSoft", "BrushText"),
        };
        _banner.Bind(Border.BackgroundProperty, _banner.GetResourceObservable(bg));
        _bannerText.Bind(TextBlock.ForegroundProperty, _bannerText.GetResourceObservable(fg));
        Avalonia.Automation.AutomationProperties.SetName(_banner, text);
    }

    private async Task SaveAsync()
    {
        if (_saving || Saved)
            return;
        var flags = EmployeeAccessChecklist.Read(_accessBoxes);
        if (_saveAsync is null)
        {
            // прежний путь: окно только возвращает флаги, отправляет вызывающий код
            AccessFlags = flags;
            Close(true);
            return;
        }
        if (flags.ChangedFields(_initial).Count == 0)
        {
            Close(false); // менять нечего
            return;
        }

        _saving = true;
        _btnCancel.IsEnabled = false;
        _btnSave.IsEnabled = false;
        _btnSave.Opacity = 1;
        _btnSave.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                new ProgressBar { IsIndeterminate = true, Width = 32, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Tr.T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        ShowBanner(Tr.T("Сохраняю доступы…", "Укуктар сакталууда…", "Saving permissions…", "Erişimler kaydediliyor…", "Huquqlar saqlanmoqda…"), "Info");
        try
        {
            await _saveAsync(flags);
        }
        catch (Exception ex)
        {
            _saving = false;
            _btnCancel.IsEnabled = true;
            _btnSave.IsEnabled = true;
            _btnSave.Content = Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
            Avalonia.Automation.AutomationProperties.SetName(_btnSave, (string)_btnSave.Content);
            var reason = ex is ApiException api ? EmployeeApiMessages.Describe(api) : ServerOutageMonitor.FriendlyError(ex);
            ShowBanner(Tr.T("Доступы не сохранены. ", "Укуктар сакталган жок. ", "Permissions were not saved. ", "Erişimler kaydedilmedi. ", "Huquqlar saqlanmadi. ") + reason, "Error");
            return;
        }

        _saving = false;
        Saved = true;
        AccessFlags = flags;
        _btnSave.IsVisible = false;
        _btnCancel.IsEnabled = true;
        _btnCancel.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        ShowBanner(Tr.T("Доступы сохранены ✓", "Укуктар сакталды ✓", "Permissions saved ✓", "Erişimler kaydedildi ✓", "Huquqlar saqlandi ✓"), "Success");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CloseDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsVisible)
                Close(true);
        };
        timer.Start();
    }
}
