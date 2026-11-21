using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.Services;

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

    /// <summary>Условия цены на странице «подробно» (вместо прежних «Бесплатно» и «через поддержку»).</summary>
    internal static string Terms(string id)
    {
        var c = For(id, false);
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
        if (snap is null)
            return;
        var profileNames = snap.Profiles.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var info in snap.Catalog.Where(i => !ModuleIds.WithOwnCard.Contains(i.Id, StringComparer.OrdinalIgnoreCase)))
        {
            var on = CompanyModules.IsOn(info.Id, false);
            var c = For(info.Id, on);
            var where = string.Join(", ", info.DefaultIn.Select(d => profileNames.TryGetValue(d, out var n) ? n : d));
            var features = new List<string>
            {
                T("Включается и выключается здесь — программа перестраивается сразу, без перезапуска", "Ушул жерден күйгүзүлөт жана өчүрүлөт — программа дароо кайра курулат, кайра ачуунун кереги жок",
                    "Turn it on and off here — the program rebuilds at once, no restart", "Burada açılıp kapatılır — program hemen yeniden kurulur, yeniden başlatma gerekmez", "Shu yerda yoqiladi va o'chiriladi — dastur darhol qayta quriladi, qayta ishga tushirish shart emas"),
            };
            if (where.Length > 0)
                features.Add(T($"Входит в сферы: {where}", $"Чөйрөлөргө кирет: {where}", $"Included in spheres: {where}", $"Dahil olduğu alanlar: {where}", $"Kiradigan sohalar: {where}"));
            items.Add(new MarketplaceItem
            {
                Id = "module:" + info.Id,
                IsModule = true,
                Kind = MarketplaceKind.Feature,
                Title = info.Name,
                ShortDescription = T("Модуль магазина: включается и выключается здесь", "Дүкөн модулу: ушул жерден күйгүзүлөт жана өчүрүлөт", "A shop module: turn it on and off here", "Mağaza modülü: burada açılıp kapatılır", "Do'kon moduli: shu yerda yoqiladi va o'chiriladi"),
                LongDescription = T($"Функция «{info.Name}» из набора модулей. Сфера магазина включает часть модулей сама; любой другой модуль можно добавить, а ненужный — убрать. Включают и выключают владелец и администратор.",
                    $"«{info.Name}» функциясы модулдар топтомунан. Дүкөндүн чөйрөсү модулдардын бир бөлүгүн өзү күйгүзөт; башка модулду кошсо, кереги жокту алып салса болот. Күйгүзүүнү жана өчүрүүнү ээси жана администратор жасайт.",
                    $"The “{info.Name}” feature from the module set. The shop's sphere turns some modules on by itself; any other module can be added, and one you do not need can be removed. The owner and administrators turn modules on and off.",
                    $"Modül setinden «{info.Name}» özelliği. Mağaza alanı bazı modülleri kendisi açar; başka bir modül eklenebilir, gereksiz olan çıkarılabilir. Modülleri sahip ve yöneticiler açıp kapatır.",
                    $"Modullar to'plamidan «{info.Name}» funksiyasi. Do'kon sohasi ba'zi modullarni o'zi yoqadi; boshqa modulni qo'shish, keraksizini olib tashlash mumkin. Modullarni egasi va administrator yoqadi va o'chiradi."),
                IconGlyph = "🧩",
                IconColor = "#0EA5E9",
                Price = c.Price,
                Status = on ? MarketplaceStatus.Installed : MarketplaceStatus.Available,
                StatusText = StatusText(c),
                Version = version,
                Tags = [T("Модуль", "Модуль", "Module", "Modül", "Modul")],
                Features = features,
                Requirements = [T("Включает и выключает владелец или администратор компании", "Күйгүзүүнү жана өчүрүүнү компаниянын ээси же администратору жасайт", "Turned on and off by the company owner or an administrator", "Şirket sahibi veya yönetici açıp kapatır", "Kompaniya egasi yoki administrator yoqadi va o'chiradi")],
                Note = Terms(info.Id),
                PriceTerms = Terms(info.Id),
                Primary = on ? null : new MarketplaceAction(EnableLabel, () => run("module-on:" + info.Id)),
                Secondary = Secondary(info.Id, c, on, run) ?? [],
            });
        }
    }
}
