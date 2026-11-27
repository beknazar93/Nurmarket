using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Решение кассира при конфликте версий квитанции.</summary>
public enum RepairConflictChoice
{
    /// <summary>Решить позже (окно закрыто): квитанция остаётся с пометкой «конфликт», кнопки есть в карточке.</summary>
    Later,
    /// <summary>«Применить мои правки поверх»: отправить свои данные заново.</summary>
    Mine,
    /// <summary>«Взять с сервера»: свои неотправленные правки отбросить.</summary>
    Server,
}

/// <summary>2026-11-26 (Умар, этап 2, stale_version): «Квитанцию изменили на другом устройстве». Пока кассир правил квитанцию, на сервере её уже поменяли (другая касса, программа владельца, мобилка).
/// Окно показывает, чем отличаются данные (у вас / на сервере) и даёт выбрать: «Взять с сервера» или «Применить мои правки поверх»; ничего не теряется без подтверждения.
/// Раскладка — по правилам окон (docs/Отчеты/Правила интерфейса — чек-лист новых окон): подпись отдельной строкой над значением, текст переносится, кнопки в WrapPanel, окно прокручивается.</summary>
public sealed class RepairConflictDialog : Window
{
    public RepairConflictChoice Choice { get; private set; } = RepairConflictChoice.Later;

    /// <summary>Чем данные кассы отличаются от серверных: (поле, у вас, на сервере). Предоплату не сравниваем — это деньги кассы, сервер их не двигает.</summary>
    public static List<(string Label, string Mine, string Server)> Differences(RepairOrder o)
    {
        var result = new List<(string, string, string)>();
        RepairServerRow? row = null;
        try { row = RepairServerRow.Parse(System.Text.Json.JsonDocument.Parse(o.ConflictJson).RootElement); }
        catch (System.Text.Json.JsonException) { /* нечитаемый ответ — просто без списка отличий */ }
        if (row is null)
            return result;
        var mine = RepairContentKey.Of(o);
        var theirs = RepairContentKey.Of(row);
        string Show(string key, string v)
        {
            if (v.Length == 0)
                return "—";
            if (key == "status")
                return RepairRules.StatusName(RepairServerRow.ParseStatus(v) ?? RepairStatus.Accepted);
            if (key is "estimate" or "final" && decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var m))
                return ServiceDeskUi.Som(m);
            return v.Length > 80 ? v[..80] + "…" : v;
        }
        string Label(string key) => key switch
        {
            "status" => Tr.T("Статус", "Абалы", "Status", "Durum", "Holat"),
            "device" => Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"),
            "name" => Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"),
            "phone" => Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"),
            "estimate" => Tr.T("Ориентир", "Болжол", "Estimate", "Tahmin", "Taxmin"),
            "final" => Tr.T("Стоимость работ", "Иштин баасы", "Cost of work", "İş bedeli", "Ish narxi"),
            "due" => Tr.T("Срок", "Мөөнөт", "Due", "Süre", "Muddat"),
            "serial" => "S/N / IMEI",
            "fault" => Tr.T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"),
            "equipment" => Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"),
            _ => Tr.T("Заметка", "Эскертүү", "Note", "Not", "Izoh"),
        };
        foreach (var (key, server) in theirs)
        {
            if (key == "prepay")
                continue;
            mine.TryGetValue(key, out var my);
            if ((my ?? "") != server)
                result.Add((Label(key), Show(key, my ?? ""), Show(key, server)));
        }
        return result;
    }

    public RepairConflictDialog(RepairOrder o)
    {
        var title = Tr.T("Квитанцию изменили на другом устройстве", "Квитанцияны башка түзмөктө өзгөртүштү", "The receipt was changed on another device", "Makbuz başka bir cihazda değiştirildi", "Kvitansiya boshqa qurilmada o'zgartirildi");
        Title = title;
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => this.FitToKassaScreen();

        var code = RepairRules.Code(o);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(ServiceDeskUi.Text(Tr.T(
            $"Пока вы правили квитанцию {code}, её изменили на другом устройстве (касса, программа владельца или телефон). Ваши правки ещё не отправлены. Что сделать?",
            $"{code} квитанциясын оңдоп жатканыңызда аны башка түзмөктө (касса, ээсинин программасы же телефон) өзгөртүштү. Оңдоолоруңуз али жөнөтүлө элек. Эмне кылабыз?",
            $"While you were editing receipt {code}, it was changed on another device (till, owner program or phone). Your edits have not been sent yet. What to do?",
            $"{code} makbuzunu düzenlerken başka bir cihazda (kasa, sahip programı veya telefon) değiştirildi. Değişiklikleriniz henüz gönderilmedi. Ne yapılsın?",
            $"{code} kvitansiyasini tahrirlayotganingizda u boshqa qurilmada (kassa, egasi dasturi yoki telefon) o'zgartirildi. O'zgarishlaringiz hali yuborilmagan. Nima qilamiz?"), 14));
        var diffs = Differences(o);
        if (diffs.Count > 0)
        {
            body.Children.Add(ServiceDeskUi.Text(Tr.T("Чем отличается:", "Эмнеси айырмаланат:", "What differs:", "Fark nedir:", "Nimasi farq qiladi:"), 12, brush: "BrushTextSoft"));
            foreach (var (label, mine, server) in diffs.Take(8))
            {
                // Подпись над значениями, значения — отдельными строками: на узком окне ничего не налезает.
                var card = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                card.Children.Add(ServiceDeskUi.Text(label, 11.5, brush: "BrushTextSoft"));
                card.Children.Add(ServiceDeskUi.Text(Tr.T("У вас: ", "Сизде: ", "Yours: ", "Sizde: ", "Sizda: ") + mine, 13.5, true));
                card.Children.Add(ServiceDeskUi.Text(Tr.T("На сервере: ", "Серверде: ", "On the server: ", "Sunucuda: ", "Serverda: ") + server, 13.5, true));
                body.Children.Add(card);
            }
        }

        var later = new Button { Name = "RepairConflictLater", Content = Tr.T("Решить позже", "Кийин чечем", "Decide later", "Sonra karar ver", "Keyinroq hal qilaman"), MinHeight = 40, MinWidth = 120, Margin = new Thickness(0, 0, 8, 8), IsCancel = true };
        later.Classes.Add("modal-cancel");
        later.Click += (_, _) => Close(RepairConflictChoice.Later);
        var serverBtn = new Button { Name = "RepairConflictServer", Content = Tr.T("Взять с сервера", "Серверден алуу", "Take the server version", "Sunucudakini al", "Serverdagini olish"), MinHeight = 40, MinWidth = 140, Margin = new Thickness(0, 0, 8, 8) };
        serverBtn.Classes.Add("modal-cancel");
        serverBtn.Click += (_, _) => Close(RepairConflictChoice.Server);
        var mineBtn = new Button { Name = "RepairConflictMine", Content = Tr.T("Применить мои правки поверх", "Менин оңдоолорумду үстүнөн колдонуу", "Apply my edits on top", "Değişikliklerimi üstüne uygula", "Mening o'zgarishlarimni ustiga qo'llash"), MinHeight = 40, MinWidth = 140, Margin = new Thickness(0, 0, 0, 8) };
        mineBtn.Classes.Add("modal-primary");
        mineBtn.Click += (_, _) => Close(RepairConflictChoice.Mine);
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 660 }; // 720 минус поля оболочки: длинные подписи (uz) переносятся на вторую строку, а не выходят за окно
        footer.Children.Add(later);
        footer.Children.Add(serverBtn);
        footer.Children.Add(mineBtn);

        var shell = new ModalShell { Margin = new Thickness(16), Footer = footer, Content = body, ShowClose = false };
        shell.Title = title;
        Content = shell;
    }

    private void Close(RepairConflictChoice choice)
    {
        Choice = choice;
        Close();
    }
}
