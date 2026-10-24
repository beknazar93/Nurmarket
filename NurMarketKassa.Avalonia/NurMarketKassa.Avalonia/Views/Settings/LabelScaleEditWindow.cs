using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-09-28 (просьба владельца): окно настроек ОДНИХ весов из списка — название, марка,
/// свой адрес/порт/способ отправки (LabelScaleSetupPanel) и категории товаров этих весов.
/// Окно строится в коде (как окна марок) и помещается на экраны 800×600, 1024×768, 1024×1024:
/// всё содержимое — в прокрутке, кнопки «Сохранить»/«Отмена» всегда видны внизу.</summary>
public sealed class LabelScaleEditWindow : Window
{
    private readonly LabelScaleProfile _profile;
    private readonly LabelScaleProfile _snapshot;
    private readonly LabelScaleSetupPanel _panel = new() { IsDialogMode = true };
    // 2026-10-22 (модалки, пакет 3): поле 44 px (было 38).
    private readonly TextBox _nameBox = new() { Classes = { "ModernTextBox" }, Height = 44, MaxLength = 40, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _categoriesPanel = new() { Orientation = Orientation.Horizontal };
    private bool _saved;

    /// <summary>Сохранили (а не закрыли/отменили).</summary>
    public bool Saved => _saved;

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public LabelScaleEditWindow() : this(LabelScaleStore.Active)
    {
    }

    public LabelScaleEditWindow(LabelScaleProfile profile)
    {
        _profile = profile;
        _snapshot = profile.Clone();
        // Поля панели читают прежние настройки марки — сначала переносим туда адрес этих весов.
        LabelScaleStore.Activate(profile);

        Title = L("Настройки весов", "Тараза жөндөөлөрү", "Scale settings", "Tartı ayarları", "Tarozi sozlamalari") + " — " + profile.Name;
        // 2026-10-22 (модалки, пакет 3): окно без рамки ОС в общей оболочке ModalShell (L = 912, было своё окно 880×760):
        // заголовок и ✕ сверху, «Отмена» (Esc = ✕) и «Сохранить» (Enter) — в закреплённом подвале, карточки прокручиваются.
        SectionCards.AsModal(this, 912);
        Styles.Add(new StyleInclude(new Uri("avares://NurMarketKassa.Avalonia/"))
        {
            Source = new Uri("avares://NurMarketKassa.Avalonia/Views/Settings/SettingsSharedStyles.axaml"),
        });

        _nameBox.Text = profile.Name;
        var nameCard = Card(L("Название весов", "Тараза аталышы", "Scale name", "Tartı adı", "Tarozi nomi"),
            L("Как весы подписаны в списке и в окне «Весы» (например «Весы в мясном отделе»).",
              "Тараза тизмеде жана «Таразалар» терезесинде кандай аталат (мисалы «Эт бөлүмүндөгү тараза»).",
              "How the scale is labelled in the list and in the “Scales” window (e.g. “Meat counter scale”).",
              "Tartının listede ve «Tartı» penceresinde adı (ör. «Et reyonu tartısı»).",
              "Tarozi ro‘yxatda va «Tarozi» oynasida qanday nomlanadi (masalan «Go‘sht bo‘limi tarozisi»)."),
            _nameBox);

        BuildCategories();
        var categoriesCard = Card(L("Категории на этих весах", "Бул таразадагы категориялар", "Categories on this scale", "Bu tartıdaki kategoriler", "Bu tarozidagi kategoriyalar"),
            L("Отметьте категории — в окне «Весы» для этих весов будут отмечены только их товары, и «Отправить на весы» пошлёт только их. Ничего не отмечено — все весовые товары (или отмеченные вручную при прошлой отправке).",
              "Категорияларды белгилеңиз — «Таразалар» терезесинде бул тараза үчүн алардын товарлары гана белгиленет жана «Таразага жөнөтүү» аларды гана жөнөтөт. Эч нерсе белгиленбесе — бардык салмактуу товарлар (же мурунку жөнөтүүдө кол менен белгиленгендер).",
              "Tick categories — in the “Scales” window only their goods will be ticked for this scale, and “Send to scale” sends only them. Nothing ticked — all weighed goods (or those ticked by hand last time).",
              "Kategorileri işaretleyin — «Tartı» penceresinde bu tartı için yalnızca onların ürünleri işaretlenir ve «Tartıya gönder» yalnızca onları gönderir. Hiçbiri işaretli değilse — tüm tartılı ürünler (veya geçen sefer elle işaretlenenler).",
              "Kategoriyalarni belgilang — «Tarozi» oynasida bu tarozi uchun faqat ularning tovarlari belgilanadi va «Taroziga yuborish» faqat ularni yuboradi. Hech narsa belgilanmasa — barcha vaznli tovarlar (yoki o‘tgan safar qo‘lda belgilanganlar)."),
            _categoriesPanel);

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(nameCard);
        content.Children.Add(_panel);
        content.Children.Add(categoriesCard);

        var cancel = SectionCards.Cancel(L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var save = SectionCards.Primary(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        save.Click += (_, _) => SaveAndClose();
        Content = SectionCards.Shell(L("Настройки весов", "Тараза жөндөөлөрү", "Scale settings", "Tartı ayarları", "Tarozi sozlamalari"), profile.Name,
            content, SectionCards.Buttons(cancel, save));
        // Высоту по экрану кассы ограничивает ModalShell (85 % рабочей области), подвал всегда виден.
        Closing += (_, _) =>
        {
            // Закрыли крестиком или «Отменой» — возвращаем весам прежний адрес и марку.
            if (!_saved)
                LabelScaleStore.Restore(_snapshot);
        };
    }

    private static Border Card(string title, string hint, Control body)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "SettingsCardTitle" }, FontSize = 14 });
        stack.Children.Add(new TextBlock { Text = hint, Classes = { "SettingsCardBody" } });
        stack.Children.Add(body);
        return new Border { Classes = { "SettingsCard" }, Padding = new Thickness(14), Child = stack };
    }

    /// <summary>Категории весовых товаров каталога — галочками.</summary>
    private void BuildCategories()
    {
        var categories = CatalogCacheService.Products
            .Where(p => p.IsWeighted && !string.IsNullOrWhiteSpace(p.Category))
            .Select(p => p.Category!.Trim())
            .Concat(_profile.Categories) // категория могла исчезнуть из каталога — не теряем отметку
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _categoriesPanel.Children.Clear();
        if (categories.Count == 0)
        {
            _categoriesPanel.Children.Add(new TextBlock
            {
                Text = L("У весовых товаров в каталоге нет категорий — на весы уйдут отмеченные товары.",
                    "Каталогдогу салмактуу товарлардын категориясы жок — таразага белгиленген товарлар кетет.",
                    "Weighed goods in the catalog have no categories — the ticked goods will be sent.",
                    "Katalogdaki tartılı ürünlerin kategorisi yok — işaretli ürünler gönderilir.",
                    "Katalogdagi vaznli tovarlarning kategoriyasi yo‘q — belgilangan tovarlar yuboriladi."),
                Classes = { "SettingsCardBody" },
            });
            return;
        }
        foreach (var category in categories)
        {
            var count = CatalogCacheService.Products.Count(p => p.IsWeighted && string.Equals(p.Category?.Trim(), category, StringComparison.CurrentCultureIgnoreCase));
            _categoriesPanel.Children.Add(new CheckBox
            {
                Content = $"{category} ({count})",
                Tag = category,
                IsChecked = _profile.Categories.Contains(category, StringComparer.CurrentCultureIgnoreCase),
                Margin = new Thickness(0, 0, 16, 4),
            });
        }
    }

    private void SaveAndClose()
    {
        _panel.SaveFields();
        LabelScaleStore.CaptureActive();
        var name = (_nameBox.Text ?? "").Trim();
        if (name.Length > 0)
            _profile.Name = name;
        _profile.Categories = _categoriesPanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true && c.Tag is string)
            .Select(c => (string)c.Tag!)
            .ToList();
        LabelScaleStore.Save();
        _saved = true;
        Close();
    }
}
