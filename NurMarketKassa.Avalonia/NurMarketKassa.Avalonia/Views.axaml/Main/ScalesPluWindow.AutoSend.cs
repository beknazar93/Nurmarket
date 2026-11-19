using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (решение владельца по сравнению с Paloma365): автоотправка изменённых товаров на весы. Здесь две части.
/// 1) Галочка «Отправлять изменения на весы автоматически» и подпись «последняя автоотправка» в нижней полосе окна.
/// 2) Тихий запуск того же кода отправки без экрана: <see cref="RunAutoSendAsync"/> создаёт НЕ показанный экземпляр окна «Весы» (его строки, закреплённые номера,
/// проверки и отправка по маркам — те же, что у кнопки «Отправить на весы»), отмечает в нём только изменённые товары и вызывает отправку. Флаг <see cref="_silent"/>
/// отключает всё, что спросило бы кассира: окна подтверждения, перезапись непринятого файла РС-200, частичный отказ из-за одной ошибочной строки,
/// запись клавиш весов (при отправке части списка она стёрла бы раскладку). Правила (склейка, повтор, кому можно) — ScaleAutoSend.cs, цикл — ScaleAutoSendService.</summary>
public partial class ScalesPluWindow
{
    /// <summary>Окно создано автоотправкой и не показывается: диалоги не открываем.</summary>
    private bool _silent;

    private static readonly TimeSpan AutoSendMaxTime = TimeSpan.FromMinutes(10);

    /// <summary>Тихая отправка изменённых товаров на весы <paramref name="profile"/>. Выбор весов на время отправки переключается и возвращается.</summary>
    public static async Task<ScaleAutoSendResult> RunAutoSendAsync(LabelScaleProfile profile, IReadOnlyList<string> ids)
    {
        var previousId = LabelScaleStore.Active.Id;
        var switched = !string.Equals(previousId, profile.Id, StringComparison.Ordinal);
        ScalesPluWindow? window = null;
        try
        {
            if (switched)
                LabelScaleStore.Activate(profile);
            window = new ScalesPluWindow { _silent = true };
            var work = window.AutoSendCoreAsync(ids);
            if (await Task.WhenAny(work, Task.Delay(AutoSendMaxTime)).ConfigureAwait(true) != work)
            {
                window._tmCts?.Cancel();
                return new ScaleAutoSendResult(false, 0, Tr.T("весы не ответили за 10 минут", "тараза 10 мүнөттө жооп берген жок", "the scale did not answer in 10 minutes", "tartı 10 dakikada yanıt vermedi", "tarozi 10 daqiqada javob bermadi"), true);
            }
            return await work.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автоотправка на весы: сбой: {ex}", "SCALES");
            return new ScaleAutoSendResult(false, 0, ex.Message, true);
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception) { /* окно не показывалось — закрывать нечего */ }
            if (switched && LabelScaleStore.Find(previousId) is { } previous)
                LabelScaleStore.Activate(previous);
        }
    }

    private async Task<ScaleAutoSendResult> AutoSendCoreAsync(IReadOnlyList<string> ids)
    {
        _brand = ScaleUi.NormalizeBrand(UserPreferences.Instance.ScaleBrand);
        InitKeySheetControls();
        FillProfileCombo();
        ApplyBrandVisibility();
        LoadRows();

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var row in _allRows)
            row.IsSelected = wanted.Contains(row.Id);

        // Товары с ошибкой (нет PLU, цена 0, цена выше поля весов…) не отправляем и называем в журнале; остальные уходят.
        var issues = BuildPreflightIssues();
        var skipped = issues.Where(i => i.Blocks).Select(i => i.ProductId!).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (skipped.Count > 0)
        {
            PosLogger.Log("Автоотправка на весы: пропущены товары с ошибками: "
                          + string.Join("; ", issues.Where(i => i.Blocks).Take(10).Select(i => $"«{i.Name}» — {ScalePreflight.Title(i.Kind)}")), "SCALES");
            foreach (var row in _allRows.Where(r => skipped.Contains(r.Id)))
                row.IsSelected = false;
        }

        var selected = _allRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
            return new ScaleAutoSendResult(true, 0, null, false); // всё с ошибками — повторять нечего, ждём правки каталога

        ClearRowStatuses();
        if (KassaNumbering && !OfflineModeHelper.UseLocalOperations)
            await SyncWebPluAsync(ask: false).ConfigureAwait(true);

        if (IsShtrikhPc)
            await SendToShtrikhPcAsync().ConfigureAwait(true);
        else if (IsDahuaWire)
            await SendToTmAsync().ConfigureAwait(true);
        else if (_brand == BrandShtrikh && ShtrikhDirect)
            await SendToShtrikhAsync().ConfigureAwait(true);
        else if (IsMassaK)
            await SendToMassaKAsync(dryRun: false).ConfigureAwait(true); // только при UserPreferences.MassaKVerified (ScaleAutoSendPolicy)
        else
            return new ScaleAutoSendResult(false, 0, "—", false); // способ отправки без прямой записи — сюда не доходит (ScaleAutoSendPolicy)

        var sent = selected.Count(r => r.StatusOk);
        if (sent == selected.Count)
            return new ScaleAutoSendResult(true, sent, null, false);
        var reason = (StatusText.Text ?? "").Trim();
        if (reason.Length > 120)
            reason = reason[..120] + "…";
        return new ScaleAutoSendResult(false, sent, reason.Length == 0 ? "—" : reason, true);
    }

    // ------------------------------------------------------------------ галочка и подпись в окне

    /// <summary>Подписи и состояние галочки под выбранные весы. Вызывается при открытии окна, смене весов и способа отправки.</summary>
    private void UpdateAutoSendControls()
    {
        if (AutoSendCheck is null || _silent)
            return;
        var profile = LabelScaleStore.Active;
        var supported = ScaleAutoSendService.Supported(profile);
        AutoSendCheck.Content = Tr.T("Отправлять изменения на весы автоматически", "Өзгөрүүлөрдү таразага автоматтык жөнөтүү", "Send changes to the scale automatically", "Değişiklikleri tartıya otomatik gönder", "O‘zgarishlarni taroziga avtomatik yuborish");
        ToolTip.SetTip(AutoSendCheck, Tr.T(
            "Цена, название или новый весовой товар — через 1–2 минуты уйдут на весы сами, тихо, только изменённые. Работает на этом компьютере: включайте на одном компьютере для одних весов.",
            "Баа, аталыш же жаңы салмактуу товар 1–2 мүнөттөн кийин таразага өзү, тынчыраак, өзгөргөндөрү гана жөнөтүлөт. Бул компьютерде иштейт: бир таразага бир компьютерде күйгүзүңүз.",
            "A price, a name or a new weighed product goes to the scale by itself in 1–2 minutes, quietly, changed ones only. Works on this computer: turn it on on one computer for one scale.",
            "Fiyat, ad veya yeni tartılı ürün 1–2 dakika içinde kendiliğinden, sessizce, yalnızca değişenler tartıya gider. Bu bilgisayarda çalışır: bir tartı için tek bilgisayarda açın.",
            "Narx, nom yoki yangi vaznli tovar 1–2 daqiqada o‘zi, jimgina, faqat o‘zgarganlari taroziga ketadi. Shu kompyuterda ishlaydi: bitta tarozi uchun bitta kompyuterda yoqing."));
        AutoSendCheck.IsEnabled = supported;
        AutoSendCheck.IsChecked = supported && profile.AutoSend;
        AutoSendInfoText.Text = !supported
            ? Tr.T("Доступно при прямой отправке: Rongta «Напрямую по сети», Штрих-ПРИНТ «Напрямую по сети», TM-30F, Штрих РС-200.",
                   "Түз жөнөтүүдө жеткиликтүү: Rongta «Тармак аркылуу түз», Штрих-ПРИНТ «Тармак аркылуу түз», TM-30F, Штрих РС-200.",
                   "Available with direct sending: Rongta “Directly over the network”, Shtrih-PRINT “Directly over the network”, TM-30F, Shtrih RS-200.",
                   "Doğrudan gönderimde kullanılabilir: Rongta «Doğrudan ağ üzerinden», Shtrih-PRINT «Doğrudan ağ üzerinden», TM-30F, Shtrih RS-200.",
                   "To‘g‘ridan-to‘g‘ri yuborishda mavjud: Rongta «To‘g‘ridan-to‘g‘ri tarmoq orqali», Shtrix-PRINT «To‘g‘ridan-to‘g‘ri tarmoq orqali», TM-30F, Shtrix RS-200.")
            : profile.AutoSend && profile.PluNumbers.Count == 0 && profile.SentPlus.Count == 0
                ? Tr.T("Включится после первой отправки на весы кнопкой.", "Таразага баскыч менен биринчи жөнөткөндөн кийин күйөт.", "Starts after the first send to the scale by the button.", "Tartıya düğmeyle ilk gönderimden sonra başlar.", "Taroziga tugma bilan birinchi yuborishdan keyin yoqiladi.")
                : ScaleAutoSendText.Last(profile.AutoSendAt, profile.AutoSendCount, profile.AutoSendError, profile.AutoSendRetryAt);
    }

    private void AutoSendCheck_Click(object? sender, RoutedEventArgs e)
    {
        var profile = LabelScaleStore.Active;
        profile.AutoSend = AutoSendCheck.IsChecked == true && ScaleAutoSendService.Supported(profile);
        if (profile.AutoSend)
            ScaleAutoSendService.BaselineSignatures(profile); // что уже записано на весы, считается актуальным — первая автоотправка не шлёт весь список
        LabelScaleStore.Save();
        PosLogger.Log($"Весы «{profile.Name}»: автоотправка изменений на этом компьютере " + (profile.AutoSend ? "включена." : "выключена."), "SCALES");
        UpdateAutoSendControls();
    }

    // ------------------------------------------------------------------ «Как подключить / памятка»

    /// <summary>Идентификаторы памяток кассира в базе знаний (раздел «Весы», группа «Памятка кассира»). Памятки CAS и Масса-К добавляют их исполнители
    /// по тому же образцу (весы-памятка-cas, весы-памятка-massa-k); пока статьи нет, открывается общая «Как отправить товары на весы».</summary>
    private static string MemoArticleId(string brand) => brand switch
    {
        BrandRongta => "весы-памятка-rongta",
        BrandShtrikh => "весы-памятка-штрих-принт",
        BrandShtrikhPc => "весы-памятка-штрих-рс200",
        BrandTm => "весы-памятка-tm-30f",
        BrandAi => "весы-памятка-ai-весы",
        "massak" => "весы-памятка-massa-k",
        "cas" => "весы-памятка-cas",
        _ => "весы-как-отправить-товары-на-весы-окно-весы",
    };

    private const string GeneralSendArticleId = "весы-как-отправить-товары-на-весы-окно-весы";

    private void InitGuideMenu()
    {
        GuideButton.Content = Tr.T("Как подключить / памятка ▾", "Кантип туташтыруу / эскертме ▾", "How to connect / cheat sheet ▾", "Nasıl bağlanır / not ▾", "Qanday ulash / eslatma ▾");
        var steps = new MenuItem { Header = Tr.T("Пошаговая инструкция с анимацией", "Анимациялуу кадам-кадам нускама", "Step-by-step guide with animation", "Animasyonlu adım adım kılavuz", "Animatsiyali bosqichma-bosqich yo‘riqnoma") };
        steps.Click += async (_, _) => await new ScaleGuideWindow(_brand).ShowDialog(this).ConfigureAwait(true);
        var memo = new MenuItem { Header = Tr.T("Памятка кассира (6–8 шагов)", "Кассир эскертмеси (6–8 кадам)", "Cashier cheat sheet (6–8 steps)", "Kasiyer notu (6–8 adım)", "Kassir eslatmasi (6–8 qadam)") };
        memo.Click += async (_, _) =>
        {
            var kb = App.GetRequiredService<KnowledgeBaseWindow>();
            kb.OpenArticle(MemoArticleId(_brand), GeneralSendArticleId);
            await kb.ShowDialog(this).ConfigureAwait(true);
        };
        GuideButton.Flyout = new MenuFlyout { Items = { steps, memo } };
    }
}
