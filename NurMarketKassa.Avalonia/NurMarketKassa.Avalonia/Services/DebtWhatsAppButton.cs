using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-08, владелец: «Нужно вернуть кнопку WA (отправка долга на WhatsApp)». Кнопка «WA» (значок WhatsApp + подпись) у должника:
/// в списке «Долги клиентов» и в блоке «Долг» карточки клиента. По нажатию открывает чат wa.me/&lt;номер&gt;?text=&lt;готовый текст&gt; на языке клиента
/// (язык из карточки клиента на сервере, иначе язык кассы). Сама ничего не отправляет и не рассылает: один клиент — одно нажатие, «Отправить»
/// жмёт человек. Нет номера — кнопка неактивна, причина в подсказке. Явный отказ клиента от сообщений (исключён из напоминаний или согласия
/// выключены с датой) — сначала предупреждение с выбором. Ссылку открывает общий <see cref="PlatformLauncher"/> (Windows / Linux / Android).</summary>
internal sealed class DebtWhatsAppButton : Button
{
    // Контур значка WhatsApp (Simple Icons, CC0), поле 24×24; флаги дуг разделены пробелами.
    private const string IconPath =
        "M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347 " +
        "m-5.421 7.403 h-.004 a9.87 9.87 0 0 1 -5.031 -1.378 l-.361-.214 -3.741.982 .998-3.648 -.235-.374 a9.86 9.86 0 0 1 -1.51 -5.26 c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898 a9.825 9.825 0 0 1 2.893 6.994 c-.003 5.45-4.437 9.884-9.885 9.884 " +
        "m8.413-18.297 A11.815 11.815 0 0 0 12.05 0 C5.495 0 .16 5.335.157 11.892 c0 2.096.547 4.142 1.588 5.945 L.057 24 l6.305-1.654 a11.882 11.882 0 0 0 5.683 1.448 h.005 c6.554 0 11.89-5.335 11.893-11.893 a11.821 11.821 0 0 0 -3.48 -8.413 Z";

    private static readonly IBrush WhatsAppGreen = new SolidColorBrush(Color.Parse("#25D366"));

    private string _clientId = "";
    private string _name = "";
    private string? _phone;
    private double _amount;
    private DateTime? _since;
    private bool _busy;
    private bool _canOpen;

    protected override Type StyleKeyOverride => typeof(Button);

    public DebtWhatsAppButton()
    {
        Height = 40; // цель нажатия 40 (как у «Принять оплату»)
        MinHeight = 40;
        MinWidth = 0;
        Padding = new Thickness(12, 0);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        FontSize = 13;
        FontWeight = FontWeight.SemiBold;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Classes.Add("sec-outline");
        ToolTip.SetShowOnDisabled(this, true);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(IconPath), Fill = WhatsAppGreen, Width = 18, Height = 18, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = "WA", VerticalAlignment = VerticalAlignment.Center });
        Content = row;
        Click += async (_, e) =>
        {
            e.Handled = true;
            await OpenChatAsync().ConfigureAwait(true);
        };
    }

    /// <summary>Подставить должника: кнопка активна, если есть клиент и из номера получается номер WhatsApp.</summary>
    public void Update(string clientId, string name, string? phone, double amount, DateTime? since)
    {
        _clientId = clientId ?? "";
        _name = name ?? "";
        _phone = phone;
        _amount = amount;
        _since = since;
        _canOpen = _clientId.Length > 0 && DebtWhatsAppLink.NormalizePhone(phone) is not null;
        IsEnabled = _canOpen && !_busy;
        var tip = _canOpen
            ? Tr.T("Напомнить о долге в WhatsApp: откроется чат с готовым текстом, «Отправить» нажимаете вы.",
                "Карызды WhatsApp'та эскертүү: даяр текст менен чат ачылат, «Жөнөтүү» баскычын өзүңүз басасыз.",
                "Remind about the debt on WhatsApp: a chat opens with the text ready, you press Send.",
                "Borcu WhatsApp'tan hatırlat: hazır metinle sohbet açılır, Gönder'e siz basarsınız.",
                "Qarzni WhatsApp'da eslatish: tayyor matn bilan chat ochiladi, «Yuborish»ni o'zingiz bosasiz.")
            : DisabledReason(_clientId.Length > 0, phone);
        ToolTip.SetTip(this, tip);
        Avalonia.Automation.AutomationProperties.SetName(this, "WhatsApp: " + Tr.T("напомнить о долге", "карызды эскертүү", "debt reminder", "borç hatırlatma", "qarz eslatmasi")
            + (_name.Length > 0 ? " — " + _name : ""));
    }

    /// <summary>Почему кнопка неактивна (5 языков).</summary>
    internal static string DisabledReason(bool hasClient, string? phone) => !hasClient
        ? Tr.T("Долг записан без клиента — напомнить некому.", "Карыз кардарсыз жазылган — эскертүүчү эч ким жок.", "The debt was recorded without a customer — there is no one to remind.",
            "Borç müşterisiz kaydedilmiş — hatırlatılacak kimse yok.", "Qarz mijozsiz yozilgan — eslatadigan hech kim yo'q.")
        : string.IsNullOrWhiteSpace(phone)
            ? Tr.T("У клиента нет телефона. Добавьте номер в карточке клиента — кнопка «WA» станет активной.", "Кардардын телефону жок. Кардардын картасына номер кошуңуз — «WA» баскычы иштейт.",
                "The customer has no phone. Add a number in the customer card and the “WA” button will work.", "Müşterinin telefonu yok. Müşteri kartına numara ekleyin — «WA» düğmesi açılır.",
                "Mijozning telefoni yo'q. Mijoz kartasiga raqam qo'shing — «WA» tugmasi ishlaydi.")
            : Tr.T("Номер клиента не похож на телефонный (нужен вид +996 700 123 456 или 0700 123 456). Исправьте его в карточке клиента.",
                "Кардардын номери телефонго окшобойт (+996 700 123 456 же 0700 123 456 түрүндө болушу керек). Аны кардардын картасында оңдоңуз.",
                "The customer's number doesn't look like a phone number (use +996 700 123 456 or 0700 123 456). Fix it in the customer card.",
                "Müşterinin numarası telefon numarasına benzemiyor (+996 700 123 456 veya 0700 123 456 biçiminde olmalı). Müşteri kartında düzeltin.",
                "Mijoz raqami telefon raqamiga o'xshamaydi (+996 700 123 456 yoki 0700 123 456 ko'rinishida bo'lsin). Uni mijoz kartasida tuzating.");

    private async Task OpenChatAsync()
    {
        if (_busy || !_canOpen)
            return;
        _busy = true;
        IsEnabled = false;
        try
        {
            var lang = DebtWhatsAppLink.AppLanguageCode();
            var refusal = false;
            try
            {
                refusal = DebtReminderPlanner.IsExcluded(_clientId); // владелец сам исключил клиента из напоминаний («отписка»)
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги, WA: список исключённых не прочитан ({ex.GetType().Name}).", "WARNING");
            }
            // Карточка клиента на сервере: язык сообщений и согласия. Нет связи / нет права / сервер без метода — без них (язык кассы, без предупреждения).
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var consents = await App.GetRequiredService<ClientMessagingApi>().GetConsentsAsync(_clientId, cts.Token).ConfigureAwait(true);
                lang = DebtWhatsAppLink.NormalizeLanguage(consents.Language, lang);
                refusal |= DebtWhatsAppLink.IsExplicitRefusal(consents.Service, consents.Marketing, consents.UpdatedAt is not null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Долги, WA: согласия клиента не получены ({ex.GetType().Name}) — язык кассы.", "INFO");
            }

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (refusal)
            {
                var go = await PosDialogs.ConfirmYesNoModalAsync(owner,
                    Tr.T($"Клиент «{_name}» отказался от сообщений (или вы исключили его из напоминаний). Всё равно открыть чат WhatsApp с напоминанием о долге?\n\nСообщение уйдёт, только если вы сами нажмёте «Отправить» в WhatsApp.",
                        $"«{_name}» кардары билдирүүлөрдөн баш тарткан (же сиз аны эскертүүлөрдөн чыгаргансыз). Баары бир карыз тууралуу WhatsApp чатын ачасызбы?\n\nБилдирүү сиз WhatsApp'та өзүңүз «Жөнөтүү» басканда гана кетет.",
                        $"Customer “{_name}” declined messages (or you excluded them from reminders). Open the WhatsApp chat with the debt reminder anyway?\n\nNothing is sent unless you press Send in WhatsApp yourself.",
                        $"«{_name}» mesajları reddetti (veya hatırlatmalardan çıkardınız). Borç hatırlatmasıyla WhatsApp sohbeti yine de açılsın mı?\n\nSiz WhatsApp'ta Gönder'e basmadıkça hiçbir şey gönderilmez.",
                        $"«{_name}» xabarlardan voz kechgan (yoki siz uni eslatmalardan chiqargansiz). Baribir qarz eslatmasi bilan WhatsApp chatini ochamizmi?\n\nSiz WhatsApp'da «Yuborish»ni o'zingiz bosmaguningizcha hech narsa yuborilmaydi."),
                    Tr.T("Клиент просил не беспокоить", "Кардар тынчсыздандырбоону суранган", "Customer asked not to be contacted", "Müşteri rahatsız edilmek istemiyor", "Mijoz bezovta qilmaslikni so'ragan"),
                    Tr.T("Открыть чат", "Чатты ачуу", "Open chat", "Sohbeti aç", "Chatni ochish"),
                    Tr.T("Не писать", "Жазбоо", "Don't write", "Yazma", "Yozmaslik"), enterCancels: true).ConfigureAwait(true);
                if (!go)
                    return;
            }

            var text = DebtWhatsAppLink.BuildText(new DebtWhatsAppLink.Info(_name, _amount, UserPreferences.Instance.StoreName, _since), lang);
            var url = DebtWhatsAppLink.BuildUrl(_phone, text);
            if (url is not null && PlatformLauncher.OpenUrl(url))
            {
                PosLogger.Log($"Долги: напоминание WhatsApp — открыт чат ({DebtReminderPlanner.MaskPhone(_phone)}, язык {lang}).", "INFO");
                return;
            }
            PosDialogs.Warning(owner, Tr.T("Не удалось открыть WhatsApp.", "WhatsApp'ты ачуу мүмкүн болгон жок.", "Could not open WhatsApp.", "WhatsApp açılamadı.", "WhatsApp'ni ochib bo'lmadi."),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долги: кнопка WA не сработала ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            _busy = false;
            IsEnabled = _canOpen;
        }
    }
}
