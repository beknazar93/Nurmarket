using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-13 (пакет Н-3, «весы простыми словами»): что нужно блоку «Быстрая настройка» от окна настроек весов.
/// Блок общий для всех марок (Rongta, TM-30F, Штрих-ПРИНТ, Штрих РС-200; подойдёт и новым — CAS, Масса-К): окно даёт только поле
/// адреса (или папки) и свои действия, а три шага, подписи и вид одинаковы.</summary>
internal sealed class ScaleQuickOptions
{
    /// <summary>Марка весов (ScaleUi.Brand…): по ней «Проверить связь» (<see cref="ScaleUi.CheckConnectionAsync"/>) и строка последней проверки.</summary>
    public required string Brand { get; init; }
    /// <summary>Подпись поля шага 1: «Адрес весов (IP)» или «Папка весов».</summary>
    public required string WhereLabel { get; init; }
    /// <summary>Поле адреса или папки — его создаёт и читает само окно.</summary>
    public required TextBox WhereBox { get; init; }
    /// <summary>Сохранить адрес из поля в настройки (вызывается перед проверкой связи и отправкой товаров).</summary>
    public required Action Save { get; init; }
    /// <summary>Кнопка «Найти» рядом с полем (поиск весов в сети, выбор папки); null — кнопки нет.</summary>
    public Func<Task>? Find { get; init; }
    /// <summary>Подпись кнопки «Найти»; null — «Найти весы…».</summary>
    public string? FindText { get; init; }
    /// <summary>Своя проверка связи; null — общая по марке (та же, что в Настройки → Весы). На весах ничего не меняет.</summary>
    public Func<Task<(bool Ok, string Message)>>? Check { get; init; }
    /// <summary>«Отправить товары…» — открыть окно «Весы» этой марки (сама отправка и её подтверждения — там).</summary>
    public required Action SendGoods { get; init; }
    /// <summary>«Проверить этикетку» — показать вкладку «Штрих-код» (пример этикетки и как её прочтёт касса). На весы ничего не шлёт.</summary>
    public required Action CheckLabel { get; init; }
    /// <summary>Строка «ответ весов» внизу окна — туда дублируется итог проверки.</summary>
    public Action<string, bool>? ShowResult { get; init; }
}

/// <summary>2026-11-13 (Н-3): собранный блок «Быстрая настройка» — корень для вкладки и обновление строки состояния связи.</summary>
internal sealed class ScaleQuickView
{
    public required StackPanel Root { get; init; }
    public required Button CheckButton { get; init; }
    public required Button SendButton { get; init; }
    public required Button LabelButton { get; init; }
    internal TextBlock Status { get; init; } = null!;
    internal TextBlock Detail { get; init; } = null!;
    internal Control Scope { get; init; } = null!;
    internal string Brand { get; init; } = "";

    /// <summary>Крупная строка шага 2: ok = null — «ещё не проверялась» (обычным цветом), иначе «✓ Весы на связи» / «⚠ Нет связи» и подробность.</summary>
    public void SetStatus(bool? ok, string detail)
    {
        Status.Text = ok switch
        {
            true => "✓ " + ScaleUi.L("Весы на связи", "Тараза байланышта", "The scale is connected", "Tartı bağlı", "Tarozi aloqada"),
            false => "⚠ " + ScaleUi.L("Нет связи с весами", "Тараза менен байланыш жок", "No connection to the scale", "Tartıyla bağlantı yok", "Tarozi bilan aloqa yo‘q"),
            _ => ScaleUi.L("Связь ещё не проверялась", "Байланыш азырынча текшерилген жок", "Connection not checked yet", "Bağlantı henüz kontrol edilmedi", "Aloqa hali tekshirilmagan"),
        };
        ScaleUi.Themed(Scope, Status, TextBlock.ForegroundProperty, ok switch { true => "BrushSuccess", false => "BrushWarning", _ => "BrushText" },
            ok switch { true => Brushes.Green, false => Brushes.DarkOrange, _ => Brushes.Black });
        Detail.Text = detail;
        Detail.IsVisible = !string.IsNullOrWhiteSpace(detail);
    }

    /// <summary>Показать последнюю проверку связи этой марки (если адрес с тех пор не меняли).</summary>
    public void RefreshFromLastCheck()
    {
        var last = ScaleUi.LastCheckOf(Brand);
        SetStatus(last?.Ok, last?.Message ?? "");
    }
}

internal static partial class ScaleUi
{
    /// <summary>2026-11-13 (Н-3): подпись первой вкладки окон настроек весов.</summary>
    public static string QuickTabTitle => L("Быстрая настройка", "Тез жөндөө", "Quick setup", "Hızlı kurulum", "Tez sozlash");

    /// <summary>2026-11-13 (Н-3): подпись последней вкладки — порты, протоколы, файлы обмена и прочее техническое.</summary>
    public static string MasterTabTitle => L("Для мастера", "Уста үчүн", "For the technician", "Teknisyen için", "Usta uchun");

    /// <summary>2026-11-13 (Д-7): размер шрифта из общей шкалы (NurFontBody, NurFontSubtitle, NurFontTitle…) привязкой — без чисел в коде окна.</summary>
    public static T FontFromScale<T>(T target, string key) where T : TextBlock
    {
        target.Bind(TextBlock.FontSizeProperty, target.GetResourceObservable(key, v => v is double d ? d : 14d));
        return target;
    }

    /// <summary>2026-11-13 (Н-3): вступление «Пройдите три шага ниже» видно только на первой вкладке («Быстрая настройка») —
    /// на остальных оно лишнее и отнимает строку у содержимого (1024×600). Строка шапки «Весы: адрес» (<paramref name="header"/>) —
    /// наоборот: на первой вкладке адрес и так виден в шаге 1, поэтому она показывается только на остальных.</summary>
    public static void IntroOnFirstTab(TabControl tabs, Control intro, Control? header = null)
    {
        void Apply()
        {
            intro.IsVisible = tabs.SelectedIndex <= 0;
            if (header is not null)
                header.IsVisible = tabs.SelectedIndex > 0;
        }
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) Apply(); };
        Apply();
    }

    /// <summary>2026-11-13 (Н-3, просьба владельца «простой режим весов в 3 шага»): первая вкладка окна настроек весов для продавца —
    /// 1) какие весы и где (адрес или папка, кнопка «Найти»), 2) «Проверить связь» с крупным итогом, 3) «Отправить товары…» (единственная
    /// акцентная кнопка) и «Проверить этикетку». Без слов «порт», «TCP», «.txp»: всё техническое окно держит во вкладке «Для мастера».
    /// Обмен с весами блок не трогает — только вызывает действия окна.</summary>
    public static ScaleQuickView QuickSetup(Control scope, ScaleQuickOptions o)
    {
        var root = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 8, 6) }; // плотнее обычных вкладок: три шага целиком на 1024×600

        // ---- 1. Какие весы и где
        var where = Card(L("1. Какие весы и где они", "1. Кайсы тараза жана ал кайда", "1. Which scale and where it is", "1. Hangi tartı ve nerede", "1. Qaysi tarozi va u qayerda"), null, out var whereBody);
        var whereGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,Auto") };
        var brandLabel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        brandLabel.Children.Add(Themed(scope, FontFromScale(new TextBlock { Text = LabelBrandTitle(o.Brand), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap }, "NurFontSubtitle"),
            TextBlock.ForegroundProperty, "BrushText", Brushes.Black));
        brandLabel.Children.Add(new TextBlock { Text = o.WhereLabel, Classes = { "label" } });
        whereGrid.Children.Add(brandLabel);
        o.WhereBox.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(o.WhereBox, 1);
        whereGrid.Children.Add(o.WhereBox);
        if (o.Find is { } find)
        {
            var findButton = MakeButton(o.FindText ?? L("Найти весы…", "Таразаны табуу…", "Find the scale…", "Tartıyı bul…", "Tarozini topish…"), false, async (_, _) => await find().ConfigureAwait(true));
            findButton.Margin = new Thickness(8, 0, 0, 0);
            findButton.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(findButton, 2);
            whereGrid.Children.Add(findButton);
        }
        whereBody.Children.Add(whereGrid);
        root.Children.Add(where);

        // ---- 2. Проверить связь
        var check = Card(L("2. Проверьте связь", "2. Байланышты текшериңиз", "2. Check the connection", "2. Bağlantıyı kontrol edin", "2. Aloqani tekshiring"), null, out var checkBody);
        var status = FontFromScale(new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, "NurFontSubtitle");
        var detail = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var checkButton = MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), false, (_, _) => { });
        checkButton.VerticalAlignment = VerticalAlignment.Top;
        var statusPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        statusPanel.Children.Add(status);
        statusPanel.Children.Add(detail);
        var checkGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        checkGrid.Children.Add(checkButton);
        Grid.SetColumn(statusPanel, 1);
        checkGrid.Children.Add(statusPanel);
        checkBody.Children.Add(checkGrid);
        root.Children.Add(check);

        // ---- 3. Отправить товары и проверить этикетку
        var send = Card(L("3. Отправьте товары и проверьте этикетку", "3. Товарларды жөнөтүп, этикетканы текшериңиз", "3. Send the goods and check a label", "3. Ürünleri gönderin ve etiketi kontrol edin", "3. Tovarlarni yuboring va yorliqni tekshiring"), null, out var sendBody);
        var sendButton = MakeButton(L("Отправить товары…", "Товарларды жөнөтүү…", "Send goods…", "Ürünleri gönder…", "Tovarlarni yuborish…"), true, (_, _) => { o.Save(); o.SendGoods(); });
        var labelButton = MakeButton(L("Проверить этикетку", "Этикетканы текшерүү", "Check a label", "Etiketi kontrol et", "Yorliqni tekshirish"), false, (_, _) => o.CheckLabel());
        sendBody.Children.Add(ButtonRow(sendButton, labelButton));
        sendBody.Children.Add(Text(L(
            "Что проверить: взвесьте товар, напечатайте этикетку на весах и отсканируйте её на кассе — название и сумма должны совпасть.",
            "Эмнени текшерүү керек: товарды таразага тартып, этикетканы басып чыгарып, кассада сканерлеңиз — аталышы менен суммасы дал келиши керек.",
            "What to check: weigh an item, print a label on the scale and scan it at the till — the name and the amount must match.",
            "Neyi kontrol etmeli: ürünü tartın, tartıda etiket basın ve kasada okutun — ad ve tutar aynı olmalı.",
            "Nimani tekshirish kerak: tovarni torting, tarozida yorliq chop eting va kassada skanerlang — nomi va summasi mos kelishi kerak.")));
        root.Children.Add(send);

        var view = new ScaleQuickView
        {
            Root = root, CheckButton = checkButton, SendButton = sendButton, LabelButton = labelButton,
            Status = status, Detail = detail, Scope = scope, Brand = o.Brand,
        };
        view.RefreshFromLastCheck();

        checkButton.Click += async (_, _) =>
        {
            o.Save();
            checkButton.IsEnabled = false;
            var checking = L("Проверка связи…", "Байланыш текшерилүүдө…", "Checking the connection…", "Bağlantı kontrol ediliyor…", "Aloqa tekshirilmoqda…");
            o.ShowResult?.Invoke(checking, false);
            try
            {
                var (ok, message) = o.Check is { } own ? await own().ConfigureAwait(true) : await CheckConnectionAsync(o.Brand).ConfigureAwait(true);
                view.SetStatus(ok, message);
                o.ShowResult?.Invoke(message, !ok);
            }
            catch (Exception ex)
            {
                NurMarketKassa.Services.PosLogger.Log($"Весы ({o.Brand}): быстрая проверка связи: {ex}", "SCALES");
                view.SetStatus(false, ex.Message);
                o.ShowResult?.Invoke(ex.Message, true);
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };
        return view;
    }
}
