using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-19 («Смета», ТЗ 19.11.2026): зависимости окон сметы. По умолчанию боевые (локальное хранилище, каталог кассы, касса, принтер чеков, WhatsApp); тест подставляет свои
/// (временный файл, свой каталог, заглушку кассы и печати). Кассы нет (<c>Till == null</c>, программа владельца) — «В чек» не показывается.</summary>
public sealed class EstimateDeps
{
    public EstimateStore Store { get; init; } = EstimateStore.Default;
    public IEstimateCatalog Catalog { get; init; } = new EstimateCatalog();
    public IEstimateTill? Till { get; init; } = EstimateTill.Resolve();
    /// <summary>Печать текста на ленту чеков (в программе владельца — сообщение «печать в кассе»).</summary>
    public Func<string, Task<string?>> PrintRibbon { get; init; } = ServiceDeskTill.PrintReceiptText;
    /// <summary>Открыть WhatsApp с готовым текстом: телефон, текст.</summary>
    public Action<string?, string> OpenWhatsApp { get; init; } = PurchaseDocs.OpenWhatsApp;
    /// <summary>Скопировать текст в буфер обмена. null — окно копирует само (Clipboard окна).</summary>
    public Func<string, Task>? CopyText { get; init; }
    /// <summary>null — обычные вопросы кассы (PosDialogs); тест подставляет ответ.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; init; }
    /// <summary>Закупка и маржа видны только владельцу и сотрудникам с правом на закупку (как в приёмке).</summary>
    public Func<bool> CanSeeCost { get; init; } = DefaultCanSeeCost;
    /// <summary>«Нормы расхода» правит владелец или сотрудник с правом «Настройки» (нормы общие для всех касс).</summary>
    public Func<bool> CanEditNorms { get; init; } = DefaultCanEditNorms;
    /// <summary>Клиенты базы для подсказки в шапке сметы (сеть); сбой — клиента вписывают вручную.</summary>
    public Func<Task<List<ClientDocsUi.Buyer>>> Buyers { get; init; } = () => ClientDocsUi.BuyersAsync();
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;
    public Func<string> Cashier { get; init; } = () => NurMarketKassa.PosApp.CurrentUserDisplayName ?? "";

    private static bool Has(string permission)
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(permission);
        }
        catch
        {
            return false;
        }
    }

    private static bool DefaultCanSeeCost() => AppMode.IsOwner || Has(PosPermissions.ViewProcurement);

    private static bool DefaultCanEditNorms() => AppMode.IsOwner || Has(PosPermissions.ViewSettings);
}

/// <summary>Общие кирпичики окон сметы (поля, подписи): цвета — только ресурсы темы, надписи — Tr.T на 5 языках, высоты 40 (сенсор).</summary>
internal static class EstimateUi
{
    public static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static TextBox Box(string name, string text, double width, string label, string? watermark = null, int maxLength = 200, bool right = false)
    {
        var box = new TextBox
        {
            Name = name, Text = text, Width = width, MaxLength = maxLength, Height = 40, FontSize = 14, CornerRadius = new CornerRadius(10),
            VerticalContentAlignment = VerticalAlignment.Center, Watermark = watermark,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        };
        ServiceDeskUi.Use(box, TemplatedControlBackground, "BrushInput");
        ServiceDeskUi.Use(box, TemplatedControlForeground, "BrushText");
        ServiceDeskUi.Use(box, TemplatedControlBorder, "BrushInputBorder");
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        return box;
    }

    private static readonly AvaloniaProperty TemplatedControlBackground = Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty;
    private static readonly AvaloniaProperty TemplatedControlForeground = Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty;
    private static readonly AvaloniaProperty TemplatedControlBorder = Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty;

    /// <summary>Подпись над полем.
    /// 2026-11-20: владелец увидел, что поля шапки сметы налезают друг на друга: у поля была своя Width (260) больше ширины ячейки (240) и оно выходило за ячейку под соседа,
    /// а подпись сидела вплотную к рамке. Теперь поле всегда растягивается по ширине ячейки (своя Width снимается), подпись — отдельной строкой с отступом 4 px (Д-8: 12,5).</summary>
    public static StackPanel Labeled(string label, Control field, double width)
    {
        var p = new StackPanel { Width = width, Margin = new Thickness(0, 0, 10, 8) };
        p.Children.Add(ServiceDeskUi.Text(label, 12.5, brush: "BrushTextSoft"));
        p.Children[0].Margin = new Thickness(0, 0, 0, 4);
        field.Width = double.NaN;
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        p.Children.Add(field);
        return p;
    }

    public static string Day(DateTime d) => d.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static string Qty(decimal v) => MoneyText.Weight(v, "0.##");

    /// <summary>Число для поля ввода: «12,5» (запятая, без хвоста нулей).</summary>
    public static string Num(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    public static string StatusName(EstimateStatus s) => s switch
    {
        EstimateStatus.Sent => T("Отправлена", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi"),
        EstimateStatus.Accepted => T("Принята", "Кабыл алынды", "Accepted", "Kabul edildi", "Qabul qilindi"),
        _ => T("Черновик", "Долбоор", "Draft", "Taslak", "Qoralama"),
    };

    public static string Title => T("Смета", "Смета", "Estimate", "Keşif", "Smeta");

    /// <summary>Подсказка про локальное хранение (ТЗ 1): смета не появляется на других кассах.</summary>
    public static string LocalHint => T("Сметы хранятся только на этом компьютере и не передаются на другие кассы.",
        "Сметалар ушул компьютерде гана сакталат жана башка кассаларга берилбейт.",
        "Estimates are stored on this computer only and are not shared with other tills.",
        "Keşifler yalnızca bu bilgisayarda saklanır, diğer kasalara aktarılmaz.",
        "Smetalar faqat shu kompyuterda saqlanadi va boshqa kassalarga uzatilmaydi.");
}
