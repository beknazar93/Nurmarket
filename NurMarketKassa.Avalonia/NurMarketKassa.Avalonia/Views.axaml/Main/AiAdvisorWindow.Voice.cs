using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-06 (В13, Л4-17): часть окна Нур Советника — живой разговор, голосовой ввод, озвучивание, ключи ИИ (строки 3715–4876 прежнего файла), без изменения логики.</summary>
public sealed partial class AiAdvisorWindow
{
    /// <summary>Значок «Разговор»: звуковая волна (четыре полоски).</summary>
    private Control WaveGlyph(string brushKey)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,10 V14 M9.5,6.5 V17.5 M14,3.5 V20.5 M18.5,8 V16"),
            Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 2.4, StrokeLineCap = PenLineCap.Round,
        };
        Use(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, brushKey);
        return path;
    }

    /// <summary>Панель живого разговора на месте поля ввода: круг (дышит под голос), что сейчас происходит, «Перебить», «Завершить».</summary>
    private void BuildLiveBar()
    {
        Use(_liveBar, Border.BackgroundProperty, "BrushPanel");
        Use(_liveBar, Border.BorderBrushProperty, "BrushAccent");
        Use(_orb, Border.BackgroundProperty, "BrushAccent");
        _orb.Child = WaveGlyph("BrushAccentForeground");
        _orb.RenderTransform = _orbScale;
        _orb.RenderTransformOrigin = RelativePoint.Center;
        _orb.VerticalAlignment = VerticalAlignment.Center;
        Use(_liveState, TextBlock.ForegroundProperty, "BrushText");
        Use(_liveHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        row.Children.Add(_orb);
        var texts = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_liveState);
        texts.Children.Add(_liveHint);
        AttachLiveLanguageLabel(texts); // 2026-11-05: подпись языка ИИ в звонке
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        _liveInterrupt = UiKit.Ghost(this, T("Перебить", "Бөлүү", "Interrupt", "Böl", "To'xtatish"));
        _liveInterrupt.Height = 40;
        _liveInterrupt.CornerRadius = new CornerRadius(20);
        _liveInterrupt.Margin = new Thickness(0, 0, 8, 0);
        _liveInterrupt.IsVisible = false;
        ToolTip.SetTip(_liveInterrupt, T("Советник замолчит и будет слушать вас", "Кеңешчи унчукпай калып, сизди угат", "The advisor stops talking and listens to you",
            "Danışman susar ve sizi dinler", "Maslahatchi jim bo'lib, sizni tinglaydi"));
        _liveInterrupt.Click += (_, _) =>
        {
            _live?.Interrupt();
            FinishLiveTurn(interrupted: true);
        };
        Grid.SetColumn(_liveInterrupt, 2);
        row.Children.Add(_liveInterrupt);
        var end = new Button
        {
            Content = T("Завершить", "Бүтүрүү", "End", "Bitir", "Tugatish"), Height = 40, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
        };
        Use(end, Button.BackgroundProperty, "BrushDanger");
        end.Foreground = Brushes.White;
        end.Click += (_, _) => EndLive(null);
        Grid.SetColumn(end, 3);
        row.Children.Add(end);
        _liveBar.Child = row;
        Grid.SetRow(_liveBar, 3);
        _root.Children.Add(_liveBar);
    }

    /// <summary>«Разговор»: сводка магазина → живой сеанс Gemini; дальше владелец просто говорит.</summary>
    private async Task StartLiveAsync()
    {
        if (_live is not null || _busy || _recorder is not null || !TelegramAiChat.IsConfigured)
            return;
        if (!Cap("voice.live"))
        {
            SayDisabled("voice.live");
            return;
        }
        var gen = ++_liveGen;
        VoiceChatPlayer.Stop();
        _sidebar.IsVisible = false;
        if (_composer is not null)
            _composer.IsVisible = false;
        _liveBar.IsVisible = true;
        _liveState.Text = T("Подключаюсь…", "Туташып жатам…", "Connecting…", "Bağlanıyor…", "Ulanmoqda…");
        _liveHint.Text = T("Готовлю данные магазина для разговора", "Маек үчүн дүкөндүн маалыматын даярдап жатам", "Preparing your shop's data for the conversation",
            "Sohbet için mağaza verileri hazırlanıyor", "Suhbat uchun do'kon ma'lumotlari tayyorlanmoqda");
        _liveTimer?.Stop();
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _liveTimer.Tick += (_, _) => TickLive();
        _liveTimer.Start();
        IReadOnlyDictionary<int, string> names;
        string instruction;
        // 2026-10-06, владелец (снимок звонка: «нет данных по сотрудникам», «не могу показать табель»): «исправь это, дай доступ».
        // Сотрудники, ставки, начисления и табель с начала месяца — параллельно со сводкой. Начало звонка не держим:
        // не успели — программа пришлёт их в звонок, как только будут готовы (SendStaffWhenReadyAsync).
        var liveAccess = CurrentAccess();
        // 2026-10-21 (О4): сотрудники в звонок — только с правом на зарплату.
        var staffTask = liveAccess.Staff ? LiveStaffContextAsync("") : Task.FromResult("");
        string? staffNow = null;
        // 2026-10-06, владелец: «долгое подключение звонка» (лог 23:10: сервер не отвечал, «Подключаюсь…» ~40 с). Сводку ждём
        // не дольше 6 с; не успела — звонок начинается без неё, сводка уходит в разговор, как только соберётся.
        var summaryTask = GetSummaryAsync();
        var summaryLate = false;
        try
        {
            if (await Task.WhenAny(summaryTask, Task.Delay(6000)).ConfigureAwait(true) != summaryTask)
            {
                summaryLate = true;
                throw new TimeoutException("сводка не готова за 6 с — дошлю в разговор");
            }
            var (sections, debtorNames) = await summaryTask.ConfigureAwait(true);
            names = debtorNames;
            await Task.WhenAny(staffTask, Task.Delay(1500)).ConfigureAwait(true);
            staffNow = staffTask.IsCompletedSuccessfully ? staffTask.Result : null;
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(sections, CurrentLines().Select(l => (l.Owner, l.Text)), staffNow, liveAccess);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сводка для разговора не собрана ({ex.Message}).", "WARNING");
            names = new Dictionary<int, string>();
            staffNow = staffTask.IsCompletedSuccessfully ? staffTask.Result : null;
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(Array.Empty<AiSection>(), CurrentLines().Select(l => (l.Owner, l.Text)), staffNow, liveAccess);
        }
        if (gen != _liveGen)
            return;
        _liveDebtors = names;
        var live = new GeminiLiveVoice(instruction);
        WireLiveLanguage(live); // 2026-11-05: команды «кыргызча жооп бер» / «говори по-русски» голосом
        // 2026-10-27: инструмент open_section — переход по словарю программы с проверкой; модель говорит только результат.
        if (ProductActionPlan.OpenNavTarget is not null)
            live.ToolHandler = (toolName, toolArgs) => LiveToolAsync(live, toolName, toolArgs);
        live.UserText += text => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            if (_liveUser is null)
            {
                _liveUser = AddBubble("", fromOwner: true);
                // Расшифровка вопроса пришла позже начала ответа — пузырь вопроса ставим перед ответом.
                if (_liveAi is not null && _liveUser.Parent is Control bubble
                    && _messages.Children.FirstOrDefault(c => c.Tag is MessageTag t && t.Body == _liveAi) is { } aiRow)
                {
                    _messages.Children.Remove(bubble);
                    _messages.Children.Insert(_messages.Children.IndexOf(aiRow), bubble);
                }
            }
            _liveUserText.Append(text);
            _liveUser.Text = _liveUserText.ToString().Trim();
            ScrollToEnd();
        });
        live.AiText += text => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            _liveAi ??= AddBubble("", fromOwner: false);
            _liveAiText.Append(text);
            _liveAi.Text = _liveAiText.ToString().Trim();
            ScrollToEnd();
        });
        live.TurnDone += () => Dispatcher.UIThread.Post(() =>
        {
            if (_live == live)
                FinishLiveTurn(interrupted: false);
        });
        live.Ended += reason => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            EndLive(reason is null
                ? null
                : T("Разговор прервался: ", "Маек үзүлдү: ", "The conversation was interrupted: ", "Sohbet kesildi: ", "Suhbat uzildi: ") + reason
                  + T(". Можно спросить голосом кнопкой микрофона или написать.", ". Микрофон баскычы менен үн менен сурасаңыз же жазсаңыз болот.",
                      ". You can ask by voice with the microphone button or type.", ". Mikrofon düğmesiyle sesli sorabilir veya yazabilirsiniz.",
                      ". Mikrofon tugmasi bilan ovozli so'rashingiz yoki yozishingiz mumkin."));
        });
        _live = live;
        try
        {
            live.Start();
            PosLogger.Log($"ИИ-советник: живой разговор начат (инструкция {instruction.Length} симв.).", "INFO");
            _liveStaffDelivered = staffNow is { Length: > 0 };
            if (staffNow is null)
                _ = SendStaffWhenReadyAsync(staffTask, live);
            if (summaryLate)
                _ = SendSummaryWhenReadyAsync(summaryTask, live);
        }
        catch (Exception ex)
        {
            EndLive(T("Разговор не начался: ", "Маек башталган жок: ", "The conversation didn't start: ", "Sohbet başlamadı: ", "Suhbat boshlanmadi: ") + ex.Message);
        }
    }

    /// <summary>Каждые 60 мс: подпись состояния, «дыхание» круга, «Перебить» — пока советник говорит; 3 минуты тишины — конец.</summary>
    private void TickLive()
    {
        var live = _live;
        var state = live?.State ?? GeminiLiveVoice.LiveState.Connecting;
        var t = Environment.TickCount64 / 1000.0;
        double scale;
        switch (state)
        {
            case GeminiLiveVoice.LiveState.Listening:
                var hearing = live!.MicLevel > 0.06;
                _liveState.Text = hearing
                    ? T("Слышу вас…", "Угуп жатам…", "I'm hearing you…", "Sizi duyuyorum…", "Sizni eshityapman…")
                    : T("Слушаю — говорите", "Угуп жатам — сүйлөңүз", "Listening — go ahead", "Dinliyorum — konuşun", "Tinglayapman — gapiring");
                _liveHint.Text = T("Спросите что угодно о магазине. Закончили — «Завершить».", "Дүкөн жөнүндө каалаганды сураңыз. Бүттүңүзбү — «Бүтүрүү».",
                    "Ask anything about your shop. Done — “End”.", "Mağazanız hakkında her şeyi sorun. Bitince — «Bitir».", "Do'kon haqida istalgan narsani so'rang. Tugatsangiz — «Tugatish».");
                scale = 1 + Math.Min(0.35, live.MicLevel * 0.45);
                _orbScale.ScaleX += (scale - _orbScale.ScaleX) * 0.5;
                _orbScale.ScaleY = _orbScale.ScaleX;
                _orb.Opacity = 1;
                break;
            case GeminiLiveVoice.LiveState.Speaking:
                _liveState.Text = T("Советник говорит…", "Кеңешчи сүйлөп жатат…", "The advisor is speaking…", "Danışman konuşuyor…", "Maslahatchi gapiryapti…");
                _liveHint.Text = T("Пока он говорит, микрофон молчит — чтобы не слышать сам себя. Перебить — кнопкой.",
                    "Ал сүйлөп жатканда микрофон өчүк — өзүн укпашы үчүн. Бөлүү — баскыч менен.",
                    "While it speaks the microphone is muted so it doesn't hear itself. Interrupt with the button.",
                    "Konuşurken mikrofon kapalı — kendini duymaması için. Bölmek için düğmeye basın.",
                    "U gapirayotganda mikrofon o'chiq — o'zini eshitmasligi uchun. To'xtatish — tugma bilan.");
                scale = 1.06 + 0.06 * Math.Sin(t * 7);
                _orbScale.ScaleX = _orbScale.ScaleY = scale;
                _orb.Opacity = 1;
                break;
            default:
                _orbScale.ScaleX = _orbScale.ScaleY = 1;
                _orb.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(t * 3));
                break;
        }
        if (_liveInterrupt is not null)
            _liveInterrupt.IsVisible = state == GeminiLiveVoice.LiveState.Speaking;
        // Забыли завершить — через 3 минуты тишины разговор заканчивается сам (бесплатный лимит Google не тратится впустую).
        if (live is not null && state == GeminiLiveVoice.LiveState.Listening && DateTime.UtcNow - live.LastActivityUtc > TimeSpan.FromMinutes(3))
            EndLive(T("Разговор завершён: три минуты тишины.", "Маек бүттү: үч мүнөт тынчтык.", "Conversation ended: three minutes of silence.",
                "Sohbet bitti: üç dakika sessizlik.", "Suhbat tugadi: uch daqiqa jimlik."));
    }

    /// <summary>Ответ договорён (или перебит): текст ответа — начисто, должники — по именам; реплики — в историю и в память чата.</summary>
    private void FinishLiveTurn(bool interrupted)
    {
        // 2026-10-06: голосом в звонке — «да, выполни» для подготовленных изменений и просьбы про товары (LiveProductPassAsync).
        var spoken = _liveUserText.ToString().Trim();
        if (!interrupted && spoken.Length > 0 && _live is { } liveNow)
        {
            // 2026-10-21 (О7): сменилась тема (фраза не про фото) — прежние «товарные цели» звонка забываются.
            if (!new[] { "фото", "фотк", "сүрөт", "photo", "rasm", "surat", "картин", "fotoğraf" }.Any(spoken.ToLowerInvariant().Contains))
            {
                _liveLastPhotoTalkUtc = DateTime.MinValue;
                _liveLastProducts = new();
            }
            // 2026-10-06, владелец в звонке: «Да, поставь четвёртое фото по счёту» — теперь программа ставит его сама.
            if (PhotoChoiceNumber(spoken) is { } photoNumber)
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var result = await ApplyPhotoChoiceAsync(photoNumber).ConfigureAwait(true);
                    if (_live == liveNow)
                        await liveNow.SendTextAsync("[Программа] " + result + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            // 2026-10-06: «найди / покажи фото Mars», «поищи в интернете» — фото в чате или варианты из интернета (LivePhotoPass).
            else if (LivePhotoPass(spoken, _liveAiText.ToString(), liveNow))
            {
            }
            else if (SiteEditorWindow.LooksLikeCommand(spoken) && ProductActionPlan.OpenSection is not null)
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var result = await SiteEditorCommandAsync(spoken).ConfigureAwait(true);
                    if (result is not null && _live == liveNow)
                        await liveNow.SendTextAsync("[Программа] " + result + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            // 2026-10-27: раздел уже открыл инструмент open_section (ответ модели — из его результата); товарное действие рядом («и спиши Марс») — как раньше.
            else if (_liveToolNav)
            {
                _liveToolNav = false;
                if (HasActionBesidesOpen(spoken))
                    _ = LiveProductPassAsync(spoken, liveNow);
            }
            // Запасной путь (инструменты выключены или модель не вызвала): тот же маршрутизатор по распознанной фразе, ответ — из факта.
            else if (ProductActionPlan.OpenNavTarget is not null && AdvisorNavRouter.Parse(spoken) is { Kind: not AdvisorNavRouter.RouteKind.NotNav } voiceRoute
                     && RouterOwnsPhrase(voiceRoute, spoken))
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var reply = await RunNavRouteAsync(voiceRoute).ConfigureAwait(true);
                    if (_live == liveNow)
                        await liveNow.SendTextAsync("[Программа] " + reply.Text + " Скажи владельцу ровно это одной короткой фразой, ничего не добавляя.").ConfigureAwait(true);
                    if (HasActionBesidesOpen(spoken))
                        _ = LiveProductPassAsync(spoken, liveNow);
                });
            else if (TryQuickOpen(spoken) is { } opened)
            {
                _ = liveNow.SendTextAsync("[Программа] " + opened + " Скажи об этом одной короткой фразой; владелец видит раздел на экране.");
                if (HasActionBesidesOpen(spoken))
                    _ = LiveProductPassAsync(spoken, liveNow);
            }
            else if (_pendingProductApply is { } apply && ProductActionPlan.IsVoiceYes(spoken))
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var done = await apply().ConfigureAwait(true);
                    if (done.Length > 0)
                        await liveNow.SendTextAsync("[Программа] Выполнено: " + done + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            else if (_pendingProductCancel is { } cancelPending && ProductActionPlan.IsVoiceNo(spoken))
                cancelPending();
            else if (ProductActionPlan.LooksLikeAction(spoken) || ProductInfoResearch.LooksLikeInfoRequest(spoken))
                _ = LiveProductPassAsync(spoken, liveNow);
            // Сотрудники: за другой период (с начала месяца уже в звонке) или сведения ещё не дошли до звонка — программа
            // присылает данные, и советник сразу отвечает (владелец, 06.10: «почему долго??» — советник ждал, данные не приходили).
            else if (TelegramAiChat.LooksLikeStaffQuestion(spoken) && CurrentAccess().Staff && (!_liveStaffDelivered || LiveStaffPeriodWords.Any(spoken.ToLowerInvariant().Contains)))
            {
                _ = LiveStaffPassAsync(spoken, liveNow);
                _ = LiveTablePassAsync(spoken, liveNow, speak: false);
            }
            // 2026-10-07, владелец: «всю информацию выводи в чат красивой таблицей» — вопрос с цифрами в звонке: таблица в чате.
            else if (LooksLikeDataQuestion(spoken))
                _ = LiveTablePassAsync(spoken, liveNow, speak: true);
            // 2026-10-27: ветка «открыть по словам самого советника» убрана — так открывался раздел из его выдумки («Прибыль и деньги, приёмка там»).
        }
        _liveToolNav = false;
        if (_liveAi is not null)
        {
            var text = _liveAiText.ToString().Trim();
            _liveAi.Text = text.Length == 0
                ? (interrupted ? "…" : "")
                : OwnerAiContext.RevealCodes(TelegramAiChat.ToPlainText(text), _liveDebtors) + (interrupted ? " …" : "");
            // 2026-10-07, владелец: «перестань выдавать фото из базы NurCRM, ищи прямо в интернете» — фото склада к репликам
            // больше не подставляются (раньше «бананды» показывало товар «Нан»); просьба о фото — поиск в интернете (LivePhotoPass).
        }
        var had = _liveUser is not null || _liveAi is not null;
        _liveUser = null;
        _liveAi = null;
        _liveUserText.Clear();
        _liveAiText.Clear();
        if (!had)
            return;
        // Текстовый чат после разговора помнит, о чём говорили голосом.
        var lines = CurrentLines();
        var turns = new List<(string Role, string Text)>();
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].Owner && !lines[i + 1].Owner)
            {
                turns.Add(("user", lines[i].Text));
                turns.Add(("model", lines[i + 1].Text));
            }
        }
        TelegramAiChat.RestoreOwnerAppHistory(turns);
        SaveCurrentChat();
    }

    /// <summary>Закончить живой разговор (кнопкой, «Новый разговор», закрытие окна или обрыв) и вернуть поле ввода.</summary>
    private void EndLive(string? note)
    {
        if (!_liveBar.IsVisible && _live is null)
            return;
        _liveGen++;
        var live = _live;
        _live = null;
        if (live is not null)
            _ = live.StopAsync();
        FinishLiveTurn(interrupted: true);
        _liveTimer?.Stop();
        _liveTimer = null;
        _liveBar.IsVisible = false;
        if (_composer is not null)
            _composer.IsVisible = true;
        RefreshKeyCard();
        if (note is not null)
            AddBubble(note, fromOwner: false);
        PosLogger.Log("ИИ-советник: живой разговор завершён.", "INFO");
    }

    /// <summary>Значок кнопки: микрофон или «стоп» (квадрат). Рисованный — эмодзи 🎤 в Windows выглядел как ручка.</summary>
    private Control MicGlyph(bool recording)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(recording
                ? "M7,7 H17 V17 H7 Z"
                : "M12,3 C13.7,3 15,4.3 15,6 V11 C15,12.7 13.7,14 12,14 C10.3,14 9,12.7 9,11 V6 C9,4.3 10.3,3 12,3 Z "
                  + "M5.5,10.5 C5.5,14.1 8.4,17 12,17 C15.6,17 18.5,14.1 18.5,10.5 M12,17 V21 M8.5,21 H15.5"),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
        };
        Use(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, "BrushText");
        if (recording)
            Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushText");
        return path;
    }

    /// <summary>🎤: первое нажатие — запись, второе — стоп, распознавание и вопрос советнику.</summary>
    private async Task ToggleVoiceAsync()
    {
        if (_mic is null)
            return;
        if (_recorder is null && !Cap("voice.ask"))
        {
            SayDisabled("voice.ask");
            return;
        }
        if (_recorder is null)
        {
            if (_busy)
                return;
            VoiceChatPlayer.Stop();
            var recorder = new VoiceChatRecorder();
            recorder.MaxLengthReached += () => Dispatcher.UIThread.Post(() => _ = ToggleVoiceAsync());
            try
            {
                recorder.Start();
            }
            catch (Exception ex)
            {
                recorder.Dispose();
                PosLogger.Log($"ИИ-советник: микрофон не включился ({ex.Message}).", "WARNING");
                AddBubble(T("Микрофон не включился: ", "Микрофон күйгөн жок: ", "The microphone did not start: ", "Mikrofon açılmadı: ", "Mikrofon yoqilmadi: ") + ex.Message, fromOwner: false);
                return;
            }
            _recorder = recorder;
            // Пока владелец говорит — собираем сводку магазина, к концу распознавания она уже готова.
            _ = GetSummaryAsync(SummaryParts.Periods | SummaryParts.Debts);
            _mic.Content = MicGlyph(recording: true);
            Use(_mic, Button.BackgroundProperty, "BrushDanger");
            _input.Watermark = T("Говорите… нажмите ■, когда закончите", "Сүйлөңүз… бүткөндө ■ басыңыз", "Speak… press ■ when you finish",
                "Konuşun… bitince ■ basın", "Gapiring… tugatgach ■ ni bosing");
            return;
        }

        var rec = _recorder;
        _recorder = null;
        var wav = rec.Stop();
        rec.Dispose();
        _mic.Content = MicGlyph(recording: false);
        Use(_mic, Button.BackgroundProperty, "BrushPanel");
        var watermark = T("Напишите вопрос и нажмите Enter…", "Суроону жазып, Enter басыңыз…", "Type a question and press Enter…", "Sorunuzu yazıp Enter'a basın…", "Savolni yozib, Enter bosing…");
        if (wav is null)
        {
            _input.Watermark = watermark;
            return;
        }
        _mic.IsEnabled = false;
        _input.Watermark = T("Распознаю речь…", "Сөздү таанып жатам…", "Recognising speech…", "Konuşma tanınıyor…", "Nutq tanilmoqda…");
        string? text = null;
        var sttWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            text = await TelegramVoice.TranscribeAsync(wav, "audio/wav", cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: речь не распознана ({ex.Message}).", "WARNING");
        }
        _input.Watermark = watermark;
        RefreshKeyCard();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddBubble(T("Не расслышал. Скажите ещё раз, ближе к микрофону.", "Уккан жокмун. Микрофонго жакыныраак кайра айтыңыз.", "I didn't catch that. Please say it again, closer to the microphone.",
                "Duyamadım. Mikrofona daha yakın tekrar söyleyin.", "Eshitmadim. Mikrofonga yaqinroq qayta ayting."), fromOwner: false);
            return;
        }
        PosLogger.Log($"ИИ-советник: вопрос голосом ({text.Length} симв., запись {wav.Length / 32000.0:0.#} с, распознано за {sttWatch.ElapsedMilliseconds} мс).", "INFO");
        _voiceAnswer = true;
        await SendAsync(text).ConfigureAwait(true);
    }

    /// <summary>Озвучка ответа: первая фраза — отдельным коротким запросом (звучит через 1–2 с), остальное готовится
    /// одновременно и играет следом. Не больше двух запросов на ответ — у бесплатного ключа малый лимит озвучки.</summary>
    private async Task SpeakAsync(string text)
    {
        VoiceChatPlayer.Stop();
        var generation = VoiceChatPlayer.Generation;
        // Таблица вслух — без разделителей и черт: «Кассир Тест, 10 смен, …».
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*\|?(\s*:?-{2,}:?\s*\|)+.*$\n?", "").Replace(" |", ",").Replace("|", "");
        var (first, rest) = SplitForSpeech(TelegramAiChat.ToPlainText(text));
        var more = T("Подробности — на экране.", "Толугураак — экранда.", "More details are on the screen.", "Ayrıntılar ekranda.", "Batafsil — ekranda.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var firstTask = TelegramVoice.SynthesizePcmAsync(first, more, cts.Token);
            var restTask = rest.Length > 0 ? TelegramVoice.SynthesizePcmAsync(rest, more, cts.Token) : null;
            var (pcm, rate) = await firstTask.ConfigureAwait(true);
            PosLogger.Log($"ИИ-советник: первая фраза озвучена за {watch.ElapsedMilliseconds} мс ({first.Length} симв.{(rest.Length > 0 ? $", дальше ещё {rest.Length}" : "")}).", "INFO");
            if (pcm is not null && IsVisible)
                VoiceChatPlayer.Enqueue(pcm, rate, generation);
            if (restTask is not null)
            {
                var (pcm2, rate2) = await restTask.ConfigureAwait(true);
                if (pcm2 is not null && IsVisible)
                    VoiceChatPlayer.Enqueue(pcm2, rate2, generation);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: ответ не озвучен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Первая фраза (до ~110 знаков) и остальное. Замер 05.10: короткая фраза озвучивается за 2,6–4,3 с,
    /// 176 знаков — за 6,5–10 с, поэтому первая фраза идёт отдельным коротким запросом, остальное — параллельно.</summary>
    private static (string Head, string Tail) SplitForSpeech(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (t.Length <= 110)
            return (t, "");
        var cut = -1;
        for (var i = 20; i < Math.Min(t.Length, 120); i++)
        {
            if (t[i] is '.' or '!' or '?' && (i + 1 == t.Length || t[i + 1] == ' '))
            {
                cut = i + 1;
                break;
            }
        }
        if (cut < 0)
            cut = t.LastIndexOf(' ', Math.Min(t.Length - 1, 100)) is var sp and > 20 ? sp : Math.Min(t.Length, 100);
        return (t[..cut].Trim(), t[cut..].Trim());
    }

    /// <summary>Блок «Модели ИИ»: ключи Groq (поиск в интернете, запасные ответы) и OpenRouter (запасные бесплатные модели).
    /// 2026-10-05, владелец: «сделай как у Gemini кнопку на получение ключа» — каждый ключ своим блоком, как ключ Gemini:
    /// заголовок, шаги, поле + «Получить ключ» + «Сохранить». Ключ проверяется до сохранения; пустое поле — прежний ключ остаётся.
    /// Пока ключа Groq нет, блок Groq показывается сам; OpenRouter (необязательный) — по кнопке ⚙.</summary>
    private void BuildModelsCard()
    {
        Use(_modelsCard, Border.BackgroundProperty, "BrushPanel");
        Use(_modelsCard, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(ProviderKeySection("Groq",
            T("Поиск в интернете — бесплатный ключ Groq", "Интернеттен издөө — Groq акысыз ачкычы", "Web search — a free Groq key",
                "İnternet araması — ücretsiz Groq anahtarı", "Internetda qidirish — bepul Groq kaliti"),
            T("Нажмите «Получить ключ» → войдите через Google → «Create API Key», скопируйте ключ (gsk_…) и вставьте ниже (карта не нужна, 1000 запросов в день). "
              + "Для поиска в Groq уходит только вопрос. Если Gemini не ответил, Groq ответит сам — тогда туда уходят только обезличенные цифры магазина: без сотрудников, должников и чеков (на такие вопросы отвечает только Gemini).",
                "«Ачкыч алуу» басыңыз → Google аркылуу кириңиз → «Create API Key», ачкычты (gsk_…) көчүрүп, төмөнгө чаптаңыз (карта керек эмес, күнүнө 1000 суроо). "
                + "Издөө үчүн Groq'ко суроо гана кетет. Gemini жооп бербесе, Groq өзү жооп берет — анда ага дүкөндүн жеке маалыматсыз сандары гана кетет: кызматкерлер, карыздарлар жана чектер жок.",
                "Press “Get a key” → sign in with Google → “Create API Key”, copy the key (gsk_…) and paste it below (no card, 1000 requests a day). "
                + "For search only the question goes to Groq. If Gemini doesn't answer, Groq answers itself — then only depersonalised shop figures go there: no staff, debtors or receipts.",
                "«Anahtar al»a basın → Google ile giriş yapın → «Create API Key», anahtarı (gsk_…) kopyalayıp aşağıya yapıştırın (kart gerekmez, günde 1000 istek). "
                + "Arama için Groq'a yalnızca soru gider. Gemini yanıt vermezse Groq kendisi yanıtlar — o zaman yalnızca kişisel veri içermeyen mağaza rakamları gider: çalışan, borçlu ve fiş yok.",
                "«Kalit olish» ni bosing → Google orqali kiring → «Create API Key», kalitni (gsk_…) nusxalab pastga joylang (karta kerak emas, kuniga 1000 so'rov). "
                + "Qidiruv uchun Groq'ga faqat savol ketadi. Gemini javob bermasa, Groq o'zi javob beradi — unda faqat shaxsiy ma'lumotsiz do'kon raqamlari ketadi: xodimlar, qarzdorlar va cheklarsiz."),
            "gsk_…", AiProviders.HasGroq, "https://console.groq.com/keys", AiProviders.TestGroqAsync,
            key => UserPreferences.Instance.GroqApiKey = key,
            onSaved: () =>
            {
                AddBubble(T("✓ Поиск в интернете включён (Groq). Спросите, например: «Какой сейчас курс доллара?»",
                    "✓ Интернеттен издөө күйгүзүлдү (Groq). Мисалы, сураңыз: «Доллардын курсу азыр канча?»",
                    "✓ Web search is on (Groq). Ask, for example: “What is the dollar rate now?”",
                    "✓ İnternet araması açık (Groq). Örneğin sorun: «Doların kuru şu an ne?»",
                    "✓ Internetda qidirish yoqildi (Groq). Masalan, so'rang: «Dollar kursi hozir qancha?»"), fromOwner: false);
                // Открылся сам (без ⚙) — после сохранения прячем, как блок ключа Gemini.
                if (!_routerSection.IsVisible)
                    _modelsCard.IsVisible = false;
            }));
        _routerSection.Child = ProviderKeySection("OpenRouter",
            T("Запасные модели — бесплатный ключ OpenRouter (необязательно)", "Запастагы моделдер — OpenRouter акысыз ачкычы (милдеттүү эмес)",
                "Backup models — a free OpenRouter key (optional)", "Yedek modeller — ücretsiz OpenRouter anahtarı (isteğe bağlı)",
                "Zaxira modellar — bepul OpenRouter kaliti (ixtiyoriy)"),
            T("Если у Gemini кончился дневной лимит, ответит бесплатная модель OpenRouter (50 запросов в день). Нажмите «Получить ключ» → войдите через Google → "
              + "«Create API Key», скопируйте ключ (sk-or-…) и вставьте ниже. При этом в OpenRouter уходят вопрос и только обезличенные цифры (без сотрудников, должников и чеков); бесплатные модели OpenRouter — разных компаний, условия хранения данных у них разные.",
                "Gemini'нин күндүк лимити бүтсө, OpenRouter'дин акысыз модели жооп берет (күнүнө 50 суроо). «Ачкыч алуу» басыңыз → Google аркылуу кириңиз → "
                + "«Create API Key», ачкычты (sk-or-…) көчүрүп, төмөнгө чаптаңыз. Анда OpenRouter'ге суроо жана жеке маалыматсыз сандар гана кетет (кызматкерлер, карыздарлар, чектер жок); акысыз моделдер ар кайсы компанияныкы.",
                "If Gemini's daily limit runs out, a free OpenRouter model answers (50 requests a day). Press “Get a key” → sign in with Google → "
                + "“Create API Key”, copy the key (sk-or-…) and paste it below. Then only the question and depersonalised figures go to OpenRouter (no staff, debtors or receipts); its free models belong to different companies with different data policies.",
                "Gemini'nin günlük limiti biterse ücretsiz bir OpenRouter modeli yanıtlar (günde 50 istek). «Anahtar al»a basın → Google ile giriş yapın → "
                + "«Create API Key», anahtarı (sk-or-…) kopyalayıp aşağıya yapıştırın. O zaman OpenRouter'a yalnızca soru ve kişisel veri içermeyen rakamlar gider (çalışan, borçlu, fiş yok); ücretsiz modeller farklı şirketlerindir.",
                "Gemini kunlik limiti tugasa, OpenRouter bepul modeli javob beradi (kuniga 50 so'rov). «Kalit olish» ni bosing → Google orqali kiring → "
                + "«Create API Key», kalitni (sk-or-…) nusxalab pastga joylang. Shunda OpenRouter'ga faqat savol va shaxsiy ma'lumotsiz raqamlar ketadi (xodimlar, qarzdorlar, cheklarsiz); bepul modellar turli kompaniyalarniki."),
            "sk-or-…", AiProviders.HasOpenRouter, "https://openrouter.ai/keys", AiProviders.TestOpenRouterAsync,
            key => UserPreferences.Instance.OpenRouterApiKey = key, onSaved: null);
        stack.Children.Add(_routerSection);
        _modelsCard.Child = stack;
        // Ключа Groq нет — блок Groq виден сразу (как блок ключа Gemini), OpenRouter — по ⚙.
        _routerSection.IsVisible = false;
        // 2026-10-21 («дизайн 10», О10): блок необязательных ключей — только по ⚙ (раньше без ключа Groq он всегда стоял над лентой).
        _modelsCard.IsVisible = false;
    }

    /// <summary>Блок одного ключа в стиле ключа Gemini: заголовок, шаги, [поле][Получить ключ][Сохранить], строка состояния.</summary>
    private StackPanel ProviderKeySection(string name, string titleText, string hintText, string sample, bool saved, string getUrl,
        Func<string, CancellationToken, Task<(bool Ok, string Message)>> test, Action<string> store, Action? onSaved)
    {
        var savedWatermark = T("ключ сохранён — вставьте новый, чтобы заменить", "ачкыч сакталды — алмаштыруу үчүн жаңысын чаптаңыз",
            "key saved — paste a new one to replace", "anahtar kaydedildi — değiştirmek için yenisini yapıştırın", "kalit saqlandi — almashtirish uchun yangisini joylang");
        var section = new StackPanel { Spacing = 8 };
        var title = new TextBlock { Text = titleText, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        section.Children.Add(title);
        var hint = new TextBlock { Text = hintText, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        section.Children.Add(hint);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        // 2026-10-27: Д-4 — скрытый ключ не отдаётся через UI Automation
        var box = UiKit.SecretInput(this, saved ? savedWatermark
            : T($"Ключ {name} ({sample})", $"{name} ачкычы ({sample})", $"{name} key ({sample})", $"{name} anahtarı ({sample})", $"{name} kaliti ({sample})"));
        box.PasswordChar = '•';
        row.Children.Add(box);
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var get = UiKit.Ghost(this, T("Получить ключ", "Ачкыч алуу", "Get a key", "Anahtar al", "Kalit olish"));
        get.Margin = new Thickness(8, 0, 0, 0);
        get.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(getUrl)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                status.Text = getUrl + " — " + ex.Message;
            }
        };
        Grid.SetColumn(get, 1);
        row.Children.Add(get);
        var save = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += async (_, _) =>
        {
            var key = (box.Text ?? "").Trim();
            if (key.Length == 0)
            {
                status.Text = T("Вставьте ключ в поле слева.", "Ачкычты сол жактагы талаага чаптаңыз.", "Paste the key into the field on the left.",
                    "Anahtarı soldaki alana yapıştırın.", "Kalitni chapdagi maydonga joylang.");
                return;
            }
            save.IsEnabled = false;
            status.Text = T("Проверяю ключ…", "Ачкычты текшерип жатам…", "Checking the key…", "Anahtar kontrol ediliyor…", "Kalit tekshirilmoqda…");
            (bool ok, string message) keyCheck;
            try
            {
                keyCheck = await test(key, LifetimeToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_closed)
            {
                return; // 2026-11-06 (Л4-26): окно закрыли, пока проверялся ключ
            }
            var (ok, message) = keyCheck;
            save.IsEnabled = true;
            PosLogger.Log($"ИИ-советник: ключ {name} {(ok ? "проверен и сохранён" : "не подошёл")}.", "INFO");
            if (!ok)
            {
                status.Text = T("Ключ не подошёл: ", "Ачкыч туура келген жок: ", "The key didn't work: ", "Anahtar çalışmadı: ", "Kalit ishlamadi: ") + message;
                return;
            }
            store(key);
            UserPreferences.Instance.SaveToDisk();
            box.Text = "";
            box.Watermark = savedWatermark;
            status.Text = "✓ " + message;
            onSaved?.Invoke();
        };
        Grid.SetColumn(save, 2);
        row.Children.Add(save);
        section.Children.Add(row);
        section.Children.Add(status);
        return section;
    }

    /// <summary>Источники из интернета под ответом: «🌐 Источники:» и кнопки-ссылки (открываются в браузере).</summary>
    private void AddWebSources(IReadOnlyList<TelegramAiChat.WebSource> sources)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var label = new TextBlock { Text = T("🌐 Найдено в интернете:", "🌐 Интернеттен табылды:", "🌐 Found on the web:", "🌐 İnternette bulundu:", "🌐 Internetdan topildi:"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(label);
        foreach (var source in sources.Take(5))
        {
            var title = source.Title.Length > 40 ? source.Title[..40] + "…" : source.Title;
            var link = UiKit.Ghost(this, title);
            link.Height = 28;
            link.FontSize = 12;
            link.Padding = new Thickness(10, 0);
            link.Margin = new Thickness(0, 0, 6, 6);
            ToolTip.SetTip(link, source.Uri);
            link.Click += async (_, _) =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                        await launcher.LaunchUriAsync(new Uri(source.Uri)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"ИИ-советник: ссылка не открылась ({ex.Message}).", "WARNING");
                }
            };
            panel.Children.Add(link);
        }
        _messages.Children.Add(panel);
    }

    /// <summary>2026-10-06, владелец в звонке: «Да, поставь четвёртое фото по счёту» — советник обещал, программа не делала.
    /// Варианты последнего поиска фото (видимые, по порядку на экране, с номерами №1…), выбор — голосом или текстом.</summary>
    private readonly List<(ProductPhotoFinder.Candidate Candidate, Button Button, Border Card, TextBlock Label)> _photoChoices = new();

    private static readonly string[][] OrdinalStems =
    {
        new[] { "перв", "биринчи", "first", "birinci", "birinchi" },
        new[] { "втор", "экинчи", "second", "ikinci", "ikkinchi" },
        new[] { "трет", "үчүнчү", "third", "üçüncü", "uchinchi" },
        new[] { "четв", "төртүнчү", "fourth", "dördüncü", "to'rtinchi" },
        new[] { "пят", "бешинчи", "fifth", "beşinci", "beshinchi" },
        new[] { "шест", "алтынчы", "sixth", "altıncı", "oltinchi" },
        new[] { "седьм", "жетинчи", "seventh", "yedinci", "yettinchi" },
        new[] { "восьм", "сегизинчи", "eighth", "sekizinci", "sakkizinchi" },
    };

    /// <summary>Номер фото из просьбы «поставь фото 4», «четвёртое фото», «№3»; null — это не выбор варианта.</summary>
    private int? PhotoChoiceNumber(string text)
    {
        var visible = _photoChoices.Count(c => c.Card.IsVisible);
        if (visible == 0 || text.Length > 90)
            return null;
        var t = text.ToLowerInvariant();
        var words = System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}\p{N}']+").Where(w => w.Length > 0).ToList();
        var aboutPhoto = new[] { "фото", "фотк", "вариант", "сүрөт", "photo", "picture", "option", "fotoğraf", "rasm", "surat", "№" }.Any(t.Contains);
        var act = words.Any(w => new[] { "постав", "став", "выбер", "бери", "давай", "кой", "танда", "use", "set", "pick", "take", "kullan", "seç", "qo'y", "tanla" }.Any(w.StartsWith));
        if (!aboutPhoto || !act)
            return null;
        int? n = null;
        if (System.Text.RegularExpressions.Regex.Match(t, @"(?:№|номер|number|nomer|фото|photo|сүрөт|вариант)\s*(\d{1,2})\b|\b(\d{1,2})\s*(?:-?(?:е|ое|й|ий|ю|th|st|nd|rd)\b|\s*(?:фото|photo|сүрөт|вариант))") is { Success: true } m)
            n = int.Parse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value);
        else
            for (var i = 0; i < OrdinalStems.Length && n is null; i++)
                if (words.Any(w => OrdinalStems[i].Any(w.StartsWith)))
                    n = i + 1;
        return n is >= 1 && n <= visible ? n : null;
    }

    /// <summary>Поставить вариант №n (по видимым на экране); текст результата для чата и звонка.</summary>
    private async Task<string> ApplyPhotoChoiceAsync(int n)
    {
        var choice = _photoChoices.Where(c => c.Card.IsVisible).ElementAt(n - 1);
        if (!choice.Button.IsEnabled)
            return T($"Фото №{n} уже поставлено или недоступно.", $"№{n} сүрөт коюлган же жеткиликсиз.", $"Photo #{n} is already set or unavailable.",
                $"{n} numaralı fotoğraf zaten ayarlı veya kullanılamıyor.", $"№{n} rasm allaqachon qo'yilgan yoki mavjud emas.");
        foreach (var other in _photoChoices.Where(c => c.Button != choice.Button))
            other.Button.IsEnabled = false;
        await ApplyPhotoAsync(choice.Button, choice.Candidate).ConfigureAwait(true);
        var ok = !choice.Button.IsEnabled;
        return ok
            ? T($"Фото №{n} поставлено товару «{choice.Candidate.Product.Title}».", $"№{n} сүрөт «{choice.Candidate.Product.Title}» товарына коюлду.",
                $"Photo #{n} set for “{choice.Candidate.Product.Title}”.", $"{n} numaralı fotoğraf «{choice.Candidate.Product.Title}» ürününe ayarlandı.",
                $"№{n} rasm «{choice.Candidate.Product.Title}» mahsulotiga qo'yildi.")
            : T($"Фото №{n} поставить не получилось — выберите другое.", $"№{n} сүрөттү коюу болбой калды — башкасын тандаңыз.",
                $"Couldn't set photo #{n} — choose another.", $"{n} numaralı fotoğraf ayarlanamadı — başka birini seçin.",
                $"№{n} rasmni qo'yib bo'lmadi — boshqasini tanlang.");
    }

    /// <summary>Товар, которому последним поставили фото (для «открой склад, проверь, поставилось ли фото»).</summary>
    private NurMarketKassa.Models.Pos.CatalogProductTileVm? _lastPhotoProduct;

    /// <summary>2026-10-06, владелец (снимок звонка: «открой склад и проверь, поставилась ли фото» — «Программа не открывает, где
    /// она?»; склад открылся через 17 с — шёл полный запрос к ИИ): «дай ИИ звонку полный доступ к программе — открывать вкладки
    /// и разделы и искать товары». «Открой …» программа выполняет сама и сразу: товар — на складе, иначе раздел меню.
    /// Ответ — что открыто; null — это не команда «открыть» или открыть нечего.</summary>
    private string? TryQuickOpen(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Length > 160)
            return null;
        var words = System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}\p{N}']+").Where(w => w.Length > 0).ToList();
        // 2026-10-07, владелец (звонок по-кыргызски: «почему ИИ не смог открыть склад и найти товар»): кыргызские «ач», «аччы»,
        // «кир». Только явная команда «открыть»: «покажи остатки на складе» — это вопрос, ответ таблицей в чат.
        var open = words.Any(w => new[] { "откр", "перей", "зайди", "зайти", "ачып", "ачкыла", "аччы", "кирип", "open", "aç", "och" }.Any(w.StartsWith)
                                  || w is "ач" or "кир" or "ачып");
        // 2026-10-07, владелец: «открывай вкладки только после команды открыть» — «найди / тап товар» больше не открывает склад:
        // сведения о товаре идут таблицей в чат (LiveTablePassAsync).
        if (!open || !Cap("nav.open"))
            return null;
        // Проверить фото — товар, которому его только что поставили. Иначе товар по целым словам названия (не подстрокой:
        // «финансы» находили товар «Нан»); раздел меню важнее товара, если не сказано «товар» и совпало одно слово.
        var section = ProductActionPlan.NavigateByWords?.Invoke(text, false);
        var (named, score) = QuickOpenProduct(words);
        var product = new[] { "фото", "фотк", "сүрөт", "photo" }.Any(t.Contains) && _lastPhotoProduct is { } photoProduct
            ? photoProduct
            : named is not null && (section is null || score >= 2 || words.Any(w => w.StartsWith("товар")))
                ? named
                : null;
        if (product is not null && ProductActionPlan.OpenProduct is { } openProduct)
        {
            openProduct(product);
            PosLogger.Log($"ИИ-советник: открыт товар «{product.Title}» на складе по фразе владельца.", "INFO");
            return T($"Открыт склад, товар «{product.Title}».", $"Кампа ачылды, товар «{product.Title}».", $"Warehouse opened at “{product.Title}”.",
                $"Depo açıldı, ürün «{product.Title}».", $"Ombor ochildi, mahsulot «{product.Title}».");
        }
        // 2026-10-27: разделы открывает маршрутизатор (RunNavRouteAsync) с проверкой результата; старое открытие по первым 5 буквам убрано.
        return null;
    }

    /// <summary>2026-10-27: фраза — для маршрутизатора, а не про товар: «открой склад Марс» / «открой Mars» (остаток фразы совпал с названием товара каталога)
    /// открывает товар (TryQuickOpen), а не раздел.</summary>
    private bool RouterOwnsPhrase(AdvisorNavRouter.Route route, string text)
    {
        if (route.Kind == AdvisorNavRouter.RouteKind.NotNav)
            return false;
        if (route.Strong && route.Residual.Count is >= 1 and <= 3)
        {
            var words = System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}']+")
                .Where(w => w.Length > 0 && route.Residual.Contains(AdvisorNavRouter.Fold(w))).ToList();
            if (words.Count > 0 && QuickOpenProduct(words).Product is not null)
                return false;
        }
        return true;
    }

    /// <summary>Открыть по маршруту и показать в чате ФАКТ: один пункт — «Открыл «Склад → Приёмка»» (после проверки); ничья — кнопки вариантов;
    /// неизвестный раздел — честно и кнопки основных разделов. Составная просьба («…и найди закончившиеся») — ещё и список закончившихся.</summary>
    private async Task<AdvisorTurn.NavReply> RunNavRouteAsync(AdvisorNavRouter.Route route)
    {
        // 2026-10-30: «Открывать разделы и товары по просьбе» выключено — не переходим, говорим почему.
        if (!Cap("nav.open"))
        {
            var blocked = AdvisorCapabilities.BlockedText("nav.open");
            AddBubble(blocked, fromOwner: false);
            return new AdvisorTurn.NavReply(AdvisorTurn.ReplyKind.Failed, blocked, Array.Empty<AdvisorNavRouter.NavTarget>(), null, null, Array.Empty<string>(), null, 0);
        }
        var open = ProductActionPlan.OpenNavTarget!;
        if (route.Kind == AdvisorNavRouter.RouteKind.Single)
        {
            var reply = await AdvisorTurn.OpenAsync(route.Targets[0], open, route.Companion, route.Residual).ConfigureAwait(true);
            AddBubble(reply.Text, fromOwner: false);
            if (reply.Kind == AdvisorTurn.ReplyKind.Opened && route.Companion is { } companion)
                AddCompanion(companion);
            return reply;
        }
        if (route.Kind == AdvisorNavRouter.RouteKind.Ambiguous)
        {
            var text = AdvisorTurn.ChooseText(route.Targets);
            AddNavButtons(AddBubble(text, fromOwner: false), route.Targets);
            return new AdvisorTurn.NavReply(AdvisorTurn.ReplyKind.Choose, text, route.Targets, null, route.Companion, route.Residual, null, 0);
        }
        var unknown = AdvisorTurn.UnknownText(route.Residual);
        var popular = AdvisorTurn.Popular();
        AddNavButtons(AddBubble(unknown, fromOwner: false), popular);
        return new AdvisorTurn.NavReply(AdvisorTurn.ReplyKind.Unknown, unknown, popular, null, null, route.Residual, null, 0);
    }

    /// <summary>Кнопки вариантов под пузырём: нажатие открывает пункт и пишет факт в чат.</summary>
    private void AddNavButtons(SelectableTextBlock body, IReadOnlyList<AdvisorNavRouter.NavTarget> targets)
    {
        if (body.Parent is not StackPanel content)
            return;
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var target in targets.Take(4))
        {
            var button = MorningReportCard.SmallButton(target.Title, T("Открыть", "Ачуу", "Open", "Aç", "Ochish") + ": " + target.Title);
            button.Margin = new Thickness(0, 4, 6, 0);
            button.MinHeight = 44; // 2026-10-27: цель касания не меньше 44
            button.Click += async (_, _) =>
            {
                foreach (var b in row.Children.OfType<Button>())
                    b.IsEnabled = false;
                var reply = await AdvisorTurn.OpenAsync(target, ProductActionPlan.OpenNavTarget!).ConfigureAwait(true);
                AddBubble(reply.Text, fromOwner: false);
                SaveCurrentChat();
            };
            row.Children.Add(button);
        }
        content.Children.Insert(content.Children.IndexOf(body) + 1, row);
    }

    /// <summary>Закончившиеся / заканчивающиеся товары из локального каталога (мгновенно, без нейросети) — вторая часть просьбы
    /// «открой приёмку и найди товары, которые закончились»: приёмка уже открыта, список здесь, а «Склад → Нет в наличии» — кнопкой.</summary>
    private void AddCompanion(AdvisorNavRouter.NavTarget companion)
    {
        var (count, names) = StockNames(companion.Filter);
        var what = companion.Filter == "low"
            ? T("заканчиваются", "түгөнүп баратат", "running low", "tükenmek üzere", "tugab bormoqda")
            : T("закончились", "түгөндү", "out of stock", "stokta yok", "tugagan");
        string text;
        if (companion.Filter is "low" or "out")
            text = count == 0
                ? T($"По локальному каталогу товаров, которые {what}, нет.", $"Жергиликтүү каталог боюнча «{what}» товарлар жок.", $"No products are {what} in the local catalog.",
                    $"Yerel katalogda «{what}» ürün yok.", $"Mahalliy katalogda «{what}» mahsulot yo'q.")
                : T($"Товаров, которые {what}: {count}. Первые: ", $"«{what}» товарлар: {count}. Алгачкылары: ", $"Products {what}: {count}. First ones: ",
                    $"«{what}» ürün: {count}. İlk olanlar: ", $"«{what}» mahsulotlar: {count}. Birinchilari: ") + string.Join(", ", names) + (count > names.Count ? "…" : ".");
        else
            text = T("Срок годности смотрите здесь:", "Жарактуулук мөөнөтүн бул жерден караңыз:", "See the expiry here:", "Son kullanma için buraya bakın:", "Yaroqlilik muddatini shu yerdan ko'ring:");
        AddNavButtons(AddBubble(text, fromOwner: false), new[] { companion, AdvisorNavRouter.ById("restock")! });
    }

    private static (int Count, List<string> Names) StockNames(string? filter)
    {
        try
        {
            var tiles = CatalogCacheService.Products.Where(p => !p.IsService).ToList();
            var selected = filter == "out" ? tiles.Where(p => p.Quantity <= 0)
                : filter == "low" ? tiles.Where(p => p.IsLowStock && p.Quantity > 0)
                : Enumerable.Empty<NurMarketKassa.Models.Pos.CatalogProductTileVm>();
            var list = selected.OrderBy(p => p.Quantity).ThenBy(p => p.Title).ToList();
            return (list.Count, list.Take(12).Select(p => p.Title).ToList());
        }
        catch (InvalidOperationException)
        {
            return (0, new List<string>());
        }
    }

    /// <summary>Нейросеть предложила открыть раздел (open_section): выполняем по словарю с проверкой и пишем в чат факт.</summary>
    private async Task ShowOpenResultAsync(ProductActionPlan.Step open)
    {
        var result = await ProductActionPlan.ExecuteAsync(open, T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi")).ConfigureAwait(true);
        if (open.Op == "open_section" && result.Message.Length > 0)
            AddBubble(result.Message, fromOwner: false);
    }

    /// <summary>Инструмент open_section живого разговора: модель выбирает id из словаря программы, программа открывает и возвращает ФАКТ —
    /// модель говорит только его. Вызывается из фонового потока.</summary>
    private async Task<string> LiveToolAsync(GeminiLiveVoice live, string name, string argsJson)
    {
        if (name != "open_section")
            return "НЕ ВЫПОЛНЕНО: такого инструмента нет.";
        if (!Cap("nav.open"))
            return "НЕ ВЫПОЛНЕНО: открывать разделы выключено владельцем (Настройки → Операции → Нур Советник). Скажи владельцу ровно это одной фразой.";
        string target = "", words = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            target = doc.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
            words = doc.RootElement.TryGetProperty("words", out var w) ? w.GetString() ?? "" : "";
        }
        catch (System.Text.Json.JsonException)
        {
        }
        var route = target.Length > 0 && target != "unknown" ? AdvisorNavRouter.Resolve(target) : AdvisorNavRouter.Parse("открой " + words);
        if (route.Kind == AdvisorNavRouter.RouteKind.NotNav)
            route = new AdvisorNavRouter.Route(AdvisorNavRouter.RouteKind.Unknown, Array.Empty<AdvisorNavRouter.NavTarget>(), null, Array.Empty<string>(), true);
        var reply = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_live != live)
                return null;
            _liveToolNav = true;
            return await RunNavRouteAsync(route).ConfigureAwait(true);
        }).ConfigureAwait(false);
        if (reply is null)
            return "НЕ ВЫПОЛНЕНО: разговор уже завершён.";
        PosLogger.Log($"ADVISOR_PERF голос: инструмент open_section({target}{(words.Length > 0 ? ", «" + words + "»" : "")}) → {reply.Kind}.", "INFO");
        return reply.Kind switch
        {
            AdvisorTurn.ReplyKind.Opened => "ОТКРЫТО (проверено программой): " + reply.Text + " Скажи владельцу ровно это, коротко.",
            AdvisorTurn.ReplyKind.Failed => "НЕ ОТКРЫТО: " + reply.Text + " Скажи владельцу, что не получилось, и причину.",
            AdvisorTurn.ReplyKind.Choose => "НЕ ОТКРЫТО, нужен выбор. Владельцу показаны кнопки: " + string.Join("; ", reply.Options.Select(o => o.Title)) + ". Спроси, какой из них открыть.",
            _ => "НЕ ОТКРЫТО: " + reply.Text + " Скажи, что такого раздела не нашёл, и ничего не придумывай.",
        };
    }

    /// <summary>Инструмент живого разговора уже открыл раздел в этом ответе — запасной разбор распознанной фразы не нужен.</summary>
    private volatile bool _liveToolNav;

    /// <summary>Товар для «открой …»: слова фразы (кроме команд) совпадают с началом слов названия; лучший по числу совпадений.</summary>
    private static (NurMarketKassa.Models.Pos.CatalogProductTileVm? Product, int Score) QuickOpenProduct(List<string> words)
    {
        string[] skip = { "откр", "перей", "зайд", "зайт", "покаж", "товар", "склад", "раздел", "вкладк", "провер", "постав", "фото", "фотк",
            "пожалуйста", "мне", "его", "это", "там", "где", "ачып", "ачкыла", "open", "show" };
        var said = words.Where(w => w.Length >= 3 && !skip.Any(w.StartsWith)).Distinct().ToList();
        if (said.Count == 0)
            return (null, 0);
        IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> tiles;
        try
        {
            tiles = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return (null, 0);
        }
        NurMarketKassa.Models.Pos.CatalogProductTileVm? best = null;
        var bestScore = 0;
        foreach (var tile in tiles)
        {
            var titleWords = System.Text.RegularExpressions.Regex.Split(tile.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            var score = said.Count(w => titleWords.Any(tw => tw.StartsWith(w) || (w.Length >= 4 && tw.Length >= 4 && w.StartsWith(tw))));
            if (score > bestScore)
                (best, bestScore) = (tile, score);
        }
        return (best, bestScore);
    }

    /// <summary>2026-10-06, владелец: «добавь редактор к нашему ИИ звонку». «Поставь тему Ала-Тоо», «тёмный стиль сайта»,
    /// «опубликуй сайт» — программа открывает «Редактор сайта» и выполняет сама. null — это не команда редактору.</summary>
    private async Task<string?> SiteEditorCommandAsync(string text)
    {
        if (!SiteEditorWindow.LooksLikeCommand(text) || ProductActionPlan.OpenSection is not { } openSection)
            return null;
        // 2026-10-27 (аудит редакторов, В-1): вопрос («Как опубликовать сайт?») — только объяснение, редактор не открываем и ничего не публикуем.
        if (SiteEditorWindow.IsQuestion(text))
            return SiteEditorWindow.PublishHowTo();
        // 2026-10-30: «Команды сайту» выключены — сайт из чата не меняем и не публикуем.
        if (!Cap("nav.site"))
            return AdvisorCapabilities.BlockedText("nav.site");
        openSection("siteeditor");
        for (var i = 0; i < 60 && SiteEditorWindow.Current is not { IsReady: true }; i++)
            await Task.Delay(250).ConfigureAwait(true);
        if (SiteEditorWindow.Current is not { IsReady: true } editor)
            return T("Редактор сайта не открылся — попробуйте ещё раз.", "Сайттын редактору ачылган жок — дагы аракет кылыңыз.", "The website editor didn't open — try again.",
                "Site düzenleyici açılmadı — tekrar deneyin.", "Sayt muharriri ochilmadi — qayta urinib ko'ring.");
        var result = await editor.VoiceCommandAsync(text).ConfigureAwait(true);
        // 2026-10-07: «открывай вкладки только после команды открыть» — сделали и вернулись к советнику, если «открой» не говорили.
        if (!HasOpenCommand(text))
            openSection("aiadvisor");
        return result;
    }

    /// <summary>Товары, о которых говорили в звонке последними (для «поищи в интернете» без названия).</summary>
    private List<NurMarketKassa.Models.Pos.CatalogProductTileVm> _liveLastProducts = new();
    private DateTime _liveLastPhotoTalkUtc = DateTime.MinValue;

    /// <summary>Товары по словам фразы: слово — начало слова названия («батончика» → «Батончик Mars 50г»), не подстрока
    /// («нас» не находит «Ананас»); только лучшие совпадения, до <paramref name="max"/>.</summary>
    private static List<NurMarketKassa.Models.Pos.CatalogProductTileVm> ProductsByWords(string phrase, int max)
    {
        string[] skip = { "найд", "найт", "поищ", "ищи", "покаж", "показ", "интернет", "котор", "есть", "мне", "нас", "наш", "склад", "фот", "сүрөт",
            "картин", "изображ", "пожалуйста", "это", "там", "где", "товар", "откр", "постав", "photo", "find", "show", "нету", "никак", "нет" };
        var words = System.Text.RegularExpressions.Regex.Split(phrase.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3 && !skip.Any(w.StartsWith)).Distinct().ToList();
        if (words.Count == 0)
            return new();
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> tiles;
        try
        {
            tiles = CatalogCacheService.Products.Where(t => !t.IsService).ToList();
        }
        catch (InvalidOperationException)
        {
            return new();
        }
        var scored = tiles.Select(tile =>
        {
            var titleWords = System.Text.RegularExpressions.Regex.Split(tile.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            return (Tile: tile, Score: words.Count(w => titleWords.Any(tw => tw.StartsWith(w) || (w.Length >= 4 && tw.Length >= 4 && w.StartsWith(tw[..Math.Min(tw.Length, 6)])))));
        }).Where(x => x.Score > 0).ToList();
        if (scored.Count == 0)
            return new();
        var best = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == best).Select(x => x.Tile).Take(max).ToList();
    }

    /// <summary>2026-10-06/07, владелец (снимки звонка): «найди мне фотки батончика Mars» — советник сказал «программа нашла», в чате
    /// ничего; «перестань выдавать фото из базы NurCRM, ищи прямо в интернете». Любая просьба о фото в звонке — сразу поиск в
    /// интернете с индикатором в чате: товар склада по словам (или тот, о котором говорили только что); такого товара на складе
    /// нет — ищем по словам фразы (без кнопки «Поставить»). Речь владельца распознана криво («non bolotat»), а советник сам сказал,
    /// что ищет фото, — товар берём из слов советника. true — просьба о фото обработана.</summary>
    private bool LivePhotoPass(string spoken, string aiSaid, GeminiLiveVoice live)
    {
        static bool OpenWords(string x) => new[] { "откр", "перейд", "зайди", "ачып" }.Any(x.Contains);
        var t = spoken.ToLowerInvariant();
        // «Открой склад, проверь фото» — это открыть товар (TryQuickOpen), не поиск фото.
        if (OpenWords(t))
            return false;
        // 2026-10-30: «Искать и ставить фото товаров» выключено — поиск в интернете не запускаем (советнику в звонке это сказано в инструкции).
        if (!Cap("inv.photo"))
            return false;
        var recentPhotoTalk = DateTime.UtcNow - _liveLastPhotoTalkUtc < TimeSpan.FromMinutes(3);
        // Поиск разрешает только прямая команда владельца, а не слова «фото» или обещание самого ИИ.
        var fromOwner = ProductActionPlan.IsExplicitPhotoWebSearchRequest(t)
                        || (recentPhotoTalk && ProductActionPlan.IsExplicitWebSearchRequest(t));
        if (!fromOwner)
            return false;
        var source = spoken;
        var products = ProductsByWords(source, 3);
        // 2026-10-21 (О7): прежний товар — только если владелец явно говорит о нём же («этого», «его», «ушул»), а не всегда, когда
        // в фразе нет названия (07.10 звонок искал фото «Патронов для степлера» вместо «Легенды клубничной»).
        if (products.Count == 0 && recentPhotoTalk && System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<!\p{L})(его|этого|этот|этому|ему|ушул|анын|it|this)(?!\p{L})"))
            products = _liveLastProducts;
        if (products.Count == 0)
        {
            // Товара на складе нет — ищем по словам фразы (посмотреть можно, поставить некуда).
            string[] skip = { "найд", "найт", "поищ", "ищи", "покаж", "интернет", "фот", "сүрөт", "картин", "мне", "пожалуйста", "тап", "изде",
                "көрсөт", "из", "в", "на", "для", "photo", "find", "show", "программ", "экран" };
            var query = string.Join(" ", System.Text.RegularExpressions.Regex.Split(source, @"[^\p{L}\p{N}]+")
                .Where(w => w.Length >= 3 && !skip.Any(w.ToLowerInvariant().StartsWith)).Take(5));
            if (query.Length < 3)
                return false;
            products = new() { new NurMarketKassa.Models.Pos.CatalogProductTileVm("", query, "", false) };
        }
        _liveLastProducts = products;
        _liveLastPhotoTalkUtc = DateTime.UtcNow;
        var names = string.Join(", ", products.Select(p => p.Title));
        _ = ShowPhotoChoicesAsync(products);
        PosLogger.Log($"ИИ-советник: в звонке поиск фото в интернете — {names}.", "INFO");
        // 2026-10-21 (О7): советник переспрашивает «ищу фото «X» — верно?» — не тот товар владелец поправит сразу.
        AddBubble(T($"Ищу фото: «{names}». Не тот товар — скажите название ещё раз.", $"Сүрөт издеп жатам: «{names}». Башка товар болсо — атын кайра айтыңыз.",
            $"Searching photos for “{names}”. Wrong product? Say the name again.", $"«{names}» için fotoğraf arıyorum. Yanlış ürünse adını tekrar söyleyin.",
            $"«{names}» uchun rasm qidiryapman. Boshqa mahsulot bo'lsa — nomini yana ayting."), fromOwner: false);
        _ = live.SendTextAsync($"[Программа] Ищу фото в интернете для: {names}. На экране крутится индикатор поиска, через несколько секунд появятся "
                               + "варианты с номерами; владелец выберет словами «поставь фото номер N». Фото из склада программы не показывай и не обещай. "
                       + $"Скажи одной фразой ровно так: «Ищу фото «{names}» — верно?». Прежние товары из разговора не ищи.");
        return true;
    }

    /// <summary>Кроме «открой» во фразе есть действие с товаром («открой склад и спиши 1 Марс») — его разбирает ИИ.</summary>
    private static bool HasActionBesidesOpen(string text) =>
        ProductActionPlan.LooksLikeAction(System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"откр\w*|ачып|ачкыла\w*|покажи товар", " "));

    /// <summary>Номера «№1 · сайт» по видимым вариантам — после скрытия тех, что не открылись.</summary>
    private void RenumberPhotoChoices()
    {
        var i = 0;
        foreach (var c in _photoChoices.Where(c => c.Card.IsVisible))
            c.Label.Text = $"№{++i} · {c.Candidate.Source.Replace("интернет: ", "")}";
    }

    /// <summary>2026-10-10: запрос «покажи/проанализируй склад без фото» отвечает по полному локальному каталогу, а не по короткой AI-сводке.</summary>
    private static bool IsNoPhotoInventoryQuery(string question)
    {
        var text = (question ?? "").ToLowerInvariant();
        if (ProductActionPlan.IsExplicitPhotoWebSearchRequest(text))
            return false;
        var withoutPhoto = new[] { "без фото", "без фотограф", "нет фото", "нет фотограф", "without photo", "no photo", "fotoğrafsız", "rasmsiz", "rasm yo'q", "сүрөтү жок" }
            .Any(text.Contains);
        var asksForInventory = new[] { "склад", "товар", "спис", "перечис", "покаж", "вывед", "введ", "анализ", "какие", "inventory", "product", "list", "show", "analy" }
            .Any(text.Contains);
        return withoutPhoto && asksForInventory;
    }

    /// <summary>Полный отчёт по отсутствию фото. Используется локальная копия всего каталога; внешние сайты и AI не вызываются.</summary>
    private static string BuildNoPhotoInventoryReport(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = LocalProductRepository.Instance.LoadAllTiles()
            .Where(product => !product.IsService)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.Count == 0)
            return T("Локальный каталог пуст или ещё не загружен. Обновите каталог и повторите запрос.",
                "Жергиликтүү каталог бош же жүктөлө элек. Каталогду жаңыртып, кайра сураңыз.",
                "The local catalog is empty or has not loaded yet. Refresh the catalog and try again.",
                "Yerel katalog boş veya henüz yüklenmedi. Kataloğu yenileyip tekrar deneyin.",
                "Mahalliy katalog bo'sh yoki hali yuklanmagan. Katalogni yangilab qayta urinib ko'ring.");

        var missing = catalog
            .Where(product => string.IsNullOrWhiteSpace(product.ImageUrl) && string.IsNullOrWhiteSpace(product.ProductImagePath))
            .OrderBy(product => product.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (missing.Count == 0)
            return T($"Проверено товаров: {catalog.Count}. У всех товаров каталога есть фото.",
                $"Текшерилген товарлар: {catalog.Count}. Каталогдогу бардык товарларда сүрөт бар.",
                $"Products checked: {catalog.Count}. Every catalog product has a photo.",
                $"Kontrol edilen ürün: {catalog.Count}. Katalogdaki tüm ürünlerin fotoğrafı var.",
                $"Tekshirilgan mahsulotlar: {catalog.Count}. Katalogdagi barcha mahsulotlarda rasm bor.");

        static string Cell(string? value) => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(T("Товары без фото в локальном каталоге:", "Жергиликтүү каталогдогу сүрөтсүз товарлар:",
            "Products without photos in the local catalog:", "Yerel katalogda fotoğrafı olmayan ürünler:",
            "Mahalliy katalogdagi rasmsiz mahsulotlar:"));
        sb.AppendLine("| № | " + T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + " | "
                      + T("Штрихкод / артикул", "Штрихкод / артикул", "Barcode / article", "Barkod / stok kodu", "Shtrix-kod / artikul") + " | "
                      + T("Категория / бренд", "Категория / бренд", "Category / brand", "Kategori / marka", "Turkum / brend") + " | "
                      + T("Цена", "Баасы", "Price", "Fiyat", "Narx") + " | "
                      + T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq") + " |");
        sb.AppendLine("|---:|---|---|---|---:|---:|");
        for (var index = 0; index < missing.Count; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var product = missing[index];
            var barcode = string.Join(" / ", new[] { product.Barcode, product.Article }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var categoryBrand = string.Join(" / ", new[] { product.Category, product.Brand }.Where(value => !string.IsNullOrWhiteSpace(value)));
            sb.Append("| ").Append(index + 1).Append(" | ").Append(Cell(product.Title)).Append(" | ")
                .Append(Cell(barcode)).Append(" | ").Append(Cell(categoryBrand)).Append(" | ")
                .Append(Cell(product.PriceLineDisplay)).Append(" | ").Append(Cell(product.StockWithUnitText)).AppendLine(" |");
        }
        sb.AppendLine();
        sb.Append(T("Всего товаров без фото", "Сүрөтсүз товарлардын жалпы саны", "Products without photos", "Fotoğrafı olmayan ürün sayısı", "Rasmsiz mahsulotlar soni"))
            .Append(": ").Append(missing.Count).Append(" / ").Append(catalog.Count);
        return sb.ToString();
    }

}
