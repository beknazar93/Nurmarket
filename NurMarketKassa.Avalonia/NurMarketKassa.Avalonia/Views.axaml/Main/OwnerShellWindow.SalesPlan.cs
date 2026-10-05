using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, ТЗ часть 7, п. 2.5 «Прогресс к цели» — сервер выложил `GET/PUT /api/main/sales-targets/` 05.10
/// около 04:30 (владелец: «если бэкенд добавит — сразу применяй»). Карточка «План продаж на месяц» в «Сводке»: цель
/// месяца (задаёт владелец здесь же), продано с 1-го числа (отчёт сервера), сколько нужно в день до конца месяца,
/// успеваем ли при нынешнем темпе и сколько принесла допродажа (журнал recommendations, если есть).</summary>
public partial class OwnerShellWindow
{
    private const string SalesTargetsPath = "api/main/sales-targets/";
    private DateTime _salesPlanAt = DateTime.MinValue;
    private bool _salesPlanUnsupported;
    private bool _salesPlanEditing;

    private static string T5(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private async Task RefreshSalesPlanAsync(bool force = false)
    {
        // 2026-10-05: план продаж — аналитика, на тарифе «Старт» его нет (как «Аналитики» и «ABC»).
        if (!TariffGate.CanUseSalesAnalytics)
        {
            SalesPlanCard.IsVisible = false;
            return;
        }
        if (_salesPlanUnsupported || _salesPlanEditing || (!force && DateTime.UtcNow - _salesPlanAt < TimeSpan.FromMinutes(5)))
            return;
        _salesPlanAt = DateTime.UtcNow;
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var month = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            var data = await api.RequestAsync(HttpMethod.Get, SalesTargetsPath, null,
                new Dictionary<string, string> { ["month"] = month }, CancellationToken.None, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            var target = ReadTarget(data);
            var report = await App.SalesApi.MarketSalesReportAsync(monthStart, DateTime.Today, CancellationToken.None).ConfigureAwait(true);
            var sold = report.ValueKind == JsonValueKind.Object && report.TryGetProperty("cards", out var cards) ? Num(cards, "revenue") : 0;
            double? upsell = null;
            if (App.AppHost?.Services.GetService(typeof(RecommendationsApi)) is RecommendationsApi rec)
                upsell = (await rec.GetStatsAsync(monthStart, DateTime.Today).ConfigureAwait(true))?.Revenue;
            RenderSalesPlan(monthStart, target, sold, upsell);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            _salesPlanUnsupported = true;
            SalesPlanCard.IsVisible = false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: план продаж не обновлён ({ex.Message}).", "WARNING");
        }
    }

    private static double ReadTarget(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("revenue_target", out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private void RenderSalesPlan(DateTime monthStart, double target, double sold, double? upsell)
    {
        SalesPlanCard.IsVisible = true;
        SalesPlanHost.Children.Clear();
        var monthName = monthStart.ToString("MMMM", UiCulture);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 4) };
        head.Children.Add(new TextBlock
        {
            Text = T5($"План продаж на {monthName}", $"{monthName} айына сатуу планы", $"Sales plan for {monthName}", $"{monthName} satış planı", $"{monthName} uchun savdo rejasi"),
            Classes = { "cardTitle" },
        });
        var edit = new Button { Classes = { "link" }, Content = target > 0 ? T5("Изменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish") : "" , IsVisible = target > 0 };
        edit.Click += (_, _) => ShowSalesPlanEditor(monthStart, target, sold, upsell);
        Grid.SetColumn(edit, 1);
        head.Children.Add(edit);
        SalesPlanHost.Children.Add(head);

        if (target <= 0)
        {
            ShowSalesPlanEditor(monthStart, target, sold, upsell, keepHead: true);
            return;
        }

        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var dayNo = DateTime.Today.Day;
        var daysLeft = daysInMonth - dayNo + 1;
        var percent = Math.Min(100, sold / target * 100);
        var expected = sold / dayNo * daysInMonth;
        var onTrack = expected >= target;
        var perDay = Math.Max(0, (target - sold) / Math.Max(1, daysLeft));

        SalesPlanHost.Children.Add(Line(T5($"Продано {Money(sold)} из {Money(target)} ({percent:0} %)",
            $"Сатылды {Money(sold)} / {Money(target)} ({percent:0} %)", $"Sold {Money(sold)} of {Money(target)} ({percent:0} %)",
            $"Satılan {Money(sold)} / {Money(target)} (%{percent:0})", $"Sotildi {Money(sold)} / {Money(target)} ({percent:0} %)"), bold: true));
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, Height = 8, CornerRadius = new CornerRadius(4) };
        SalesPlanHost.Children.Add(bar);
        if (sold >= target)
        {
            SalesPlanHost.Children.Add(Line(T5("✓ План выполнен", "✓ План аткарылды", "✓ Plan reached", "✓ Plan tamamlandı", "✓ Reja bajarildi"), good: true));
        }
        else
        {
            SalesPlanHost.Children.Add(Line(T5($"Осталось {daysLeft} дн.: нужно {Money(perDay)} в день",
                $"{daysLeft} күн калды: күнүнө {Money(perDay)} керек", $"{daysLeft} days left: {Money(perDay)} a day needed",
                $"{daysLeft} gün kaldı: günde {Money(perDay)} gerekli", $"{daysLeft} kun qoldi: kuniga {Money(perDay)} kerak")));
            SalesPlanHost.Children.Add(Line(onTrack
                ? T5($"✓ Успеваем: при нынешнем темпе будет {Money(expected)}", $"✓ Үлгүрөбүз: ушул темп менен {Money(expected)} болот",
                    $"✓ On track: at this pace {Money(expected)}", $"✓ Yetişiyoruz: bu hızla {Money(expected)}", $"✓ Ulguramiz: shu sur'atda {Money(expected)}")
                : T5($"Отстаём: при нынешнем темпе будет {Money(expected)}", $"Артта калып жатабыз: ушул темп менен {Money(expected)}",
                    $"Behind: at this pace {Money(expected)}", $"Geride: bu hızla {Money(expected)}", $"Orqada: shu sur'atda {Money(expected)}"),
                good: onTrack, bad: !onTrack));
        }
        if (upsell is > 0)
            SalesPlanHost.Children.Add(Line(T5($"Допродажа «С этим часто берут»: {Money(upsell.Value)}", $"«Муну менен көп алышат»: {Money(upsell.Value)}",
                $"“Often bought with”: {Money(upsell.Value)}", $"«Bununla sık alınır»: {Money(upsell.Value)}", $"«Bu bilan ko'p olishadi»: {Money(upsell.Value)}")));
    }

    private void ShowSalesPlanEditor(DateTime monthStart, double target, double sold, double? upsell, bool keepHead = false)
    {
        _salesPlanEditing = true;
        if (!keepHead)
        {
            while (SalesPlanHost.Children.Count > 1)
                SalesPlanHost.Children.RemoveAt(1);
        }
        SalesPlanHost.Children.Add(Line(T5("Сколько хотите продать за месяц? Касса и программа покажут, успеваете ли.",
            "Айына канча сатууну каалайсыз? Программа үлгүрүп жатасызбы — көрсөтөт.", "How much do you want to sell this month? The app will show whether you are on track.",
            "Bu ay ne kadar satmak istiyorsunuz? Program yetişip yetişmediğinizi gösterir.", "Bu oy qancha sotmoqchisiz? Dastur ulgurayapsizmi — ko'rsatadi.")));
        var box = new TextBox { Text = target > 0 ? target.ToString("0", CultureInfo.InvariantCulture) : "", Watermark = "1 500 000", Height = 40, VerticalContentAlignment = VerticalAlignment.Center };
        var save = UiKit.Primary(this, T5("Сохранить план", "Планды сактоо", "Save plan", "Planı kaydet", "Rejani saqlash"));
        save.Height = 40;
        var status = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        row.Children.Add(box);
        Grid.SetColumn(save, 2);
        row.Children.Add(save);
        SalesPlanHost.Children.Add(row);
        SalesPlanHost.Children.Add(status);
        save.Click += async (_, _) =>
        {
            var text = new string((box.Text ?? "").Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                status.IsVisible = true;
                status.Text = T5("Введите сумму, например 1500000", "Сумманы жазыңыз, мисалы 1500000", "Enter an amount, e.g. 1500000", "Bir tutar girin, ör. 1500000", "Summani kiriting, masalan 1500000");
                return;
            }
            save.IsEnabled = false;
            try
            {
                var month = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                await App.GetRequiredService<NurMarketApiClient>().RequestAsync(HttpMethod.Put, SalesTargetsPath,
                    new Dictionary<string, object?> { ["month"] = month, ["revenue_target"] = value.ToString("0.00", CultureInfo.InvariantCulture) },
                    new Dictionary<string, string> { ["month"] = month }, CancellationToken.None, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                PosLogger.Log($"Owner app: план продаж на {month} — {value:0} сом.", "INFO");
                _salesPlanEditing = false;
                await RefreshSalesPlanAsync(force: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                save.IsEnabled = true;
                status.IsVisible = true;
                status.Text = T5("Не сохранилось: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            }
        };
    }

    private TextBlock Line(string text, bool bold = false, bool good = false, bool bad = false)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(good ? "BrushSuccess" : bad ? "BrushDanger" : "BrushText"));
        return t;
    }

    private string Money(double v) => v.ToString("N0", UiCulture) + " " + T5("сом", "сом", "som", "som", "so'm");
}
