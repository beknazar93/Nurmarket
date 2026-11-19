using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.AvaloniaHost.Views;
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
        /// <summary>2026-10-28: флаги схемы сервера (редактирование, зарплата, долги, работа на кассе) — чекбокс есть, только если
        /// сервер вернул это поле у сотрудника (feature-detect); при создании их нет.</summary>
        public Dictionary<string, CheckBox> Extra { get; } = new();
        /// <summary>2026-11-19: все галочки по ключам (шаблоны ролей, поиск) и событие «что-то изменилось» (подсветка «Сохранить»).</summary>
        public Dictionary<string, CheckBox> ByKey { get; } = new();
        public List<CheckBox> All { get; } = new();
        public event Action? Changed;
        internal void RaiseChanged() => Changed?.Invoke();
    }

    public static (ScrollViewer View, Boxes Checkboxes) Build(EmployeeAccessFlags? initial = null)
    {
        var flags = initial ?? new EmployeeAccessFlags();

        CheckBox Check(string label, bool isChecked) => new()
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 20, 8), IsChecked = isChecked,
        };

        var boxes = new Boxes
        {
            // 2026-10-18, аудит: can_view_cashbox на сайте — «Финансы → Кассы» (деньги всех касс компании), а не
            // работа на кассе; подпись «Касса» вводила владельца в заблуждение.
            Cashbox = Check(Tr.T("Финансы: кассы компании", "Финансы: компаниянын кассалары", "Finance: company cash desks", "Finans: şirket kasaları", "Moliya: kompaniya kassalari"), flags.CanViewCashbox),
            Analytics = Check(Tr.T("Аналитика", "Аналитика", "Analytics", "Analitik", "Analitika"), flags.CanViewAnalytics),
            Products = Check(Tr.T("Склад", "Склад", "Warehouse", "Depo", "Ombor"), flags.CanViewProducts),
            Sale = Check(Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"), flags.CanViewSale),
            Clients = Check(Tr.T("Клиенты", "Кардарлар", "Clients", "Müşteriler", "Mijozlar"), flags.CanViewClients),
            BrandCategory = Check(Tr.T("Бренд, Категория", "Бренд, Категория", "Brand, Category", "Marka, Kategori", "Brend, Kategoriya"), flags.CanViewBrandCategory),
            Employees = Check(Tr.T("Сотрудники", "Кызматкерлер", "Employees", "Personel", "Xodimlar"), flags.CanViewEmployees),
            Settings = Check(Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar"), flags.CanViewSettings),
            MarketProcurement = Check(Tr.T("Закупки", "Сатып алуулар", "Purchasing", "Satın almalar", "Xaridlar"), flags.CanViewMarketProcurement),
            MarketSupplier = Check(Tr.T("Поставщики", "Жеткирүүчүлөр", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilar"), flags.CanViewMarketSupplier),

            Cashier = Check(Tr.T("Интерфейс кассира", "Кассир интерфейси", "Cashier interface", "Kasiyer arayüzü", "Kassir interfeysi"), flags.CanViewCashier),
            Shifts = Check(Tr.T("Смены", "Сменалар", "Shifts", "Vardiyalar", "Smenalar"), flags.CanViewShifts),
            Document = Check(Tr.T("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar"), flags.CanViewDocument),

            MarketDiscount = Check(Tr.T("Скидка в кассе", "Кассадагы арзандатуу", "Discount at checkout", "Kasada indirim", "Kassada chegirma"), flags.CanViewMarketDiscount),
            MarketEditPrice = Check(Tr.T("Изменение цены в кассе", "Кассада баасын өзгөртүү", "Price change at checkout", "Kasada fiyat değiştirme", "Kassada narxni o'zgartirish"), flags.CanViewMarketEditPrice),
            MarketDeleteCartItem = Check(Tr.T("Удаление позиций из корзины", "Себеттен позицияларды өчүрүү", "Removing items from the cart", "Sepetten ürün silme", "Savatdan pozitsiyalarni o'chirish"), flags.CanViewMarketDeleteCartItem),
            MarketEmployeeReturn = Check(Tr.T("Возврат продаж сотрудником", "Кызматкер тарабынан кайтаруу", "Sales returns by employee", "Personel tarafından iade", "Xodim tomonidan sotuvlarni qaytarish"), flags.CanViewMarketEmployeeReturn),

            MarketLabel = Check(Tr.T("Печать штрих-кодов", "Штрихкод басып чыгаруу", "Barcode printing", "Barkod yazdırma", "Shtrix-kod chop etish"), flags.CanViewMarketLabel),
            MarketScales = Check(Tr.T("Интеграция с весами", "Тараза менен интеграция", "Scale integration", "Tartı entegrasyonu", "Tarozi bilan integratsiya"), flags.CanViewMarketScales),
            Whatsapp = Check("WhatsApp", flags.CanViewWhatsapp),
            Telegram = Check("Telegram", flags.CanViewTelegram),
            Instagram = Check("Instagram", flags.CanViewInstagram),
            Documents = Check(Tr.T("Документы (доп. услуга)", "Документтер (кошумча кызмат)", "Documents (add-on)", "Belgeler (ek hizmet)", "Hujjatlar (qo'shimcha xizmat)"), flags.CanViewDocuments),
        };

        // 2026-11-19 (запрос владельца, п. 3): ключи для шаблонов ролей и поиска, группы-карточки со счётчиком и «Выбрать все».
        void Reg(string key, CheckBox box) { boxes.ByKey[key] = box; boxes.All.Add(box); }
        Reg("cashbox", boxes.Cashbox); Reg("analytics", boxes.Analytics); Reg("products", boxes.Products); Reg("sale", boxes.Sale);
        Reg("clients", boxes.Clients); Reg("brand", boxes.BrandCategory); Reg("employees", boxes.Employees); Reg("settings", boxes.Settings);
        Reg("procurement", boxes.MarketProcurement); Reg("supplier", boxes.MarketSupplier); Reg("cashier", boxes.Cashier);
        Reg("shifts", boxes.Shifts); Reg("document", boxes.Document); Reg("discount", boxes.MarketDiscount);
        Reg("editprice", boxes.MarketEditPrice); Reg("delcart", boxes.MarketDeleteCartItem); Reg("empreturn", boxes.MarketEmployeeReturn);
        Reg("label", boxes.MarketLabel); Reg("scales", boxes.MarketScales); Reg("whatsapp", boxes.Whatsapp);
        Reg("telegram", boxes.Telegram); Reg("instagram", boxes.Instagram); Reg("documents", boxes.Documents);
        // 2026-11-01 (ответ Умара): право «Редактор документов закупок» (can_edit_purchase_documents) выдают и снимают владелец ИЛИ администратор — строка в блоке «Редактирование и зарплата»
        // (показывается, когда сервер вернул поле; у владельца и администратора право есть всегда).

        // 2026-10-28: поля из схемы сервера (EmployeeUpdate) и Б-16 (can_use_pos) — только если сервер их вернул.
        var extra = new List<(string Key, string Label, bool? Value)>
        {
            ("use_pos", Tr.T("Работа на кассе (продажи, смены)", "Кассада иштөө (сатуу, сменалар)", "Work at the till (sales, shifts)", "Kasada çalışma (satış, vardiya)", "Kassada ishlash (sotuv, smenalar)"), flags.CanUsePos),
            // 2026-11-13 (живая проверка доступов, ошибка 6): право «Продажа ниже закупки» — как на сайте; владелец и администратор имеют его по роли.
            ("sell_below_cost", Tr.T("Продажа ниже закупки (владелец и админ — всегда)", "Сатып алуу баасынан төмөн сатуу (ээси жана админ — дайыма)", "Selling below cost (owner and admin always can)", "Alış fiyatının altında satış (sahip ve yönetici her zaman)", "Xarid narxidan past sotish (egasi va admin — doim)"),
                flags.SellBelowCostShown ? flags.CanSellBelowCost ?? false : (bool?)null),
            ("view_salary", Tr.T("Зарплата: просмотр", "Эмгек акы: көрүү", "Salary: view", "Maaş: görüntüleme", "Ish haqi: ko'rish"), flags.CanViewSalary),
            ("edit_salary", Tr.T("Зарплата: редактирование", "Эмгек акы: түзөтүү", "Salary: edit", "Maaş: düzenleme", "Ish haqi: tahrirlash"), flags.CanEditSalary),
            ("edit_employees", Tr.T("Сотрудники: редактирование", "Кызматкерлер: түзөтүү", "Employees: edit", "Personel: düzenleme", "Xodimlar: tahrirlash"), flags.CanEditEmployees),
            ("edit_products", Tr.T("Склад: редактирование", "Склад: түзөтүү", "Warehouse: edit", "Depo: düzenleme", "Ombor: tahrirlash"), flags.CanEditProducts),
            ("edit_clients", Tr.T("Клиенты: редактирование", "Кардарлар: түзөтүү", "Clients: edit", "Müşteriler: düzenleme", "Mijozlar: tahrirlash"), flags.CanEditClients),
            ("edit_cashbox", Tr.T("Финансы: редактирование", "Финансы: түзөтүү", "Finance: edit", "Finans: düzenleme", "Moliya: tahrirlash"), flags.CanEditCashbox),
            ("edit_settings", Tr.T("Настройки: редактирование", "Жөндөөлөр: түзөтүү", "Settings: edit", "Ayarlar: düzenleme", "Sozlamalar: tahrirlash"), flags.CanEditSettings),
            ("edit_procurement", Tr.T("Закупки: редактирование", "Сатып алуулар: түзөтүү", "Purchasing: edit", "Satın alma: düzenleme", "Xaridlar: tahrirlash"), flags.CanEditProcurement),
            ("edit_documents", Tr.T("Документы: редактирование", "Документтер: түзөтүү", "Documents: edit", "Belgeler: düzenleme", "Hujjatlar: tahrirlash"), flags.CanEditDocuments),
            ("view_debts", Tr.T("Долги: просмотр", "Карыздар: көрүү", "Debts: view", "Borçlar: görüntüleme", "Qarzlar: ko'rish"), flags.CanViewDebts),
            ("edit_purchase_documents", Tr.T("Редактор документов закупок (правка накладных)", "Сатып алуу документтеринин редактору (накладнойлорду оңдоо)", "Purchase documents editor (invoice edits)", "Alım belgeleri düzenleyicisi (fatura düzenleme)", "Xarid hujjatlari muharriri (yuk xatlarini tahrirlash)"), flags.CanEditPurchaseDocuments),
        };
        var extraKeys = new List<string>();
        foreach (var e in extra.Where(e => e.Value.HasValue))
        {
            var box = Check(e.Label, e.Value == true);
            boxes.Extra[e.Key] = box;
            Reg(e.Key, box);
            extraKeys.Add(e.Key);
        }

        // Пояснения под опасными правами: что именно разрешает галочка.
        var notes = new Dictionary<string, string>
        {
            ["discount"] = Tr.T("Кассир сможет давать скидку на чек и позицию.", "Кассир чекке жана позицияга арзандатуу бере алат.", "The cashier can give discounts on a receipt or item.", "Kasiyer fişe veya ürüne indirim verebilir.", "Kassir chek va pozitsiyaga chegirma bera oladi."),
            ["editprice"] = Tr.T("Кассир сможет менять цену товара прямо в чеке.", "Кассир товардын баасын чекте өзгөртө алат.", "The cashier can change an item's price in the receipt.", "Kasiyer fişte ürün fiyatını değiştirebilir.", "Kassir chekda tovar narxini o'zgartira oladi."),
            ["sell_below_cost"] = Tr.T("Можно продать с убытком: дешевле закупочной цены.", "Зыян менен сатууга болот: сатып алуу баасынан арзан.", "Selling at a loss: cheaper than the purchase price.", "Zararına satış: alış fiyatından ucuza.", "Zarar bilan sotish mumkin: xarid narxidan arzon."),
            ["delcart"] = Tr.T("Кассир сможет убирать позиции из чека до оплаты.", "Кассир төлөөгө чейин позицияларды өчүрө алат.", "The cashier can remove items before payment.", "Kasiyer ödemeden önce ürünleri silebilir.", "Kassir to'lovdan oldin pozitsiyalarni o'chira oladi."),
            ["edit_salary"] = Tr.T("Сотрудник сможет менять чужие зарплаты.", "Кызматкер башкалардын эмгек акысын өзгөртө алат.", "The employee can change others' salaries.", "Personel başkalarının maaşını değiştirebilir.", "Xodim boshqalarning ish haqini o'zgartira oladi."),
        };

        var groups = new List<(string Title, string[] Keys)>
        {
            (Tr.T("Базовые доступы", "Негизги укуктар", "Basic permissions", "Temel erişimler", "Asosiy huquqlar"),
                new[] { "cashbox", "analytics", "products", "sale", "clients", "brand", "employees", "settings", "procurement", "supplier" }),
            (Tr.T("Секторные доступы", "Секторлук укуктар", "Sector permissions", "Sektör erişimleri", "Sektor huquqlari"),
                new[] { "cashier", "shifts", "document" }),
            (Tr.T("Касса Маркета", "Маркеттин кассасы", "Market till", "Market kasası", "Market kassasi"),
                new[] { "discount", "editprice", "delcart", "empreturn" }),
            (Tr.T("Дополнительные услуги", "Кошумча кызматтар", "Add-on services", "Ek hizmetler", "Qo'shimcha xizmatlar"),
                new[] { "label", "scales", "whatsapp", "telegram", "instagram", "documents" }),
        };
        if (extraKeys.Count > 0)
            groups.Add((Tr.T("Редактирование и зарплата", "Түзөтүү жана эмгек акы", "Editing and salary", "Düzenleme ve maaş", "Tahrirlash va ish haqi"), extraKeys.ToArray()));

        var panel = new StackPanel { Spacing = 10 };
        var noResults = new TextBlock
        {
            Text = Tr.T("Такого права нет. Измените запрос.", "Мындай укук жок. Сурамды өзгөртүңүз.", "No such permission. Change the search.", "Böyle bir erişim yok. Aramayı değiştirin.", "Bunday huquq yo'q. So'rovni o'zgartiring."),
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16), FontSize = 13,
        };
        noResults.Bind(TextBlock.ForegroundProperty, noResults.GetResourceObservable("BrushTextSoft"));

        var cardRefresh = new List<Func<string, bool>>();
        var counters = new List<Action>();
        foreach (var (title, keys) in groups)
        {
            var members = keys.Where(boxes.ByKey.ContainsKey).ToList();
            if (members.Count == 0)
                continue;
            var rows = new List<(Control Row, string Text)>();
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var key in members)
            {
                var box = boxes.ByKey[key];
                box.Margin = new Thickness(0);
                var row = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 12, 8) };
                row.Children.Add(box);
                if (notes.TryGetValue(key, out var note))
                {
                    var noteText = new TextBlock { Text = note, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 0, 0, 0) };
                    noteText.Classes.Add("access-note");
                    noteText.Bind(TextBlock.ForegroundProperty, noteText.GetResourceObservable("BrushWarning"));
                    row.Children.Add(noteText);
                }
                rows.Add((row, (((box.Content as TextBlock)?.Text) ?? "") + " " + (notes.GetValueOrDefault(key) ?? "")));
                wrap.Children.Add(row);
            }

            var count = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            count.Bind(TextBlock.ForegroundProperty, count.GetResourceObservable("BrushTextSoft"));
            var all = SectionCards.Ghost("");
            var memberBoxes = members.Select(k => boxes.ByKey[k]).ToList();
            void UpdateCounter()
            {
                var on = memberBoxes.Count(c => c.IsChecked == true);
                count.Text = Tr.T($"{on} из {memberBoxes.Count}", $"{on} / {memberBoxes.Count}", $"{on} of {memberBoxes.Count}", $"{on} / {memberBoxes.Count}", $"{on} / {memberBoxes.Count}");
                all.Content = on == memberBoxes.Count
                    ? Tr.T("Снять все", "Баарын алуу", "Clear all", "Tümünü kaldır", "Hammasini olib tashlash")
                    : Tr.T("Выбрать все", "Баарын тандоо", "Select all", "Tümünü seç", "Hammasini tanlash");
            }
            counters.Add(UpdateCounter);
            UpdateCounter();
            all.Click += (_, _) =>
            {
                var target = memberBoxes.Any(c => c.IsChecked != true);
                foreach (var c in memberBoxes)
                    c.IsChecked = target;
            };

            var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            titleText.Bind(TextBlock.ForegroundProperty, titleText.GetResourceObservable("BrushText"));
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(titleText);
            Grid.SetColumn(count, 1);
            head.Children.Add(count);
            Grid.SetColumn(all, 2);
            head.Children.Add(all);

            var card = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 4), BorderThickness = new Thickness(1),
                Child = new StackPanel { Children = { head, wrap } },
            };
            card.Classes.Add("access-card");
            card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
            Avalonia.Automation.AutomationProperties.SetName(card, title);
            panel.Children.Add(card);

            cardRefresh.Add(query =>
            {
                var any = false;
                foreach (var (row, text) in rows)
                {
                    var show = query.Length == 0 || text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                    row.IsVisible = show;
                    any |= show;
                }
                card.IsVisible = any;
                return any;
            });
        }
        panel.Children.Add(noResults);

        foreach (var box in boxes.All)
            box.IsCheckedChanged += (_, _) =>
            {
                foreach (var c in counters)
                    c();
                boxes.RaiseChanged();
            };

        // Верх: поиск по названию права и шаблоны ролей.
        var search = new TextBox
        {
            Name = "AccessSearchBox",
            Watermark = Tr.T("Найти право (например, скидка)", "Укукту издөө (мисалы, арзандатуу)", "Find a permission (e.g. discount)", "Erişim ara (ör. indirim)", "Huquqni qidirish (masalan, chegirma)"),
        };
        search.Classes.Add("modal-field");
        search.TextChanged += (_, _) =>
        {
            var q = (search.Text ?? "").Trim();
            var anyShown = false;
            foreach (var r in cardRefresh)
                anyShown |= r(q);
            noResults.IsVisible = q.Length > 0 && !anyShown;
        };

        var tplRow = new WrapPanel { Orientation = Orientation.Horizontal };
        tplRow.Children.Add(new TextBlock
        {
            Text = Tr.T("Шаблон роли:", "Ролдун үлгүсү:", "Role template:", "Rol şablonu:", "Rol shabloni:"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6), FontSize = 13,
        });
        foreach (var (name, label, keys) in Templates(extraKeys))
        {
            var b = SectionCards.Secondary(label);
            b.Name = "Template_" + name;
            b.Margin = new Thickness(0, 0, 8, 6);
            var keySet = keys;
            b.Click += (_, _) =>
            {
                foreach (var kv in boxes.ByKey)
                    kv.Value.IsChecked = keySet.Contains(kv.Key);
            };
            tplRow.Children.Add(b);
        }

        var scroll = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 12, Children = { new StackPanel { Spacing = 8, Children = { search, tplRow } }, panel } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        return (scroll, boxes);
    }

    /// <summary>2026-11-19: шаблоны ролей — наборы галочек по существующим правам (владелец потом правит руками).</summary>
    private static IEnumerable<(string Name, string Label, HashSet<string> Keys)> Templates(List<string> extraKeys)
    {
        var cashier = new HashSet<string> { "sale", "clients", "cashier", "shifts", "use_pos" };
        var senior = new HashSet<string>(cashier) { "products", "discount", "delcart", "empreturn", "label", "view_debts" };
        var warehouse = new HashSet<string> { "products", "brand", "procurement", "supplier", "label", "scales", "document", "edit_products", "edit_procurement" };
        var admin = new HashSet<string>(extraKeys)
        {
            "cashbox", "analytics", "products", "sale", "clients", "brand", "employees", "settings", "procurement", "supplier",
            "cashier", "shifts", "document", "discount", "editprice", "delcart", "empreturn", "label", "scales", "whatsapp", "telegram", "instagram", "documents",
        };
        yield return ("Cashier", Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"), cashier);
        yield return ("SeniorCashier", Tr.T("Старший кассир", "Улук кассир", "Senior cashier", "Kıdemli kasiyer", "Katta kassir"), senior);
        yield return ("Warehouse", Tr.T("Менеджер склада", "Склад менеджери", "Warehouse manager", "Depo yöneticisi", "Ombor menejeri"), warehouse);
        yield return ("Admin", Tr.T("Администратор", "Администратор", "Administrator", "Yönetici", "Administrator"), admin);
    }

    private static bool? ReadExtra(Boxes b, string key) => b.Extra.TryGetValue(key, out var c) ? c.IsChecked == true : null;

    public static EmployeeAccessFlags Read(Boxes b) => new()
    {
        CanUsePos = ReadExtra(b, "use_pos"),
        CanSellBelowCost = ReadExtra(b, "sell_below_cost"),
        SellBelowCostShown = b.Extra.ContainsKey("sell_below_cost"), // 2026-11-13
        CanViewSalary = ReadExtra(b, "view_salary"),
        CanEditSalary = ReadExtra(b, "edit_salary"),
        CanEditEmployees = ReadExtra(b, "edit_employees"),
        CanEditProducts = ReadExtra(b, "edit_products"),
        CanEditClients = ReadExtra(b, "edit_clients"),
        CanEditCashbox = ReadExtra(b, "edit_cashbox"),
        CanEditSettings = ReadExtra(b, "edit_settings"),
        CanEditProcurement = ReadExtra(b, "edit_procurement"),
        CanEditDocuments = ReadExtra(b, "edit_documents"),
        CanViewDebts = ReadExtra(b, "view_debts"),
        CanEditPurchaseDocuments = ReadExtra(b, "edit_purchase_documents"),
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
