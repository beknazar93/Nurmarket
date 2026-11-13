using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (владелец: «редактирование накладной возвраты поставщику! нужно добавить и провести стресс тест»): окно «Правка накладной возврата поставщику»
/// и кнопка «Править накладную» в Avalonia.Headless против поддельного сервера (WireMock; настоящий NurCRM и принтеры не нужны).
/// Стресс: 1 / 30 / 500 / 2000 строк (время открытия, ввод, прокрутка, память, сохранение), двойной клик «Сохранить», 412, PIN неверный / токен истёк, оба object_type,
/// 24 ч истекли при открытом окне, обрыв связи (тот же Idempotency-Key, без дубля), две копии окна, аннулирован → повторное открытие, дробные количества,
/// смена языка/темы, закрытие окна во время записи, 200 открытий/закрытий без роста памяти. Цифры — %TEMP%\nurmarket-tests\ui-actual\supret-stress.txt,
/// снимки — %TEMP%\nurmarket-tests\ui-actual\supret-*.png.</summary>
[Collection("UiScenarios")]
public sealed class SupplierReturnEditStressUiTests
{
    private const string ReturnPath = "/api/main/suppliers/returns/r1/";
    private const string PinPath = "/api/users/auth/pin/verify/";
    private static readonly string ShotDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");

    // ───────────── поддельный сервер ─────────────

    /// <summary>Свой WireMock: вход, признак supplier_return_edit_24h (через настоящий разбор capabilities), карточка возврата, PIN, PATCH.</summary>
    private sealed class Stand : IAsyncDisposable
    {
        public WireMockServer Server { get; } = WireMockServer.Start();
        public NurMarketApiClient Api { get; private set; } = null!;
        public ReturnEditApi Edit { get; private set; } = null!;

        public static async Task<Stand> StartAsync(bool feature = true)
        {
            var s = new Stand();
            s.Json("POST", "/api/users/auth/login/", 200, "{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}", priority: 9);
            s.Api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Server.Url! });
            await s.Api.LoginAsync("a@b.kg", "12345678");
            s.Json("GET", "/api/main/capabilities/", 200, feature ? "[\"supplier_return_edit_24h\"]" : "[\"receipt_delete\"]", priority: 9);
            await ServerCapabilities.LoadAsync(s.Api, force: true);
            s.Edit = new ReturnEditApi(s.Api);
            s.Json("POST", PinPath, 200, "{\"confirm_token\":\"tok-1\",\"expires_in\":120,\"approved_by_name\":\"Айбек\"}", priority: 8);
            return s;
        }

        public void Json(string method, string path, int status, string body, int priority = 5, TimeSpan? delay = null)
        {
            var r = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
            if (delay is { } d)
                r = r.WithDelay(d);
            Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(r);
        }

        public List<ILogEntry> Calls(string method, string path) =>
            Server.LogEntries.Where(e => e.RequestMessage.Method == method && e.RequestMessage.Path == path).ToList();

        public List<ILogEntry> Patches => Calls("PATCH", ReturnPath);
        public List<ILogEntry> Pins => Calls("POST", PinPath);

        public async ValueTask DisposeAsync()
        {
            Server.Reset();
            Json("POST", "/api/users/auth/login/", 200, "{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}");
            Json("GET", "/api/main/capabilities/", 200, "[]");
            try
            {
                await ServerCapabilities.LoadAsync(Api, force: true);
            }
            finally
            {
                ServerCapabilities.Reset();
                Server.Stop();
            }
        }
    }

    /// <summary>Карточка возврата поставщику с <paramref name="lines"/> строками: каждая третья — весовая (кг, 1,255), остальные — штучные (2 шт); цена 150,50.</summary>
    private static string CardJson(int lines, int version = 3, bool canEdit = true, bool canVoid = true, bool voided = false, string? reason = null, bool flags = true)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"r1\",\"number_display\":\"ВП-2026-000123\",\"supplier_id\":\"s1\",\"supplier_name\":\"ОсОО «Поставщик»\",\"reason\":\"defect\",\"compensation\":\"debt_offset\",");
        sb.Append("\"created_at\":\"2026-11-12T03:00:00Z\",\"comment\":\"брак упаковки\",\"total_amount\":\"1000.00\",");
        if (flags)
        {
            sb.Append("\"version\":").Append(version).Append(",\"status\":\"").Append(voided ? "voided" : "posted").Append("\",");
            sb.Append("\"can_edit\":").Append(canEdit ? "true" : "false").Append(",\"can_void\":").Append(canVoid ? "true" : "false").Append(',');
            sb.Append("\"editable_until\":\"2026-11-13T03:00:00Z\",\"server_now\":\"2026-11-12T08:00:00Z\",");
            if (reason is not null)
                sb.Append("\"edit_block_reason\":\"").Append(reason).Append("\",");
        }
        sb.Append("\"items\":[");
        for (var i = 0; i < lines; i++)
        {
            if (i > 0)
                sb.Append(',');
            var kg = i % 3 == 2;
            sb.Append("{\"id\":\"l").Append(i).Append("\",\"product_id\":\"p").Append(i).Append("\",\"product_name\":\"Товар №").Append(i + 1)
                .Append(kg ? " сыр весовой" : " молоко 1 л").Append("\",\"qty\":\"").Append(kg ? "1.255" : "2").Append("\",\"unit\":\"").Append(kg ? "кг" : "шт")
                .Append("\",\"purchase_price\":\"150.50\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static SupplierReturnInfo Info(string json) => SupplierApiService.ParseReturn(JsonDocument.Parse(json).RootElement.Clone());

    private static JsonElement Body(ILogEntry e) => JsonDocument.Parse(e.RequestMessage.Body!).RootElement.Clone();

    private static string Header(ILogEntry e, string name) => e.RequestMessage.Headers![name].Single();

    // ───────────── шаги окна ─────────────

    private static Window Owner(int width = 1366, int height = 768)
    {
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        return owner;
    }

    private static void Wait(Func<bool> done, int ms = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 10);
    }

    private static async Task<T> Finish<T>(Task<T> task, int ms = 20000)
    {
        Wait(() => task.IsCompleted, ms);
        task.IsCompleted.Should().BeTrue("операция окна закончилась");
        return await task;
    }

    private static async Task Finish(Task task, int ms = 20000)
    {
        Wait(() => task.IsCompleted, ms);
        task.IsCompleted.Should().BeTrue("операция окна закончилась");
        await task;
    }

    /// <summary>Дождаться окна PIN руководителя поверх <paramref name="dialog"/> и набрать PIN цифровым блоком, как кассир. false — окно PIN не появилось.</summary>
    private static bool EnterPin(Window dialog, string pin = "1234", int ms = 8000)
    {
        ManagerPinDialog? pd = null;
        Wait(() => (pd = dialog.OwnedWindows.OfType<ManagerPinDialog>().FirstOrDefault(w => w.IsVisible)) is not null, ms);
        if (pd is null)
            return false;
        UiShot.Settle(2, 5);
        foreach (var d in pin)
            UiDrive.Click(pd, UiDrive.ButtonByText(pd, d.ToString()));
        UiDrive.Click(pd, UiDrive.ButtonByText(pd, "Подтвердить", "Ырастоо"));
        return true;
    }

    private static void Close(Window? dialog, Window owner)
    {
        dialog?.Close();
        owner.Close();
        UiShot.Pump();
        ModalShell.WorkingAreaForTests = null; // 2026-11-12 (Т-11): подмена «экрана» — только на время теста
    }

    private static void Note(string line)
    {
        Directory.CreateDirectory(ShotDir);
        File.AppendAllText(Path.Combine(ShotDir, "supret-stress.txt"), DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
    }

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        Directory.CreateDirectory(ShotDir);
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), data.ToArray());
    }

    private static long Mem()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(true);
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024d / 1024d, 1);

    // ───────────── 1 / 30 / 500 / 2000 строк ─────────────

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(500)]
    [InlineData(2000)]
    public async Task Stress_Rows_OpenTypeScrollSave_TimesAndMemory(int lines)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(lines);
        stand.Json("GET", ReturnPath, 200, json);
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}");
        var owner = Owner();
        SupplierReturnEditDialog? dialog = null;
        try
        {
            var info = Info(json);
            var mem0 = Mem();
            var sw = Stopwatch.StartNew();
            dialog = new SupplierReturnEditDialog(info, "ВП-2026-000123", stand.Edit);
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Pump();
            var open = sw.Elapsed;
            // кадры после открытия: самый долгий кадр = насколько «замирает» окно
            var maxFrame = TimeSpan.Zero;
            for (var i = 0; i < 3; i++)
            {
                var f = Stopwatch.StartNew();
                UiShot.Pump();
                if (f.Elapsed > maxFrame)
                    maxFrame = f.Elapsed;
            }
            var memOpen = Mem() - mem0;
            dialog.RowCountForTests.Should().Be(lines);
            dialog.ShownRowsForTests.Should().Be(Math.Min(lines, 50), "поля ввода — только для первой страницы (2026-11-12: иначе 2000 строк = 31 с и ~950 МБ)");

            // поиск товара в длинной накладной и ввод количества в последней строке (сумма пересчитывается по всем строкам)
            var find = TimeSpan.Zero;
            if (lines > 50)
            {
                sw.Restart();
                dialog.FindForTests("№" + lines + " ");
                UiShot.Pump();
                find = sw.Elapsed;
                dialog.ShownRowsForTests.Should().Be(1, "поиск находит последнюю строку");
            }
            sw.Restart();
            dialog.SetQtyForTests(lines - 1, "1");
            UiShot.Pump();
            var type = sw.Elapsed;
            dialog.TotalText.Should().Contain("было", "сумма изменилась и показано прежнее значение");
            if (lines > 50)
            {
                dialog.FindForTests("");
                dialog.ShowMoreForTests();
                UiShot.Pump();
                dialog.ShownRowsForTests.Should().Be(100, "«Показать ещё» добавляет страницу");
            }

            // прокрутка в конец списка строк
            var scrolled = TimeSpan.Zero;
            ScrollViewer? scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(s => s.Extent.Height).First();
            sw.Restart();
            scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            UiShot.Pump();
            scrolled = sw.Elapsed;
            if (lines >= 30)
                scroll.Offset.Y.Should().BeGreaterThan(0, "длинный список прокручивается");
            scroll = null; // ссылка на дерево окна не должна держать его после закрытия (замер памяти)

            // сохранение: проверка версии → PIN → PATCH со всеми строками
            dialog.SetReasonForTests("ошиблись количеством");
            sw.Restart();
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue("перед сохранением — PIN руководителя");
            await Finish(save, 60000);
            var saved = sw.Elapsed;
            (await Finish(shown)).Should().BeTrue("окно закрылось с результатом «изменено»");
            stand.Patches.Should().HaveCount(1);
            var body = Body(stand.Patches[0]);
            body.GetProperty("items").GetArrayLength().Should().Be(lines, "уходит полный новый состав");
            body.GetProperty("items")[lines - 1].GetProperty("qty").GetString().Should().Be("1");
            if (lines >= 3)
                body.GetProperty("items")[2].GetProperty("qty").GetString().Should().Be("1.255", "кг не округляются до целых");
            Header(stand.Patches[0], "If-Match").Should().Be("\"3\"");
            var weakDialog = new WeakReference(dialog);
            dialog = null;
            UiShot.Pump();
            stand.Server.ResetLogEntries(); // журнал WireMock (тела GET/PATCH) — память теста, не окна
            AssertCollected(weakDialog, "Окно правки накладной после сохранения");
            var memAfter = Mem() - mem0;

            Note($"[stress] строк {lines,5}: открытие {open.TotalMilliseconds,7:0} мс, макс. кадр {maxFrame.TotalMilliseconds,6:0} мс, поиск {find.TotalMilliseconds,6:0} мс, ввод {type.TotalMilliseconds,6:0} мс, "
                 + $"прокрутка {scrolled.TotalMilliseconds,6:0} мс, сохранение (с PIN) {saved.TotalMilliseconds,6:0} мс, память окна {Mb(memOpen),6} МБ, после закрытия {Mb(memAfter),6} МБ");

            // 2026-11-12 (аудит 12.11, Т-19): пределы — от замеров ×2 (supret-stress.txt 12.11: открытие 38 / 194 / 347 / 429 мс, память окна 1,9 / 15,9 / 25,9 / 25,9 МБ,
            // после закрытия 2,4 / 13,8 / 42,7 / 45,6 МБ), а не «с запасом» 3–5 с и 80 МБ. Для коротких замеров (единицы мс) — нижняя планка 200 мс: это шум кадра Headless, не работа окна.
            static double Ms(double measured) => Math.Max(200, measured * 2);
            var (openMs, memOpenMb, memAfterMb) = lines switch
            {
                <= 1 => (38.0, 1.9, 2.4),
                <= 30 => (194.0, 15.9, 13.8),
                <= 500 => (347.0, 25.9, 42.7),
                _ => (429.0, 25.9, 45.6),
            };
            open.TotalMilliseconds.Should().BeLessThan(Ms(openMs), $"окно с {lines} строками открывается быстро (строится одна страница; замер {openMs} мс)");
            type.TotalMilliseconds.Should().BeLessThan(Ms(15), "ввод количества не подвешивает окно (замер до 15 мс)");
            find.TotalMilliseconds.Should().BeLessThan(Ms(59), "поиск товара в накладной быстрый (замер до 59 мс)");
            // 2026-11-12 (аудит Fable, В-7, пакет П-5): память — НЕ «замер ×2», а смысловые пороги. Прежний замер «после закрытия» (42,7 / 45,6 МБ на 500/2000 строк) был БОЛЬШЕ «памяти окна» (25,9 МБ):
            // окно с выведенными строками оставалось в памяти после сохранения (удержание), а порог ×2 закреплял эту цифру как норму. Теперь: открытая страница (50 строк) < 35 МБ;
            // окно собрано сборщиком (слабая ссылка мертва); после очистки журнала мок-сервера (тела GET/PATCH на 2000 строк — память теста, не окна) «после закрытия» < 8 МБ.
            // Рост по числу строк — SupplierEdit_MemoryOpen_DoesNotGrowWithRows; цикл «сохранить → закрыть» ×6 — SupplierEdit_SaveThenClose_WindowAndPinDialogCollected.
            _ = (memOpenMb, memAfterMb); // прежние замеры — в комментарии выше; пороги памяти от них больше не зависят
            Mb(memOpen).Should().BeLessThan(35, "память окна не растёт с числом строк (2026-11-12: было ~950 МБ на 2000 строк); открытая страница — 50 строк ≈ 26 МБ");
            Mb(memAfter).Should().BeLessThan(8, "после закрытия окна и очистки журнала мок-сервера память не держится (было ~776 МБ на 2000 строк)");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── двойной клик «Сохранить» ─────────────

    [AvaloniaFact]
    public async Task DoubleClickSave_OnePinOnePatch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(3);
        stand.Json("GET", ReturnPath, 200, json, delay: TimeSpan.FromMilliseconds(300));
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}", delay: TimeSpan.FromMilliseconds(300));
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            // два нажатия подряд мышью + ещё один вызов, пока первый ждёт сервер
            var save = UiDrive.ButtonByText(dialog, "Сохранить");
            UiDrive.Click(dialog, save);
            UiDrive.Click(dialog, save);
            var extra = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(extra);
            (await Finish(shown)).Should().BeTrue();
            stand.Pins.Should().HaveCount(1, "один PIN на одну правку");
            stand.Patches.Should().HaveCount(1, "двойной клик не отправляет правку дважды");
            Calls(stand, "GET").Should().HaveCount(1, "свежая версия запрошена один раз");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    private static List<ILogEntry> Calls(Stand s, string method) => s.Calls(method, ReturnPath);

    // ───────────── 412 → перечитать → повторить ─────────────

    [AvaloniaFact]
    public async Task VersionConflict412_Locks_ThenReopenByFreshCard_SavesWithNewVersion()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var v3 = CardJson(2, version: 3);
        stand.Json("GET", ReturnPath, 200, v3);
        stand.Json("PATCH", ReturnPath, 412, "{\"code\":\"version_conflict\",\"current_version\":4}");
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(v3), "ВП-2026-000123", stand.Edit);
        SupplierReturnEditDialog? again = null;
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("другим сотрудником");
            dialog.SaveEnabledForTests.Should().BeFalse("после 412 сохранять эту версию бессмысленно");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            (await Finish(shown)).Should().BeTrue("список надо перечитать");

            // перечитывание: в списке v3, на сервере уже v4 — окно открывается по свежей карточке (не отказ)
            stand.Server.Reset();
            stand.Json("POST", PinPath, 200, "{\"confirm_token\":\"tok-2\"}");
            var v4 = CardJson(2, version: 4);
            stand.Json("GET", ReturnPath, 200, v4);
            stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":5}");
            var prep = await ReturnEditFlows.PrepareSupplierEditAsync(Info(v3), stand.Edit);
            prep.Card.Should().NotBeNull("устаревшая версия списка — не повод отказывать: карточка перечитана");
            prep.Card!.EditState.Version.Should().Be(4);
            prep.Reload.Should().BeTrue();
            again = new SupplierReturnEditDialog(prep.Card, "ВП-2026-000123", stand.Edit);
            var shown2 = again.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            again.SetQtyForTests(0, "1");
            again.SetReasonForTests("ошиблись количеством");
            var save2 = again.SaveForTestsAsync();
            EnterPin(again).Should().BeTrue();
            await Finish(save2);
            (await Finish(shown2)).Should().BeTrue();
            stand.Patches.Should().HaveCount(1);
            Header(stand.Patches[0], "If-Match").Should().Be("\"4\"", "повтор — уже с новой версией");
            again = null;
        }
        finally
        {
            again?.Close();
            Close(dialog, owner);
        }
    }

    // ───────────── PIN неверный, токен истёк, оба object_type ─────────────

    [AvaloniaFact]
    public async Task WrongPin_ThenConfirmExpired_ThenSaved_OnlyOneSuccessfulPatch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(2);
        stand.Json("GET", ReturnPath, 200, json);
        // PIN «1111» — неверный; «2222» — токен, который к PATCH уже истёк; «3333» — годный токен (по телу запроса, без сценариев WireMock)
        stand.Server.Given(Request.Create().WithPath(PinPath).UsingMethod("POST").WithBody((string? b) => b?.Contains("\"1111\"") == true)).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"pin_invalid\",\"attempts_left\":4}"));
        stand.Server.Given(Request.Create().WithPath(PinPath).UsingMethod("POST").WithBody((string? b) => b?.Contains("\"2222\"") == true)).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"confirm_token\":\"tok-old\"}"));
        stand.Server.Given(Request.Create().WithPath(PinPath).UsingMethod("POST").WithBody((string? b) => b?.Contains("\"3333\"") == true)).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"confirm_token\":\"tok-ok\"}"));
        stand.Server.Given(Request.Create().WithPath(ReturnPath).UsingMethod("PATCH").WithHeader("X-Confirm-Token", "tok-old")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"confirm_invalid\"}"));
        stand.Server.Given(Request.Create().WithPath(ReturnPath).UsingMethod("PATCH").WithHeader("X-Confirm-Token", "tok-ok")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r1\",\"version\":4}"));
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(1, "0");
            dialog.SetReasonForTests("строка лишняя, убрали");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog, "1111").Should().BeTrue();
            UiShot.Settle(6, 20);
            var pin = dialog.OwnedWindows.OfType<ManagerPinDialog>().Single(w => w.IsVisible);
            UiDrive.AllText(pin).Should().Contain(ServerErrorCodes.PinInvalidText(4), "неверный PIN — сказано, сколько попыток осталось (аудит 12.11, Т-7: полный текст «Осталось попыток: 4.»)");
            EnterPin(dialog, "2222").Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("устарело", "токен подтверждения истёк — понятный текст");
            dialog.SaveEnabledForTests.Should().BeTrue("после истёкшего токена можно нажать ещё раз");

            var retry = dialog.SaveForTestsAsync();
            EnterPin(dialog, "3333").Should().BeTrue();
            await Finish(retry);
            (await Finish(shown)).Should().BeTrue();
            stand.Patches.Should().HaveCount(2);
            Header(stand.Patches[1], "X-Confirm-Token").Should().Be("tok-ok");
            Body(stand.Patches[1]).GetProperty("items").GetArrayLength().Should().Be(1, "строка с 0 убрана из возврата");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaTheory]
    [InlineData("supplier_return")]
    [InlineData("supplier")]
    public async Task ObjectType_EitherVariantAccepted_SaveGoesThrough(string accepted)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        stand.Server.Reset();
        var json = CardJson(2);
        stand.Json("GET", ReturnPath, 200, json);
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}");
        stand.Server.Given(Request.Create().WithPath(PinPath).UsingMethod("POST").WithBody((string? b) => b is not null && b.Contains("\"object_type\":\"" + accepted + "\"", StringComparison.Ordinal))).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"confirm_token\":\"tok-ot\"}"));
        stand.Json("POST", PinPath, 400, "{\"code\":\"validation_error\",\"errors\":{\"object_type\":[\"Значение недопустимо.\"]}}", priority: 5);
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetPriceForTests(0, "149,5");
            dialog.SetReasonForTests("цена по накладной другая");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            (await Finish(shown)).Should().BeTrue($"сервер принимает object_type «{accepted}» — касса подстраивается");
            stand.Patches.Should().HaveCount(1);
            Body(stand.Patches[0]).GetProperty("items")[0].GetProperty("purchase_price").GetString().Should().Be("149.5");
            stand.Pins.Count.Should().BeInRange(1, 2, "не больше одного повтора с другим object_type");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 24 ч истекли, пока окно открыто ─────────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowExpiredWhileOpen_ReasonShown_NothingSent(bool beforePin)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var open = CardJson(2);
        if (beforePin)
            stand.Json("GET", ReturnPath, 200, CardJson(2, canEdit: false, canVoid: false, reason: "edit_window_expired"));
        else
        {
            stand.Json("GET", ReturnPath, 200, open);
            stand.Json("PATCH", ReturnPath, 409, "{\"code\":\"edit_window_expired\"}");
        }
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(open), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            if (!beforePin)
                EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("24");
            dialog.SaveEnabledForTests.Should().BeFalse("срок вышел — повтор бессмыслен");
            if (beforePin)
            {
                stand.Pins.Should().BeEmpty("срок проверен до PIN");
                stand.Patches.Should().BeEmpty();
            }
            else
                stand.Patches.Should().HaveCount(1);
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            (await Finish(shown)).Should().BeTrue("список перечитать");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── обрыв связи на сохранении ─────────────

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConnectionDropOnSave_RetrySameKey_NoDuplicate(bool serverApplied, bool socketDrop)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(2);
        // фаза 1: ответа на PATCH нет — шлюз отвечает 504 (так выглядит обрыв за nginx: дошла ли правка до сервера, неизвестно)
        // 2026-11-12 (аудит 12.11, Т-19): и настоящий обрыв сокета — сервер закрывает соединение без ответа (WireMock FaultType.EMPTY_RESPONSE)
        stand.Json("GET", ReturnPath, 200, json);
        stand.Server.Given(Request.Create().WithPath(ReturnPath).UsingMethod("PATCH")).AtPriority(1)
            .RespondWith(socketDrop
                ? Response.Create().WithFault(FaultType.EMPTY_RESPONSE)
                : Response.Create().WithStatusCode(504).WithHeader("Content-Type", "text/html").WithBody("<html><body>504 Gateway Time-out</body></html>"));
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        SupplierReturnEditDialog? reopened = null;
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save, 90000);
            dialog.ErrorText.Should().Contain("неизвестно, дошла ли правка", "исход неизвестен — честно сказано, а не «ничего не изменено»");
            dialog.ErrorText.Should().Contain("не проведёт её второй раз", "и что повтор безопасен");
            dialog.SaveEnabledForTests.Should().BeTrue();
            var firstKeys = stand.Patches.Select(p => Header(p, "Idempotency-Key")).Distinct().ToList();
            firstKeys.Should().HaveCount(1, "даже автоповтор HttpClient — с тем же ключом");
            // окно закрыли и открыли снова (ключ повтора живёт по id возврата, а не по окну)
            dialog.Close();
            await Finish(shown);

            // фаза 2: связь вернулась
            stand.Server.Reset();
            stand.Json("POST", PinPath, 200, "{\"confirm_token\":\"tok-2\"}");
            stand.Json("GET", ReturnPath, 200, serverApplied ? new System.Text.RegularExpressions.Regex("\"qty\":\"2\"").Replace(CardJson(2, version: 4), "\"qty\":\"1\"", 1) : json); // serverApplied: ответ потерялся, а сервер правку провёл — карточка уже v4 С НАШИМ составом (строка 0: 1 шт; 2026-11-12, аудит В-3: «скорее всего, сохранено» — только если состав совпал)
            stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4,\"replayed\":true}");
            reopened = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
            var shown2 = reopened.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            reopened.SetQtyForTests(0, "1");
            reopened.SetReasonForTests("ошиблись количеством");
            var retry = reopened.SaveForTestsAsync();
            if (serverApplied)
            {
                await Finish(retry);
                reopened.ErrorText.Should().Contain("скорее всего, изменение сохранено", "своя правка прошла — не «изменил другой»");
                stand.Patches.Should().BeEmpty("повторно ничего не отправлено — дубля нет");
                UiDrive.Click(reopened, UiDrive.ButtonByText(reopened, "Отмена"));
                (await Finish(shown2)).Should().BeTrue();
            }
            else
            {
                EnterPin(reopened).Should().BeTrue();
                await Finish(retry);
                (await Finish(shown2)).Should().BeTrue();
                stand.Patches.Should().HaveCount(1);
                Header(stand.Patches[0], "Idempotency-Key").Should().Be(firstKeys[0], "повтор того же состава после обрыва — тот же Idempotency-Key (сервер не проведёт дважды), даже в новом окне");
            }
            reopened = null;
        }
        finally
        {
            reopened?.Close();
            Close(dialog, owner);
        }
    }

    // ───────────── две копии окна ─────────────

    [AvaloniaFact]
    public async Task TwoWindowsSameReturn_SecondGetsConflict_OnePatch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(2);
        stand.Json("GET", ReturnPath, 200, json);
        stand.Server.Given(Request.Create().WithPath(ReturnPath).UsingMethod("PATCH")).InScenario("par").WillSetStateTo("v4").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r1\",\"version\":4}"));
        stand.Server.Given(Request.Create().WithPath(ReturnPath).UsingMethod("GET")).InScenario("par").WhenStateIs("v4").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(CardJson(2, version: 4)));
        var owner = Owner();
        var a = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        var b = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shownA = a.ShowDialog<bool>(owner);
            var shownB = b.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            foreach (var d in new[] { a, b })
            {
                d.SetQtyForTests(0, d == a ? "1" : "0");
                d.SetReasonForTests("правка с двух касс");
            }
            var saveA = a.SaveForTestsAsync();
            EnterPin(a).Should().BeTrue();
            await Finish(saveA);
            (await Finish(shownA)).Should().BeTrue();
            var saveB = b.SaveForTestsAsync();
            await Finish(saveB);
            b.ErrorText.Should().Contain("другим сотрудником");
            b.SaveEnabledForTests.Should().BeFalse();
            stand.Patches.Should().HaveCount(1, "вторая копия окна не затирает первую правку");
            UiDrive.Click(b, UiDrive.ButtonByText(b, "Отмена"));
            (await Finish(shownB)).Should().BeTrue();
        }
        finally
        {
            a.Close();
            Close(b, owner);
        }
    }

    // ───────────── аннулирован → повторное открытие; нет признака; нет флагов ─────────────

    [AvaloniaFact]
    public async Task Voided_ThenReopen_NotEditable_WithReason()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        stand.Json("POST", ReturnPath + "void/", 200, "{\"id\":\"r1\",\"version\":4,\"status\":\"voided\"}");
        await stand.Edit.VoidAsync(ReturnDocKind.Supplier, "r1", 3, "дубль возврата", "tok-1", "void-key-1");
        stand.Json("GET", ReturnPath, 200, CardJson(2, version: 4, canEdit: false, canVoid: false, voided: true));
        var prep = await ReturnEditFlows.PrepareSupplierEditAsync(Info(CardJson(2)), stand.Edit);
        prep.Card.Should().BeNull("аннулированный возврат не правится");
        prep.Message.Should().Contain("аннулирован");
        prep.Reload.Should().BeTrue();
        Header(stand.Calls("POST", ReturnPath + "void/").Single(), "Idempotency-Key").Should().Be("void-key-1");
    }

    [AvaloniaFact]
    public async Task CardWithoutFlags_ClearMessage_NotSilent()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        stand.Json("GET", ReturnPath, 200, CardJson(2, flags: false));
        var prep = await ReturnEditFlows.PrepareSupplierEditAsync(Info(CardJson(2, flags: false)), stand.Edit);
        prep.Card.Should().BeNull();
        prep.Message.Should().Contain("can_edit");
    }

    [AvaloniaFact]
    public async Task FeatureOff_PrepareExplains_NoRequest()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync(feature: false);
        var prep = await ReturnEditFlows.PrepareSupplierEditAsync(Info(CardJson(2)), stand.Edit);
        prep.Card.Should().BeNull();
        prep.Message.Should().Contain("не включил");
        Calls(stand, "GET").Should().BeEmpty("без признака сервера карточку не запрашиваем");
    }

    // ───────────── дробные количества ─────────────

    [AvaloniaFact]
    public async Task FractionalQty_KgKeptExact_PieceMustBeWhole()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(3);
        stand.Json("GET", ReturnPath, 200, json);
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}");
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            UiDrive.AllText(dialog).Should().Contain("1,255", "дробное количество показано полностью");
            dialog.SetReasonForTests("ошиблись весом");
            dialog.SetQtyForTests(0, "1,5"); // штучный — дробное нельзя
            await dialog.SaveForTestsAsync();
            dialog.ErrorText.Should().Contain("целое");
            stand.Pins.Should().BeEmpty();
            dialog.SetQtyForTests(0, "2");
            dialog.SetQtyForTests(2, "0,755");
            dialog.TotalText.Should().Contain("715,63", "2 × 150,50 + 2 × 150,50 + 0,755 × 150,50 (113,6275 → 113,63) = 715,63 (до копейки)");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            (await Finish(shown)).Should().BeTrue();
            Body(stand.Patches.Single()).GetProperty("items")[2].GetProperty("qty").GetString().Should().Be("0.755");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── смена языка и темы при открытом окне ─────────────

    [AvaloniaFact]
    public async Task LanguageAndThemeSwitch_WhileOpen_NoCrash_TotalInNewLanguage()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(30);
        var owner = Owner(1024, 600);
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            UiShot.Setup(UiTheme.Dark, AppLanguage.Kyrgyz);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            UiShot.Pump();
            dialog.TotalText.Should().StartWith("Суммасы", "пересчёт после смены языка — на новом языке");
            dialog.IsVisible.Should().BeTrue();
            SaveShot(dialog, "supret-edit-dark-ky-1024x600");
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            UiShot.Settle(2);
            dialog.SetQtyForTests(0, "2");
            UiShot.Pump();
            dialog.TotalText.Should().StartWith("Сумма");
        }
        finally
        {
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            Close(dialog, owner);
        }
    }

    // ───────────── закрытие окна во время записи ─────────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseWhileWriting_NoCrash_UserCannotCloseWhileBusy(bool duringPatch)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(2);
        stand.Json("GET", ReturnPath, 200, json, delay: duringPatch ? null : TimeSpan.FromMilliseconds(1500));
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}", delay: TimeSpan.FromMilliseconds(1500));
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(Info(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            // 2026-11-12 (аудит 12.11, Т-19): раньше ждали 60–150 мс «на глаз» при задержке ответа 1500 мс — теперь ждём само состояние: запрос ушёл и окно занято
            if (duringPatch)
            {
                EnterPin(dialog).Should().BeTrue();
                Wait(() => stand.Server.LogEntries.Any(e => e.RequestMessage.Method == "PATCH") || !dialog.SaveEnabledForTests, 5000);
            }
            else
                Wait(() => !dialog.SaveEnabledForTests, 5000);
            dialog.SaveEnabledForTests.Should().BeFalse("запрос ещё идёт (ответ сервера задержан на 1,5 с) — окно занято");
            save.IsCompleted.Should().BeFalse("сохранение ещё не закончилось — проверяем именно «во время записи»");
            dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is "Отмена")
                .Should().OnlyContain(b => !b.IsEffectivelyEnabled, "пока идёт запрос, кнопки подвала выключены — окно не закрыть посреди записи");
            dialog.Close(); // закрыли из кода (например, закрылось окно-владелец)
            await Finish(save);
            await Finish(shown);
            UiShot.Settle(2);
            if (duringPatch)
                stand.Patches.Should().HaveCount(1, "отправленная правка дошла один раз");
            else
            {
                stand.Pins.Should().BeEmpty("окно закрыто до PIN — дальше ничего не идёт");
                dialog.OwnedWindows.Should().BeEmpty();
                stand.Patches.Should().BeEmpty();
            }
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 200 открытий/закрытий ─────────────

    [AvaloniaFact]
    public async Task OpenClose200_NoMemoryGrowth_NoHangingDialogs()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var info = Info(CardJson(30));
        var owner = Owner();
        try
        {
            void Cycle(List<WeakReference>? track)
            {
                var d = new SupplierReturnEditDialog(info, "ВП-2026-000123", stand.Edit);
                _ = d.ShowDialog<bool>(owner);
                UiShot.Pump();
                d.SetQtyForTests(0, "1");
                d.Close();
                UiShot.Pump();
                track?.Add(new WeakReference(d));
            }
            for (var i = 0; i < 20; i++)
                Cycle(null);
            var before = Mem();
            var sw = Stopwatch.StartNew();
            var refs = new List<WeakReference>();
            for (var i = 0; i < 200; i++)
                Cycle(i >= 190 ? refs : null);
            var elapsed = sw.Elapsed;
            UiShot.Settle(3, 10);
            var after = Mem();
            var alive = refs.Count(r => r.IsAlive);
            Note($"[open/close ×200] {elapsed.TotalMilliseconds:0} мс ({elapsed.TotalMilliseconds / 200:0} мс на цикл), память {Mb(before)} → {Mb(after)} МБ (рост {Mb(after - before)} МБ), живых окон из последних 10: {alive}");
            Mb(after - before).Should().BeLessThan(15, "200 открытий окна не копят память");
            alive.Should().BeLessThanOrEqualTo(1, "закрытые окна не держатся подписками");
            owner.OwnedWindows.Should().BeEmpty();
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double OpenMb, WeakReference Dialog) OpenMeasured(Stand stand, Window owner, int lines)
    {
        var info = Info(CardJson(lines));
        var mem0 = Mem();
        var dialog = new SupplierReturnEditDialog(info, "ВП-2026-000123", stand.Edit);
        _ = dialog.ShowDialog<bool>(owner);
        UiShot.Pump();
        UiShot.Pump();
        var open = Mem() - mem0;
        dialog.Close();
        UiShot.Pump();
        return (Mb(open), new WeakReference(dialog));
    }

    /// <summary>2026-11-12 (аудит Fable, В-7): память открытой страницы не зависит от длины накладной — 2000 строк не дороже 500 больше чем на 3 МБ; каждое окно &lt; 35 МБ; после закрытия собрано.</summary>
    [AvaloniaFact]
    public async Task SupplierEdit_MemoryOpen_DoesNotGrowWithRows()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        stand.Json("GET", ReturnPath, 200, CardJson(1));
        var owner = Owner();
        try
        {
            OpenMeasured(stand, owner, 1); // прогрев кешей
            var (m500, w500) = OpenMeasured(stand, owner, 500);
            var (m2000, w2000) = OpenMeasured(stand, owner, 2000);
            Note($"[memory-open] 500 строк: {m500} МБ, 2000 строк: {m2000} МБ");
            m500.Should().BeLessThan(35);
            m2000.Should().BeLessThan(35);
            m2000.Should().BeLessThanOrEqualTo(m500 + 3, "память открытой страницы не растёт с числом строк накладной");
            AssertCollected(w500, "Окно правки (500 строк)");
            AssertCollected(w2000, "Окно правки (2000 строк)");
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 2026-11-12 (аудит Fable, В-7/В-12): окно правки после СОХРАНЕНИЯ (через PIN) собирается сборщиком ─────────────

    /// <summary>Цикл «открыть → ввести → Сохранить → PIN цифрами → PATCH → окно закрылось само». Возвращает слабые ссылки на окно правки и окно PIN; сильных ссылок не оставляет
    /// (NoInlining, отдельный метод — локальные переменные не держат окна в отладочной сборке).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Dialog, WeakReference Pin, bool Saved) SaveCycle(Stand stand, Window owner, SupplierReturnInfo info, int showMore = 0)
    {
        var dialog = new SupplierReturnEditDialog(info, "ВП-2026-000123", stand.Edit);
        var shown = dialog.ShowDialog<bool>(owner);
        UiShot.Pump();
        for (var i = 0; i < showMore; i++)
            dialog.ShowMoreForTests();
        UiShot.Pump();
        dialog.SetQtyForTests(0, "1");
        dialog.SetReasonForTests("ошиблись количеством");
        var save = dialog.SaveForTestsAsync();
        ManagerPinDialog? pd = null;
        Wait(() => (pd = dialog.OwnedWindows.OfType<ManagerPinDialog>().FirstOrDefault(w => w.IsVisible)) is not null, 8000);
        pd.Should().NotBeNull("перед сохранением — окно PIN");
        var pinRef = new WeakReference(pd);
        UiShot.Settle(2, 5);
        foreach (var d in "1234")
            UiDrive.Click(pd!, UiDrive.ButtonByText(pd!, d.ToString()));
        UiDrive.Click(pd!, UiDrive.ButtonByText(pd!, "Подтвердить", "Ырастоо"));
        Wait(() => save.IsCompleted, 30000);
        Wait(() => shown.IsCompleted, 10000);
        var ok = shown.IsCompleted && shown.GetAwaiter().GetResult();
        pd = null;
        UiShot.Settle(2, 10);
        return (new WeakReference(dialog), pinRef, ok);
    }

    private static bool Collected(WeakReference weak)
    {
        for (var i = 0; i < 20; i++)
        {
            UiShot.Settle(2, 30);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.IsAlive)
                return true;
        }
        return false;
    }

    private static void AssertCollected(WeakReference weak, string what)
    {
        if (Collected(weak))
            return;
        var target = weak.Target;
        Assert.Fail(what + " остался в памяти после закрытия." + Environment.NewLine + (target is null ? "(объект собрался во время поиска)" : HeapPath.Find(target)));
    }

    /// <summary>В-7/В-12: в стресс-замере «после закрытия» (42–45 МБ) было больше «памяти окна» (26 МБ) — окно с выведенными строками оставалось в памяти после сохранения.
    /// Здесь — именно путь «Сохранить → PIN → PATCH → закрытие» (OpenClose200 его не проходил): окно правки и окно PIN собираются сборщиком, а рост памяти от цикла
    /// к циклу — не больше малой величины.</summary>
    [AvaloniaFact]
    public async Task SupplierEdit_SaveThenClose_WindowAndPinDialogCollected()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = CardJson(120);
        stand.Json("GET", ReturnPath, 200, json);
        stand.Json("PATCH", ReturnPath, 200, "{\"id\":\"r1\",\"version\":4}");
        var info = Info(json);
        var owner = Owner();
        try
        {
            // прогрев (первое окно PIN, кеши шрифтов и тем — разовая цена, не утечка)
            for (var i = 0; i < 2; i++)
                SaveCycle(stand, owner, info);
            stand.Server.ResetLogEntries();

            // 100 выведенных строк (две «Показать ещё»): если окно удержано, каждый цикл добавляет десятки МБ
            var (dialogRef, pinRef, saved) = SaveCycle(stand, owner, info, showMore: 1);
            saved.Should().BeTrue("окно закрылось с результатом «изменено»");
            AssertCollected(dialogRef, "Окно правки накладной возврата поставщику");
            AssertCollected(pinRef, "Окно PIN руководителя");

            stand.Server.ResetLogEntries();
            var cycles = new List<double>();
            var weak = new List<WeakReference>();
            var before = Mem();
            for (var i = 0; i < 6; i++)
            {
                var (d, p, ok) = SaveCycle(stand, owner, info, showMore: 1);
                ok.Should().BeTrue();
                weak.Add(d);
                weak.Add(p);
                stand.Server.ResetLogEntries(); // журнал мок-сервера — не память окна
                cycles.Add(Mb(Mem() - before));
            }
            Note($"[save→close ×6, 100 строк] рост памяти по циклам, МБ: {string.Join(" / ", cycles)}; живых слабых ссылок: {weak.Count(r => r.IsAlive)}");
            weak.Count(r => r.IsAlive).Should().Be(0, "ни одно окно (правки, PIN) не держится после сохранения");
            // рост от цикла к циклу: последний цикл не дороже предыдущего больше чем на малую величину (разовые кеши остаются в первых циклах)
            (cycles[^1] - cycles[2]).Should().BeLessThan(3, "рост памяти от цикла к циклу после сборки мусора — единицы МБ, а не десятки (удержанное окно = ~0,4 МБ на строку)");
            cycles[^1].Should().BeLessThan(8, "после 6 сохранений память почти не выросла");
            owner.OwnedWindows.Should().BeEmpty();
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── кнопка «Править накладную» и пояснения (ru/ky, 1024×600 и 1366×768) ─────────────

    public static IEnumerable<object[]> Screens() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task Bar_EditButtonOrReason_Everywhere(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var ky = lang == AppLanguage.Kyrgyz;
        await using (var stand = await Stand.StartAsync())
        {
            var host = new Window { Width = width, Height = height };
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
            host.Content = new ScrollViewer { Content = panel };
            var editable = Info(CardJson(2));
            var noFlags = Info(CardJson(2, flags: false));
            var expired = Info(CardJson(2, canEdit: false, canVoid: false));
            var voided = Info(CardJson(2, canEdit: false, canVoid: false, voided: true));
            foreach (var r in new[] { editable, noFlags, expired, voided })
                panel.Children.Add(ReturnEditFlows.SupplierBar(panel, r, () => Task.CompletedTask, stand.Edit));
            using (UiShot.Render(host, new UiSize(width, height)))
            {
                UiShot.Settle(3);
                string Texts(int i) => string.Join(" | ", ((Control)panel.Children[i]).GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible)
                    .Select(c => c is Button b ? "[" + UiDrive.TextOf(b) + "]" : c is TextBlock t ? t.Text : null).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
                var edit = ky ? "Накладнойду оңдоо" : "Править накладную";
                Texts(0).Should().Contain("[" + edit + "]", "can_edit — кнопка видна");
                Texts(1).Should().Contain("[" + edit + "]", "строка списка без can_edit — кнопка всё равно видна (решает карточка)");
                Texts(2).Should().NotContain("[" + edit + "]").And.Contain("24", "can_edit:false без кода — пояснение «прошло 24 часа»");
                Texts(3).Should().NotContain("[" + edit + "]", "аннулированный — без правки");
                TextClipChecker.Find(host, "supret-bar", lang.ToString()).Should().BeEmpty();
                SaveShot(host, $"supret-bar-{(ky ? "ky" : "ru")}-{width}x{height}");
            }
            host.Close();
            UiShot.Pump();
        }

        // признака сервера нет — пояснение вместо молчаливого отсутствия кнопки
        await using (var off = await Stand.StartAsync(feature: false))
        {
            var host = new Window { Width = width, Height = 200 };
            var panel = new StackPanel { Margin = new Thickness(16) };
            host.Content = panel;
            panel.Children.Add(ReturnEditFlows.SupplierBar(panel, Info(CardJson(2)), () => Task.CompletedTask, off.Edit));
            using (UiShot.Render(host, new UiSize(width, 200)))
            {
                UiShot.Settle(3);
                var text = string.Join(" ", panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                text.Should().Contain(ky ? "күйгүзгөн жок" : "не включил", "без признака сервера видно, почему правки нет");
                TextClipChecker.Find(host, "supret-bar-off", lang.ToString()).Should().BeEmpty();
                SaveShot(host, $"supret-bar-off-{(ky ? "ky" : "ru")}-{width}x{height}");
            }
            host.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Окно правки с 30 строками на 1024×600 и 1366×768 (ru/ky): не выше/шире экрана, главная кнопка видна, текст не обрезан.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task EditDialog30Rows_FitsScreen(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        await using var stand = await Stand.StartAsync();
        var owner = Owner(width, height);
        var dialog = new SupplierReturnEditDialog(Info(CardJson(30)), "ВП-2026-000123", stand.Edit);
        try
        {
            // 2026-11-12 (аудит 12.11, Т-11): подменён размер «экрана» — карточку ограничивает сам ModalShell (85 %), тест MaxHeight не выставляет
            ModalShell.WorkingAreaForTests = new Size(width, height);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            (dialog.Content as ModalShell)!.Bounds.Height.Should().BeLessThanOrEqualTo(Math.Floor(height * 0.85) + 0.5, "ModalShell сам ограничил высоту 85 % экрана");
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height);
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width);
            var save = UiDrive.ButtonByText(dialog, "Сохранить", "Сактоо");
            save.TranslatePoint(new Point(0, save.Bounds.Height), dialog)!.Value.Y.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5, "«Сохранить» видна без прокрутки");
            UiDrive.AllText(dialog).Should().Contain(lang == AppLanguage.Kyrgyz ? "накладнойун оңдоо" : "Правка накладной");
            TextClipChecker.Find(dialog, "supret-edit30", lang.ToString()).Should().BeEmpty();
            SaveShot(dialog, $"supret-edit30-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    /// <summary>Окно «Новый возврат поставщику» после оформления: «Готово», «Править накладную», «Распечатать накладную возврата» помещаются и не обрезаны.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task CreateDialog_AfterSave_EditWaybillButtonVisible(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UiFake.Reset();
        var owner = Owner(width, height);
        var dialog = new SupplierReturnDialog();
        try
        {
            ModalShell.WorkingAreaForTests = new Size(width, height); // 2026-11-12 (аудит 12.11, Т-11)
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            dialog.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) == ReturnEditFlows.SupplierEditButtonText())
                .Should().BeFalse("до оформления строки правятся в самой форме");
            dialog.ShowSavedForTests("s1", "r1", 2);
            UiShot.Settle(4);
            (dialog.Content as ModalShell)!.Bounds.Height.Should().BeLessThanOrEqualTo(Math.Floor(height * 0.85) + 0.5, "ModalShell сам ограничил высоту 85 % экрана");
            var edit = UiDrive.ButtonByText(dialog, ReturnEditFlows.SupplierEditButtonText());
            edit.IsEffectivelyEnabled.Should().BeTrue();
            var right = edit.TranslatePoint(new Point(edit.Bounds.Width, edit.Bounds.Height), dialog)!.Value;
            right.X.Should().BeLessThanOrEqualTo(dialog.Bounds.Width + 0.5, "кнопка внутри окна");
            right.Y.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5);
            // 2026-11-12: третьей кнопкой подвала «Распечатать накладную возврата» уходила за правый край на 1024 (ky) — теперь все кнопки внутри окна
            foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Any(c => c.StartsWith("modal-", StringComparison.Ordinal))))
            {
                var p = b.TranslatePoint(new Point(b.Bounds.Width, 0), dialog)!.Value;
                p.X.Should().BeLessThanOrEqualTo(dialog.Bounds.Width - 8, $"кнопка «{UiDrive.TextOf(b)}» не уходит за край окна");
            }
            TextClipChecker.Find(dialog, "supret-create-saved", lang.ToString()).Should().BeEmpty();
            SaveShot(dialog, $"supret-create-saved-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            Close(dialog, owner);
            UiFake.Reset();
        }
    }

    // ───────────── программа владельца: «Возвраты поставщикам» → «Править накладную» → окно правки ─────────────

    [AvaloniaTheory]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    public async Task OwnerSection_SupplierReturns_EditButton_OpensEditDialog(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var fake = UiFake.Reset();
        var card = CardJson(3);
        fake.Json("GET", "/api/main/capabilities/", 200, "[\"supplier_return_edit_24h\"]");
        fake.Json("GET", "/api/main/suppliers/returns/", 200, "{\"count\":1,\"results\":[" + card + "]}");
        fake.Json("GET", ReturnPath, 200, card);
        var api = TestHost.Services.GetService(typeof(NurMarketApiClient)) as NurMarketApiClient;
        await ServerCapabilities.LoadAsync(api!, force: true);
        var window = new SupplierReturnsWindow();
        window.AsOwnerSection();
        try
        {
            using (UiShot.Render(window, new UiSize(width, height)))
            {
                Wait(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) == ReturnEditFlows.SupplierEditButtonText()), 10000);
                var edit = UiDrive.ButtonByText(window, ReturnEditFlows.SupplierEditButtonText());
                TextClipChecker.Find(window, "supret-owner", lang.ToString()).Should().BeEmpty();
                SaveShot(window, $"supret-owner-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
                UiDrive.Click(window, edit);
                SupplierReturnEditDialog? dialog = null;
                Wait(() => (dialog = window.OwnedWindows.OfType<SupplierReturnEditDialog>().FirstOrDefault()) is not null, 10000);
                dialog.Should().NotBeNull("«Править накладную» в программе владельца открывает окно правки");
                UiShot.Settle(4);
                dialog!.RowCountForTests.Should().Be(3);
                SaveShot(dialog, $"supret-owner-edit-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
                dialog.Close();
                UiShot.Settle(3);
            }
        }
        finally
        {
            window.Close();
            fake.Json("GET", "/api/main/capabilities/", 200, "[]");
            await ServerCapabilities.LoadAsync(api!, force: true);
            ServerCapabilities.Reset();
            UiFake.Reset();
            UiShot.Pump();
        }
    }
    // ───────────── 2026-11-12 (аудит 12.11, пакет 7): итоги «Возвратов поставщикам» и ошибка открытия правки ─────────────

    private static string ListRow(string id, string amount, string compensation, bool voided) =>
        "{\"id\":\"" + id + "\",\"number_display\":\"ВП-" + id + "\",\"supplier\":\"s1\",\"supplier_name\":\"Поставщик\",\"total_amount\":\"" + amount + "\",\"compensation\":\"" + compensation
        + "\",\"status\":\"" + (voided ? "voided" : "posted") + "\",\"created_at\":\"" + DateTimeOffset.Now.AddHours(-1).ToString("O") + "\",\"items\":[]}";

    /// <summary>Л-8 / Д-13: «На сумму» = «в кассу» + «в счёт долга» до копейки; аннулированный возврат не входит ни в одну плитку (а meta.total_amount сервера при status=all — со всеми).</summary>
    [AvaloniaFact]
    public void KpiTotals_ExcludeVoided_ExactKopecks()
    {
        var rows = JsonDocument.Parse("[" + ListRow("1", "0.10", "cash", false) + "," + ListRow("2", "0.20", "cash", false) + "," + ListRow("3", "11.06", "debt_offset", false)
            + "," + ListRow("4", "999.99", "cash", true) + "]").RootElement.Clone();
        var k = SupplierReturnsWindow.KpiTotals(rows, serverCount: 4);
        k.Count.Should().Be(3, "аннулированный — не в счётчике");
        k.Cash.Should().Be(0.30m, "0,10 + 0,20 = 0,30 ровно (в double было бы 0,30000000000000004)");
        k.Debt.Should().Be(11.06m);
        k.Total.Should().Be(11.36m);
        k.Total.Should().Be(k.Cash + k.Debt, "«На сумму» = «в кассу» + «в счёт долга» до копейки");
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task SupplierReturnsList_WithVoided_TilesAddUpToTheKopeck(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var fake = UiFake.Reset();
        fake.Json("GET", "/api/main/capabilities/", 200, "[\"supplier_return_edit_24h\"]");
        // сервер при status=all: count и meta.total_amount — со всеми, в том числе с аннулированным на 999,99
        fake.Json("GET", "/api/main/suppliers/returns/", 200, "{\"count\":3,\"meta\":{\"total_amount\":\"1350.06\"},\"results\":["
            + ListRow("a1", "100.03", "cash", false) + "," + ListRow("a2", "250.04", "debt_offset", false) + "," + ListRow("a3", "999.99", "cash", true) + "]}");
        var api = TestHost.Services.GetService(typeof(NurMarketApiClient)) as NurMarketApiClient;
        await ServerCapabilities.LoadAsync(api!, force: true);
        var window = new SupplierReturnsWindow();
        window.AsOwnerSection();
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768)))
            {
                Wait(() => UiDrive.AllText(window).Contains("350,07"), 10000);
                fake.Server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/main/suppliers/returns/" && e.RequestMessage.Url.Contains("status=all"),
                    "при признаке правки 24 ч список просит и аннулированные");
                var text = UiDrive.AllText(window);
                var som = lang == AppLanguage.Kyrgyz ? "сом" : "сом";
                text.Should().Contain("350,07 " + som, "«На сумму» — 100,03 + 250,04 без аннулированного");
                text.Should().Contain("100,03 " + som, "«в кассу»");
                text.Should().Contain("250,04 " + som, "«в счёт долга»");
                text.Should().NotContain("1 350,06", "итог сервера со всеми (status=all) больше не показывается");
                text.Should().NotContain("1350,06");
                SaveShot(window, $"supret-kpi-voided-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-1366x768");
            }
        }
        finally
        {
            window.Close();
            fake.Json("GET", "/api/main/capabilities/", 200, "[]");
            await ServerCapabilities.LoadAsync(api!, force: true);
            ServerCapabilities.Reset();
            UiFake.Reset();
            UiShot.Pump();
        }
    }

    /// <summary>Л-22: «Править накладную» после оформления не открылась (обрыв связи, сбой окна) — пользователь видит красную строку, а не тишину.
    /// Открытие правки подменено (EditOpenerForTests бросает исключение) — без модальных окон, детерминированно.</summary>
    [AvaloniaFact]
    public void CreateDialog_EditWaybillFails_ErrorShownOnScreen()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var calls = 0;
        SupplierReturnDialog.EditOpenerForTests = (_, info, _, _) =>
        {
            calls++;
            info.Id.Should().Be("r1", "правка — по id из ответа сервера");
            throw new HttpRequestException("обрыв связи");
        };
        var owner = Owner();
        var dialog = new SupplierReturnDialog();
        try
        {
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4);
            dialog.ShowSavedForTests("s1", "r1", 2);
            dialog.ErrorTextForTests.Should().BeEmpty();
            var edit = dialog.EditSavedForTestsAsync();
            Wait(() => edit.IsCompleted, 5000);
            edit.IsCompleted.Should().BeTrue("ошибка не подвешивает окно");
            calls.Should().Be(1);
            dialog.ErrorTextForTests.Should().Be(SupplierReturnDialog.EditOpenFailedText, "ошибка открытия правки видна на экране");
        }
        finally
        {
            SupplierReturnDialog.EditOpenerForTests = null;
            Close(dialog, owner);
        }
    }
}
