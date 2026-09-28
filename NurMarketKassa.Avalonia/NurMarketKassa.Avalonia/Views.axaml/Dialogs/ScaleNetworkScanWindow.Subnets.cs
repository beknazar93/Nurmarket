using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: просьба владельца «проверка всех подсетей при поиске ip адреса весов даже если
/// комп стоит статичный ip адрес». Весы часто стоят с заводским адресом в ДРУГОЙ подсети
/// (Rongta 192.168.1.87, Штрих-ПРИНТ 192.168.0.202), а ping туда уходит на шлюз и до весов в
/// том же кабеле не доходит. В этом же окне добавлено:
/// • широковещательный UDP-запрос весам Штрих-ПРИНТ (всегда, после основного поиска);
/// • в списке «Сеть» — типовые заводские подсети весов, «все подряд» и «своя подсеть или диапазон»;
/// • режим «временно добавить компьютеру адрес в этой подсети» (с подтверждением, одно окно UAC,
///   адрес удаляется всегда — см. ScaleTempAddressSession);
/// • колонка «Как найдено» и подсказка для весов из чужой подсети с готовым адресом для
///   «Использовать для…».
/// </summary>
public partial class ScaleNetworkScanWindow
{
    private enum TargetKind { Local, Separator, AllPresets, Preset, Custom }

    private sealed record ScanTarget(TargetKind Kind, LocalSubnet? Local = null, ScaleSubnetPreset? Preset = null);

    private readonly List<ScanTarget> _targets = new();
    private IReadOnlyList<ScaleScanAdapter> _adapters = Array.Empty<ScaleScanAdapter>();
    private readonly List<string> _scanNotes = new();
    private string? _progressPrefix;
    /// <summary>Пояснение про DHCP-адаптер (для окна подтверждения и итога), без общей части.</summary>
    private string? _dhcpNote;

    private ScanTarget? SelectedTarget =>
        SubnetBox.SelectedIndex >= 0 && SubnetBox.SelectedIndex < _targets.Count ? _targets[SubnetBox.SelectedIndex] : null;

    // ------------------------------------------------------------------ список «Сеть»

    /// <summary>Строит пункты списка: сначала подсети адаптеров ПК, потом «чужие».</summary>
    private void FillTargets()
    {
        _targets.Clear();
        SubnetBox.Items.Clear();
        foreach (var s in _subnets)
        {
            _targets.Add(new ScanTarget(TargetKind.Local, Local: s));
            SubnetBox.Items.Add(new ComboBoxItem
            {
                Content = $"{s.AdapterName} — {s.LocalAddress}/{s.PrefixLength} · "
                          + L("адреса ", "даректер ", "addresses ", "adresler ", "manzillar ") + s.RangeText
                          + $" ({s.HostCount})",
            });
        }

        if (_subnets.Count == 0)
            return; // без сети искать негде — и в чужих подсетях тоже

        _targets.Add(new ScanTarget(TargetKind.Separator));
        SubnetBox.Items.Add(new ComboBoxItem
        {
            Content = L("— Другие подсети (весы с заводским адресом) —", "— Башка подсеттер (заводдук даректеги тараза) —", "— Other subnets (scales with a factory address) —", "— Diğer alt ağlar (fabrika adresli tartılar) —", "— Boshqa quyi tarmoqlar (zavod manzilli tarozilar) —"),
            IsEnabled = false,
        });

        _targets.Add(new ScanTarget(TargetKind.AllPresets));
        SubnetBox.Items.Add(new ComboBoxItem
        {
            Content = L($"Все типовые подсети весов подряд ({ScaleNetworkScanner.TypicalScaleSubnets.Count} × 254 адреса)",
                        $"Таразалардын бардык типтүү подсеттери катары менен ({ScaleNetworkScanner.TypicalScaleSubnets.Count} × 254 дарек)",
                        $"All typical scale subnets one by one ({ScaleNetworkScanner.TypicalScaleSubnets.Count} × 254 addresses)",
                        $"Tüm tipik tartı alt ağları sırayla ({ScaleNetworkScanner.TypicalScaleSubnets.Count} × 254 adres)",
                        $"Barcha odatiy tarozi quyi tarmoqlari ketma-ket ({ScaleNetworkScanner.TypicalScaleSubnets.Count} × 254 manzil)"),
        });

        foreach (var p in ScaleNetworkScanner.TypicalScaleSubnets)
        {
            _targets.Add(new ScanTarget(TargetKind.Preset, Preset: p));
            SubnetBox.Items.Add(new ComboBoxItem { Content = $"{p.Range.Label} — {PresetShort(p.Key)}" });
        }

        _targets.Add(new ScanTarget(TargetKind.Custom));
        SubnetBox.Items.Add(new ComboBoxItem
        {
            Content = L("Своя подсеть или диапазон…", "Өз подсетиңиз же диапазон…", "Your own subnet or range…", "Kendi alt ağınız veya aralık…", "O‘z quyi tarmog‘ingiz yoki oraliq…"),
        });
    }

    private static string PresetShort(string key) => key switch
    {
        "shtrikh" => L("Штрих-ПРИНТ (заводской 192.168.0.202), роутеры TP-Link/D-Link", "Штрих-ПРИНТ (заводдук 192.168.0.202), TP-Link/D-Link роутерлери", "Shtrih-PRINT (factory 192.168.0.202), TP-Link/D-Link routers", "Shtrih-PRINT (fabrika 192.168.0.202), TP-Link/D-Link yönlendiriciler", "Shtrix-PRINT (zavod 192.168.0.202), TP-Link/D-Link routerlari"),
        "rongta" => L("Rongta (заводской 192.168.1.87), многие роутеры", "Rongta (заводдук 192.168.1.87), көп роутерлер", "Rongta (factory 192.168.1.87), many routers", "Rongta (fabrika 192.168.1.87), birçok yönlendirici", "Rongta (zavod 192.168.1.87), ko‘p routerlar"),
        "huawei" => L("4G-модемы Huawei", "Huawei 4G-модемдери", "Huawei 4G modems", "Huawei 4G modemleri", "Huawei 4G modemlari"),
        "mikrotik" => L("роутеры MikroTik", "MikroTik роутерлери", "MikroTik routers", "MikroTik yönlendiriciler", "MikroTik routerlari"),
        "gpon" => L("оптические терминалы провайдера (GPON)", "провайдердин оптикалык терминалдары (GPON)", "provider fibre terminals (GPON)", "sağlayıcı fiber terminalleri (GPON)", "provayder optik terminallari (GPON)"),
        "xiaomi" => L("роутеры Xiaomi", "Xiaomi роутерлери", "Xiaomi routers", "Xiaomi yönlendiriciler", "Xiaomi routerlari"),
        _ => L("часть роутеров и точек доступа", "айрым роутерлер жана кирүү чекиттери", "some routers and access points", "bazı yönlendiriciler ve erişim noktaları", "ba’zi routerlar va kirish nuqtalari"),
    };

    /// <summary>Откуда взялась подсеть — объяснение для владельца.</summary>
    private static string PresetWhy(ScaleSubnetPreset p) => p.Key switch
    {
        "shtrikh" => L($"{p.Range.Label}: у весов ШТРИХ-ПРИНТ заводской адрес 192.168.0.202 и UDP-порт 1111 (руководство администратора Штрих-Принт 4.5, п. 1.6.1.1). В этой же сети роутеры TP-Link и D-Link с адресом 192.168.0.1.",
                       $"{p.Range.Label}: ШТРИХ-ПРИНТ таразасынын заводдук дареги 192.168.0.202, UDP-порту 1111 (Штрих-Принт 4.5 администратор колдонмосу, 1.6.1.1-п.). Ушул эле тармакта 192.168.0.1 дарегиндеги TP-Link жана D-Link роутерлери.",
                       $"{p.Range.Label}: Shtrih-PRINT scales ship with address 192.168.0.202 and UDP port 1111 (Shtrih-Print 4.5 administrator manual, 1.6.1.1). TP-Link and D-Link routers with 192.168.0.1 use the same network.",
                       $"{p.Range.Label}: Shtrih-PRINT tartıların fabrika adresi 192.168.0.202, UDP portu 1111 (Shtrih-Print 4.5 yönetici kılavuzu, 1.6.1.1). Aynı ağda 192.168.0.1 adresli TP-Link ve D-Link yönlendiriciler.",
                       $"{p.Range.Label}: Shtrix-PRINT tarozisining zavod manzili 192.168.0.202, UDP porti 1111 (Shtrix-Print 4.5 administrator qo‘llanmasi, 1.6.1.1). Shu tarmoqda 192.168.0.1 manzilli TP-Link va D-Link routerlari."),
        "rongta" => L($"{p.Range.Label}: у весов Rongta RLS заводской адрес 192.168.1.87 (Label Scale User Manual). В этой же сети многие роутеры с адресом 192.168.1.1 — весы с постоянным адресом, настроенные в сети такого роутера, остаются здесь.",
                      $"{p.Range.Label}: Rongta RLS таразасынын заводдук дареги 192.168.1.87 (Label Scale User Manual). Ушул эле тармакта 192.168.1.1 дарегиндеги көп роутерлер — ошондой роутердин тармагында туруктуу дарек коюлган тараза ушул жерде калат.",
                      $"{p.Range.Label}: Rongta RLS scales ship with 192.168.1.87 (Label Scale User Manual). Many routers use 192.168.1.1 — a scale given a fixed address in such a network stays here.",
                      $"{p.Range.Label}: Rongta RLS tartıların fabrika adresi 192.168.1.87 (Label Scale User Manual). Birçok yönlendirici 192.168.1.1 kullanır — böyle bir ağda sabit adres verilen tartı burada kalır.",
                      $"{p.Range.Label}: Rongta RLS tarozisining zavod manzili 192.168.1.87 (Label Scale User Manual). Ko‘p routerlar 192.168.1.1 dan foydalanadi — shunday tarmoqda doimiy manzil berilgan tarozi shu yerda qoladi."),
        "huawei" => L($"{p.Range.Label}: 4G-модемы и роутеры Huawei раздают адреса 192.168.8.x — сюда попадают весы с постоянным адресом, настроенные в сети такого модема.",
                      $"{p.Range.Label}: Huawei 4G-модемдери жана роутерлери 192.168.8.x даректерин берет — ошондой модемдин тармагында туруктуу дарек коюлган тараза ушул жерде.",
                      $"{p.Range.Label}: Huawei 4G modems and routers hand out 192.168.8.x — scales given a fixed address in such a network end up here.",
                      $"{p.Range.Label}: Huawei 4G modem ve yönlendiriciler 192.168.8.x dağıtır — böyle bir ağda sabit adres verilen tartılar burada olur.",
                      $"{p.Range.Label}: Huawei 4G modem va routerlari 192.168.8.x manzillarini beradi — shunday tarmoqda doimiy manzil berilgan tarozilar shu yerda bo‘ladi."),
        "mikrotik" => L($"{p.Range.Label}: заводская сеть роутеров MikroTik (роутер 192.168.88.1).",
                        $"{p.Range.Label}: MikroTik роутерлеринин заводдук тармагы (роутер 192.168.88.1).",
                        $"{p.Range.Label}: the factory network of MikroTik routers (router 192.168.88.1).",
                        $"{p.Range.Label}: MikroTik yönlendiricilerin fabrika ağı (yönlendirici 192.168.88.1).",
                        $"{p.Range.Label}: MikroTik routerlarining zavod tarmog‘i (router 192.168.88.1)."),
        "gpon" => L($"{p.Range.Label}: оптические терминалы провайдеров (Huawei, ZTE) — 192.168.100.1.",
                    $"{p.Range.Label}: провайдерлердин оптикалык терминалдары (Huawei, ZTE) — 192.168.100.1.",
                    $"{p.Range.Label}: provider fibre terminals (Huawei, ZTE) — 192.168.100.1.",
                    $"{p.Range.Label}: sağlayıcı fiber terminalleri (Huawei, ZTE) — 192.168.100.1.",
                    $"{p.Range.Label}: provayder optik terminallari (Huawei, ZTE) — 192.168.100.1."),
        "xiaomi" => L($"{p.Range.Label}: заводская сеть роутеров Xiaomi (роутер 192.168.31.1).",
                      $"{p.Range.Label}: Xiaomi роутерлеринин заводдук тармагы (роутер 192.168.31.1).",
                      $"{p.Range.Label}: the factory network of Xiaomi routers (router 192.168.31.1).",
                      $"{p.Range.Label}: Xiaomi yönlendiricilerin fabrika ağı (yönlendirici 192.168.31.1).",
                      $"{p.Range.Label}: Xiaomi routerlarining zavod tarmog‘i (router 192.168.31.1)."),
        _ => L($"{p.Range.Label}: такую сеть раздают некоторые роутеры и точки доступа.",
               $"{p.Range.Label}: мындай тармакты айрым роутерлер жана кирүү чекиттери берет.",
               $"{p.Range.Label}: some routers and access points use this network.",
               $"{p.Range.Label}: bazı yönlendiriciler ve erişim noktaları bu ağı kullanır.",
               $"{p.Range.Label}: bu tarmoqni ba’zi routerlar va kirish nuqtalari beradi."),
    };

    // ------------------------------------------------------------------ панель «чужой подсети»

    private void InitForeignPanel()
    {
        IntroText.Text += " " + L(
            "Весы с заводским адресом из другой подсети (например, 192.168.1.87 при компьютере 10.x.x.x) выберите в списке «Сеть» ниже «Другие подсети». Весам Штрих-ПРИНТ дополнительно отправляется широковещательный запрос.",
            "Башка подсеттин заводдук дареги бар тараза (мисалы, компьютер 10.x.x.x болсо, 192.168.1.87) — «Тармак» тизмесинен «Башка подсеттер» бөлүгүн тандаңыз. Штрих-ПРИНТ таразасына кошумча кеңири таратуучу суроо жөнөтүлөт.",
            "For a scale with a factory address from another subnet (e.g. 192.168.1.87 while this computer is 10.x.x.x) pick it in the “Network” list under “Other subnets”. Shtrih-PRINT scales also get a broadcast request.",
            "Başka alt ağdan fabrika adresli tartı için (ör. bilgisayar 10.x.x.x iken 192.168.1.87) “Ağ” listesinde “Diğer alt ağlar” altından seçin. Shtrih-PRINT tartılara ayrıca yayın isteği gönderilir.",
            "Boshqa quyi tarmoqdan zavod manzilli tarozi uchun (masalan, kompyuter 10.x.x.x bo‘lsa, 192.168.1.87) «Tarmoq» ro‘yxatidagi «Boshqa quyi tarmoqlar»dan tanlang. Shtrix-PRINT tarozilariga qo‘shimcha keng tarqatuvchi so‘rov yuboriladi.");

        CustomRangeLabel.Text = L("Подсеть или диапазон:", "Подсеть же диапазон:", "Subnet or range:", "Alt ağ veya aralık:", "Quyi tarmoq yoki oraliq:");
        TempAddressBox.Content = L(
            "На время поиска добавить компьютеру второй адрес в этой подсети (нужны права администратора — Windows спросит один раз; после поиска адрес удаляется)",
            "Издөө убагында компьютерге ушул подсетте экинчи дарек кошуу (администратор укугу керек — Windows бир жолу сурайт; издөөдөн кийин дарек өчүрүлөт)",
            "Temporarily give this computer a second address in this subnet (administrator rights needed — Windows asks once; the address is removed after the search)",
            "Arama süresince bilgisayara bu alt ağda ikinci bir adres ekle (yönetici hakları gerekir — Windows bir kez sorar; aramadan sonra adres silinir)",
            "Qidiruv davomida kompyuterga shu quyi tarmoqda ikkinchi manzil qo‘shish (administrator huquqi kerak — Windows bir marta so‘raydi; qidiruvdan keyin manzil o‘chiriladi)");
        AdapterLabel.Text = L("Адаптер:", "Адаптер:", "Adapter:", "Bağdaştırıcı:", "Adapter:");
        TempIpLabel.Text = L("Временный адрес:", "Убактылуу дарек:", "Temporary address:", "Geçici adres:", "Vaqtinchalik manzil:");

        _adapters = ScaleNetworkScanner.GetAdaptersForTempAddress();
        AdapterBox.Items.Clear();
        foreach (var a in _adapters)
        {
            AdapterBox.Items.Add(new ComboBoxItem
            {
                Content = $"{a.Name} — {a.AddressesText} · "
                          + (a.IsWireless ? "Wi-Fi" : L("кабель", "кабель", "cable", "kablo", "kabel"))
                          + (a.IsDhcp ? " · DHCP" : " · " + L("постоянный адрес", "туруктуу дарек", "static address", "sabit adres", "doimiy manzil")),
            });
        }
        if (_adapters.Count > 0)
            AdapterBox.SelectedIndex = 0;

        TempAddressBox.IsCheckedChanged += (_, _) => UpdateForeignPanel();
        AdapterBox.SelectionChanged += (_, _) => UpdateForeignPanel();
        CustomRangeBox.TextChanged += (_, _) => UpdateTempIpSuggestion();
    }

    private ScaleScanAdapter? SelectedAdapter =>
        AdapterBox.SelectedIndex >= 0 && AdapterBox.SelectedIndex < _adapters.Count ? _adapters[AdapterBox.SelectedIndex] : null;

    private void UpdateForeignPanel()
    {
        var t = SelectedTarget;
        var foreign = t is { Kind: TargetKind.AllPresets or TargetKind.Preset or TargetKind.Custom };
        ForeignPanel.IsVisible = foreign;
        if (!foreign)
            return;

        var routedNote = L(
            "Без временного адреса касса проверит эту подсеть через роутер: ответят только устройства, до которых роутер умеет доставлять пакеты. Весы в том же кабеле или коммутаторе, но с адресом из другой подсети, так не видны — для них включите временный адрес.",
            "Убактылуу дареги жок касса бул подсетти роутер аркылуу текшерет: роутер пакет жеткире алган түзмөктөр гана жооп берет. Ошол эле кабелдеги же коммутатордогу, бирок башка подсеттин дареги бар тараза мындай көрүнбөйт — ал үчүн убактылуу даректи күйгүзүңүз.",
            "Without a temporary address the till checks this subnet through the router: only devices the router can reach will answer. A scale on the same cable or switch but with an address from another subnet is not visible this way — turn on the temporary address for it.",
            "Geçici adres olmadan kasa bu alt ağı yönlendirici üzerinden kontrol eder: yalnızca yönlendiricinin ulaşabildiği cihazlar yanıt verir. Aynı kabloda veya anahtarda ama başka alt ağ adresli tartı böyle görünmez — bunun için geçici adresi açın.",
            "Vaqtinchalik manzilsiz kassa bu quyi tarmoqni router orqali tekshiradi: faqat router yetkaza oladigan qurilmalar javob beradi. Shu kabel yoki kommutatordagi, lekin boshqa quyi tarmoq manzilli tarozi bunday ko‘rinmaydi — buning uchun vaqtinchalik manzilni yoqing.");
        ForeignInfoText.Text = t!.Kind switch
        {
            TargetKind.Preset => PresetWhy(t.Preset!) + "\n" + routedNote,
            TargetKind.AllPresets => string.Join("\n", ScaleNetworkScanner.TypicalScaleSubnets.Select(PresetWhy)) + "\n" + routedNote,
            _ => L("Введите подсеть (192.168.5.0/24) или диапазон (192.168.5.10-192.168.5.60, можно 192.168.5.10-60). Не шире 1024 адресов.",
                   "Подсетти (192.168.5.0/24) же диапазонду (192.168.5.10-192.168.5.60, 192.168.5.10-60 болот) жазыңыз. 1024 даректен кенен эмес.",
                   "Enter a subnet (192.168.5.0/24) or a range (192.168.5.10-192.168.5.60, or 192.168.5.10-60). No more than 1024 addresses.",
                   "Bir alt ağ (192.168.5.0/24) veya aralık (192.168.5.10-192.168.5.60 ya da 192.168.5.10-60) girin. En fazla 1024 adres.",
                   "Quyi tarmoq (192.168.5.0/24) yoki oraliq (192.168.5.10-192.168.5.60, 192.168.5.10-60 ham bo‘ladi) kiriting. 1024 manzildan ko‘p emas.") + "\n" + routedNote,
        };
        CustomRangeRow.IsVisible = t.Kind == TargetKind.Custom;

        var temp = TempAddressBox.IsChecked == true;
        TempAddressBox.IsEnabled = _adapters.Count > 0;
        TempRow.IsVisible = temp;
        TempIpBox.IsEnabled = t.Kind != TargetKind.AllPresets;
        UpdateTempIpSuggestion();

        var notes = new List<string>();
        if (_adapters.Count == 0)
        {
            notes.Add(L("Нет подключённого проводного или беспроводного адаптера — временный адрес добавить некуда.",
                        "Туташкан зымдуу же зымсыз адаптер жок — убактылуу даректи кошо турган жер жок.",
                        "No connected wired or wireless adapter — nowhere to add a temporary address.",
                        "Bağlı kablolu veya kablosuz bağdaştırıcı yok — geçici adres eklenecek yer yok.",
                        "Ulangan simli yoki simsiz adapter yo‘q — vaqtinchalik manzil qo‘shadigan joy yo‘q."));
        }
        else if (temp)
        {
            notes.Add(L("Касса добавит адрес командой netsh с пометкой «только до перезагрузки», без шлюза (шлюз и основной адрес компьютера не меняются), проверит, что адрес свободен, найдёт весы и сразу удалит адрес — даже при ошибке или «Остановить».",
                        "Касса даректи netsh буйругу менен «кайра жүктөлгөнгө чейин гана» белгиси менен, шлюзсуз кошот (шлюз жана компьютердин негизги дареги өзгөрбөйт), дарек бош экенин текшерет, таразаны табат жана даректи дароо өчүрөт — ката же «Токтотуу» болсо да.",
                        "The till adds the address with netsh as “until reboot only”, without a gateway (the gateway and the computer's main address do not change), checks the address is free, searches, and removes the address right away — even on an error or “Stop”.",
                        "Kasa adresi netsh ile “yalnızca yeniden başlatmaya kadar”, ağ geçidi olmadan ekler (ağ geçidi ve bilgisayarın ana adresi değişmez), adresin boş olduğunu kontrol eder, arar ve adresi hemen siler — hata veya “Durdur” olsa bile.",
                        "Kassa manzilni netsh buyrug‘i bilan «faqat qayta yuklashgacha», shlyuzsiz qo‘shadi (shlyuz va kompyuterning asosiy manzili o‘zgarmaydi), manzil bo‘shligini tekshiradi, qidiradi va manzilni darhol o‘chiradi — xato yoki «To‘xtatish» bo‘lsa ham."));
            var a = SelectedAdapter;
            _dhcpNote = null;
            if (a is { IsDhcp: true })
            {
                notes.Add(_dhcpNote = ScaleTempAddressSession.DhcpCoexistenceSupported
                    ? L("Адаптер получает адрес от роутера (DHCP). Обычная команда netsh на таком адаптере выключила бы DHCP, поэтому касса на время поиска включит режим Windows «DHCP и постоянный адрес вместе», проверит, что DHCP остался включён (иначе сразу вернёт как было), и потом выключит этот режим обратно.",
                        "Адаптер даректи роутерден алат (DHCP). Кадимки netsh буйругу мындай адаптерде DHCP'ни өчүрүп салмак, ошондуктан касса издөө убагында Windows'тун «DHCP жана туруктуу дарек бирге» режимин күйгүзөт, DHCP күйүк калганын текшерет (болбосо дароо мурункудай кылат) жана андан кийин бул режимди кайра өчүрөт.",
                        "The adapter gets its address from the router (DHCP). A plain netsh command would turn DHCP off on it, so for the search the till turns on Windows' “DHCP and static address together” mode, checks DHCP stays on (otherwise it restores everything at once), and turns the mode off again afterwards.",
                        "Bağdaştırıcı adresini yönlendiriciden alıyor (DHCP). Düz netsh komutu DHCP'yi kapatırdı; bu yüzden kasa arama süresince Windows'un “DHCP ve sabit adres birlikte” modunu açar, DHCP'nin açık kaldığını kontrol eder (değilse hemen geri alır) ve sonra modu tekrar kapatır.",
                        "Adapter manzilni routerdan oladi (DHCP). Oddiy netsh buyrug‘i unda DHCP'ni o‘chirib qo‘yardi, shuning uchun kassa qidiruv davomida Windows'ning «DHCP va doimiy manzil birga» rejimini yoqadi, DHCP yoqiq qolganini tekshiradi (aks holda darhol qaytaradi) va keyin rejimni yana o‘chiradi.")
                    : L("Адаптер получает адрес от роутера (DHCP), а эта версия Windows не умеет держать DHCP и второй постоянный адрес вместе: команда netsh перевела бы адаптер на постоянный адрес. Касса этого делать не будет. Варианты: поменять адрес весов на их экране на адрес из сети компьютера или подключить весы к компьютеру с постоянным адресом.",
                        "Адаптер даректи роутерден алат (DHCP), ал эми Windows'тун бул версиясы DHCP менен экинчи туруктуу даректи бирге кармай албайт: netsh буйругу адаптерди туруктуу дарекке которуп салмак. Касса муну кылбайт. Жолдору: таразанын экранынан анын дарегин компьютердин тармагындагы дарекке алмаштыруу же таразаны туруктуу дареги бар компьютерге туташтыруу.",
                        "The adapter gets its address from the router (DHCP), and this Windows version cannot keep DHCP and a second static address together: netsh would switch the adapter to a static address. The till will not do that. Options: change the scale's address on its screen to one from the computer's network, or connect the scale to a computer with a static address.",
                        "Bağdaştırıcı adresini yönlendiriciden alıyor (DHCP) ve bu Windows sürümü DHCP ile ikinci sabit adresi birlikte tutamaz: netsh bağdaştırıcıyı sabit adrese geçirirdi. Kasa bunu yapmaz. Seçenekler: tartının adresini ekranından bilgisayarın ağındaki bir adresle değiştirmek veya tartıyı sabit adresli bir bilgisayara bağlamak.",
                        "Adapter manzilni routerdan oladi (DHCP), Windows'ning bu versiyasi esa DHCP va ikkinchi doimiy manzilni birga ushlay olmaydi: netsh adapterni doimiy manzilga o‘tkazib yuborardi. Kassa buni qilmaydi. Variantlar: tarozi manzilini uning ekranidan kompyuter tarmog‘idagi manzilga almashtirish yoki tarozini doimiy manzilli kompyuterga ulash."));
            }
        }
        TempNote.Text = string.Join("\n", notes);
        TempNote.IsVisible = notes.Count > 0;
    }

    /// <summary>Предлагает временный адрес «.250» в выбранной подсети (пользователь может поменять).</summary>
    private void UpdateTempIpSuggestion()
    {
        var t = SelectedTarget;
        if (t is null)
            return;
        if (t.Kind == TargetKind.AllPresets)
        {
            TempIpBox.Text = L("авто: .250 в каждой", "авто: ар биринде .250", "auto: .250 in each", "otomatik: her birinde .250", "avto: har birida .250");
            return;
        }
        var range = t.Kind == TargetKind.Preset ? t.Preset!.Range
            : ScaleNetworkScanner.TryParseRange(CustomRangeBox.Text, out var r, out _) ? r : null;
        if (range is null)
            return;
        var taken = ScaleNetworkScanner.AllLocalAddresses();
        TempIpBox.Text = ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(range.Network, range.Prefix, taken));
    }

    private string RangeErrorText(string? error) => error switch
    {
        "empty" => L("Введите подсеть или диапазон, например 192.168.5.0/24.", "Подсетти же диапазонду жазыңыз, мисалы 192.168.5.0/24.", "Enter a subnet or range, e.g. 192.168.5.0/24.", "Bir alt ağ veya aralık girin, ör. 192.168.5.0/24.", "Quyi tarmoq yoki oraliq kiriting, masalan 192.168.5.0/24."),
        "too_wide" => L("Слишком широкий диапазон: не больше 1024 адресов (не шире /22).", "Диапазон өтө кенен: 1024 даректен ашпасын (/22'ден кенен эмес).", "The range is too wide: at most 1024 addresses (/22).", "Aralık çok geniş: en fazla 1024 adres (/22).", "Oraliq juda keng: 1024 manzildan ko‘p emas (/22)."),
        _ => L("Не понял адрес. Примеры: 192.168.5.0/24, 192.168.5.10-192.168.5.60, 192.168.5.10-60.", "Дарек түшүнүксүз. Мисалдар: 192.168.5.0/24, 192.168.5.10-192.168.5.60, 192.168.5.10-60.", "Could not read the address. Examples: 192.168.5.0/24, 192.168.5.10-192.168.5.60, 192.168.5.10-60.", "Adres anlaşılamadı. Örnekler: 192.168.5.0/24, 192.168.5.10-192.168.5.60, 192.168.5.10-60.", "Manzil tushunarsiz. Misollar: 192.168.5.0/24, 192.168.5.10-192.168.5.60, 192.168.5.10-60."),
    };

    // ------------------------------------------------------------------ сканирование

    /// <summary>Весь поиск по нажатию «Начать поиск»: своя подсеть или чужие + широковещание.
    /// null — поиск не начат (ошибка ввода уже показана).</summary>
    private async Task<IReadOnlyList<ScaleNetworkDevice>?> RunScanAsync(ScanTarget target, int shtrikhPort, IProgress<ScaleScanProgress> progress, CancellationToken ct)
    {
        _scanNotes.Clear();
        _progressPrefix = null;
        var all = new Dictionary<uint, ScaleNetworkDevice>();

        void Merge(IEnumerable<ScaleNetworkDevice> found, ScaleFoundBy extra, string? label)
        {
            foreach (var d in found)
            {
                d.FoundBy |= extra;
                d.ScanLabel ??= label;
                if (!all.TryGetValue(d.IpNumber, out var existing))
                {
                    all[d.IpNumber] = d;
                    continue;
                }
                existing.FoundBy |= d.FoundBy;
                existing.Mac ??= d.Mac;
                existing.ShtrikhInfo ??= d.ShtrikhInfo;
                if (existing.Guess == ScaleDeviceGuess.Unknown)
                    existing.Guess = d.Guess;
            }
        }

        if (target.Kind == TargetKind.Local)
        {
            var subnet = target.Local!;
            var devices = await Task.Run(() => ScaleNetworkScanner.ScanAsync(subnet, shtrikhPort, progress, ct), ct).ConfigureAwait(true);
            Merge(devices, ScaleFoundBy.None, $"{ScaleNetworkScanner.ToIp(ScaleNetworkScanner.ToNumber(subnet.LocalAddress) & ScaleNetworkScanner.MaskOf(subnet.PrefixLength))}/{subnet.PrefixLength}");
        }
        else
        {
            var ranges = new List<ScaleScanRange>();
            switch (target.Kind)
            {
                case TargetKind.Preset:
                    ranges.Add(target.Preset!.Range);
                    break;
                case TargetKind.AllPresets:
                    ranges.AddRange(ScaleNetworkScanner.TypicalScaleSubnets.Select(p => p.Range));
                    break;
                default:
                    if (!ScaleNetworkScanner.TryParseRange(CustomRangeBox.Text, out var custom, out var error))
                    {
                        ShowResult(RangeErrorText(error), true);
                        return null;
                    }
                    ranges.Add(custom!);
                    break;
            }

            // Подсети, которые на самом деле уже сеть этого компьютера, — сканируем как свои.
            var local = new List<(ScaleScanRange Range, LocalSubnet Subnet)>();
            var foreign = new List<ScaleScanRange>();
            foreach (var r in ranges)
            {
                var overlap = ScaleNetworkScanner.LocalSubnetOverlapping(r, _subnets);
                if (overlap is not null)
                    local.Add((r, overlap));
                else
                    foreign.Add(r);
            }

            ScaleTempAddressSession? session = null;
            var tempByRange = new Dictionary<ScaleScanRange, IPAddress>();
            try
            {
                if (TempAddressBox.IsChecked == true && foreign.Count > 0)
                {
                    var prepared = await PrepareTempAddressesAsync(target, foreign, ct).ConfigureAwait(true);
                    if (prepared is null)
                        return null; // отказ в подтверждении или ошибка ввода — сообщение уже показано
                    if (prepared.Value.Session is not null)
                    {
                        session = prepared.Value.Session;
                        foreach (var kv in prepared.Value.Addresses)
                            tempByRange[kv.Key] = kv.Value;
                    }
                }

                var step = 0;
                var total = local.Count + foreign.Count;
                foreach (var (r, s) in local)
                {
                    ct.ThrowIfCancellationRequested();
                    _progressPrefix = $"[{++step}/{total}] {r.Label}";
                    var subnet = ScaleNetworkScanner.SubnetForRange(r, s.AdapterName, s.LocalAddress, s.Gateway);
                    var found = await Task.Run(() => ScaleNetworkScanner.ScanAsync(subnet, shtrikhPort, progress, ct), ct).ConfigureAwait(true);
                    Merge(found, ScaleFoundBy.None, r.Label);
                    _scanNotes.Add(L($"{r.Label} — это сеть самого компьютера ({s.LocalAddress}), проверена как своя.",
                                     $"{r.Label} — бул компьютердин өз тармагы ({s.LocalAddress}), өзүнүкүдөй текшерилди.",
                                     $"{r.Label} is this computer's own network ({s.LocalAddress}); checked as local.",
                                     $"{r.Label} bu bilgisayarın kendi ağı ({s.LocalAddress}); yerel olarak kontrol edildi.",
                                     $"{r.Label} — bu kompyuterning o‘z tarmog‘i ({s.LocalAddress}), o‘ziniki kabi tekshirildi."));
                }
                foreach (var r in foreign)
                {
                    ct.ThrowIfCancellationRequested();
                    _progressPrefix = $"[{++step}/{total}] {r.Label}";
                    var hasTemp = tempByRange.TryGetValue(r, out var tempIp);
                    var adapterName = hasTemp ? SelectedAdapter?.Name ?? "" : "";
                    var subnet = ScaleNetworkScanner.SubnetForRange(r, adapterName, hasTemp ? tempIp! : IPAddress.Any);
                    var found = await Task.Run(() => ScaleNetworkScanner.ScanAsync(subnet, shtrikhPort, progress, ct), ct).ConfigureAwait(true);
                    Merge(found, hasTemp ? ScaleFoundBy.TempAddress : ScaleFoundBy.Routed, r.Label);
                }
            }
            finally
            {
                if (session is not null)
                    await FinishTempAddressesAsync(session).ConfigureAwait(true);
            }
        }

        // Широковещательный запрос Штрих-ПРИНТ — со всех адресов ПК, в любом режиме.
        ct.ThrowIfCancellationRequested();
        _progressPrefix = null;
        ProgressText.Text = L("Широковещательный запрос весам Штрих-ПРИНТ (UDP)…", "Штрих-ПРИНТ таразасына кеңири таратуучу суроо (UDP)…", "Broadcast request to Shtrih-PRINT scales (UDP)…", "Shtrih-PRINT tartılara yayın isteği (UDP)…", "Shtrix-PRINT tarozilariga keng tarqatuvchi so‘rov (UDP)…");
        var ports = new[] { shtrikhPort, ShtrikhPrintLanScaleService.DefaultPort }.Distinct().ToArray();
        var liveSubnets = ScaleNetworkScanner.GetLocalSubnets();
        var replies = await Task.Run(() => ScaleNetworkScanner.BroadcastShtrikhProbeAsync(liveSubnets, ports, ct: ct), ct).ConfigureAwait(true);
        PosLogger.Log($"Поиск весов: широковещательный FCh на порты {string.Join(",", ports)} с адресов {string.Join(",", liveSubnets.Select(s => s.LocalAddress))}: ответов {replies.Count}"
                      + (replies.Count > 0 ? " — " + string.Join("; ", replies.Select(r => $"{r.Ip}:{r.Port} {r.Info}")) : ""), "SCALES");
        foreach (var reply in replies)
        {
            if (!all.TryGetValue(reply.IpNumber, out var d))
            {
                d = new ScaleNetworkDevice { Ip = reply.Ip, IpNumber = reply.IpNumber };
                all[reply.IpNumber] = d;
            }
            d.FoundBy |= ScaleFoundBy.Broadcast;
            d.ShtrikhInfo ??= reply.Info;
            d.Guess = ScaleDeviceGuess.Shtrikh;
            d.ScanLabel ??= $"broadcast → {reply.ViaLocalAddress}";
        }
        _scanNotes.Add(replies.Count == 0
            ? L("На широковещательный запрос весы Штрих-ПРИНТ не ответили — это обычно: по протоколу весы принимают широковещательные команды, только если их заранее включили, и не отвечают на них.",
                "Кеңири таратуучу суроого Штрих-ПРИНТ таразасы жооп берген жок — бул кадимки нерсе: протокол боюнча тараза кеңири таратуучу буйруктарды алдын ала күйгүзүлсө гана кабыл алат жана аларга жооп бербейт.",
                "No Shtrih-PRINT scale answered the broadcast request — that is normal: per the protocol a scale accepts broadcast commands only when enabled beforehand and does not reply to them.",
                "Yayın isteğine hiçbir Shtrih-PRINT tartı yanıt vermedi — bu normaldir: protokole göre tartı yayın komutlarını yalnızca önceden etkinleştirilmişse kabul eder ve yanıtlamaz.",
                "Keng tarqatuvchi so‘rovga Shtrix-PRINT tarozisi javob bermadi — bu odatiy: protokol bo‘yicha tarozi keng tarqatuvchi buyruqlarni faqat oldindan yoqilgan bo‘lsa qabul qiladi va ularga javob bermaydi.")
            : L($"На широковещательный запрос ответили весы Штрих-ПРИНТ: {string.Join(", ", replies.Select(r => r.Ip))}.",
                $"Кеңири таратуучу суроого Штрих-ПРИНТ таразасы жооп берди: {string.Join(", ", replies.Select(r => r.Ip))}.",
                $"Shtrih-PRINT scales answered the broadcast request: {string.Join(", ", replies.Select(r => r.Ip))}.",
                $"Yayın isteğine Shtrih-PRINT tartılar yanıt verdi: {string.Join(", ", replies.Select(r => r.Ip))}.",
                $"Keng tarqatuvchi so‘rovga Shtrix-PRINT tarozilari javob berdi: {string.Join(", ", replies.Select(r => r.Ip))}."));

        foreach (var d in all.Values)
            d.OutsideLocalNetworks = !ScaleNetworkScanner.IsInLocalNetworks(d.IpNumber, _subnets);

        return all.Values
            .OrderBy(d => d.Guess == ScaleDeviceGuess.Unknown ? 1 : 0)
            .ThenBy(d => d.IpNumber)
            .ToList();
    }

    // ------------------------------------------------------------------ временный адрес

    /// <summary>Проверки, подтверждение и запуск временного адреса. null — пользователь
    /// передумал или ошибка ввода; Session = null — продолжаем без временного адреса.</summary>
    private async Task<(ScaleTempAddressSession? Session, Dictionary<ScaleScanRange, IPAddress> Addresses)?> PrepareTempAddressesAsync(
        ScanTarget target, List<ScaleScanRange> foreign, CancellationToken ct)
    {
        var none = new Dictionary<ScaleScanRange, IPAddress>();
        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            ShowResult(L("Выберите адаптер для временного адреса.", "Убактылуу дарек үчүн адаптерди тандаңыз.", "Choose an adapter for the temporary address.", "Geçici adres için bir bağdaştırıcı seçin.", "Vaqtinchalik manzil uchun adapterni tanlang."), true);
            return null;
        }
        if (adapter.IsDhcp && !ScaleTempAddressSession.DhcpCoexistenceSupported)
        {
            _scanNotes.Add(_dhcpNote ?? "");
            return (null, none); // честно сказали в подсказке; ищем через роутер
        }

        var taken = ScaleNetworkScanner.AllLocalAddresses();
        foreach (var (address, _) in ScaleNetworkScanner.ReadArpTable())
            taken.Add(address);

        var plan = new Dictionary<ScaleScanRange, IPAddress>();
        foreach (var r in foreign)
        {
            uint ip;
            if (target.Kind == TargetKind.AllPresets)
            {
                ip = ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken);
            }
            else if (!IPAddress.TryParse((TempIpBox.Text ?? "").Trim(), out var typed)
                     || typed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                     || (ScaleNetworkScanner.ToNumber(typed) & r.Mask) != r.Network
                     || ScaleNetworkScanner.ToNumber(typed) == r.Network
                     || ScaleNetworkScanner.ToNumber(typed) == r.Broadcast)
            {
                ShowResult(L($"Временный адрес должен быть из подсети {ScaleNetworkScanner.ToIp(r.Network)}/{r.Prefix} (не первый и не последний), например {ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken))}.",
                             $"Убактылуу дарек {ScaleNetworkScanner.ToIp(r.Network)}/{r.Prefix} подсетинен болушу керек (биринчиси да, акыркысы да эмес), мисалы {ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken))}.",
                             $"The temporary address must be from {ScaleNetworkScanner.ToIp(r.Network)}/{r.Prefix} (not the first or last one), e.g. {ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken))}.",
                             $"Geçici adres {ScaleNetworkScanner.ToIp(r.Network)}/{r.Prefix} alt ağından olmalı (ilk veya son değil), ör. {ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken))}.",
                             $"Vaqtinchalik manzil {ScaleNetworkScanner.ToIp(r.Network)}/{r.Prefix} quyi tarmog‘idan bo‘lishi kerak (birinchi va oxirgisi emas), masalan {ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(r.Network, r.Prefix, taken))}."), true);
                return null;
            }
            else
            {
                ip = ScaleNetworkScanner.ToNumber(typed);
            }

            if (taken.Contains(ip))
            {
                ShowResult(L($"Адрес {ScaleNetworkScanner.ToIp(ip)} уже занят (это адрес компьютера или он есть в ARP-таблице). Укажите другой.",
                             $"{ScaleNetworkScanner.ToIp(ip)} дареги бош эмес (компьютердин дареги же ARP-таблицада бар). Башкасын жазыңыз.",
                             $"Address {ScaleNetworkScanner.ToIp(ip)} is taken (it is this computer's or in the ARP table). Pick another one.",
                             $"{ScaleNetworkScanner.ToIp(ip)} adresi dolu (bilgisayarın adresi veya ARP tablosunda). Başka birini seçin.",
                             $"{ScaleNetworkScanner.ToIp(ip)} manzili band (kompyuter manzili yoki ARP jadvalida bor). Boshqasini tanlang."), true);
                return null;
            }
            // Отвечает на ping (через роутер) — значит, адрес где-то живой: не берём.
            if (await ScaleNetworkScanner.PingRepliesAsync(ScaleNetworkScanner.ToAddress(ip), 700, ct).ConfigureAwait(true))
            {
                ShowResult(L($"Адрес {ScaleNetworkScanner.ToIp(ip)} отвечает на ping — он занят. Укажите другой временный адрес.",
                             $"{ScaleNetworkScanner.ToIp(ip)} дареги ping'ге жооп берет — бош эмес. Башка убактылуу дарек жазыңыз.",
                             $"Address {ScaleNetworkScanner.ToIp(ip)} answers ping — it is in use. Pick another temporary address.",
                             $"{ScaleNetworkScanner.ToIp(ip)} adresi ping'e yanıt veriyor — kullanımda. Başka geçici adres seçin.",
                             $"{ScaleNetworkScanner.ToIp(ip)} manzili ping'ga javob beradi — band. Boshqa vaqtinchalik manzil tanlang."), true);
                return null;
            }
            plan[r] = ScaleNetworkScanner.ToAddress(ip);
        }

        var addresses = plan.Select(kv => new ScaleTempAddress(kv.Value, kv.Key.Prefix)).ToList();
        var commands = ScaleTempAddressSession.DescribeCommands(adapter, addresses);
        PosLogger.Log($"Поиск весов: план временного адреса на «{adapter.Name}» (индекс {adapter.Index}, DHCP={adapter.IsDhcp}): {string.Join(" | ", commands)}", "SCALES");

        var message = L(
                $"Чтобы увидеть весы в другой подсети, касса на время поиска добавит компьютеру адрес(а) {string.Join(", ", plan.Values)} на адаптере «{adapter.Name}».\n\n• Основной адрес компьютера и шлюз не меняются, интернет и касса работают как раньше.\n• Адрес «только до перезагрузки» и удаляется сразу после поиска — даже при ошибке, «Остановить» или закрытии кассы.\n• Windows попросит права администратора один раз.\n\nКоманды:\n",
                $"Башка подсеттеги таразаны көрүү үчүн касса издөө убагында компьютерге «{adapter.Name}» адаптеринде {string.Join(", ", plan.Values)} дарек(тер)ин кошот.\n\n• Компьютердин негизги дареги жана шлюз өзгөрбөйт, интернет жана касса мурункудай иштейт.\n• Дарек «кайра жүктөлгөнгө чейин гана» жана издөөдөн кийин дароо өчүрүлөт — ката, «Токтотуу» же касса жабылса да.\n• Windows администратор укугун бир жолу сурайт.\n\nБуйруктар:\n",
                $"To see scales in another subnet, the till will temporarily give this computer the address(es) {string.Join(", ", plan.Values)} on adapter “{adapter.Name}”.\n\n• The computer's main address and gateway do not change; internet and the till keep working.\n• The address is “until reboot only” and is removed right after the search — even on an error, “Stop” or closing the till.\n• Windows will ask for administrator rights once.\n\nCommands:\n",
                $"Başka alt ağdaki tartıları görmek için kasa arama süresince bilgisayara “{adapter.Name}” bağdaştırıcısında {string.Join(", ", plan.Values)} adres(ler)ini ekleyecek.\n\n• Bilgisayarın ana adresi ve ağ geçidi değişmez; internet ve kasa çalışmaya devam eder.\n• Adres “yalnızca yeniden başlatmaya kadar” geçerlidir ve aramadan hemen sonra silinir — hata, “Durdur” veya kasanın kapanması durumunda bile.\n• Windows bir kez yönetici hakkı isteyecek.\n\nKomutlar:\n",
                $"Boshqa quyi tarmoqdagi tarozilarni ko‘rish uchun kassa qidiruv davomida kompyuterga «{adapter.Name}» adapterida {string.Join(", ", plan.Values)} manzil(lar)ini qo‘shadi.\n\n• Kompyuterning asosiy manzili va shlyuz o‘zgarmaydi, internet va kassa avvalgidek ishlaydi.\n• Manzil «faqat qayta yuklashgacha» va qidiruvdan so‘ng darhol o‘chiriladi — xato, «To‘xtatish» yoki kassa yopilganda ham.\n• Windows administrator huquqini bir marta so‘raydi.\n\nBuyruqlar:\n")
            + string.Join("\n", commands)
            + (adapter.IsDhcp && _dhcpNote is not null ? "\n\n" + _dhcpNote : "");

        var ok = await PosDialogHost.ShowAsync(
            new PosConfirmDialog(
                L("Временный адрес компьютера", "Компьютердин убактылуу дареги", "Temporary computer address", "Geçici bilgisayar adresi", "Kompyuterning vaqtinchalik manzili"),
                message,
                L("Добавить и искать", "Кошуп издөө", "Add and search", "Ekle ve ara", "Qo‘shib qidirish"),
                L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")),
            this).ConfigureAwait(true) == true;
        if (!ok)
        {
            ShowResult(L("Поиск не начат.", "Издөө башталган жок.", "The search was not started.", "Arama başlatılmadı.", "Qidiruv boshlanmadi."), false);
            return null;
        }

        ProgressText.Text = L("Жду разрешения Windows (окно «Контроль учётных записей»)…", "Windows'тун уруксатын күтүүдө («Каттоо эсептерин көзөмөлдөө» терезеси)…", "Waiting for Windows permission (User Account Control)…", "Windows izni bekleniyor (Kullanıcı Hesabı Denetimi)…", "Windows ruxsati kutilmoqda (Hisob qaydnomalarini boshqarish oynasi)…");
        var session = new ScaleTempAddressSession(adapter, addresses);
        var start = await session.StartAsync(ct).ConfigureAwait(true);
        PosLogger.Log($"Поиск весов: временный адрес — {start}; статус: {string.Join(" | ", session.Lines)}", "SCALES");
        switch (start)
        {
            case ScaleTempAddressStart.UacDeclined:
                _scanNotes.Add(L("Права администратора не дали — временный адрес не добавлен, подсеть проверена через роутер.",
                                 "Администратор укугу берилген жок — убактылуу дарек кошулган жок, подсеть роутер аркылуу текшерилди.",
                                 "Administrator rights were declined — no temporary address; the subnet was checked through the router.",
                                 "Yönetici hakları verilmedi — geçici adres eklenmedi; alt ağ yönlendirici üzerinden kontrol edildi.",
                                 "Administrator huquqi berilmadi — vaqtinchalik manzil qo‘shilmadi, quyi tarmoq router orqali tekshirildi."));
                return (null, none);
            case ScaleTempAddressStart.Failed:
                _scanNotes.Add(DescribeTempFailure(session));
                await FinishTempAddressesAsync(session).ConfigureAwait(true); // итог удаления (если что-то успело добавиться)
                return (null, none);
        }

        // Windows проверяет, не занят ли адрес в сети (DAD). Занят — эту подсеть через роутер.
        // Остановка здесь — адрес уже добавлен: снимаем его сразу, не дожидаясь сторожа скрипта.
        var ready = new Dictionary<ScaleScanRange, IPAddress>();
        var added = session.Added.ToHashSet();
        try
        {
            await CheckDuplicatesAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            await FinishTempAddressesAsync(session).ConfigureAwait(true);
            throw;
        }
        foreach (var failed in session.Lines.Where(l => l.StartsWith("ADD_FAIL ", StringComparison.Ordinal)))
            _scanNotes.Add(L("Не удалось добавить временный адрес: ", "Убактылуу дарек кошулган жок: ", "Could not add the temporary address: ", "Geçici adres eklenemedi: ", "Vaqtinchalik manzil qo‘shilmadi: ") + failed[9..]);
        return (session, ready);

        async Task CheckDuplicatesAsync()
        {
            foreach (var kv in plan)
            {
                if (!added.Contains(kv.Value.ToString()))
                    continue;
                DuplicateAddressDetectionState? state = null;
                for (var i = 0; i < 15; i++)
                {
                    state = ScaleNetworkScanner.AddressDadState(kv.Value);
                    if (state is not DuplicateAddressDetectionState.Tentative)
                        break;
                    await Task.Delay(200, ct).ConfigureAwait(true);
                }
                if (state == DuplicateAddressDetectionState.Duplicate)
                {
                    _scanNotes.Add(L($"Адрес {kv.Value} оказался занят другим устройством — подсеть {kv.Key.Label} проверена через роутер.",
                                     $"{kv.Value} дарегин башка түзмөк колдонот экен — {kv.Key.Label} подсети роутер аркылуу текшерилди.",
                                     $"Address {kv.Value} turned out to be used by another device — subnet {kv.Key.Label} was checked through the router.",
                                     $"{kv.Value} adresi başka bir cihaz tarafından kullanılıyormuş — {kv.Key.Label} alt ağı yönlendirici üzerinden kontrol edildi.",
                                     $"{kv.Value} manzili boshqa qurilmada ekan — {kv.Key.Label} quyi tarmog‘i router orqali tekshirildi."));
                    continue;
                }
                ready[kv.Key] = kv.Value;
            }
        }
    }

    private static string DescribeTempFailure(ScaleTempAddressSession session)
    {
        var lines = session.Lines;
        if (lines.Any(l => l.StartsWith("DHCP_LOST", StringComparison.Ordinal)))
        {
            return L("Windows выключила DHCP на адаптере при добавлении адреса — касса сразу удалила адрес и вернула DHCP. Временный адрес на этом адаптере невозможен: поменяйте адрес весов на их экране на адрес из сети компьютера.",
                     "Дарек кошулганда Windows адаптердеги DHCP'ни өчүрдү — касса даректи дароо өчүрүп, DHCP'ни кайтарды. Бул адаптерде убактылуу дарек мүмкүн эмес: таразанын экранынан анын дарегин компьютердин тармагындагы дарекке алмаштырыңыз.",
                     "Windows turned DHCP off on the adapter when the address was added — the till removed the address and restored DHCP at once. A temporary address is not possible on this adapter: change the scale's address on its screen to one from the computer's network.",
                     "Adres eklenirken Windows bağdaştırıcıda DHCP'yi kapattı — kasa adresi hemen sildi ve DHCP'yi geri açtı. Bu bağdaştırıcıda geçici adres mümkün değil: tartının adresini ekranından bilgisayarın ağındaki bir adresle değiştirin.",
                     "Manzil qo‘shilganda Windows adapterdagi DHCP'ni o‘chirdi — kassa manzilni darhol o‘chirib, DHCP'ni qaytardi. Bu adapterda vaqtinchalik manzil mumkin emas: tarozi manzilini uning ekranidan kompyuter tarmog‘idagi manzilga almashtiring.");
        }
        var detail = string.Join("; ", lines.Where(l => l.StartsWith("ADD_FAIL", StringComparison.Ordinal) || l.StartsWith("COEX_FAIL", StringComparison.Ordinal) || l.StartsWith("START_FAIL", StringComparison.Ordinal) || l == "READY_TIMEOUT" || l == "NONE_ADDED"));
        return L("Временный адрес добавить не удалось, подсеть проверена через роутер. ", "Убактылуу дарек кошулган жок, подсеть роутер аркылуу текшерилди. ", "Could not add the temporary address; the subnet was checked through the router. ", "Geçici adres eklenemedi; alt ağ yönlendirici üzerinden kontrol edildi. ", "Vaqtinchalik manzil qo‘shilmadi, quyi tarmoq router orqali tekshirildi. ")
               + (detail.Length > 0 ? "(" + detail + ")" : "");
    }

    /// <summary>Удаляет временные адреса (всегда, из finally) и пишет итог.</summary>
    private async Task FinishTempAddressesAsync(ScaleTempAddressSession session)
    {
        await session.FinishAsync().ConfigureAwait(true);
        PosLogger.Log($"Поиск весов: временный адрес снят; статус: {string.Join(" | ", session.Lines)}", "SCALES");
        if (session.NotRemoved.Count == 0)
        {
            if (session.Added.Count > 0)
            {
                _scanNotes.Add(L($"Временный адрес {string.Join(", ", session.Added)} удалён.",
                                 $"Убактылуу дарек {string.Join(", ", session.Added)} өчүрүлдү.",
                                 $"Temporary address {string.Join(", ", session.Added)} removed.",
                                 $"Geçici adres {string.Join(", ", session.Added)} silindi.",
                                 $"Vaqtinchalik manzil {string.Join(", ", session.Added)} o‘chirildi."));
            }
            return;
        }
        var index = SelectedAdapter?.Index.ToString() ?? "?";
        var manual = string.Join("\n", session.NotRemoved.Select(ip => $"netsh interface ipv4 delete address name={index} address={ip}"));
        _scanNotes.Add(L($"ВНИМАНИЕ: касса не получила подтверждения, что временный адрес удалён ({string.Join(", ", session.NotRemoved)}). Он исчезнет сам после перезагрузки, или удалите его командой (от администратора):\n{manual}",
                         $"КӨҢҮЛ БУРУҢУЗ: касса убактылуу дарек өчүрүлгөнүн тастыктай алган жок ({string.Join(", ", session.NotRemoved)}). Ал кайра жүктөлгөндөн кийин өзү жоголот, же буйрук менен өчүрүңүз (администратордон):\n{manual}",
                         $"WARNING: the till got no confirmation that the temporary address was removed ({string.Join(", ", session.NotRemoved)}). It disappears after a reboot, or remove it with (as administrator):\n{manual}",
                         $"DİKKAT: kasa geçici adresin silindiğine dair onay almadı ({string.Join(", ", session.NotRemoved)}). Yeniden başlatmada kendiliğinden kaybolur veya şu komutla silin (yönetici olarak):\n{manual}",
                         $"DIQQAT: kassa vaqtinchalik manzil o‘chirilganini tasdiqlay olmadi ({string.Join(", ", session.NotRemoved)}). U qayta yuklashdan keyin o‘zi yo‘qoladi yoki buyruq bilan o‘chiring (administrator sifatida):\n{manual}"));
    }

    // ------------------------------------------------------------------ подсказки в таблице

    /// <summary>Подсеть ПК, в которую стоит перевести весы (со шлюзом — сеть магазина).</summary>
    private LocalSubnet? PrimarySubnet => _subnets.FirstOrDefault(s => s.Gateway is not null) ?? _subnets.FirstOrDefault();

    /// <summary>Свободный адрес в сети компьютера для весов: с конца сети, не занятый ПК,
    /// шлюзом, найденными устройствами и ARP-таблицей.</summary>
    private string? SuggestAddressForScale(IReadOnlyList<ScaleNetworkDevice> devices)
    {
        var s = PrimarySubnet;
        if (s is null)
            return null;
        var taken = ScaleNetworkScanner.AllLocalAddresses();
        foreach (var d in devices)
            taken.Add(d.IpNumber);
        foreach (var (address, _) in ScaleNetworkScanner.ReadArpTable())
            taken.Add(address);
        if (s.Gateway is not null)
            taken.Add(ScaleNetworkScanner.ToNumber(s.Gateway));
        var prefix = Math.Max(s.PrefixLength, 24); // широкие сети — в «сотне» компьютера, как и сам поиск
        var network = ScaleNetworkScanner.ToNumber(s.LocalAddress) & ScaleNetworkScanner.MaskOf(prefix);
        return ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(network, prefix, taken));
    }

    private static string FoundByText(ScaleNetworkDevice d)
    {
        var parts = new List<string>();
        if (d.FoundBy.HasFlag(ScaleFoundBy.Ping))
            parts.Add("ping");
        if (d.FoundBy.HasFlag(ScaleFoundBy.Arp))
            parts.Add("ARP");
        if (d.FoundBy.HasFlag(ScaleFoundBy.Broadcast))
            parts.Add(L("широковещ. ответ", "кеңири тар. жооп", "broadcast reply", "yayın yanıtı", "keng tarq. javob"));
        if (d.FoundBy.HasFlag(ScaleFoundBy.TempAddress))
            parts.Add(L("врем. адрес", "убакт. дарек", "temp. address", "geçici adres", "vaqt. manzil"));
        if (d.FoundBy.HasFlag(ScaleFoundBy.Routed))
            parts.Add(L("через роутер", "роутер аркылуу", "via router", "yönlendirici üzerinden", "router orqali"));
        var text = parts.Count > 0 ? string.Join(" + ", parts) : "—";
        return string.IsNullOrWhiteSpace(d.ScanLabel) ? text : $"{text}\n{d.ScanLabel}";
    }

    private string OutsideHint(ScaleNetworkDevice d, string? suggest)
    {
        var s = PrimarySubnet;
        var scaleNet = ScaleNetworkScanner.ToIp(d.IpNumber & 0xFFFFFF00u) + "/24";
        var pcNet = s is null ? "—" : $"{ScaleNetworkScanner.ToIp(ScaleNetworkScanner.ToNumber(s.LocalAddress) & ScaleNetworkScanner.MaskOf(s.PrefixLength))}/{s.PrefixLength}";
        var mask = s is null ? "255.255.255.0" : ScaleNetworkScanner.ToIp(ScaleNetworkScanner.MaskOf(Math.Max(s.PrefixLength, 24)));
        var gw = s?.Gateway?.ToString() ?? "—";
        var pcTemp = ScaleNetworkScanner.ToIp(ScaleNetworkScanner.SuggestFreeAddress(d.IpNumber & 0xFFFFFF00u, 24, new HashSet<uint> { d.IpNumber }));
        return L($"Весы в другой подсети ({scaleNet}), а компьютер — в {pcNet} ({s?.LocalAddress}). Касса так работать с ними не сможет. Поменяйте адрес весов на {suggest ?? "…"} (маска {mask}, шлюз {gw}) и выберите «Использовать для… → {suggest}», или дайте компьютеру адрес в сети весов, например {pcTemp}/24.",
                 $"Тараза башка подсетте ({scaleNet}), ал эми компьютер — {pcNet} ({s?.LocalAddress}). Касса аны менен мындай иштей албайт. Таразанын дарегин {suggest ?? "…"} кылып алмаштырыңыз (маска {mask}, шлюз {gw}) жана «Колдонуу… → {suggest}» тандаңыз, же компьютерге тараза тармагынан дарек бериңиз, мисалы {pcTemp}/24.",
                 $"The scale is in another subnet ({scaleNet}) while the computer is in {pcNet} ({s?.LocalAddress}). The till cannot work with it like this. Change the scale's address to {suggest ?? "…"} (mask {mask}, gateway {gw}) and choose “Use for… → {suggest}”, or give the computer an address in the scale's network, e.g. {pcTemp}/24.",
                 $"Tartı başka bir alt ağda ({scaleNet}), bilgisayar ise {pcNet} ({s?.LocalAddress}) ağında. Kasa bu şekilde çalışamaz. Tartının adresini {suggest ?? "…"} yapın (maske {mask}, ağ geçidi {gw}) ve “Şunun için kullan… → {suggest}” seçin ya da bilgisayara tartının ağından bir adres verin, ör. {pcTemp}/24.",
                 $"Tarozi boshqa quyi tarmoqda ({scaleNet}), kompyuter esa {pcNet} ({s?.LocalAddress}) da. Kassa u bilan bunday ishlay olmaydi. Tarozi manzilini {suggest ?? "…"} ga o‘zgartiring (niqob {mask}, shlyuz {gw}) va «Foydalanish… → {suggest}» ni tanlang yoki kompyuterga tarozi tarmog‘idan manzil bering, masalan {pcTemp}/24.");
    }

    private string? ScanNotesText => _scanNotes.Count == 0 ? null : string.Join("\n", _scanNotes.Where(n => !string.IsNullOrWhiteSpace(n)));

    private void ReportProgressPrefix(ref string text)
    {
        if (!string.IsNullOrEmpty(_progressPrefix))
            text = $"{_progressPrefix} · {text}";
    }
}
