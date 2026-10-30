// 2026-10-26 (голос-управление, В4 + видеозвонок): контроллер звонка с Нур Советником. Звонок больше НЕ принадлежит странице:
// живёт в этом контроллере над всем приложением (провайдер [callControllerProvider]); экран «Разговор» (AdvisorCallScreen) и
// мини-плашка (AssistantOverlay) только показывают его состояние. Закрыть/свернуть экран ≠ завершить звонок; ИИ открывает
// раздел → звонок сворачивается в плашку, `router.push`, разговор идёт. Завершается звонок: «Завершить», лимит, потеря фокуса
// (paused/hidden — микрофон не остаётся включённым в фоне), нет связи 20 с, выход из учётной записи.
//
// Сборка по готовым движкам: один TableStore и один ActionKit на звонок, все группы инструментов в одном ToolRegistry,
// ворота намерения смотрят на расшифровку микрофона (call.speech). Режим «Спрашивать перед деньгами» — карточка «Сделать».
// Деньги и остатки проводятся только через существующие репозитории и «ручки» экранов — здесь их нет.
import 'dart:async';
import 'dart:io' show Platform;

import 'package:camera/camera.dart' show ResolutionPreset, availableCameras;
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show MissingPluginException, PlatformException;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/assistant/action_tools.dart';
import '../../core/ai/assistant/ai_handles.dart';
import '../../core/ai/assistant/ai_journal.dart';
import '../../core/ai/assistant/call_focus.dart';
import '../../core/ai/assistant/call_product_search.dart' show searchForCall;
import '../../core/ai/assistant/call_table.dart';
import '../../core/ai/assistant/lang.dart' show detectLang;
import '../../core/ai/assistant/ai_perf.dart';
import '../../core/ai/assistant/nav_router.dart';
import '../../core/ai/assistant/nav_tools.dart';
import '../../core/ai/assistant/product_wizard.dart';
import '../../core/ai/assistant/read_tools.dart';
import '../../core/ai/assistant/screen_opener.dart';
import '../../core/ai/assistant/screen_registry.dart';
import '../../core/ai/assistant/staff_tools.dart';
import '../../core/ai/assistant/tool_registry.dart';
import '../../core/ai/assistant/undo.dart';
import '../../core/ai/assistant/wizard_tools.dart';
import '../../core/ai/gemini_keys.dart' show GeminiKeyPool;
import '../../core/ai/live_call.dart';
import '../../core/ai/live_tools.dart';
import '../../core/ai/live_video.dart';
import '../../core/ai/vision.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/audit.dart';
import '../../l10n/app_localizations.dart';
import '../staff/staff_providers.dart' show staffSourceProvider;
import '../admin/analytics.dart' show analyticsRepoProvider;
import '../admin/admin_receive.dart' show ReceiveLine;
import '../checks/checks_screen.dart' show CheckRow;
import '../scan/code_lookup.dart';
import '../scan/live_shot.dart' show CameraGate;
import '../stock/product_edit.dart' show ProductEditScreen;
import '../profile/printer_settings.dart' show PrinterSettingsScreen;
import '../admin/advisor.dart' show AiSettingsScreen;
import '../admin/ai_access_ui.dart' show AiAccessScreen;
import '../admin/gemini_keys_screen.dart' show GeminiKeysScreen;
import '../profile/kassa_link_screen.dart' show KassaLinkScreen;
import 'assistant_mode.dart';
import 'call_consent.dart';
import 'call_table_pdf.dart';
import 'call_turn.dart';
import 'call_video_bridge.dart';
import 'call_wizard_backend.dart';

export 'call_turn.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Состояния и записи
// ---------------------------------------------------------------------------------------------------------------------

enum CallPhase {
  /// Звонка нет.
  idle,

  /// Готовлю данные магазина, проверяю микрофон и ключ.
  preparing,

  /// Идёт (включая «подключаюсь»).
  active,

  /// Закончился с причиной (лимит, свернули приложение, ошибка) — экран показывает её и «Повторить / Закрыть».
  problem,
}

enum CallView { full, mini }

enum PillPos { top, bottom }

/// Состояние капсулы (раздел «Мини-плашка» макетов).
enum PillState { listening, thinking, speaking, noLink, micOff }

enum ActionKind { ok, lock, bad, warn }

/// Строка итога «ИИ: … · Отменить» (8 с).
class ActionLine {
  final int id;
  final String text;
  final String? sub;
  final ActionKind kind;
  final bool undoable;
  const ActionLine(this.id, this.text, {this.sub, this.kind = ActionKind.ok, this.undoable = false});
}

class AmbiguousOption {
  final int n;
  final String title;
  final String? price;
  const AmbiguousOption(this.n, this.title, {this.price});
}

/// «Колы две — какую?»: варианты с номерами. Выбор — нажатием или голосом («вторую»).
class AmbiguousPrompt {
  final String say;
  final List<AmbiguousOption> options;
  const AmbiguousPrompt(this.say, this.options);
}

/// «Цена 120 — правильно?» (число не расслышано): две кнопки.
class NumberPrompt {
  final String say;
  final String number;
  const NumberPrompt(this.say, this.number);
}

/// Режим «Спрашивать перед деньгами»: карточка «Сделать: …».
class MoneyPrompt {
  final String what;
  final String? amount;
  final String? detail;
  final Completer<bool> _done = Completer<bool>();
  MoneyPrompt(this.what, {this.amount, this.detail});
  bool get isOpen => !_done.isCompleted;
  Future<bool> get result => _done.future;
}

/// Настройки и подмены звонка: в приложении — по умолчанию, в тестах — фейки (без сети, микрофона и камеры).
class CallDeps {
  final LiveAudio Function()? audio;
  final LiveConnect? connect;
  final LiveFrames Function()? frames;
  final Future<bool> Function()? hasCamera;
  final bool? android;
  final Duration? maxDuration;
  final NavHost? nav;
  final ActionHost? actions;
  final ReadSource? reads;
  final StaffSource? staff;
  final AiJournal? journal;

  /// 2026-10-27 (М2): справочник / ИИ по фото / создание для мастера «добавить товар» (в тестах — мок).
  final WizardBackend? wizardBackend;
  final Duration actionLife;
  final Duration noLinkLimit;
  final Duration slowAfter;

  /// 2026-10-27: локальная навигация по словам пользователя (без ИИ): через сколько тишины в расшифровке разбирать фразу; null — выключено.
  final Duration? localNavDelay;
  const CallDeps({
    this.audio,
    this.connect,
    this.frames,
    this.hasCamera,
    this.android,
    this.maxDuration,
    this.nav,
    this.actions,
    this.reads,
    this.staff,
    this.journal,
    this.wizardBackend,
    this.actionLife = const Duration(seconds: 8),
    this.noLinkLimit = const Duration(seconds: 20),
    this.slowAfter = const Duration(seconds: 4),
    this.localNavDelay = const Duration(milliseconds: 600),
  });
}

/// Что экран, начавший звонок, даёт контроллеру: сводку магазина для инструкции, запасную таблицу и подстановку кодов.
class CallRequest {
  /// Инструкция звонка. [CallPrep] — режим помощника и есть ли камера (их знает контроллер).
  final Future<String> Function(CallPrep prep) prepare;

  /// Запасной путь (модель без инструментов): таблица по вопросу с цифрами текстовым советником.
  final Future<String> Function(String question) askTable;

  /// Коды [Д1] → имена (только на экране телефона).
  final String Function(String text) reveal;
  const CallRequest({required this.prepare, required this.askTable, required this.reveal});
}

class CallPrep {
  final AssistantMode mode;
  final bool video;
  const CallPrep({required this.mode, required this.video});
}

/// Текущий адрес роутера вместе с параметрами. После `push` (ImperativeRouteMatch) `uri` роутера остаётся адресом нижней
/// страницы, поэтому берём адрес последней страницы стека.
String routerLocation(GoRouter r) {
  try {
    final cfg = r.routerDelegate.currentConfiguration;
    final last = cfg.last;
    return last is ImperativeRouteMatch ? last.matches.uri.toString() : cfg.uri.toString();
  } catch (_) {
    return '';
  }
}

final callControllerProvider = Provider<AssistantCallController>((ref) {
  final c = AssistantCallController(ref.read);
  ref.onDispose(c.dispose);
  return c;
});

// ---------------------------------------------------------------------------------------------------------------------
// Контроллер
// ---------------------------------------------------------------------------------------------------------------------

class AssistantCallController extends ChangeNotifier with WidgetsBindingObserver {
  final Reader _read;
  final CallDeps deps;
  AssistantCallController(this._read, {this.deps = const CallDeps()});

  // --- состояние для экрана
  CallPhase phase = CallPhase.idle;
  CallView view = CallView.full;
  PillPos pillPos = PillPos.top;
  String? problem;
  bool micBlocked = false;
  bool warned = false;
  LiveCall? call;
  LiveVideo? video;
  LiveFrames? frames;
  bool hasCamera = false;
  final tables = TableStore();
  final turns = <CallTurn>[];
  final _user = StringBuffer();
  final _ai = StringBuffer();
  ToolRegistry? registry;
  ActionKit? kit;

  /// 2026-10-27 (мобилка, видеозвонок, М2): «этот товар» (карточка камеры) и мастер «добавить товар».
  final focus = CallFocus();
  ProductWizard? wizard;
  AssistantMode mode = AssistantMode.defaultMode;
  CallRequest? _req;

  /// Замер объявлений при старте (шт, символов) — в журнал отладки и для проверки.
  ({int count, int chars})? toolsMeasure;

  /// Кадры/распознавание «Что это? …»: идёт ли и последняя карточка.
  VideoAsk? videoBusy;
  VideoAsk? lastAsk;
  VideoCard? videoCard;

  /// Когда пользователь перебил ответ ИИ (для метки «Перебили — слушаю» на 3 с).
  DateTime? interruptedAt;
  bool economy = false;

  // --- строка итога, уточнения, подтверждение
  ActionLine? actionLine;
  AmbiguousPrompt? ambiguous;
  NumberPrompt? numberPrompt;
  MoneyPrompt? moneyPrompt;
  int _lineId = 0;
  Timer? _lineTimer;

  // --- ожидание инструмента: «Ищу…», «Сервер отвечает медленно…»
  int _toolsInFlight = 0;
  bool toolSearching = false;
  bool toolSlow = false;
  Timer? _slowTimer;
  Timer? _pendingTimer;
  bool toolPending = false;

  // --- связь
  bool _everListening = false;
  int? _noLinkSecs; // сколько секунд нет связи (считаем по тикам, не по часам — так проверяется без ожидания)
  bool _endedByNoLink = false;
  Timer? _clock;

  // --- чат после звонка
  List<CallTurn>? _finished;
  final finishedTick = ValueNotifier<int>(0);
  final _exported = <String>{};
  final _notices = StreamController<String>.broadcast();

  /// Короткие сообщения для пользователя («Таблица добавлена в чат»): экран показывает снэкбар.
  Stream<String> get notices => _notices.stream;

  /// Высота панели таблицы (dp), которую занимает панель сейчас — экран звонка подстраивает раскладку.
  final panelInset = ValueNotifier<double>(0);

  // --- оболочка приложения
  NavHost? _nav;
  ScreenOpenerHost? _openerHost;
  GoRouter? _router;
  String _lastLocation = '';
  DateTime _navByToolAt = DateTime(2000);
  Timer? _moveTimer;
  DateTime _movedSentAt = DateTime(2000);

  /// Показать полный экран звонка (ставит оболочка: она знает навигатор и экран). Нет — звонок остаётся плашкой.
  Future<void> Function()? presentFull;
  NavigatorState? _fullNav;
  Route<void>? _fullRoute;

  // =================================================================================================================
  // оболочка: роутер, переходы
  // =================================================================================================================

  /// Роутер приложения (app.dart). Переходы ИИ идут через него: `push` без ожидания закрытия экрана.
  void attachRouter(GoRouter r) {
    if (identical(_router, r)) return;
    _router?.routerDelegate.removeListener(_onRouteChanged);
    _router = r;
    _nav = deps.nav ?? RouterNavHost(r, this);
    _openerHost = _MinimizingOpener(RouterOpenerHost(r), this, r);
    r.routerDelegate.addListener(_onRouteChanged);
    _lastLocation = _currentLocation();
  }

  NavHost? get nav => _nav ?? deps.nav;

  String _currentLocation() => _router == null ? '' : routerLocation(_router!);

  /// ИИ сам ведёт по разделам: звонок сворачивается, переход помечается как «по инструменту» (не как ручная смена экрана).
  void markNavByTool() => _navByToolAt = DateTime.now();

  void _onRouteChanged() {
    final loc = _currentLocation();
    if (loc == _lastLocation) return;
    _lastLocation = loc;
    if (phase != CallPhase.active || call == null) return;
    // раздел 5.6 ТЗ: ручная смена экрана — через 1 с тишины «[Программа] Пользователь открыл экран …», не чаще раза в 3 с
    if (DateTime.now().difference(_navByToolAt) < const Duration(milliseconds: 2500)) return;
    _moveTimer?.cancel();
    _moveTimer = Timer(const Duration(seconds: 1), () {
      final c = call;
      if (c == null || c.ended || phase != CallPhase.active) return;
      if (c.state.value == CallState.speaking) return;
      if (DateTime.now().difference(_movedSentAt) < const Duration(seconds: 3)) return;
      final text = _screenTitleOf(_lastLocation);
      if (text == null) return;
      _movedSentAt = DateTime.now();
      c.sendText('[Программа] Пользователь открыл экран «$text»', respond: false);
    });
  }

  String? _screenTitleOf(String loc) {
    final uri = Uri.tryParse(loc);
    if (uri == null) return null;
    for (final d in const ScreenRegistry().all) {
      if (d.route == uri.path) {
        final q = uri.queryParameters;
        final extra = q.isEmpty ? '' : ', ${q.entries.map((e) => '${e.key}=${e.value}').join(', ')}';
        return '${d.title(_uiLang)}$extra';
      }
    }
    return null;
  }

  // =================================================================================================================
  // язык, тексты
  // =================================================================================================================

  String get _uiLang {
    try {
      return _read(prefsProvider).locale.languageCode;
    } catch (_) {
      return 'ru';
    }
  }

  AppLocalizations get _l => _lOf(_uiLang);

  static AppLocalizations _lOf(String code) {
    try {
      return lookupAppLocalizations(Locale(code));
    } catch (_) {
      return lookupAppLocalizations(const Locale('ru'));
    }
  }

  Session? get _session {
    try {
      return _read(sessionProvider);
    } catch (_) {
      return null;
    }
  }

  AppDatabase? get _db {
    try {
      return _read(databaseProvider);
    } catch (_) {
      return null;
    }
  }

  /// Язык ответа программы: по последней реплике пользователя, иначе язык интерфейса.
  String _langNow() => detectLang(call?.speech.last?.text ?? '', fallback: _uiLang);

  /// Имена вместо кодов [Д1] для экрана: сперва то, что дал экран-инициатор, потом имена из таблиц.
  String reveal(String text) => tables.reveal(_req?.reveal(text) ?? text);

  String get liveUser => _user.toString();
  String get liveAi => _ai.toString();

  bool get isActive => phase == CallPhase.active || phase == CallPhase.preparing;
  bool get hasCall => phase != CallPhase.idle;

  // =================================================================================================================
  // старт
  // =================================================================================================================

  /// Начать звонок. Идёт уже — просто разворачивает его. Ошибки (нет микрофона, ключа, платформа) — в [problem].
  Future<void> start(CallRequest req) async {
    if (phase == CallPhase.active || phase == CallPhase.preparing) {
      view = CallView.full;
      notifyListeners();
      return;
    }
    _req = req;
    _resetForCall();
    phase = CallPhase.preparing;
    view = CallView.full;
    notifyListeners();
    final l = _l;
    final android = deps.android ?? (!kIsWeb && Platform.isAndroid);
    if (deps.audio == null && !android) {
      _fail(l.callUnsupported);
      return;
    }
    try {
      if (deps.audio == null) {
        var mic = await PlatformLiveAudio.micPermission();
        if (mic != 'granted') mic = await PlatformLiveAudio.requestMic();
        if (mic != 'granted') {
          micBlocked = true;
          _fail(l.callNoMic);
          return;
        }
      }
      final vision = _read(visionProvider);
      final key = await vision.key();
      if (key == null) {
        _fail(l.advisorNoKey);
        return;
      }
      final session = _session;
      if (session == null) {
        _fail(l.callFailed('нет входа'));
        return;
      }
      final db = _db;
      mode = db == null ? AssistantMode.defaultMode : await AssistantModePrefs.load(db, session.userId);
      hasCamera = android || deps.hasCamera != null ? await (deps.hasCamera ?? _defaultHasCamera)() : false;
      final instruction = await req.prepare(CallPrep(mode: mode, video: hasCamera));
      if (phase != CallPhase.preparing) return; // отменили, пока готовили данные

      final built = _buildTools(session);
      final decls = built.reg.declarations(session, mode);
      toolsMeasure = (count: decls.length, chars: ToolDecl.sizeOf(decls));
      // 2026-10-26 (голос-управление, В4): замер объявлений при старте (владелец ≈ 21 тыс. симв. / 53 инструмента) — в журнал
      // отладки. Ленивое «ядро + расширение» не делается: если на устройстве объявления тормозят старт — отдельная задача.
      if (kDebugMode) {
        debugPrint('[assistant] объявления инструментов: ${toolsMeasure!.count} шт, ${toolsMeasure!.chars} симв.; режим ${mode.wire}');
      }
      registry = built.reg;
      kit = built.kit;
      economy = db == null ? false : await VideoPrefs.economy(db);

      ToolHandler? handler;
      final c = LiveCall(
        apiKey: key,
        instruction: instruction,
        audio: deps.audio?.call() ?? PlatformLiveAudio(),
        connect: deps.connect,
        maxDuration: deps.maxDuration ?? const Duration(minutes: 10),
        tools: decls,
        onToolCall: (tc) => handler!(tc),
        video: hasCamera,
      );
      call = c;
      final inner = built.reg.handler(
        session: () => _read(sessionProvider) ?? session,
        speech: c.speech,
        mode: () => mode,
        lang: _langNow,
        screen: () => nav?.location ?? '',
        confirm: mode == AssistantMode.askMoney ? _confirm : null,
      );
      handler = (tc) => _onTool(inner, tc);

      c.onUserText = (t) {
        _user.write(t);
        notifyListeners();
      };
      c.onAiText = (t) {
        _ai.write(t);
        notifyListeners();
      };
      c.onTurnDone = _turnDone;
      c.onUserSpeech = _onUserSpeech;
      c.onWarning = () {
        warned = true;
        notifyListeners();
      };
      c.onKeyRejected = vision.rotateLiveKey; // 2026-10-27: дополнительные ключи Gemini — при отказе по лимиту берём следующий
      c.onEnded = (why, detail) => _onEnded(why, detail, c.apiKey);
      c.onToolsRejected = (reason) {
        if (kDebugMode) debugPrint('[assistant] модель отвергла инструменты — запасной путь');
        c.sendText('[Программа] Инструментов в этом звонке нет. Открывать разделы и менять данные ты не можешь — говори только то, что есть в данных.', respond: false);
        notifyListeners();
      };
      c.onVideoRejected = (reason) => notifyListeners();
      c.onVideoStats = (line) => unawaited(Audit.log('ai', line));
      // расход токенов за звонок — одной строкой (без ключей и текста речи) в журнал
      c.onUsageLine = (line) => unawaited(Audit.log('ai', line));
      c.state.addListener(_onCallState);
      c.videoActive.addListener(notifyListeners);

      if (hasCamera) {
        frames = deps.frames?.call() ?? CameraLiveFrames();
        video = LiveVideo(call: c, frames: frames!, economy: economy);
        video!.state.addListener(notifyListeners);
        video!.lastSentAt.addListener(notifyListeners);
      }
      tables.onExport = exportTable;
      WidgetsBinding.instance.addObserver(this);
      _clock = Timer.periodic(const Duration(seconds: 1), (_) => _tick());
      phase = CallPhase.active;
      notifyListeners();
      await c.start();
    } on AiAccessDenied catch (e) {
      _fail(e.message);
    } on PlatformException catch (e) {
      _fail(l.callFailed(e.message ?? e.code));
    } on MissingPluginException {
      _fail(l.callUnsupported);
    } catch (e) {
      _fail(l.callFailed('$e'));
    }
  }

  static Future<bool> _defaultHasCamera() async {
    try {
      return (await availableCameras()).isNotEmpty;
    } catch (_) {
      return false;
    }
  }

  void _resetForCall() {
    turns.clear();
    _user.clear();
    _ai.clear();
    problem = null;
    micBlocked = false;
    warned = false;
    videoBusy = null;
    videoCard = null;
    focus.clear();
    wizard?.reset();
    actionLine = null;
    ambiguous = null;
    numberPrompt = null;
    moneyPrompt = null;
    toolSearching = toolSlow = toolPending = false;
    _toolsInFlight = 0;
    _everListening = false;
    _noLinkSecs = null;
    _endedByNoLink = false;
    _exported.clear();
    tables.reset();
    panelInset.value = 0;
    kit?.undo.clear();
  }

  void _fail(String text) {
    problem = text;
    phase = CallPhase.problem;
    view = CallView.full;
    notifyListeners();
  }

  ({ToolRegistry reg, ActionKit kit}) _buildTools(Session session) {
    final reg = ToolRegistry(hideActionsInViewOnly: true);
    final navHost = nav ?? _FallbackNav();
    registerNavTools(reg, navHost);
    registerReadTools(reg, ReadContext(deps.reads ?? RepoReadSource.fromReader(_read), tables, focus: focus));
    final opener = _openerHost == null ? null : buildScreenOpener(host: _openerHost!, read: _read);
    final host = deps.actions ??
        RepoActionHost.fromReader(
          _read,
          productSales: (f, t) => _read(analyticsRepoProvider).products(f, t),
          opener: opener,
        );
    // 2026-10-27 (М2): мастер «добавить товар» нужен только со включаемой камерой; черновик живёт в памяти звонка
    ProductWizard? wiz;
    if (hasCamera) {
      wiz = ProductWizard(host: host, backend: deps.wizardBackend ?? RepoWizardBackend(_read), camera: _wizardStill, cameraOn: () => video?.cameraOn == true, focus: focus);
      wiz.addListener(notifyListeners);
    }
    wizard?.dispose();
    wizard = wiz;
    final k = ActionKit(
      host: host,
      undo: UndoStack(),
      journal: deps.journal ?? AiJournal(),
      handles: _safeHandles(),
      tables: tables,
      focus: focus,
      wizard: wiz,
      model: null,
    );
    registerActionTools(reg, k);
    if (wiz != null) registerWizardTools(reg, k, wiz);
    // 2026-10-26 (В7): настоящий источник — задачи, табель, зарплата, выплата и схема оплаты (раньше был только список сотрудников)
    registerStaffTools(reg, k, deps.staff ?? _read(staffSourceProvider), tables: tables);
    for (final t in _serviceTools()) {
      reg.register(t);
    }
    return (reg: reg, kit: k);
  }

  AiScreenHandles _safeHandles() {
    try {
      return _read(aiHandlesProvider);
    } catch (_) {
      return AiScreenHandles();
    }
  }

  /// Служебные инструменты звонка: `call_view` (развернуть/свернуть) и `end_call` («до свидания», закрывается через 2 с).
  List<AssistantTool> _serviceTools() => [
    AssistantTool(
      decl: ToolDecl(
        'call_view',
        'Expand or minimize this call screen (ru: разверни/сверни звонок, ky: чалууну жый, uz: qo\'ng\'iroqni yig\', en: minimize the call, tr: görüşmeyi küçült).',
        parameters: sObj({'expand': sBool(desc: 'true to expand, false to minimize')}, required: ['expand']),
      ),
      group: ToolGroup.service,
      run: (r) async {
        final expand = r.args['expand'] == true;
        if (expand) {
          unawaited(this.expand());
        } else {
          minimize();
        }
        final lang = r.env.lang;
        return ToolResult(ToolStatus.ok, expand ? tr5(lang, 'Развернула звонок.', 'Чалууну ачтым.', 'Qo\'ng\'iroqni ochdim.', 'Expanded the call.', 'Görüşmeyi açtım.') : tr5(lang, 'Свернула звонок.', 'Чалууну жыйдым.', 'Qo\'ng\'iroqni yig\'dim.', 'Minimized the call.', 'Görüşmeyi küçülttüm.'));
      },
    ),
    AssistantTool(
      decl: const ToolDecl(
        'end_call',
        'End the call when the user says goodbye (ru: пока, до свидания, заверши звонок; ky: саламатта бол; uz: xayr; en: goodbye, hang up; tr: hoşça kal). Say a short goodbye; the call closes in 2 seconds.',
      ),
      group: ToolGroup.service,
      run: (r) async {
        Timer(const Duration(seconds: 2), () => call?.end());
        return ToolResult(ToolStatus.ok, tr5(r.env.lang, 'До свидания!', 'Саламатта бол!', 'Xayr!', 'Goodbye!', 'Hoşça kalın!'));
      },
    ),
  ];

  // =================================================================================================================
  // инструменты: обёртка для экрана
  // =================================================================================================================

  Future<Map<String, dynamic>> _onTool(ToolHandler inner, LiveToolCall tc) async {
    _toolsInFlight++;
    if (_toolsInFlight == 1) {
      toolSearching = true;
      toolSlow = false;
      toolPending = false;
      _slowTimer?.cancel();
      _pendingTimer?.cancel();
      _slowTimer = Timer(deps.slowAfter, () {
        if (_toolsInFlight > 0) {
          toolSlow = true;
          notifyListeners();
        }
      });
      _pendingTimer = Timer(deps.slowAfter * 2, () {
        if (_toolsInFlight > 0) {
          toolPending = true;
          notifyListeners();
        }
      });
      notifyListeners();
    }
    try {
      final r = await inner(tc);
      _afterTool(tc, r);
      return r;
    } finally {
      _toolsInFlight--;
      if (_toolsInFlight <= 0) {
        _toolsInFlight = 0;
        toolSearching = toolSlow = toolPending = false;
        _slowTimer?.cancel();
        _pendingTimer?.cancel();
        notifyListeners();
      }
    }
  }

  void _afterTool(LiveToolCall tc, Map<String, dynamic> r) {
    if (phase != CallPhase.active) return;
    final tool = registry?[tc.name];
    final status = '${r['status'] ?? ''}';
    final say = '${r['say'] ?? ''}'.trim();
    final undo = r['undo'] == true;
    final lang = _langNow();
    if (status != ToolStatus.ambiguous.wire) ambiguous = null;
    if (status != ToolStatus.clarify.wire) numberPrompt = null;

    ActionLine? line;
    if (status == ToolStatus.ok.wire && tool != null) {
      final isUndoTool = tc.name == 'undo_last';
      // 2026-10-27 (М2): шаги мастера «добавить товар» (старт, кадр, поля) строкой итога не показываем — состояние видно в панели
      if ((tool.isAction && tc.name != 'product_wizard') || isUndoTool) {
        final already = say.startsWith(toolMsg('already', lang));
        final irreversible = tool.isAction && !undo && !already && !isUndoTool;
        line = ActionLine(
          ++_lineId,
          irreversible ? '$say · ${_lOf(lang).aiCantUndo}' : say,
          sub: _subOf(),
          kind: already
              ? ActionKind.lock
              : (isUndoTool || undo ? ActionKind.ok : ActionKind.lock),
          undoable: undo && !already && !isUndoTool,
        );
      } else if (tool.group == ToolGroup.nav) {
        line = ActionLine(++_lineId, say, sub: _subOf(), kind: ActionKind.ok);
      }
    } else if (status == ToolStatus.denied.wire || status == ToolStatus.viewOnly.wire) {
      line = ActionLine(++_lineId, say, sub: _subOf(), kind: ActionKind.bad);
    } else if (status == ToolStatus.error.wire && tool != null && (tool.isAction || tool.group == ToolGroup.nav)) {
      line = ActionLine(++_lineId, say, sub: _subOf(), kind: ActionKind.bad);
    } else if (status == ToolStatus.ambiguous.wire) {
      final opts = r['options'];
      if (opts is List && opts.isNotEmpty) {
        ambiguous = AmbiguousPrompt(say, [
          for (final o in opts)
            if (o is Map)
              AmbiguousOption(
                (o['n'] as num?)?.toInt() ?? 0,
                '${o['title'] ?? ''}',
                price: o['price'] is num ? CallTable.formatCell(o['price'], CellKind.money, lang) : null,
              ),
        ]);
      }
    } else if (status == ToolStatus.clarify.wire) {
      final m = RegExp(r'\d[\d.,]*').firstMatch(say);
      if (m != null && toolMsg('confirmNumber', lang, {'n': m.group(0)!}) == say) {
        numberPrompt = NumberPrompt(say, m.group(0)!);
      } else {
        line = ActionLine(++_lineId, say, kind: ActionKind.warn);
      }
    }
    if (line != null) _showLine(line);
    notifyListeners();
  }

  String? _subOf() {
    final said = call?.speech.last?.text.trim() ?? '';
    final t = DateTime.now();
    final hh = '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
    if (said.isEmpty) return hh;
    return '«${said.length > 60 ? '${said.substring(0, 59)}…' : said}» · $hh';
  }

  void _showLine(ActionLine line) {
    actionLine = line;
    _lineTimer?.cancel();
    _lineTimer = Timer(deps.actionLife, () {
      if (actionLine?.id == line.id) {
        actionLine = null;
        notifyListeners();
      }
    });
    notifyListeners();
  }

  void dismissLine() {
    _lineTimer?.cancel();
    actionLine = null;
    notifyListeners();
  }

  /// «Отменить» с экрана — то же, что голосом «отмени последнее»; ИИ узнаёт итог и говорит его.
  Future<void> undoFromUi() async {
    final k = kit;
    final s = _session;
    if (k == null || s == null) return;
    dismissLine();
    final lang = _langNow();
    final say = await k.undoLast(s, lang);
    if (say.isNotEmpty) _showLine(ActionLine(++_lineId, say, sub: _subOf(), kind: ActionKind.ok));
    final c = call;
    if (c != null && !c.ended && say.isNotEmpty) {
      c.sendText('[Программа] Пользователь нажал «Отменить». Итог: $say', respond: true);
    }
  }

  // --- уточнения кнопками

  void pickOption(int n) {
    final c = call;
    if (c == null || c.ended) return;
    final p = ambiguous;
    ambiguous = null;
    c.speech
      ..closeTurn()
      ..add('$n');
    c.speech.closeTurn();
    c.sendText('[Программа] Пользователь выбрал вариант $n${p == null ? '' : ' из ${p.options.length}'}. Повтори тот же вызов с pick: $n.', respond: true);
    notifyListeners();
  }

  void dismissAmbiguous() {
    ambiguous = null;
    notifyListeners();
  }

  void answerNumber(bool yes) {
    final c = call;
    final p = numberPrompt;
    numberPrompt = null;
    notifyListeners();
    if (c == null || c.ended || p == null) return;
    if (yes) {
      final word = tr5(_uiLang, 'да', 'ооба', 'ha', 'yes', 'evet');
      c.speech
        ..closeTurn()
        ..add('$word ${p.number}');
      c.speech.closeTurn();
      c.sendText('[Программа] Пользователь нажал «Да, ${p.number}» — число верное. Повтори действие.', respond: true);
    } else {
      c.sendText('[Программа] Пользователь нажал «Нет» — число неверное. Попроси назвать число ещё раз и ничего не меняй.', respond: true);
    }
  }

  // --- «Спрашивать перед деньгами»

  Future<bool> _confirm(AssistantTool tool, Map<String, dynamic> args) {
    final k = kit;
    final l = _l;
    final p = _moneyPromptFor(tool, args, l, k);
    moneyPrompt?._done.isCompleted == false ? moneyPrompt!._done.complete(false) : null;
    moneyPrompt = p;
    notifyListeners();
    final timer = Timer(const Duration(seconds: 40), () => answerMoney(false));
    return p.result.whenComplete(timer.cancel);
  }

  void answerMoney(bool yes) {
    final p = moneyPrompt;
    if (p == null || !p.isOpen) return;
    p._done.complete(yes);
    moneyPrompt = null;
    notifyListeners();
  }

  MoneyPrompt _moneyPromptFor(AssistantTool tool, Map<String, dynamic> args, AppLocalizations l, ActionKit? k) {
    String? amount;
    String? detail;
    double? n(Object? v) => v is num ? v.toDouble() : double.tryParse('$v'.replaceAll(',', '.'));
    String money(double v) => CallTable.formatCell(v, CellKind.money, _uiLang).replaceAll(' ', ' ');
    final what = switch (tool.name) {
      'checkout' || 'pay' => l.aiWhatPay,
      'return_sale' || 'return' => l.aiWhatReturn,
      'receive_from_supplier' || 'receive_simple' || 'receive' => l.aiWhatReceive,
      'write_off' => l.aiWhatWriteOff,
      'stock_count' => l.aiWhatCount,
      'close_shift' => l.aiWhatCloseShift,
      'salary_payout' => l.aiWhatSalary,
      'pay_debt' => l.aiWhatDebt,
      'delete_product' => l.aiWhatDelete,
      'wizard_create' => l.wizCreate,
      'set_price' || 'bulk_markup' => l.aiWhatPrice,
      'cash_in_out' => l.aiWhatCash,
      _ => l.aiWhatOther,
    };
    if (tool.name == 'checkout') {
      final total = k?.host.cart.total;
      if (total != null) amount = money(total);
      final method = '${args['method'] ?? ''}';
      detail = switch (method) {
        'cash' => l.cash,
        'card' || 'transfer' => l.card,
        'debt' => l.debt,
        'qr' => 'QR',
        _ => null,
      };
      final given = n(args['cash_given']);
      if (given != null && given > 0) detail = '${detail ?? ''}${detail == null ? '' : ' · '}${money(given)}';
    } else {
      for (final key in const ['amount', 'counted_cash', 'price', 'cash_given', 'percent']) {
        final v = n(args[key]);
        if (v != null) {
          amount = key == 'percent' ? '${v == v.roundToDouble() ? v.toStringAsFixed(0) : v} %' : money(v);
          break;
        }
      }
    }
    return MoneyPrompt(what, amount: amount, detail: detail);
  }

  // =================================================================================================================
  // реплики
  // =================================================================================================================

  // --- 2026-10-27: локальная навигация — первый путь («открой приёмку» не ждёт ИИ и не зависит от его выбора раздела)
  Timer? _localNavTimer;
  String _localNavDone = '';

  void _scheduleLocalNav() {
    final delay = deps.localNavDelay;
    if (delay == null) return;
    _localNavTimer?.cancel();
    _localNavTimer = Timer(delay, () => unawaited(_localNavNow()));
  }

  /// Фраза пользователя целиком (после паузы) разобрана как ЯСНАЯ просьба открыть раздел → открываем сами теми же инструментами
  /// (право, проверка открытия, журнал). ИИ получает справку «раздел уже открыт» и не обязан вызывать open_screen: если вызовет
  /// — получит «открыла» (а не «уже открыт»). Неясное, с ничьей или с остатком слов — по-прежнему решает ИИ.
  Future<void> _localNavNow() async {
    final c = call;
    final reg = registry;
    final s = _session;
    if (c == null || c.ended || reg == null || s == null || phase != CallPhase.active) return;
    final turn = c.speech.last;
    if (turn == null || turn.echoSuspect) return;
    final said = turn.text.trim();
    if (said.isEmpty || said == _localNavDone) return;
    // ИИ уже сам открыл раздел в этой реплике (или ещё работает инструмент) — его выбор может быть точнее (фильтр, период), не перебиваем
    if (_toolsInFlight > 0 || _navByToolAt.isAfter(turn.at)) return;
    final sw = Stopwatch()..start();
    final d = parseNavCommand(said, session: s);
    final parseMs = sw.elapsedMilliseconds;
    final def = d.def;
    if (d.kind != NavKind.open || !d.confident || def == null || !def.allowedFor(s)) return;
    _localNavDone = said;
    final lang = _langNow();
    final env = ToolEnv(session: s, speech: c.speech, mode: mode, lang: lang, screen: nav?.location ?? '');
    final res = await reg.execute(LiveToolCall('loc${DateTime.now().microsecondsSinceEpoch}', 'open_screen', {'screen': def.key, ...d.args}), env);
    (AiPerfTrace('call.nav')
          ..tag('screen', def.key)
          ..tag('ok', '${res.status == ToolStatus.ok}')
          ..tag('path', 'local')
          ..ms('parse', parseMs)
          ..ms('open', sw.elapsedMilliseconds - parseMs))
        .finish();
    if (res.status == ToolStatus.ok && !c.ended) {
      c.sendText('[Программа] По просьбе пользователя программа уже открыла «${def.fullTitle(lang)}». Скажи об этом одной короткой фразой; open_screen повторно вызывать не нужно.', respond: false);
    }
  }

  void _onUserSpeech(String text, bool echoSuspect) {
    if (echoSuspect) return;
    _scheduleLocalNav();
    final p = moneyPrompt;
    if (p != null && p.isOpen) {
      final t = _speechNorm(text);
      if (_isYes(t)) {
        answerMoney(true);
      } else if (_isNo(t)) {
        answerMoney(false);
      }
    }
  }

  static String _speechNorm(String s) => s.toLowerCase().replaceAll('ё', 'е').replaceAll(RegExp(r"[^\p{L}\p{N}' ]", unicode: true), ' ').replaceAll(RegExp(r'\s+'), ' ').trim();

  static bool _isYes(String t) {
    if (t.isEmpty || t.split(' ').length > 3) return false;
    const yes = {'да', 'ага', 'давай', 'делай', 'подтверждаю', 'ок', 'окей', 'хорошо', 'ооба', 'жарайт', 'макул', 'ha', 'xop', "xo'p", 'mayli', 'yes', 'ok', 'okay', 'sure', 'evet', 'tamam', 'yap'};
    return t.split(' ').any(yes.contains);
  }

  static bool _isNo(String t) {
    if (t.isEmpty || t.split(' ').length > 3) return false;
    const no = {'нет', 'не', 'отмена', 'отмени', 'жок', "yo'q", 'yoq', 'no', 'hayir', 'hayır', 'cancel', 'vazgeç', 'stop'};
    return t.split(' ').any(no.contains);
  }

  void _turnDone(bool interrupted) {
    if (interrupted && phase == CallPhase.active) interruptedAt = DateTime.now();
    final said = _user.toString().trim();
    final answer = _ai.toString().trim();
    _user.clear();
    _ai.clear();
    if (said.isEmpty && answer.isEmpty) {
      notifyListeners();
      return;
    }
    if (said.isNotEmpty) turns.add(CallTurn(true, said));
    if (answer.isNotEmpty) turns.add(CallTurn(false, interrupted ? '$answer …' : answer));
    final c = call;
    if (!interrupted && said.isNotEmpty && c != null && !c.ended) {
      final ask = hasCamera && video?.cameraOn == true ? videoIntent(said) : null;
      if (ask != null) {
        // фраза «что это?» при включённой камере = нажатие кнопки; таблица для этой реплики не нужна
        unawaited(askVideo(ask));
      } else if (!c.toolsSupported) {
        unawaited(_fallbackTurn(said));
      }
    }
    notifyListeners();
  }

  /// Запасной путь (модель без инструментов, раздел 3.7 ТЗ): «открой склад» — по словам (только при явном «открой / ач / och /
  /// open»), вопрос с цифрами — таблица текстовым советником сразу на экране. Действия не выполняются.
  Future<void> _fallbackTurn(String said) async {
    final c = call;
    final reg = registry;
    final s = _session;
    if (c == null || reg == null || s == null) return;
    final nw = const ScreenRegistry().navigateByWords(said);
    if (nw != null && nw.def.allowedFor(s)) {
      final env = ToolEnv(session: s, speech: c.speech, mode: mode, lang: _langNow(), screen: nav?.location ?? '');
      final res = await reg.execute(LiveToolCall('fb${DateTime.now().microsecondsSinceEpoch}', 'open_screen', {'screen': nw.def.key, ...nw.args}), env);
      if (!c.ended) c.sendText('[Программа] ${res.say}', respond: true);
      return;
    }
    if (looksLikeDataQuestion(said) && _req != null) {
      try {
        final table = await _req!.askTable(said);
        if (table.trim().isEmpty || c.ended) return;
        turns.add(CallTurn(false, table, table: true));
        c.sendText('[Программа] На экране таблица по вопросу пользователя (вслух не зачитывай, просто запомни): ${table.length > 900 ? '${table.substring(0, 900)}…' : table}');
        notifyListeners();
      } catch (_) {}
    }
  }

  // =================================================================================================================
  // состояние звонка, часы
  // =================================================================================================================

  void _onCallState() {
    final c = call;
    if (c == null) return;
    final st = c.state.value;
    if (st == CallState.listening || st == CallState.speaking || st == CallState.thinking) {
      _everListening = true;
      _noLinkSecs = null;
    } else if (st == CallState.connecting && _everListening) {
      _noLinkSecs ??= 0;
    }
    notifyListeners();
  }

  /// Сколько секунд осталось до конца звонка без связи; null — связь есть.
  int? get noLinkLeft {
    final secs = _noLinkSecs;
    if (secs == null || phase != CallPhase.active) return null;
    final left = deps.noLinkLimit.inSeconds - secs;
    return left < 0 ? 0 : left;
  }

  void _tick() {
    final c = call;
    if (c != null && !c.ended && _noLinkSecs != null) {
      _noLinkSecs = _noLinkSecs! + 1;
      if (_noLinkSecs! >= deps.noLinkLimit.inSeconds) {
        _endedByNoLink = true;
        c.end(CallEnd.error);
        return;
      }
    }
    notifyListeners();
  }

  PillState get pillState {
    final c = call;
    if (c == null) return PillState.listening;
    if (noLinkLeft != null) return PillState.noLink;
    if (c.muted) return PillState.micOff;
    return switch (c.state.value) {
      CallState.speaking => PillState.speaking,
      CallState.thinking => PillState.thinking,
      _ => toolSearching ? PillState.thinking : PillState.listening,
    };
  }

  Duration get elapsed => call == null ? Duration.zero : DateTime.now().difference(call!.started);
  Duration get total => call?.totalDuration ?? deps.maxDuration ?? const Duration(minutes: 10);

  // =================================================================================================================
  // конец звонка
  // =================================================================================================================

  void _onEnded(CallEnd why, String? detail, String key) {
    _turnDone(true);
    _clock?.cancel();
    _lineTimer?.cancel();
    _slowTimer?.cancel();
    _pendingTimer?.cancel();
    _moveTimer?.cancel();
    final c = call;
    c?.state.removeListener(_onCallState);
    c?.videoActive.removeListener(notifyListeners);
    WidgetsBinding.instance.removeObserver(this);
    // 2026-10-27: Google явно отклонил ключ — только временная пометка на 30 минут (rotateLiveKey), а не «навсегда» (markBadKey):
    // раньше после одного звонка общий облачный ключ оставался плохим на этом телефоне даже после перезапуска.
    if (detail != null && GeminiKeyPool.isInvalidKeyText(detail)) {
      try {
        _read(visionProvider).rotateLiveKey(key, detail);
      } catch (_) {}
    }
    final v = video;
    video = null;
    if (v != null) {
      v.state.removeListener(notifyListeners);
      v.lastSentAt.removeListener(notifyListeners);
      unawaited(v.dispose());
    }
    // таблицы звонка — в чат (не те, что уже отправлены кнопкой «В чат»)
    for (final t in tables.history) {
      if (_exported.contains(t.id)) continue;
      turns.add(CallTurn(false, t.toPlainText(revealNames: false), table: true, display: t.toPlainText()));
    }
    tables.reset();
    panelInset.value = 0;
    kit?.undo.clear();
    ambiguous = null;
    numberPrompt = null;
    actionLine = null;
    final p = moneyPrompt;
    moneyPrompt = null;
    if (p != null && p.isOpen) p._done.complete(false);
    _finished = List.of(turns);
    finishedTick.value++;

    final l = _l;
    switch (why) {
      case CallEnd.user:
        phase = CallPhase.idle;
        _closeFullRoute();
      case CallEnd.limit:
        _problemEnd(l.callEndedLimit(total.inMinutes));
      case CallEnd.focus:
        _problemEnd(l.callEndedFocus);
      case CallEnd.error:
        if (_endedByNoLink) {
          _problemEnd(l.callNoLinkWork);
        } else if (c?.quotaExceeded == true) {
          _problemEnd(videoMsg('quota', _uiLang));
        } else {
          _problemEnd(l.callFailed(detail ?? ''));
        }
    }
    notifyListeners();
  }

  void _problemEnd(String text) {
    problem = text;
    phase = CallPhase.problem;
    view = CallView.full;
    unawaited(expand());
  }

  /// Беседа для чата (один раз): реплики и таблицы закончившегося звонка.
  List<CallTurn>? takeFinished() {
    final f = _finished;
    _finished = null;
    return f == null || f.isEmpty ? null : f;
  }

  /// 2026-10-29 (полная изоляция аккаунтов): вошёл другой аккаунт — реплики, таблицы и «этот товар» прошлого звонка стираются.
  void resetForAccountSwitch() {
    _finished = null;
    turns.clear();
    tables.reset();
    focus.clear();
    wizard = null;
    videoBusy = null;
    lastAsk = null;
    videoCard = null;
    ambiguous = null;
    numberPrompt = null;
    actionLine = null;
    problem = null;
    kit?.undo.clear();
    finishedTick.value++;
    notifyListeners();
  }

  /// Закрыть сообщение о конце звонка (кнопка «Закрыть») — звонка больше нет.
  void dismiss() {
    if (phase == CallPhase.active || phase == CallPhase.preparing) return;
    phase = CallPhase.idle;
    problem = null;
    _closeFullRoute();
    notifyListeners();
  }

  Future<void> retry() async {
    final r = _req;
    if (r == null) return;
    phase = CallPhase.idle;
    await start(r);
  }

  /// Завершить звонок (кнопка «Завершить», плашка). Идёт подготовка — отменяет её.
  void endCall() {
    final c = call;
    if (phase == CallPhase.preparing) {
      phase = CallPhase.idle;
      _closeFullRoute();
      notifyListeners();
      return;
    }
    if (c != null && !c.ended) {
      c.end();
    } else {
      dismiss();
    }
  }

  // =================================================================================================================
  // жизненный цикл приложения
  // =================================================================================================================

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    final s = state;
    final c = call;
    if (c == null || c.ended) return;
    // свернули приложение / погас экран — звонок завершается (микрофон не остаётся включённым в фоне); шторка и системные
    // окна (inactive) звонок не рвут — камера отпускается и возвращается сама
    if (s == AppLifecycleState.paused || s == AppLifecycleState.hidden) {
      unawaited(video?.onLifecycle(s));
      c.end(CallEnd.focus);
      return;
    }
    unawaited(video?.onLifecycle(s));
  }

  // =================================================================================================================
  // вид: полный экран ↔ плашка
  // =================================================================================================================

  /// Свернуть в плашку: полный экран закрывается СРАЗУ (до следующего перехода), разговор продолжается.
  void minimize() {
    if (view != CallView.mini) view = CallView.mini;
    _closeFullRoute();
    notifyListeners();
  }

  Future<void> expand() async {
    view = CallView.full;
    notifyListeners();
    final p = presentFull;
    if (p != null) await p();
  }

  /// Полный экран показан маршрутом [route] в навигаторе [nav]; когда его закроют — звонок станет плашкой.
  Future<void> showFull(NavigatorState nav, WidgetBuilder builder) async {
    if (_fullRoute != null) return;
    final route = MaterialPageRoute<void>(builder: builder, settings: const RouteSettings(name: 'assistant-call'));
    _fullNav = nav;
    _fullRoute = route;
    view = CallView.full;
    notifyListeners();
    await nav.push<void>(route);
    if (identical(_fullRoute, route)) {
      _fullRoute = null;
      _fullNav = null;
      if (phase == CallPhase.active || phase == CallPhase.preparing) {
        view = CallView.mini;
      } else if (phase == CallPhase.problem) {
        // закрыли сообщение об ошибке — звонка больше нет
        phase = CallPhase.idle;
        problem = null;
      }
      notifyListeners();
    }
  }

  bool get fullShown => _fullRoute != null;

  void _closeFullRoute() {
    final r = _fullRoute;
    final n = _fullNav;
    if (r == null || n == null) return;
    _fullRoute = null;
    _fullNav = null;
    try {
      if (r.isCurrent) {
        n.pop();
      } else if (r.isActive) {
        n.removeRoute(r);
      }
    } catch (_) {}
  }

  Future<void> setPillPos(PillPos p) async {
    if (pillPos == p) return;
    pillPos = p;
    notifyListeners();
    try {
      await _db?.setSetting('assistant.pill.pos', p.name);
    } catch (_) {}
  }

  Future<void> loadPillPos() async {
    try {
      final v = await _db?.getSetting('assistant.pill.pos');
      if (v == 'bottom') pillPos = PillPos.bottom;
    } catch (_) {}
  }

  // =================================================================================================================
  // микрофон, динамик, лимит
  // =================================================================================================================

  Future<void> toggleMute() async {
    final c = call;
    if (c == null || c.ended) return;
    await c.setMuted(!c.muted);
    notifyListeners();
  }

  Future<void> toggleSpeaker() async {
    final c = call;
    if (c == null || c.ended) return;
    await c.setSpeaker(!c.speaker);
    notifyListeners();
  }

  void interrupt() => call?.interrupt();

  /// «Ещё 10 минут» (вопрос 6 ТЗ).
  void extend() {
    final c = call;
    if (c == null || c.ended) return;
    c.extend(const Duration(minutes: 10));
    warned = false;
    _notices.add(_l.callExtended);
    notifyListeners();
  }

  // =================================================================================================================
  // таблицы: В чат, PDF, Поделиться
  // =================================================================================================================

  Future<bool> exportTable(String action, CallTable t) async {
    switch (action) {
      case 'export_chat':
        if (_exported.add(t.id)) {
          turns.add(CallTurn(false, t.toPlainText(revealNames: false), table: true, display: t.toPlainText()));
          // ИИ знает: таблица уже в чате (дальше о ней не заботится)
        }
        _notices.add(_l.tblSentChat);
        return true;
      case 'export_pdf':
        final ok = await shareCallTablePdf(t, shop: _session?.companyName ?? '');
        if (!ok) _notices.add(_l.tblPdfFailed);
        return ok;
      case 'share':
        return shareCallTableText(t);
    }
    return false;
  }

  // =================================================================================================================
  // видео: камера, согласие, «Что это? / Накладная / Полка / Ценник»
  // =================================================================================================================

  Future<bool> needsConsent() async {
    final db = _db;
    final id = _session?.userId ?? '';
    if (db == null) return false;
    return !await VideoConsent.has(db, id);
  }

  Future<void> acceptConsent() async {
    final db = _db;
    if (db != null) await VideoConsent.accept(db, _session?.userId ?? '');
  }

  Future<void> startCamera() async {
    final v = video;
    if (v == null) return;
    await v.start();
    notifyListeners();
  }

  Future<void> stopCamera() async {
    await video?.stop();
    notifyListeners();
  }

  Future<void> switchCamera() async {
    final v = video;
    if (v == null) return;
    await v.switchCamera();
    final key = v.takeToastKey();
    if (key != null) _notices.add(videoMsg(key, _uiLang));
    notifyListeners();
  }

  Future<void> toggleTorch() async {
    final v = video;
    if (v == null) return;
    await v.setTorch(!v.torch);
    notifyListeners();
  }

  Future<void> setEconomy(bool on) async {
    economy = on;
    video?.economy = on;
    final db = _db;
    if (db != null) await VideoPrefs.setEconomy(db, on);
    notifyListeners();
  }

  bool get canStock => _session?.can(Session.viewProducts) ?? false;
  bool get canProcure => _session?.canProcure ?? false;

  // =================================================================================================================
  // 2026-10-27 (мобилка, видеозвонок, М2): мастер «добавить товар» — кнопки экрана ведут тот же мастер, что и голос
  // =================================================================================================================

  /// Кнопка «Добавить товар» видна: есть камера в звонке, право «Склад» (создание товара) и режим не «Только смотреть».
  bool get canAddProduct => wizard != null && mode != AssistantMode.viewOnly && (_session?.canRoute('/admin/new-product') ?? false);

  bool get wizardActive => wizard?.active == true;

  Future<void> _wizardSay(WizardOut o) async {
    notifyListeners();
    final c = call;
    if (c != null && !c.ended && o.say.isNotEmpty) {
      await _sendWhenQuiet('[Программа] Мастер добавления товара: ${o.say} Скажи это пользователю как есть, коротко.', true);
    }
  }

  Future<void> wizardStart() async {
    final w = wizard;
    if (w == null || !canAddProduct) return;
    await _wizardSay(w.start(_langNow()));
  }

  /// Кнопка «Снять»: кадр текущей (или выбранной) стороны. Нажатие — само подтверждение, слова «готово» не нужно.
  Future<void> wizardShoot([WizardSide? side]) async {
    final w = wizard;
    if (w == null || !w.active) return;
    await _wizardSay(await w.shoot(side, _langNow(), viaUi: true));
  }

  /// Штрихкод введён руками или принят со сканера (сканер печатает цифры в поле): дубль, название — тот же путь, что со снимка.
  Future<void> wizardBarcode(String code) async {
    final w = wizard;
    if (w == null || !w.active || code.trim().isEmpty) return;
    await _wizardSay(await w.acceptBarcode(code, _langNow()));
  }

  Future<void> wizardCancel() async {
    final w = wizard;
    if (w == null || !w.active) return;
    await _wizardSay(w.cancel(_langNow()));
  }

  /// Кнопка «Создать товар» (показывается, когда есть название и цена): тот же путь, что `wizard_create`, с журналом и отменой.
  Future<void> wizardCreate() async {
    final w = wizard;
    final k = kit;
    final s = _session;
    final c = call;
    if (w == null || k == null || s == null || c == null || !w.active || !canAddProduct) return;
    final lang = _langNow();
    final r = ToolRequest(
      LiveToolCall('ui-${DateTime.now().microsecondsSinceEpoch}', 'wizard_create', const {}),
      ToolEnv(session: s, speech: c.speech, mode: mode, lang: lang),
    );
    final res = await wizardCreateRun(k, w, r, viaUi: true);
    if (res.status == ToolStatus.ok) {
      _showLine(ActionLine(++_lineId, res.say, sub: _subOf(), kind: ActionKind.ok, undoable: res.undo));
    }
    await _wizardSay(WizardOut(res.status, res.say));
  }

  /// Снимок с потока камеры для мастера (тот же `takeStill`, что у «Что это?»).
  Future<WizardStill> _wizardStill() async {
    final v = video;
    if (v == null || !v.cameraOn) return (bytes: null, barcode: null, error: videoMsg('noCamera', _langNow()));
    final still = await v.takeStill(ResolutionPreset.high);
    if (!still.ok) return (bytes: null, barcode: null, error: videoMsg(still.errorKey ?? 'stillFailed', _langNow()));
    return (bytes: still.bytes, barcode: still.barcode, error: null);
  }

  VideoRecognizer? _recognizerOverride;
  set recognizer(VideoRecognizer r) => _recognizerOverride = r;

  /// Кнопка (или фраза) → чёткий снимок → карточка + сообщение ИИ. Одно распознавание за раз.
  Future<void> askVideo(VideoAsk ask) async {
    final v = video;
    if (v == null || videoBusy != null || !v.cameraOn) return;
    final l = _l;
    videoBusy = ask;
    lastAsk = ask;
    notifyListeners();
    VideoCard card;
    try {
      final preset = ask == VideoAsk.invoice || ask == VideoAsk.shelf ? ResolutionPreset.veryHigh : ResolutionPreset.high;
      final still = await v.takeStill(preset);
      if (!still.ok) {
        card = VideoErrorCard(videoMsg(still.errorKey ?? 'stillFailed', _uiLang));
      } else {
        final VideoRecognizer rec = _recognizerOverride ?? _read<VideoRecognizer>(videoRecognizerProvider);
        card = await rec.run(ask, still, VideoAskContext(canStock: canStock, canProcure: canProcure, l: l));
      }
    } catch (_) {
      card = VideoErrorCard(l.aiErrOther);
    }
    videoBusy = null;
    videoCard = card;
    focus.newFrame(card is VideoProductCard ? card.product : null); // «этот товар» = с камеры; не узнали — сброс (ревью М-4)
    turns.add(CallTurn(false, card.chatText, table: true));
    notifyListeners();
    final c = call;
    if (c != null && !c.ended) unawaited(_sendWhenQuiet(card.aiMessage, card.aiRespond));
  }

  void closeVideoCard() {
    videoCard = null;
    notifyListeners();
  }

  /// Нажатие на строку таблицы (или «Открыть» в карточке): открыть объект. Звонок сворачивается в плашку.
  Future<void> openTableRow(CallTable t, int row) async {
    final route = t.objectRoute;
    if (route == null) return;
    final ref = row >= 0 && row < t.refs.length ? t.refs[row] : null;
    final n = nav;
    final s = _session;
    if (n == null || s == null) return;
    minimize();
    markNavByTool();
    try {
      switch (route) {
        case 'product':
          if (ref != null) await n.openProduct(NavProduct(ref, row < t.rows.length ? t.cellText(row, 0, revealNames: false) : ''));
        case 'check' || 'sale':
          if (ref == null) return;
          if (n is RouterNavHost) {
            await n.openSaleById(ref);
          } else {
            await n.openCheck(number: ref);
          }
        default:
          final def = const ScreenRegistry().byKey(route);
          if (def != null && def.route != null && def.allowedFor(s)) await n.openRoute(def.route!);
      }
    } catch (_) {}
  }

  /// Выбрать вариант в карточке «Похоже на:».
  void chooseLooksLike(Product p) {
    final card = videoCard;
    if (card is! VideoProductCard) return;
    videoCard = card.choose(p, canStock: canStock);
    focus.setCamera(p);
    notifyListeners();
    final c = call;
    if (c != null && !c.ended) unawaited(_sendWhenQuiet(videoCard!.aiMessage, true));
  }

  /// Сообщение ИИ — не во время его ответа: ждём, пока замолчит (до 5 с).
  Future<void> _sendWhenQuiet(String text, bool respond) async {
    final c = call;
    if (c == null) return;
    final end = DateTime.now().add(const Duration(seconds: 5));
    while (c.state.value == CallState.speaking && DateTime.now().isBefore(end) && !c.ended) {
      await Future<void>.delayed(const Duration(milliseconds: 100));
    }
    if (!c.ended) c.sendText(text, respond: respond);
  }

  /// «Открыть приход (N)»: камера отпускается, звонок завершается (разговор уходит в чат), открывается приёмка со строками.
  Future<void> openReceive(List<ReceiveLine> lines) async {
    if (lines.isEmpty || !canProcure) return;
    await video?.stop();
    await CameraGate.idle();
    final c = call;
    if (c != null && !c.ended) c.end();
    _router?.push('/admin/receive', extra: lines);
  }

  // =================================================================================================================

  @override
  void dispose() {
    _clock?.cancel();
    _lineTimer?.cancel();
    _slowTimer?.cancel();
    _pendingTimer?.cancel();
    _moveTimer?.cancel();
    _localNavTimer?.cancel();
    _router?.routerDelegate.removeListener(_onRouteChanged);
    WidgetsBinding.instance.removeObserver(this);
    final c = call;
    if (c != null && !c.ended) {
      c.onEnded = null;
      c.end();
    }
    unawaited(video?.dispose());
    wizard?.removeListener(notifyListeners);
    wizard?.dispose();
    unawaited(_notices.close());
    finishedTick.dispose();
    panelInset.dispose();
    tables.dispose();
    super.dispose();
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Оболочка над go_router
// ---------------------------------------------------------------------------------------------------------------------

/// Навигация ИИ поверх go_router. [openRoute] возвращается сразу после начала перехода; перед каждым переходом звонок
/// сворачивается в плашку (полный экран закрывается синхронно — «назад» не заденет его).
class RouterNavHost implements NavHost, NavVerifier {
  final GoRouter router;
  final AssistantCallController c;
  RouterNavHost(this.router, this.c);

  /// 2026-10-27: какой экран вне маршрутов поставлен последним (для подтверждения открытия).
  String? _auxOpened;

  static String _path(String l) => Uri.tryParse(l)?.path ?? l;

  /// Подтверждение открытия: адрес роутера стал целью (или страница вне маршрутов поставлена) — не позже [wait].
  @override
  Future<bool> confirmed(String where, {Duration wait = const Duration(milliseconds: 1500)}) async {
    final end = DateTime.now().add(wait);
    while (true) {
      if (where.startsWith('/') ? _path(location) == _path(where) : _auxOpened == where) return true;
      if (!DateTime.now().isBefore(end)) return false;
      await Future<void>.delayed(const Duration(milliseconds: 40));
    }
  }

  Reader get _read => c._read;

  @override
  String get location => routerLocation(router);

  @override
  bool get canGoBack => router.canPop();

  void _before() {
    c.markNavByTool();
    c.minimize();
  }

  @override
  Future<void> openRoute(String location) async {
    _before();
    unawaited(router.push<Object?>(location).catchError((Object _) => null));
  }

  @override
  Future<void> openAux(String key) async {
    _before();
    _auxOpened = null;
    final nav = router.routerDelegate.navigatorKey.currentState;
    if (nav == null) return;
    final Widget? page = switch (key) {
      'printer' => const PrinterSettingsScreen(),
      'ai_keys' => const AiSettingsScreen(),
      'kassa_link' => const KassaLinkScreen(), // 2026-10-27: экраны профиля, которые раньше голосом не открывались
      'gemini_keys' => const GeminiKeysScreen(),
      'ai_access' => const AiAccessScreen(),
      _ => null,
    };
    if (page != null) {
      _auxOpened = key;
      unawaited(nav.push<void>(MaterialPageRoute<void>(builder: (_) => page)));
    }
  }

  @override
  Future<void> goHome() async {
    _before();
    router.go('/home');
  }

  @override
  Future<void> goBack() async {
    _before();
    if (router.canPop()) {
      router.pop();
    } else {
      await router.routerDelegate.navigatorKey.currentState?.maybePop();
    }
  }

  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async {
    final db = _read(databaseProvider);
    if (barcode != null && barcode.isNotEmpty) {
      final found = await db.productByScan(barcode);
      if (found != null) return [_nav(found.product)];
      final r = await CodeLookup.find(db, _read(apiProvider), barcode);
      return r.product == null ? const [] : [_nav(r.product!)];
    }
    // 2026-10-27 (М2): общий матчер (Асу = ASU, «винстон»), а не подстроки
    final q = query ?? '';
    final rows = q.trim().isEmpty ? await db.searchProducts(q, limit: 30) : [for (final h in await searchForCall(db, q, top: 30)) h.product];
    return [for (final p in rows) _nav(p)];
  }

  static NavProduct _nav(Product p) => NavProduct(p.id, p.name, barcode: p.barcode, price: p.price);

  @override
  Future<void> openProduct(NavProduct p) async {
    final db = _read(databaseProvider);
    final product = await db.productById(p.id);
    final nav = router.routerDelegate.navigatorKey.currentState;
    if (product == null || nav == null) return;
    _before();
    unawaited(nav.push<void>(MaterialPageRoute<void>(builder: (_) => ProductEditScreen(product: product))));
  }

  /// Чек по id продажи (строка таблицы «Чеки»).
  Future<bool> openSaleById(String id) async {
    try {
      final m = await _read(salesRepoProvider).sale(id);
      _before();
      unawaited(router.push<Object?>('/checks/detail', extra: CheckRow.fromServer(m)).catchError((Object _) => null));
      return true;
    } catch (_) {
      return false;
    }
  }

  @override
  Future<bool> openCheck({String? number, bool last = false}) async {
    final sales = _read(salesRepoProvider);
    String norm(String s) => s.replaceAll(RegExp(r'[#№\s]'), '').replaceFirst(RegExp(r'^0+(?=\d)'), '');
    final wanted = number == null ? '' : norm(number);
    for (var page = 1; page <= 5; page++) {
      final rows = await sales.sales(page: page);
      if (rows.isEmpty) break;
      for (final m in rows) {
        final row = CheckRow.fromServer(m);
        if (last || (wanted.isNotEmpty && norm(row.number) == wanted)) {
          _before();
          unawaited(router.push<Object?>('/checks/detail', extra: row).catchError((Object _) => null));
          return true;
        }
      }
      if (last) break;
    }
    return false;
  }
}

/// Хост переходов для экранов с «ручками» (оплата, возврат…): звонок сворачивается перед переходом.
class _MinimizingOpener implements ScreenOpenerHost {
  final ScreenOpenerHost inner;
  final AssistantCallController c;
  final GoRouter router;
  _MinimizingOpener(this.inner, this.c, this.router);

  /// Путь верхней страницы (с учётом push) — иначе «уже открыт» не узнать и экран открылся бы второй раз.
  @override
  String get location => Uri.tryParse(routerLocation(router))?.path ?? inner.location;

  @override
  Future<void> push(String location) {
    c.markNavByTool();
    c.minimize();
    return inner.push(location);
  }

  @override
  Future<void> pushPage(Widget page) {
    c.markNavByTool();
    c.minimize();
    return inner.pushPage(page);
  }
}

/// Запасной хост, пока роутер не подключён (тесты без роутера): переходов нет.
class _FallbackNav implements NavHost {
  @override
  String get location => '';
  @override
  bool get canGoBack => false;
  @override
  Future<void> openRoute(String location) async {}
  @override
  Future<void> openAux(String key) async {}
  @override
  Future<void> goHome() async {}
  @override
  Future<void> goBack() async {}
  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async => const [];
  @override
  Future<void> openProduct(NavProduct p) async {}
  @override
  Future<bool> openCheck({String? number, bool last = false}) async => false;
}
