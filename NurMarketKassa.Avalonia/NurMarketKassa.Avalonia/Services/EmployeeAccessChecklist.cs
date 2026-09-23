using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Чек-лист доступов сотрудника (can_view_*), общий для AddEmployeeDialog (создание) и
/// EmployeeAccessDialog (редактирование уже созданного) — вынесен в отдельный класс 2026-09-21,
/// чтобы не дублировать одну и ту же разметку из ~25 чекбоксов дважды. Группировка и подписи
/// сверены со скриншотом формы "Управление доступами" на сайте и живым GET api/users/employees/
/// (см. doc-comment у EmployeeAccessFlags). "Склад" и "Интерфейс кассира" на сайте показаны
/// дважды (в базовых/секторных доступах И в доп. услугах) — это один и тот же флаг, здесь только
/// один чекбокс на каждый, чтобы не путать кассира двумя независимо выглядящими переключателями
/// одного и того же права.</summary>
internal static class EmployeeAccessChecklist
{
    public sealed class Boxes
    {
        public required CheckBox Cashbox, Analytics, Products, Sale, Clients, BrandCategory, Employees, Settings, MarketProcurement, MarketSupplier;
        public required CheckBox Cashier, Shifts, Document;
        public required CheckBox MarketDiscount, MarketEditPrice, MarketDeleteCartItem, MarketEmployeeReturn;
        public required CheckBox MarketLabel, MarketScales, Whatsapp, Telegram, Instagram, Documents;
    }

    public static (ScrollViewer View, Boxes Checkboxes) Build(EmployeeAccessFlags? initial = null)
    {
        var flags = initial ?? new EmployeeAccessFlags();

        CheckBox Check(string label, bool isChecked) => new()
        {
            Content = label, Margin = new Thickness(0, 0, 20, 8), IsChecked = isChecked,
        };

        TextBlock GroupHeader(string text) => new()
        {
            Text = text, FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 14, 0, 4),
        };

        var boxes = new Boxes
        {
            Cashbox = Check(Tr.T("Касса", "Касса", "Cashbox", "Kasa", "Kassa"), flags.CanViewCashbox),
            Analytics = Check(Tr.T("Аналитика", "Аналитика", "Analytics", "Analitik", "Analitika"), flags.CanViewAnalytics),
            Products = Check(Tr.T("Склад", "Склад", "Warehouse", "Depo", "Ombor"), flags.CanViewProducts),
            Sale = Check(Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"), flags.CanViewSale),
            Clients = Check(Tr.T("Клиенты", "Кардарлар", "Clients", "Müşteriler", "Mijozlar"), flags.CanViewClients),
            BrandCategory = Check(Tr.T("Бренд, Категория", "Бренд, Категория", "Brand, Category", "Marka, Kategori", "Brend, Kategoriya"), flags.CanViewBrandCategory),
            Employees = Check(Tr.T("Сотрудники", "Кызматкерлер", "Employees", "Personel", "Xodimlar"), flags.CanViewEmployees),
            Settings = Check(Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar"), flags.CanViewSettings),
            MarketProcurement = Check(Tr.T("Закупки", "Сатып алуулар", "Procurement", "Satın almalar", "Xaridlar"), flags.CanViewMarketProcurement),
            MarketSupplier = Check(Tr.T("Поставщики", "Жеткирүүчүлөр", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilar"), flags.CanViewMarketSupplier),

            Cashier = Check(Tr.T("Интерфейс кассира", "Кассир интерфейси", "Cashier interface", "Kasiyer arayüzü", "Kassir interfeysi"), flags.CanViewCashier),
            Shifts = Check(Tr.T("Смены", "Сменалар", "Shifts", "Vardiyalar", "Smenalar"), flags.CanViewShifts),
            Document = Check(Tr.T("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar"), flags.CanViewDocument),

            MarketDiscount = Check(Tr.T("Скидка в кассе", "Кассадагы арзандатуу", "Discount at checkout", "Kasada indirim", "Kassada chegirma"), flags.CanViewMarketDiscount),
            MarketEditPrice = Check(Tr.T("Изменение цены в кассе", "Кассада баасын өзгөртүү", "Change price at checkout", "Kasada fiyat değiştirme", "Kassada narxni o'zgartirish"), flags.CanViewMarketEditPrice),
            MarketDeleteCartItem = Check(Tr.T("Удаление позиций из корзины", "Себеттен позицияларды өчүрүү", "Delete cart items", "Sepetten ürün silme", "Savatdan pozitsiyalarni o'chirish"), flags.CanViewMarketDeleteCartItem),
            MarketEmployeeReturn = Check(Tr.T("Возврат продаж сотрудником", "Кызматкер тарабынан кайтаруу", "Return sales by employee", "Personel tarafından iade", "Xodim tomonidan qaytarish"), flags.CanViewMarketEmployeeReturn),

            MarketLabel = Check(Tr.T("Печать штрих-кодов", "Штрихкод басып чыгаруу", "Print barcodes", "Barkod yazdırma", "Shtrix-kod chop etish"), flags.CanViewMarketLabel),
            MarketScales = Check(Tr.T("Интеграция с весами", "Тараза менен интеграция", "Scale integration", "Tartı entegrasyonu", "Tarozi bilan integratsiya"), flags.CanViewMarketScales),
            Whatsapp = Check("WhatsApp", flags.CanViewWhatsapp),
            Telegram = Check("Telegram", flags.CanViewTelegram),
            Instagram = Check("Instagram", flags.CanViewInstagram),
            Documents = Check(Tr.T("Документы (доп. услуга)", "Документтер (кошумча кызмат)", "Documents (add-on)", "Belgeler (ek hizmet)", "Hujjatlar (qo'shimcha xizmat)"), flags.CanViewDocuments),
        };

        WrapPanel Row(params CheckBox[] cbs)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var box in cbs)
                wrap.Children.Add(box);
            return wrap;
        }

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(GroupHeader(Tr.T("Базовые доступы", "Негизги доступтар", "Basic access", "Temel erişimler", "Asosiy huquqlar")));
        panel.Children.Add(Row(boxes.Cashbox, boxes.Analytics, boxes.Products, boxes.Sale, boxes.Clients, boxes.BrandCategory, boxes.Employees, boxes.Settings, boxes.MarketProcurement, boxes.MarketSupplier));

        panel.Children.Add(GroupHeader(Tr.T("Секторные доступы", "Секторлук доступтар", "Sector access", "Sektör erişimleri", "Sektor huquqlari")));
        panel.Children.Add(Row(boxes.Cashier, boxes.Shifts, boxes.Document));

        panel.Children.Add(GroupHeader(Tr.T("Касса Маркета", "Маркеттин кассасы", "Market checkout", "Market kasası", "Market kassasi")));
        panel.Children.Add(Row(boxes.MarketDiscount, boxes.MarketEditPrice, boxes.MarketDeleteCartItem, boxes.MarketEmployeeReturn));

        panel.Children.Add(GroupHeader(Tr.T("Дополнительные услуги", "Кошумча кызматтар", "Add-on services", "Ek hizmetler", "Qo'shimcha xizmatlar")));
        panel.Children.Add(Row(boxes.MarketLabel, boxes.MarketScales, boxes.Whatsapp, boxes.Telegram, boxes.Instagram, boxes.Documents));

        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        return (scroll, boxes);
    }

    public static EmployeeAccessFlags Read(Boxes b) => new()
    {
        CanViewCashbox = b.Cashbox.IsChecked == true,
        CanViewAnalytics = b.Analytics.IsChecked == true,
        CanViewProducts = b.Products.IsChecked == true,
        CanViewSale = b.Sale.IsChecked == true,
        CanViewClients = b.Clients.IsChecked == true,
        CanViewBrandCategory = b.BrandCategory.IsChecked == true,
        CanViewEmployees = b.Employees.IsChecked == true,
        CanViewSettings = b.Settings.IsChecked == true,
        CanViewMarketProcurement = b.MarketProcurement.IsChecked == true,
        CanViewMarketSupplier = b.MarketSupplier.IsChecked == true,
        CanViewCashier = b.Cashier.IsChecked == true,
        CanViewShifts = b.Shifts.IsChecked == true,
        CanViewDocument = b.Document.IsChecked == true,
        CanViewMarketDiscount = b.MarketDiscount.IsChecked == true,
        CanViewMarketEditPrice = b.MarketEditPrice.IsChecked == true,
        CanViewMarketDeleteCartItem = b.MarketDeleteCartItem.IsChecked == true,
        CanViewMarketEmployeeReturn = b.MarketEmployeeReturn.IsChecked == true,
        CanViewMarketLabel = b.MarketLabel.IsChecked == true,
        CanViewMarketScales = b.MarketScales.IsChecked == true,
        CanViewWhatsapp = b.Whatsapp.IsChecked == true,
        CanViewTelegram = b.Telegram.IsChecked == true,
        CanViewInstagram = b.Instagram.IsChecked == true,
        CanViewDocuments = b.Documents.IsChecked == true,
    };
}
