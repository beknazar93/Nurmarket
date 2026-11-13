using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using WireMock;
using WireMock.ResponseBuilders;
using Xunit;
using static NurMarketKassa.UiTests.Harness.StrictReturnServer;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (аудит Fable, пакет П-5, В-8, В-12, С-33, С-37): правка и аннулирование возвратов — настоящие окна (Avalonia.Headless) против СТРОГОГО поддельного сервера
/// (<see cref="StrictReturnServer"/>: 428 без If-Match / X-Confirm-Token, 400 без Idempotency-Key, 403 на чужой токен, 412 на старую версию, полный документ в ответе, повтор по ключу).
/// PIN вводится цифрами против сервера: токен выдаёт СЕРВЕР, а не тест (раньше тест сам подставлял «tok-1» и сверял заголовок с ним же). Здесь же — недостающие тесты окон возврата от клиента,
/// аннулирования (scope, версия в теле, остаток, обрыв), настоящего обрыва сокета, отмены PIN, скрытых строк длинной накладной, срока 24 ч между открытием и сохранением,
/// программы владельца (смена, ящик, печать — поддельный TCP-порт принтера) и отказа по правам.</summary>
[Collection("UiScenarios")]
public sealed class ReturnEditContractUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _wasOwner = AppMode.IsOwner;
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public ReturnEditContractUiTests()
    {
        CustomerReturnEditDialog.ResetKeysForTests();
        SupplierReturnEditDialog.ResetKeysForTests();
    }

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _wasOwner);
        PosApp.ActiveShiftId = _savedShift;
    }

    // ───────────── общие шаги ─────────────

    private static Line L(string id, string name, double qty, double price, string unit = "шт", double sold = 0, double other = 0) =>
        new() { Id = id, ProductId = "p-" + id, Name = name, Qty = qty, Price = price, Unit = unit, Sold = sold, OtherReturned = other };

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

    private static async Task Finish(Task task, int ms = 20000)
    {
        Wait(() => task.IsCompleted, ms);
        task.IsCompleted.Should().BeTrue("операция окна закончилась");
        await task;
    }

    private static async Task<T> Finish<T>(Task<T> task, int ms = 20000)
    {
        Wait(() => task.IsCompleted, ms);
        task.IsCompleted.Should().BeTrue("операция окна закончилась");
        return await task;
    }

    /// <summary>Цикл диспетчера (в Headless таймеры и автопилот срабатывают только внутри цикла), пока условие не выполнится.</summary>
    private static bool RunUntil(Func<bool> condition, int ms = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource();
            using var stop = DispatcherTimer.RunOnce(() => cts.Cancel(), TimeSpan.FromMilliseconds(80));
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return condition();
    }

    private static ManagerPinDialog? PinOf(Window dialog) => dialog.OwnedWindows.OfType<ManagerPinDialog>().FirstOrDefault(w => w.IsVisible);

    /// <summary>Дождаться окна PIN руководителя и набрать PIN цифровым блоком, как кассир (настоящий запрос проверки PIN к серверу). false — окно PIN не появилось.</summary>
    private static bool EnterPin(Window dialog, string pin = ValidPin, int ms = 8000)
    {
        Wait(() => PinOf(dialog) is not null, ms);
        if (PinOf(dialog) is not { } pd)
            return false;
        UiShot.Settle(2, 5);
        foreach (var d in pin)
            UiDrive.Click(pd, UiDrive.ButtonByText(pd, d.ToString()));
        UiDrive.Click(pd, UiDrive.ButtonByText(pd, "Подтвердить", "Ырастоо"));
        return true;
    }

    private static void Close(Window? dialog, Window owner)
    {
        if (dialog is { IsVisible: true })
            dialog.Close();
        if (owner.IsVisible)
            owner.Close();
        UiShot.Pump();
    }

    private static SupplierReturnInfo SupplierInfo(StrictReturnServer s, Doc d) => SupplierApiService.ParseReturn(JsonDocument.Parse(s.DocJson(d, replayed: false)).RootElement.Clone());

    private static (ReturnDocRef Doc, List<CustomerReturnEditLine> Lines) CustomerOpen(StrictReturnServer s, Doc d)
    {
        var card = JsonDocument.Parse(s.DocJson(d, replayed: false)).RootElement.Clone();
        var sale = JsonDocument.Parse(StrictReturnServer.SaleJsonOf(d)).RootElement.Clone();
        var total = d.Lines.Sum(l => l.Qty * l.Price);
        return (new ReturnDocRef(ReturnDocKind.Customer, d.Id, d.Number, total, ReturnEditState.Parse(card, DateTime.UtcNow)), CustomerReturnEdit.FromCards(card, sale));
    }

    private static CustomerReturnEditDialog OpenCustomer(StrictReturnServer s, Doc d, Window owner, out Task<bool> shown)
    {
        var (doc, lines) = CustomerOpen(s, d);
        var dialog = new CustomerReturnEditDialog(doc, lines, s.Edit, d.SaleId);
        shown = dialog.ShowDialog<bool>(owner);
        UiShot.Settle(4, 10);
        return dialog;
    }

    private static List<Line> Customer2Lines() => [L("si-1", "Молоко", 2, 100, sold: 5), L("si-2", "Сыр весовой", 1.5, 100, "кг", sold: 2)];

    // ───────────── 2. окно возврата от клиента: PIN, PATCH, заголовки и тело ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_Pin_Patch_HeadersAndBody_ThroughDialog()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            dialog = OpenCustomer(s, d, owner, out var shown);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue("перед сохранением — окно PIN руководителя");
            await Finish(save);
            (await Finish(shown)).Should().BeTrue("окно закрылось с результатом «изменено»");

            var pin = BodyOf(s.Pins.Should().ContainSingle().Subject);
            pin["pin"]!.ToString().Should().Be(ValidPin, "PIN набран цифрами и ушёл серверу на проверку");
            pin["scope"]!.ToString().Should().Be("return_edit");
            pin["object_type"]!.ToString().Should().Be("pos");
            pin["object_id"]!.ToString().Should().Be("pr1");
            pin["version"]!.ToString().Should().Be("3");

            var patch = s.Patches.Should().ContainSingle().Subject;
            HeaderOf(patch, "If-Match").Should().Be("\"3\"", "версия — защита от чужой правки");
            HeaderOf(patch, "X-Confirm-Token").Should().Be(s.IssuedTokens.Single(), "в заголовке — токен, выданный СЕРВЕРОМ на этот PIN");
            HeaderOf(patch, "Idempotency-Key").Should().NotBeNullOrWhiteSpace();
            var body = BodyOf(patch);
            body["version"]!.ToString().Should().Be("3");
            body["edit_reason"]!.ToString().Should().Be("ошиблись количеством");
            var items = body["items"]!.AsArray();
            items.Should().HaveCount(2);
            items[0]!["sale_item_id"]!.ToString().Should().Be("si-1");
            items[0]!["quantity"]!.ToString().Should().Be("1");
            items[1]!["sale_item_id"]!.ToString().Should().Be("si-2");
            items[1]!["quantity"]!.ToString().Should().Be("1.5", "нетронутая строка уходит с прежним количеством");

            d.AppliedEdits.Should().Be(1, "сервер применил правку один раз");
            d.Version.Should().Be(4);
            dialog.SavedState!.Version.Should().Be(4, "окно взяло новую версию из полного документа в ответе");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 3. 412: чужая правка между PIN и PATCH ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_412_Rereads_ThenSavesWithNewVersion()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var owner = Owner();
        CustomerReturnEditDialog? first = null;
        CustomerReturnEditDialog? second = null;
        try
        {
            first = OpenCustomer(s, d, owner, out var shown1);
            first.SetQtyForTests(0, "1");
            first.SetReasonForTests("ошиблись количеством");
            var save = first.SaveForTestsAsync();
            Wait(() => PinOf(first) is not null);
            d.Version = 4; // пока открыто окно PIN, возврат изменил другой сотрудник
            EnterPin(first).Should().BeTrue();
            await Finish(save);
            first.ErrorText.Should().Contain("изменён другим", "412 version_conflict — понятный текст, не сырой ответ");
            d.AppliedEdits.Should().Be(0, "ничего не сохранено");
            s.Patches.Should().ContainSingle().Which.Should().NotBeNull();
            first.Close(false);
            await Finish(shown1);
            UiShot.Pump();

            // Перечитали документ (новое окно по свежей карточке) — теперь правка уходит с НОВОЙ версией и токеном на неё.
            second = OpenCustomer(s, d, owner, out var shown2);
            second.SetQtyForTests(0, "1");
            second.SetReasonForTests("ошиблись количеством");
            var save2 = second.SaveForTestsAsync();
            EnterPin(second).Should().BeTrue();
            await Finish(save2);
            (await Finish(shown2)).Should().BeTrue();
            s.Pins.Should().HaveCount(2);
            BodyOf(s.Pins[1])["version"]!.ToString().Should().Be("4", "токен выдан на новую версию");
            HeaderOf(s.Patches[1], "If-Match").Should().Be("\"4\"");
            d.AppliedEdits.Should().Be(1);
            d.Version.Should().Be(5);
        }
        finally
        {
            Close(first, owner);
            Close(second, owner);
        }
    }

    // ───────────── 4. двойной клик «Сохранить» ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_DoubleClickSave_OnePatch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        s.GetDelayMs = 300; // первое нажатие ещё ждёт сервер, когда придёт второе
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            dialog = OpenCustomer(s, d, owner, out var shown);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = UiDrive.ButtonByText(dialog, "Сохранить");
            UiDrive.Click(dialog, save);
            UiDrive.Click(dialog, save);
            var extra = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(extra);
            (await Finish(shown)).Should().BeTrue();
            s.Pins.Should().HaveCount(1, "один PIN на одну правку");
            s.Patches.Should().HaveCount(1, "двойной клик не отправляет правку дважды");
            d.AppliedEdits.Should().Be(1);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 5. 409: по чеку появился другой возврат ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_ExceedsReturnable_OtherReturnAppeared_Shows409Text()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        s.Override = (_, isVoid) => isVoid ? null : new ResponseMessage
        {
            StatusCode = 409,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = "{\"code\":\"exceeds_returnable\",\"product_id\":\"p-si-1\",\"max_qty\":\"3\"}" },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            dialog = OpenCustomer(s, d, owner, out var shown);
            dialog.SetQtyForTests(0, "5"); // по чеку максимум 5 (продано 5, другими не возвращено) — окно пропускает
            dialog.SetReasonForTests("добавили товар");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("больше, чем можно вернуть", "409 exceeds_returnable — понятный текст");
            dialog.ErrorText.Should().MatchRegex("3", "в тексте — сколько можно вернуть (max_qty из ответа сервера)");
            dialog.ErrorText.Should().Contain("Молоко", "товар назван по id из ответа сервера (product_id → название строки окна)");
            dialog.SaveEnabledForTests.Should().BeTrue("отказ по существу — окно не заблокировано, количество можно уменьшить и повторить");
            d.AppliedEdits.Should().Be(0);
            d.Version.Should().Be(3);
            shown.IsCompleted.Should().BeFalse("окно осталось открытым");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 6. чек в долг: правка не поддерживается ─────────────

    [AvaloniaFact]
    public async Task CustomerEdit_DebtSale_NotSupported_ShownInWindow()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        s.Override = (_, isVoid) => isVoid ? null : new ResponseMessage
        {
            StatusCode = 403,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = "{\"code\":\"debt_sale_not_supported\",\"detail\":\"debt sale\"}" },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            dialog = OpenCustomer(s, d, owner, out _);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain(ReturnEditErrors.BlockReasonText("debt_sale_not_supported")!, "причина — в самом окне правки, понятными словами, без кода и сырого ответа");
            dialog.ErrorText.Should().NotContain("{\"");
            dialog.SaveEnabledForTests.Should().BeFalse("сервер запретил по существу — повторять бессмысленно");
            d.AppliedEdits.Should().Be(0);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── автопилот модальных окон аннулирования ─────────────

    private static IEnumerable<Window> Walk(IEnumerable<Window> roots)
    {
        foreach (var w in roots)
        {
            yield return w;
            foreach (var child in Walk(w.OwnedWindows))
                yield return child;
        }
    }

    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Отвечает на окна аннулирования: подтверждение — «Удалить», причина — текст, PIN — цифрами против сервера, сообщения — «Понятно» (тексты — в <see cref="Alerts"/>).</summary>
    private sealed class Autopilot : IDisposable
    {
        private readonly Window[] _roots;
        private readonly Dictionary<Window, int> _seen = new();
        private readonly HashSet<Window> _handled = new();
        private readonly DispatcherTimer _timer;
        public List<string> Alerts { get; } = new();
        public List<string> Questions { get; } = new();
        public string Reason { get; set; } = "дубль возврата";
        public bool EnterPinDigits { get; set; } = true;

        public Autopilot(params Window[] roots)
        {
            _roots = roots;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            foreach (var w in Walk(_roots).ToList())
            {
                if (!w.IsVisible || _handled.Contains(w))
                    continue;
                _seen[w] = _seen.GetValueOrDefault(w) + 1;
                if (_seen[w] < 3)
                    continue; // дать окну построить раскладку (кнопки PIN нажимаются мышью по координатам окна)
                switch (w)
                {
                    case PosConfirmDialog confirm:
                        _handled.Add(w);
                        Questions.Add(UiDrive.AllText(confirm));
                        Press(confirm.FindControl<Button>("ConfirmButton")!);
                        break;
                    case SimpleInputDialog input:
                        _handled.Add(w);
                        input.GetVisualDescendants().OfType<TextBox>().First().Text = Reason;
                        Press(UiDrive.ButtonByText(input, "Далее", "Андан ары"));
                        break;
                    case PosAlertDialog alert:
                        _handled.Add(w);
                        Alerts.Add(UiDrive.AllText(alert));
                        Press(alert.FindControl<Button>("OkButton")!);
                        break;
                    case ManagerPinDialog pin when EnterPinDigits:
                        _handled.Add(w);
                        foreach (var d in ValidPin)
                            UiDrive.Click(pin, UiDrive.ButtonByText(pin, d.ToString()));
                        UiDrive.Click(pin, UiDrive.ButtonByText(pin, "Подтвердить", "Ырастоо"));
                        break;
                }
            }
        }

        public void Dispose()
        {
            RunUntil(() => false, 1500);
            _timer.Stop();
        }
    }

    private static ReturnDocRef VoidDoc(StrictReturnServer s, Doc d) => d.Customer
        ? CustomerOpen(s, d).Doc
        : new ReturnDocRef(ReturnDocKind.Supplier, d.Id, d.Number, d.Lines.Sum(l => l.Qty * l.Price), ReturnEditState.Parse(JsonDocument.Parse(s.DocJson(d, false)).RootElement.Clone(), DateTime.UtcNow));

    // ───────────── 7. аннулирование: scope и версия в теле ─────────────

    [AvaloniaFact]
    public async Task Void_Pin_SendsScopeReturnVoid_AndVersionInBody()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var owner = Owner();
        using var pilot = new Autopilot(owner);
        try
        {
            var task = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, d), s.Edit);
            RunUntil(() => task.IsCompleted).Should().BeTrue(string.Join("\n---\n", pilot.Questions.Concat(pilot.Alerts)));
            (await task).Should().BeTrue("возврат аннулирован");

            var pin = BodyOf(s.Pins.Should().ContainSingle().Subject);
            pin["scope"]!.ToString().Should().Be("return_void", "PIN подтверждает именно аннулирование");
            pin["object_type"]!.ToString().Should().Be("pos");
            pin["object_id"]!.ToString().Should().Be("pr1");
            pin["version"]!.ToString().Should().Be("3");

            var post = s.Voids.Should().ContainSingle().Subject;
            HeaderOf(post, "If-Match").Should().Be("\"3\"");
            HeaderOf(post, "X-Confirm-Token").Should().Be(s.IssuedTokens.Single(), "токен выдан сервером");
            HeaderOf(post, "Idempotency-Key").Should().NotBeNullOrWhiteSpace();
            var body = BodyOf(post);
            body["version"]!.ToString().Should().Be("3", "версия — и в заголовке, и в теле");
            body["reason"]!.ToString().Should().Be("дубль возврата");
            d.Voided.Should().BeTrue();
            d.AppliedVoids.Should().Be(1);
            pilot.Alerts.Should().Contain(a => a.Contains("аннулирован"));
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 8. аннулирование: товар уже продан (отрицательный остаток) ─────────────

    [AvaloniaFact]
    public async Task Void_NegativeStock_AlreadySold_ShowsProductAndKeepsDocument()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        s.Override = (_, isVoid) => !isVoid ? null : new ResponseMessage
        {
            StatusCode = 409,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = "{\"code\":\"negative_stock\",\"product_id\":\"p-si-1\",\"available\":\"0.5\"}" },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };
        var owner = Owner();
        using var pilot = new Autopilot(owner);
        try
        {
            var task = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, d), s.Edit);
            RunUntil(() => task.IsCompleted).Should().BeTrue();
            (await task).Should().BeFalse("отказ по существу — список перечитывать нечего");
            var alert = pilot.Alerts.Should().ContainSingle(a => a.Contains("Не хватает остатка")).Subject;
            alert.Should().Contain("Молоко", "названо, какого товара не хватает");
            alert.Should().MatchRegex("0[.,]5", "и сколько его осталось на складе");
            d.Voided.Should().BeFalse("документ остался как был");
            d.AppliedVoids.Should().Be(0);
            d.Version.Should().Be(3);

            // Когда остаток появился (товар вернули на склад), повтор проходит: новый ключ, документ аннулирован один раз.
            s.Override = null;
            pilot.Alerts.Clear();
            var again = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, d), s.Edit);
            RunUntil(() => again.IsCompleted).Should().BeTrue();
            (await again).Should().BeTrue();
            d.AppliedVoids.Should().Be(1);
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 9. аннулирование: обрыв связи, повтор с тем же ключом ─────────────

    [AvaloniaFact]
    public async Task Void_ConnectionDrop_RetrySameKey_NoSecondVoid()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var lost = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray()); // сервер применил, ответ потерян
        var notReached = s.AddCustomer("pr2", "s2", Customer2Lines().ToArray()); // запрос не дошёл до применения
        var owner = Owner();
        using var pilot = new Autopilot(owner);
        try
        {
            // A. Ответ потерян настоящим обрывом сокета: исход неизвестен; повтор НЕ шлёт второго аннулирования — документ уже аннулирован.
            s.DropNextResponseAfterApply = true;
            var t1 = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, lost), s.Edit);
            RunUntil(() => t1.IsCompleted).Should().BeTrue();
            lost.AppliedVoids.Should().Be(1, "сервер успел применить");
            pilot.Alerts.Should().Contain(a => a.Contains("неизвестно, дошло ли аннулирование"), "честный текст при обрыве: исход неизвестен");
            pilot.Alerts.Clear();
            var t2 = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, lost), s.Edit);
            RunUntil(() => t2.IsCompleted).Should().BeTrue();
            pilot.Alerts.Should().Contain(a => a.Contains("уже аннулирован"), "прошлая попытка дошла — об этом сказано, второй раз ничего не отправлено");
            s.Voids.Count(v => v.RequestMessage.Path.Contains("/pr1/", StringComparison.Ordinal)).Should().Be(1, "второго POST void по документу нет");
            lost.AppliedVoids.Should().Be(1);

            // B. Запрос не дошёл (обрыв до применения): повтор уходит с ТЕМ ЖЕ Idempotency-Key и применяется ровно один раз.
            var dropOnce = true;
            s.Override = (doc, isVoid) =>
            {
                if (!isVoid || doc.Id != "pr2" || !dropOnce)
                    return null;
                dropOnce = false;
                return new ResponseMessage { FaultType = WireMock.ResponseBuilders.FaultType.EMPTY_RESPONSE };
            };
            pilot.Alerts.Clear();
            var t3 = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, notReached), s.Edit);
            RunUntil(() => t3.IsCompleted).Should().BeTrue();
            notReached.AppliedVoids.Should().Be(0);
            pilot.Alerts.Clear();
            var t4 = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, notReached), s.Edit);
            RunUntil(() => t4.IsCompleted).Should().BeTrue();
            notReached.AppliedVoids.Should().Be(1, "повтор применился один раз");
            var posts = s.Voids.Where(v => v.RequestMessage.Path.Contains("/pr2/", StringComparison.Ordinal)).ToList();
            posts.Should().HaveCount(2);
            HeaderOf(posts[0], "Idempotency-Key").Should().Be(HeaderOf(posts[1], "Idempotency-Key"), "тот же ключ — сервер не проведёт дважды");
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 10. возврат поставщику: настоящий обрыв сокета ─────────────

    [AvaloniaFact]
    public async Task SupplierEdit_RealSocketDropAndTimeout_SameKeyOnRetry()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddSupplier("r1", L("l1", "Молоко 1 л", 2, 150.5, sold: 0), L("l2", "Сыр весовой", 1.255, 150.5, "кг"));
        var owner = Owner();
        SupplierReturnEditDialog? dialog = null;
        try
        {
            // A. Правка применена, ответ потерян настоящим обрывом сокета (не 504 с HTML): окно говорит «неизвестно», повтор не шлёт второй правки.
            s.DropNextResponseAfterApply = true;
            dialog = new SupplierReturnEditDialog(SupplierInfo(s, d), d.Number, s.Edit);
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4, 10);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            d.AppliedEdits.Should().Be(1, "сервер применил правку, ответ не дошёл");
            dialog.ErrorText.Should().Contain("неизвестно, дошла ли правка", "обрыв сокета = исход неизвестен, а не «ничего не изменено»");
            var save2 = dialog.SaveForTestsAsync();
            await Finish(save2);
            dialog.ErrorText.Should().Contain("скорее всего", "документ уже новой версии — это наша правка, второй раз ничего не отправлено");
            s.Patches.Should().ContainSingle("повтор после обрыва не отправил вторую правку");
            d.AppliedEdits.Should().Be(1);
            dialog.Close(false);
            await Finish(shown);

            // B. Запрос оборван до применения: повтор из того же окна — с тем же Idempotency-Key, применяется один раз.
            var d2 = s.AddSupplier("r2", L("m1", "Молоко 1 л", 2, 150.5), L("m2", "Сыр весовой", 1.255, 150.5, "кг"));
            var dropOnce = true;
            s.Override = (doc, isVoid) =>
            {
                if (isVoid || doc.Id != "r2" || !dropOnce)
                    return null;
                dropOnce = false;
                return new ResponseMessage { FaultType = WireMock.ResponseBuilders.FaultType.EMPTY_RESPONSE };
            };
            var info2 = SupplierInfo(s, d2);
            dialog = new SupplierReturnEditDialog(info2, d2.Number, s.Edit);
            var shown2 = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4, 10);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save3 = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save3);
            d2.AppliedEdits.Should().Be(0);
            var save4 = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save4);
            (await Finish(shown2)).Should().BeTrue();
            d2.AppliedEdits.Should().Be(1, "повтор применился ровно один раз");
            var posts = s.Patches.Where(p => p.RequestMessage.Path.Contains("/r2/", StringComparison.Ordinal)).ToList();
            posts.Should().HaveCount(2);
            HeaderOf(posts[0], "Idempotency-Key").Should().Be(HeaderOf(posts[1], "Idempotency-Key"), "тот же состав той же версии — тот же ключ");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 11. возврат поставщику: PIN отменён ─────────────

    [AvaloniaFact]
    public async Task SupplierEdit_PinCancelled_NothingSent_SaveEnabled()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddSupplier("r1", L("l1", "Молоко 1 л", 2, 150.5), L("l2", "Сыр весовой", 1.255, 150.5, "кг"));
        var owner = Owner();
        SupplierReturnEditDialog? dialog = null;
        try
        {
            dialog = new SupplierReturnEditDialog(SupplierInfo(s, d), d.Number, s.Edit);
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4, 10);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            Wait(() => PinOf(dialog) is not null);
            var pd = PinOf(dialog)!;
            UiShot.Settle(2, 5);
            UiDrive.Click(pd, UiDrive.ButtonByText(pd, "Отмена", "Жокко чыгаруу"));
            await Finish(save);
            s.Pins.Should().BeEmpty("PIN не отправлялся");
            s.Patches.Should().BeEmpty("без PIN ничего не уходит на сервер");
            d.Version.Should().Be(3);
            dialog.SaveEnabledForTests.Should().BeTrue("«Сохранить» снова доступна");
            shown.IsCompleted.Should().BeFalse("окно правки осталось открытым");
            dialog.ErrorText.Should().BeEmpty("отмена PIN — не ошибка");
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 12. длинная накладная: поиск, «Показать ещё», правка скрытых строк ─────────────

    [AvaloniaFact]
    public async Task SupplierEdit_FindThenShowMore_EditHiddenRow_AllSentInPatch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var lines = Enumerable.Range(0, 120).Select(i => L("l" + i, $"Товар №{i + 1} " + (i % 3 == 2 ? "сыр весовой" : "молоко 1 л"), i % 3 == 2 ? 1.255 : 2, 150.5, i % 3 == 2 ? "кг" : "шт")).ToArray();
        var d = s.AddSupplier("r1", lines);
        var owner = Owner();
        SupplierReturnEditDialog? dialog = null;
        try
        {
            dialog = new SupplierReturnEditDialog(SupplierInfo(s, d), d.Number, s.Edit);
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4, 10);
            dialog.ShownRowsForTests.Should().Be(50, "показана первая страница");

            dialog.FindForTests("№90 ");                   // найти скрытую строку и поправить её
            UiShot.Pump();
            dialog.ShownRowsForTests.Should().Be(1);
            dialog.SetQtyForTests(89, "0.005");            // 0,005 кг: 0,005 × 150,50 = 0,7525 → 0,75
            dialog.FindForTests("");
            dialog.ShowMoreForTests();
            dialog.ShowMoreForTests();
            UiShot.Pump();
            dialog.ShownRowsForTests.Should().Be(120, "после двух «Показать ещё» видна вся накладная");
            dialog.SetQtyForTests(99, "2.5");              // строка со второй страницы
            dialog.SetQtyForTests(110, "0.005");           // строка с третьей страницы
            dialog.SetReasonForTests("ошиблись количеством");

            // Итог — независимый расчёт: сумма округлённых до копеек сумм строк (как считает сервер).
            decimal Round(decimal q, decimal p) => Math.Round(q * p, 2, MidpointRounding.AwayFromZero);
            var expected = 0m;
            for (var i = 0; i < 120; i++)
            {
                var q = i switch { 89 => 0.005m, 110 => 0.005m, 99 => 2.5m, _ => i % 3 == 2 ? 1.255m : 2m };
                expected += Round(q, 150.5m);
            }
            dialog.TotalText.Should().Contain(MoneyText.Som((double)expected), "сумма пересчитана по всем строкам, включая скрытые, с округлением каждой строки");

            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save, 60000);
            (await Finish(shown)).Should().BeTrue();
            var items = BodyOf(s.Patches.Should().ContainSingle().Subject)["items"]!.AsArray();
            items.Should().HaveCount(120, "в PATCH — полный состав, в том числе строки, которые были скрыты");
            items[89]!["qty"]!.ToString().Should().Be("0.005", "скрытая строка, найденная поиском, ушла с правкой");
            items[110]!["qty"]!.ToString().Should().Be("0.005", "строка третьей страницы ушла с правкой");
            items[99]!["qty"]!.ToString().Should().Be("2.5");
            items[0]!["qty"]!.ToString().Should().Be("2", "нетронутые строки — как были");
            items[2]!["qty"]!.ToString().Should().Be("1.255", "кг не округляются");
            d.Lines.Should().HaveCount(120);
            d.Lines[89].Qty.Should().Be(0.005);
        }
        finally
        {
            Close(dialog, owner);
        }
    }

    // ───────────── 13. срок 24 ч вышел между открытием окна и «Сохранить» ─────────────

    [AvaloniaFact]
    public async Task Edit_WindowExpiredBetweenOpenAndSave_ShowsServerReason()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var supplier = s.AddSupplier("r1", L("l1", "Молоко 1 л", 2, 150.5), L("l2", "Сыр весовой", 1.255, 150.5, "кг"));
        var customer = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var owner = Owner();
        SupplierReturnEditDialog? sd = null;
        CustomerReturnEditDialog? cd = null;
        try
        {
            sd = new SupplierReturnEditDialog(SupplierInfo(s, supplier), supplier.Number, s.Edit);
            _ = sd.ShowDialog<bool>(owner);
            UiShot.Settle(4, 10);
            sd.SetQtyForTests(0, "1");
            sd.SetReasonForTests("ошиблись количеством");
            supplier.Expired = true; // пока окно открыто, прошли сутки по времени сервера
            await Finish(sd.SaveForTestsAsync());
            sd.ErrorText.Should().Contain(ReturnEditErrors.BlockReasonText("edit_window_expired")!, "причина — от сервера (edit_block_reason), понятными словами");
            PinOf(sd).Should().BeNull("окно PIN не открывалось — сервер правку уже не даёт");

            cd = OpenCustomer(s, customer, owner, out _);
            cd.SetQtyForTests(0, "1");
            cd.SetReasonForTests("ошиблись количеством");
            customer.CanEdit = false;
            customer.BlockReason = "shift_closed"; // другая причина сервера — показывается именно она
            await Finish(cd.SaveForTestsAsync());
            cd.ErrorText.Should().Contain(ReturnEditErrors.BlockReasonText("shift_closed")!);
            PinOf(cd).Should().BeNull();

            s.Pins.Should().BeEmpty("ни PIN, ни PATCH не уходили");
            s.Patches.Should().BeEmpty();
            supplier.AppliedEdits.Should().Be(0);
            customer.AppliedEdits.Should().Be(0);
        }
        finally
        {
            Close(sd, owner);
            Close(cd, owner);
        }
    }

    // ───────────── 14. программа владельца: правка и аннулирование без смены, ящика и печати ─────────────

    /// <summary>Поддельный сетевой принтер чеков: TCP-порт на 127.0.0.1, считает байты. Всё, что касса отправила бы на принтер (чек, импульс ящика), попадает сюда.</summary>
    private sealed class FakePrinterPort : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private long _bytes;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public long Bytes => Interlocked.Read(ref _bytes);

        public FakePrinterPort()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            using var c = client;
                            var buf = new byte[4096];
                            var stream = c.GetStream();
                            int n;
                            while ((n = await stream.ReadAsync(buf)) > 0)
                                Interlocked.Add(ref _bytes, n);
                        });
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [AvaloniaFact]
    public async Task Owner_EditAndVoid_NoShiftNoDrawerNoPrint_PrinterEnabled()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        var saved = (prefs.ReceiptEnabled, prefs.ReceiptDevicePath, prefs.CashDrawerEnabled, prefs.CheckoutPrintReceipt);
        using var printer = new FakePrinterPort();
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var voided = s.AddCustomer("pr2", "s2", Customer2Lines().ToArray());
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            // Печать чеков и ящик ВКЛЮЧЕНЫ, порт принтера указан — это настоящая проверка «у владельца не печатает» (а не «печать выключена»).
            prefs.ReceiptEnabled = true;
            prefs.ReceiptDevicePath = "TCP:127.0.0.1:" + printer.Port;
            prefs.CashDrawerEnabled = true;
            prefs.CheckoutPrintReceipt = true;

            // Контроль чувствительности: на КАССЕ тот же порт принтера получает байты чека возврата — иначе нулевой счётчик у владельца ничего бы не доказывал.
            IsOwnerProp.SetValue(null, false);
            var kassaError = OperationReceiptPrinter.PrintReturn("№1115", [("Молоко", 1, 100m, 100m)], 100m, "тест", isWholeSale: false, "Кассир");
            kassaError.Should().BeNull("касса печатает чек возврата на включённый принтер");
            Wait(() => printer.Bytes > 0, 5000);
            printer.Bytes.Should().BeGreaterThan(0, "контроль: порт принимает байты чека");
            var afterControl = printer.Bytes;

            // Программа владельца: смены нет.
            IsOwnerProp.SetValue(null, true);
            PosApp.ActiveShiftId = null;
            dialog = OpenCustomer(s, d, owner, out var shown);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            (await Finish(shown)).Should().BeTrue("правка у владельца прошла");
            d.AppliedEdits.Should().Be(1);

            using (var pilot = new Autopilot(owner))
            {
                var task = ReturnEditFlows.VoidAsync(owner, VoidDoc(s, voided), s.Edit);
                RunUntil(() => task.IsCompleted).Should().BeTrue();
                (await task).Should().BeTrue();
                voided.AppliedVoids.Should().Be(1, "аннулирование у владельца прошло");
            }
            RunUntil(() => false, 1500); // фоновые дела (ящик, печать) успели бы дойти до порта

            printer.Bytes.Should().Be(afterControl, "у владельца на принтер не ушло ни одного байта: ни чека, ни импульса ящика");
            var paths = s.Server.LogEntries.Select(e => e.RequestMessage.Path).ToList();
            paths.Should().NotContain(p => p.Contains("shift", StringComparison.OrdinalIgnoreCase), "программа владельца не открывает и не закрывает смену и не пишет в неё");
            PosApp.ActiveShiftId.Should().BeNull();
        }
        finally
        {
            (prefs.ReceiptEnabled, prefs.ReceiptDevicePath, prefs.CashDrawerEnabled, prefs.CheckoutPrintReceipt) = saved;
            Close(dialog, owner);
        }
    }

    // ───────────── 15. программа владельца: нет права — 403 в окне, кнопки скрыты ─────────────

    [AvaloniaFact]
    public async Task Owner_NoPermission_403_ShownAndButtonsHidden()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await using var s = await StrictReturnServer.StartAsync();
        var d = s.AddCustomer("pr1", "s1", Customer2Lines().ToArray());
        var owner = Owner();
        CustomerReturnEditDialog? dialog = null;
        try
        {
            IsOwnerProp.SetValue(null, true);
            PosApp.ActiveShiftId = null;

            // Сервер отказал в подтверждении PIN правом (403): понятный текст в окне правки, правка не уходит, повтор бессмыслен.
            s.PinForbidden = true;
            dialog = OpenCustomer(s, d, owner, out _);
            dialog.SetQtyForTests(0, "1");
            dialog.SetReasonForTests("ошиблись количеством");
            var save = dialog.SaveForTestsAsync();
            EnterPin(dialog).Should().BeTrue();
            await Finish(save);
            dialog.ErrorText.Should().Contain("Нет прав на это действие", "403 — словами, а не кодом и не сырым ответом");
            dialog.ErrorText.Should().NotContain("{\"");
            dialog.SaveEnabledForTests.Should().BeFalse("права от повтора не появятся");
            s.Patches.Should().BeEmpty("без токена правка не отправляется");
            d.AppliedEdits.Should().Be(0);

            // Сервер сказал, что прав нет (can_edit/can_void = false, edit_block_reason = permission_denied): кнопок «Редактировать» и «Удалить» нет, причина видна.
            var denied = new ReturnDocRef(ReturnDocKind.Customer, "pr9", "№77", 120, ReturnEditState.Parse(JsonDocument.Parse(
                "{\"version\":2,\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"permission_denied\"}").RootElement.Clone()));
            var host = new Window { Width = 640, Height = 300 };
            var panel = new StackPanel();
            host.Content = panel;
            panel.Children.Add(ReturnEditFlows.CustomerBar(panel, denied, () => Task.CompletedTask, s.Edit));
            using (UiShot.Render(host, new UiSize(640, 300)))
            {
                UiShot.Settle(4);
                var texts = host.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
                texts.Should().NotContain(new[] { "Редактировать", "Удалить" }, "нет права — нет и кнопок");
                UiDrive.AllText(host).Should().Contain("Нет прав на это действие", "вместо кнопок — причина");
            }
            host.Close();
            UiShot.Pump();
        }
        finally
        {
            Close(dialog, owner);
        }
    }
}
