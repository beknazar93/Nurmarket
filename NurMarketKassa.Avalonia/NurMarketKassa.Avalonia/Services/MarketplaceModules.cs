using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-21 (ответ Умара 20.11, В.16 / В.23; решение «цены только серверные»): карточки Маркетплейса, которые включаются и выключаются как модули компании на сервере
// (GET/PUT users/company/modules/, DELETE users/company/tariff/modules/{code}/). Цена — «N сом/мес» из GET public/modules/ (разовой платы нет); модуль профиля сферы — «Входит в вашу сферу».
// Своя карточка и окно — у Сметы, Проката, Ремонта и Trade-in (MarketplaceCatalog); остальные модули сервера получают общую карточку «Модуль» (Append).
internal static class MarketplaceModules
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Состояние карточки модуля. ServerMode — сервер знает такой модуль (иначе карточка работает по-старому: локальный признак или ключ).</summary>
    internal sealed record Card(bool ServerMode, bool On, bool Included, decimal? Monthly, int TrialDays = 0, bool OnTrial = false, DateTimeOffset? TrialUntil = null, bool TrialUsed = false)
    {
        /// <summary>2026-11-21 (ответ Умара 21.11): «Попробовать 7 дней» — у выключенного модуля сервера с trial_days > 0, если проба ещё не использована (сервер ответил бы 409 trial_used).</summary>
        public bool TrialAvailable => ServerMode && !On && !Included && TrialDays > 0 && !TrialUsed;

        /// <summary>Цена к показу: у модуля сферы и без ответа сервера цены нет (цену не придумываем).</summary>
        public MarketplacePrice? Price => Included || Monthly is null ? null : new MarketplacePrice(Monthly, MarketplacePricePeriod.PerMonth);
    }

    internal static Card For(string id, bool on)
    {
        var snap = CompanyModules.Snapshot;
        var server = snap is not null && snap.Knows(id);
        var onTrial = CompanyModules.IsOnTrial(id, out var until);
        return new Card(server, on, server && CompanyModules.IncludedInProfile(id), PublicModules.PriceOf(id), PublicModules.TrialDaysOf(id), on && onTrial, until, server && CompanyModules.IsTrialUsed(id));
    }

    /// <summary>Строка состояния: «Включено» / «Входит в вашу сферу» / «100 сом/мес» / «Не включено».</summary>
    internal static string StatusText(Card c) =>
        c.On && c.OnTrial
            ? c.TrialUntil is { } until
                ? T($"Проба до {Day(until)}", $"Сыноо {Day(until)} чейин", $"Trial until {Day(until)}", $"Deneme: {Day(until)} tarihine kadar", $"Sinov {Day(until)} gacha")
                : T("Пробный период", "Сыноо мөөнөтү", "Trial period", "Deneme süresi", "Sinov muddati")
            : c.On
            ? c.Included
                ? T("Входит в вашу сферу", "Сиздин чөйрөгө кирет", "Included in your sphere", "Alanınıza dahil", "Sohangizga kiradi")
                : T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan")
            : c.Price is { } p
                ? p.Label
                : T("Не включено", "Күйгүзүлгөн эмес", "Not enabled", "Etkin değil", "Yoqilmagan");

    private static string Day(DateTimeOffset d) => d.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>«Попробовать 7 дней» — второе действие выключенного модуля, у которого сервер даёт пробный период и он ещё не использован.</summary>
    internal static MarketplaceAction TrialAction(string id, int days, Action<string> run) =>
        new(T($"Попробовать {days} дн.", $"{days} күн сынап көрүү", $"Try for {days} days", $"{days} gün dene", $"{days} kun sinab ko'rish"), () => run("module-trial:" + id));

    /// <summary>Вторые действия карточки модуля: включён и его можно выключить — «Выключить»; выключен и есть проба — «Попробовать N дней»; иначе null (пусто).</summary>
    internal static MarketplaceAction[]? Secondary(string id, Card c, bool canDisable, Action<string> run) =>
        c.On && canDisable ? [DisableAction(id, run)]
        : c.TrialAvailable ? [TrialAction(id, c.TrialDays, run)]
        : null;

    /// <summary>«Включить» — действие карточки, когда модуль выключен.</summary>
    internal static string EnableLabel => T("Включить", "Күйгүзүү", "Turn on", "Aç", "Yoqish");

    /// <summary>«Выключить» — второе действие у включённого модуля (владелец / администратор).</summary>
    internal static MarketplaceAction DisableAction(string id, Action<string> run) =>
        new(T("Выключить", "Өчүрүү", "Turn off", "Kapat", "O'chirish"), () => run("module-off:" + id));

    /// <summary>2026-11-26: «Выход мяса при приходе» — местный бесплатный модуль, пока сервер его не знает (потом станет обычным серверным).</summary>
    internal static string CarcassYieldTitle => T("Выход мяса при приходе", "Кабыл алууда эттин чыгымы", "Meat yield on receiving", "Girişte et verimi", "Qabulda go'sht chiqishi");

    /// <summary>2026-11-26: название «Статус ремонта на витрине» (пока сервер модуль не присылает; потом название придёт с сервера).</summary>
    internal static string RepairStatusTitle => T("Статус ремонта на витрине", "Оңдоонун абалы витринада", "Repair status on the storefront", "Vitrinde tamir durumu", "Vitrinada ta'mir holati");

    /// <summary>Серверный признак, который включает «Статус ремонта на витрине».</summary>
    internal const string RepairStatusFlag = "repair_status_public";

    /// <summary>2026-11-26 (Умар: «привязано к Ремонту»): отдельного модуля у сервера нет — признак repair_status_public приходит в GET api/main/capabilities/ (features), когда у компании
    /// включён модуль «Ремонт». Есть признак — карточка «Включено», отдельной платы и переключателя нет.</summary>
    internal static bool RepairStatusActive => ServerCapabilities.Has(RepairStatusFlag);

    private static bool IsRepairStatus(string id) => string.Equals(id, ModuleIds.RepairStatusSite, StringComparison.OrdinalIgnoreCase);

    /// <summary>Реестр модулей получен, а «Ремонт» выключен: статус на витрине заработает, когда владелец включит «Ремонт».</summary>
    internal static bool RepairStatusNeedsRepair(string id) =>
        IsRepairStatus(id) && !RepairStatusActive && CompanyModules.Snapshot is not null && !CompanyModules.IsOn(ModuleIds.Repair, false);

    /// <summary>«Статус ремонта на витрине» ещё «Скоро»: «Ремонт» включён (или реестра нет), а сервер признак repair_status_public не прислал.</summary>
    internal static bool RepairStatusSoon(string id) => IsRepairStatus(id) && !RepairStatusActive && !RepairStatusNeedsRepair(id);

    internal static string RepairStatusLinkedNote() => T("Привязано к модулю «Ремонт»: работает, пока он включён, отдельной платы нет. Страница статуса — на вашей онлайн-витрине.", "«Оңдоо» модулуна байланган: ал күйгүзүлгөндө иштейт, өзүнчө төлөм жок. Абал барагы — онлайн-витринаңызда.",
        "Tied to the “Repair” module: works while it is on, no separate fee. The status page is on your online storefront.", "«Tamir» modülüne bağlı: o açıkken çalışır, ayrı ücret yok. Durum sayfası çevrimiçi vitrininizde.", "«Ta'mir» moduliga bog'langan: u yoqilganda ishlaydi, alohida to'lov yo'q. Holat sahifasi onlayn vitrinangizda.");

    internal static string RepairStatusNeedsRepairNote() => T("Включите модуль «Ремонт» — статус ремонта на витрине заработает вместе с ним, без доплаты.", "«Оңдоо» модулун күйгүзүңүз — витринада оңдоонун абалы аны менен бирге иштейт, кошумча төлөмсүз.",
        "Turn on the “Repair” module — the repair status on the storefront will work with it, at no extra charge.", "«Tamir» modülünü açın — vitrinde tamir durumu onunla birlikte çalışır, ek ücret yok.", "«Ta'mir» modulini yoqing — vitrinada ta'mir holati u bilan birga ishlaydi, qo'shimcha to'lovsiz.");

    internal static string RepairStatusSoonNote() => T("Скоро: ждём сервер NurCRM. Работает только при включённом модуле «Ремонт» и онлайн-витрине.", "Жакында: NurCRM серверин күтөбүз. «Оңдоо» модулу жана онлайн-витрина күйгүзүлгөндө гана иштейт.",
        "Coming soon: waiting for the NurCRM server. Works only with the “Repair” module turned on and an online storefront.", "Yakında: NurCRM sunucusunu bekliyoruz. Yalnızca «Tamir» modülü ve çevrimiçi vitrin açıkken çalışır.", "Tez orada: NurCRM serverini kutmoqdamiz. Faqat «Ta'mir» moduli va onlayn vitrina yoqilganda ishlaydi.");

    internal static bool IsLocalFree(string id, Card c) => id == ModuleIds.CarcassYield && !c.ServerMode;

    /// <summary>Условия цены на странице «подробно» (вместо прежних «Бесплатно» и «через поддержку»).</summary>
    internal static string Terms(string id)
    {
        var c = For(id, false);
        if (IsLocalFree(id, c))
            return T("Бесплатно: включается и выключается здесь, цену сервер не задаёт. Выход запоминается на этой кассе.", "Бекер: ушул жерден күйгүзүлөт жана өчүрүлөт, баасын сервер коёт эмес. Чыгым ушул кассада эсте калат.",
                "Free: turn it on and off here, the server sets no price. The yield is remembered on this till.", "Ücretsiz: burada açılıp kapatılır, fiyatı sunucu belirlemez. Verim bu kasada hatırlanır.", "Bepul: shu yerda yoqiladi va o'chiriladi, narxni server belgilamaydi. Chiqish shu kassada eslab qolinadi.");
        if (c.ServerMode && c.Included)
            return T("Входит в вашу сферу — без доплаты. Модуль можно выключить здесь, а потом включить снова.",
                "Сиздин чөйрөгө кирет — кошумча төлөмсүз. Модулду ушул жерде өчүрүп, кийин кайра күйгүзсө болот.",
                "Included in your sphere — no extra charge. You can turn the module off here and on again later.",
                "Alanınıza dahil — ek ücret yok. Modülü burada kapatıp daha sonra tekrar açabilirsiniz.",
                "Sohangizga kiradi — qo'shimcha to'lovsiz. Modulni shu yerda o'chirib, keyin qayta yoqish mumkin.");
        if (c.Monthly is { } m)
        {
            var price = new MarketplacePrice(m, MarketplacePricePeriod.PerMonth).Label;
            return T($"{price}, цена с сервера. Включается и выключается здесь, разовой платы нет.",
                $"{price}, баа серверден. Ушул жерден күйгүзүлөт жана өчүрүлөт, бир жолку төлөм жок.",
                $"{price}, the price comes from the server. Turn it on and off here, no one-time fee.",
                $"{price}, fiyat sunucudan gelir. Burada açılıp kapatılır, tek seferlik ücret yok.",
                $"{price}, narx serverdan. Shu yerda yoqiladi va o'chiriladi, bir martalik to'lov yo'q.");
        }
        return T("Цену модуля задаёт сервер (в месяц) — она появится здесь, как только загрузится. Включается и выключается здесь, разовой платы нет.",
            "Модулдун баасын сервер коёт (айына) — жүктөлгөндө бул жерде пайда болот. Ушул жерден күйгүзүлөт жана өчүрүлөт, бир жолку төлөм жок.",
            "The module price is set by the server (per month) — it appears here once loaded. Turn it on and off here, no one-time fee.",
            "Modül fiyatını sunucu belirler (aylık) — yüklenince burada görünür. Burada açılıp kapatılır, tek seferlik ücret yok.",
            "Modul narxini server belgilaydi (oyiga) — yuklangach shu yerda paydo bo'ladi. Shu yerda yoqiladi va o'chiriladi, bir martalik to'lov yo'q.");
    }

    /// <summary>Общие карточки «Модуль» для модулей сервера, у которых нет своей карточки: включить / выключить, цена с сервера. Только когда реестр модулей получен.</summary>
    internal static void Append(IList<MarketplaceItem> items, Action<string> run, string version)
    {
        var snap = CompanyModules.Snapshot;
        // 2026-11-26 («Выход мяса при приходе», просьба владельца): сервер модуль carcass_yield пока не знает — карточка есть всегда и работает по местному признаку (бесплатно);
        // когда сервер модуль заведёт, он придёт в catalog[] и карточка станет обычной серверной (цена, проба, «Выключить»).
        var catalog = snap?.Catalog.ToList() ?? [];
        if (!catalog.Any(i => string.Equals(i.Id, ModuleIds.CarcassYield, StringComparison.OrdinalIgnoreCase)))
            catalog.Add(new ModuleInfo(ModuleIds.CarcassYield, CarcassYieldTitle, "warehouse", []));
        // 2026-11-26: «Статус ремонта на витрине» — отдельного модуля на сервере нет, он привязан к «Ремонту» (признак repair_status_public в capabilities): карточка есть всегда — «Включено», «Нужен Ремонт» или «Скоро» (см. RepairStatusSoon).
        if (!catalog.Any(i => string.Equals(i.Id, ModuleIds.RepairStatusSite, StringComparison.OrdinalIgnoreCase)))
            catalog.Add(new ModuleInfo(ModuleIds.RepairStatusSite, RepairStatusTitle, "electronics", ["electronics"]));
        var profileNames = snap?.Profiles.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in catalog.Where(i => !ModuleIds.WithOwnCard.Contains(i.Id, StringComparer.OrdinalIgnoreCase)))
        {
            var soon = RepairStatusSoon(info.Id);
            var linked = IsRepairStatus(info.Id); // 2026-11-26: «Статус ремонта на витрине» привязан к «Ремонту» — своей цены и переключателя нет
            var linkedOn = linked && RepairStatusActive;
            var needRepair = linked && RepairStatusNeedsRepair(info.Id);
            var on = linkedOn || (!soon && info.Id == ModuleIds.CarcassYield ? CarcassYieldFeature.IsAvailable : CompanyModules.IsOn(info.Id, false));
            var c = For(info.Id, on);
            var where = string.Join(", ", info.DefaultIn.Select(d => profileNames.TryGetValue(d, out var n) ? n : d));
            var features = new List<string>
            {
                T("Включается и выключается здесь — программа перестраивается сразу, без перезапуска", "Ушул жерден күйгүзүлөт жана өчүрүлөт — программа дароо кайра курулат, кайра ачуунун кереги жок",
                    "Turn it on and off here — the program rebuilds at once, no restart", "Burada açılıp kapatılır — program hemen yeniden kurulur, yeniden başlatma gerekmez", "Shu yerda yoqiladi va o'chiriladi — dastur darhol qayta quriladi, qayta ishga tushirish shart emas"),
            };
            if (where.Length > 0)
                features.Add(T($"Входит в сферы: {where}", $"Чөйрөлөргө кирет: {where}", $"Included in spheres: {where}", $"Dahil olduğu alanlar: {where}", $"Kiradigan sohalar: {where}"));
            // 2026-11-22: у каждого известного модуля — свой значок, цвет и описание (MarketplaceModuleContent); шаблон ниже — только запасной для модулей без записи.
            var text = MarketplaceModuleContent.TextOf(info.Id);
            items.Add(new MarketplaceItem
            {
                Id = "module:" + info.Id,
                IsModule = true,
                Kind = MarketplaceKind.Feature,
                Title = info.Name,
                IconKey = MarketplaceModuleContent.IconKeyOf(info.Id),
                Tagline = text?.Short ?? "",
                Line = MarketplaceModuleContent.LineOf(info.Id) ?? "", // 2026-11-26: короткая фраза под названием плитки
                HowTo = text?.Steps ?? [],
                Audience = text?.Audience ?? [],
                ShortDescription = text?.Short ?? MarketplaceModuleContent.LineOf(info.Id) /* 2026-11-26: описание сервера — запасное, пока нашей записи нет */ ?? T("Модуль магазина: включается и выключается здесь", "Дүкөн модулу: ушул жерден күйгүзүлөт жана өчүрүлөт", "A shop module: turn it on and off here", "Mağaza modülü: burada açılıp kapatılır", "Do'kon moduli: shu yerda yoqiladi va o'chiriladi"),
                LongDescription = text?.What ?? MarketplaceModuleContent.LineOf(info.Id) ?? T($"Функция «{info.Name}» из набора модулей. Сфера магазина включает часть модулей сама; любой другой модуль можно добавить, а ненужный — убрать. Включают и выключают владелец и администратор.",
                    $"«{info.Name}» функциясы модулдар топтомунан. Дүкөндүн чөйрөсү модулдардын бир бөлүгүн өзү күйгүзөт; башка модулду кошсо, кереги жокту алып салса болот. Күйгүзүүнү жана өчүрүүнү ээси жана администратор жасайт.",
                    $"The “{info.Name}” feature from the module set. The shop's sphere turns some modules on by itself; any other module can be added, and one you do not need can be removed. The owner and administrators turn modules on and off.",
                    $"Modül setinden «{info.Name}» özelliği. Mağaza alanı bazı modülleri kendisi açar; başka bir modül eklenebilir, gereksiz olan çıkarılabilir. Modülleri sahip ve yöneticiler açıp kapatır.",
                    $"Modullar to'plamidan «{info.Name}» funksiyasi. Do'kon sohasi ba'zi modullarni o'zi yoqadi; boshqa modulni qo'shish, keraksizini olib tashlash mumkin. Modullarni egasi va administrator yoqadi va o'chiradi."),
                IconGlyph = "➕", // 2026-11-22: запасной глиф вместо «пазла» (виден только если значок MarketplaceIcons не найдётся)
                IconColor = MarketplaceModuleContent.ColorOf(info.Id),
                Price = soon || linked ? null : c.Price,
                Status = soon ? MarketplaceStatus.ComingSoon : on ? MarketplaceStatus.Installed : MarketplaceStatus.Available,
                StatusText = soon ? T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada") : linkedOn ? T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan") : needRepair ? T("Нужен «Ремонт»", "«Оңдоо» керек", "Needs “Repair”", "«Tamir» gerekli", "«Ta'mir» kerak") : IsLocalFree(info.Id, c) ? (on ? T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan") : T("Бесплатно", "Бекер", "Free", "Ücretsiz", "Bepul")) : StatusText(c), // 2026-11-26: местный бесплатный модуль — «Бесплатно», а не «Не включено»
                Version = version,
                Tags = [T("Модуль", "Модуль", "Module", "Modül", "Modul")],
                Features = features,
                Requirements = [T("Включает и выключает владелец или администратор компании", "Күйгүзүүнү жана өчүрүүнү компаниянын ээси же администратору жасайт", "Turned on and off by the company owner or an administrator", "Şirket sahibi veya yönetici açıp kapatır", "Kompaniya egasi yoki administrator yoqadi va o'chiradi")],
                Note = soon ? RepairStatusSoonNote() : linkedOn ? RepairStatusLinkedNote() : needRepair ? RepairStatusNeedsRepairNote() : Terms(info.Id),
                PriceTerms = soon ? "" : linked ? RepairStatusLinkedNote() : Terms(info.Id),
                Primary = on || soon ? null : needRepair ? new MarketplaceAction(T("Включить «Ремонт»", "«Оңдоону» күйгүзүү", "Turn on “Repair”", "«Tamir»i aç", "«Ta'mir»ni yoqish"), () => run("module-on:" + ModuleIds.Repair)) : new MarketplaceAction(EnableLabel, () => run("module-on:" + info.Id)),
                Secondary = soon || linked ? [] : Secondary(info.Id, c, on, run) ?? [],
                RelatedIds = info.Id == ModuleIds.RepairStatusSite ? [ModuleIds.Repair] : [],
            });
        }
    }
}
