using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using NurMarketKassa.AvaloniaHost.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец (снимок витрины NBS): «где редактор сайта??». Раздел программы владельца «Редактор сайта»:
/// весь вид онлайн-витрины одним документом сервера NurCRM (ТЗ-BE-2026-05, п. 5–6): готовые стили, цвета, шрифт и форма,
/// шапка, главный блок, порядок разделов, категории, товары, карточка товара, корзина и заказ, подвал, SEO.
/// Правки уходят в черновик (PATCH showcase/design/draft/ через 0,8 с после изменения), справа — предпросмотр черновика
/// (preview-link), покупатели видят изменения только после «Опубликовать». Есть «Отменить изменения» и история версий.
/// Сервер пускает в редактор только компании с услугой «Онлайн витрина» (иначе 403 feature_disabled — ТЗ часть 12, п. 2.8):
/// тогда раздел показывает нынешний вид витрины (публичный адрес) только для просмотра и говорит, почему сохранять нельзя.
/// 2026-10-05, владелец: «создай несколько тем готовых для сайта как темы и редактор оставь» — сверху галерея «Темы сайта»
/// (Assets/SiteThemes: набор настроек, обложка главного блока, заглушка товара без фото, превью). Тема меняет весь вид, но не
/// тексты, телефон и товары магазина; после неё всё можно подправить ниже и опубликовать.</summary>
public sealed class SiteEditorWindow : Window, IOwnerSection
{
    private const string DesignPath = "api/main/showcase/design/";

    private readonly NurMarketApiClient _api;
    private readonly Grid _root = new() { Margin = new Thickness(24, 16, 24, 20), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _banner = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly TextBlock _bannerText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _editor = new() { Spacing = 10, Margin = new Thickness(0, 0, 12, 12) };
    private readonly ScrollViewer _editorScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _previewBox = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private Button? _publish;
    private Button? _discard;
    private Button? _versionsButton;
    private WebView2Host? _web;
    private CoreWebView2? _core;
    private string? _navigated;

    private JsonObject _draft = new();
    private JsonObject _patch = new();
    private string _lang = "ru";
    private string? _slug;
    private bool _locked;
    private bool _saving;
    private bool _hasUnpublished;
    private int _version;
    private string _publishedAt = "";
    private JsonElement _options;
    private bool _versionsOpen;

    // 2026-10-05, журнал владельца: сразу после открытия раздела и после каждой темы уходил «черновик сохранён (header, hero, …)»
    // без единой правки — поля при построении присылают «изменение» (TextBox — отложенно, ползунок ещё и округляет значение).
    // Правки принимаются только когда редактор построен (_editorReady) и только если значение правда другое.
    private bool _editorReady;

    /// <summary>Готовая тема витрины из Assets/SiteThemes/{code}.json.</summary>
    private sealed record SiteTheme(string Code, string Name, string Description, JsonObject Patch, List<(string Path, string File, string Kind)> Images);

    private static List<SiteTheme>? _themes;

    /// <summary>Картинки темы, уже загруженные на сервер в этом сеансе (тема → адрес поля → номер картинки):
    /// повторное применение той же темы не плодит копии в медиатеке.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> UploadedThemeImages = new();

    /// <summary>Названия цветов по адресу поля — для предупреждений сервера («theme.colors.text» → «Текст»).</summary>
    private readonly Dictionary<string, string> _labels = new();

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public SiteEditorWindow()
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Редактор сайта", "Сайттын редактору", "Website editor", "Web sitesi düzenleyici", "Sayt muharriri");
        Width = 1280;
        Height = 860;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        // Шапка: название, состояние черновика и кнопки.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var titles = new StackPanel { Spacing = 2 };
        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        titles.Children.Add(_title);
        titles.Children.Add(_status);
        head.Children.Add(titles);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var open = UiKit.Ghost(this, T("Открыть сайт", "Сайтты ачуу", "Open website", "Siteyi aç", "Saytni ochish"));
        open.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_slug))
                SiteOrdersWindow.OpenUrl(ShowcaseApiService.CatalogUrl(_slug));
        };
        _versionsButton = UiKit.Ghost(this, T("История версий", "Версиялар тарыхы", "Version history", "Sürüm geçmişi", "Versiyalar tarixi"));
        _versionsButton.Click += async (_, _) => await ToggleVersionsAsync().ConfigureAwait(true);
        _discard = UiKit.Ghost(this, T("Отменить изменения", "Өзгөртүүлөрдү жокко чыгаруу", "Discard changes", "Değişiklikleri geri al", "O'zgarishlarni bekor qilish"));
        _discard.Click += async (_, _) => await DiscardAsync().ConfigureAwait(true);
        _publish = UiKit.Primary(this, T("Опубликовать", "Жарыялоо", "Publish", "Yayınla", "E'lon qilish"));
        _publish.Click += async (_, _) => await PublishAsync().ConfigureAwait(true);
        foreach (var b in new[] { open, _versionsButton, _discard, _publish })
        {
            b.Margin = new Thickness(8, 4, 0, 4);
            buttons.Children.Add(b);
        }
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        _root.Children.Add(head);

        Use(_banner, Border.BackgroundProperty, "BrushPanel");
        Use(_banner, Border.BorderBrushProperty, "BrushWarning");
        Use(_bannerText, TextBlock.ForegroundProperty, "BrushText");
        _banner.Child = _bannerText;
        Grid.SetRow(_banner, 1);
        _root.Children.Add(_banner);

        // Слева настройки, справа предпросмотр.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("440,16,*") };
        _editorScroll.Content = _editor;
        body.Children.Add(_editorScroll);
        Use(_previewBox, Border.BorderBrushProperty, "BrushBorder");
        Use(_previewBox, Border.BackgroundProperty, "BrushPanel");
        Grid.SetColumn(_previewBox, 2);
        body.Children.Add(_previewBox);
        Grid.SetRow(body, 2);
        _root.Children.Add(body);
        Content = _root;

        if (OperatingSystem.IsWindows())
        {
            _web = new WebView2Host();
            _web.WebViewReady += core => _core = core;
            _web.InitializationFailed += message => Dispatcher.UIThread.Post(() => _previewBox.Child = PreviewNote(
                T("Предпросмотр не открылся: ", "Алдын ала көрүү ачылган жок: ", "The preview didn't open: ", "Önizleme açılmadı: ", "Oldindan ko'rish ochilmadi: ") + message));
            _previewBox.Child = _web;
        }
        else
        {
            _previewBox.Child = PreviewNote(T("Как выглядит сайт — кнопка «Открыть сайт» сверху.", "Сайт кандай көрүнөт — жогорудагы «Сайтты ачуу» баскычы.",
                "To see the website, use “Open website” at the top.", "Sitenin görünümü için üstteki «Siteyi aç» düğmesi.", "Sayt ko'rinishi — yuqoridagi «Saytni ochish» tugmasi."));
        }

        // Телефон и узкое окно: предпросмотр не помещается — только настройки на всю ширину.
        NarrowLayout.Attach(this, 980, narrow =>
        {
            body.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,0,0" : "440,16,*");
            _previewBox.IsVisible = !narrow;
        });

        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveDraftAsync().ConfigureAwait(true);
        };
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closed += (_, _) => _saveTimer.Stop();
    }

    public void AsOwnerSection()
    {
        _title.IsVisible = false;
        _root.Margin = OwnerSectionLayout.Margin;
    }

    private Control PreviewNote(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    // ───────────────────────────────────────────── загрузка, черновик, публикация

    private async Task LoadAsync()
    {
        _status.Text = T("Загружаю вид сайта…", "Сайттын көрүнүшү жүктөлүүдө…", "Loading the website design…", "Site tasarımı yükleniyor…", "Sayt ko'rinishi yuklanmoqda…");
        try
        {
            _slug = (await App.GetRequiredService<ShowcaseApiService>().GetSettingsAsync().ConfigureAwait(true)).Slug;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор сайта: адрес витрины не получен ({ex.Message}).", "WARNING");
        }
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, DesignPath, null, null, CancellationToken.None, TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            _draft = ParseObject(data, "draft") ?? new JsonObject();
            _hasUnpublished = data.TryGetProperty("has_unpublished_changes", out var hu) && hu.ValueKind == JsonValueKind.True;
            _version = data.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
            _publishedAt = data.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() ?? "" : "";
            _locked = false;
            _banner.IsVisible = false;
            try
            {
                _options = await _api.RequestAsync(HttpMethod.Get, "api/main/showcase/editor/options/", null, null, CancellationToken.None, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Редактор сайта: справочники не получены ({ex.Message}).", "WARNING");
            }
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            // Услуга «Онлайн витрина» не подключена (или сервер старый) — показываем нынешний вид только для просмотра.
            _locked = true;
            var reason = ex.Payload is { } p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("detail", out var d) ? d.GetString() : ex.Message;
            _bannerText.Text = T("Сохранять изменения пока нельзя — сервер NurCRM ответил: «", "Өзгөртүүлөрдү азырынча сактоого болбойт — NurCRM сервери жооп берди: «",
                                 "Changes can't be saved yet — the NurCRM server replied: “", "Değişiklikler henüz kaydedilemiyor — NurCRM sunucusu yanıt verdi: «",
                                 "O'zgarishlarni hozircha saqlab bo'lmaydi — NurCRM serveri javob berdi: «")
                               + reason
                               + T("». Ниже — нынешний вид сайта: какие настройки будут доступны, когда услугу «Онлайн витрина» подключат для вашей компании в NurCRM.",
                                   "». Төмөндө — сайттын азыркы көрүнүшү: NurCRMде компанияңызга «Онлайн витрина» кызматы кошулганда кайсы жөндөөлөр жеткиликтүү болот.",
                                   "”. Below is the current design: these settings become available once the “Online showcase” service is enabled for your company in NurCRM.",
                                   "». Aşağıda sitenin şu anki görünümü: NurCRM'de şirketiniz için «Online vitrin» hizmeti açıldığında bu ayarlar kullanılabilir olacak.",
                                   "». Quyida saytning hozirgi ko'rinishi: NurCRMda kompaniyangiz uchun «Onlayn vitrina» xizmati ulanganda bu sozlamalar ishlaydi.");
            _banner.IsVisible = true;
            PosLogger.Log($"Редактор сайта: сервер не пускает в редактор ({ex.StatusCode}: {reason}).", "WARNING");
            _draft = await LoadPublicDesignAsync().ConfigureAwait(true) ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _status.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Редактор сайта: вид не загружен ({ex.Message}).", "WARNING");
            return;
        }
        _lang = (Get("languages.default") as JsonValue)?.ToString() is { Length: > 0 } lang ? lang : "ru";
        _patch = new JsonObject();
        BuildEditor();
        RefreshState();
        await RefreshPreviewAsync().ConfigureAwait(true);
    }

    private async Task<JsonObject?> LoadPublicDesignAsync()
    {
        if (string.IsNullOrWhiteSpace(_slug))
            return null;
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/public/companies/{Uri.EscapeDataString(_slug)}/showcase/design/", null, null,
                CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            return JsonNode.Parse(data.GetRawText()) as JsonObject;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор сайта: публичный вид не получен ({ex.Message}).", "WARNING");
            return null;
        }
    }

    private static JsonObject? ParseObject(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(part.GetRawText()) as JsonObject
            : null;

    private void RefreshState()
    {
        var saving = _saving || _saveTimer.IsEnabled;
        if (_locked)
            _status.Text = T("Только просмотр", "Көрүү гана", "View only", "Yalnızca görüntüleme", "Faqat ko'rish");
        else if (saving)
            _status.Text = T("Сохраняю черновик…", "Долбоор сакталууда…", "Saving the draft…", "Taslak kaydediliyor…", "Qoralama saqlanmoqda…");
        else if (_hasUnpublished)
            _status.Text = T("Есть неопубликованные изменения — покупатели их пока не видят. Нажмите «Опубликовать».",
                "Жарыялана элек өзгөртүүлөр бар — кардарлар аларды азырынча көрбөйт. «Жарыялоо» басыңыз.",
                "There are unpublished changes — customers don't see them yet. Press “Publish”.",
                "Yayınlanmamış değişiklikler var — müşteriler henüz görmüyor. «Yayınla»ya basın.",
                "E'lon qilinmagan o'zgarishlar bor — xaridorlar ularni hali ko'rmaydi. «E'lon qilish»ni bosing.");
        else
            _status.Text = T($"Всё опубликовано · версия {_version}", $"Баары жарыяланды · версия {_version}", $"Everything is published · version {_version}",
                $"Her şey yayında · sürüm {_version}", $"Hammasi e'lon qilingan · versiya {_version}")
                + (DateTimeOffset.TryParse(_publishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? $" · {at.LocalDateTime:dd.MM.yyyy HH:mm}" : "");
        if (_publish is not null)
            _publish.IsEnabled = !_locked && !saving && _hasUnpublished;
        if (_discard is not null)
            _discard.IsEnabled = !_locked && !saving && _hasUnpublished;
        if (_versionsButton is not null)
            _versionsButton.IsEnabled = !_locked;
    }

    /// <summary>Только просмотр: группы раскрываются, а поля — нет (сохранить всё равно нельзя).</summary>
    private void LockInputs()
    {
        if (!_locked)
            return;
        foreach (var control in _editor.GetLogicalDescendants().OfType<Control>())
            if (control is Button { Tag: "swatch" } swatch)
                swatch.Flyout = null; // цвет виден, а палитра не открывается
            else if (control is TextBox or CheckBox or Avalonia.Controls.Slider || (control is Button b && b.Tag as string != "head"))
                control.IsEnabled = false;
    }

    /// <summary>Правка поля: сразу в документ на экране и в очередь черновика; на сервер — через 0,8 с тишины.</summary>
    private void Set(string path, JsonNode? value)
    {
        if (_locked || !_editorReady || JsonNode.DeepEquals(Get(path), value))
            return;
        SetIn(_draft, path, value?.DeepClone());
        SetIn(_patch, path, value?.DeepClone());
        _saveTimer.Stop();
        _saveTimer.Start();
        RefreshState();
    }

    private static void SetIn(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var o = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (o[parts[i]] is not JsonObject child)
            {
                child = new JsonObject();
                o[parts[i]] = child;
            }
            o = child;
        }
        o[parts[^1]] = value;
    }

    private JsonNode? Get(string path)
    {
        JsonNode? n = _draft;
        foreach (var part in path.Split('.'))
        {
            if (n is JsonObject o && o.TryGetPropertyValue(part, out var v))
                n = v;
            else
                return null;
        }
        return n;
    }

    private string Str(string path)
    {
        var n = Get(path);
        if (n is JsonObject i18n)
            return i18n[_lang]?.ToString() ?? i18n.FirstOrDefault().Value?.ToString() ?? "";
        return n is JsonValue v ? v.ToString() : "";
    }

    private double? Number(string path) =>
        Get(path) is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private bool Flag(string path) => Get(path) is JsonValue v && v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

    private async Task SaveDraftAsync()
    {
        if (_locked || _saving || _patch.Count == 0)
            return;
        var patch = _patch;
        _patch = new JsonObject();
        _saving = true;
        RefreshState();
        try
        {
            var data = await _api.RequestAsync(new HttpMethod("PATCH"), DesignPath + "draft/", patch, null, CancellationToken.None, TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            _hasUnpublished = data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("has_unpublished_changes", out var hu) || hu.ValueKind != JsonValueKind.False;
            ShowWarnings(data);
            PosLogger.Log($"Редактор сайта: черновик сохранён ({string.Join(", ", patch.Select(p => p.Key))}).", "INFO");
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Не сохранилось — правки возвращаются в очередь, следующая попытка — со следующей правкой или «Опубликовать».
            foreach (var (key, value) in patch.ToList())
            {
                if (!_patch.ContainsKey(key))
                {
                    patch.Remove(key);
                    _patch[key] = value;
                }
            }
            ShowBanner(T("Черновик не сохранился: ", "Долбоор сакталган жок: ", "The draft wasn't saved: ", "Taslak kaydedilmedi: ", "Qoralama saqlanmadi: ")
                       + ServerTelegramBotApi.DescribeFields(ex), warning: true);
            PosLogger.Log($"Редактор сайта: черновик не сохранён ({ex.Message}).", "WARNING");
        }
        finally
        {
            _saving = false;
            RefreshState();
        }
    }

    private void ShowWarnings(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array || warnings.GetArrayLength() == 0)
        {
            if (!_locked)
                _banner.IsVisible = false;
            return;
        }
        var lines = new List<string>();
        foreach (var w in warnings.EnumerateArray())
        {
            var fieldPath = w.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "";
            var againstPath = w.TryGetProperty("against", out var ag) ? ag.GetString() ?? "" : "";
            var field = _labels.TryGetValue(fieldPath, out var fl) ? fl : fieldPath;
            var against = _labels.TryGetValue(againstPath, out var al) ? al : againstPath;
            var code = w.TryGetProperty("code", out var c) ? c.GetString() : "";
            var ratio = w.TryGetProperty("ratio", out var r) && r.TryGetDouble(out var rv) ? rv : 0;
            var min = w.TryGetProperty("min_ratio", out var mr) && mr.TryGetDouble(out var mv) ? mv : 4.5;
            lines.Add(code == "low_contrast"
                ? T($"«{field}» плохо читается на «{against}» (контраст {ratio:0.0} : 1, нужно от {min:0.0}). Сохранено, но покупателям будет трудно читать.",
                    $"«{field}» «{against}» үстүндө начар окулат (контраст {ratio:0.0} : 1, {min:0.0}дөн кем эмес керек). Сакталды, бирок кардарларга окуу кыйын болот.",
                    $"“{field}” is hard to read on “{against}” (contrast {ratio:0.0}:1, {min:0.0} needed). Saved, but customers will struggle to read it.",
                    $"«{field}», «{against}» üzerinde zor okunuyor (kontrast {ratio:0.0}:1, en az {min:0.0}). Kaydedildi ama müşteriler zor okur.",
                    $"«{field}» «{against}» ustida yomon o'qiladi (kontrast {ratio:0.0} : 1, kamida {min:0.0} kerak). Saqlandi, lekin xaridorlarga o'qish qiyin bo'ladi.")
                : $"«{field}»: {code}");
        }
        ShowBanner(string.Join("\n", lines), warning: true);
    }

    private void ShowBanner(string text, bool warning)
    {
        _bannerText.Text = text;
        Use(_banner, Border.BorderBrushProperty, warning ? "BrushWarning" : "BrushSuccess");
        _banner.IsVisible = true;
    }

    private async Task PublishAsync()
    {
        if (_locked)
            return;
        _saveTimer.Stop();
        await SaveDraftAsync().ConfigureAwait(true);
        if (_patch.Count > 0)
            return;
        _publish!.IsEnabled = false;
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Post, DesignPath + "publish/", new Dictionary<string, object?>(), null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("version", out var v) && v.TryGetInt32(out var n))
                _version = n;
            _publishedAt = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : DateTimeOffset.Now.ToString("o");
            _hasUnpublished = false;
            ShowBanner(T("Опубликовано — покупатели уже видят новый вид сайта.", "Жарыяланды — кардарлар сайттын жаңы көрүнүшүн көрүп жатышат.",
                "Published — customers already see the new design.", "Yayınlandı — müşteriler yeni görünümü görüyor.", "E'lon qilindi — xaridorlar saytning yangi ko'rinishini ko'rmoqda."), warning: false);
            PosLogger.Log($"Редактор сайта: опубликована версия {_version}.", "INFO");
        }
        catch (Exception ex)
        {
            ShowBanner(T("Не опубликовано: ", "Жарыяланган жок: ", "Not published: ", "Yayınlanmadı: ", "E'lon qilinmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
            PosLogger.Log($"Редактор сайта: публикация не прошла ({ex.Message}).", "WARNING");
        }
        RefreshState();
    }

    private async Task DiscardAsync()
    {
        if (_locked)
            return;
        var ok = await ConfirmAsync(T("Отменить все неопубликованные изменения? Сайт останется таким, как сейчас у покупателей.",
            "Жарыялана элек бардык өзгөртүүлөрдү жокко чыгарасызбы? Сайт кардарлардагыдай калат.",
            "Discard all unpublished changes? The website stays as customers see it now.",
            "Yayınlanmamış tüm değişiklikler geri alınsın mı? Site müşterilerin şu an gördüğü gibi kalır.",
            "E'lon qilinmagan barcha o'zgarishlar bekor qilinsinmi? Sayt xaridorlar hozir ko'rgandek qoladi.")).ConfigureAwait(true);
        if (!ok)
            return;
        _saveTimer.Stop();
        _patch = new JsonObject();
        try
        {
            await _api.RequestAsync(HttpMethod.Post, DesignPath + "discard/", new Dictionary<string, object?>(), null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            PosLogger.Log("Редактор сайта: изменения черновика отменены.", "INFO");
        }
        catch (Exception ex)
        {
            ShowBanner(T("Не отменилось: ", "Жокко чыгарылган жок: ", "Not discarded: ", "Geri alınamadı: ", "Bekor qilinmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
        }
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task ApplyPresetAsync(string code)
    {
        if (_locked)
            return;
        _saveTimer.Stop();
        await SaveDraftAsync().ConfigureAwait(true);
        try
        {
            await _api.RequestAsync(HttpMethod.Post, DesignPath + "draft/apply-preset/", new Dictionary<string, object?> { ["preset"] = code }, null,
                CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            PosLogger.Log($"Редактор сайта: применён стиль {code}.", "INFO");
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowBanner(T("Стиль не применился: ", "Стиль колдонулган жок: ", "The style wasn't applied: ", "Stil uygulanmadı: ", "Uslub qo'llanmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
        }
    }

    /// <summary>Предпросмотр: черновик по ссылке сервера (preview-link), без неё — опубликованный сайт.</summary>
    private async Task RefreshPreviewAsync()
    {
        if (_web is null)
            return;
        string? url = null;
        if (!_locked)
        {
            try
            {
                var data = await _api.RequestAsync(HttpMethod.Post, DesignPath + "preview-link/", new Dictionary<string, object?>(), null,
                    CancellationToken.None, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                url = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("url", out var u) ? u.GetString() : null;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Редактор сайта: ссылка предпросмотра не получена ({ex.Message}).", "WARNING");
            }
        }
        url ??= string.IsNullOrWhiteSpace(_slug) ? null : ShowcaseApiService.CatalogUrl(_slug);
        if (url is null)
            return;
        if (url == _navigated && _core is not null)
            _core.Reload();
        else
            _web.Navigate(url);
        _navigated = url;
    }

    private async Task ToggleVersionsAsync()
    {
        _versionsOpen = !_versionsOpen;
        BuildEditor();
        if (!_versionsOpen)
            return;
        var host = new StackPanel { Spacing = 6 };
        var card = Card(T("История версий", "Версиялар тарыхы", "Version history", "Sürüm geçmişi", "Versiyalar tarixi"), host, open: true);
        _editor.Children.Insert(0, card);
        host.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, DesignPath + "versions/", null, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            host.Children.Clear();
            var rows = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList()
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r.EnumerateArray().ToList() : new List<JsonElement>();
            if (rows.Count == 0)
                host.Children.Add(Hint(T("Опубликованных версий пока нет.", "Жарыяланган версиялар азырынча жок.", "No published versions yet.", "Henüz yayınlanmış sürüm yok.", "Hali e'lon qilingan versiyalar yo'q.")));
            foreach (var row in rows.Take(30))
            {
                var n = row.TryGetProperty("version", out var vv) && vv.TryGetInt32(out var vn) ? vn : 0;
                var when = row.TryGetProperty("published_at", out var pa) && DateTimeOffset.TryParse(pa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at.LocalDateTime.ToString("dd.MM.yyyy HH:mm") : "";
                var author = row.TryGetProperty("author", out var au) ? au.ToString() : "";
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                line.Children.Add(Label(T($"Версия {n}", $"{n}-версия", $"Version {n}", $"Sürüm {n}", $"{n}-versiya") + $" · {when}" + (author.Length > 0 ? " · " + author : "")));
                var restore = UiKit.Ghost(this, T("Вернуть в черновик", "Долбоорго кайтаруу", "Restore to draft", "Taslağa geri al", "Qoralamaga qaytarish"));
                restore.Height = 32;
                restore.Click += async (_, _) =>
                {
                    try
                    {
                        await _api.RequestAsync(HttpMethod.Post, DesignPath + $"versions/{n}/restore/", new Dictionary<string, object?>(), null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                        _versionsOpen = false;
                        PosLogger.Log($"Редактор сайта: версия {n} возвращена в черновик.", "INFO");
                        await LoadAsync().ConfigureAwait(true);
                        ShowBanner(T($"Версия {n} — в черновике. Проверьте и нажмите «Опубликовать».", $"{n}-версия долбоордо. Текшерип, «Жарыялоо» басыңыз.",
                            $"Version {n} is in the draft. Check it and press “Publish”.", $"Sürüm {n} taslakta. Kontrol edip «Yayınla»ya basın.",
                            $"{n}-versiya qoralamada. Tekshirib, «E'lon qilish»ni bosing."), warning: false);
                    }
                    catch (Exception ex)
                    {
                        ShowBanner(T("Не вернулось: ", "Кайтарылган жок: ", "Not restored: ", "Geri alınamadı: ", "Qaytarilmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
                    }
                };
                Grid.SetColumn(restore, 1);
                line.Children.Add(restore);
                host.Children.Add(line);
            }
        }
        catch (Exception ex)
        {
            host.Children.Clear();
            host.Children.Add(Hint(T("Не загрузилось: ", "Жүктөлгөн жок: ", "Didn't load: ", "Yüklenmedi: ", "Yuklanmadi: ") + ex.Message));
        }
    }

    private async Task<bool> ConfirmAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Title = Title,
        };
        Use(dialog, BackgroundProperty, "BrushWindowBackdrop");
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        Use(label, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(label);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = UiKit.Primary(this, T("Да", "Ооба", "Yes", "Evet", "Ha"));
        var no = UiKit.Ghost(this, T("Нет", "Жок", "No", "Hayır", "Yo'q"));
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        row.Children.Add(no);
        row.Children.Add(yes);
        panel.Children.Add(row);
        dialog.Content = panel;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    // ───────────────────────────────────────────── настройки

    private void BuildEditor()
    {
        _editorReady = false;
        _editor.Children.Clear();
        Dispatcher.UIThread.Post(() => _editorReady = true, DispatcherPriority.Background);

        // Готовые темы — весь вид сайта одним нажатием.
        var themes = Group(T("Темы сайта", "Сайттын темалары", "Website themes", "Site temaları", "Sayt mavzulari"), open: true);
        BuildThemes(themes);

        // Готовые цвета сервера и режим.
        var style = Group(T("Готовые цвета", "Даяр түстөр", "Ready-made colours", "Hazır renkler", "Tayyor ranglar"));
        style.Children.Add(Hint(T("Один щелчок — новые цвета, шрифт и форма. Остальные настройки не меняются.", "Бир басуу — жаңы түстөр, шрифт жана форма. Башка жөндөөлөр өзгөрбөйт.",
            "One click — new colours, font and shapes. Other settings stay the same.", "Tek tıkla yeni renkler, yazı tipi ve şekiller. Diğer ayarlar değişmez.",
            "Bir bosish — yangi ranglar, shrift va shakl. Boshqa sozlamalar o'zgarmaydi.")));
        var presets = new WrapPanel();
        foreach (var (code, name) in Presets())
        {
            var chip = Chip(name, Str("theme.preset") == code);
            chip.Click += async (_, _) => await ApplyPresetAsync(code).ConfigureAwait(true);
            presets.Children.Add(chip);
        }
        style.Children.Add(presets);
        Choice(style, T("Режим", "Режим", "Mode", "Mod", "Rejim"), "theme.mode", new[]
        {
            ("light", T("Светлый", "Жарык", "Light", "Açık", "Yorug'")), ("dark", T("Тёмный", "Караңгы", "Dark", "Koyu", "Qorong'i")),
            ("auto", T("Как в телефоне покупателя", "Кардардын телефонундагыдай", "Follow the customer's phone", "Müşterinin telefonuna göre", "Xaridor telefonidagidek")),
        });

        var colors = Group(T("Цвета", "Түстөр", "Colours", "Renkler", "Ranglar"));
        foreach (var (key, label) in new[]
                 {
                     ("accent", T("Главный цвет — кнопки, выделение", "Негизги түс — баскычтар", "Main colour — buttons, highlights", "Ana renk — düğmeler", "Asosiy rang — tugmalar")),
                     ("accent_text", T("Текст на кнопках", "Баскычтагы текст", "Text on buttons", "Düğme metni", "Tugmadagi matn")),
                     ("background", T("Фон страницы", "Барактын фону", "Page background", "Sayfa arka planı", "Sahifa foni")),
                     ("surface", T("Фон карточек", "Карточкалардын фону", "Card background", "Kart arka planı", "Kartochkalar foni")),
                     ("text", T("Текст", "Текст", "Text", "Metin", "Matn")),
                     ("text_muted", T("Второстепенный текст", "Кошумча текст", "Secondary text", "İkincil metin", "Ikkinchi darajali matn")),
                     ("border", T("Рамки", "Алкактар", "Borders", "Kenarlıklar", "Ramkalar")),
                     ("header_bg", T("Шапка — фон", "Баш бөлүк — фон", "Header — background", "Üst bölüm — arka plan", "Sarlavha — fon")),
                     ("header_text", T("Шапка — текст", "Баш бөлүк — текст", "Header — text", "Üst bölüm — metin", "Sarlavha — matn")),
                     ("price", T("Цена", "Баа", "Price", "Fiyat", "Narx")),
                     ("old_price", T("Старая цена", "Эски баа", "Old price", "Eski fiyat", "Eski narx")),
                     ("badge_new_bg", T("Значок «Новинка»", "«Жаңылык» белгиси", "“New” badge", "«Yeni» rozeti", "«Yangi» belgisi")),
                     ("badge_sale_bg", T("Значок скидки", "Арзандатуу белгиси", "Discount badge", "İndirim rozeti", "Chegirma belgisi")),
                     ("footer_bg", T("Подвал — фон", "Төмөнкү бөлүк — фон", "Footer — background", "Alt bilgi — arka plan", "Pastki qism — fon")),
                     ("footer_text", T("Подвал — текст", "Төмөнкү бөлүк — текст", "Footer — text", "Alt bilgi — metin", "Pastki qism — matn")),
                 })
            ColorField(colors, label, "theme.colors." + key);

        var shape = Group(T("Шрифт и форма", "Шрифт жана форма", "Font and shape", "Yazı tipi ve şekil", "Shrift va shakl"));
        Choice(shape, T("Шрифт", "Шрифт", "Font", "Yazı tipi", "Shrift"), "theme.font.family", Fonts().Select(f => (f, f)).ToArray());
        Slider(shape, T("Размер текста", "Тексттин өлчөмү", "Text size", "Metin boyutu", "Matn o'lchami"), "theme.font.base_size", 13, 18, 1);
        Slider(shape, T("Скругление углов", "Бурчтардын тегеректиги", "Corner rounding", "Köşe yuvarlaklığı", "Burchaklar yumaloqligi"), "theme.radius", 0, 28, 1);
        Choice(shape, T("Тень", "Көлөкө", "Shadow", "Gölge", "Soya"), "theme.shadow", new[]
        {
            ("none", T("Нет", "Жок", "None", "Yok", "Yo'q")), ("soft", T("Мягкая", "Жумшак", "Soft", "Yumuşak", "Yumshoq")), ("strong", T("Заметная", "Байкалаарлык", "Strong", "Belirgin", "Kuchli")),
        });
        Choice(shape, T("Кнопки", "Баскычтар", "Buttons", "Düğmeler", "Tugmalar"), "theme.button_style", new[]
        {
            ("filled", T("Залитые", "Толтурулган", "Filled", "Dolu", "To'ldirilgan")), ("outline", T("Контур", "Контур", "Outline", "Çerçeve", "Kontur")),
            ("soft", T("Мягкие", "Жумшак", "Soft", "Yumuşak", "Yumshoq")),
        });

        var header = Group(T("Шапка сайта", "Сайттын баш бөлүгү", "Website header", "Site üst bölümü", "Sayt sarlavhasi"));
        ImageField(header, T("Логотип", "Логотип", "Logo", "Logo", "Logotip"), "header.logo", "logo");
        Slider(header, T("Высота логотипа", "Логотиптин бийиктиги", "Logo height", "Logo yüksekliği", "Logotip balandligi"), "header.logo_height", 24, 80, 2);
        Toggle(header, T("Показывать название магазина", "Дүкөндүн атын көрсөтүү", "Show the shop name", "Mağaza adını göster", "Do'kon nomini ko'rsatish"), "header.show_name");
        TextField(header, T("Название", "Аталышы", "Name", "Ad", "Nomi"), "header.name", i18n: true);
        TextField(header, T("Слоган под названием", "Аталыштын астындагы ураан", "Slogan under the name", "Ad altındaki slogan", "Nom ostidagi shior"), "header.slogan", i18n: true);
        Choice(header, T("Расположение", "Жайгашуусу", "Layout", "Yerleşim", "Joylashuv"), "header.layout", new[]
        {
            ("logo_left", T("Логотип слева", "Логотип солдо", "Logo on the left", "Logo solda", "Logotip chapda")),
            ("logo_center", T("Логотип по центру", "Логотип ортодо", "Logo in the centre", "Logo ortada", "Logotip markazda")),
        });
        Toggle(header, T("Шапка остаётся сверху при прокрутке", "Жылдырганда баш бөлүк жогоруда калат", "Header stays on top while scrolling", "Kaydırırken üst bölüm sabit kalır", "Aylantirganda sarlavha yuqorida qoladi"), "header.sticky");
        Toggle(header, T("Поиск товаров", "Товар издөө", "Product search", "Ürün arama", "Mahsulot qidirish"), "header.show_search");
        TextField(header, T("Подсказка в поиске", "Издөөдөгү кеңеш", "Search placeholder", "Arama ipucu", "Qidiruvdagi maslahat"), "header.search_placeholder", i18n: true);
        TextField(header, T("Надпись на кнопке корзины", "Себет баскычындагы жазуу", "Cart button text", "Sepet düğmesi metni", "Savat tugmasidagi yozuv"), "header.cart_button.text", i18n: true);
        Toggle(header, T("Полоса объявления над шапкой", "Баш бөлүктүн үстүндөгү кулактандыруу", "Announcement bar above the header", "Üst bölümün üstünde duyuru şeridi", "Sarlavha ustidagi e'lon chizig'i"), "header.announcement.enabled");
        TextField(header, T("Текст объявления", "Кулактандыруунун тексти", "Announcement text", "Duyuru metni", "E'lon matni"), "header.announcement.text", i18n: true);
        ColorField(header, T("Объявление — фон", "Кулактандыруу — фон", "Announcement — background", "Duyuru — arka plan", "E'lon — fon"), "header.announcement.bg");
        ColorField(header, T("Объявление — текст", "Кулактандыруу — текст", "Announcement — text", "Duyuru — metin", "E'lon — matn"), "header.announcement.text_color");

        var hero = Group(T("Главный блок", "Башкы блок", "Main block", "Ana blok", "Asosiy blok"));
        Choice(hero, T("Вид", "Түрү", "Style", "Görünüm", "Ko'rinish"), "hero.style", new[]
        {
            ("card", T("Карточка (как сейчас)", "Карточка (азыркыдай)", "Card (as now)", "Kart (şimdiki gibi)", "Kartochka (hozirgidek)")),
            ("cover", T("Картинка на всю ширину", "Толук туурасы сүрөт", "Full-width picture", "Tam genişlik resim", "To'liq kenglikdagi rasm")),
            ("split", T("Текст и картинка рядом", "Текст жана сүрөт катар", "Text and picture side by side", "Metin ve resim yan yana", "Matn va rasm yonma-yon")),
            ("hidden", T("Не показывать", "Көрсөтпөө", "Hide", "Gösterme", "Ko'rsatmaslik")),
        });
        TextField(hero, T("Заголовок", "Аталыш", "Title", "Başlık", "Sarlavha"), "hero.title", i18n: true);
        TextField(hero, T("Подзаголовок", "Кошумча аталыш", "Subtitle", "Alt başlık", "Kichik sarlavha"), "hero.subtitle", i18n: true);
        ImageField(hero, T("Картинка", "Сүрөт", "Picture", "Resim", "Rasm"), "hero.image", "cover");
        Slider(hero, T("Затемнение картинки", "Сүрөттү караңгылатуу", "Picture darkening", "Resim karartma", "Rasmni qoraytirish"), "hero.overlay", 0, 0.8, 0.05);
        Choice(hero, T("Выравнивание", "Тегиздөө", "Alignment", "Hizalama", "Tekislash"), "hero.align", new[]
        {
            ("left", T("Слева", "Солдо", "Left", "Sol", "Chapda")), ("center", T("По центру", "Ортодо", "Centre", "Orta", "Markazda")),
        });
        Toggle(hero, T("Кнопка в главном блоке", "Башкы блоктогу баскыч", "Button in the main block", "Ana blokta düğme", "Asosiy blokdagi tugma"), "hero.button.enabled");
        TextField(hero, T("Надпись на кнопке", "Баскычтагы жазуу", "Button text", "Düğme metni", "Tugmadagi yozuv"), "hero.button.text", i18n: true);

        var sections = Group(T("Разделы страницы и их порядок", "Барактын бөлүмдөрү жана тартиби", "Page sections and order", "Sayfa bölümleri ve sırası", "Sahifa bo'limlari va tartibi"));
        BuildSections(sections);

        var cats = Group(T("Категории", "Категориялар", "Categories", "Kategoriler", "Toifalar"));
        Choice(cats, T("Вид", "Түрү", "Style", "Görünüm", "Ko'rinish"), "categories.style", new[]
        {
            ("chips", T("Кнопки (как сейчас)", "Баскычтар (азыркыдай)", "Chips (as now)", "Düğmeler (şimdiki gibi)", "Tugmalar (hozirgidek)")),
            ("tiles", T("Плитки с картинками", "Сүрөттүү плиткалар", "Tiles with pictures", "Resimli kutular", "Rasmli plitkalar")),
            ("list", T("Список", "Тизме", "List", "Liste", "Ro'yxat")), ("hidden", T("Не показывать", "Көрсөтпөө", "Hide", "Gösterme", "Ko'rsatmaslik")),
        });
        TextField(cats, T("Заголовок", "Аталыш", "Title", "Başlık", "Sarlavha"), "categories.title", i18n: true);
        Toggle(cats, T("Кнопка «Все»", "«Баары» баскычы", "“All” button", "«Tümü» düğmesi", "«Hammasi» tugmasi"), "categories.show_all");
        Toggle(cats, T("Показывать число категорий", "Категориялардын санын көрсөтүү", "Show the number of categories", "Kategori sayısını göster", "Toifalar sonini ko'rsatish"), "categories.show_count");

        var products = Group(T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"));
        TextField(products, T("Заголовок", "Аталыш", "Title", "Başlık", "Sarlavha"), "products.title", i18n: true);
        Slider(products, T("Колонок на компьютере", "Компьютерде мамычалар", "Columns on a computer", "Bilgisayarda sütun", "Kompyuterda ustunlar"), "products.columns.desktop", 2, 6, 1);
        Slider(products, T("Колонок на планшете", "Планшетте мамычалар", "Columns on a tablet", "Tablette sütun", "Planshetda ustunlar"), "products.columns.tablet", 2, 4, 1);
        Slider(products, T("Колонок на телефоне", "Телефондо мамычалар", "Columns on a phone", "Telefonda sütun", "Telefonda ustunlar"), "products.columns.mobile", 1, 3, 1);
        Slider(products, T("Товаров на странице", "Бетте товарлар", "Products per page", "Sayfa başına ürün", "Sahifadagi mahsulotlar"), "products.page_size", 12, 120, 12);
        Choice(products, T("Листание", "Барактоо", "Paging", "Sayfalama", "Varaqlash"), "products.pagination", new[]
        {
            ("pages", T("Страницы", "Барактар", "Pages", "Sayfalar", "Sahifalar")), ("load_more", T("Кнопка «Показать ещё»", "«Дагы көрсөтүү» баскычы", "“Show more” button", "«Daha fazla» düğmesi", "«Yana ko'rsatish» tugmasi")),
            ("infinite", T("Подгружать при прокрутке", "Жылдырганда жүктөө", "Load on scroll", "Kaydırınca yükle", "Aylantirganda yuklash")),
        });
        Choice(products, T("Порядок по умолчанию", "Демейки тартип", "Default order", "Varsayılan sıra", "Standart tartib"), "products.default_sort", new[]
        {
            ("default", T("Как в кассе", "Кассадагыдай", "As in the till", "Kasadaki gibi", "Kassadagidek")), ("name_asc", T("По названию А→Я", "Аталышы А→Я", "Name A→Z", "Ada göre A→Z", "Nomi A→Z")),
            ("price_asc", T("Сначала дешёвые", "Адегенде арзандары", "Cheapest first", "Önce ucuzlar", "Avval arzonlari")), ("price_desc", T("Сначала дорогие", "Адегенде кымбаттары", "Most expensive first", "Önce pahalılar", "Avval qimmatlari")),
            ("discount_desc", T("Сначала со скидкой", "Адегенде арзандатуулар", "Discounts first", "Önce indirimliler", "Avval chegirmalilar")),
        });
        Toggle(products, T("Показывать число товаров", "Товарлардын санын көрсөтүү", "Show the number of products", "Ürün sayısını göster", "Mahsulotlar sonini ko'rsatish"), "products.show_count");
        Toggle(products, T("Скрывать товары, которых нет в наличии", "Жок товарларды жашыруу", "Hide out-of-stock products", "Stokta olmayanları gizle", "Mavjud bo'lmaganlarini yashirish"), "products.hide_out_of_stock");
        Toggle(products, T("Скрывать товары без цены", "Баасы жок товарларды жашыруу", "Hide products without a price", "Fiyatsız ürünleri gizle", "Narxsiz mahsulotlarni yashirish"), "products.hide_zero_price");

        var card = Group(T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Mahsulot kartochkasi"));
        Choice(card, T("Вид", "Түрү", "Style", "Görünüm", "Ko'rinish"), "card.template", new[]
        {
            ("compact", T("Компактная", "Компакттуу", "Compact", "Kompakt", "Ixcham")), ("standard", T("Обычная (как сейчас)", "Кадимки (азыркыдай)", "Standard (as now)", "Standart (şimdiki gibi)", "Oddiy (hozirgidek)")),
            ("large", T("Крупная", "Чоң", "Large", "Büyük", "Katta")), ("list", T("Строкой", "Сап менен", "Row", "Satır", "Qator")),
        });
        Choice(card, T("Форма фото", "Сүрөттүн формасы", "Photo shape", "Fotoğraf biçimi", "Rasm shakli"), "card.photo_ratio", new[] { ("1:1", "1:1"), ("4:3", "4:3"), ("3:4", "3:4"), ("16:9", "16:9") });
        Choice(card, T("Фото в карточке", "Карточкадагы сүрөт", "Photo in the card", "Karttaki fotoğraf", "Kartochkadagi rasm"), "card.photo_fit", new[]
        {
            ("contain", T("Целиком", "Толугу менен", "Whole photo", "Tamamı", "To'liq")), ("cover", T("На всю карточку", "Бүт карточкага", "Fill the card", "Kartı doldur", "Butun kartochkaga")),
        });
        Choice(card, T("Где цена", "Баа кайда", "Price position", "Fiyat konumu", "Narx qayerda"), "card.price_position", new[]
        {
            ("photo_corner", T("В углу фото", "Сүрөттүн бурчунда", "Photo corner", "Fotoğraf köşesinde", "Rasm burchagida")),
            ("under_name", T("Под названием", "Аталыштын астында", "Under the name", "Adın altında", "Nom ostida")),
        });
        TextField(card, T("Надпись, когда нет фото", "Сүрөт жок болгондогу жазуу", "Text when there is no photo", "Fotoğraf yokken metin", "Rasm bo'lmaganda yozuv"), "card.placeholder_text", i18n: true);
        foreach (var (key, label) in new[]
                 {
                     ("old_price", T("Старая цена", "Эски баа", "Old price", "Eski fiyat", "Eski narx")),
                     ("discount_badge", T("Значок скидки", "Арзандатуу белгиси", "Discount badge", "İndirim rozeti", "Chegirma belgisi")),
                     ("new_badge", T("Значок «Новинка»", "«Жаңылык» белгиси", "“New” badge", "«Yeni» rozeti", "«Yangi» belgisi")),
                     ("category", T("Категория", "Категория", "Category", "Kategori", "Toifa")),
                     ("unit", T("Единица (шт, кг)", "Бирдик (даана, кг)", "Unit (pcs, kg)", "Birim (adet, kg)", "Birlik (dona, kg)")),
                     ("description", T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif")),
                     ("add_button", T("Кнопка «В корзину»", "«Себетке» баскычы", "“Add to cart” button", "«Sepete ekle» düğmesi", "«Savatga» tugmasi")),
                     ("quantity_stepper", T("Выбор количества", "Санын тандоо", "Quantity selector", "Adet seçici", "Miqdor tanlash")),
                 })
            Toggle(card, T("Показывать: ", "Көрсөтүү: ", "Show: ", "Göster: ", "Ko'rsatish: ") + label, "card.show." + key);
        Choice(card, T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"), "card.show.stock", new[]
        {
            ("hidden", T("Не показывать", "Көрсөтпөө", "Hide", "Gösterme", "Ko'rsatmaslik")), ("low_only", T("Только когда мало", "Аз болгондо гана", "Only when low", "Yalnızca azken", "Faqat kam bo'lganda")),
            ("always", T("Всегда", "Дайыма", "Always", "Her zaman", "Doim")),
        });
        Slider(card, T("«Новинка» — сколько дней", "«Жаңылык» — канча күн", "“New” — how many days", "«Yeni» — kaç gün", "«Yangi» — necha kun"), "card.new_badge_days", 1, 60, 1);
        TextField(card, T("Текст значка «Новинка»", "«Жаңылык» белгисинин тексти", "“New” badge text", "«Yeni» rozeti metni", "«Yangi» belgisi matni"), "card.new_badge_text", i18n: true);
        TextField(card, T("Надпись на кнопке «В корзину»", "«Себетке» баскычындагы жазуу", "“Add to cart” button text", "«Sepete ekle» düğme metni", "«Savatga» tugmasidagi yozuv"), "card.add_button_text", i18n: true);
        Toggle(card, T("Тень у карточки", "Карточканын көлөкөсү", "Card shadow", "Kart gölgesi", "Kartochka soyasi"), "card.shadow");
        Toggle(card, T("Рамка у карточки", "Карточканын алкагы", "Card border", "Kart kenarlığı", "Kartochka ramkasi"), "card.border");

        var cart = Group(T("Корзина и заказ", "Себет жана заказ", "Cart and order", "Sepet ve sipariş", "Savat va buyurtma"));
        Choice(cart, T("Корзина", "Себет", "Cart", "Sepet", "Savat"), "cart.style", new[]
        {
            ("drawer", T("Панель сбоку (как сейчас)", "Капталдагы панель (азыркыдай)", "Side panel (as now)", "Yan panel (şimdiki gibi)", "Yon panel (hozirgidek)")),
            ("page", T("Отдельная страница", "Өзүнчө барак", "Separate page", "Ayrı sayfa", "Alohida sahifa")),
        });
        var fieldModes = new[]
        {
            ("required", T("Обязательно", "Милдеттүү", "Required", "Zorunlu", "Majburiy")), ("optional", T("По желанию", "Каалоосу боюнча", "Optional", "İsteğe bağlı", "Ixtiyoriy")),
            ("hidden", T("Не спрашивать", "Сурабоо", "Don't ask", "Sorma", "So'ramaslik")),
        };
        Choice(cart, T("Телефон покупателя", "Кардардын телефону", "Customer phone", "Müşteri telefonu", "Xaridor telefoni"), "cart.fields.phone", fieldModes);
        Choice(cart, T("Имя", "Аты", "Name", "Ad", "Ism"), "cart.fields.name", fieldModes);
        Choice(cart, T("Адрес", "Дарек", "Address", "Adres", "Manzil"), "cart.fields.address", fieldModes);
        Choice(cart, T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), "cart.fields.comment", fieldModes);
        NumberField(cart, T("Минимальная сумма заказа, сом", "Заказдын минималдуу суммасы, сом", "Minimum order, som", "Asgari sipariş, som", "Eng kam buyurtma, so'm"), "cart.min_order_total");
        Toggle(cart, T("Самовывоз", "Өзү алып кетүү", "Pickup", "Gel-al", "Olib ketish"), "cart.delivery.pickup");
        Toggle(cart, T("Доставка", "Жеткирүү", "Delivery", "Teslimat", "Yetkazib berish"), "cart.delivery.delivery");
        NumberField(cart, T("Стоимость доставки, сом", "Жеткирүүнүн баасы, сом", "Delivery fee, som", "Teslimat ücreti, som", "Yetkazish narxi, so'm"), "cart.delivery.delivery_fee");
        NumberField(cart, T("Бесплатная доставка от, сом", "Акысыз жеткирүү, сомдон", "Free delivery from, som", "Ücretsiz teslimat, som'dan", "Bepul yetkazish, so'mdan"), "cart.delivery.free_from");
        TextField(cart, T("Где доставляете", "Кайда жеткиресиз", "Delivery areas", "Teslimat bölgeleri", "Qayerga yetkazasiz"), "cart.delivery.zones_text", i18n: true);
        TextField(cart, T("Как оплатить", "Кантип төлөө", "How to pay", "Nasıl ödenir", "Qanday to'lash"), "cart.payment_text", i18n: true);
        Choice(cart, T("Куда приходит заказ", "Заказ кайда келет", "Where orders go", "Sipariş nereye gelir", "Buyurtma qayerga keladi"), "cart.order_channel", new[]
        {
            ("whatsapp", "WhatsApp"), ("server", T("В программу («Заказы с сайта»)", "Программага («Сайттан заказдар»)", "To the app (“Website orders”)", "Programa («Web sitesi siparişleri»)", "Dasturga («Saytdan buyurtmalar»)")),
            ("server_and_whatsapp", T("В программу и WhatsApp", "Программага жана WhatsApp'ка", "App and WhatsApp", "Program ve WhatsApp", "Dastur va WhatsApp")),
        });
        TextField(cart, T("Надпись на кнопке заказа", "Заказ баскычындагы жазуу", "Order button text", "Sipariş düğmesi metni", "Buyurtma tugmasidagi yozuv"), "cart.checkout_button_text", i18n: true);
        TextField(cart, T("Текст после заказа", "Заказдан кийинки текст", "Text after ordering", "Sipariş sonrası metin", "Buyurtmadan keyingi matn"), "cart.success_text", i18n: true);

        var footer = Group(T("Подвал сайта", "Сайттын төмөнкү бөлүгү", "Website footer", "Site alt bilgisi", "Sayt pastki qismi"));
        Toggle(footer, T("Показывать подвал", "Төмөнкү бөлүктү көрсөтүү", "Show the footer", "Alt bilgiyi göster", "Pastki qismni ko'rsatish"), "footer.enabled");
        TextField(footer, T("Адрес магазина", "Дүкөндүн дареги", "Shop address", "Mağaza adresi", "Do'kon manzili"), "footer.address", i18n: true);
        ListField(footer, T("Телефоны (через запятую)", "Телефондор (үтүр менен)", "Phones (comma-separated)", "Telefonlar (virgülle)", "Telefonlar (vergul bilan)"), "footer.phones");
        TextField(footer, T("Часы работы", "Иштөө убактысы", "Opening hours", "Çalışma saatleri", "Ish vaqti"), "footer.hours", i18n: true);
        foreach (var social in new[] { "instagram", "telegram", "whatsapp", "tiktok" })
            TextField(footer, char.ToUpperInvariant(social[0]) + social[1..], "footer.socials." + social, i18n: false);
        TextField(footer, T("Ссылка на карту (2ГИС, Google)", "Картага шилтеме (2ГИС, Google)", "Map link (2GIS, Google)", "Harita bağlantısı (2GIS, Google)", "Xaritaga havola (2GIS, Google)"), "footer.map_url", i18n: false);
        TextField(footer, T("Строка внизу (©)", "Төмөнкү сап (©)", "Bottom line (©)", "Alt satır (©)", "Pastki qator (©)"), "footer.copyright", i18n: true);

        var seo = Group(T("Поиск Google и соцсети", "Google издөө жана соцтармактар", "Google search and social", "Google arama ve sosyal ağlar", "Google qidiruvi va ijtimoiy tarmoqlar"));
        TextField(seo, T("Заголовок в поиске", "Издөөдөгү аталыш", "Title in search", "Aramadaki başlık", "Qidiruvdagi sarlavha"), "seo.title", i18n: true);
        TextField(seo, T("Описание в поиске", "Издөөдөгү сүрөттөмө", "Description in search", "Aramadaki açıklama", "Qidiruvdagi tavsif"), "seo.description", i18n: true, multiline: true);
        ImageField(seo, T("Картинка для ссылки в соцсетях", "Соцтармактагы шилтеме үчүн сүрөт", "Picture for social links", "Sosyal bağlantı resmi", "Ijtimoiy tarmoq havolasi uchun rasm"), "seo.og_image", "og");
        Toggle(seo, T("Показывать сайт в Google", "Сайтты Google'да көрсөтүү", "Show the website in Google", "Siteyi Google'da göster", "Saytni Google'da ko'rsatish"), "seo.indexing");
        LockInputs();
    }

    private static List<SiteTheme> LoadThemes()
    {
        if (_themes is not null)
            return _themes;
        var list = new List<SiteTheme>();
        try
        {
            using var order = AssetLoader.Open(new Uri("avares://NurMarketKassa.Avalonia/Assets/SiteThemes/themes.json"));
            var codes = JsonSerializer.Deserialize<List<string>>(order) ?? new List<string>();
            foreach (var code in codes)
            {
                using var stream = AssetLoader.Open(new Uri($"avares://NurMarketKassa.Avalonia/Assets/SiteThemes/{code}.json"));
                if (JsonNode.Parse(stream) is not JsonObject doc || doc["patch"] is not JsonObject patch)
                    continue;
                string Local(string key) => doc[key] is JsonObject n
                    ? T(n["ru"]?.ToString() ?? "", n["ky"]?.ToString() ?? "", n["en"]?.ToString() ?? "", n["tr"]?.ToString() ?? "", n["uz"]?.ToString() ?? "")
                    : code;
                var images = new List<(string, string, string)>();
                if (doc["images"] is JsonObject imgs)
                    foreach (var (path, value) in imgs)
                        if (value is JsonObject v && v["file"]?.ToString() is { Length: > 0 } file)
                            images.Add((path, file, v["kind"]?.ToString() ?? "other"));
                list.Add(new SiteTheme(code, Local("name"), Local("description"), patch, images));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор сайта: темы не прочитаны ({ex.Message}).", "WARNING");
        }
        return _themes = list;
    }

    /// <summary>Галерея тем: превью, название, для какого магазина, «Применить»; нынешняя тема отмечена.</summary>
    private void BuildThemes(StackPanel host)
    {
        host.Children.Add(Hint(T("Весь вид сайта одним нажатием: цвета, шрифт, шапка, главный блок с картинкой, карточки товаров, подвал. "
                                 + "Название, телефон, тексты и товары магазина не меняются. Покупатели увидят тему после «Опубликовать».",
            "Сайттын бүт көрүнүшү бир басуу менен: түстөр, шрифт, баш бөлүк, сүрөттүү башкы блок, товар карточкалары, төмөнкү бөлүк. "
            + "Дүкөндүн аты, телефону, тексттери жана товарлары өзгөрбөйт. Кардарлар теманы «Жарыялоодон» кийин көрүшөт.",
            "The whole website look in one click: colours, font, header, main block with a picture, product cards, footer. "
            + "The shop's name, phone, texts and products stay. Customers see the theme after “Publish”.",
            "Sitenin tüm görünümü tek tıkla: renkler, yazı tipi, üst bölüm, resimli ana blok, ürün kartları, alt bilgi. "
            + "Mağaza adı, telefon, metinler ve ürünler değişmez. Müşteriler temayı «Yayınla»dan sonra görür.",
            "Saytning butun ko'rinishi bir bosishda: ranglar, shrift, sarlavha, rasmli asosiy blok, mahsulot kartochkalari, pastki qism. "
            + "Do'kon nomi, telefoni, matnlari va mahsulotlari o'zgarmaydi. Xaridorlar mavzuni «E'lon qilish»dan keyin ko'radi.")));
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var accent = Str("theme.colors.accent").ToUpperInvariant();
        var background = Str("theme.colors.background").ToUpperInvariant();
        var font = Str("theme.font.family");
        foreach (var theme in LoadThemes())
        {
            var current = theme.Patch["theme"] is JsonObject th
                          && (th["colors"]?["accent"]?.ToString() ?? "").ToUpperInvariant() == accent
                          && (th["colors"]?["background"]?.ToString() ?? "").ToUpperInvariant() == background
                          && (th["font"]?["family"]?.ToString() ?? "") == font;
            var card = new Border { Width = 184, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(current ? 2 : 1), Margin = new Thickness(0, 0, 8, 8), ClipToBounds = true };
            Use(card, Border.BackgroundProperty, "BrushPanelSoft");
            Use(card, Border.BorderBrushProperty, current ? "BrushAccentStrong" : "BrushBorder");
            var stack = new StackPanel();
            try
            {
                using var thumb = AssetLoader.Open(new Uri($"avares://NurMarketKassa.Avalonia/Assets/SiteThemes/{theme.Code}_thumb.png"));
                stack.Children.Add(new Image { Source = new Bitmap(thumb), Height = 115, Stretch = Stretch.UniformToFill });
            }
            catch
            {
                // нет превью — только название
            }
            var info = new StackPanel { Spacing = 3, Margin = new Thickness(10, 8, 10, 10) };
            var name = new TextBlock { Text = theme.Name + (current ? "  ✓" : ""), FontSize = 14, FontWeight = FontWeight.Bold };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            info.Children.Add(name);
            var about = Hint(theme.Description);
            about.MinHeight = 32;
            info.Children.Add(about);
            var apply = current
                ? UiKit.Ghost(this, T("Применена", "Колдонулду", "Applied", "Uygulandı", "Qo'llangan"))
                : UiKit.Primary(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
            apply.Height = 32;
            apply.Margin = new Thickness(0, 6, 0, 0);
            apply.HorizontalAlignment = HorizontalAlignment.Stretch;
            apply.Click += async (_, _) => await ApplyThemeAsync(theme).ConfigureAwait(true);
            info.Children.Add(apply);
            stack.Children.Add(info);
            card.Child = stack;
            wrap.Children.Add(card);
        }
        host.Children.Add(wrap);
    }

    /// <summary>Применить тему: картинки темы — на сервер (showcase/media/), весь набор настроек — в черновик одним запросом.</summary>
    private async Task ApplyThemeAsync(SiteTheme theme)
    {
        if (_locked)
            return;
        var ok = await ConfirmAsync(T($"Применить тему «{theme.Name}»? Поменяются цвета, шрифт, вид блоков и картинки. Название, телефон и товары останутся. "
                                      + "Покупатели увидят после «Опубликовать»; до этого можно всё подправить или нажать «Отменить изменения».",
            $"«{theme.Name}» темасын колдоносузбу? Түстөр, шрифт, блоктордун көрүнүшү жана сүрөттөр өзгөрөт. Аты, телефону жана товарлар калат. "
            + "Кардарлар «Жарыялоодон» кийин көрүшөт; ага чейин баарын оңдосо же «Өзгөртүүлөрдү жокко чыгаруу» басса болот.",
            $"Apply the “{theme.Name}” theme? Colours, font, block styles and pictures will change. The name, phone and products stay. "
            + "Customers see it after “Publish”; until then you can adjust anything or press “Discard changes”.",
            $"«{theme.Name}» teması uygulansın mı? Renkler, yazı tipi, blok görünümleri ve resimler değişir. Ad, telefon ve ürünler kalır. "
            + "Müşteriler «Yayınla»dan sonra görür; o zamana kadar düzenleyebilir ya da «Değişiklikleri geri al»a basabilirsiniz.",
            $"«{theme.Name}» mavzusi qo'llansinmi? Ranglar, shrift, bloklar ko'rinishi va rasmlar o'zgaradi. Nomi, telefoni va mahsulotlar qoladi. "
            + "Xaridorlar «E'lon qilish»dan keyin ko'radi; ungacha hammasini tuzatish yoki «O'zgarishlarni bekor qilish»ni bosish mumkin.")).ConfigureAwait(true);
        if (!ok)
            return;
        _saveTimer.Stop();
        await SaveDraftAsync().ConfigureAwait(true);
        ShowBanner(T("Применяю тему…", "Тема колдонулууда…", "Applying the theme…", "Tema uygulanıyor…", "Mavzu qo'llanmoqda…"), warning: false);
        try
        {
            var patch = (JsonObject)theme.Patch.DeepClone();
            if (!UploadedThemeImages.TryGetValue(theme.Code, out var uploaded))
                UploadedThemeImages[theme.Code] = uploaded = new Dictionary<string, string>();
            foreach (var (path, file, kind) in theme.Images)
            {
                if (!uploaded.TryGetValue(path, out var id))
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nurmarket-theme-" + file);
                    await using (var asset = AssetLoader.Open(new Uri($"avares://NurMarketKassa.Avalonia/Assets/SiteThemes/{file}")))
                    await using (var output = System.IO.File.Create(temp))
                        await asset.CopyToAsync(output).ConfigureAwait(true);
                    var data = await _api.UploadFileAsync("api/main/showcase/media/", "file", temp, new Dictionary<string, string> { ["kind"] = kind }).ConfigureAwait(true);
                    id = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var idv) ? idv.ToString() : "";
                    if (id.Length == 0)
                        throw new InvalidOperationException(T("сервер не вернул номер картинки", "сервер сүрөттүн номерин кайтарган жок", "the server returned no picture id", "sunucu resim numarası döndürmedi", "server rasm raqamini qaytarmadi"));
                    uploaded[path] = id;
                    try
                    {
                        System.IO.File.Delete(temp);
                    }
                    catch
                    {
                        // временный файл удалит Windows
                    }
                }
                SetIn(patch, path, JsonValue.Create(id));
            }
            var result = await _api.RequestAsync(new HttpMethod("PATCH"), DesignPath + "draft/", patch, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Редактор сайта: применена тема {theme.Code}.", "INFO");
            await LoadAsync().ConfigureAwait(true);
            // 2026-10-05, владелец: «темы не применяются» — сервер тему сохраняет, а сайт витрины NurCRM оформление пока не читает
            // (docs/ТЗ для фронтенда NurCRM - витрина должна показывать оформление из редактора). Говорим об этом прямо.
            ShowBanner(T($"Тема «{theme.Name}» сохранена в черновике. Нравится — «Опубликовать». Если справа вид не изменился — сайт витрины NurCRM пока не показывает оформление: тема хранится на сервере и появится у покупателей, когда NurCRM обновит витрину.",
                $"«{theme.Name}» темасы долбоордо сакталды. Жакса — «Жарыялоо». Оң жакта көрүнүш өзгөрбөсө — NurCRM витринасы азырынча жасалгалоону көрсөтпөйт: тема серверде сакталат жана NurCRM витринаны жаңыртканда кардарларга көрүнөт.",
                $"The “{theme.Name}” theme is saved in the draft. Like it — “Publish”. If the preview on the right didn't change, the NurCRM showcase site doesn't display designs yet: the theme is stored on the server and customers will see it once NurCRM updates the showcase.",
                $"«{theme.Name}» teması taslağa kaydedildi. Beğendiyseniz — «Yayınla». Sağdaki görünüm değişmediyse NurCRM vitrin sitesi henüz tasarımı göstermiyor: tema sunucuda saklanır ve NurCRM vitrini güncellediğinde müşteriler görür.",
                $"«{theme.Name}» mavzusi qoralamada saqlandi. Yoqsa — «E'lon qilish». O'ngda ko'rinish o'zgarmasa — NurCRM vitrina sayti hozircha bezakni ko'rsatmaydi: mavzu serverda saqlanadi va NurCRM vitrinani yangilaganda xaridorlar ko'radi."), warning: false);
            ShowWarningsAppend(result);
        }
        catch (Exception ex)
        {
            ShowBanner(T("Тема не применилась: ", "Тема колдонулган жок: ", "The theme wasn't applied: ", "Tema uygulanmadı: ", "Mavzu qo'llanmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
            PosLogger.Log($"Редактор сайта: тема {theme.Code} не применилась ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Предупреждения сервера о контрасте после темы — под сообщением о теме (у готовых тем их быть не должно).</summary>
    private void ShowWarningsAppend(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("warnings", out var w) || w.ValueKind != JsonValueKind.Array || w.GetArrayLength() == 0)
            return;
        var text = _bannerText.Text;
        ShowWarnings(data);
        _bannerText.Text = text + "\n" + _bannerText.Text;
    }

    private IEnumerable<(string Code, string Name)> Presets()
    {
        if (_options.ValueKind == JsonValueKind.Object && _options.TryGetProperty("presets", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            foreach (var p in list.EnumerateArray())
            {
                var code = p.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var name = p.TryGetProperty("name", out var n) ? (n.ValueKind == JsonValueKind.Object ? (n.TryGetProperty(_lang, out var ln) ? ln.GetString() : n.EnumerateObject().FirstOrDefault().Value.GetString()) : n.GetString()) : code;
                if (code.Length > 0)
                    yield return (code, name ?? code);
            }
            yield break;
        }
        yield return ("classic", T("Классика", "Классика", "Classic", "Klasik", "Klassika"));
        yield return ("dark", T("Тёмный", "Караңгы", "Dark", "Koyu", "Qorong'i"));
        yield return ("minimal", T("Минимализм", "Минимализм", "Minimal", "Minimal", "Minimalizm"));
        yield return ("fresh", T("Свежий", "Жаңы", "Fresh", "Ferah", "Yangi"));
        yield return ("kyrgyz", T("Кыргызский", "Кыргызча", "Kyrgyz", "Kırgız", "Qirg'izcha"));
        yield return ("premium", T("Премиум", "Премиум", "Premium", "Premium", "Premium"));
    }

    private IEnumerable<string> Fonts()
    {
        var list = new List<string>();
        if (_options.ValueKind == JsonValueKind.Object && _options.TryGetProperty("fonts", out var fonts) && fonts.ValueKind == JsonValueKind.Array)
            foreach (var f in fonts.EnumerateArray())
                if ((f.ValueKind == JsonValueKind.Object && f.TryGetProperty("family", out var fam) ? fam.GetString() : f.ValueKind == JsonValueKind.String ? f.GetString() : null) is { Length: > 0 } name)
                    list.Add(name);
        if (list.Count == 0)
            list.AddRange(new[] { "Inter", "Roboto", "Roboto Condensed", "Montserrat", "Nunito", "PT Sans", "Noto Sans" });
        var current = Str("theme.font.family");
        if (current.Length > 0 && !list.Contains(current))
            list.Insert(0, current);
        return list;
    }

    /// <summary>Разделы страницы: включить или выключить и поменять порядок (массив sections сервер заменяет целиком).</summary>
    private void BuildSections(StackPanel host)
    {
        host.Children.Clear();
        if (Get("sections") is not JsonArray sections)
            return;
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i] is not JsonObject s)
                continue;
            var index = i;
            var type = s["type"]?.ToString() ?? "";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var check = new CheckBox { Content = SectionName(type), IsChecked = s["enabled"]?.ToString() == "true", VerticalAlignment = VerticalAlignment.Center };
            Use(check, CheckBox.ForegroundProperty, "BrushText");
            check.IsCheckedChanged += (_, _) =>
            {
                var copy = (JsonArray)sections.DeepClone();
                ((JsonObject)copy[index]!)["enabled"] = check.IsChecked == true;
                Set("sections", copy);
            };
            row.Children.Add(check);
            var up = SmallButton("↑", index > 0);
            up.Click += (_, _) => MoveSection(index, -1, host);
            Grid.SetColumn(up, 1);
            row.Children.Add(up);
            var down = SmallButton("↓", index < sections.Count - 1);
            down.Click += (_, _) => MoveSection(index, 1, host);
            Grid.SetColumn(down, 2);
            row.Children.Add(down);
            host.Children.Add(row);
        }
    }

    private void MoveSection(int index, int delta, StackPanel host)
    {
        if (Get("sections") is not JsonArray sections || index + delta < 0 || index + delta >= sections.Count)
            return;
        var copy = (JsonArray)sections.DeepClone();
        var item = copy[index];
        copy.RemoveAt(index);
        copy.Insert(index + delta, item);
        Set("sections", copy);
        BuildSections(host);
    }

    private Button SmallButton(string text, bool enabled)
    {
        var b = new Button { Content = text, Width = 34, Height = 30, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled, HorizontalContentAlignment = HorizontalAlignment.Center };
        Use(b, Button.BackgroundProperty, "BrushPanelSoft");
        Use(b, Button.ForegroundProperty, "BrushText");
        return b;
    }

    private static string SectionName(string type) => type switch
    {
        "announcement" => T("Объявление", "Кулактандыруу", "Announcement", "Duyuru", "E'lon"),
        "hero" => T("Главный блок", "Башкы блок", "Main block", "Ana blok", "Asosiy blok"),
        "banners" => T("Рекламные баннеры", "Жарнама баннерлери", "Ad banners", "Reklam bannerları", "Reklama bannerlari"),
        "categories" => T("Категории", "Категориялар", "Categories", "Kategoriler", "Toifalar"),
        "promo_blocks" => T("Блоки акций", "Акция блоктору", "Promotion blocks", "Kampanya blokları", "Aksiya bloklari"),
        "products" => T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha mahsulotlar"),
        "featured" => T("Избранные товары", "Тандалган товарлар", "Featured products", "Öne çıkan ürünler", "Tanlangan mahsulotlar"),
        "new_arrivals" => T("Новинки", "Жаңылыктар", "New arrivals", "Yeni gelenler", "Yangiliklar"),
        "on_sale" => T("Со скидкой", "Арзандатуу менен", "On sale", "İndirimde", "Chegirmada"),
        "text" => T("Текст", "Текст", "Text", "Metin", "Matn"),
        "contacts" => T("Контакты", "Байланыштар", "Contacts", "İletişim", "Kontaktlar"),
        _ => type,
    };

    // ───────────────────────────────────────────── поля

    private StackPanel Group(string title, bool open = false)
    {
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 0, 4), IsVisible = open };
        _editor.Children.Add(Card(title, body, open));
        return body;
    }

    private Border Card(string title, StackPanel body, bool open)
    {
        body.IsVisible = open;
        var arrow = new TextBlock { Text = open ? "▾" : "▸", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Use(arrow, TextBlock.ForegroundProperty, "BrushTextSoft");
        var caption = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Use(caption, TextBlock.ForegroundProperty, "BrushText");
        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        headRow.Children.Add(arrow);
        headRow.Children.Add(caption);
        var head = new Button
        {
            Content = headRow, Tag = "head", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Hand),
        };
        head.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            arrow.Text = body.IsVisible ? "▾" : "▸";
        };
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), BorderThickness = new Thickness(1), Child = stack };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private StackPanel Field(StackPanel host, string label)
    {
        var field = new StackPanel { Spacing = 5 };
        field.Children.Add(Label(label));
        host.Children.Add(field);
        return field;
    }

    private Button Chip(string text, bool selected)
    {
        var b = new Button
        {
            Content = text, MinHeight = 34, Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(17), FontSize = 13, Margin = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(selected ? 2 : 1), Focusable = false, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Use(b, Button.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanelSoft");
        Use(b, Button.BorderBrushProperty, selected ? "BrushAccentStrong" : "BrushBorder");
        Use(b, Button.ForegroundProperty, "BrushText");
        return b;
    }

    private void Choice(StackPanel host, string label, string path, (string Value, string Label)[] options)
    {
        var field = Field(host, label);
        var wrap = new WrapPanel();
        void Render()
        {
            wrap.Children.Clear();
            var current = Str(path);
            foreach (var (value, text) in options)
            {
                var chip = Chip(text, value == current);
                chip.Click += (_, _) =>
                {
                    Set(path, JsonValue.Create(value));
                    Render();
                };
                wrap.Children.Add(chip);
            }
        }
        Render();
        field.Children.Add(wrap);
    }

    private void Toggle(StackPanel host, string label, string path)
    {
        var check = new CheckBox { Content = label, IsChecked = Flag(path) };
        Use(check, CheckBox.ForegroundProperty, "BrushText");
        check.IsCheckedChanged += (_, _) => Set(path, JsonValue.Create(check.IsChecked == true));
        host.Children.Add(check);
    }

    private void TextField(StackPanel host, string label, string path, bool i18n, bool multiline = false)
    {
        var field = Field(host, label);
        var box = new TextBox { Text = Str(path), MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline)
            box.Height = 76;
        box.TextChanged += (_, _) =>
        {
            var text = box.Text ?? "";
            if (i18n)
                Set(path + "." + _lang, JsonValue.Create(text));
            else
                Set(path, text.Trim().Length == 0 ? null : JsonValue.Create(text.Trim()));
        };
        field.Children.Add(box);
    }

    private void NumberField(StackPanel host, string label, string path)
    {
        var field = Field(host, label);
        var value = Number(path);
        var box = new TextBox { Text = value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "", Watermark = T("не задано", "коюлган эмес", "not set", "ayarlanmadı", "belgilanmagan"), MinHeight = 38, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) =>
        {
            var text = (box.Text ?? "").Replace(',', '.').Replace(" ", "");
            if (text.Length == 0)
                Set(path, null);
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0)
                Set(path, JsonValue.Create(d));
        };
        field.Children.Add(box);
    }

    private void ListField(StackPanel host, string label, string path)
    {
        var field = Field(host, label);
        var current = Get(path) is JsonArray arr ? string.Join(", ", arr.Select(x => x?.ToString())) : "";
        var box = new TextBox { Text = current, MinHeight = 38 };
        box.TextChanged += (_, _) =>
        {
            var items = (box.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Set(path, new JsonArray(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()));
        };
        field.Children.Add(box);
    }

    private void Slider(StackPanel host, string label, string path, double min, double max, double step)
    {
        var field = Field(host, label);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var value = Math.Clamp(Number(path) ?? min, min, max);
        var slider = new Avalonia.Controls.Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true };
        var shown = new TextBlock { Text = Format(value, step), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        Use(shown, TextBlock.ForegroundProperty, "BrushText");
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
                return;
            var v = Math.Round(slider.Value / step) * step;
            shown.Text = Format(v, step);
            Set(path, step < 1 ? JsonValue.Create(Math.Round(v, 2)) : JsonValue.Create((int)Math.Round(v)));
        };
        row.Children.Add(slider);
        Grid.SetColumn(shown, 1);
        row.Children.Add(shown);
        field.Children.Add(row);
    }

    private static string Format(double v, double step) => step < 1 ? v.ToString("0.0#", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.InvariantCulture);

    private static readonly string[] Palette =
    {
        "#111827", "#374151", "#6B7280", "#9CA3AF", "#E5E7EB", "#FFFFFF",
        "#F7D74F", "#F59E0B", "#F97316", "#EF4444", "#C8102E", "#EC4899",
        "#8B5CF6", "#6366F1", "#3B82F6", "#0EA5E9", "#14B8A6", "#22C55E",
        "#15803D", "#065F46", "#7C2D12", "#F5F6F8", "#FEF3C7", "#181F2B",
    };

    private void ColorField(StackPanel host, string label, string path)
    {
        _labels[path] = label;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(Label(label));
        var current = Str(path);
        var swatch = new Button { Tag = "swatch", Width = 34, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0) };
        Use(swatch, Button.BorderBrushProperty, "BrushBorder");
        var box = new TextBox { Text = current, Width = 96, MinHeight = 32, FontFamily = new FontFamily("Consolas, monospace") };
        void Paint(string hex)
        {
            if (Color.TryParse(hex, out var c))
                swatch.Background = new SolidColorBrush(c);
        }
        Paint(current);
        var palette = new WrapPanel { Width = 6 * 34 };
        var flyout = new Flyout { Content = palette, Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var hex in Palette)
        {
            var cell = new Button { Width = 28, Height = 28, Margin = new Thickness(3), Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse(hex)), BorderThickness = new Thickness(1) };
            Use(cell, Button.BorderBrushProperty, "BrushBorder");
            ToolTip.SetTip(cell, hex);
            cell.Click += (_, _) =>
            {
                box.Text = hex;
                flyout.Hide();
            };
            palette.Children.Add(cell);
        }
        swatch.Flyout = flyout;
        box.TextChanged += (_, _) =>
        {
            var hex = (box.Text ?? "").Trim().ToUpperInvariant();
            if (!hex.StartsWith('#'))
                hex = "#" + hex;
            if (System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-F]{6}$"))
            {
                Paint(hex);
                if (hex != Str(path).ToUpperInvariant())
                    Set(path, JsonValue.Create(hex));
            }
        };
        Grid.SetColumn(swatch, 1);
        Grid.SetColumn(box, 2);
        row.Children.Add(swatch);
        row.Children.Add(box);
        host.Children.Add(row);
    }

    /// <summary>Картинка: загрузка на сервер (showcase/media/, multipart file + kind), в документ — номер картинки.</summary>
    private void ImageField(StackPanel host, string label, string path, string kind)
    {
        var field = Field(host, label);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var state = Hint(string.IsNullOrWhiteSpace(Str(path)) && Get(path) is not JsonObject
            ? T("не загружена", "жүктөлгөн эмес", "not uploaded", "yüklenmedi", "yuklanmagan")
            : T("загружена", "жүктөлгөн", "uploaded", "yüklendi", "yuklangan"));
        state.VerticalAlignment = VerticalAlignment.Center;
        var upload = UiKit.Ghost(this, T("Загрузить…", "Жүктөө…", "Upload…", "Yükle…", "Yuklash…"));
        upload.Height = 34;
        var remove = UiKit.Ghost(this, T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
        remove.Height = 34;
        remove.Click += (_, _) =>
        {
            Set(path, null);
            state.Text = T("не загружена", "жүктөлгөн эмес", "not uploaded", "yüklenmedi", "yuklanmagan");
        };
        upload.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(T("Картинки", "Сүрөттөр", "Pictures", "Resimler", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
            }).ConfigureAwait(true);
            var local = files.FirstOrDefault()?.TryGetLocalPath();
            if (local is null)
                return;
            state.Text = T("загружаю…", "жүктөлүүдө…", "uploading…", "yükleniyor…", "yuklanmoqda…");
            try
            {
                var data = await _api.UploadFileAsync("api/main/showcase/media/", "file", local, new Dictionary<string, string> { ["kind"] = kind }).ConfigureAwait(true);
                var id = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var idv) ? idv.ToString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException(T("сервер не вернул номер картинки", "сервер сүрөттүн номерин кайтарган жок", "the server returned no picture id", "sunucu resim numarası döndürmedi", "server rasm raqamini qaytarmadi"));
                Set(path, JsonValue.Create(id));
                state.Text = T("загружена", "жүктөлгөн", "uploaded", "yüklendi", "yuklangan");
                PosLogger.Log($"Редактор сайта: картинка ({kind}) загружена.", "INFO");
            }
            catch (Exception ex)
            {
                state.Text = T("не загрузилась: ", "жүктөлгөн жок: ", "upload failed: ", "yüklenemedi: ", "yuklanmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
                PosLogger.Log($"Редактор сайта: картинка не загружена ({ex.Message}).", "WARNING");
            }
        };
        row.Children.Add(upload);
        row.Children.Add(remove);
        row.Children.Add(state);
        field.Children.Add(row);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
