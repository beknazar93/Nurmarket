using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-19 («Неудобства от владельца», п. 10): блок пустого состояния списка товаров с кнопками действий.
/// 2026-11-21: выбор товаров галочками и панель массовых действий убраны — печать ценников и штрихкодов теперь только на вкладке
/// «Ценники и штрихкоды» (выбор товаров с поиском и фильтрами — внутри неё), здесь больше нет ни галочек, ни кнопок печати.</summary>
public partial class WarehouseWindow
{
    /// <summary>Пустой список товаров: значок, заголовок, подсказка и кнопки действий (общий вид — <see cref="StateBlock.Empty"/>).</summary>
    private Control BuildProductsEmpty(bool filtered, bool compact)
    {
        Control Head(string title, string hint)
        {
            if (!compact)
                return StateBlock.Empty(title, hint);
            var text = new TextBlock { Text = title + ". " + hint, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushText"));
            return text;
        }
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (filtered)
        {
            panel.Children.Add(Head(
                Tr.T("Ничего не найдено", "Эчтеке табылган жок", "Nothing found", "Hiçbir şey bulunamadı", "Hech narsa topilmadi"),
                Tr.T("Измените поиск или фильтры.", "Издөөнү же чыпкаларды өзгөртүңүз.", "Change the search or the filters.", "Aramayı veya filtreleri değiştirin.", "Qidiruv yoki filtrlarni o'zgartiring.")));
            buttons.Children.Add(EmptyAction(Tr.T("Сбросить поиск и фильтры", "Издөөнү жана чыпкаларды тазалоо", "Reset search and filters", "Aramayı ve filtreleri sıfırla", "Qidiruv va filtrlarni tozalash"), primary: false, (_, _) =>
            {
                _viewModel.ProductSearchText = "";
                ResetSaleUnitFilter();
                SetStockFilter("all");
            }));
        }
        else
        {
            // 2026-11-21: импорт списка из файла — только в программе владельца; в кассе вместо кнопки — пояснение ImportAccess.KassaNote.
            if (ImportAccess.Allowed)
            {
                panel.Children.Add(Head(
                    Tr.T("Загрузите товары", "Товарларды жүктөңүз", "Add your products", "Ürünlerinizi yükleyin", "Mahsulotlarni yuklang"),
                    Tr.T("Создайте первый товар или загрузите весь список из Excel, 1С и других программ.", "Биринчи товарды түзүңүз же бүт тизмени Excel, 1С жана башка программалардан жүктөңүз.",
                        "Create the first product or import the whole list from Excel, 1C and other programs.", "İlk ürünü oluşturun veya tüm listeyi Excel, 1C ve diğer programlardan yükleyin.",
                        "Birinchi mahsulotni yarating yoki butun ro'yxatni Excel, 1C va boshqa dasturlardan yuklang.")));
            }
            else
            {
                panel.Children.Add(Head(
                    Tr.T("Добавьте товары", "Товарларды кошуңуз", "Add your products", "Ürünlerinizi ekleyin", "Mahsulotlarni qo'shing"),
                    Tr.T("Создайте первый товар. ", "Биринчи товарды түзүңүз. ", "Create the first product. ", "İlk ürünü oluşturun. ", "Birinchi mahsulotni yarating. ") + ImportAccess.KassaNote + "."));
            }
            buttons.Children.Add(EmptyAction(Tr.T("Создать товар", "Товар түзүү", "Create product", "Ürün oluştur", "Mahsulot yaratish"), primary: true, AddProduct_Click));
            if (ImportAccess.Allowed)
                buttons.Children.Add(EmptyAction(Tr.T("Массовая загрузка", "Массалык жүктөө", "Bulk import", "Toplu yükleme", "Ommaviy yuklash"), primary: false, BulkImport_Click));
        }
        panel.Children.Add(buttons);
        return panel;
    }

    private static Button EmptyAction(string text, bool primary, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 4, 4, 0), Focusable = false };
        button.Classes.Add(primary ? "PrimaryButton" : "SecondaryButton");
        button.Click += click;
        return button;
    }
}
