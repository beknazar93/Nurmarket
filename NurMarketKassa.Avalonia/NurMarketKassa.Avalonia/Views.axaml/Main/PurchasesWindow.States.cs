using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (аудит «Закупки — удобство и дизайн», БП-7): реестр «История закупок» не загрузился — вместо красной строки с техническим текстом сервера
/// карточка «Нет связи с NurCRM» (время последней удачной загрузки, кнопка «Повторить»), а сводка «Всего закупок» показывает «—», а не обманчивый 0.
/// Технический текст — в журнал и в подсказку карточки.</summary>
public partial class PurchasesWindow
{
    private string? _receiptsLoadError;
    private DateTime? _receiptsLoadedAt;

    /// <summary>Есть ошибка загрузки и показать нечего — цифры сводки неизвестны («—»).</summary>
    private bool ReceiptsUnknown => _receiptsLoadError is not null && _receipts.Count == 0;

    /// <summary>Загрузка удалась.</summary>
    private void ReceiptsLoaded()
    {
        _receiptsLoadError = null;
        _receiptsLoadedAt = DateTime.Now;
    }

    /// <summary>Загрузка не удалась: причина для подсказки, строка статуса скрыта — её заменяет карточка в списке.</summary>
    private void ReceiptsLoadFailed(Exception ex)
    {
        _receiptsLoadError = ex is OperationCanceledException
            ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi")
            : ServerTelegramBotApi.DescribeFields(ex);
        // Список прошлой загрузки на экране остаётся — честно говорим, что он не обновился (без технического текста).
        SetStatus(_receipts.Count == 0 ? null
            : _receiptsLoadedAt is { } at
                ? T($"Нет связи с NurCRM — показан список на {at:HH:mm}. Нажмите «Обновить».", $"NurCRM менен байланыш жок — {at:HH:mm} абалындагы тизме көрсөтүлдү. «Жаңыртууну» басыңыз.", $"No connection to NurCRM — showing the list as of {at:HH:mm}. Press “Refresh”.", $"NurCRM ile bağlantı yok — {at:HH:mm} itibarıyla liste gösteriliyor. «Yenile»ye basın.", $"NurCRM bilan aloqa yo'q — {at:HH:mm} holatidagi ro'yxat ko'rsatildi. «Yangilash»ni bosing.")
                : T("Нет связи с NurCRM — список не обновился. Нажмите «Обновить».", "NurCRM менен байланыш жок — тизме жаңыланган жок. «Жаңыртууну» басыңыз.", "No connection to NurCRM — the list was not refreshed. Press “Refresh”.", "NurCRM ile bağlantı yok — liste yenilenmedi. «Yenile»ye basın.", "NurCRM bilan aloqa yo'q — ro'yxat yangilanmadi. «Yangilash»ni bosing."), error: true);
        RenderKpi();
    }

    /// <summary>Карточка «Нет связи с NurCRM» + «Повторить» (null — ошибки нет или список уже есть).</summary>
    private Control? ReceiptsErrorCard()
    {
        if (!ReceiptsUnknown)
            return null;
        var title = new TextBlock { Text = T("Нет связи с NurCRM", "NurCRM менен байланыш жок", "No connection to NurCRM", "NurCRM ile bağlantı yok", "NurCRM bilan aloqa yo'q"), FontSize = 16, FontWeight = FontWeight.SemiBold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var when = _receiptsLoadedAt is { } at
            ? T($"Закупки не загрузились. Последняя удачная загрузка — в {at:HH:mm}.", $"Сатып алуулар жүктөлгөн жок. Акыркы ийгиликтүү жүктөө — {at:HH:mm}.", $"Purchases didn't load. Last successful load — at {at:HH:mm}.", $"Satın almalar yüklenmedi. Son başarılı yükleme — {at:HH:mm}.", $"Xaridlar yuklanmadi. Oxirgi muvaffaqiyatli yuklash — {at:HH:mm}.")
            : T("Закупки не загрузились. Проверьте интернет и нажмите «Повторить».", "Сатып алуулар жүктөлгөн жок. Интернетти текшерип, «Кайталоону» басыңыз.", "Purchases didn't load. Check the internet and press “Retry”.", "Satın almalar yüklenmedi. İnterneti kontrol edip «Tekrar dene»ye basın.", "Xaridlar yuklanmadi. Internetni tekshirib, «Qayta urinish»ni bosing.");
        var text = new TextBlock { Text = when, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        Use(text, TextBlock.ForegroundProperty, "BrushTextSoft");
        var retry = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
        retry.Focusable = true;
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        Avalonia.Automation.AutomationProperties.SetName(retry, T("Повторить загрузку закупок", "Сатып алууларды кайра жүктөө", "Retry loading purchases", "Satın almaları yeniden yükle", "Xaridlarni qayta yuklash"));
        retry.Click += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16), Margin = new Thickness(0, 8),
            Child = new StackPanel { Children = { title, text, retry } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanelSoft");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        ToolTip.SetTip(card, _receiptsLoadError); // техническая причина — только в подсказке (и в журнале)
        Avalonia.Automation.AutomationProperties.SetName(card, title.Text + ". " + when);
        return card;
    }
}
