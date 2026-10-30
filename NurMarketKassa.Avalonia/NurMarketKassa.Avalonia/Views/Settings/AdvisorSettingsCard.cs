using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-30, владелец: «Нур Советник: в настройках поставить тумблер (находить штрихкоды) и прочие его возможности, чтобы
/// можно было выключать и включать какую-то часть». Карточка «Нур Советник» в Настройки → Операции (рядом с ключами ИИ): главный
/// выключатель, три кнопки («Включить всё безопасное» / «Только чтение» / «Выключить всё»), возможности по группам с тумблером, риском
/// и пояснением «что изменится». Хранение — <see cref="AdvisorCapabilities"/> (общее для магазина через облако + копия на компьютере).
/// Меняют владелец и администратор; кассир видит только включённые возможности, без тумблеров. Тумблеры срабатывают сразу.
/// Вставляется в OperationsSettingsView через AdvisorHost.</summary>
public sealed class AdvisorSettingsCard : Border
{
    private readonly StackPanel _body = new() { Spacing = 8 };
    private bool _building;
    // Свой тумблер уже стоит как надо — список не перестраиваем (иначе прокрутка прыгает); чужие изменения (облако, кнопки) перестраивают.
    private int _skipRebuild;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Стиль «Border.SettingsCard» (общие стили настроек) — как у обычной карточки: селектор типа точный.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public AdvisorSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(16);
        Child = _body;
        AdvisorCapabilities.ActorProvider = ActorName;
        void OnChanged() => Dispatcher.UIThread.Post(() =>
        {
            if (_skipRebuild > 0)
            {
                _skipRebuild--;
                return;
            }
            Rebuild();
        });
        AttachedToVisualTree += (_, _) =>
        {
            AdvisorCapabilities.Changed -= OnChanged;
            SettingsCloudSync.Applied -= OnChanged;
            AdvisorCapabilities.Changed += OnChanged;
            SettingsCloudSync.Applied += OnChanged;
            Rebuild();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            AdvisorCapabilities.Changed -= OnChanged;
            SettingsCloudSync.Applied -= OnChanged;
        };
        Rebuild();
    }

    /// <summary>Менять могут владелец (программа владельца, автономный режим) и администратор; кассир — нет.</summary>
    private static bool CanEdit
    {
        get
        {
            if (AppMode.IsOwner)
                return true;
            if (App.AppHost?.Services is not { } sp)
                return false;
            if (sp.GetService<NurMarketKassa.Core.Contracts.IAutonomousAuthService>() is { IsCurrentSessionAutonomous: true })
                return true;
            return sp.GetService<NurMarketKassa.Services.Api.IAuthApiService>() is { } auth && PermissionService.IsOwnerOrAdmin(auth.UserPayload);
        }
    }

    /// <summary>Кто меняет — для записи в журнал (почта пользователя; в программе владельца без входа — «владелец»).</summary>
    private static string ActorName()
    {
        try
        {
            if (App.AppHost?.Services.GetService<NurMarketKassa.Services.Api.IAuthApiService>() is { } auth)
            {
                var payload = auth.UserPayload;
                var user = payload.ValueKind == System.Text.Json.JsonValueKind.Object && payload.TryGetProperty("user", out var nested)
                           && nested.ValueKind == System.Text.Json.JsonValueKind.Object ? nested : payload;
                if (user.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var name in new[] { "email", "username", "phone", "first_name" })
                        if (user.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                            return s;
            }
        }
        catch (Exception)
        {
            // имя необязательно
        }
        return AppMode.IsOwner ? "программа владельца" : "касса";
    }

    private TextBlock Soft(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft")); // 2026-10-31: от самого элемента, а не от this — иначе убранные при Rebuild() строки оставались живыми (утечка +0,45 МБ на заход во вкладку «Операции»)
        return t;
    }

    private static string RiskText(AdvisorCapability c)
    {
        var risk = c.Risk switch
        {
            AdvisorRisk.Reads => T("читает данные", "маалыматты окуйт", "reads data", "veri okur", "ma'lumotni o'qiydi"),
            AdvisorRisk.Changes => T("меняет данные", "маалыматты өзгөртөт", "changes data", "veriyi değiştirir", "ma'lumotni o'zgartiradi"),
            _ => T("отправляет наружу", "сыртка жөнөтөт", "sends outside", "dışarı gönderir", "tashqariga yuboradi"),
        };
        var parts = new List<string> { risk };
        if (c.NeedsConfirm)
            parts.Add(T("с подтверждением", "ырастоо менен", "with confirmation", "onayla", "tasdiq bilan"));
        if (c.Permission is { } p)
        {
            var right = p switch
            {
                "Products" => T("«Склад»", "«Кампа»", "“Warehouse”", "«Depo»", "«Ombor»"),
                "Clients" => T("«Клиенты»", "«Кардарлар»", "“Customers”", "«Müşteriler»", "«Mijozlar»"),
                "Staff" => T("«Настройки»", "«Жөндөөлөр»", "“Settings”", "«Ayarlar»", "«Sozlamalar»"),
                _ => T("«Настройки»", "«Жөндөөлөр»", "“Settings”", "«Ayarlar»", "«Sozlamalar»"),
            };
            parts.Add(T("нужно право ", "уруксат керек ", "needs access ", "erişim gerekir ", "ruxsat kerak ") + right);
        }
        return string.Join(" · ", parts);
    }

    private Control Row(AdvisorCapability c, bool editable, bool dimmed)
    {
        var on = AdvisorCapabilities.IsSet(c.Id);
        var left = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = c.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        left.Children.Add(title);
        var risk = Soft(RiskText(c), 11.5);
        risk.Bind(TextBlock.ForegroundProperty, risk.GetResourceObservable(c.Risk == AdvisorRisk.Reads ? "BrushTextSoft" : "BrushWarning"));
        left.Children.Add(risk);
        if (editable)
            left.Children.Add(Soft(c.Description));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(left);
        if (editable)
        {
            var toggle = new CheckBox { Classes = { "ToggleSwitch" }, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetName(toggle, c.Name);
            var id = c.Id;
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (_building)
                    return;
                var desired = toggle.IsChecked == true;
                if (id != AdvisorCapabilities.Master && AdvisorCapabilities.IsSet(id) != desired)
                    _skipRebuild++;
                AdvisorCapabilities.Set(id, desired);
            };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
        }
        row.Opacity = dimmed ? 0.55 : 1;
        return row;
    }

    private Button PresetButton(string text, AdvisorCapabilities.Preset preset, string tip)
    {
        var b = new Button { Content = text, MinHeight = 40, Padding = new Thickness(14, 0), Margin = new Thickness(0, 0, 8, 6) };
        b.Classes.Add("btn-secondary");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => AdvisorCapabilities.ApplyPreset(preset);
        return b;
    }

    private void Rebuild()
    {
        _building = true;
        try
        {
            _body.Children.Clear();
            // Нур Советник — только на тарифе «Стандарт» (как ключи ИИ).
            IsVisible = TariffGate.CanUseAi;
            var editable = CanEdit;
            var masterOn = AdvisorCapabilities.MasterOn;
            _body.Children.Add(new TextBlock { Text = T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), Classes = { "SettingsCardTitle" }, FontSize = 14 });
            _body.Children.Add(Soft(editable
                ? T("Здесь вы решаете, что умеет Нур Советник. Выключенное не показывается в окне, не попадает в запросы к ИИ и не выполняется. Настройки общие для всех касс магазина (меняет владелец или администратор). "
                    + "Имена и телефоны клиентов и сотрудников в ИИ не уходят никогда — только коды. Пометка под названием: что делает возможность — читает, меняет данные (только после вашего подтверждения) или отправляет что-то наружу.",
                    "Бул жерде Нур Кеңешчи эмне кыла аларын өзүңүз чечесиз. Өчүрүлгөн нерсе терезеде көрүнбөйт, ИИге суроолорго кирбейт жана аткарылбайт. Жөндөөлөр дүкөндүн бардык кассалары үчүн жалпы (ээси же администратор өзгөртөт). "
                    + "Кардарлардын жана кызматкерлердин аттары менен телефондору ИИге эч качан кетпейт — коддор гана. Аталыштын астындагы белги: мүмкүнчүлүк эмне кылат — окуйт, маалыматты өзгөртөт (сиздин ырастооңуздан кийин гана) же сыртка бир нерсе жөнөтөт.",
                    "Here you decide what Nur Advisor can do. Anything turned off is hidden in the window, left out of AI requests and never executed. The settings are shared by all tills of the shop (the owner or an administrator changes them). "
                    + "Customer and staff names and phones never go to the AI — only codes. The marks under each name show what the feature does: reads, changes data (only after your confirmation) or sends something outside.",
                    "Nur Danışman'ın neler yapabileceğine burada siz karar verirsiniz. Kapatılan şey pencerede görünmez, yapay zekâ isteklerine girmez ve çalıştırılmaz. Ayarlar mağazanın tüm kasaları için ortaktır (sahip veya yönetici değiştirir). "
                    + "Müşteri ve çalışan adları ile telefonları yapay zekâya asla gitmez — yalnızca kodlar. Adın altındaki işaretler özelliğin ne yaptığını gösterir: okur, veriyi değiştirir (yalnızca onayınızdan sonra) veya dışarı bir şey gönderir.",
                    "Bu yerda Nur Maslahatchi nima qila olishini o'zingiz hal qilasiz. O'chirilgan narsa oynada ko'rinmaydi, SI so'rovlariga kirmaydi va bajarilmaydi. Sozlamalar do'konning barcha kassalari uchun umumiy (egasi yoki administrator o'zgartiradi). "
                    + "Mijoz va xodimlarning ismi va telefoni SIga hech qachon ketmaydi — faqat kodlar. Nom ostidagi belgilar imkoniyat nima qilishini ko'rsatadi: o'qiydi, ma'lumotni o'zgartiradi (faqat sizning tasdig'ingizdan keyin) yoki tashqariga nimadir yuboradi.")
                : T("Нур Советник настраивает владелец или администратор. Ниже — то, что сейчас включено в вашем магазине.", "Нур Кеңешчини ээси же администратор жөндөйт. Төмөндө — дүкөнүңүздө азыр күйгүзүлгөнү.",
                    "Nur Advisor is set up by the owner or an administrator. Below is what is currently on in your shop.", "Nur Danışman'ı sahip veya yönetici ayarlar. Aşağıda mağazanızda şu an açık olanlar var.",
                    "Nur Maslahatchini egasi yoki administrator sozlaydi. Quyida do'koningizda hozir yoqilgan imkoniyatlar.")));

            var master = AdvisorCapabilities.Find(AdvisorCapabilities.Master)!;
            if (!editable)
            {
                if (!masterOn)
                    _body.Children.Add(Soft(AdvisorCapabilities.BlockedText(), 13));
                else
                    foreach (var g in AdvisorCapabilities.Groups)
                    {
                        var on = AdvisorCapabilities.All.Where(c => c.Group == g.Id && c.Id != AdvisorCapabilities.Master && AdvisorCapabilities.IsOn(c.Id)).ToList();
                        if (on.Count == 0)
                            continue;
                        var gt = new TextBlock { Text = g.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
                        gt.Bind(TextBlock.ForegroundProperty, gt.GetResourceObservable("BrushText"));
                        _body.Children.Add(gt);
                        foreach (var c in on)
                            _body.Children.Add(Row(c, editable: false, dimmed: false));
                    }
                return;
            }

            _body.Children.Add(Row(master, true, false));
            var presets = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            presets.Children.Add(PresetButton(T("Включить всё безопасное", "Бардык коопсуздарын күйгүзүү", "Turn on everything safe", "Güvenli olanların hepsini aç", "Xavfsizlarning hammasini yoqish"), AdvisorCapabilities.Preset.SafeOn,
                T("Включает чтение и действия с подтверждением. Интернет, голос, фото и рассылки не трогает.", "Окуу жана ырастоо менен аракеттерди күйгүзөт. Интернет, үн, сүрөт жана жөнөтүүлөргө тийбейт.",
                    "Turns on reading and confirmed actions. Leaves the internet, voice, photos and messages as they are.", "Okuma ve onaylı işlemleri açar. İnternet, ses, fotoğraf ve mesajlara dokunmaz.", "O'qish va tasdiqli amallarni yoqadi. Internet, ovoz, rasm va xabarlarga tegmaydi.")));
            presets.Children.Add(PresetButton(T("Только чтение", "Окуу гана", "Read only", "Yalnızca okuma", "Faqat o'qish"), AdvisorCapabilities.Preset.ReadOnly,
                T("Советник отвечает по цифрам и открывает разделы, но ничего не меняет и не отправляет.", "Кеңешчи сандар боюнча жооп берет жана бөлүмдөрдү ачат, бирок эч нерсе өзгөртпөйт жана жөнөтпөйт.",
                    "The advisor answers from figures and opens sections but changes and sends nothing.", "Danışman rakamlarla yanıtlar ve bölümleri açar, ancak hiçbir şeyi değiştirmez ve göndermez.", "Maslahatchi raqamlar bo'yicha javob beradi va bo'limlarni ochadi, lekin hech narsani o'zgartirmaydi va yubormaydi.")));
            presets.Children.Add(PresetButton(T("Выключить всё", "Баарын өчүрүү", "Turn everything off", "Hepsini kapat", "Hammasini o'chirish"), AdvisorCapabilities.Preset.AllOff,
                T("Нур Советник выключается целиком: меню, ответы, бот, отчёты.", "Нур Кеңешчи толугу менен өчөт: меню, жооптор, бот, отчёттор.", "Nur Advisor is switched off completely: menu, answers, bot, reports.",
                    "Nur Danışman tamamen kapanır: menü, yanıtlar, bot, raporlar.", "Nur Maslahatchi butunlay o'chadi: menyu, javoblar, bot, hisobotlar.")));
            _body.Children.Add(presets);
            if (SettingsCloudSync.CompanySettingsLoaded && !SettingsCloudSync.CompanySettingsWritable)
                _body.Children.Add(Soft(T("Сервер не разрешил менять общие настройки магазина — изменения останутся на этом компьютере.", "Сервер дүкөндүн жалпы жөндөөлөрүн өзгөртүүгө уруксат берген жок — өзгөртүүлөр ушул компьютерде калат.",
                    "The server did not allow changing shared shop settings — changes stay on this computer.", "Sunucu ortak mağaza ayarlarının değiştirilmesine izin vermedi — değişiklikler bu bilgisayarda kalır.", "Server umumiy do'kon sozlamalarini o'zgartirishga ruxsat bermadi — o'zgarishlar shu kompyuterda qoladi.")));
            if (!masterOn)
                _body.Children.Add(Soft(AdvisorCapabilities.BlockedText(), 13));

            foreach (var g in AdvisorCapabilities.Groups)
            {
                var items = AdvisorCapabilities.All.Where(c => c.Group == g.Id).ToList();
                if (items.Count == 0)
                    continue;
                var gt = new TextBlock { Text = g.Name, FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
                gt.Bind(TextBlock.ForegroundProperty, gt.GetResourceObservable("BrushText"));
                _body.Children.Add(gt);
                _body.Children.Add(Soft(g.Description));
                foreach (var c in items)
                    _body.Children.Add(Row(c, true, !masterOn));
            }
        }
        finally
        {
            _building = false;
        }
    }
}
