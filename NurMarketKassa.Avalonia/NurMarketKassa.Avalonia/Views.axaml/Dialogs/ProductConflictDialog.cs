using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Выбор владельца в окне конфликта правок.</summary>
public enum ConflictChoice
{
    /// <summary>Не сохранять, окно остаётся открытым, введённое не теряется.</summary>
    Cancel,
    /// <summary>Подставить в окно свежие значения изменённых полей (остальной ввод остаётся), сохранять самому.</summary>
    Refresh,
    /// <summary>Сохранить введённое поверх чужой правки.</summary>
    Overwrite,
}

/// <summary>
/// 2026-11-20 (С7): «Данные изменились на другом устройстве» — при сохранении карточки, если поля, которые правит владелец, уже изменены на сервере с момента открытия окна
/// (<see cref="ProductConflict"/>). Три выхода: обновить / перезаписать / отмена; ничего из введённого само не пропадает.
/// </summary>
public sealed class ProductConflictDialog : Window
{
    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

    public ProductConflictDialog(string what, IReadOnlyList<ProductConflict.Diff> diffs)
    {
        var title = Title = Tr.T("Данные изменились на другом устройстве", "Берилиштер башка түзмөктө өзгөрдү", "Data changed on another device",
            "Veriler başka bir cihazda değişti", "Ma'lumotlar boshqa qurilmada o'zgardi");
        SectionCards.AsModal(this, 640);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = Message(what, diffs), TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Finish(ConflictChoice.Cancel);
        var refresh = SectionCards.Secondary(Tr.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Click += (_, _) => Finish(ConflictChoice.Refresh);
        var overwrite = SectionCards.Primary(Tr.T("Перезаписать", "Үстүнөн жазуу", "Overwrite", "Üzerine yaz", "Ustiga yozish"));
        overwrite.Click += (_, _) => Finish(ConflictChoice.Overwrite);
        Content = SectionCards.Shell(title, null, body, SectionCards.Buttons(cancel, refresh, overwrite));
    }

    private void Finish(ConflictChoice choice)
    {
        Choice = choice;
        Close(choice);
    }

    /// <summary>Подпись поля по ключу ответа сервера (на языке кассы).</summary>
    public static string FieldLabel(string key) => key switch
    {
        "name" => Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi"),
        "price" => Tr.T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"),
        "purchase_price" => Tr.T("Закупочная цена", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
        "wholesale_price" => Tr.T("Оптовая цена", "Көтөрмө баасы", "Wholesale price", "Toptan fiyat", "Ulgurji narx"),
        "description" => Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif"),
        "storage_location" => Tr.T("Ячейка", "Уяча", "Bin", "Göz", "Katak"),
        "attributes" => Tr.T("Номера и признаки запчасти", "Тетиктин номерлери жана белгилери", "Part numbers and flags", "Parça numaraları ve işaretleri", "Detal raqamlari va belgilari"),
        _ => key,
    };

    /// <summary>Текст окна: что изменилось и что делают кнопки (значения обрезаны, длинные атрибуты не печатаются целиком).</summary>
    public static string Message(string what, IReadOnlyList<ProductConflict.Diff> diffs)
    {
        static string Cut(string v) => v.Length == 0 ? "—" : v.Length > 40 || v.StartsWith('[') ? Tr.T("изменено", "өзгөртүлдү", "changed", "değişti", "o'zgardi") : v;
        var lines = string.Join("\n", diffs.Take(6).Select(d => $"• {FieldLabel(d.Key)}: {Cut(d.Opened)} → {Cut(d.Fresh)}"));
        return Tr.T($"«{what}» уже изменили на другом устройстве, пока это окно было открыто:\n{lines}\n\nОбновить — подставить свежие значения в окно (остальной ввод останется). Перезаписать — сохранить ваши значения поверх. Отмена — вернуться к окну без сохранения.",
            $"«{what}» бул терезе ачык турганда башка түзмөктө өзгөртүлдү:\n{lines}\n\nЖаңыртуу — жаңы маанилерди терезеге коюу (калган киргизүү сакталат). Үстүнөн жазуу — сиздин маанилерди сактоо. Жокко чыгаруу — сактабай терезеге кайтуу.",
            $"“{what}” was changed on another device while this window was open:\n{lines}\n\nRefresh puts the fresh values into the window (the rest of your input stays). Overwrite saves your values over them. Cancel returns to the window without saving.",
            $"«{what}» bu pencere açıkken başka bir cihazda değiştirildi:\n{lines}\n\nYenile — güncel değerleri pencereye koyar (diğer girişleriniz kalır). Üzerine yaz — sizin değerlerinizi kaydeder. İptal — kaydetmeden pencereye döner.",
            $"«{what}» bu oyna ochiq turganda boshqa qurilmada o'zgartirildi:\n{lines}\n\nYangilash — yangi qiymatlarni oynaga qo'yadi (qolgan kiritganlaringiz qoladi). Ustiga yozish — sizning qiymatlaringizni saqlaydi. Bekor qilish — saqlamasdan oynaga qaytadi.");
    }
}
