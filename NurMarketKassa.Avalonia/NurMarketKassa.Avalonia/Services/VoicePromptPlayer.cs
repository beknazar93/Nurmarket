using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Короткие голосовые подсказки кассиру (не путать с голосовым УПРАВЛЕНИЕМ —
/// VoiceControlService слушает микрофон, а это озвучивает вопрос через колонки), заранее
/// синтезированные офлайн и встроенные как WAV-ресурсы приложения — ни разовой генерации речи
/// "на лету", ни сетевого TTS не требуется. Озвучивается на текущем языке интерфейса (та же
/// конвенция, что у Tr.T), с русским как запасным для языков без записанной подсказки.
///
/// 2026-09-07: кыргызские подсказки перезаписаны нейросетевым голосом Meta MMS-TTS (VITS,
/// через sherpa-onnx) вместо espeak-ng — прежние были неразборчивы. Плюс папка переопределений:
/// если в %AppData%\NurMarketKassa\voice_prompts\ лежит файл с тем же именем (например,
/// not_found_ky.wav, записанный живым голосом), играется он, а не встроенный — владелец может
/// озвучить подсказки сам, без пересборки кассы.</summary>
public static class VoicePromptPlayer
{
    private static readonly string OverrideDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa",
        "voice_prompts");

    /// <summary>При выборе способа продажи товара с упаковкой (PackageChoiceDialog) —
    /// "Поштучно или целая пачка?" / "Даанадан же бүтүн пачкадан?".</summary>
    public static void PlayPieceOrPackChoice() => PlayByLanguage("piece_or_pack_ru.wav", "piece_or_pack_ky.wav");

    /// <summary>"Товар не найден." — голосовая команда не нашла ни одного товара по названию
    /// (2026-09-05, по запросу пользователя озвучить не только успешные, но и ошибочные
    /// результаты голосовых команд).</summary>
    public static void PlayProductNotFound() => PlayByLanguage("not_found_ru.wav", "not_found_ky.wav");

    /// <summary>"Уточните товар." — голосовая команда нашла НЕСКОЛЬКО подходящих товаров, нужно
    /// назвать точнее (сам список кандидатов кассир и так видит текстом на экране/в тосте — тут
    /// только звуковой сигнал, что нужно уточнение, без перечисления вариантов голосом).</summary>
    public static void PlayClarifyProduct() => PlayByLanguage("clarify_ru.wav", "clarify_ky.wav");

    /// <summary>"Голос не совпадает." — голосовой замок отклонил команду (не тот голос).</summary>
    public static void PlayVoiceMismatch() => PlayByLanguage("voice_mismatch_ru.wav", "voice_mismatch_ky.wav");

    /// <summary>"Чек пуст." — общая для "убрать последнюю" и "оплата" на пустом чеке.</summary>
    public static void PlayCartEmpty() => PlayByLanguage("cart_empty_ru.wav", "cart_empty_ky.wav");

    /// <summary>Папка, куда владелец может положить свои записи (см. комментарий к классу).</summary>
    public static string OverrideDirectory => OverrideDir;

    /// <summary>Прослушать фразу на выбранном языке («ru»/«ky») — своя запись, если есть,
    /// иначе встроенная (карточка «Озвучка своим голосом», CustomVoicePrompts).</summary>
    public static void PlayPrompt(string key, string lang) => Play($"{key}_{lang}.wav");

    private static void PlayByLanguage(string ruFileName, string kyFileName) =>
        Play(UserPreferences.Instance.Language == AppLanguage.Kyrgyz ? kyFileName : ruFileName);

    /// <summary>Task.Run, а не прямой Play() на UI-потоке — тот же приём, что у Console.Beep в
    /// VoiceControlService: не блокирует открытие диалога. PlaySync (не асинхронный Play) внутри
    /// фоновой задачи — держит SoundPlayer и его поток живыми до конца воспроизведения без
    /// отдельного поля/ссылки, иначе асинхронный Play() рискует быть собранным GC на середине
    /// воспроизведения, т.к. ничего не держит SoundPlayer живым после возврата из метода.</summary>
    private static void Play(string fileName)
    {
        Task.Run(() =>
        {
            try
            {
                using var buffer = new MemoryStream();
                var overridePath = Path.Combine(OverrideDir, fileName);
                if (File.Exists(overridePath))
                {
                    using var fileStream = File.OpenRead(overridePath);
                    fileStream.CopyTo(buffer);
                }
                else
                {
                    using var resourceStream = AssetLoader.Open(new Uri($"avares://NurMarketKassa.Avalonia/Assets/Sounds/{fileName}"));
                    resourceStream.CopyTo(buffer);
                }

                buffer.Position = 0;
                using var player = new SoundPlayer(buffer);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Голосовая подсказка: не удалось воспроизвести {fileName}: {ex.Message}", "VOICE_PROMPT");
            }
        });
    }
}
