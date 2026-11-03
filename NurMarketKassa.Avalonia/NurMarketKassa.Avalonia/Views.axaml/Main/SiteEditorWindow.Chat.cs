using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-01, ТЗ «Сайт — темы, Telegram-бот и ИИ-ассистент» (ИИ-1, ИИ-2) и ответ Умара по ТЗ 01.11: раздел «Чат на сайте» —
/// «Помощник магазина». Настройки (включён, потолок вызовов ИИ в сутки, ключ Gemini магазина) — GET/PUT api/main/inbox/assistant/settings/
/// по кнопке «Сохранить настройки помощника»; «Проверить помощника» — POST .../assistant/test/. Ключ Gemini — «у каждого магазина свой»
/// (вариант «а»): поле только для записи, обратно его сервер не отдаёт, в журнал и на экран он не попадает. «Знания для помощника» —
/// ключ дизайна shop.info.knowledge (ru, ky, en, uz, kk; до 2000 знаков): программа собирает черновик из настроек, владелец правит,
/// и в черновик сайта (design/draft/) текст уходит только по «Сохранить знания». Пока сервер не отдаёт признаки showcase_assistant /
/// showcase_chat или адреса настроек — «Скоро: ждём сервер NurCRM».
/// Настройки чата К-Ч8 (включить чат, места, приветствие) — отдельный пакет, здесь их нет.</summary>
public sealed partial class SiteEditorWindow
{
    private const string KnowledgePath = "shop.info.knowledge";
    private const int KnowledgeMax = 2000;
    private static readonly string[] KnowledgeLangs = { "ru", "ky", "en", "uz", "kk" };

    /// <summary>Настройки помощника, полученные меньше двух минут назад (редактор перестраивается — сервер лишний раз не спрашиваем).</summary>
    private (DateTime At, AssistantSettings Settings)? _asst;

    private void BuildChatGroup()
    {
        var group = Group(T("Чат на сайте: помощник магазина", "Сайттагы чат: дүкөндүн жардамчысы", "Website chat: shop assistant", "Sitedeki sohbet: mağaza asistanı", "Saytdagi chat: do'kon yordamchisi"));
        var box = new StackPanel { Spacing = 12 };
        group.Children.Add(box);
        box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        _ = FillChatAsync(box);
    }

    private static string KnowledgeLangName(string code) => code switch
    {
        "ky" => "Кыргызча",
        "en" => "English",
        "uz" => "Oʻzbekcha",
        "kk" => "Қазақша",
        _ => "Русский",
    };

    private void SetStatusText(TextBlock block, string text, string brush)
    {
        block.Text = text;
        Use(block, TextBlock.ForegroundProperty, brush);
    }

    private async Task FillChatAsync(StackPanel box, bool force = false)
    {
        try
        {
            var (caps, capError) = await ShowcaseCapabilities.TryLoadAsync(_api, force: force).ConfigureAwait(true);
            box.Children.Clear();
            if (capError != null) // 2026-11-01 (контрольный аудит): сбой запроса — не «Скоро»
            {
                ShowSiteUnavailable(box, capError, () => FillChatAsync(box, force: true));
                return;
            }
            var soon = Hint(T("Скоро: ждём сервер NurCRM. ИИ-помощник на сайте (подбор размера, наличие, доставка, «Позвать продавца») включится, когда сервер откроет его для вашего магазина.",
                "Жакында: NurCRM серверин күтөбүз. Сайттагы ЖИ-жардамчы (өлчөм тандоо, бар-жогу, жеткирүү, «Сатуучуну чакыруу») сервер дүкөнүңүз үчүн ачканда иштейт.",
                "Coming soon: waiting for the NurCRM server. The AI assistant on the website (size advice, availability, delivery, “Call the seller”) will start once the server opens it for your shop.",
                "Yakında: NurCRM sunucusu bekleniyor. Sitedeki yapay zekâ asistanı (beden önerisi, stok, teslimat, «Satıcıyı çağır») sunucu mağazanız için açınca başlar.",
                "Tez orada: NurCRM serveri kutilmoqda. Saytdagi SI yordamchi (o'lcham tanlash, mavjudlik, yetkazish, «Sotuvchini chaqirish») server do'koningiz uchun ochganda ishlaydi."));
            if (!caps.Contains(ShowcaseCapabilities.Assistant))
            {
                box.Children.Add(soon);
                return;
            }

            var api = new SiteAssistantApi(_api);
            AssistantSettings? loaded;
            if (_asst is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2))
                loaded = cached.Settings;
            else
            {
                try
                {
                    loaded = await api.GetSettingsAsync().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Редактор сайта: настройки помощника не получены ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
                    box.Children.Add(Hint(T("Не удалось получить настройки помощника: ", "Жардамчынын жөндөөлөрүн алуу мүмкүн болгон жок: ", "Couldn't get the assistant settings: ",
                        "Asistan ayarları alınamadı: ", "Yordamchi sozlamalarini olib bo'lmadi: ") + SiteAssistantApi.FriendlyError(ex)));
                    var again = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
                    again.Click += async (_, _) =>
                    {
                        box.Children.Clear();
                        box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
                        await FillChatAsync(box, force: true).ConfigureAwait(true);
                    };
                    box.Children.Add(again);
                    return;
                }
                if (loaded is not null)
                    _asst = (DateTime.UtcNow, loaded);
            }
            if (loaded is null)
            {
                box.Children.Add(soon);
                return;
            }
            var s = loaded;

            box.Children.Add(Hint(T("Помощник отвечает покупателям в чате сайта: подбирает размер по вашей таблице, показывает наличие и цены из каталога, рассказывает о доставке и обмене, "
                                    + "собирает корзину (оформляет покупатель сам) и зовёт продавца. Числа в его ответах сервер сверяет с каталогом; неуверенный ответ — «Уточню у продавца». "
                                    + "Он отвечает как ИИ и может ошибаться — покупатели видят это предупреждение.",
                "Жардамчы сайттын чатында кардарларга жооп берет: өлчөмдү таблицаңыз боюнча тандайт, каталогдон бар-жогун жана баасын көрсөтөт, жеткирүү жана алмаштыруу жөнүндө айтат, "
                + "себетти чогултат (тариздөөнү кардар өзү жасайт) жана сатуучуну чакырат. Жоопторундагы сандарды сервер каталог менен салыштырат; ишенбеген жооп — «Сатуучудан тактайм». "
                + "Ал ЖИ катары жооп берет жана жаңылышы мүмкүн — кардарлар бул эскертүүнү көрөт.",
                "The assistant answers customers in the website chat: advises a size from your table, shows availability and prices from the catalogue, explains delivery and exchange, "
                + "builds a cart (the customer checks out) and calls the seller. The server checks the numbers in its answers against the catalogue; when unsure it says “I'll check with the seller”. "
                + "It answers as an AI and may be wrong — customers see this notice.",
                "Asistan sitenin sohbetinde müşterilere yanıt verir: tablonuza göre beden önerir, katalogdan stok ve fiyatı gösterir, teslimat ve değişimi anlatır, "
                + "sepet hazırlar (siparişi müşteri verir) ve satıcıyı çağırır. Yanıtlardaki sayıları sunucu katalogla karşılaştırır; emin değilse «Satıcıya soracağım» der. "
                + "Yapay zekâ olarak yanıt verir ve yanılabilir — müşteriler bu uyarıyı görür.",
                "Yordamchi sayt chatida xaridorlarga javob beradi: o'lchamni jadvalingiz bo'yicha tanlaydi, katalogdan mavjudlik va narxni ko'rsatadi, yetkazish va almashtirish haqida aytadi, "
                + "savat yig'adi (rasmiylashtirishni xaridor o'zi qiladi) va sotuvchini chaqiradi. Javoblardagi raqamlarni server katalog bilan solishtiradi; ishonchi bo'lmasa — «Sotuvchidan aniqlayman». "
                + "U SI sifatida javob beradi va adashishi mumkin — xaridorlar bu ogohlantirishni ko'radi.")));
            if (!caps.Contains(ShowcaseCapabilities.Chat))
                box.Children.Add(Hint(T("Чат на сайте сервер пока не включил — помощник заработает вместе с ним.", "Сайттагы чатты сервер азырынча күйгүзгөн жок — жардамчы аны менен бирге иштейт.",
                    "The server hasn't enabled the website chat yet — the assistant will start together with it.", "Sunucu sitedeki sohbeti henüz açmadı — asistan onunla birlikte çalışacak.",
                    "Server saytdagi chatni hali yoqmagan — yordamchi u bilan birga ishlaydi.")));

            // ── Настройки ──
            var enabled = new CheckBox
            {
                Content = T("Помощник отвечает покупателям на сайте", "Жардамчы сайттагы кардарларга жооп берет", "The assistant answers customers on the website",
                    "Asistan sitedeki müşterilere yanıt verir", "Yordamchi saytdagi xaridorlarga javob beradi"),
                IsChecked = s.Enabled,
            };
            Use(enabled, CheckBox.ForegroundProperty, "BrushText");
            box.Children.Add(enabled);

            var limitField = Field(box, T("Потолок вызовов ИИ в сутки (пусто — как задано сервером)", "Суткасына ЖИ чакыруулар чеги (бош — сервер койгондой)",
                "AI calls per day ceiling (empty — as set by the server)", "Günlük yapay zekâ çağrı sınırı (boş — sunucudaki gibi)", "Sutkada SI chaqiruvlari chegarasi (bo'sh — server belgilaganidek)"));
            var limitBox = new TextBox { Text = s.DailyLimit?.ToString(CultureInfo.InvariantCulture) ?? "", MinHeight = 38, Width = 180, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 7 };
            limitField.Children.Add(limitBox);
            limitField.Children.Add(Hint(T("Когда потолок исчерпан, сайт без ИИ покажет «Помощник занят» и кнопки связи (Telegram, WhatsApp, телефон), а сообщение дойдёт до продавца.",
                "Чек түгөнгөндө сайт ЖИсиз «Жардамчы бош эмес» жана байланыш баскычтарын (Telegram, WhatsApp, телефон) көрсөтөт, билдирүү сатуучуга жетет.",
                "When the ceiling is reached, the website shows “Assistant is busy” and contact buttons (Telegram, WhatsApp, phone) without AI, and the message still reaches the seller.",
                "Sınır dolunca site yapay zekâsız «Asistan meşgul» ve iletişim düğmelerini (Telegram, WhatsApp, telefon) gösterir, mesaj yine de satıcıya ulaşır.",
                "Chegara tugaganda sayt SIsiz «Yordamchi band» va aloqa tugmalarini (Telegram, WhatsApp, telefon) ko'rsatadi, xabar baribir sotuvchiga yetadi.")));
            var usage = Hint("");
            void RenderUsage(AssistantSettings cur)
            {
                usage.Text = cur.UsedToday is { } used
                    ? T($"Сегодня вызовов ИИ: {used}", $"Бүгүн ЖИ чакыруулар: {used}", $"AI calls today: {used}", $"Bugünkü yapay zekâ çağrıları: {used}", $"Bugun SI chaqiruvlari: {used}")
                      + (cur.DailyLimit is { } lim && lim > 0 ? T($" из {lim}", $" / {lim}", $" of {lim}", $" / {lim}", $" / {lim}") : "")
                    : "";
                usage.IsVisible = usage.Text.Length > 0;
            }
            RenderUsage(s);
            box.Children.Add(usage);

            var keyField = Field(box, T("Ключ Gemini вашего магазина", "Дүкөнүңүздүн Gemini ачкычы", "Your shop's Gemini key", "Mağazanızın Gemini anahtarı", "Do'koningizning Gemini kaliti"));
            string KeyWatermark(bool set) => set
                ? T("Ключ задан — введите новый, чтобы заменить", "Ачкыч коюлган — алмаштыруу үчүн жаңысын жазыңыз", "Key is set — enter a new one to replace it", "Anahtar tanımlı — değiştirmek için yenisini girin", "Kalit o'rnatilgan — almashtirish uchun yangisini kiriting")
                : T("Вставьте ключ из Google AI Studio (AIza…)", "Google AI Studio ачкычын жабыштырыңыз (AIza…)", "Paste the key from Google AI Studio (AIza…)", "Google AI Studio anahtarını yapıştırın (AIza…)", "Google AI Studio kalitini qo'ying (AIza…)");
            var keyBox = UiKit.SecretInput(this, KeyWatermark(s.KeySet), 44);
            keyBox.PasswordChar = '•';
            keyBox.MaxLength = 200;
            keyField.Children.Add(keyBox);
            var keyState = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            void RenderKeyState(bool set) => SetStatusText(keyState,
                set ? T("✓ Ключ задан и хранится на сервере. Назад он не показывается.", "✓ Ачкыч коюлган жана серверде сакталат. Кайра көрсөтүлбөйт.", "✓ The key is set and stored on the server. It is never shown back.",
                        "✓ Anahtar tanımlı ve sunucuda saklanıyor. Geri gösterilmez.", "✓ Kalit o'rnatilgan va serverda saqlanadi. Qayta ko'rsatilmaydi.")
                    : T("⚠ Ключ не задан — без него помощник «недоступен».", "⚠ Ачкыч коюлган эмес — ансыз жардамчы «жеткиликсиз».", "⚠ No key — without it the assistant is “unavailable”.",
                        "⚠ Anahtar yok — onsuz asistan «kullanılamıyor».", "⚠ Kalit yo'q — usiz yordamchi «mavjud emas»."),
                set ? "BrushSuccess" : "BrushWarning");
            RenderKeyState(s.KeySet);
            keyField.Children.Add(keyState);
            keyField.Children.Add(Hint(T("Ключ у каждого магазина свой: общего ключа NurCRM нет. Он уходит на сервер при «Сохранить» и нигде не показывается и не пишется в журнал. "
                                         + "Бесплатный ключ Google делит лимит с Telegram-ботом, и Google может использовать вопросы покупателей для улучшения своих продуктов — для сайта лучше платный ключ.",
                "Ачкыч ар бир дүкөндө өзүнчө: NurCRMдин жалпы ачкычы жок. Ал «Сактоодо» серверге кетет, эч жерде көрсөтүлбөйт жана журналга жазылбайт. "
                + "Google акысыз ачкычы лимитти Telegram-бот менен бөлүшөт, Google кардарлардын суроолорун өз өнүмдөрүн жакшыртууга колдониши мүмкүн — сайт үчүн акы төлөнүүчү ачкыч жакшы.",
                "Every shop has its own key: there is no shared NurCRM key. It goes to the server on “Save” and is never shown or written to the log. "
                + "A free Google key shares its limit with the Telegram bot, and Google may use customers' questions to improve its products — a paid key is better for the website.",
                "Her mağazanın kendi anahtarı vardır: ortak bir NurCRM anahtarı yoktur. «Kaydet»te sunucuya gider, hiçbir yerde gösterilmez ve günlüğe yazılmaz. "
                + "Ücretsiz Google anahtarı limiti Telegram botuyla paylaşır ve Google müşteri sorularını ürünlerini geliştirmek için kullanabilir — site için ücretli anahtar daha iyi.",
                "Har bir do'konning o'z kaliti bor: NurCRMning umumiy kaliti yo'q. U «Saqlash»da serverga ketadi, hech qayerda ko'rsatilmaydi va jurnalga yozilmaydi. "
                + "Google bepul kaliti limitni Telegram-bot bilan bo'lishadi, Google xaridorlar savollarini o'z mahsulotlarini yaxshilashga ishlatishi mumkin — sayt uchun pullik kalit yaxshiroq.")));
            var getKey = UiKit.Ghost(this, T("Где взять ключ", "Ачкычты кайдан алуу", "Where to get a key", "Anahtar nereden alınır", "Kalitni qayerdan olish"));
            getKey.HorizontalAlignment = HorizontalAlignment.Left;
            getKey.Click += (_, _) => SiteOrdersWindow.OpenUrl("https://aistudio.google.com/apikey");
            keyField.Children.Add(getKey);

            var saveStatus = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            var save = UiKit.Primary(this, T("Сохранить настройки помощника", "Жардамчынын жөндөөлөрүн сактоо", "Save assistant settings", "Asistan ayarlarını kaydet", "Yordamchi sozlamalarini saqlash"));
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Click += async (_, _) =>
            {
                bool? newEnabled = enabled.IsChecked == true != s.Enabled ? enabled.IsChecked == true : null;
                int? newLimit = null;
                var limitText = (limitBox.Text ?? "").Trim();
                if (limitText.Length > 0)
                {
                    if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                    {
                        saveStatus.IsVisible = true;
                        SetStatusText(saveStatus, T("Потолок — целое число не меньше 0.", "Чек — 0дөн кем эмес бүтүн сан.", "The ceiling must be a whole number, 0 or more.", "Sınır 0 veya daha büyük bir tam sayı olmalı.", "Chegara — 0 yoki undan katta butun son."), "BrushWarning");
                        return;
                    }
                    if (parsed != s.DailyLimit)
                        newLimit = parsed;
                }
                var newKey = keyBox.Text;
                if (newEnabled is null && newLimit is null && string.IsNullOrWhiteSpace(newKey))
                {
                    saveStatus.IsVisible = true;
                    SetStatusText(saveStatus, T("Нечего сохранять — настройки не менялись.", "Сактоого эч нерсе жок — жөндөөлөр өзгөргөн жок.", "Nothing to save — the settings haven't changed.",
                        "Kaydedilecek bir şey yok — ayarlar değişmedi.", "Saqlaydigan narsa yo'q — sozlamalar o'zgarmagan."), "BrushTextSoft");
                    return;
                }
                save.IsEnabled = false;
                saveStatus.IsVisible = true;
                SetStatusText(saveStatus, T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…"), "BrushTextSoft");
                keyBox.Text = ""; // 2026-11-01 (В-2): ключ в окне не держим — после отправки поле пустое, при отказе сервера ключ вводится заново
                try
                {
                    var updated = await api.SaveSettingsAsync(s, newEnabled, newLimit, newKey).ConfigureAwait(true);
                    // Если сервер в ответе не прислал признак ключа, а мы ключ отправили — он задан.
                    if (!string.IsNullOrWhiteSpace(newKey) && !updated.KeySet)
                        updated = updated with { KeySet = true };
                    s = updated;
                    _asst = (DateTime.UtcNow, s);
                    keyBox.Text = "";
                    keyBox.Watermark = KeyWatermark(s.KeySet);
                    RenderKeyState(s.KeySet);
                    enabled.IsChecked = s.Enabled;
                    limitBox.Text = s.DailyLimit?.ToString(CultureInfo.InvariantCulture) ?? "";
                    RenderUsage(s);
                    SetStatusText(saveStatus, T("Сохранено на сервере.", "Серверде сакталды.", "Saved on the server.", "Sunucuda kaydedildi.", "Serverda saqlandi."), "BrushSuccess");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetStatusText(saveStatus, T("Не сохранилось: ", "Сакталган жок: ", "Not saved: ", "Kaydedilemedi: ", "Saqlanmadi: ") + SiteAssistantApi.FriendlyError(ex), "BrushWarning");
                    PosLogger.Log($"Редактор сайта: настройки помощника не сохранены ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
                }
                finally
                {
                    save.IsEnabled = true;
                }
            };
            box.Children.Add(save);
            box.Children.Add(saveStatus);

            // ── Проверить помощника ──
            var testField = Field(box, T("Проверить помощника", "Жардамчыны текшерүү", "Test the assistant", "Asistanı dene", "Yordamchini tekshirish"));
            testField.Children.Add(Hint(T("Пробный вопрос уйдёт помощнику по сохранённым на сервере настройкам, как вопрос покупателя. Один вызов ИИ входит в суточный потолок.",
                "Сыноо суроосу жардамчыга серверде сакталган жөндөөлөр боюнча кардардын суроосундай барат. Бир ЖИ чакыруу суткалык чекке кирет.",
                "The test question goes to the assistant with the settings saved on the server, like a customer's question. One AI call counts toward the daily ceiling.",
                "Deneme sorusu, müşteri sorusu gibi, sunucuda kayıtlı ayarlarla asistana gider. Bir yapay zekâ çağrısı günlük sınıra sayılır.",
                "Sinov savoli serverda saqlangan sozlamalar bo'yicha xaridor savolidek yordamchiga boradi. Bitta SI chaqiruvi sutkalik chegaraga kiradi.")));
            var questionBox = new TextBox
            {
                Text = T("Какие у вас часы работы и есть ли доставка?", "Иштөө убактыңыз кандай жана жеткирүү барбы?", "What are your opening hours and do you deliver?",
                    "Çalışma saatleriniz nedir, teslimat var mı?", "Ish vaqtingiz qanday va yetkazib berish bormi?"),
                MinHeight = 38, MaxLength = 300, VerticalContentAlignment = VerticalAlignment.Center,
            };
            testField.Children.Add(questionBox);
            var test = UiKit.Ghost(this, T("Проверить помощника", "Жардамчыны текшерүү", "Test the assistant", "Asistanı dene", "Yordamchini tekshirish"));
            test.HorizontalAlignment = HorizontalAlignment.Left;
            var testResult = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            test.Click += async (_, _) =>
            {
                var question = (questionBox.Text ?? "").Trim();
                if (question.Length == 0)
                    return;
                test.IsEnabled = false;
                testResult.IsVisible = true;
                SetStatusText(testResult, T("Спрашиваю помощника…", "Жардамчыдан сурап жатам…", "Asking the assistant…", "Asistana soruluyor…", "Yordamchidan so'ralmoqda…"), "BrushTextSoft");
                try
                {
                    var r = await api.TestAsync(question, KnowledgeLangs.Contains(_lang) ? _lang : "ru").ConfigureAwait(true);
                    var text = new StringBuilder();
                    if (r.Busy)
                        text.AppendLine(SiteAssistantApi.FriendlyError(new ApiException("busy", 429)));
                    else if (string.IsNullOrWhiteSpace(r.Answer))
                        text.AppendLine(T("Помощник вернул пустой ответ — Google Gemini перегружен или ключ не подошёл. Повторите через минуту.",
                            "Жардамчы бош жооп кайтарды — Google Gemini жүктөлгөн же ачкыч жарабады. Бир мүнөттөн кийин кайталаңыз.",
                            "The assistant returned an empty answer — Google Gemini is overloaded or the key was rejected. Try again in a minute.",
                            "Asistan boş yanıt döndürdü — Google Gemini yoğun veya anahtar kabul edilmedi. Bir dakika sonra tekrar deneyin.",
                            "Yordamchi bo'sh javob qaytardi — Google Gemini band yoki kalit mos kelmadi. Bir daqiqadan keyin qayta urinib ko'ring."));
                    else
                        text.AppendLine(T("Ответ: ", "Жооп: ", "Answer: ", "Yanıt: ", "Javob: ") + r.Answer);
                    text.Append(T("Вызвано функций: ", "Чакырылган функциялар: ", "Functions called: ", "Çağrılan işlevler: ", "Chaqirilgan funksiyalar: ")
                                + (r.Functions.Count == 0 ? T("нет", "жок", "none", "yok", "yo'q") : string.Join(", ", r.Functions)));
                    if (r.CardCount > 0)
                        text.Append(T($" · карточек товаров: {r.CardCount}", $" · товар карточкалары: {r.CardCount}", $" · product cards: {r.CardCount}", $" · ürün kartı: {r.CardCount}", $" · mahsulot kartochkalari: {r.CardCount}"));
                    if (r.NumbersReplaced)
                        text.Append('\n').Append(T("Сервер заменил неподтверждённые числа в ответе на «Уточню у продавца».", "Сервер ырасталбаган сандарды жооптогу «Сатуучудан тактайм» менен алмаштырды.",
                            "The server replaced unconfirmed numbers in the answer with “I'll check with the seller”.", "Sunucu yanıttaki doğrulanmamış sayıları «Satıcıya soracağım» ile değiştirdi.",
                            "Server javobdagi tasdiqlanmagan raqamlarni «Sotuvchidan aniqlayman» bilan almashtirdi."));
                    SetStatusText(testResult, text.ToString(), r.Busy || string.IsNullOrWhiteSpace(r.Answer) ? "BrushWarning" : "BrushText");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetStatusText(testResult, T("Помощник не ответил: ", "Жардамчы жооп берген жок: ", "The assistant didn't answer: ", "Asistan yanıt vermedi: ", "Yordamchi javob bermadi: ") + SiteAssistantApi.FriendlyError(ex), "BrushWarning");
                    PosLogger.Log($"Редактор сайта: проверка помощника не прошла ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
                }
                finally
                {
                    test.IsEnabled = true;
                }
            };
            testField.Children.Add(test);
            testField.Children.Add(testResult);

            BuildKnowledgeBlock(box);
            LockInputs(box);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            box.Children.Clear();
            box.Children.Add(Hint(T("Не загрузилось: ", "Жүктөлгөн жок: ", "Didn't load: ", "Yüklenmedi: ", "Yuklanmadi: ") + ServerOutageMonitor.FriendlyError(ex)));
            PosLogger.Log($"Редактор сайта: раздел «Чат на сайте» не построен ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
        }
    }

    // ───────────────────────────────────────────── ИИ-2: «Знания для помощника»

    /// <summary>Текст «Правила магазина» для выбранного языка: ключ дизайна shop.info.knowledge.{язык}. Старое значение-строка читается как русское.</summary>
    private string KnowledgeSaved(string lang)
    {
        var node = Get(KnowledgePath);
        if (node is JsonObject o)
            return o[lang]?.ToString() ?? "";
        return lang == "ru" && node is JsonValue v ? v.ToString() : "";
    }

    private void BuildKnowledgeBlock(StackPanel box)
    {
        var field = Field(box, T("Знания для помощника (правила магазина)", "Жардамчы үчүн билим (дүкөндүн эрежелери)", "Knowledge for the assistant (shop rules)",
            "Asistan için bilgi (mağaza kuralları)", "Yordamchi uchun bilim (do'kon qoidalari)"));
        field.Children.Add(Hint(T("Помощник цитирует этот текст в ответах про обмен и возврат, примерку, доставку, часы и адрес — и не выдумывает правила. До 2000 знаков на язык. "
                                  + "Программа может собрать черновик из ваших настроек (срок обмена, список «без обмена», адрес, часы, доставка, оплата) — проверьте и поправьте его. "
                                  + "В черновик сайта текст уходит только по «Сохранить знания»; покупатели и помощник увидят его после «Опубликовать».",
            "Жардамчы бул текстти алмаштыруу жана кайтаруу, кийип көрүү, жеткирүү, иштөө убактысы жана дарек жөнүндө жооптордо келтирет — эрежелерди ойлоп таппайт. Ар тилге 2000 белгиге чейин. "
            + "Программа жөндөөлөрүңүздөн долбоор түзө алат (алмаштыруу мөөнөтү, «алмаштырылбайт» тизмеси, дарек, убакыт, жеткирүү, төлөм) — текшерип, оңдоңуз. "
            + "Текст сайттын долбоорун «Билимди сактоо» басканда гана кетет; кардарлар жана жардамчы аны «Жарыялоодон» кийин көрөт.",
            "The assistant quotes this text when answering about exchange and returns, trying on, delivery, hours and address — and doesn't invent rules. Up to 2000 characters per language. "
            + "The program can compose a draft from your settings (exchange period, “no exchange” list, address, hours, delivery, payment) — check and edit it. "
            + "The text goes to the website draft only on “Save knowledge”; customers and the assistant see it after “Publish”.",
            "Asistan değişim ve iade, deneme, teslimat, saatler ve adres sorularında bu metni alıntılar — kural uydurmaz. Dil başına 2000 karaktere kadar. "
            + "Program ayarlarınızdan (değişim süresi, «değişmez» listesi, adres, saatler, teslimat, ödeme) bir taslak hazırlayabilir — kontrol edip düzeltin. "
            + "Metin siteye yalnızca «Bilgiyi kaydet»te taslak olarak gider; müşteriler ve asistan «Yayınla»dan sonra görür.",
            "Yordamchi bu matnni almashtirish va qaytarish, kiyib ko'rish, yetkazish, ish vaqti va manzil haqidagi javoblarda keltiradi — qoidalarni o'ylab topmaydi. Har til uchun 2000 belgigacha. "
            + "Dastur sozlamalaringizdan qoralama tuzishi mumkin (almashtirish muddati, «almashtirilmaydi» ro'yxati, manzil, vaqt, yetkazish, to'lov) — tekshirib, tuzating. "
            + "Matn sayt qoralamasiga faqat «Bilimni saqlash»da ketadi; xaridorlar va yordamchi uni «E'lon qilish»dan keyin ko'radi.")));

        var texts = KnowledgeLangs.ToDictionary(l => l, KnowledgeSaved);
        var lang = KnowledgeLangs.Contains(_lang) ? _lang : "ru";
        var text = new TextBox
        {
            Text = texts[lang], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170, MaxLength = KnowledgeMax,
            Watermark = T("Например: обмен в течение 14 дней с чеком; примерка бесплатно; доставка по городу 150 сом.", "Мисалы: чек менен 14 күндүн ичинде алмаштыруу; кийип көрүү акысыз; шаар боюнча жеткирүү 150 сом.",
                "For example: exchange within 14 days with the receipt; free trying on; delivery in the city 150 som.", "Örneğin: fişle 14 gün içinde değişim; deneme ücretsiz; şehir içi teslimat 150 som.",
                "Masalan: chek bilan 14 kun ichida almashtirish; kiyib ko'rish bepul; shahar bo'ylab yetkazish 150 so'm."),
        };
        var counter = Hint("");
        var status = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var chips = new WrapPanel();

        void RenderCounter() => counter.Text = $"{(text.Text ?? "").Length} / {KnowledgeMax}";
        void RenderChips()
        {
            chips.Children.Clear();
            foreach (var code in KnowledgeLangs)
            {
                var chip = Chip(KnowledgeLangName(code) + (texts[code].Trim().Length > 0 ? " ●" : ""), code == lang);
                chip.Click += (_, _) =>
                {
                    texts[lang] = text.Text ?? "";
                    lang = code;
                    text.Text = texts[lang];
                    status.IsVisible = false;
                    RenderChips();
                    RenderCounter();
                };
                chips.Children.Add(chip);
            }
        }
        RenderChips();
        RenderCounter();
        text.TextChanged += (_, _) =>
        {
            RenderCounter();
            if ((text.Text ?? "") != KnowledgeSaved(lang))
            {
                status.IsVisible = true;
                SetStatusText(status, T("Есть несохранённые правки.", "Сакталбаган оңдоолор бар.", "There are unsaved edits.", "Kaydedilmemiş düzenlemeler var.", "Saqlanmagan tuzatishlar bor."), "BrushTextSoft");
            }
        };

        field.Children.Add(chips);
        field.Children.Add(text);
        field.Children.Add(counter);

        var buttons = new WrapPanel();
        var compose = UiKit.Ghost(this, T("Собрать из программы", "Программадан түзүү", "Compose from the program", "Programdan hazırla", "Dasturdan tuzish"));
        compose.Margin = new Thickness(0, 0, 8, 6);
        compose.Click += async (_, _) =>
        {
            var draft = BuildKnowledgeDraft(lang);
            status.IsVisible = true;
            if (draft.Length == 0)
            {
                SetStatusText(status, T("В программе пока нет данных для этого текста — напишите правила своими словами.", "Программада бул текст үчүн маалымат жок — эрежелерди өз сөзүңүз менен жазыңыз.",
                    "The program has no data for this text yet — write the rules in your own words.", "Programda bu metin için henüz veri yok — kuralları kendi sözlerinizle yazın.",
                    "Dasturda bu matn uchun hali ma'lumot yo'q — qoidalarni o'z so'zlaringiz bilan yozing."), "BrushWarning");
                return;
            }
            if ((text.Text ?? "").Trim().Length > 0 && (text.Text ?? "").Trim() != draft.Trim()
                && !await ConfirmAsync(T("Заменить текст в окне собранным из программы черновиком? Сохранённый на сайте текст не изменится, пока вы не нажмёте «Сохранить знания».",
                        "Терезедеги тексти программадан түзүлгөн долбоорго алмаштыруу керекпи? Сактоо басылмайынча, сайтта сакталган текст өзгөрбөйт.",
                        "Replace the text in the box with the draft composed from the program? The text saved for the website won't change until you press “Save knowledge”.",
                        "Penceredeki metin programdan hazırlanan taslakla değiştirilsin mi? «Bilgiyi kaydet»e basmadıkça siteye kaydedilen metin değişmez.",
                        "Oynadagi matn dasturdan tuzilgan qoralama bilan almashtirilsinmi? «Bilimni saqlash» bosilmaguncha saytda saqlangan matn o'zgarmaydi."),
                    T("Заменить", "Алмаштыруу", "Replace", "Değiştir", "Almashtirish"), T("Оставить мой текст", "Менин текстимди калтыруу", "Keep my text", "Metnimi koru", "Mening matnimni qoldirish")).ConfigureAwait(true))
                return;
            text.Text = draft;
            SetStatusText(status, T("Черновик собран из настроек программы — проверьте и поправьте. На сайт он уйдёт только после «Сохранить знания».",
                "Долбоор программанын жөндөөлөрүнөн түзүлдү — текшерип, оңдоңуз. Сайтка «Билимди сактоо» басылгандан кийин гана кетет.",
                "The draft is composed from the program settings — check and edit it. It goes to the website only after “Save knowledge”.",
                "Taslak program ayarlarından hazırlandı — kontrol edip düzeltin. Siteye yalnızca «Bilgiyi kaydet»ten sonra gider.",
                "Qoralama dastur sozlamalaridan tuzildi — tekshirib, tuzating. Saytga faqat «Bilimni saqlash»dan keyin ketadi."), "BrushTextSoft");
        };
        var saveKnowledge = UiKit.Primary(this, T("Сохранить знания", "Билимди сактоо", "Save knowledge", "Bilgiyi kaydet", "Bilimni saqlash"));
        saveKnowledge.Margin = new Thickness(0, 0, 8, 6);
        saveKnowledge.Click += (_, _) =>
        {
            var value = (text.Text ?? "").Trim();
            if (value.Length > KnowledgeMax)
                value = value[..KnowledgeMax];
            texts[lang] = value;
            if (Get(KnowledgePath) is JsonValue legacy) // старое значение-строка → переносим в русский, остальные языки добавятся рядом
            {
                var asObject = new JsonObject { ["ru"] = legacy.ToString() };
                SetIn(_draft, KnowledgePath, asObject);
            }
            Set($"{KnowledgePath}.{lang}", JsonValue.Create(value));
            RenderChips();
            status.IsVisible = true;
            SetStatusText(status, T("Сохранено в черновик сайта. Нажмите «Опубликовать» вверху — тогда помощник начнёт цитировать этот текст.",
                "Сайттын долбоорунда сакталды. Жогорудагы «Жарыялоо» басыңыз — ошондо жардамчы бул текстти келтире баштайт.",
                "Saved to the website draft. Press “Publish” at the top — then the assistant starts quoting this text.",
                "Site taslağına kaydedildi. Üstteki «Yayınla»ya basın — asistan bu metni o zaman alıntılamaya başlar.",
                "Sayt qoralamasiga saqlandi. Yuqoridagi «E'lon qilish»ni bosing — shunda yordamchi bu matnni keltira boshlaydi."), "BrushSuccess");
        };
        buttons.Children.Add(compose);
        buttons.Children.Add(saveKnowledge);
        field.Children.Add(buttons);
        field.Children.Add(status);
    }

    /// <summary>Черновик «Правил магазина» из того, что уже есть в программе (только чтение): срок обмена и список «без обмена»
    /// (NonExchangeableRules, сфера «Одежда»), адрес, часы, телефоны, самовывоз/доставка и оплата из дизайна сайта. Пусто — собирать не из чего.</summary>
    private string BuildKnowledgeDraft(string lang)
    {
        // Подписи — на выбранном языке знаний; для kk — русские (в программе казахских надписей нет).
        string[] w = lang switch
        {
            "ky" => new[]
            {
                "Алмаштыруу жана кайтаруу: сатып алгандан кийин {0} күндүн ичинде.", "Кемтиксиз алмаштырылбайт: {0}.", "Дарек: {0}.", "Иштөө убактысы: {0}.", "Телефондор: {0}.",
                "Өзү алып кетүүгө болот.", "Жеткирүү: {0}.", "Жеткирүүнүн баасы {0} сом.", "{0} сомдон акысыз.", "Төлөө: {0}.",
            },
            "en" => new[]
            {
                "Exchange and return: within {0} days after purchase.", "Not exchangeable unless defective: {0}.", "Address: {0}.", "Opening hours: {0}.", "Phones: {0}.",
                "Pickup is available.", "Delivery: {0}.", "Delivery costs {0} som.", "Free from {0} som.", "Payment: {0}.",
            },
            "uz" => new[]
            {
                "Almashtirish va qaytarish: xariddan keyin {0} kun ichida.", "Nuqsonsiz almashtirilmaydi: {0}.", "Manzil: {0}.", "Ish vaqti: {0}.", "Telefonlar: {0}.",
                "Olib ketish mumkin.", "Yetkazib berish: {0}.", "Yetkazib berish narxi {0} so'm.", "{0} so'mdan bepul.", "To'lov: {0}.",
            },
            _ => new[]
            {
                "Обмен и возврат: в течение {0} дн. после покупки.", "Не подлежит обмену без брака: {0}.", "Адрес: {0}.", "Часы работы: {0}.", "Телефоны: {0}.",
                "Самовывоз возможен.", "Доставка: {0}.", "Стоимость доставки {0} сом.", "Бесплатно от {0} сом.", "Оплата: {0}.",
            },
        };

        // Текст дизайна на нужном языке; нет — на языке сайта, затем любой.
        string Design(string path)
        {
            var n = Get(path);
            if (n is JsonObject o)
            {
                foreach (var key in new[] { lang, _lang })
                    if (o[key]?.ToString() is { Length: > 0 } t && t.Trim().Length > 0)
                        return t.Trim();
                return o.Select(p => p.Value?.ToString() ?? "").FirstOrDefault(t => t.Trim().Length > 0)?.Trim() ?? "";
            }
            return n is JsonValue v ? v.ToString().Trim() : "";
        }
        string Num(string path) => Number(path) is { } d && d > 0 ? d.ToString("0.##", CultureInfo.InvariantCulture) : "";

        var lines = new List<string>();
        var days = NonExchangeableRules.DaysLimit;
        if (days > 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[0], days));
            var no = NonExchangeableRules.SelectedCategories().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (no.Count > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, w[1], string.Join(", ", no)));
        }
        if (Design("footer.address") is { Length: > 0 } address)
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[2], address));
        if (Design("footer.hours") is { Length: > 0 } hours)
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[3], hours));
        if (Get("footer.phones") is JsonArray phones && phones.Select(p => p?.ToString() ?? "").Where(p => p.Trim().Length > 0).ToList() is { Count: > 0 } phoneList)
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[4], string.Join(", ", phoneList)));
        if (Flag("cart.delivery.pickup"))
            lines.Add(w[5]);
        if (Flag("cart.delivery.delivery"))
        {
            var zones = Design("cart.delivery.zones_text");
            var available = lang switch { "ky" => "бар", "en" => "available", "uz" => "mavjud", _ => "есть" };
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[6], zones.Length > 0 ? zones : available));
            if (Num("cart.delivery.delivery_fee") is { Length: > 0 } fee)
                lines.Add(string.Format(CultureInfo.InvariantCulture, w[7], fee));
            if (Num("cart.delivery.free_from") is { Length: > 0 } free)
                lines.Add(string.Format(CultureInfo.InvariantCulture, w[8], free));
        }
        if (Design("cart.payment_text") is { Length: > 0 } pay)
            lines.Add(string.Format(CultureInfo.InvariantCulture, w[9], pay));

        // До 2000 знаков — целыми строками.
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > KnowledgeMax)
                break;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}
