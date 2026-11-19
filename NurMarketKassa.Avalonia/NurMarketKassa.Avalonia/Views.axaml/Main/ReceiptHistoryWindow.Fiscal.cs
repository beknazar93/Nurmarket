using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): в «Истории чеков» под панелью действий — карточка «Фискальный чек (ККМ)»: статус «Пробит / Не пробит / Ошибка», ФД, ФМ, РН, ссылка QR,
/// у пробитого чека — замок «изменить нельзя — оформите возврат». Поля fiscal_* берутся из карточки чека сервера (<c>pos/sales/{id}/</c>), которая уже загружена для состава чека.
/// Карточка видна, только если сервер знает фискализацию и режим не «выключено» (FiscalSettings); иначе окно как раньше. В других местах (Продажи владельца) — тот же контрол.</summary>
public partial class ReceiptHistoryWindow
{
    private FiscalRequisitesCard? _fiscalCard;

    /// <summary>Вызывается один раз из конструктора: карточка встаёт в конец панели действий чека.</summary>
    private void InitFiscal(Panel actions)
    {
        _fiscalCard = new FiscalRequisitesCard();
        actions.Children.Add(_fiscalCard);
    }

    private void HideFiscalCard() => _fiscalCard?.Clear();

    /// <summary>Карточка чека загружена — показать реквизиты (у чека из офлайн-очереди полей нет: «Не пробит», пока он не дошёл до сервера).</summary>
    private void ShowFiscalCard(JsonElement detail) => _fiscalCard?.Show(FiscalRequisites.Parse(detail), FiscalDocKind.Sale);
}
