using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (Л-9 «Поддержка»): «Написать в поддержку» — одной кнопкой собрать обращение: описание проблемы, последние строки журнала
/// (без паролей и ключей — SensitiveDataRedactor), версия, компания и филиал, снимок окна по желанию. Уходит существующим каналом поддержки
/// (SupportLogService → бот поддержки). Бот не подключён — WhatsApp открывается сам с готовым текстом (статус «Отправлено через WhatsApp»); связи нет —
/// обращение остаётся в списке со статусом «Не отправлено» и кнопкой «Повторить». Часы работы и срок ответа — SupportInfo (нет данных — строки скрыты).
/// Открывается из меню «Тех. поддержка» (касса и программа владельца), из окна ошибки и из «Журнала ошибок».</summary>
public sealed class SupportRequestWindow : Window
{
    private const int HistoryRows = 5;

    private readonly string _source;
    private readonly string _branch;
    private readonly RenderTargetBitmap? _shot;
    private readonly TextBox _description;
    private readonly CheckBox _withLog;
    private readonly CheckBox _withShot;
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _send;
    private readonly Button _whatsApp;
    private readonly StackPanel _history = new() { Spacing = 6 };
    private readonly ModalShell _shell;
    private SupportRequestRecord? _lastFailed;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Открыть окно. <paramref name="owner"/> — окно, снимок которого можно приложить; <paramref name="source"/> — «меню», «окно ошибки», «журнал»;
    /// <paramref name="problemText"/> — готовое описание (текст ошибки), подставляется в поле.</summary>
    public static async Task ShowAsync(Window? owner, string source, string? problemText = null)
    {
        SupportRequestWindow? window = null;
        try
        {
            window = new SupportRequestWindow(owner, source, problemText);
            await PosDialogHost.ShowAsync(window, owner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: окно обращения не открылось ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            window?.ReleaseShot();
        }
    }

    public SupportRequestWindow(Window? owner, string source, string? problemText)
    {
        _source = source;
        _branch = SafeBranch();
        _shot = TryCaptureOwner(owner);
        Title = T("Написать в поддержку", "Колдоого жазуу", "Contact support", "Destek ile iletişim", "Yordamga yozish");
        Width = 672;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this.Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        this.Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var version = typeof(SupportRequestWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        var company = CompanyInfoService.LastCompany?.Name;
        if (string.IsNullOrWhiteSpace(company))
            company = UserPreferences.Instance.StoreName;
        var subtitle = $"NurCRM {version} · {(string.IsNullOrWhiteSpace(company) ? "—" : company)}" +
                       (_branch.Length > 0 ? " · " + T("филиал", "филиал", "branch", "şube", "filial") + " " + _branch : "");

        var root = new StackPanel { Spacing = 12 };

        // Часы работы и срок первого ответа (тексты-настройки SupportInfo).
        // 2026-11-20: строки без данных («уточняется») не показываем; нет ни часов, ни срока — карточки нет совсем.
        var hoursLines = new List<string>();
        if (SupportInfo.HoursOrNull() is { } hoursValue)
            hoursLines.Add(T("Часы работы поддержки: ", "Колдоонун иш убактысы: ", "Support hours: ", "Destek saatleri: ", "Yordam ish vaqti: ") + hoursValue);
        if (SupportInfo.ResponseTimeOrNull() is { } responseValue)
            hoursLines.Add(T("Первый ответ: ", "Биринчи жооп: ", "First reply: ", "İlk yanıt: ", "Birinchi javob: ") + responseValue);
        if (hoursLines.Count > 0)
        {
            var hours = new TextBlock { Text = string.Join("\n", hoursLines), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            hours.Bind(TextBlock.ForegroundProperty, hours.GetResourceObservable("BrushText"));
            var hoursCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), Child = hours };
            hoursCard.Bind(Border.BackgroundProperty, hoursCard.GetResourceObservable("BrushAccentSoft"));
            hoursCard.Bind(Border.BorderBrushProperty, hoursCard.GetResourceObservable("BrushAccent"));
            root.Children.Add(hoursCard);
        }

        root.Children.Add(Soft(T("Что случилось? Опишите коротко: что делали и что пошло не так.",
            "Эмне болду? Кыскача жазыңыз: эмне кылдыңыз жана эмне туура эмес болду.",
            "What happened? Briefly describe what you did and what went wrong.",
            "Ne oldu? Kısaca yazın: ne yaptınız ve neyin ters gittiği.",
            "Nima bo'ldi? Qisqacha yozing: nima qildingiz va nima noto'g'ri ketdi.")));
        _description = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 160, MaxLength = SupportRequestService.MaxDescriptionChars,
            Text = problemText is { Length: > 0 } ? SupportRequestService.CleanDescription(problemText) : "",
        };
        root.Children.Add(_description);

        _withLog = new CheckBox
        {
            IsChecked = true, MinHeight = 44,
            Content = T($"Приложить журнал кассы (последние {SupportLogTail.DefaultLines} строк, пароли и ключи закрыты)",
                $"Кассанын журналын тиркөө (акыркы {SupportLogTail.DefaultLines} саптар, сырсөздөр жана ачкычтар жабык)",
                $"Attach the till log (last {SupportLogTail.DefaultLines} lines, passwords and keys masked)",
                $"Kasa günlüğünü ekle (son {SupportLogTail.DefaultLines} satır, parolalar ve anahtarlar gizli)",
                $"Kassa jurnalini ilova qilish (oxirgi {SupportLogTail.DefaultLines} qator, parollar va kalitlar yopiq)"),
        };
        _withShot = new CheckBox
        {
            IsChecked = false, MinHeight = 44, IsEnabled = _shot is not null,
            Content = T("Приложить снимок окна (на нём могут быть данные клиентов)",
                "Терезенин сүрөтүн тиркөө (анда кардарлардын маалыматы болушу мүмкүн)",
                "Attach a screenshot of the window (it may show customer data)",
                "Pencere görüntüsünü ekle (müşteri verileri görünebilir)",
                "Oyna suratini ilova qilish (unda mijozlar ma'lumoti bo'lishi mumkin)"),
        };
        root.Children.Add(_withLog);
        root.Children.Add(_withShot);
        root.Children.Add(Soft(T("Вместе с обращением уйдут версия, компания и филиал.",
            "Кайрылуу менен бирге версия, компания жана филиал жөнөтүлөт.",
            "The version, company and branch are sent with the request.",
            "Sürüm, şirket ve şube talebinizle birlikte gönderilir.",
            "Murojaat bilan birga versiya, kompaniya va filial yuboriladi.")));

        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushText"));
        _whatsApp = new Button
        {
            Content = T("Открыть WhatsApp с текстом обращения", "Кайрылуунун тексти менен WhatsApp ачуу", "Open WhatsApp with the request text",
                "Talep metniyle WhatsApp'ı aç", "Murojaat matni bilan WhatsApp'ni ochish"),
            Classes = { "nur-secondary" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
        };
        _whatsApp.Click += (_, _) => OpenWhatsApp(_lastFailed);
        root.Children.Add(_status);
        root.Children.Add(_whatsApp);

        var historyTitle = new TextBlock
        {
            Text = T("Мои обращения", "Менин кайрылууларым", "My requests", "Taleplerim", "Murojaatlarim"),
            FontSize = 14, FontWeight = FontWeight.SemiBold,
        };
        historyTitle.Bind(TextBlock.ForegroundProperty, historyTitle.GetResourceObservable("BrushText"));
        root.Children.Add(historyTitle);
        root.Children.Add(_history);

        var close = new Button
        {
            Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), Classes = { "nur-secondary" }, IsCancel = true,
        };
        close.Click += (_, _) => Close();
        _send = new Button
        {
            Content = T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), Classes = { "nur-primary" }, MinWidth = 200,
        };
        _send.Click += async (_, _) => await SendAsync().ConfigureAwait(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { close, _send } };

        _shell = new ModalShell { Margin = new Thickness(16), Title = Title, Subtitle = subtitle, Footer = buttons, Content = root, InitialFocus = _description };
        Content = _shell;
        NarrowLayout.Attach(this, 560, narrow =>
        {
            _shell.Margin = narrow ? new Thickness(6) : new Thickness(16);
            _shell.Padding = narrow ? new Thickness(12, 12) : new Thickness(24, 18);
        });
        RenderHistory();
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static string SafeBranch()
    {
        try
        {
            return PosApp.AuthApi?.ActiveBranchId ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Снимок окна-владельца (до открытия этого окна). Ошибка — снимка нет, флажок выключен.</summary>
    private static RenderTargetBitmap? TryCaptureOwner(Window? owner)
    {
        try
        {
            if (owner is not { IsVisible: true } || owner.Bounds.Width < 1 || owner.Bounds.Height < 1)
                return null;
            // 2026-11-19: размер снимка в пикселях (округление вверх) — через PixelSize.FromSize, чтобы проверка «деньги в окнах» не принимала это за расчёт
            var size = PixelSize.FromSize(owner.Bounds.Size, 1.0);
            var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(owner);
            return bitmap;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: снимок окна не сделан ({ex.GetType().Name}).", "INFO");
            return null;
        }
    }

    private void ReleaseShot() => _shot?.Dispose();

    private byte[]? EncodeShot()
    {
        if (_shot is null || _withShot.IsChecked != true)
            return null;
        try
        {
            using var ms = new MemoryStream();
            _shot.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: снимок окна не сохранён ({ex.GetType().Name}).", "INFO");
            return null;
        }
    }

    private async Task SendAsync()
    {
        var text = (_description.Text ?? "").Trim();
        if (text.Length < 5)
        {
            ShowStatus(T("Опишите проблему хотя бы одной фразой.", "Көйгөйдү жок дегенде бир сүйлөм менен жазыңыз.", "Describe the problem in at least a short phrase.",
                "Sorunu en az kısa bir cümleyle anlatın.", "Muammoni kamida bitta jumla bilan yozing."), "BrushDanger");
            return;
        }
        if (!_shell.TryBeginBusy())
            return;
        try
        {
            _send.IsEnabled = false;
            _whatsApp.IsVisible = false;
            ShowStatus(T("Отправляю…", "Жөнөтүп жатам…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…"), "BrushTextSoft");
            var includeLog = _withLog.IsChecked == true;
            var shot = EncodeShot();
            var source = _source;
            var branch = _branch;
            var record = await Task.Run(() => SupportRequestService.SubmitAsync(text, includeLog, shot, source, branch)).ConfigureAwait(true);
            ShowResult(record);
            if (record.Status == SupportRequestRecord.StatusSent || SupportRequestService.IsWhatsApp(record))
                _description.Text = "";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: обращение не отправлено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowStatus(T("Не удалось отправить обращение. Попробуйте ещё раз.", "Кайрылууну жөнөтүү мүмкүн болгон жок. Кайра аракет кылыңыз.",
                "Could not send the request. Please try again.", "Talep gönderilemedi. Tekrar deneyin.", "Murojaatni yuborib bo'lmadi. Qayta urinib ko'ring."), "BrushDanger");
        }
        finally
        {
            _send.IsEnabled = true;
            _shell.EndBusy();
            RenderHistory();
        }
    }

    private void ShowResult(SupportRequestRecord record)
    {
        switch (record.Status)
        {
            case SupportRequestRecord.StatusSent:
                _lastFailed = null;
                ShowStatus(T($"Обращение № {record.Id} отправлено. Ответ придёт по телефону или в WhatsApp.",
                    $"№ {record.Id} кайрылуу жөнөтүлдү. Жооп телефон же WhatsApp аркылуу келет.",
                    $"Request № {record.Id} was sent. The reply will come by phone or WhatsApp.",
                    $"№ {record.Id} numaralı talep gönderildi. Yanıt telefon veya WhatsApp ile gelir.",
                    $"№ {record.Id} murojaat yuborildi. Javob telefon yoki WhatsApp orqali keladi."), "BrushSuccess");
                break;
            case SupportRequestRecord.StatusWhatsApp:
            case SupportRequestRecord.StatusNotConfigured:
                // 2026-11-20: серверного канала нет — WhatsApp открывается сам, с готовым текстом на номер поддержки NurCRM; не тупик «канал не подключён».
                _lastFailed = record;
                ShowStatus(T($"Обращение № {record.Id} отправлено через WhatsApp: текст уже готов — нажмите в WhatsApp «Отправить».",
                    $"№ {record.Id} кайрылуу WhatsApp аркылуу жөнөтүлдү: тексти даяр — WhatsApp'та «Жөнөтүү» баскычын басыңыз.",
                    $"Request № {record.Id} sent via WhatsApp: the text is ready — press “Send” in WhatsApp.",
                    $"№ {record.Id} numaralı talep WhatsApp ile gönderildi: metin hazır — WhatsApp'ta «Gönder»e basın.",
                    $"№ {record.Id} murojaat WhatsApp orqali yuborildi: matn tayyor — WhatsApp'da «Yuborish»ni bosing."), "BrushSuccess");
                _whatsApp.Content = T("Открыть WhatsApp ещё раз", "WhatsApp'ты кайра ачуу", "Open WhatsApp again", "WhatsApp'ı tekrar aç", "WhatsApp'ni yana ochish");
                _whatsApp.IsVisible = true;
                OpenWhatsApp(record);
                break;
            default:
                _lastFailed = record;
                ShowStatus(T($"Обращение № {record.Id} не отправлено: {record.Error}. Оно сохранено в списке — нажмите «Повторить», когда появится связь.",
                    $"№ {record.Id} кайрылуу жөнөтүлгөн жок: {record.Error}. Ал тизмеде сакталды — байланыш чыкканда «Кайталоо» баскычын басыңыз.",
                    $"Request № {record.Id} was not sent: {record.Error}. It is saved in the list — press “Retry” when the connection is back.",
                    $"№ {record.Id} numaralı talep gönderilmedi: {record.Error}. Listede kayıtlı — bağlantı gelince «Tekrar dene»ye basın.",
                    $"№ {record.Id} murojaat yuborilmadi: {record.Error}. U ro'yxatda saqlandi — aloqa paydo bo'lganda «Qayta urinish»ni bosing."), "BrushDanger");
                _whatsApp.Content = T("Открыть WhatsApp с текстом обращения", "Кайрылуунун тексти менен WhatsApp ачуу", "Open WhatsApp with the request text",
                    "Talep metniyle WhatsApp'ı aç", "Murojaat matni bilan WhatsApp'ni ochish");
                _whatsApp.IsVisible = true;
                break;
        }
    }

    private void ShowStatus(string text, string brushKey)
    {
        _status.Text = text;
        _status.IsVisible = true;
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(brushKey));
    }

    private static void OpenWhatsApp(SupportRequestRecord? record)
    {
        if (record is null || !PlatformLauncher.OpenUrl(SupportRequestService.WhatsAppUrl(record)))
            PosLogger.Log("Поддержка: WhatsApp не открылся.", "WARNING");
    }

    private void RenderHistory()
    {
        _history.Children.Clear();
        var items = SupportRequestJournal.Load().Take(HistoryRows).ToList();
        if (items.Count == 0)
        {
            _history.Children.Add(Soft(T("Обращений пока нет.", "Азырынча кайрылуулар жок.", "No requests yet.", "Henüz talep yok.", "Hozircha murojaatlar yo'q.")));
            return;
        }
        foreach (var item in items)
            _history.Children.Add(BuildRow(item));
    }

    private Control BuildRow(SupportRequestRecord item)
    {
        var (label, brush, soft) = item.Status switch
        {
            SupportRequestRecord.StatusSent => (T("Отправлено", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi"), "BrushSuccess", "BrushSuccessSoft"),
            SupportRequestRecord.StatusWhatsApp or SupportRequestRecord.StatusNotConfigured => (T("Отправлено через WhatsApp", "WhatsApp аркылуу жөнөтүлдү", "Sent via WhatsApp", "WhatsApp ile gönderildi", "WhatsApp orqali yuborildi"), "BrushSuccess", "BrushSuccessSoft"),
            _ => (T("Не отправлено", "Жөнөтүлгөн жок", "Not sent", "Gönderilmedi", "Yuborilmadi"), "BrushDanger", "BrushDangerSoft"),
        };
        var description = item.Description.Replace('\n', ' ').Replace('\r', ' ');
        if (description.Length > 80)
            description = description[..80] + "…";
        var title = new TextBlock { Text = $"{item.CreatedAt:dd.MM HH:mm} · № {item.Id}", FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        var body = new TextBlock { Text = description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        body.Bind(TextBlock.ForegroundProperty, body.GetResourceObservable("BrushTextSoft"));
        var badgeText = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        badgeText.Bind(TextBlock.ForegroundProperty, badgeText.GetResourceObservable(brush));
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3), Child = badgeText, VerticalAlignment = VerticalAlignment.Center };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable(soft));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(badge);
        var whatsApp = SupportRequestService.IsWhatsApp(item);
        if (item.Status != SupportRequestRecord.StatusSent && !whatsApp)
        {
            var retry = new Button
            {
                Content = T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"), Classes = { "nur-ghost" }, MinHeight = 44,
            };
            retry.Click += async (_, _) => await RetryAsync(item).ConfigureAwait(true);
            right.Children.Add(retry);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new StackPanel { Spacing = 2, Children = { title, body } };
        if (whatsApp)
        {
            // 2026-11-20: для WhatsApp-статуса вместо «Повторить» — «Открыть WhatsApp ещё раз» (под текстом, чтобы строка не распирала окно).
            var again = new Button
            {
                Content = T("Открыть WhatsApp ещё раз", "WhatsApp'ты кайра ачуу", "Open WhatsApp again", "WhatsApp'ı tekrar aç", "WhatsApp'ni yana ochish"),
                Classes = { "nur-ghost" }, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left,
            };
            again.Click += (_, _) => OpenWhatsApp(item);
            left.Children.Add(again);
        }
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), Child = grid };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanelSoft"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
        return card;
    }

    /// <summary>Повторная отправка той же записи (тот же номер); журнал и снимок при повторе — свежий журнал, без снимка (старого снимка уже нет).</summary>
    private async Task RetryAsync(SupportRequestRecord item)
    {
        if (!_shell.TryBeginBusy())
            return;
        try
        {
            ShowStatus(T("Отправляю…", "Жөнөтүп жатам…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…"), "BrushTextSoft");
            var withLog = item.LogLines > 0;
            var record = await Task.Run(() => SupportRequestService.SubmitAsync(item.Description, withLog, null, item.Source, item.Branch, item)).ConfigureAwait(true);
            ShowResult(record);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: повтор обращения не удался ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            _shell.EndBusy();
            RenderHistory();
        }
    }
}
