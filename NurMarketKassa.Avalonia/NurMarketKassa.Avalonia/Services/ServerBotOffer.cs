using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-01, владелец: «у других клиентов тоже на сервере будет? — да, и оставь в
/// настройках перенос на сервер». После обновления программа ОДИН раз спрашивает клиента, у которого
/// бот подключён и работает на компьютере: перенести ли его на сервер NurCRM (тогда он отвечает
/// круглые сутки). Без согласия токен и ключ ИИ на сервер не уходят — правило «обновление не трогает
/// настройки клиента». Ответ запоминается в общем для кассы и программы владельца файле, чтобы
/// вторая программа не спросила снова. Кнопка переноса остаётся в мастере бота
/// (Настройки → Операции → «Подключить бота»).</summary>
public static class ServerBotOffer
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(20);
    private static bool _scheduled;

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-server-offer.done");

    /// <summary>Через 20 с после открытия окна (чтобы не наложиться на «Что нового» и вход).</summary>
    public static void Schedule(Window owner)
    {
        if (_scheduled || File.Exists(MarkerPath))
            return;
        _scheduled = true;
        var timer = new DispatcherTimer { Interval = Delay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await OfferAsync(owner).ConfigureAwait(true);
        };
        timer.Start();
    }

    private static async Task OfferAsync(Window owner)
    {
        try
        {
            if (File.Exists(MarkerPath) || !owner.IsVisible)
                return;
            UserPreferences.AdoptTelegramBotFromOtherApp();
            // Бота нет — переносить нечего; спросим, когда подключат (кнопка в мастере есть всегда).
            if (!TelegramBotService.IsConfigured || !TariffGate.CanUseTelegramBot)
                return;
            if (App.AppHost?.Services.GetService(typeof(ServerTelegramBotApi)) is not ServerTelegramBotApi api)
                return;

            var settings = await api.GetSettingsAsync().ConfigureAwait(true);
            if (settings is null)
                return; // сервер без бота — спросим после его обновления
            // 2026-10-02, владелец: «на сервер выгрузи всё равно бота и ИИ» — спрашиваем, не дожидаясь
            // полей здоровья сервера (last_reply_at); скорость ответа сервера — ТЗ часть 7, раздел 1.
            if (settings.IsServerMode)
            {
                MarkDone();
                return;
            }

            var bot = string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotUsername) ? "" : " @" + UserPreferences.Instance.TelegramBotUsername;
            var answer = PosMessageBox.Show(owner,
                Tr.T(
                    $"Перенести телеграм-бот{bot} на сервер NurCRM?\n\nСейчас он отвечает, только пока на этом компьютере открыта касса или программа владельца. На сервере он будет отвечать круглые сутки, даже когда компьютер выключен: команды, отчёты, ИИ, консультант для покупателей, заказы и сводка по закрытию смены.\n\nТокен бота и ключ ИИ будут храниться на сервере NurCRM в зашифрованном виде. Передумаете — «Настройки → Операции → Подключить бота → Вернуть на этот компьютер».",
                    $"Телеграм-ботту{bot} NurCRM серверине көчүрөлүбү?\n\nАзыр ал бул компьютерде касса же ээсинин программасы ачык турганда гана жооп берет. Серверде ал компьютер өчүк болсо да күнү-түнү жооп берет: буйруктар, отчёттор, ЖИ, сатып алуучулар үчүн кеңешчи, заказдар жана смена жабылганда жыйынтык.\n\nБоттун токени жана ЖИ ачкычы NurCRM серверинде шифрленип сакталат. Ойуңуз өзгөрсө — «Жөндөөлөр → Операциялар → Ботту туташтыруу → Бул компьютерге кайтаруу».",
                    $"Move the Telegram bot{bot} to the NurCRM server?\n\nRight now it answers only while the kassa or the owner app is open on this computer. On the server it will answer around the clock, even when the computer is off: commands, reports, AI, the customer consultant, orders and the shift-close summary.\n\nThe bot token and the AI key will be stored encrypted on the NurCRM server. Changed your mind — “Settings → Operations → Connect bot → Move back to this computer”.",
                    $"Telegram botu{bot} NurCRM sunucusuna taşınsın mı?\n\nŞu anda yalnızca bu bilgisayarda kasa veya işletme sahibi programı açıkken yanıt veriyor. Sunucuda bilgisayar kapalıyken bile günün her saati yanıt verir: komutlar, raporlar, yapay zekâ, müşteri danışmanı, siparişler ve vardiya kapanış özeti.\n\nBot token'ı ve yapay zekâ anahtarı NurCRM sunucusunda şifreli saklanır. Fikrinizi değiştirirseniz — «Ayarlar → İşlemler → Botu bağla → Bu bilgisayara geri al».",
                    $"Telegram bot{bot} NurCRM serveriga ko'chirilsinmi?\n\nHozir u faqat shu kompyuterda kassa yoki ega dasturi ochiq bo'lganda javob beradi. Serverda u kompyuter o'chiq bo'lsa ham kecha-kunduz javob beradi: buyruqlar, hisobotlar, SI, xaridorlar uchun maslahatchi, buyurtmalar va smena yopilganda hisobot.\n\nBot tokeni va SI kaliti NurCRM serverida shifrlangan holda saqlanadi. Fikringiz o'zgarsa — «Sozlamalar → Operatsiyalar → Botni ulash → Shu kompyuterga qaytarish»."),
                Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot"),
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Yes);
            MarkDone();
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                PosLogger.Log("Телеграм-бот: перенос на сервер отклонён клиентом (кнопка остаётся в мастере бота).", "TELEGRAM");
                return;
            }

            try
            {
                var moved = await api.MoveCurrentBotToServerAsync().ConfigureAwait(true);
                PosLogger.Log($"Телеграм-бот перенесён на сервер NurCRM по согласию клиента (вебхук {(moved?.WebhookOk == true ? "ок" : "нет")}).", "TELEGRAM");
                PosMessageBox.Show(owner,
                    Tr.T("Готово: бот работает на сервере NurCRM круглые сутки.", "Даяр: бот NurCRM серверинде күнү-түнү иштейт.", "Done: the bot runs on the NurCRM server around the clock.",
                        "Tamam: bot NurCRM sunucusunda günün her saati çalışıyor.", "Tayyor: bot NurCRM serverida kecha-kunduz ishlaydi."),
                    Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot"));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: перенос на сервер не удался: {ServerTelegramBotApi.Describe(ex)}", "WARNING");
                PosMessageBox.Show(owner,
                    Tr.T("Не удалось перенести бота: ", "Ботту көчүрүү болбой калды: ", "Could not move the bot: ", "Bot taşınamadı: ", "Botni ko'chirib bo'lmadi: ")
                    + ServerTelegramBotApi.Describe(ex)
                    + Tr.T("\n\nПопробуйте позже: Настройки → Операции → Подключить бота.", "\n\nКийинчерээк аракет кылыңыз: Жөндөөлөр → Операциялар → Ботту туташтыруу.",
                        "\n\nTry later: Settings → Operations → Connect bot.", "\n\nDaha sonra deneyin: Ayarlar → İşlemler → Botu bağla.", "\n\nKeyinroq urinib ko'ring: Sozlamalar → Operatsiyalar → Botni ulash."),
                    Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot"));
            }
        }
        catch (Exception ex)
        {
            // Предложение — не главное: его сбой не должен мешать работе.
            PosLogger.Log($"Телеграм-бот: предложение перенести на сервер не показано ({ex.GetType().Name}: {ex.Message}).", "TELEGRAM");
        }
    }

    private static void MarkDone()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: отметка о предложении не сохранена ({ex.Message}).", "WARNING");
        }
    }
}
