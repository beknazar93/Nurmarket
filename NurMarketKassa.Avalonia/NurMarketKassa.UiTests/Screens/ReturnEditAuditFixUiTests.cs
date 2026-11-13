using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (аудит «Возвраты/Владелец/Тесты» 12.11, пакеты П-1 и П-2): окна правки возврата от клиента и накладной поставщику и шаг «Удалить возврат» в Avalonia.Headless
/// против поддельного сервера (WireMock; настоящий NurCRM и принтеры не нужны).
/// К-1: правка и аннулирование возврата от клиента → журнал возвратов кассы и смена обновлены → потолок нового возврата и итог смены сходятся с сервером; В-1: кассиру сказано, что делать
/// с деньгами; В-2: обрыв связи — «неизвестно, дошла ли правка», тот же ключ повтора, а при аннулировании — «уже аннулирован» по следующему входу; В-3: после отказа 409 и чужой правки —
/// «изменён другим сотрудником», а не «скорее всего, сохранено»; В-4/В-5: нетронутая строка накладной уходит с исходной ценой, дробная строка без единицы не мешает; С-1: вопрос при закрытии
/// с изменениями; С-5: длина причины — в окне ввода; С-6: ошибка в скрытой строке раскрывает её; С-7: «Показать ещё» добавляет страницу и не сбрасывает фокус; С-9: вопрос при цене 0.</summary>
[Collection("UiScenarios")]
public sealed class ReturnEditAuditFixUiTests
{
    private const string PinPath = "/api/users/auth/pin/verify/";

    // ───────────── поддельный сервер ─────────────

    private sealed class Stand : IAsyncDisposable
    {
        public WireMockServer Server { get; } = WireMockServer.Start();
        public NurMarketApiClient Api { get; private set; } = null!;
        public ReturnEditApi Edit { get; private set; } = null!;

        public static async Task<Stand> StartAsync()
        {
            var s = new Stand();
            s.Json("POST", "/api/users/auth/login/", 200, "{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}", priority: 9);
            s.Api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Server.Url! });
            await s.Api.LoginAsync("a@b.kg", "12345678");
            s.Json("GET", "/api/main/capabilities/", 200, "[\"pos_return_edit_24h\",\"supplier_return_edit_24h\"]", priority: 9);
            await ServerCapabilities.LoadAsync(s.Api, force: true);
            s.Edit = new ReturnEditApi(s.Api);
            s.Pin();
            return s;
        }

        public void Pin(string token = "tok-1") => Json("POST", PinPath, 200, "{\"confirm_token\":\"" + token + "\",\"approved_by_name\":\"Айбек\"}", priority: 8);

        public void Json(string method, string path, int status, string body, int priority = 5, TimeSpan? delay = null)
        {
            var r = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
            if (delay is { } d)
                r = r.WithDelay(d);
            Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(r);
        }

        public List<ILogEntry> Calls(string method, string path) => Server.LogEntries.Where(e => e.RequestMessage.Method == method && e.RequestMessage.Path == path).ToList();

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

    private static JsonElement Body(ILogEntry e) => JsonDocument.Parse(e.RequestMessage.Body!).RootElement.Clone();

    private static string Header(ILogEntry e, string name) => e.RequestMessage.Headers![name].Single();

    // ───────────── шаги окон ─────────────

    private static Window Owner()
    {
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1366, 768));
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
        task.IsCompleted.Should().BeTrue("операция закончилась");
        return await task;
    }

    private static async Task Finish(Task task, int ms = 20000)
    {
        Wait(() => task.IsCompleted, ms);
        task.IsCompleted.Should().BeTrue("операция закончилась");
        await task;
    }

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

    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Close(Window? dialog, Window owner)
    {
        dialog?.Close();
        owner.Close();
        UiShot.Pump();
    }

    private static IEnumerable<Window> Walk(IEnumerable<Window> windows)
    {
        foreach (var w in windows.ToList())
        {
            yield return w;
            foreach (var child in Walk(w.OwnedWindows))
                yield return child;
        }
    }

    /// <summary>Отвечает на модальные окна шага «Удалить возврат» как кассир (по таймеру: окна сообщений в Headless показываются вложенным циклом, который блокирует тест, пока их не закрыть):
    /// подтверждение — «Удалить» (или по <see cref="Answer"/>), причина — текст, PIN набирается цифрами и уходит на заглушку сервера, итог — «Понятно».
    /// Тексты окон — в <see cref="Questions"/> и <see cref="Alerts"/>.</summary>
    private sealed class Autopilot : IDisposable
    {
        private readonly Window _owner;
        private readonly HashSet<Window> _handled = new();
        private readonly Avalonia.Threading.DispatcherTimer _timer;
        public List<string> Questions { get; } = new();
        public List<string> Alerts { get; } = new();
        public Func<string, bool> Answer { get; set; } = _ => true;
        public string Reason { get; set; } = "дубль возврата";

        public Autopilot(Window owner)
        {
            _owner = owner;
            _timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(60), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            foreach (var w in Walk(_owner.OwnedWindows))
            {
                if (!w.IsVisible || _handled.Contains(w))
                    continue;
                switch (w)
                {
                    case PosConfirmDialog confirm:
                        _handled.Add(w);
                        var q = UiDrive.AllText(confirm) + "\n" + confirm.Title;
                        Questions.Add(q);
                        Press(confirm.FindControl<Button>(Answer(q) ? "ConfirmButton" : "CancelButton")!);
                        return;
                    case PosAlertDialog alert:
                        _handled.Add(w);
                        Alerts.Add(UiDrive.AllText(alert));
                        Press(alert.FindControl<Button>("OkButton")!);
                        return;
                    case SimpleInputDialog input:
                        _handled.Add(w);
                        input.GetVisualDescendants().OfType<TextBox>().First().Text = Reason;
                        Press(UiDrive.ButtonByText(input, "Далее", "Андан ары"));
                        return;
                    case ManagerPinDialog pin:
                        _handled.Add(w);
                        foreach (var d in "1234")
                            Press(UiDrive.ButtonByText(pin, d.ToString()));
                        Press(UiDrive.ButtonByText(pin, "Подтвердить", "Ырастоо"));
                        return;
                }
            }
        }

        public async Task<T> RunAsync<T>(Task<T> task, int ms = 90000)
        {
            Wait(() => task.IsCompleted, ms);
            task.IsCompleted.Should().BeTrue("шаг «Удалить возврат» дошёл до конца");
            return await task;
        }

        /// <summary>Перед остановкой — ещё немного отвечает на поздние окна (иначе окно, всплывшее после конца теста, держало бы вложенный цикл следующего).</summary>
        public void Dispose()
        {
            Wait(() => false, 600);
            _timer.Stop();
        }
    }

    // ───────────── данные возврата от клиента ─────────────

    private sealed class CustomerCase
    {
        public string Sale = "sale-" + Guid.NewGuid().ToString("N")[..8];
        public string Doc = "pr-" + Guid.NewGuid().ToString("N")[..8];
        public string Shift = "shift-" + Guid.NewGuid().ToString("N")[..8];
        public string DocPath => "/api/main/pos/returns/" + Doc + "/";
        public string SalePath => "/api/main/pos/sales/" + Sale + "/";

        /// <summary>Карточка возврата: 3 шт молока по 100 = 300 (чек на 6 шт, ещё можно вернуть 3).</summary>
        public string Card(int version = 3, int qty = 3, bool voided = false, bool flags = true) =>
            "{\"id\":\"" + Doc + "\",\"sale\":\"" + Sale + "\",\"version\":" + version + ",\"status\":\"" + (voided ? "voided" : "posted") + "\","
            + (flags ? "\"can_edit\":" + (voided ? "false" : "true") + ",\"can_void\":" + (voided ? "false" : "true") + ",\"editable_until\":\"2026-11-13T03:00:00Z\",\"server_now\":\"2026-11-12T08:00:00Z\"," : "")
            + "\"amount\":\"" + (qty * 100) + ".00\",\"items\":[{\"sale_item_id\":\"si-1\",\"name\":\"Молоко 1 л\",\"unit\":\"шт\",\"quantity\":\"" + qty + "\",\"amount\":\"" + (qty * 100) + ".00\"}]}";

        public string SaleJson => "{\"id\":\"" + Sale + "\",\"items\":[{\"id\":\"si-1\",\"quantity\":\"6\",\"returnable_qty\":\"3\"}]}";

        public ReturnDocRef DocRef(string card, double total) => new(ReturnDocKind.Customer, Doc, "№1115", total, ReturnEditState.Parse(JsonDocument.Parse(card).RootElement.Clone()));

        public List<CustomerReturnEditLine> Lines(string card) => CustomerReturnEdit.FromCards(JsonDocument.Parse(card).RootElement.Clone(), JsonDocument.Parse(SaleJson).RootElement.Clone());

        /// <summary>Чек на 600 (наличные) и этот возврат на 300 на этой кассе — как записывает окно возврата: журнал кассы и событие смены.</summary>
        public void RecordRefund()
        {
            SaleReturnLedger.RememberPayment(Sale, 600m, "cash", 600m, 0m, null, null, 600m, DateTimeOffset.Now);
            SaleReturnLedger.AddRefund(Sale, new SaleLedgerRefund
            {
                At = DateTimeOffset.Now, Amount = 300m, Cash = 300m, ServerAmount = 300m, ServerDocId = Doc,
                Lines = new() { new SaleLedgerLine { LineId = "si-1", Name = "Молоко 1 л", Quantity = 3, UnitPrice = 100m, Total = 300m } },
            });
            ShiftEventsStore.Record(ShiftEventsStore.KindReturn, Shift, ShiftEventsStore.OperationKey(Sale), 300, "cash=300.00;noncash=0.00;debt=0.00");
        }

        public double ShiftReturns() => ShiftEventsStore.TotalsForShift(Shift).GetValueOrDefault(ShiftEventsStore.KindReturn);
    }

    private static string? _savedShift;

    private static void UseShift(string shift)
    {
        _savedShift = PosApp.ActiveShiftId;
        PosApp.ActiveShiftId = shift;
    }

    private static void RestoreShift() => PosApp.ActiveShiftId = _savedShift;

    // ───────────── К-1, В-1, В-8: правка возврата от клиента через окно ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_Save_UpdatesLedgerAndShift_MoneyNote_PinPatchHeadersAndBody()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CustomerReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        c.RecordRefund();
        UseShift(c.Shift);
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Json("PATCH", c.DocPath, 200, "{\"id\":\"" + c.Doc + "\",\"version\":4,\"edit_count\":1,\"amount\":\"200.00\"}");
        var owner = Owner();
        var dialog = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
        try
        {
            SaleReturnLedger.Get(c.Sale)!.Remaining.Should().Be(300m, "до правки: оплачено 600, возвращено 300");
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "2");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            (await Finish(shown)).Should().BeTrue();

            // запросы: PIN на правку именно этого документа и версии; PATCH с версией, ключом, токеном и составом
            var pin = Body(stand.Calls("POST", PinPath).Single());
            pin.GetProperty("scope").GetString().Should().Be("return_edit");
            pin.GetProperty("object_id").GetString().Should().Be(c.Doc);
            pin.GetProperty("version").GetInt32().Should().Be(3);
            var patch = stand.Calls("PATCH", c.DocPath).Should().ContainSingle().Subject;
            Header(patch, "If-Match").Should().Be("\"3\"");
            Header(patch, "X-Confirm-Token").Should().Be("tok-1");
            Header(patch, "Idempotency-Key").Should().NotBeNullOrWhiteSpace();
            var body = Body(patch);
            body.GetProperty("version").GetInt32().Should().Be(3);
            body.GetProperty("edit_reason").GetString().Should().Be("ошиблись количеством");
            var item = body.GetProperty("items").EnumerateArray().Single();
            item.GetProperty("sale_item_id").GetString().Should().Be("si-1");
            item.GetProperty("quantity").GetString().Should().Be("2");

            // журналы кассы: сумма записи — по ответу сервера (200), потолок нового возврата вырос, смена сходится
            var entry = SaleReturnLedger.Get(c.Sale)!;
            entry.Refunds.Single().Amount.Should().Be(200m);
            entry.Remaining.Should().Be(400m, "потолок нового возврата = оплачено 600 − возвращено 200: как на сервере после правки");
            c.ShiftReturns().Should().Be(200, "в смене возврат 200 (сторно 100 на разницу)");
            dialog.JournalChange.Should().NotBeNull();
            dialog.MoneyNote.Should().Contain("Заберите у клиента 100,00").And.Contain("наличными 100,00").And.Contain("сторно");
        }
        finally
        {
            RestoreShift();
            Close(dialog, owner);
        }
    }

    // ───────────── К-1, В-1, В-2, С-5: «Удалить возврат» ─────────────

    [AvaloniaFact]
    public async Task Void_Flow_RemovesLedgerRefund_StornoInShift_ScopeAndVersionSent_MoneyNoteShown()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        c.RecordRefund();
        UseShift(c.Shift);
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Json("POST", c.DocPath + "void/", 200, "{\"id\":\"" + c.Doc + "\",\"version\":4,\"status\":\"voided\"}");
        var owner = Owner();
        using var ap = new Autopilot(owner);
        try
        {
            SaleReturnLedger.Get(c.Sale)!.Remaining.Should().Be(300m);
            var voided = await ap.RunAsync(ReturnEditFlows.VoidAsync(owner, c.DocRef(card, 300), stand.Edit));

            voided.Should().BeTrue();
            var pin = Body(stand.Calls("POST", PinPath).Single());
            pin.GetProperty("scope").GetString().Should().Be("return_void", "PIN выдан именно на аннулирование");
            pin.GetProperty("object_id").GetString().Should().Be(c.Doc);
            pin.GetProperty("version").GetInt32().Should().Be(3);
            var post = stand.Calls("POST", c.DocPath + "void/").Should().ContainSingle().Subject;
            Header(post, "If-Match").Should().Be("\"3\"");
            Header(post, "X-Confirm-Token").Should().Be("tok-1");
            Body(post).GetProperty("version").GetInt32().Should().Be(3, "версия в теле void");
            Body(post).GetProperty("reason").GetString().Should().Be("дубль возврата");

            var entry = SaleReturnLedger.Get(c.Sale)!;
            entry.Refunds.Should().BeEmpty("запись возврата убрана из журнала кассы");
            entry.Remaining.Should().Be(600m, "новый возврат по чеку считается от полного остатка");
            c.ShiftReturns().Should().Be(0, "сторно погасило возврат в смене");
            ap.Alerts.Should().ContainSingle(a => a.Contains("аннулирован")).Which.Should().Contain("Заберите у клиента 300,00");
        }
        finally
        {
            RestoreShift();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Void_ConnectionDrop_UnknownText_ThenRetryFindsVoided_JournalsUpdatedOnce()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        c.RecordRefund();
        UseShift(c.Shift);
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Server.Given(Request.Create().WithPath(c.DocPath + "void/").UsingMethod("POST")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(504).WithHeader("Content-Type", "text/html").WithBody("<html><body>504 Gateway Time-out</body></html>"));
        var owner = Owner();
        var ap = new Autopilot(owner);
        try
        {
            var first = await ap.RunAsync(ReturnEditFlows.VoidAsync(owner, c.DocRef(card, 300), stand.Edit));

            first.Should().BeFalse("исход неизвестен — список не перечитываем как «изменено»");
            ap.Alerts.Should().ContainSingle().Which.Should().Contain("неизвестно, дошло ли аннулирование").And.NotContain("Ничего не изменено");
            SaleReturnLedger.Get(c.Sale)!.Refunds.Should().ContainSingle("пока не знаем, дошло ли, — журнал не трогаем");
            var keys = stand.Calls("POST", c.DocPath + "void/").Select(p => Header(p, "Idempotency-Key")).Distinct().ToList();
            keys.Should().HaveCount(1, "даже автоповтор HttpClient — с тем же ключом");

            ap.Dispose(); // первый помощник больше не нужен — иначе оба нажимали бы одни и те же окна второго шага
            // связь вернулась: аннулирование на сервере, оказывается, прошло — карточка уже аннулирована
            stand.Server.Reset();
            stand.Pin();
            stand.Json("GET", c.DocPath, 200, c.Card(version: 4, voided: true));
            using var ap2 = new Autopilot(owner);
            var second = await ap2.RunAsync(ReturnEditFlows.VoidAsync(owner, c.DocRef(card, 300), stand.Edit));

            second.Should().BeTrue();
            stand.Calls("POST", c.DocPath + "void/").Should().BeEmpty("второй раз аннулирование не отправляется");
            ap2.Alerts.Should().ContainSingle().Which.Should().Contain("прошлая попытка дошла").And.Contain("Заберите у клиента 300,00");
            SaleReturnLedger.Get(c.Sale)!.Remaining.Should().Be(600m);
            c.ShiftReturns().Should().Be(0);
        }
        finally
        {
            RestoreShift();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Void_RefusedByServer_JournalsUntouched_NoStorno()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        c.RecordRefund();
        UseShift(c.Shift);
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Json("POST", c.DocPath + "void/", 409, "{\"code\":\"negative_stock\",\"product_id\":\"p1\",\"available\":\"1\"}");
        var owner = Owner();
        using var ap = new Autopilot(owner);
        try
        {
            await ap.RunAsync(ReturnEditFlows.VoidAsync(owner, c.DocRef(card, 300), stand.Edit));

            ap.Alerts.Should().ContainSingle().Which.Should().Contain("Возврат не удалён").And.Contain("на складе 1");
            SaleReturnLedger.Get(c.Sale)!.Refunds.Should().ContainSingle("сервер отказал — возврат остался в силе");
            c.ShiftReturns().Should().Be(300);
        }
        finally
        {
            RestoreShift();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReasonDialog_TooShort_ErrorInsideDialog_StaysOpen()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = Owner();
        var input = new SimpleInputDialog("Удаление возврата", "Причина удаления (от 5 знаков):", confirmText: "Далее", minLength: 5, maxLength: 500);
        try
        {
            var shown = input.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            var box = input.GetVisualDescendants().OfType<TextBox>().First();
            box.Text = "брак";
            UiDrive.Click(input, UiDrive.ButtonByText(input, "Далее"));
            UiShot.Settle(3);
            input.IsVisible.Should().BeTrue("причина из 4 знаков не принимается — окно остаётся");
            shown.IsCompleted.Should().BeFalse();
            UiDrive.AllText(input).Should().Contain("не меньше 5 знаков");

            box.Text = "брак упаковки";
            UiDrive.Click(input, UiDrive.ButtonByText(input, "Далее"));
            (await Finish(shown)).Should().BeTrue();
            input.Result.Should().Be("брак упаковки");
        }
        finally
        {
            Close(input, owner);
        }
    }

    // ───────────── В-2: обрыв при правке возврата от клиента ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_ConnectionDrop_SameKeyOnRetry_TextUnknown_JournalsNotTouched()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CustomerReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        c.RecordRefund();
        UseShift(c.Shift); // 2026-11-13: журнал кассы правится только при открытой смене (без смены — сверка с сервером позже)
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Server.Given(Request.Create().WithPath(c.DocPath).UsingMethod("PATCH")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(504).WithHeader("Content-Type", "text/html").WithBody("<html><body>504 Gateway Time-out</body></html>"));
        var owner = Owner();
        var dialog = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
        CustomerReturnEditDialog? reopened = null;
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "2");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save, 90000);
            dialog.ErrorText.Should().Contain("неизвестно, дошла ли правка").And.Contain("не проведёт её второй раз").And.NotContain("Ничего не изменено");
            dialog.SaveEnabledForTests.Should().BeTrue();
            SaleReturnLedger.Get(c.Sale)!.Refunds.Single().Amount.Should().Be(300m, "исход неизвестен — журнал не трогаем");
            var firstKeys = stand.Calls("PATCH", c.DocPath).Select(p => Header(p, "Idempotency-Key")).Distinct().ToList();
            firstKeys.Should().HaveCount(1);
            dialog.Close();
            await Finish(shown);

            // связь вернулась; окно открыто заново (ключ живёт по id возврата) — повтор идёт с тем же ключом
            stand.Server.Reset();
            stand.Pin("tok-2");
            stand.Json("GET", c.DocPath, 200, card);
            stand.Json("PATCH", c.DocPath, 200, "{\"id\":\"" + c.Doc + "\",\"version\":4,\"amount\":\"200.00\",\"replayed\":true}");
            reopened = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
            var shown2 = reopened.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            reopened.SetQtyForTests(0, "2");
            reopened.SetReasonForTests("ошиблись количеством");
            var retry = reopened.SaveForTestsAsync();
            EnterPin(reopened).Should().BeTrue();
            await Finish(retry);
            (await Finish(shown2)).Should().BeTrue();
            var patch = stand.Calls("PATCH", c.DocPath).Should().ContainSingle().Subject;
            Header(patch, "Idempotency-Key").Should().Be(firstKeys[0], "повтор того же состава после обрыва — тот же ключ: сервер не проведёт правку дважды");
            SaleReturnLedger.Get(c.Sale)!.Refunds.Single().Amount.Should().Be(200m, "правка прошла — журнал обновлён один раз");
            reopened = null;
        }
        finally
        {
            reopened?.Close();
            Close(dialog, owner);
            RestoreShift();
        }
    }

    // ───────────── В-3: 409 и чужая правка ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_After409_ThenForeignEdit_SaysChangedByOther()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CustomerReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        var card = c.Card();
        stand.Json("GET", c.DocPath, 200, card);
        stand.Json("PATCH", c.DocPath, 409, "{\"code\":\"exceeds_returnable\",\"max_qty\":2}");
        var owner = Owner();
        var dialog = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "2");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("Не сохранено").And.Contain("можно не больше 2");
            dialog.SaveEnabledForTests.Should().BeTrue();

            // пока окно открыто, возврат правит другой сотрудник: версия 4, состав другой
            stand.Server.Reset();
            stand.Pin();
            stand.Json("GET", c.DocPath, 200, c.Card(version: 4, qty: 1));
            var again = dialog.SaveForTestsAsync();
            await Finish(again);
            dialog.ErrorText.Should().Contain("другим сотрудником").And.NotContain("скорее всего").And.NotContain("сохранена");
            stand.Calls("PATCH", c.DocPath).Should().BeEmpty();
        }
        finally
        {
            foreach (var q in dialog.OwnedWindows.OfType<PosConfirmDialog>().ToList())
                q.Close();
            Close(dialog, owner);
        }
    }

    // ───────────── С-1: закрытие с несохранёнными правками ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_CloseWithChanges_Asks_StayKeepsWindow_CloseClosesIt()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var c = new CustomerCase();
        var card = c.Card();
        var owner = Owner();
        var untouched = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
        var dialog = new CustomerReturnEditDialog(c.DocRef(card, 300), c.Lines(card), stand.Edit, c.Sale);
        using var ap = new Autopilot(owner) { Answer = _ => false }; // вопрос «Закрыть без сохранения?» показывается вложенным циклом — отвечает таймер
        try
        {
            // без изменений — закрывается сразу, без вопроса
            var shown0 = untouched.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            UiDrive.Click(untouched, UiDrive.ButtonByText(untouched, "Отмена"));
            (await Finish(shown0)).Should().BeFalse();
            ap.Questions.Should().BeEmpty("правок нет — вопроса нет");

            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            Wait(() => ap.Questions.Count == 1);
            ap.Questions.Should().ContainSingle().Which.Should().Contain("Закрыть без сохранения", "есть несохранённые правки — вопрос");
            UiShot.Settle(4);
            dialog.IsVisible.Should().BeTrue("«Остаться» — окно и правки на месте");
            shown.IsCompleted.Should().BeFalse();

            ap.Answer = _ => true; // «Закрыть»
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            (await Finish(shown)).Should().BeFalse("закрыто без сохранения");
            ap.Questions.Should().HaveCount(2);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_CloseWithChanges_Asks()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(Supplier.Card(2)), "ВП-2026-000123", stand.Edit);
        using var ap = new Autopilot(owner) { Answer = _ => false };
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetReasonForTests("ошиблись");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            Wait(() => ap.Questions.Count == 1);
            ap.Questions.Should().ContainSingle("введена причина — закрытие спрашивает");
            UiShot.Settle(4);
            dialog.IsVisible.Should().BeTrue();
            ap.Answer = _ => true;
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Отмена"));
            (await Finish(shown)).Should().BeFalse();
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── накладная поставщику ─────────────

    private static class Supplier
    {
        public const string Path = "/api/main/suppliers/returns/r1/";

        /// <summary>Карточка возврата поставщику: <paramref name="lines"/> строк; каждая третья — весовая 1,255 кг; цена 150,50. <paramref name="mutate"/> меняет JSON строк.</summary>
        public static string Card(int lines, int version = 3, string? firstQty = null, string? firstPrice = null, bool noUnitOnFractional = false)
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":\"r1\",\"number_display\":\"ВП-2026-000123\",\"supplier_id\":\"s1\",\"supplier_name\":\"ОсОО «Поставщик»\",\"reason\":\"defect\",\"compensation\":\"debt_offset\",");
            sb.Append("\"created_at\":\"2026-11-12T03:00:00Z\",\"comment\":\"брак\",\"total_amount\":\"1000.00\",");
            sb.Append("\"version\":").Append(version).Append(",\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,\"editable_until\":\"2026-11-13T03:00:00Z\",\"server_now\":\"2026-11-12T08:00:00Z\",\"items\":[");
            for (var i = 0; i < lines; i++)
            {
                if (i > 0)
                    sb.Append(',');
                var kg = i % 3 == 2;
                var qty = i == 0 && firstQty is not null ? firstQty : kg ? "1.255" : "2";
                var price = i == 0 && firstPrice is not null ? firstPrice : "150.50";
                sb.Append("{\"id\":\"l").Append(i).Append("\",\"product_id\":\"p").Append(i).Append("\",\"product_name\":\"Товар №").Append(i + 1).Append(kg ? " сыр весовой" : " молоко 1 л")
                    .Append("\",\"qty\":\"").Append(qty).Append('"');
                if (!(kg && noUnitOnFractional))
                    sb.Append(",\"unit\":\"").Append(kg ? "кг" : "шт").Append('"');
                sb.Append(",\"barcode\":\"4600").Append(i.ToString("D4")).Append("\",\"purchase_price\":\"").Append(price).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    private static SupplierReturnInfo SupplierInfo(string json) => SupplierApiService.ParseReturn(JsonDocument.Parse(json).RootElement.Clone());

    [AvaloniaFact]
    public async Task SupplierEdit_After409_ThenForeignEdit_SaysChangedByOther()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SupplierReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        var json = Supplier.Card(3);
        stand.Json("GET", Supplier.Path, 200, json);
        stand.Json("PATCH", Supplier.Path, 409, "{\"code\":\"negative_stock\",\"product_id\":\"p0\",\"available\":\"1\"}");
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("Не сохранено").And.Contain("«Товар №1 молоко 1 л»").And.Contain("на складе 1", "409 называет товар и доступное количество");
            dialog.SaveEnabledForTests.Should().BeTrue("отказ по существу — можно поправить и повторить");

            // потом документ поправил другой сотрудник (версия 4, другое количество)
            stand.Server.Reset();
            stand.Pin();
            stand.Json("GET", Supplier.Path, 200, Supplier.Card(3, version: 4, firstQty: "5"));
            var again = dialog.SaveForTestsAsync();
            await Finish(again);
            dialog.ErrorText.Should().Contain("другим сотрудником").And.NotContain("скорее всего", "наша правка не прошла — ложного «сохранено» нет");
            stand.Calls("PATCH", Supplier.Path).Should().BeEmpty();
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_ConnectionDropThenServerHasOurLines_SaysSaved_ButDifferentLines_SaysChangedByOther()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SupplierReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        var json = Supplier.Card(2);
        stand.Json("GET", Supplier.Path, 200, json);
        stand.Server.Given(Request.Create().WithPath(Supplier.Path).UsingMethod("PATCH")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(504).WithHeader("Content-Type", "text/html").WithBody("<html><body>504</body></html>"));
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save, 90000);
            dialog.ErrorText.Should().Contain("неизвестно, дошла ли правка");

            // на сервере — версия 4 с НАШИМ составом (строка 0 → 1 шт): правка дошла
            stand.Server.Reset();
            stand.Pin();
            stand.Json("GET", Supplier.Path, 200, Supplier.Card(2, version: 4, firstQty: "1"));
            await Finish(dialog.SaveForTestsAsync());
            dialog.ErrorText.Should().Contain("скорее всего, изменение сохранено", "состав на сервере совпал с отправленным — это наша же правка");
            stand.Calls("PATCH", Supplier.Path).Should().BeEmpty("повторно ничего не отправлено");
        }
        finally
        {
            Close(dialog, owner);
        }

        // версия 4, но состав другой (правил другой сотрудник), а наша отправка осталась без ответа: «изменил другой»
        SupplierReturnEditDialog.ResetKeysForTests();
        await using var stand2 = await Stand.StartAsync();
        stand2.Json("GET", Supplier.Path, 200, json);
        stand2.Server.Given(Request.Create().WithPath(Supplier.Path).UsingMethod("PATCH")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(504).WithHeader("Content-Type", "text/html").WithBody("<html><body>504</body></html>"));
        var owner2 = Owner();
        var dialog2 = new SupplierReturnEditDialog(SupplierInfo(json), "ВП-2026-000123", stand2.Edit);
        try
        {
            var shown = dialog2.ShowDialog<bool>(owner2);
            UiShot.Settle(3);
            dialog2.SetQtyForTests(0, "1");
            dialog2.SetReasonForTests("ошиблись количеством");
            var save = dialog2.SaveForTestsAsync();
            EnterPin(dialog2).Should().BeTrue();
            await Finish(save, 90000);
            stand2.Server.Reset();
            stand2.Pin();
            stand2.Json("GET", Supplier.Path, 200, Supplier.Card(2, version: 4, firstQty: "7"));
            await Finish(dialog2.SaveForTestsAsync());
            dialog2.ErrorText.Should().Contain("другим сотрудником").And.NotContain("сохранена");
        }
        finally
        {
            Close(dialog2, owner2);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_UntouchedLines_SentWithFourDecimals_AndFractionalWithoutUnitDoesNotBlock()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SupplierReturnEditDialog.ResetKeysForTests();
        await using var stand = await Stand.StartAsync();
        // строка 0: цена упаковки 33,3333; строка 2: весовая 1,255 без единицы в ответе сервера; строку 1 правим
        var json = Supplier.Card(3, firstPrice: "33.3333", noUnitOnFractional: true);
        stand.Json("GET", Supplier.Path, 200, json);
        stand.Json("PATCH", Supplier.Path, 200, "{\"id\":\"r1\",\"version\":4}");
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(json), "ВП-2026-000123", stand.Edit);
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            // «Изменений нет»: ничего не трогали — цена 33,3333 не округляется до 33,33 и не считается правкой
            dialog.SetReasonForTests("проверка правки");
            await Finish(dialog.SaveForTestsAsync());
            dialog.ErrorText.Should().Contain("Изменений нет");

            dialog.SetQtyForTests(1, "1");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            (await Finish(shown)).Should().BeTrue();
            var items = Body(stand.Calls("PATCH", Supplier.Path).Single()).GetProperty("items").EnumerateArray().ToList();
            items.Should().HaveCount(3);
            items[0].GetProperty("purchase_price").GetString().Should().Be("33.3333", "нетронутая строка — цена до последнего знака");
            items[2].GetProperty("qty").GetString().Should().Be("1.255", "весовая строка без единицы не блокирует сохранение и уходит как была");
            items[1].GetProperty("qty").GetString().Should().Be("1");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_ErrorInHiddenRow_RevealsAndFocusesRow()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(Supplier.Card(120)), "ВП-2026-000123", stand.Edit);
        try
        {
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.ShownRowsForTests.Should().Be(50, "первая страница");
            dialog.RowBoxesForTests(100).Qty.Should().BeNull("строка 100 скрыта страницей");
            dialog.SetQtyForTests(100, "abc");
            dialog.SetReasonForTests("ошиблись количеством");
            await Finish(dialog.SaveForTestsAsync());
            dialog.ErrorText.Should().Contain("Товар №101").And.Contain("введите число");
            dialog.RowBoxesForTests(100).Qty.Should().NotBeNull("строка с ошибкой раскрыта");
            dialog.FocusedRowForTests.Should().Be(100, "фокус стоит в поле строки с ошибкой");
            stand.Calls("GET", Supplier.Path).Should().BeEmpty("ошибка найдена до запросов к серверу");

            // то же при поиске, который скрывает строку: поиск сбрасывается
            dialog.FindForTests("Товар №3 ");
            dialog.RowBoxesForTests(100).Qty.Should().BeNull();
            await Finish(dialog.SaveForTestsAsync());
            dialog.RowBoxesForTests(100).Qty.Should().NotBeNull("поиск сброшен, строка показана");
            dialog.FocusedRowForTests.Should().Be(100);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_ValidationErrorInHiddenRow_RevealsRow()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(Supplier.Card(120)), "ВП-2026-000123", stand.Edit);
        try
        {
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetQtyForTests(80, "1.5"); // штучный товар (шт) — только целое число
            dialog.SetReasonForTests("ошиблись количеством");
            await Finish(dialog.SaveForTestsAsync());
            dialog.ErrorText.Should().Contain("Товар №81").And.Contain("целое");
            dialog.RowBoxesForTests(80).Qty.Should().NotBeNull();
            dialog.FocusedRowForTests.Should().Be(80);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_ShowMore_AppendsPage_KeepsFocusAndBoxes()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(Supplier.Card(120)), "ВП-2026-000123", stand.Edit);
        try
        {
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.ShownRowsForTests.Should().Be(50);
            dialog.FocusRowForTests(0);
            UiShot.Settle(2);
            dialog.FocusedRowForTests.Should().Be(0);
            var before = dialog.RowBoxesForTests(0);

            dialog.ShowMoreForTests();
            UiShot.Settle(2);

            dialog.ShownRowsForTests.Should().Be(100, "добавилась одна страница");
            dialog.RowBoxesForTests(0).Qty.Should().BeSameAs(before.Qty, "уже показанные поля не пересоздаются");
            dialog.RowBoxesForTests(49).Qty.Should().NotBeNull();
            dialog.RowBoxesForTests(99).Qty.Should().NotBeNull();
            dialog.RowBoxesForTests(100).Qty.Should().BeNull();
            dialog.FocusedRowForTests.Should().Be(0, "фокус не потерян");
            UiDrive.AllText(dialog).Should().Contain("показано 100 из 120");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_Find_ByBarcode_And_ByName()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(Supplier.Card(120)), "ВП-2026-000123", stand.Edit);
        try
        {
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.FindForTests("46000077");
            dialog.ShownRowsForTests.Should().Be(1, "поиск по штрихкоду находит строку 77");
            dialog.RowBoxesForTests(77).Qty.Should().NotBeNull();
            dialog.FindForTests("Товар №110 ");
            dialog.ShownRowsForTests.Should().Be(1, "и по названию");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    [AvaloniaFact]
    public async Task SupplierEdit_PriceZero_AsksBeforePin_CheckKeepsWindowNothingSent()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var json = Supplier.Card(2);
        stand.Json("GET", Supplier.Path, 200, json);
        var owner = Owner();
        var dialog = new SupplierReturnEditDialog(SupplierInfo(json), "ВП-2026-000123", stand.Edit);
        using var ap = new Autopilot(owner) { Answer = _ => false }; // «Проверить»
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(3);
            dialog.SetPriceForTests(0, "0");
            dialog.SetReasonForTests("ошиблись ценой");
            var save = dialog.SaveForTestsAsync();
            Wait(() => ap.Questions.Count == 1);
            ap.Questions.Should().ContainSingle("цена 0 — вопрос до PIN").Which.Should().Contain("цена закупки 0").And.Contain("Сохранить так?");
            await Finish(save);
            stand.Calls("POST", PinPath).Should().BeEmpty("до PIN не дошло");
            stand.Calls("PATCH", Supplier.Path).Should().BeEmpty();
            shown.IsCompleted.Should().BeFalse("окно осталось для поправки");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── С-4: ошибка кнопки видна кассиру; С-13: «Загрузка…» и «Повторить» ─────────────

    [AvaloniaFact]
    public async Task BarButtonError_ShownToCashier_NotOnlyInLog()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var stand = await Stand.StartAsync();
        var owner = Owner();
        var panel = new StackPanel();
        owner.Content = panel;
        var doc = new CustomerCase();
        var card = doc.Card();
        panel.Children.Add(ReturnEditFlows.Bar(panel, doc.DocRef(card, 300), _ => throw new InvalidOperationException("сломалось"), () => Task.CompletedTask));
        using var ap = new Autopilot(owner);
        try
        {
            using var _ = UiShot.Render(owner, new UiSize(900, 400));
            UiShot.Settle(3);
            UiDrive.Click(owner, UiDrive.ButtonByText(owner, "Редактировать"));
            Wait(() => ap.Alerts.Count == 1, 5000);
            ap.Alerts.Should().ContainSingle("кассир нажал кнопку — он увидит ошибку").Which.Should().Contain("не выполнено").And.Contain("сломалось");
            await Task.CompletedTask;
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void TimeLeftHint_WordsFollowPermissions()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReturnEditState State(bool edit, bool voidDoc) => ReturnEditState.Parse(JsonDocument.Parse(
            "{\"version\":3,\"can_edit\":" + (edit ? "true" : "false") + ",\"can_void\":" + (voidDoc ? "true" : "false") + ",\"editable_until\":\"2026-11-12T10:00:00Z\",\"server_now\":\"2026-11-12T08:00:00Z\"}").RootElement.Clone());
        ReturnEditFlows.TimeLeftHint(State(true, true)).Should().StartWith("Изменить или удалить можно ещё");
        ReturnEditFlows.TimeLeftHint(State(true, false)).Should().StartWith("Изменить можно ещё").And.NotContain("удалить");
        ReturnEditFlows.TimeLeftHint(State(false, true)).Should().StartWith("Удалить можно ещё").And.NotContain("Изменить");
        ReturnEditFlows.TimeLeftHint(State(false, false)).Should().BeNull();
    }
}
