using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11, запрос владельца «убрать пункт весов из кассы и поставить на владелец»: раздел программы владельца «Товары → Весы».
/// Те же окна и тот же код, что были в «Настройки → Весы» кассы (ScaleSettingsView): список весов с этикетками (Штрих-М/Штрих-ПРИНТ, Штрих РС-200,
/// Rongta, TM-30F/Dahua, AI), «Настроить…», «Отправить товары» (окно «Отправка товаров на весы» — ScalesPluWindow), инструкции подключения и
/// «Штрих-код этикеток». 2026-11-12, решение владельца (аудит, пакет 3): только две вкладки — «Весы с этикетками» и «Штрих-код: вес / сумма»;
/// «Весы на кассе» (COM) и «Табло цены» остались в кассе (COM-порты держит она). Платный доступ — как раньше (TariffGate.CanUseScales),
/// пункт меню скрывается при его отсутствии.</summary>
public sealed class ScalesSectionWindow : Window, IOwnerSection
{
    public ScalesSectionWindow()
    {
        Title = Tr.T("Весы", "Тараза", "Scales", "Tartı", "Tarozi");
        Width = 1000;
        Height = 760;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        // OwnerSectionMode — до показа: страница в режиме программы владельца (две вкладки: весы с этикетками и штрих-код).
        Content = new ScaleSettingsView { OwnerSectionMode = true };
    }

    /// <summary>Название раздела уже в полосе программы владельца, свои кнопки у окна не рисуются — прятать нечего.</summary>
    public void AsOwnerSection()
    {
    }
}
