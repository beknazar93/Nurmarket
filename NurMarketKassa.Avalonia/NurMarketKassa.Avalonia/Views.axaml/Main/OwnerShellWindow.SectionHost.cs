using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15 — часть класса OwnerShellWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — OwnerShellWindow.axaml.cs
public partial class OwnerShellWindow
{
    private sealed class Section
    {
        public required string Key { get; init; }
        public required Window Window { get; init; }

        /// <summary>Открыт до смены языка — при следующем переходе откроется заново.</summary>
        public bool Stale { get; set; }

        /// <summary>2026-10-20: номер последнего показа — кого из спрятанных закрыть первым (TrimHiddenSections).</summary>
        public long LastShown { get; set; }

        /// <summary>2026-11-05 (аудит №4, Л4-11): с какого момента раздел спрятан; null — показан сейчас (TrimIdleHiddenSections).</summary>
        public DateTime? HiddenSince { get; set; }
    }

    /// <summary>2026-10-20, живой аудит (п. 9, память): к концу аудита ≈ 25 спрятанных разделов держали 933 МБ рабочего
    /// набора / 1 165 МБ private — рядом с кассой на ПК с 7,8 ГБ это много. Теперь спрятанными живут только несколько
    /// последних разделов, остальные закрываются штатно (Close — их Closed отписывает события и таймеры).</summary>
    // 2026-11-01 (В-06, аудит «10 из 10»): было 3 — на кругу из 17 разделов каждый переход заново грузил данные (3,2 запроса на переход, фриз
    // >200 мс в 12,7 % переходов). Лёгкие разделы (Долги, Клиенты, Зарплата, Прибыль — 5–15 МБ каждый) держим 5; тяжёлые (HeavySections)
    // по-прежнему закрываются. Данные «Бонусов» кэшируются отдельно (BonusAnalyticsWindow.ReportCache). Память замерить, см. отчёт.
    private const int HiddenSectionsKept = 5;

    /// <summary>Тяжёлые разделы (предпросмотр сайта, графики аналитики) — закрываются вскоре после ухода из них.
    /// 2026-10-30 (оптимизация кассы, Б4): + «CRM» и «WhatsApp» (WebView2: 6–9 процессов Chromium, свёрнутый WhatsApp Web продолжал гонять JS и сокет).
    /// Вход в WhatsApp сохраняется в профиле WebView2; при возврате страница грузится 3–5 с.</summary>
    private static readonly HashSet<string> HeavySections = new() { "siteeditor", "analytics", "crm", "whatsapp" };

    /// <summary>Что вернуть разделу, открытому заново после закрытия: вкладку и кнопку периода.</summary>
    private sealed class SectionState
    {
        public int TabIndex { get; init; } = -1;
        public string? QuickDate { get; init; }
    }

    private readonly Dictionary<string, SectionState> _sectionStates = new();
    private long _sectionShowCounter;

    /// <summary>Разделы, которые сами переводятся на лету (калькулятор, база знаний), и сайт
    /// NurCRM — язык его страниц не наш. Остальные собирают часть надписей в коде один раз.</summary>
    private static readonly HashSet<string> LiveLanguageSections = new() { "calculator", "kb", "crm" };

    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, string> _navTitles = new();
    private Section? _activeSection;
    private bool _positioningSection;
    private bool _closingAllSections;

    private string TitleFor(Section section) =>
        _navTitles.TryGetValue(section.Key, out var title) ? title : section.Window.Title ?? section.Key;

    private void OpenSection(string key, Func<Window> create)
    {
        var existing = _sections.FirstOrDefault(s => s.Key == key);
        if (existing is { Stale: true })
        {
            // Сначала из списка — чтобы закрытие не вернуло окно к сводке и не нашлось снова.
            _sections.Remove(existing);
            existing.Window.Close();
            existing = null;
        }

        if (existing != null)
        {
            ShowSection(existing);
            return;
        }

        var window = create();
        PrepareEmbeddedWindow(window);
        UnsavedInputTracker.Attach(window); // 2026-10-20: «есть несохранённое» — см. SectionBusy
        var section = new Section { Key = key, Window = window };
        window.Closed += (_, _) =>
        {
            OnSectionClosed(section);
            // 2026-11-06 (В11, ПМ-1): закрытый раздел берём на учёт по слабой ссылке (MemoryDiagnostics назовёт тот, что остался в памяти после сборок) и
            // через 20 с уплотняем кучу — иначе дыры в куче больших объектов держат частную память процесса после закрытия раздела.
            ClosedObjectWatch.Instance.Track(window, key, DateTime.UtcNow, GC.CollectionCount(2));
            ScheduleMemoryTrim();
            // 2026-11-07 (память, Б-J1): Window.Close() не отсоединяет содержимое от дерева — бесконечная анимация индикатора загрузки на общих часах и подписки «при отсоединении»
            // держали закрытый раздел; отсоединяем сами — отложенно, чтобы все остальные подписчики Closed (в том числе добавленные окном позже) успели отработать.
            Dispatcher.UIThread.Post(() => ClosedWindowCleanup.Release(window), DispatcherPriority.Background);
        };
        _sections.Add(section);
        PosLogger.Log($"Owner app: раздел «{TitleFor(section)}» открыт.", "UI");
        MemoryDiagnostics.LogAround($"раздел {TitleFor(section)} открыт"); // 2026-11-05 (Р3-01): память до/после открытия раздела — в журнал
        ShowSection(section);
        // 2026-10-20: раздел закрывали ради памяти — возвращаем вкладку и период, как было бы у спрятанного окна.
        if (_sectionStates.Remove(SectionStateKey(section), out var state))
            RestoreSectionState(window, state);
    }

    /// <summary>Окно раздела без рамки, значка в панели задач и ограничений размера — его место
    /// и размер задаёт SyncSectionBounds. Свои кнопки «свернуть/развернуть» у разделов с
    /// нарисованной шапкой прячем: окно сворачивается и разворачивается целиком, кнопками сверху.</summary>
    private void PrepareEmbeddedWindow(Window window)
    {
        FullscreenHelper.MarkEmbedded(window); // 2026-10-27: встроенный раздел не разворачивается сам (FullscreenHelper.TryApplySectionWindowMode)
        // 2026-11-01: «Масштаб интерфейса» и на раздел — содержимое в общей обёртке масштаба (OwnerShellWindow.Nav.cs).
        WrapSectionForScale(window);
        ApplySectionScale(window);
        window.SystemDecorations = SystemDecorations.None;
        window.ExtendClientAreaToDecorationsHint = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SizeToContent = SizeToContent.Manual;
        window.CanResize = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        window.WindowState = WindowState.Normal;

        // Раздел сам убирает повтор своего названия и свои кнопки окна (2026-09-27, «дублируется»).
        var ownerSection = window as IOwnerSection;
        ownerSection?.AsOwnerSection();
        // Esc закрывал раздел его кнопкой «Закрыть» (IsCancel), а она теперь скрыта — закрываем
        // напрямую. Разделы со своим обработчиком Esc (Склад, Продажи, Финансы…) срабатывают раньше.
        if (ownerSection != null)
            EscapeKey.Attach(window);
        // 2026-10-06: Ctrl+K из раздела — к поиску раздела в меню (OwnerShellWindow.NavSearch.cs).
        AttachNavHotkey(window);

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Поиск по классу — только для окон, которые не прячут свои кнопки сами: он задевал и
            // кнопки внутри окна (✕ карточки клиента тоже WindowControlButton).
            if (ownerSection == null)
            {
                foreach (var button in window.GetVisualDescendants().OfType<Button>())
                {
                    var classes = button.Classes;
                    if ((classes.Contains("WindowControlButton") && !classes.Contains("WindowCloseButton"))
                        || (classes.Contains("caption") && !classes.Contains("close")))
                        button.IsVisible = false;
                }
            }

            SyncSectionBounds();
        }, DispatcherPriority.Loaded);

        // Раздел сам себя не двигает и не разворачивает: перетаскивание за его шапку или двойной
        // щелчок возвращаем на место.
        window.PositionChanged += (_, _) =>
        {
            if (!_positioningSection && ReferenceEquals(_activeSection?.Window, window))
                Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Background);
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || _positioningSection || window.WindowState == WindowState.Normal)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Minimized && ReferenceEquals(_activeSection?.Window, window))
                {
                    window.WindowState = WindowState.Normal;
                    SyncSectionBounds();
                }
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>null — сводка. Иначе раздел занимает правую часть окна, шапка сжимается до
    /// полосы с названием раздела и кнопками окна.</summary>
    private void ShowSection(Section? section)
    {
        // 2026-10-18, вид «Бургер ☰»: переход в раздел закрывает выехавшее меню и убирает снимок раздела под ним.
        CloseBurgerQuietly();
        _activeSection = section;
        UpdateSectionExcelButton(section);
        var returning = section is { LastShown: > 0 };
        if (section != null)
            section.LastShown = ++_sectionShowCounter;
        // 2026-10-18, этап О2: в колонке пунктов — группа открытого раздела; раскрытая нажатием колонка сворачивается,
        // а до 1366 точек колонка пунктов сворачивается сама, пока открыт раздел (ширина SectionHost — ниже, UpdateLayout).
        if (GroupOfKey(section?.Key ?? "overview") is { } navGroup)
            _navGroup = navGroup;
        _navPeek = false;
        ApplySidebarLayout();
        PlaceRentalAlert();
        foreach (var other in _sections)
        {
            if (!ReferenceEquals(other, section))
            {
                if (other.Window.IsVisible)
                    other.Window.Hide();
                other.HiddenSince ??= DateTime.UtcNow; // 2026-11-05 (Л4-11): отсчёт простоя спрятанного раздела
            }
        }
        if (section != null)
            section.HiddenSince = null;
        // 2026-10-20, память: лишние спрятанные разделы — закрыть.
        TrimHiddenSections();

        var overview = section == null;
        // 2026-10-19, вторая живая проверка: возврат на «Сводку» — карточка долгов из свежего кэша раздела (без запросов).
        if (overview)
            ApplyExactDebtsFromCache();
        OverviewScroll.IsVisible = overview;
        OverviewTools.IsVisible = overview;
        TitlePanel.IsVisible = overview;
        SectionTitleText.IsVisible = !overview;
        SectionHost.IsVisible = !overview;
        HeaderGrid.Margin = overview ? new Thickness(28, 12, 10, 4) : new Thickness(24, 4, 10, 4);
        AdjustHeaderForPhone(overview);
        // 2026-10-18, этап О2: шапка «Сводки» на узком окне — в две строки, в разделе — в одну (FitHeaderRows).
        FitHeaderTools();
        if (section != null)
            SectionTitleText.Text = TitleFor(section);
        UpdateNavHighlight();
        // 2026-11-01 (В2-03): возврат в спрятанный раздел — он сам обновляет данные старше 60 с (со своим индикатором загрузки).
        if (returning && section!.Window is IOwnerSectionRefresh refresh)
        {
            try
            {
                if (refresh.RefreshOnShow() is { } dataAt)
                {
                    // Тихо, с пометкой в шапке раздела: данные старые, идёт обновление (пометка гаснет через 6 с).
                    var baseTitle = TitleFor(section);
                    var note = baseTitle + " · " + Tr.T($"обновляется · данные на {dataAt:HH:mm}", $"жаңырууда · маалымат {dataAt:HH:mm} боюнча",
                        $"updating · data as of {dataAt:HH:mm}", $"güncelleniyor · veriler {dataAt:HH:mm} itibarıyla", $"yangilanmoqda · ma'lumot {dataAt:HH:mm} holatiga ko'ra");
                    SectionTitleText.Text = note;
                    var shown = section;
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (ReferenceEquals(_activeSection, shown) && SectionTitleText.Text == note)
                            SectionTitleText.Text = baseTitle;
                    }, TimeSpan.FromSeconds(6));
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: раздел «{TitleFor(section)}» не обновился при показе ({ex.Message}).", "WARNING");
            }
        }
        if (section == null)
        {
            if (_abcStale)
                _ = RefreshAbcAsync();
            return;
        }

        // Область раздела только что стала видимой — размеры у неё появятся после разметки.
        UpdateLayout();
        SyncSectionBounds();
        if (!section.Window.IsVisible)
            section.Window.Show(this);
        section.Window.Activate();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    private void UpdateNavHighlight()
    {
        var activeKey = _activeSection?.Key ?? "overview";
        foreach (var (key, button) in _navButtons)
            button.Classes.Set("active", key == activeKey);
        // 2026-10-06: открытый раздел виден и в свёрнутой группе.
        ApplyNavFilter();
    }

    /// <summary>Кладёт окно открытого раздела ровно на правую часть окна.</summary>
    private void SyncSectionBounds()
    {
        var section = _activeSection;
        if (section == null || !IsVisible || WindowState == WindowState.Minimized || !SectionHost.IsVisible)
            return;

        var size = SectionHost.Bounds.Size;
        if (size.Width < 50 || size.Height < 50)
            return;
        // 2026-11-01: размер области раздела — в точках макета, а окно раздела — отдельное окно в реальных точках (масштаб интерфейса).
        size = new Size(size.Width * _shellScale, size.Height * _shellScale);

        var topLeft = SectionHost.PointToScreen(new Point(0, 0));
        var window = section.Window;
        _positioningSection = true;
        try
        {
            if (window.WindowState != WindowState.Normal)
                window.WindowState = WindowState.Normal;
            if (window.Position != topLeft)
                window.Position = topLeft;
            if (Math.Abs(window.Width - size.Width) > 0.5)
                window.Width = size.Width;
            if (Math.Abs(window.Height - size.Height) > 0.5)
                window.Height = size.Height;
        }
        finally
        {
            _positioningSection = false;
        }
    }

    /// <summary>Раздел закрылся сам (своя кнопка «закрыть», Esc) — возвращаемся к сводке.</summary>
    private DispatcherTimer? _memoryTrimTimer;

    /// <summary>2026-11-06 (В11, ПМ-1): одна сборка с уплотнением кучи через OwnerMemoryTrim.SettleDelay после последнего закрытия раздела (несколько закрытий подряд сливаются в одну).</summary>
    private void ScheduleMemoryTrim()
    {
        if (_cts.IsCancellationRequested)
            return;
        if (_memoryTrimTimer is null)
        {
            _memoryTrimTimer = new DispatcherTimer { Interval = OwnerMemoryTrim.SettleDelay };
            _memoryTrimTimer.Tick += (_, _) =>
            {
                _memoryTrimTimer?.Stop();
                var lastKey = _lastKeyInputUtc;
                _ = Task.Run(() => OwnerMemoryTrim.TrimNow(lastKey));
            };
            Closed += (_, _) => _memoryTrimTimer?.Stop();
        }
        _memoryTrimTimer.Stop();
        _memoryTrimTimer.Start();
    }

    private void OnSectionClosed(Section section)
    {
        if (!_sections.Remove(section) || _closingAllSections || !ReferenceEquals(_activeSection, section))
            return;
        ShowSection(null);
    }

    /// <summary>2026-10-20, память (живой аудит п. 9): спрятанными держим только HiddenSectionsKept последних разделов,
    /// тяжёлые (HeavySections) закрываем через несколько секунд после ухода. Не закрываем раздел с открытым окном поверх
    /// (диалог приёмки, карточка…) и раздел, который сам просит остаться (IOwnerSectionKeepAlive: звонок, несохранённая
    /// приёмка) — такие и в счёт не идут. Раздел после смены языка (Stale) всё равно открылся бы заново — закрываем сразу.</summary>
    private void TrimHiddenSections()
    {
        if (_closingAllSections)
            return;
        var kept = 0;
        foreach (var section in _sections.Where(s => !ReferenceEquals(s, _activeSection)).OrderByDescending(s => s.LastShown).ToList())
        {
            if (SectionBusy(section))
                continue;
            if (section.Stale)
                CloseHiddenSection(section, "после смены языка");
            else if (HeavySections.Contains(section.Key))
                ScheduleHeavySectionClose(section);
            else if (++kept > HiddenSectionsKept)
                CloseHiddenSection(section, "давно не открывали");
        }
    }

    /// <summary>2026-11-05 (аудит №4, Л4-11): спрятанный раздел, который не открывали дольше IdleCloseAfter и где нет реально несохранённого
    /// ввода (SectionBusy), закрывается — освобождает ~37 МБ дерева элементов. Раньше «давно не открывали» срабатывало только при открытии
    /// другого раздела и пропускало всё, где общий признак ввода «залип». Вкладка и период вернутся при следующем открытии (CaptureSectionState).</summary>
    private static readonly TimeSpan IdleCloseAfter = TimeSpan.FromMinutes(10);

    private void TrimIdleHiddenSections()
    {
        if (_closingAllSections)
            return;
        var now = DateTime.UtcNow;
        foreach (var section in _sections.Where(s => !ReferenceEquals(s, _activeSection) && s.HiddenSince is { } since && now - since >= IdleCloseAfter).ToList())
        {
            if (section.Window.IsVisible || SectionBusy(section))
                continue;
            CloseHiddenSection(section, "простой больше 10 минут");
        }
    }

    private static bool SectionBusy(Section section) =>
        section.Window.OwnedWindows.Count > 0
        || section.Window is IOwnerSectionKeepAlive { KeepAliveWhenHidden: true }
        // 2026-10-20, доработка: несохранённый ввод (Настройки, Калькуляция, черновик редактора…) — раздел не закрываем.
        || ((section.Window as IOwnerSection)?.HasUnsavedChanges ?? UnsavedInputTracker.IsDirty(section.Window));

    /// <summary>Не сразу: «Редактор сайта» отправляет черновик через 0,8 с после правки, Нур Советник открывает редактор
    /// на одну голосовую команду и возвращается — пусть успеет. Вернулись в раздел за это время — не закрываем.</summary>
    private void ScheduleHeavySectionClose(Section section)
    {
        var shown = section.LastShown;
        DispatcherTimer.RunOnce(() =>
        {
            if (section.LastShown == shown && _sections.Contains(section) && !ReferenceEquals(_activeSection, section)
                && !section.Window.IsVisible && !SectionBusy(section))
                CloseHiddenSection(section, "тяжёлый раздел");
        }, TimeSpan.FromSeconds(5));
    }

    private void CloseHiddenSection(Section section, string why)
    {
        try
        {
            if (CaptureSectionState(section.Window) is { } state)
                _sectionStates[SectionStateKey(section)] = state;
            PosLogger.Log($"Owner app: спрятанный раздел «{TitleFor(section)}» закрыт ({why}) — освобождаем память.", "UI");
            MemoryDiagnostics.LogAround($"раздел {TitleFor(section)} закрыт ({why})"); // 2026-11-05 (Р3-01)
            // Из списка его убирает Closed → OnSectionClosed (раздел не открыт — к сводке не возвращает).
            section.Window.Close();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: hidden section close failed: {ex.Message}", "WARNING");
        }
    }

    /// <summary>У «Продаж и финансов» состав вкладок зависит от прав (Tag окна) — состояние храним для каждого состава.</summary>
    private static string SectionStateKey(Section section) => section.Key + "|" + (section.Window.Tag as string);

    /// <summary>Главная вкладка (первый TabControl окна) и кнопка периода «Сегодня/Вчера/Неделя/Месяц» (группа QuickDate).</summary>
    private static SectionState? CaptureSectionState(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        var quick = window.GetVisualDescendants().OfType<RadioButton>()
            .FirstOrDefault(r => r.GroupName == "QuickDate" && r.IsChecked == true)?.Tag as string;
        var tabIndex = tabs?.SelectedIndex ?? -1;
        return tabIndex <= 0 && (quick is null or "Today") ? null : new SectionState { TabIndex = tabIndex, QuickDate = quick };
    }

    /// <summary>Вкладка — сразу (до SelectSalesFinanceTab/ShowStockTab: явный переход главнее), период — нажатием кнопки
    /// после первой загрузки окна, как если бы его нажал владелец. «Спец. дата» не возвращаем — она открывает окно дат.</summary>
    private static void RestoreSectionState(Window window, SectionState state)
    {
        try
        {
            if (state.TabIndex > 0 && window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { } tabs
                && state.TabIndex < tabs.ItemCount && tabs.Items[state.TabIndex] is TabItem { IsVisible: true, IsEnabled: true })
                tabs.SelectedIndex = state.TabIndex;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: section tab restore failed: {ex.Message}", "WARNING");
        }

        if (state.QuickDate is null or "Today" or "Custom")
            return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!window.IsVisible)
                    return;
                var button = window.GetVisualDescendants().OfType<RadioButton>()
                    .FirstOrDefault(r => r.GroupName == "QuickDate" && Equals(r.Tag, state.QuickDate) && r.IsEffectivelyVisible && r.IsEffectivelyEnabled);
                if (button is null || button.IsChecked == true)
                    return;
                button.IsChecked = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section period restore failed: {ex.Message}", "WARNING");
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>2026-09-26, «баг с языком»: раздел, открытый до смены языка, оставался на старом
    /// (меню по-узбекски, «Эмгек акы» по-кыргызски). Спрятанные разделы закрываем — при переходе
    /// они откроются уже на новом языке; открытый сейчас (обычно «Настройки», где язык и меняют)
    /// не трогаем, а помечаем — он откроется заново при следующем переходе в него.</summary>
    private void RebuildSectionsForLanguage()
    {
        foreach (var section in _sections.ToList())
        {
            if (LiveLanguageSections.Contains(section.Key))
                continue;
            if (ReferenceEquals(section, _activeSection))
            {
                section.Stale = true;
                continue;
            }

            _sections.Remove(section);
            try
            {
                // 2026-10-20: вкладку и период — тоже вернуть при новом открытии (как у разделов, закрытых ради памяти).
                if (CaptureSectionState(section.Window) is { } state)
                    _sectionStates[SectionStateKey(section)] = state;
                section.Window.Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section close on language change failed: {ex.Message}", "WARNING");
            }
        }
    }

    // ------------------------------------------------------------------ Excel раздела (2026-11-02, аудит №2, В-07)

    private Button? _sectionExcelButton;

    private void UpdateSectionExcelButton(Section? section)
    {
        if (_sectionExcelButton is null)
        {
            _sectionExcelButton = new Button
            {
                Content = "Excel",
                Classes = { "link" },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                IsVisible = false,
            };
            _sectionExcelButton.Click += async (_, _) => await ExportSectionExcelAsync().ConfigureAwait(true);
            CaptionButtons.Children.Insert(0, _sectionExcelButton);
        }
        // 2026-11-05 (Н-10, приёмка 1.17.75): имя для диктора/UIA ставим при каждом обновлении — раньше фиксировалось на языке момента создания кнопки (при ky оставалось русским).
        Avalonia.Automation.AutomationProperties.SetName(_sectionExcelButton, Tr.T("Выгрузить в Excel", "Excel'ге чыгаруу", "Export to Excel", "Excel'e aktar", "Excel'ga chiqarish"));
        _sectionExcelButton.IsVisible = section?.Window is IOwnerSectionExcel;
    }

    private async Task ExportSectionExcelAsync()
    {
        if (_activeSection?.Window is not IOwnerSectionExcel exporter)
            return;
        try
        {
            if (!TariffGate.CanUseAnalyticsExport)
            {
                PosMessageBox.Show(this, TariffGate.AnalyticsExportLockedMessage, Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            var table = exporter.BuildExportTable();
            if (table is null || table.Rows.Count == 0)
            {
                PosMessageBox.Show(this, Tr.T("Выгружать пока нечего: данные ещё не загружены или список пуст.", "Чыгара турган нерсе жок: маалымат жүктөлө элек же тизме бош.",
                    "Nothing to export yet: the data is not loaded or the list is empty.", "Henüz aktarılacak bir şey yok: veri yüklenmedi veya liste boş.", "Hozircha chiqariladigan narsa yo'q: ma'lumot yuklanmagan yoki ro'yxat bo'sh."),
                    Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            var shop = ReportShopName.Current;
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = Tr.T("Сохранить в Excel", "Excel'ге сактоо", "Save to Excel", "Excel olarak kaydet", "Excel'ga saqlash"),
                SuggestedFileName = SectionTableExport.SuggestFileName(table, shop),
                FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
            }).ConfigureAwait(true);
            if (file is null)
                return;
            // 2026-11-06 (аудит №5, А5-02): на Android выбор файла отдаёт content:// без локального пути — раньше Excel раздела владельца оставался пустым файлом на 0 байт.
            // Пишем по пути из ExportFileBridge (на ПК — выбранный путь, на Android — временный файл), затем CompleteAsync копирует его в выбранный документ.
            var path = ExportFileBridge.PathFor(file);
            await Task.Run(() => SectionTableExport.ExportToExcel(path, table, shop)).ConfigureAwait(true);
            path = await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);
            PosMessageBox.Show(this, Tr.T($"Файл сохранён: {path}", $"Файл сакталды: {path}", $"File saved: {path}", $"Dosya kaydedildi: {path}", $"Fayl saqlandi: {path}"),
                Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: выгрузка раздела в Excel не удалась: {ex}", "WARNING");
            PosMessageBox.Show(this, Tr.T("Не удалось сохранить файл: ", "Файлды сактоо мүмкүн болгон жок: ", "Could not save the file: ", "Dosya kaydedilemedi: ", "Faylni saqlab bo'lmadi: ")
                + ServerOutageMonitor.FriendlyError(ex), Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private bool _exitConfirmed;
    private bool _exitAsking;
    private bool _unsentChecked; // 2026-11-06: вопрос про неотправленные приходы и новые товары уже задан и принят

    /// <summary>Разделы с несохранённым (явный признак раздела или общий признак ввода — как в SectionBusy).</summary>
    private List<Section> DirtySections() => _sections
        .Where(s => (s.Window as IOwnerSection)?.HasUnsavedChanges ?? UnsavedInputTracker.IsDirty(s.Window))
        .ToList();

    /// <summary>2026-11-02 (В3-01): спросить, что делать с несохранённым, прежде чем выйти из программы или из учётной записи.
    /// true — можно уходить (сохранено или владелец отказался от правок); false — остаёмся (открыт первый раздел с правками).</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        var dirty = DirtySections();
        if (dirty.Count == 0)
            return true;
        var names = string.Join(", ", dirty.Select(TitleFor));
        // 2026-11-02 (приёмка круга 3, п. 6): «Сохранить и выйти» предлагается, ТОЛЬКО если сохранить умеют ВСЕ разделы с правками (сейчас —
        // редактор сайта). Иначе кнопка обещала бы невозможное: остальные формы (настройки, зарплата, приёмка…) сохраняются своей кнопкой в самом
        // разделе. Тогда в вопросе перечислены разделы, которые автоматически не сохранить, и остаются «Остаться» / «Выйти без сохранения».
        var canSave = dirty.All(s => s.Window is IOwnerSectionExit);
        var manualOnly = string.Join(", ", dirty.Where(s => s.Window is not IOwnerSectionExit).Select(TitleFor));
        var manualNote = manualOnly.Length == 0 ? "" : " " + Tr.T($"Автоматически сохранить нельзя: {manualOnly} — вернитесь в раздел и нажмите «Сохранить».",
            $"Автоматтык сактоого болбойт: {manualOnly} — бөлүмгө кайтып, «Сактоо» баскычын басыңыз.",
            $"Cannot be saved automatically: {manualOnly} — go back to the section and press “Save”.",
            $"Otomatik kaydedilemez: {manualOnly} — bölüme dönüp «Kaydet»e basın.",
            $"Avtomatik saqlab bo'lmaydi: {manualOnly} — bo'limga qaytib, «Saqlash»ni bosing.");
        var tcs = new TaskCompletionSource<int>(); // 0 — остаться, 1 — выйти без сохранения, 2 — сохранить и выйти
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 512);
        var label = new TextBlock
        {
            Text = Tr.T($"В разделах есть несохранённые правки: {names}. Если выйти сейчас, они пропадут.",
                $"Бөлүмдөрдө сакталбаган оңдоолор бар: {names}. Азыр чыксаңыз, алар жоголот.",
                $"There are unsaved changes in: {names}. If you leave now, they will be lost.",
                $"Şu bölümlerde kaydedilmemiş değişiklikler var: {names}. Şimdi çıkarsanız kaybolur.",
                $"Bo'limlarda saqlanmagan o'zgarishlar bor: {names}. Hozir chiqsangiz, ular yo'qoladi.") + manualNote,
            TextWrapping = TextWrapping.Wrap, FontSize = 14,
        };
        UseBrush(label, TextBlock.ForegroundProperty, "BrushText");
        var stay = SectionCards.Cancel(Tr.T("Остаться", "Калуу", "Stay", "Kal", "Qolish"));
        var discard = SectionCards.Danger(Tr.T("Выйти без сохранения", "Сактабай чыгуу", "Leave without saving", "Kaydetmeden çık", "Saqlamasdan chiqish"));
        stay.Click += (_, _) => { tcs.TrySetResult(0); dialog.Close(); };
        discard.Click += (_, _) => { tcs.TrySetResult(1); dialog.Close(); };
        Button? save = null;
        if (canSave)
        {
            save = SectionCards.Primary(Tr.T("Сохранить и выйти", "Сактап чыгуу", "Save and leave", "Kaydet ve çık", "Saqlab chiqish"), isDefault: true);
            save.Click += (_, _) => { tcs.TrySetResult(2); dialog.Close(); };
        }
        dialog.Closed += (_, _) => tcs.TrySetResult(0);
        var shell = SectionCards.Shell(Title ?? "NurCRM", null, label, save is null ? SectionCards.Buttons(stay, discard) : SectionCards.Buttons(stay, discard, save));
        shell.InitialFocus = stay;
        dialog.Content = shell;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        var choice = await tcs.Task.ConfigureAwait(true);
        if (choice == 0)
        {
            ShowSection(dirty[0]);
            return false;
        }
        if (choice == 2)
        {
            foreach (var section in dirty.Where(s => s.Window is IOwnerSectionExit))
            {
                bool saved;
                try
                {
                    saved = await ((IOwnerSectionExit)section.Window).SaveForExitAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: раздел «{TitleFor(section)}» не сохранился при выходе ({ex.Message}).", "WARNING");
                    saved = false;
                }
                if (!saved)
                {
                    ShowSection(section); // причина — в самом разделе
                    return false;
                }
            }
        }
        foreach (var section in dirty)
            (section.Window as IOwnerSectionExit)?.ConfirmExit();
        return true;
    }

    private void CloseAllSections()
    {
        _closingAllSections = true;
        foreach (var section in _sections.ToList())
        {
            try
            {
                section.Window.Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section close failed: {ex.Message}", "WARNING");
            }
        }
    }

    private bool Authorize(string permission)
    {
        if (App.GetRequiredService<IPermissionService>().HasPermission(permission))
            return true;
        // 2026-10-30: сессия недействительна — права не подтвердить, это не «нет доступа»: вместо отказа — «Сессия истекла. Войдите снова».
        if (SessionExpiryMonitor.IsExpired)
        {
            PosLogger.Log($"Owner app: section {permission} — сессия недействительна, показываю «Войдите снова».", "AUTH");
            _ = NurMarketKassa.AvaloniaHost.Services.SessionExpiredPrompt.AskAsync(this, SignOutAsync);
            return false;
        }
        PosLogger.Log($"Owner app: permission denied: {permission}", "WARNING");
        // 2026-11-09, жалоба клиента «все права выданы, перезапуск не помогает»: права в кассе — из профиля, прочитанного при входе (или из сохранённого
        // входа, если сервер при запуске не ответил за 3 с). Перед отказом — один раз перечитать профиль с сервера: доступ есть — раздел открывается сам
        // (нажатие в меню) или просьба нажать ещё раз; нет — прежний отказ со строкой «для поддержки». Профиль только что с сервера или авария — сразу отказ.
        if (_permissionRecheckRunning)
            return false; // повторное нажатие, пока идёт перепроверка: ответ придёт от неё
        if (!ServerOutageMonitor.IsOutage
            && !(PermissionRecheck.LiveProfileAt is { } liveAt && DateTimeOffset.Now - liveAt < TimeSpan.FromSeconds(20)))
        {
            _ = RecheckPermissionThenExplainAsync(permission, _navOpenInProgress);
            return false;
        }
        ShowPermissionDenied(permission);
        return false;
    }

    /// <summary>2026-11-09: открытие пункта меню, идущее сейчас (для повтора после перепроверки прав); null — вызов не из меню.</summary>
    private Action? _navOpenInProgress;
    private bool _permissionRecheckRunning;

    private async Task RecheckPermissionThenExplainAsync(string permission, Action? retry)
    {
        _permissionRecheckRunning = true;
        var oldCursor = Cursor;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
        bool refreshed;
        try
        {
            refreshed = await PermissionRecheck.RefreshProfileAsync(App.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>(),
                App.GetRequiredService<IAutonomousAuthService>(), "раздел " + permission, TimeSpan.FromSeconds(6)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Owner app: перепроверка прав не удалась ({ex.GetType().Name}).", "WARNING");
            refreshed = false;
        }
        finally
        {
            _permissionRecheckRunning = false;
            Cursor = oldCursor;
        }
        if (refreshed && App.GetRequiredService<IPermissionService>().HasPermission(permission))
        {
            PosLogger.Log($"Owner app: {permission} — после перечитывания профиля доступ есть.", "AUTH");
            if (retry is not null)
            {
                try
                {
                    retry();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: section failed to open after permission recheck: {ex}", "ERROR");
                }
                return;
            }
            PosMessageBox.Show(this,
                Tr.T("Права обновлены с сервера NurCRM — доступ есть. Откройте раздел ещё раз.", "Укуктар NurCRM серверинен жаңыртылды — уруксат бар. Бөлүмдү кайра ачыңыз.",
                    "Permissions were updated from the NurCRM server — access granted. Open the section again.",
                    "Yetkiler NurCRM sunucusundan güncellendi — erişim var. Bölümü yeniden açın.",
                    "Huquqlar NurCRM serveridan yangilandi — ruxsat bor. Bo'limni qayta oching."),
                Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        ShowPermissionDenied(permission);
    }

    private void ShowPermissionDenied(string permission)
    {
        // 2026-11-09: строка «для поддержки» (право, что прислал сервер, роль, откуда права) — в окне и в журнале.
        var diagnostics = PermissionRecheck.Describe(permission, App.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>().UserPayload);
        PosLogger.Log($"Owner app: отказ по правам — {diagnostics}", "WARNING");
        // 2026-10-01: подсказка, какую галочку включить на сайте (названия — как на сайте NurCRM).
        var siteRight = SiteRightName(permission);
        var hint = siteRight is null ? "" : Environment.NewLine + Environment.NewLine + Tr.T(
            $"Доступ даёт владелец на сайте NurCRM: Сотрудники → сотрудник → доступ «{siteRight}». После изменения перезапустите программу.",
            $"Уруксатты ээси NurCRM сайтында берет: Кызматкерлер → кызматкер → «{siteRight}» уруксаты. Өзгөрткөндөн кийин программаны кайра ачыңыз.",
            $"The owner grants it on the NurCRM website: Employees → employee → “{siteRight}” access. Restart the program after the change.",
            $"Erişimi işletme sahibi NurCRM sitesinde verir: Çalışanlar → çalışan → «{siteRight}» erişimi. Değişiklikten sonra programı yeniden başlatın.",
            $"Ruxsatni ega NurCRM saytida beradi: Xodimlar → xodim → «{siteRight}» ruxsati. O'zgartirgandan keyin dasturni qayta ishga tushiring.");
        PosMessageBox.Show(this,
            Tr.T("Недостаточно прав для этого раздела.", "Бул бөлүм үчүн укук жетишсиз.", "You don't have permission to open this section.",
                "Bu bölüm için yetkiniz yetersiz.", "Bu bo'lim uchun huquq yetarli emas.") + hint + Environment.NewLine + Environment.NewLine + diagnostics,
            Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>Название галочки доступа на сайте NurCRM (Сотрудники → доступы).</summary>
    private static string? SiteRightName(string permission) => permission switch
    {
        PosPermissions.ViewProducts => "Склад",
        PosPermissions.ViewAnalytics => "Аналитика",
        PosPermissions.ViewClients => "Клиенты",
        PosPermissions.ViewOrders => "Заказы",
        PosPermissions.ViewSettings => "Настройки",
        PosPermissions.ViewProcurement => "Закупки",
        PosPermissions.ViewSupplier => "Поставщики",
        PosPermissions.EmployeeReturn => "Возврат продаж сотрудником",
        _ => null,
    };

    // ------------------------------------------------------------------ сводка
}
