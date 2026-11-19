using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19: обучающие анимации базы знаний 2.0 (anim-sale.gif, anim-return.gif): 4 кадра по шагам, подпись шага на языке снимка, 960×540,
/// пауза 1,8 с на кадр. Кадры — настоящие окна кассы на вымышленных товарах. См. KbShots.</summary>
[Collection("UiScenarios")]
public sealed class KbShotsAnimTests
{
    private static readonly UiSize Hd = new(1280, 720);
    private const int W = 960, H = 604; // 540 под кадр окна + 64 под подпись шага

    private static Window PrepareMain()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
        NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Standard);
        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using (UiShot.Render(main, Hd)) { }
        main.Activate();
        UiShot.Settle(10, 40);
        return main;
    }

    private static void CleanUp(MainWindow? main, MainLayoutRestore saved)
    {
        try { KbShots.Vm(main!).Basket.ClearTrainingReceipt(); } catch { /* чек теста не оставляем */ }
        UserPreferences.Instance.MainLayoutMode = saved.Layout;
        NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Normalize(saved.Layout));
        UiRobot.CloseAll();
        UiRobot.Main = null;
        main?.Hide(); // закрытие окна кассы подвешивает хост Headless — прячем
        UiShot.Pump();
    }

    private readonly record struct MainLayoutRestore(string Layout);

    [AvaloniaFact]
    public async Task Anim_Sale()
    {
        if (!KbShots.Want("anim-sale"))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var saved = new MainLayoutRestore(UserPreferences.Instance.MainLayoutMode);
        var main = (MainWindow)PrepareMain();
        using var guard = UiRobot.Guard(600000);
        try
        {
            foreach (var lang in KbShots.Langs)
            {
                try { KbShots.Vm(main).Basket.ClearTrainingReceipt(); } catch { /* пусто */ }
                UiShot.Setup(UiTheme.Light, lang);
                UiShot.Settle(6, 40);
                var frames = new List<SKBitmap>();
                var f1 = KbShots.Frame(main, Hd);
                frames.Add(KbShots.Step(f1, W, H, 1, 4, Tr.T(
                    "Найдите товар: отсканируйте штрихкод или введите код или название в поле над каталогом",
                    "Товарды табыңыз: штрихкодду сканерлеңиз же каталогдун үстүндөгү талаага код же аталышын жазыңыз",
                    "Find the item: scan the barcode or type a code or name in the field above the catalog",
                    "Ürünü bulun: barkodu okutun veya katalogun üstündeki alana kod ya da ad yazın",
                    "Mahsulotni toping: shtrixkodni skanerlang yoki katalog ustidagi maydonga kod yoki nomini yozing")));
                f1.Dispose();

                KbShots.Scan(main, "3*4700000000001");
                KbShots.Scan(main, "4700000000003");
                var f2 = KbShots.Frame(main, Hd);
                frames.Add(KbShots.Step(f2, W, H, 2, 4, Tr.T(
                    "Товар сразу попадает в чек справа; количество — «3*код». Проверьте сумму в строке «Итого»",
                    "Товар ошол замат оң жактагы чекке түшөт; саны — «3*код». «Жыйынтык» сабындагы суммаңызды текшериңиз",
                    "The item goes straight to the receipt on the right; quantity is «3*code». Check the total",
                    "Ürün hemen sağdaki fişe düşer; adet — «3*kod». Toplam tutarı kontrol edin",
                    "Mahsulot darhol o'ngdagi chekka tushadi; miqdor — «3*kod». Jami summani tekshiring")));

                // Окно оплаты: наличные с суммой «получено».
                var owner = new Window { Width = Hd.Width, Height = Hd.Height };
                owner.Show();
                UiShot.Pump();
                var cartJson = CartJson([("Хлеб «Бородинский» нарезной", 3, 45, 0), ("Молоко 3,2 % 1 л", 1, 89, 0)]);
                var totals = CartTotalsCalculator.Calculate(System.Text.Json.JsonDocument.Parse(cartJson).RootElement);
                var vm = new CheckoutViewModel(totals, "", "") { IsPrintReceiptEnabled = false };
                var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
                dialog.DockTo(owner);
                dialog.Show(owner);
                UiShot.Settle(6, 40);
                UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "300");
                UiShot.Settle(4, 40);
                using (var d = KbShots.Frame(dialog))
                using (var over = KbShots.Overlay(f2, d))
                    frames.Add(KbShots.Step(over, W, H, 3, 4, Tr.T(
                        "Нажмите «Оплатить», выберите способ оплаты, при наличных впишите сумму — касса покажет сдачу",
                        "«Төлөө» баскычын басыңыз, төлөм ыкмасын тандаңыз, накталайда сумманы жазыңыз — касса кайтарымды көрсөтөт",
                        "Press «Pay», choose the payment method, for cash enter the amount — the till shows the change",
                        "«Öde»'ye basın, ödeme yöntemini seçin, nakitte tutarı yazın — kasa para üstünü gösterir",
                        "«To'lash» tugmasini bosing, to'lov usulini tanlang, naqdda summani yozing — kassa qaytimni ko'rsatadi")));
                dialog.Close();
                owner.Close();
                UiShot.Pump();

                var success = new SaleSuccessDialog(224, false);
                success.Show();
                UiShot.Settle(6, 40);
                using (var d = KbShots.Frame(success))
                using (var over = KbShots.Overlay(f2, d))
                    frames.Add(KbShots.Step(over, W, H, 4, 4, Tr.T(
                        "Готово — продажа проведена. Чек можно распечатать, и сразу начать следующую продажу",
                        "Даяр — сатуу өткөрүлдү. Чекти басып чыгарууга болот, ошол замат кийинки сатууну баштаңыз",
                        "Done — the sale is complete. You can print the receipt and start the next sale at once",
                        "Hazır — satış tamamlandı. Fişi yazdırabilir ve hemen sonraki satışa geçebilirsiniz",
                        "Tayyor — sotuv rasmiylashtirildi. Chekni chop etish va darhol keyingi sotuvni boshlash mumkin")));
                success.Close();
                UiShot.Pump();
                f2.Dispose();

                KbShots.SaveGif(frames, KbShots.Delays(frames.Count), "anim-sale", lang);
                foreach (var f in frames)
                    f.Dispose();
            }
        }
        finally
        {
            CleanUp(main, saved);
            restore();
        }
    }

    private static string Row(string id, int number, string total, string item, int daysAgo) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"total\":\"" + total + "\",\"payment_method\":\"cash\""
        + ",\"created_at\":\"" + new DateTimeOffset(DateTime.Today.AddHours(12).AddDays(-daysAgo)).ToString("O") + "\",\"first_item_name\":\"" + item + "\",\"user_display\":\"Айбек\"}";

    [AvaloniaFact]
    public async Task Anim_Return()
    {
        if (!KbShots.Want("anim-return"))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var saved = new MainLayoutRestore(UserPreferences.Instance.MainLayoutMode);
        var main = (MainWindow)PrepareMain();
        using var guard = UiRobot.Guard(600000);
        try
        {
            await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
            var rows = new[] { Row("s-1", 2101, "414.00", "Кока-Кола 1 л", 0), Row("s-2", 2102, "260.00", "Сыр Российский", 1), Row("s-3", 2103, "12500.00", "Стиральный порошок 3 кг", 2) };
            UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":3,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
            var at = new DateTimeOffset(DateTime.Today.AddHours(12)).ToString("O");
            UiFake.Server.Json("GET", "/api/main/pos/sales/s-1/", 200, "{\"id\":\"s-1\",\"number\":2101,\"status\":\"paid\",\"return_status\":\"none\",\"payment_method\":\"cash\",\"total\":\"414.00\",\"created_at\":\"" + at + "\",\"items\":["
                + "{\"id\":\"si-1\",\"product_id\":\"kb-1\",\"name\":\"Хлеб «Бородинский» нарезной\",\"quantity\":\"3\",\"unit_price\":\"45\",\"total\":\"135\",\"returnable_qty\":3},"
                + "{\"id\":\"si-2\",\"product_id\":\"kb-3\",\"name\":\"Молоко 3,2 % 1 л\",\"quantity\":\"1\",\"unit_price\":\"89\",\"total\":\"89\",\"returnable_qty\":1},"
                + "{\"id\":\"si-3\",\"product_id\":\"kb-4\",\"name\":\"Кока-Кола 1 л\",\"quantity\":\"2\",\"unit_price\":\"95\",\"total\":\"190\",\"returnable_qty\":2}]}");
            UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[]");
            var size = new UiSize(1280, 688);
            foreach (var lang in KbShots.Langs)
            {
                try { KbShots.Vm(main).Basket.ClearTrainingReceipt(); } catch { /* пусто */ }
                UiShot.Setup(UiTheme.Light, lang);
                KbShots.Vm(main).Toolbar.ToggleSideMenuCommand.Execute(null);
                UiShot.Settle(10, 40);
                var frames = new List<SKBitmap>();
                using (var menu = KbShots.Frame(main, Hd))
                    frames.Add(KbShots.Step(menu, W, H, 1, 4, Tr.T(
                        "Откройте меню ☰ и в разделе «Работа» выберите «Возврат»",
                        "☰ менюсун ачыңыз да, «Иш» бөлүмүнөн «Кайтаруу» дегенди тандаңыз",
                        "Open the ☰ menu and in the «Work» section choose «Return»",
                        "☰ menüsünü açın ve «Çalışma» bölümünden «İade»'yi seçin",
                        "☰ menyusini oching va «Ish» bo'limidan «Qaytarish»ni tanlang")));
                KbShots.Vm(main).CloseSideMenu();
                UiShot.Settle(6, 40);

                var dialog = new ReturnSaleDialog();
                try
                {
                    UiShot.Render(dialog, size).Dispose();
                    KbShots.WaitFor(() => dialog.Sales.Count == 3);
                    UiShot.Settle(6, 40);
                    using (var list = KbShots.Frame(dialog, size))
                        frames.Add(KbShots.Step(list, W, H, 2, 4, Tr.T(
                            "Найдите нужный чек в списке (есть поиск по номеру, сумме, дате, клиенту) и нажмите «Выбрать»",
                            "Керектүү чекти тизмеден табыңыз (номер, сумма, күн, кардар боюнча издөө бар) жана «Тандоо» баскычын басыңыз",
                            "Find the receipt in the list (search by number, amount, date, client) and press «Select»",
                            "Fişi listeden bulun (numara, tutar, tarih, müşteriye göre arama var) ve «Seç»'e basın",
                            "Chekni ro'yxatdan toping (raqam, summa, sana, mijoz bo'yicha qidiruv bor) va «Tanlash»ni bosing")));
                    typeof(ReturnSaleDialog).GetMethod("OpenSaleByIdAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dialog, ["s-1"]);
                    KbShots.WaitFor(() => dialog.Lines.Count == 3);
                    UiShot.Settle(6, 40);
                    using (var lines = KbShots.Frame(dialog, size))
                        frames.Add(KbShots.Step(lines, W, H, 3, 4, Tr.T(
                            "Откроется состав чека. Чтобы вернуть весь чек, нажмите «Вернуть весь чек»; часть товаров — отметьте их галочками",
                            "Чектин курамы ачылат. Бүт чекти кайтаруу үчүн «Бүт чекти кайтаруу» баскычын басыңыз; бир бөлүгүн — белгилеп коюңуз",
                            "The receipt lines open. To return the whole receipt press «Return whole receipt»; for part of it tick the lines",
                            "Fişin içeriği açılır. Tüm fişi iade etmek için «Tüm fişi iade et»'e basın; bir kısmı için satırları işaretleyin",
                            "Chek tarkibi ochiladi. Butun chekni qaytarish uchun «Butun chekni qaytarish»ni bosing; qismini — belgilab qo'ying")));
                    foreach (var line in dialog.Lines)
                        line.IsSelected = true;
                    UiShot.Settle(6, 40);
                    using (var all = KbShots.Frame(dialog, size))
                        frames.Add(KbShots.Step(all, W, H, 4, 4, Tr.T(
                            "Укажите причину возврата и подтвердите. Касса оформит возврат — верните покупателю деньги",
                            "Кайтаруунун себебин көрсөтүп, ырастаңыз. Касса кайтарууну тариздейт — акчаны сатып алуучуга кайтарыңыз",
                            "Enter the reason and confirm. The till records the return — give the money back to the customer",
                            "İade nedenini yazıp onaylayın. Kasa iadeyi işler — parayı müşteriye geri verin",
                            "Qaytarish sababini ko'rsating va tasdiqlang. Kassa qaytarishni rasmiylashtiradi — pulni xaridorga qaytaring")));
                }
                finally
                {
                    dialog.Close();
                    UiShot.Pump();
                }
                KbShots.SaveGif(frames, KbShots.Delays(frames.Count), "anim-return", lang);
                foreach (var f in frames)
                    f.Dispose();
            }
        }
        finally
        {
            CleanUp(main, saved);
            restore();
        }
    }
}
