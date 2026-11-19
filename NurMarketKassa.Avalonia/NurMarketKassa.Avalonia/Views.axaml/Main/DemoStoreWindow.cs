using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (Л-10, «Новичок: перенос данных»): «Посмотреть на примере» — 30 учебных товаров демо-магазина (Assets/demo/demo-store.json).
/// Окно только показывает набор и по кнопке «Попробовать продажу на примере» включает его (<see cref="DemoStoreCatalog.Activate"/>) — НЕ записывает ничего на сервер
/// и в базу. Результат ShowAsync: true — кассир хочет попробовать; вызывающий (мастер первого запуска, Н-1) включает учебный режим кассы
/// (MainWindow.EnterTrainingMode), и плитки демо-набора появляются в каталоге на время учебного режима.</summary>
public sealed class DemoStoreWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private const string AssetUri = "avares://NurMarketKassa.Avalonia/Assets/demo/demo-store.json";

    /// <summary>Передать demo-store.json из ресурсов приложения в <see cref="DemoStoreCatalog"/> (один раз). false — ресурса нет.</summary>
    public static bool EnsureSource()
    {
        if (DemoStoreCatalog.HasSource)
            return true;
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            using var reader = new System.IO.StreamReader(stream);
            DemoStoreCatalog.SetSource(reader.ReadToEnd());
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Демо-магазин: ресурс не прочитан ({ex.Message}).", "WARNING");
            return false;
        }
    }

    /// <summary>Показать окно. true — кассир выбрал «Попробовать продажу на примере» (набор включён).</summary>
    public static async Task<bool> ShowAsync(Window? owner)
    {
        if (!EnsureSource())
            return false;
        var window = new DemoStoreWindow();
        if (owner is null)
        {
            window.Show();
            return false;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.TryRequested;
    }

    public bool TryRequested { get; private set; }

    public DemoStoreWindow()
    {
        EnsureSource();
        var products = DemoStoreCatalog.Products();
        Title = T("Посмотреть на примере", "Мисалда көрүү", "See an example", "Örnekte görün", "Misolda ko'rish");
        SectionCards.AsModal(this, 760);
        MinWidth = 560;

        var note = new TextBlock
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = T("Это учебный набор из 30 товаров. Он нужен только для пробы: на сервер и в вашу базу он не попадает, остатки и деньги не меняются. "
                    + "Нажмите «Попробовать продажу на примере» — касса включит учебный режим, и эти товары появятся в каталоге.",
                "Бул 30 товардан турган окуу топтому. Ал сынап көрүү үчүн гана: серверге жана базаңызга түшпөйт, калдык менен акча өзгөрбөйт. "
                    + "«Мисалда сатууну сынап көрүү» басыңыз — касса окуу режимин күйгүзөт, бул товарлар каталогдо чыгат.",
                "This is a training set of 30 products. It is only for trying things out: it does not go to the server or to your database, stock and money do not change. "
                    + "Press “Try a sale on the example” — the till turns on training mode and these products appear in the catalog.",
                "Bu 30 ürünlük bir eğitim setidir. Yalnızca denemek içindir: sunucuya ve veritabanınıza girmez, stok ve para değişmez. "
                    + "«Örnekte satış dene»ye basın — kasa eğitim modunu açar ve bu ürünler katalogda görünür.",
                "Bu 30 ta mahsulotdan iborat o'quv to'plami. U faqat sinab ko'rish uchun: serverga va bazangizga tushmaydi, qoldiq va pul o'zgarmaydi. "
                    + "«Misolda sotuvni sinash»ni bosing — kassa o'quv rejimini yoqadi, bu mahsulotlar katalogda paydo bo'ladi."),
        };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));

        var list = new StackPanel { Spacing = 0 };
        foreach (var p in products)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,130,100"), MinHeight = 32, Margin = new Thickness(0, 1) };
            var name = new TextBlock { Text = p.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var cat = new TextBlock { Text = p.Category, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var price = new TextBlock { Text = $"{p.Sale:0.##} / {p.Unit}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
            cat.Bind(TextBlock.ForegroundProperty, cat.GetResourceObservable("BrushTextSoft"));
            price.Bind(TextBlock.ForegroundProperty, price.GetResourceObservable("BrushText"));
            Grid.SetColumn(cat, 1);
            Grid.SetColumn(price, 2);
            row.Children.Add(name);
            row.Children.Add(cat);
            row.Children.Add(price);
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        var go = SectionCards.Primary(T("Попробовать продажу на примере", "Мисалда сатууну сынап көрүү", "Try a sale on the example", "Örnekte satış dene", "Misolda sotuvni sinash"));
        go.MinHeight = 44;
        go.IsEnabled = products.Count > 0;
        go.Click += (_, _) =>
        {
            DemoStoreCatalog.Activate();
            TryRequested = true;
            Close();
        };

        Content = SectionCards.Shell(Title ?? "", DemoStoreCatalog.Title(),
            new StackPanel { Children = { note, scroll } }, SectionCards.Buttons(close, go));
    }
}
