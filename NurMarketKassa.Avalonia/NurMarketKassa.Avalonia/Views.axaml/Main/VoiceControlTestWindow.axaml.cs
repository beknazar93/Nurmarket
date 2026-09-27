using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>"Регистрация голоса" из Настроек (2026-09-04) — панель живой диагностики
/// голосового управления: показывает статус, что услышал микрофон и как это разобрал парсер,
/// плюс позволяет проверить текст парсера БЕЗ микрофона (как требовал ТЗ: IVoiceCommandParser
/// должен тестироваться отдельно от аудио). Подписывается на уже запущенный в MainWindow
/// IVoiceControlService — отдельного экземпляра/второго микрофона не открывает.</summary>
public partial class VoiceControlTestWindow : Window
{
    private const int MaxItems = 30;

    private IVoiceControlService? _voiceControl;
    private DispatcherTimer? _statusTimer;

    public VoiceControlTestWindow()
    {
        InitializeComponent();
    }

    public static void Open(Window? owner)
    {
        var window = new VoiceControlTestWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        _voiceControl = App.AppHost?.Services.GetService(typeof(IVoiceControlService)) as IVoiceControlService;
        if (_voiceControl != null)
        {
            _voiceControl.RawTextRecognized += OnRawTextRecognized;
            _voiceControl.CommandRecognized += OnCommandRecognized;
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
        RefreshUnitWords();
        RefreshVoiceLockUi();
        RefreshProductAliases();
        NewAliasProductBox.ItemsSource = CatalogCacheService.Products;
        NewAliasProductBox.ItemFilter = (search, item) =>
            item is CatalogProductTileVm p && p.Title.Contains(search ?? "", StringComparison.OrdinalIgnoreCase);
        NewAliasProductBox.ItemSelector = (search, item) =>
            item is CatalogProductTileVm p ? p.Title : "";
        // 2026-09-08: без явного шаблона AutoCompleteBox рисует ToString() объекта (полное имя
        // класса) — нужен и в выпадающем списке, и в самом текстовом поле после выбора.
        NewAliasProductBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CatalogProductTileVm>(
            (p, _) => new TextBlock { Text = p?.Title ?? "" });

        _promptLang = UserPreferences.Instance.Language == AppLanguage.Kyrgyz ? "ky" : "ru";
        PromptLangRu.IsChecked = _promptLang == "ru";
        PromptLangKy.IsChecked = _promptLang == "ky";
        CustomPromptsTitle.Text = Tr.T("Озвучка своим голосом", "Өз үнүңүз менен үн коштоо", "Prompts in your own voice",
            "Kendi sesinizle seslendirme", "O'z ovozingiz bilan ovozlashtirish");
        CustomPromptsHint.Text = Tr.T(
            "Касса произносит эти фразы вашим голосом — с вашей интонацией и тоном. Запишите фразу с микрофона (нажмите «Записать», скажите, нажмите «Стоп») или загрузите готовый файл WAV/MP3. Тишина по краям обрезается сама. Записывайте свой голос или голос человека, который на это согласен.",
            "Касса бул сөздөрдү сиздин үнүңүз менен — сиздин интонацияңыз жана обонуңуз менен айтат. Сөз айкашын микрофондон жазыңыз («Жазуу» басып, айтып, «Токтотуу» басыңыз) же даяр WAV/MP3 файлын жүктөңүз. Четтердеги тынчтык өзү кесилет. Өз үнүңүздү же макул болгон адамдын үнүн жазыңыз.",
            "The till speaks these phrases in your voice, with your intonation and tone. Record a phrase with the microphone (click “Record”, speak, click “Stop”) or upload a ready WAV/MP3 file. Silence at the start and end is trimmed automatically. Record your own voice or the voice of someone who has agreed to it.",
            "Kasa bu cümleleri sizin sesinizle, sizin tonlamanızla söyler. Cümleyi mikrofondan kaydedin («Kaydet»e basın, cümleyi söyleyin, «Durdur»a basın) veya hazır bir WAV/MP3 dosyası yükleyin. Baştaki ve sondaki sessizlik otomatik olarak kırpılır. Kendi sesinizi veya buna izin veren birinin sesini kaydedin.",
            "Kassa bu iboralarni sizning ovozingiz bilan — o'z ohangingiz va tembringizda aytadi. Iborani mikrofondan yozib oling («Yozish»ni bosing, ayting, «To'xtatish»ni bosing) yoki tayyor WAV/MP3 faylini yuklang. Boshidagi va oxiridagi sukunat avtomatik kesiladi. O'z ovozingizni yoki bunga rozi bo'lgan odamning ovozini yozing.");
        // Подсказка в поле фраз — в коде, а не в XAML: в ней переносы строк. Сами примеры
        // («кола», «кокакола»…) остаются как есть: это то, что кассир говорит вслух.
        NewAliasPhraseBox.Watermark = Tr.T("Фразы, по одной на строку, например:\nкола\nкокакола\nчёрный напиток",
            "Фразалар, бир сапка бирден, мисалы:\nкола\nкокакола\nкара суусундук",
            "Phrases, one per line, for example:\nкола\nкокакола\nчёрный напиток",
            "İfadeler, her satıra bir tane, örneğin:\nкола\nкокакола\nчёрный напиток",
            "Iboralar, har qatorga bittadan, masalan:\nкола\nкокакола\nчёрный напиток");
        RefreshCustomPrompts();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_voiceControl != null)
        {
            _voiceControl.RawTextRecognized -= OnRawTextRecognized;
            _voiceControl.CommandRecognized -= OnCommandRecognized;
        }
        _statusTimer?.Stop();
        StopPromptRecordingSilently();
    }

    // ── Озвучка своим голосом ──────────────────────────────────────────────────────────

    private string _promptLang = "ru";
    private CustomVoicePrompts.Recorder? _promptRecorder;
    private string? _recordingKey;
    private bool _voiceWasListening;

    private void PromptLang_Click(object? sender, RoutedEventArgs e)
    {
        _promptLang = PromptLangKy.IsChecked == true ? "ky" : "ru";
        RefreshCustomPrompts();
    }

    private void RefreshCustomPrompts()
    {
        CustomPromptsPanel.Children.Clear();
        var recording = _promptRecorder != null;
        foreach (var prompt in CustomVoicePrompts.All)
        {
            var own = CustomVoicePrompts.HasCustom(prompt.Key, _promptLang);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };

            var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = prompt.Text(_promptLang), FontSize = 13, Foreground = ThemeBrush("BrushText"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = own
                    ? Tr.T("своя запись", "өз жазууңуз", "your recording", "kendi kaydınız", "o'z yozuvingiz")
                    : Tr.T("стандартная", "стандарттык", "standard", "standart", "standart"),
                FontSize = 11,
                Foreground = own ? ThemeBrush("BrushAccent") : ThemeBrush("BrushTextSoft"),
            });
            row.Children.Add(text);

            Button MakeButton(string content, int column, bool primary, Action onClick, bool enabled = true)
            {
                var button = new Button
                {
                    Content = content,
                    Classes = { primary ? "PrimaryButton" : "SecondaryButton" },
                    IsEnabled = enabled,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(6, 0, 0, 0),
                };
                button.Click += (_, _) => onClick();
                Grid.SetColumn(button, column);
                row.Children.Add(button);
                return button;
            }

            var key = prompt.Key;
            var isThisRecording = recording && _recordingKey == key;
            MakeButton("▶", 1, false, () => VoicePromptPlayer.PlayPrompt(key, _promptLang), !recording);
            MakeButton(
                isThisRecording
                    ? Tr.T("■ Стоп", "■ Токтотуу", "■ Stop", "■ Durdur", "■ To'xtatish")
                    : Tr.T("● Записать", "● Жазуу", "● Record", "● Kaydet", "● Yozish"),
                2, true, () => _ = TogglePromptRecordingAsync(key), !recording || isThisRecording);
            MakeButton(Tr.T("Файл…", "Файл…", "File…", "Dosya…", "Fayl…"), 3, false, () => _ = ImportPromptAsync(key), !recording);
            MakeButton(Tr.T("Стандартная", "Стандарттык", "Standard", "Standart", "Standart"), 4, false, () =>
            {
                CustomVoicePrompts.Reset(key, _promptLang);
                CustomPromptsStatus.Text = Tr.T("Возвращена стандартная фраза.", "Стандарттык сөз айкашы кайтарылды.",
                    "Standard phrase restored.", "Standart cümleye geri dönüldü.", "Standart ibora tiklandi.");
                RefreshCustomPrompts();
            }, !recording && own);

            CustomPromptsPanel.Children.Add(row);
        }
    }

    private async Task TogglePromptRecordingAsync(string key)
    {
        if (_promptRecorder == null)
        {
            try
            {
                // Микрофон занят прослушиванием команд — на время записи оно останавливается.
                _voiceWasListening = _voiceControl?.IsListening == true;
                if (_voiceWasListening)
                    _voiceControl!.Stop();

                _promptRecorder = new CustomVoicePrompts.Recorder();
                _recordingKey = key;
                _promptRecorder.Start();
                CustomPromptsStatus.Text = Tr.T("Идёт запись — скажите фразу и нажмите «Стоп».",
                    "Жазылууда — сөз айкашын айтып, «Токтотуу» басыңыз.", "Recording — say the phrase and click “Stop”.",
                    "Kayıt yapılıyor — cümleyi söyleyin ve «Durdur» düğmesine basın.", "Yozilmoqda — iborani ayting va «To'xtatish»ni bosing.");
            }
            catch (Exception ex)
            {
                StopPromptRecordingSilently();
                CustomPromptsStatus.Text = Tr.T("Микрофон не открылся: ", "Микрофон ачылган жок: ", "Could not open the microphone: ",
                    "Mikrofon açılamadı: ", "Mikrofon ochilmadi: ") + ex.Message;
            }

            RefreshCustomPrompts();
            return;
        }

        var recorder = _promptRecorder;
        _promptRecorder = null;
        _recordingKey = null;
        try
        {
            await recorder.StopAndSaveAsync(key, _promptLang).ConfigureAwait(true);
            CustomPromptsStatus.Text = Tr.T("Записано. Нажмите ▶, чтобы послушать.", "Жазылды. Угуу үчүн ▶ басыңыз.",
                "Recorded. Click ▶ to listen.", "Kaydedildi. Dinlemek için ▶ düğmesine basın.", "Yozildi. Tinglash uchun ▶ tugmasini bosing.");
            VoicePromptPlayer.PlayPrompt(key, _promptLang);
        }
        catch (Exception ex)
        {
            CustomPromptsStatus.Text = ex.Message;
            PosLogger.Log($"Своя озвучка не записана: {ex}", "VOICE_PROMPT");
        }
        finally
        {
            recorder.Dispose();
            ResumeVoiceControl();
            RefreshCustomPrompts();
        }
    }

    private async Task ImportPromptAsync(string key)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Tr.T("Файл с голосом", "Үн файлы", "Voice file", "Ses dosyası", "Ovoz fayli"),
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(Tr.T("Аудио", "Аудио", "Audio", "Ses", "Audio"))
                {
                    Patterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.wma"],
                },
            ],
        }).ConfigureAwait(true);
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            CustomPromptsStatus.Text = Tr.T("Обработка файла…", "Файл иштетилүүдө…", "Processing the file…", "Dosya işleniyor…", "Fayl qayta ishlanmoqda…");
            await CustomVoicePrompts.ImportAsync(path, key, _promptLang).ConfigureAwait(true);
            CustomPromptsStatus.Text = Tr.T("Файл загружен. Нажмите ▶, чтобы послушать.", "Файл жүктөлдү. Угуу үчүн ▶ басыңыз.",
                "File loaded. Click ▶ to listen.", "Dosya yüklendi. Dinlemek için ▶ düğmesine basın.", "Fayl yuklandi. Tinglash uchun ▶ tugmasini bosing.");
            VoicePromptPlayer.PlayPrompt(key, _promptLang);
        }
        catch (Exception ex)
        {
            CustomPromptsStatus.Text = Tr.T("Файл не подошёл: ", "Файл туура келген жок: ", "Unsuitable file: ",
                "Dosya uygun değil: ", "Fayl mos kelmadi: ") + ex.Message;
            PosLogger.Log($"Своя озвучка из файла не загружена: {ex}", "VOICE_PROMPT");
        }

        RefreshCustomPrompts();
    }

    private void StopPromptRecordingSilently()
    {
        _promptRecorder?.Dispose();
        _promptRecorder = null;
        _recordingKey = null;
        ResumeVoiceControl();
    }

    private void ResumeVoiceControl()
    {
        if (!_voiceWasListening)
            return;
        _voiceWasListening = false;
        try
        {
            _voiceControl?.Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление не возобновилось после записи фразы: {ex.Message}", "VOICE_PROMPT");
        }
    }

    private void RefreshStatus()
    {
        if (_voiceControl is null)
        {
            StatusText.Text = Tr.T("Служба голосового управления недоступна.", "Үн менен башкаруу кызматы жеткиликсиз.", "The voice control service is unavailable.", "Sesli kontrol hizmeti kullanılamıyor.", "Ovozli boshqaruv xizmati mavjud emas.");
            StatusBadge.Background = ThemeBrush("BrushDangerSoft");
            StatusText.Foreground = ThemeBrush("BrushDanger");
            return;
        }

        var listening = _voiceControl.IsListening;
        StatusText.Text = Tr.T("Статус: ", "Абалы: ", "Status: ", "Durum: ", "Holat: ") + _voiceControl.Status;
        StatusBadge.Background = ThemeBrush(listening ? "BrushSuccessSoft" : "BrushWarningSoft");
        StatusText.Foreground = ThemeBrush(listening ? "BrushSuccess" : "BrushWarning");
    }

    private void OnRawTextRecognized(string text) =>
        Dispatcher.UIThread.Post(() => PrependRow(RawTextList, $"{DateTime.Now:HH:mm:ss}  «{text}»"));

    private void OnCommandRecognized(VoiceCommandResult result) =>
        Dispatcher.UIThread.Post(() => PrependRow(CommandList, DescribeResult(result)));

    private static string DescribeResult(VoiceCommandResult result)
    {
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var suffix = result.Intent switch
        {
            VoiceIntent.AddProduct when result.Product != null =>
                Tr.T($"добавить «{result.Product.Title}» × {result.Quantity:0.###}",
                    $"«{result.Product.Title}» × {result.Quantity:0.###} кошуу",
                    $"add “{result.Product.Title}” × {result.Quantity:0.###}",
                    $"ekle «{result.Product.Title}» × {result.Quantity:0.###}",
                    $"«{result.Product.Title}» × {result.Quantity:0.###} qo'shish"),
            VoiceIntent.AddProduct when result.Candidates.Count > 1 =>
                Tr.T("неоднозначно", "бир маанилүү эмес", "ambiguous", "belirsiz", "noaniq")
                    + $" ({result.Candidates.Count}): {string.Join(", ", result.Candidates.Take(3).Select(p => p.Title))}",
            VoiceIntent.AddProduct => Tr.T("товар не найден", "товар табылган жок", "product not found", "ürün bulunamadı", "mahsulot topilmadi"),
            VoiceIntent.FindProduct => Tr.T($"поиск «{result.ProductQuery}»: {result.Candidates.Count} найдено",
                $"издөө «{result.ProductQuery}»: {result.Candidates.Count} табылды",
                $"search “{result.ProductQuery}”: {result.Candidates.Count} found",
                $"arama «{result.ProductQuery}»: {result.Candidates.Count} bulundu",
                $"qidiruv «{result.ProductQuery}»: {result.Candidates.Count} ta topildi"),
            VoiceIntent.RemoveLastItem => Tr.T("убрать последнюю позицию", "акыркы позицияны алып салуу", "remove last item", "son kalemi kaldır", "oxirgi pozitsiyani olib tashlash"),
            VoiceIntent.ClearCart => Tr.T("очистить чек", "чекти тазалоо", "clear receipt", "fişi temizle", "chekni tozalash"),
            VoiceIntent.Pay => Tr.T("оплата", "төлөө", "payment", "ödeme", "to'lov"),
            VoiceIntent.RepeatLast => Tr.T("повторить последнюю команду", "акыркы буйрукту кайталоо", "repeat last command", "son komutu tekrarla", "oxirgi buyruqni takrorlash"),
            VoiceIntent.Cancel => Tr.T("отмена", "жокко чыгаруу", "cancel", "iptal", "bekor qilish"),
            _ => Tr.T("не распознано", "таанылган жок", "not recognized", "tanınmadı", "tanib olinmadi"),
        };
        var lockSuffix = result.VoiceMatched == false
            ? Tr.T(" ⚠ ГОЛОС НЕ СОВПАЛ", " ⚠ ҮН ДАЛ КЕЛГЕН ЖОК", " ⚠ VOICE MISMATCH", " ⚠ SES EŞLEŞMEDİ", " ⚠ OVOZ MOS KELMADI")
            : "";
        return $"{time}  [{result.Intent}] «{result.RawText}» -> {suffix}{lockSuffix}";
    }

    private void PrependRow(ItemsControl list, string text)
    {
        list.Items.Insert(0, new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
        });

        while (list.Items.Count > MaxItems)
            list.Items.RemoveAt(list.Items.Count - 1);
    }

    private void ParseManual_Click(object? sender, RoutedEventArgs e) => RunManualParse();

    private void ManualTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunManualParse();
    }

    /// <summary>Полностью офлайн-путь без микрофона: снимаем ключевое слово, разбираем
    /// намерение, ищем товар — тот же код, что использует VoiceControlService на живом звуке.</summary>
    private void RunManualParse()
    {
        var input = ManualTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(input))
        {
            ManualResultText.Text = Tr.T("Введите текст.", "Текст жазыңыз.", "Enter some text.", "Metin girin.", "Matn kiriting.");
            return;
        }

        if (!VoiceCommandParser.TryStripWakeWord(input, out var commandText))
        {
            ManualResultText.Text = Tr.T("Ключевое слово «касса»/«каса» не найдено в тексте — без него команда игнорируется даже в реальной работе.", "Текстте «касса»/«каса» ачкыч сөзү табылган жок — ансыз буйрук чыныгы иште да эске алынбайт.", "The keyword «касса»/«каса» wasn't found in the text — without it the command is ignored in real use too.", "Metinde «касса»/«каса» anahtar kelimesi bulunamadı — bu kelime olmadan komut gerçek kullanımda da yok sayılır.", "Matnda «касса»/«каса» kalit so'zi topilmadi — usiz buyruq haqiqiy ishda ham e'tiborga olinmaydi.");
            return;
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            ManualResultText.Text = Tr.T("Ключевое слово найдено, но после него пусто — команда не задана.", "Ачкыч сөз табылды, бирок андан кийин эч нерсе жок — буйрук берилген эмес.", "The keyword was found, but nothing follows it — no command given.", "Anahtar kelime bulundu ama ardından bir şey yok — komut verilmedi.", "Kalit so'z topildi, lekin undan keyin hech narsa yo'q — buyruq berilmagan.");
            return;
        }

        var parser = new DefaultVoiceCommandParser();
        var command = parser.Parse(commandText);

        if (command.Intent != NurMarketKassa.Services.Hardware.VoiceIntent.AddProduct
            && command.Intent != NurMarketKassa.Services.Hardware.VoiceIntent.FindProduct)
        {
            ManualResultText.Text = $"Intent = {command.Intent}" + (command.RequiresConfirmation ? Tr.T(" (требует подтверждения)", " (ырастоону талап кылат)", " (requires confirmation)", " (onay gerektirir)", " (tasdiqlashni talab qiladi)") : "");
            return;
        }

        var candidates = VoiceCommandParser.FindProducts(command.ProductText, CatalogCacheService.Products);
        ManualResultText.Text = candidates.Count switch
        {
            0 => Tr.T($"Intent = {command.Intent}, запрос «{command.ProductText}», количество {command.Quantity:0.###} — товар НЕ найден.",
                $"Intent = {command.Intent}, сурам «{command.ProductText}», саны {command.Quantity:0.###} — товар ТАБЫЛГАН ЖОК.",
                $"Intent = {command.Intent}, query “{command.ProductText}”, quantity {command.Quantity:0.###} — product NOT found.",
                $"Intent = {command.Intent}, sorgu «{command.ProductText}», miktar {command.Quantity:0.###} — ürün BULUNAMADI.",
                $"Intent = {command.Intent}, so'rov «{command.ProductText}», miqdor {command.Quantity:0.###} — mahsulot TOPILMADI."),
            1 => Tr.T($"Intent = {command.Intent}, товар «{candidates[0].Title}», количество {command.Quantity:0.###}.",
                $"Intent = {command.Intent}, товар «{candidates[0].Title}», саны {command.Quantity:0.###}.",
                $"Intent = {command.Intent}, product “{candidates[0].Title}”, quantity {command.Quantity:0.###}.",
                $"Intent = {command.Intent}, ürün «{candidates[0].Title}», miktar {command.Quantity:0.###}.",
                $"Intent = {command.Intent}, mahsulot «{candidates[0].Title}», miqdor {command.Quantity:0.###}."),
            _ => Tr.T($"Intent = {command.Intent}, запрос «{command.ProductText}» — неоднозначно, {candidates.Count} вариантов: ",
                    $"Intent = {command.Intent}, сурам «{command.ProductText}» — бир маанилүү эмес, {candidates.Count} вариант: ",
                    $"Intent = {command.Intent}, query “{command.ProductText}” — ambiguous, {candidates.Count} options: ",
                    $"Intent = {command.Intent}, sorgu «{command.ProductText}» — belirsiz, {candidates.Count} seçenek: ",
                    $"Intent = {command.Intent}, so'rov «{command.ProductText}» — noaniq, {candidates.Count} ta variant: ")
                + string.Join(", ", candidates.Take(5).Select(p => p.Title)) + ".",
        };
    }

    /// <summary>Собственные слова-единицы кассира (VoiceLexiconStore) — база для расширения
    /// встроенного русского/кыргызского списка, чтобы не редактировать код ради, например,
    /// "мешок" или "ящик".</summary>
    private void RefreshUnitWords()
    {
        UnitWordsList.Items.Clear();
        List<(int Id, string Word, string? Abbreviation)> words;
        try
        {
            words = VoiceLexiconStore.LoadUnitWords();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось загрузить словарь единиц: {ex}", "VOICE");
            return;
        }

        if (words.Count == 0)
        {
            UnitWordsList.Items.Add(new TextBlock
            {
                Text = Tr.T("Своих слов пока не добавлено — используются только встроенные (кг, л, шт, даана, бөтөлкө и т.п.).", "Өз сөздөрүңүз азырынча кошула элек — камтылгандар гана колдонулат (кг, л, шт, даана, бөтөлкө ж.б.).", "No custom words added yet — only the built-in ones are used (кг, л, шт, даана, бөтөлкө, etc.).", "Henüz kendi kelimeniz eklenmedi — yalnızca yerleşik olanlar kullanılıyor (кг, л, шт, даана, бөтөлкө vb.).", "Hali o'z so'zlaringiz qo'shilmagan — faqat o'rnatilgan so'zlar ishlatiladi (кг, л, шт, даана, бөтөлкө va h.k.)."),
                FontSize = 12,
                Foreground = ThemeBrush("BrushTextSoft"),
            });
            return;
        }

        foreach (var (id, word, abbreviation) in words)
        {
            var label = string.IsNullOrWhiteSpace(abbreviation) ? word : $"{word} ({abbreviation})";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var removeButton = new Button
            {
                Content = Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                Classes = { "SecondaryButton" },
                FontSize = 11,
                Padding = new Avalonia.Thickness(8, 3),
                Tag = id,
            };
            removeButton.Click += RemoveUnitWord_Click;
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(removeButton);
            UnitWordsList.Items.Add(row);
        }
    }

    private void AddUnitWord_Click(object? sender, RoutedEventArgs e)
    {
        var word = (NewUnitWordBox.Text ?? "").Trim();
        if (word.Length == 0)
            return;

        var abbreviation = (NewUnitAbbreviationBox.Text ?? "").Trim();
        try
        {
            VoiceLexiconStore.AddUnitWord(word, abbreviation.Length == 0 ? null : abbreviation);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось добавить слово-единицу: {ex}", "VOICE");
            return;
        }

        NewUnitWordBox.Text = "";
        NewUnitAbbreviationBox.Text = "";
        RefreshUnitWords();
    }

    private void RemoveUnitWord_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id })
            return;

        try
        {
            VoiceLexiconStore.RemoveUnitWord(id);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось удалить слово-единицу: {ex}", "VOICE");
            return;
        }

        RefreshUnitWords();
    }

    /// <summary>2026-09-08: "обучение" голосового помощника — фраза напрямую привязывается к
    /// товару в обход обычного пословного поиска (VoiceCommandParser.FindProducts проверяет
    /// это ПЕРВЫМ). См. DatabaseService.AddVoiceProductAlias.</summary>
    private void RefreshProductAliases()
    {
        ProductAliasesList.Items.Clear();
        List<(int Id, string Phrase, string ProductId, string ProductTitle)> aliases;
        try
        {
            aliases = VoiceLexiconStore.LoadProductAliases();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось загрузить обучающие фразы: {ex}", "VOICE");
            return;
        }

        if (aliases.Count == 0)
        {
            ProductAliasesList.Items.Add(new TextBlock
            {
                Text = Tr.T("Обучающих фраз пока нет.", "Үйрөтүүчү фразалар азырынча жок.", "No training phrases yet.", "Henüz eğitim ifadesi yok.", "Hali o'rgatuvchi iboralar yo'q."),
                FontSize = 12,
                Foreground = ThemeBrush("BrushTextSoft"),
            });
            return;
        }

        // 2026-09-09: группируем по товару — теперь у одного товара обычно несколько фраз
        // (владелец попросил "минимум 3"), плоский список было бы неудобно читать.
        foreach (var group in aliases.GroupBy(a => a.ProductTitle).OrderBy(g => g.Key))
        {
            var groupItems = group.ToList();
            var groupPanel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
            groupPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Key} ({groupItems.Count})",
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
            });

            foreach (var (id, phrase, _, _) in groupItems)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(12, 0, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = $"«{phrase}»",
                    FontSize = 12,
                    Foreground = ThemeBrush("BrushTextSoft"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                var removeButton = new Button
                {
                    Content = Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                    Classes = { "SecondaryButton" },
                    FontSize = 11,
                    Padding = new Avalonia.Thickness(8, 3),
                    Tag = id,
                };
                removeButton.Click += RemoveProductAlias_Click;
                Grid.SetColumn(removeButton, 1);
                row.Children.Add(removeButton);
                groupPanel.Children.Add(row);
            }

            ProductAliasesList.Items.Add(groupPanel);
        }
    }

    /// <summary>2026-09-09: несколько фраз за один раз (по одной на строку) — владелец попросил,
    /// чтобы у одного товара можно было держать минимум 3 голосовых названия, вместо того чтобы
    /// нажимать "Добавить" по одной фразе.</summary>
    private void AddProductAlias_Click(object? sender, RoutedEventArgs e)
    {
        AliasErrorText.IsVisible = false;

        var phrases = (NewAliasPhraseBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var product = NewAliasProductBox.SelectedItem as CatalogProductTileVm;

        if (phrases.Count == 0 || product is null)
        {
            AliasErrorText.Text = Tr.T("Укажите хотя бы одну фразу (по одной на строку) и выберите товар из списка (не просто впишите название).", "Жок дегенде бир фраза жазыңыз (бир сапка бирден) жана товарды тизмеден тандаңыз (атын жөн эле жазып койбоңуз).", "Enter at least one phrase (one per line) and pick a product from the list (don't just type the name).", "En az bir ifade girin (her satıra bir tane) ve ürünü listeden seçin (sadece adını yazmayın).", "Kamida bitta ibora kiriting (har qatorga bittadan) va mahsulotni ro'yxatdan tanlang (shunchaki nomini yozmang).");
            AliasErrorText.IsVisible = true;
            return;
        }

        try
        {
            foreach (var phrase in phrases)
                VoiceLexiconStore.AddProductAlias(phrase, product.Id, product.Title);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось сохранить обучающую фразу: {ex}", "VOICE");
            AliasErrorText.Text = Tr.T($"Не удалось сохранить: {ex.Message}", $"Сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save: {ex.Message}", $"Kaydedilemedi: {ex.Message}", $"Saqlab bo'lmadi: {ex.Message}");
            AliasErrorText.IsVisible = true;
            return;
        }

        NewAliasPhraseBox.Text = "";
        NewAliasProductBox.Text = "";
        NewAliasProductBox.SelectedItem = null;
        RefreshProductAliases();
    }

    private void RemoveProductAlias_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id })
            return;

        try
        {
            VoiceLexiconStore.RemoveProductAlias(id);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось удалить обучающую фразу: {ex}", "VOICE");
            return;
        }

        RefreshProductAliases();
    }

    /// <summary>Голосовой замок (2026-09-05) — четыре состояния карточки: (1) модуль не скачан —
    /// только кнопка скачивания; (2) скачан, голос не записан — только кнопка записи;
    /// (3) записан, замок выключен — запись/чекбокс/сброс; (4) записан, включён — то же самое,
    /// плюс статус явно говорит "включён". Кнопки, которые сейчас не нужны, скрываются, а не
    /// просто дизейблятся — меньше визуального шума в и без того плотной карточке.</summary>
    private void RefreshVoiceLockUi()
    {
        var modelReady = SpeakerVerificationModelService.IsInstalled();
        var enrolled = _voiceControl?.IsVoiceLockEnrolled == true;
        var enabled = UserPreferences.Instance.VoiceLockEnabled;

        VoiceLockDownloadButton.IsVisible = !modelReady;
        VoiceLockEnrollButton.IsVisible = modelReady;
        VoiceLockEnrollButton.Content = enrolled
            ? Tr.T("🎙 Перезаписать голос (3 фразы)", "🎙 Үндү кайра жазуу (3 фраза)", "🎙 Re-record voice (3 phrases)", "🎙 Sesi yeniden kaydet (3 ifade)", "🎙 Ovozni qayta yozib olish (3 ibora)")
            : Tr.T("🎙 Записать голос (3 фразы)", "🎙 Үндү жазуу (3 фраза)", "🎙 Record voice (3 phrases)", "🎙 Sesi kaydet (3 ifade)", "🎙 Ovozni yozib olish (3 ibora)");
        VoiceLockEnabledCheck.IsVisible = modelReady && enrolled;
        VoiceLockEnabledCheck.IsChecked = enabled;
        VoiceLockClearButton.IsVisible = modelReady && enrolled;

        VoiceLockStatusText.Text = !modelReady
            ? Tr.T("Модуль голосового замка не скачан.", "Үн кулпусунун модулу жүктөлө элек.", "The voice lock module isn't downloaded.", "Ses kilidi modülü indirilmedi.", "Ovozli qulf moduli yuklab olinmagan.")
            : !enrolled
                ? Tr.T("Голос ещё не зарегистрирован.", "Үн азырынча каттала элек.", "No voice enrolled yet.", "Ses henüz kaydedilmedi.", "Ovoz hali ro'yxatdan o'tkazilmagan.")
                : enabled
                    ? Tr.T("Голос зарегистрирован, замок ВКЛЮЧЁН — команды от другого голоса будут отклоняться.", "Үн катталган, кулпу КҮЙГҮЗҮЛГӨН — башка үндөн келген буйруктар четке кагылат.", "Voice enrolled, lock is ON — commands from a different voice will be rejected.", "Ses kaydedildi, kilit ETKİN — başka bir sesten gelen komutlar reddedilecek.", "Ovoz ro'yxatdan o'tgan, qulf YOQILGAN — boshqa ovozdagi buyruqlar rad etiladi.")
                    : Tr.T("Голос зарегистрирован, но замок выключен — команды выполняются от любого голоса.", "Үн катталган, бирок кулпу өчүк — буйруктар каалаган үндөн аткарылат.", "Voice enrolled, but the lock is off — commands from any voice are executed.", "Ses kaydedildi ama kilit kapalı — her sesten gelen komut yürütülür.", "Ovoz ro'yxatdan o'tgan, lekin qulf o'chiq — buyruqlar har qanday ovozdan bajariladi.");
    }

    private async void VoiceLockDownload_Click(object? sender, RoutedEventArgs e)
    {
        VoiceLockDownloadButton.IsEnabled = false;
        VoiceLockProgress.IsVisible = true;
        VoiceLockStatusText.Text = Tr.T("Скачивание модуля голосового замка…", "Үн кулпусунун модулу жүктөлүүдө…",
            "Downloading the voice lock module…", "Ses kilidi modülü indiriliyor…", "Ovozli qulf moduli yuklab olinmoqda…");

        var ok = await SpeakerVerificationModelService.DownloadAndInstallAsync(progress: null).ConfigureAwait(true);

        VoiceLockProgress.IsVisible = false;
        VoiceLockDownloadButton.IsEnabled = true;

        if (!ok)
        {
            PosMessageBox.Show(this,
                Tr.T("Не удалось скачать модуль голосового замка. Проверьте интернет-соединение и попробуйте снова.",
                    "Үн кулпусунун модулун жүктөп алуу мүмкүн болгон жок. Интернетти текшерип, кайра аракет кылыңыз.",
                    "Could not download the voice lock module. Check your internet connection and try again.",
                    "Ses kilidi modülü indirilemedi. İnternet bağlantısını kontrol edip tekrar deneyin.",
                    "Ovozli qulf modulini yuklab bo'lmadi. Internet aloqasini tekshirib, qayta urinib ko'ring."),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshVoiceLockUi();
    }

    private async void VoiceLockEnroll_Click(object? sender, RoutedEventArgs e)
    {
        PosLogger.Log($"Голосовой замок: нажата кнопка записи (voiceControl={(_voiceControl is null ? "null" : "ok")}).", "VOICE_LOCK");
        if (_voiceControl is null)
        {
            PosLogger.Log("Голосовой замок: IVoiceControlService не внедрён (DI) — регистрация невозможна.", "VOICE_LOCK");
            return;
        }

        var proceed = PosMessageBox.Show(this,
            Tr.T("Сейчас прозвучат 3 коротких сигнала. После КАЖДОГО сигнала скажите любую фразу вслух (например \"касса\" и название товара) и на секунду замолчите — касса сама поймёт, что фраза закончилась.\n\nОбычное прослушивание команд на это время остановится.",
                "Азыр 3 кыска сигнал угулат. АР БИР сигналдан кийин каалаган фразаны үн чыгарып айтыңыз (мисалы, \"касса\" жана товардын аты) жана бир секунда унчукпай туруңуз — касса фраза бүткөнүн өзү түшүнөт.\n\nБул убакта буйруктарды кадимки угуу токтотулат.",
                "You'll now hear 3 short beeps. After EACH beep, say any phrase out loud (for example, «касса» and a product name), then stay silent for a second — the till will detect that the phrase has ended.\n\nNormal command listening pauses during this time.",
                "Şimdi 3 kısa sinyal duyacaksınız. HER sinyalden sonra herhangi bir ifadeyi yüksek sesle söyleyin (örneğin \"касса\" ve bir ürün adı) ve bir saniye susun — kasa ifadenin bittiğini kendisi anlar.\n\nBu süre boyunca normal komut dinleme duraklatılır.",
                "Hozir 3 ta qisqa signal eshitiladi. HAR BIR signaldan keyin istalgan iborani ovoz chiqarib ayting (masalan, \"касса\" va mahsulot nomi) va bir soniya jim turing — kassa ibora tugaganini o'zi tushunadi.\n\nBu vaqtda buyruqlarni odatdagidek tinglash to'xtatiladi."),
            Tr.T("Запись голоса", "Үн жазуу", "Voice recording", "Ses kaydı", "Ovoz yozish"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (proceed != MessageBoxResult.OK)
            return;

        VoiceLockEnrollButton.IsEnabled = false;
        VoiceLockClearButton.IsEnabled = false;
        VoiceLockProgress.IsVisible = true;

        void OnSampleRecorded(int current, int total) =>
            Dispatcher.UIThread.Post(() => VoiceLockStatusText.Text = Tr.T($"Записано {current} из {total}…", $"Жазылды: {current} / {total}…", $"Recorded {current} of {total}…", $"Kaydedildi: {current} / {total}…", $"Yozib olindi: {current} / {total}…"));

        VoiceLockStatusText.Text = Tr.T("Приготовьтесь — сейчас будет сигнал 1 из 3…", "Даярданыңыз — азыр 3 сигналдын 1-си угулат…", "Get ready — beep 1 of 3 is coming…", "Hazırlanın — ilk sinyal (1/3) geliyor…", "Tayyor turing — hozir 3 signaldan 1-si eshitiladi…");

        bool ok;
        try
        {
            ok = await _voiceControl.EnrollVoiceAsync(3, OnSampleRecorded, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовой замок: ошибка записи: {ex}", "VOICE_LOCK");
            ok = false;
        }

        VoiceLockProgress.IsVisible = false;
        VoiceLockEnrollButton.IsEnabled = true;
        VoiceLockClearButton.IsEnabled = true;

        if (!ok)
        {
            PosMessageBox.Show(this,
                Tr.T("Не удалось записать голос — возможно, микрофон не расслышал фразу вовремя (10 секунд на фразу). Попробуйте снова и говорите сразу после сигнала.",
                    "Үндү жазуу мүмкүн болгон жок — балким, микрофон фразаны өз убагында уккан жок (бир фразага 10 секунд). Кайра аракет кылып, сигналдан кийин дароо сүйлөңүз.",
                    "Could not record the voice — the microphone may not have caught the phrase in time (10 seconds per phrase). Try again and speak right after the beep.",
                    "Ses kaydedilemedi — mikrofon ifadeyi zamanında duymamış olabilir (ifade başına 10 saniye). Tekrar deneyin ve sinyalden hemen sonra konuşun.",
                    "Ovozni yozib bo'lmadi — ehtimol, mikrofon iborani o'z vaqtida eshitmadi (har bir iboraga 10 soniya). Qayta urinib ko'ring va signaldan so'ng darhol gapiring."),
                Tr.T("Не получилось", "Ишке ашкан жок", "Recording failed", "Başarısız", "Bo'lmadi"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshVoiceLockUi();
    }

    private void VoiceLockEnabledCheck_Click(object? sender, RoutedEventArgs e)
    {
        UserPreferences.Instance.VoiceLockEnabled = VoiceLockEnabledCheck.IsChecked == true;
        UserPreferences.Instance.SaveToDisk();
        RefreshVoiceLockUi();
    }

    private void VoiceLockClear_Click(object? sender, RoutedEventArgs e)
    {
        var confirm = PosMessageBox.Show(this,
            Tr.T("Удалить запись голоса и выключить голосовой замок?", "Үн жазуусун өчүрүп, үн кулпусун өчүрөсүзбү?", "Delete the voice recording and turn off the voice lock?", "Ses kaydı silinip ses kilidi kapatılsın mı?", "Ovoz yozuvini o'chirib, ovozli qulfni o'chirasizmi?"),
            Tr.T("Сбросить голосовой замок", "Үн кулпусун тазалоо", "Reset voice lock", "Ses kilidini sıfırla", "Ovozli qulfni tiklash"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        _voiceControl?.ClearVoiceEnrollment();
        RefreshVoiceLockUi();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private IBrush ThemeBrush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
