using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19: окна кассы для базы знаний 2.0 — смена, внесение/изъятие, скидка, доп. услуга, оплата (наличные, смешанная, в долг), возврат. См. KbShots.</summary>
[Collection("UiScenarios")]
public sealed class KbShotsDialogsTests
{
    private static async Task LoginAsync() =>
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");

    /// <summary>Небольшое окно «по размеру содержимого»: для каждого языка создаётся заново (надписи собираются при создании).</summary>
    private static void Dialogs(string name, Func<Window> create, Action<Window>? prepare = null)
    {
        if (!KbShots.Want(name))
            return;
        foreach (var lang in KbShots.Langs)
        {
            UiShot.Setup(UiTheme.Light, lang);
            var w = create();
            try
            {
                w.Show();
                UiShot.Settle(8, 40);
                prepare?.Invoke(w);
                UiShot.Settle(4, 40);
                KbShots.Shot(w, name, lang);
            }
            finally
            {
                w.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact]
    public async Task Kassa_SmallDialogs()
    {
        var names = new[] { "kassa-open-shift", "kassa-close-shift", "kassa-cash-ops", "kassa-custom-service", "kassa-line-discount", "kassa-pay-debt", "kassa-payment-failed" };
        if (!names.Any(KbShots.Want))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        try
        {
            await LoginAsync();
            Dialogs("kassa-open-shift", () => new OpenShiftDialog());
            Dialogs("kassa-close-shift", () => new CloseShiftDialog());
            Dialogs("kassa-cash-ops", () => new NewOperationDialog(true));
            Dialogs("kassa-custom-service", () => new CustomServiceDialog());
            Dialogs("kassa-line-discount", () =>
            {
                var d = TestHost.Services.GetRequiredService<OrderDiscountDialog>();
                d.SetItemMode("Молоко 3,2 % 1 л", null, null);
                d.SetPreviewBase(89);
                return d;
            });
            Dialogs("kassa-pay-debt", () => new PayDebtDialog());
            Dialogs("kassa-payment-failed", () =>
            {
                var d = new PaymentStatusDialog(12635);
                d.ShowResult(false, null);
                return d;
            });
        }
        finally
        {
            restore();
        }
    }

    [AvaloniaFact]
    public async Task Kassa_Payment()
    {
        var names = new[] { "kassa-payment", "kassa-payment-mixed", "kassa-payment-debt" };
        if (!names.Any(KbShots.Want))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        try
        {
            foreach (var lang in KbShots.Langs)
            {
                UiShot.Setup(UiTheme.Light, lang);
                var owner = new Window { Width = 1280, Height = 720 };
                owner.Show();
                UiShot.Pump();
                // Чек: 3 хлеба + стиральный порошок = 12 635 сом (вымышленные товары).
                var cartJson = CartJson([("Хлеб «Бородинский» нарезной", 3, 45, 0), ("Стиральный порошок 3 кг", 1, 12500, 0)]);
                var totals = CartTotalsCalculator.Calculate(System.Text.Json.JsonDocument.Parse(cartJson).RootElement);
                var vm = new CheckoutViewModel(totals, "", "") { IsPrintReceiptEnabled = false };
                var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
                dialog.DockTo(owner);
                dialog.Show(owner);
                UiShot.Settle(6, 40);
                var cash = UiDrive.Find<TextBox>(dialog, "CashReceivedBox");
                UiDrive.Type(dialog, cash, "13000");
                UiShot.Settle(4, 40);
                if (KbShots.Want("kassa-payment"))
                    KbShots.Shot(dialog, "kassa-payment", lang);

                UiDrive.Find<RadioButton>(dialog, "PayMixedOption").IsChecked = true;
                UiShot.Settle(5, 40);
                UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "MixedCashBox"), "10000");
                UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "MixedNonCashBox"), "2635");
                UiShot.Settle(4, 40);
                if (KbShots.Want("kassa-payment-mixed"))
                    KbShots.Shot(dialog, "kassa-payment-mixed", lang);

                UiDrive.Find<RadioButton>(dialog, "PayDebtOption").IsChecked = true;
                UiShot.Settle(5, 40);
                if (KbShots.Want("kassa-payment-debt"))
                    KbShots.Shot(dialog, "kassa-payment-debt", lang);
                dialog.Close();
                owner.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            restore();
        }
    }

    // ------------------------------------------------------------------ возврат

    private static string Row(string id, int number, string total, string item, int daysAgo, string? client = null, string? phone = null) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"total\":\"" + total + "\",\"payment_method\":\"cash\""
        + ",\"created_at\":\"" + new DateTimeOffset(DateTime.Today.AddHours(12).AddDays(-daysAgo)).ToString("O") + "\",\"first_item_name\":\"" + item + "\",\"user_display\":\"Айбек\""
        + (client is null ? "" : ",\"client\":\"c-1\",\"client_name\":\"" + client + "\"")
        + (phone is null ? "" : ",\"client_phone\":\"" + phone + "\"") + "}";

    [AvaloniaFact]
    public async Task Kassa_Return()
    {
        if (!KbShots.Want("kassa-return") && !KbShots.Want("kassa-return-selected"))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        try
        {
            await LoginAsync();
            var rows = new[]
            {
                Row("s-1", 2101, "414.00", "Кока-Кола 1 л", 0),
                Row("s-2", 2102, "260.00", "Сыр Российский", 1, "Азамат Усупов", "+996 700 123 456"),
                Row("s-3", 2103, "12500.00", "Стиральный порошок 3 кг", 2),
                Row("s-4", 2104, "135.50", "Шоколад молочный", 3),
                Row("s-5", 2105, "98.00", "Сахар 1 кг", 4),
            };
            UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":5,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
            var at = new DateTimeOffset(DateTime.Today.AddHours(12)).ToString("O");
            UiFake.Server.Json("GET", "/api/main/pos/sales/s-1/", 200, "{\"id\":\"s-1\",\"number\":2101,\"status\":\"paid\",\"return_status\":\"none\",\"payment_method\":\"cash\",\"total\":\"414.00\",\"created_at\":\"" + at + "\",\"items\":["
                + "{\"id\":\"si-1\",\"product_id\":\"kb-1\",\"name\":\"Хлеб «Бородинский» нарезной\",\"quantity\":\"3\",\"unit_price\":\"45\",\"total\":\"135\",\"returnable_qty\":3},"
                + "{\"id\":\"si-2\",\"product_id\":\"kb-3\",\"name\":\"Молоко 3,2 % 1 л\",\"quantity\":\"1\",\"unit_price\":\"89\",\"total\":\"89\",\"returnable_qty\":1},"
                + "{\"id\":\"si-3\",\"product_id\":\"kb-4\",\"name\":\"Кока-Кола 1 л\",\"quantity\":\"2\",\"unit_price\":\"95\",\"total\":\"190\",\"returnable_qty\":2}]}");
            UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[]");
            var size = new UiSize(1280, 688);
            foreach (var lang in KbShots.Langs)
            {
                UiShot.Setup(UiTheme.Light, lang);
                var dialog = new ReturnSaleDialog();
                try
                {
                    UiShot.Render(dialog, size).Dispose();
                    KbShots.WaitFor(() => dialog.Sales.Count == 5);
                    UiShot.Settle(6, 40);
                    if (KbShots.Want("kassa-return"))
                        KbShots.Shot(dialog, "kassa-return", lang, size);
                    if (KbShots.Want("kassa-return-selected"))
                    {
                        // Открываем чек №2101 так же, как кассир (двойной клик по строке — тот же путь, что «Выбрать чек»).
                        typeof(ReturnSaleDialog).GetMethod("OpenSaleByIdAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dialog, ["s-1"]);
                        KbShots.WaitFor(() => dialog.Lines.Count == 3);
                        UiShot.Settle(6, 40);
                        foreach (var line in dialog.Lines.Take(2))
                            line.IsSelected = true;
                        UiShot.Settle(6, 40);
                        KbShots.Shot(dialog, "kassa-return-selected", lang, size);
                    }
                }
                finally
                {
                    dialog.Close();
                    UiShot.Pump();
                }
            }
        }
        finally
        {
            restore();
        }
    }
}
