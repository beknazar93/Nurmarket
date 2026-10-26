// 2026-10-26 (голос-управление, В0): реестр инструментов звонка с Нур Советником — каркас. Реальные инструменты навигации,
// чтения и действий (В1–В3) подключаются через [ToolRegistry.register]. Здесь же: проверка права роли (при объявлении и
// ещё раз при вызове), режим помощника, ворота намерения, защита от повтора (отпечаток действия 10 с) и журнал вызовов
// в памяти. Деньги и остатки инструменты проводят только через существующие репозитории с ключами повтора — не здесь.
// 2026-10-26 (голос-управление, В3а): ворота читают числа словами (spokenNumbers), у инструмента с несколькими действиями
// требования ворот по полям аргументов (GateReq), объекты просьбы (клиент, сотрудник), повтор «ещё одну» в новой реплике,
// цепочка уточнений, маскирование телефонов и длинных цифр в журнале.
import 'dart:async';
import 'dart:convert';

import '../../api/session_store.dart';
import '../live_tools.dart';
import 'intent_gate.dart';
import 'lang.dart' show detectLang, spokenNumbers;

/// Группа инструмента (раздел 3.3 ТЗ).
enum ToolGroup { nav, read, table, action, money, service }

/// Режим помощника (раздел 2.3 ТЗ). По умолчанию — «Выполнять по просьбе». Голосом режим не меняется: инструмента для этого нет.
enum AssistantMode {
  act('act'),
  viewOnly('view_only'),
  askMoney('ask_money');

  final String wire;
  const AssistantMode(this.wire);

  static const defaultMode = AssistantMode.act;

  /// Из настройки; пусто или незнакомо — по умолчанию.
  static AssistantMode parse(String? v) => values.firstWhere((m) => m.wire == v, orElse: () => defaultMode);

  /// Более строгий из двух (общий режим телефона и режим сотрудника — действует строгий).
  static AssistantMode stricter(AssistantMode a, AssistantMode b) {
    const rank = {AssistantMode.act: 0, AssistantMode.askMoney: 1, AssistantMode.viewOnly: 2};
    return rank[a]! >= rank[b]! ? a : b;
  }
}

/// Статус ответа инструмента (раздел 3.2 ТЗ). [wire] уходит модели как есть.
enum ToolStatus {
  ok('ok'),
  clarify('clarify'),
  ambiguous('ambiguous'),
  notRequested('not_requested'),
  denied('denied'),
  notFound('not_found'),
  offline('offline'),
  pending('pending'),
  error('error'),
  viewOnly('view_only');

  final String wire;
  const ToolStatus(this.wire);
}

/// Ответ инструмента: [say] модель произносит как есть (его собирает программа), [data] — данные магазина, не команды.
class ToolResult {
  final ToolStatus status;
  final String say;
  final Map<String, dynamic>? data;
  final String? tableId;
  final List<Map<String, dynamic>>? options;
  final bool undo;
  final String? screen;
  const ToolResult(this.status, this.say, {this.data, this.tableId, this.options, this.undo = false, this.screen});

  /// Предел `data` в ответе модели (символов JSON), раздел 3.2 ТЗ.
  static const dataLimit = 1500;

  Map<String, dynamic> toJson() {
    Map<String, dynamic>? d = data;
    if (d != null) {
      final text = jsonEncode(d);
      if (text.length > dataLimit) {
        // Обрезка так, чтобы и сам обрезанный JSON (с экранированием кавычек) уложился в предел.
        var n = dataLimit - 60;
        while (n > 0 && jsonEncode({'truncated': true, 'text': text.substring(0, n)}).length > dataLimit) {
          n -= 40;
        }
        d = {'truncated': true, 'text': text.substring(0, n < 0 ? 0 : n)};
      }
    }
    return {
      'status': status.wire,
      'say': say,
      'data': ?d,
      'table_id': ?tableId,
      'options': ?options,
      if (undo) 'undo': true,
      'screen': ?screen,
    };
  }

  ToolResult withSay(String s) => ToolResult(status, s, data: data, tableId: tableId, options: options, undo: undo, screen: screen);
}

/// Окружение одного вызова: кто вызывает, режим, речь пользователя. Собирается заново на каждый вызов (сессия и режим
/// могли смениться — права проверяются при вызове ещё раз).
class ToolEnv {
  final Session session;
  final AssistantMode mode;
  final UserSpeechLog speech;

  /// Язык ответа программы (ru/ky/uz/en/tr): по последней реплике пользователя, иначе язык интерфейса.
  final String lang;

  /// Где сейчас пользователь (для отпечатка действия и поля screen).
  final String screen;
  final DateTime Function() now;

  /// Режим «Спрашивать перед деньгами»: карточка «Сделать» — решает UI (В5). Нет обработчика — действие не выполняется.
  final Future<bool> Function(AssistantTool tool, Map<String, dynamic> args)? confirm;
  ToolEnv({
    required this.session,
    required this.speech,
    this.mode = AssistantMode.defaultMode,
    this.lang = 'ru',
    this.screen = '',
    this.confirm,
    DateTime Function()? now,
  }) : now = now ?? DateTime.now;
}

/// Запрос к инструменту: вызов, окружение, ссылка на запись о сказанном.
class ToolRequest {
  final LiveToolCall call;
  final ToolEnv env;
  const ToolRequest(this.call, this.env);
  Map<String, dynamic> get args => call.args;
  bool get cancelled => call.cancel.isCancelled;
}

/// Инструмент: объявление для модели + кто может + что сделать.
class AssistantTool {
  final ToolDecl decl;
  final ToolGroup group;

  /// Право роли (Session.can…); null — всем. Проверяется при объявлении (forSession) и при каждом вызове.
  final bool Function(Session s)? allowed;

  /// Как называется нужное право (для ответа «нет права …»), по-русски; необязательно.
  final String? needs;

  /// Основы слов-просьбы (любого из 5 языков). Обязательны для action/money.
  final List<String> intentWords;

  /// Имена числовых аргументов, которые пользователь обязан сказать сам (цена, количество, сумма). Числа, которые знает
  /// сама программа (итог корзины), сюда не включаются.
  final List<String> numericArgs;

  /// Режим «Спрашивать перед деньгами» требует подтверждения (по умолчанию — группа money).
  final bool? confirmInAskMode;
  final Future<ToolResult> Function(ToolRequest r) run;

  /// 2026-10-26 (В3а): имена текстовых аргументов-объектов (клиент, сотрудник, товар), слово которых должно прозвучать в речи.
  final List<String> objectArgs;

  /// Значения числовых аргументов по умолчанию, которые называть вслух не нужно («добавь Колу» = 1 штука).
  final Map<String, num> numericDefaults;

  /// Требования ворот по аргументам вызова: у инструмента с несколькими действиями (cart_edit: add / remove / discount…)
  /// слова-просьбы и числа зависят от `op`. null — одно требование из [intentWords], [numericArgs], [objectArgs].
  final List<GateReq> Function(Map<String, dynamic> args)? gateFor;

  /// false — повтор не склеивается с прежним (отмена: «отмени» два раза подряд — это два шага назад).
  final bool dedupe;

  /// true — тот же отпечаток в НОВОЙ реплике пользователя — новое действие («добавь Колу» … «ещё одну Колу» через 5 с);
  /// двойной вызов моделью в одной реплике по-прежнему не задваивает. Для денег всегда строго: реплика не учитывается.
  final bool perTurnDedupe;

  AssistantTool({
    required this.decl,
    required this.group,
    required this.run,
    this.allowed,
    this.needs,
    this.intentWords = const [],
    this.numericArgs = const [],
    this.confirmInAskMode,
    this.objectArgs = const [],
    this.numericDefaults = const {},
    this.gateFor,
    this.dedupe = true,
    this.perTurnDedupe = false,
  });

  List<GateReq> gateReqs(Map<String, dynamic> args) =>
      gateFor != null ? gateFor!(args) : [GateReq(words: intentWords, numeric: numericArgs, objects: objectArgs)];

  String get name => decl.name;

  /// Действие (меняет данные): за воротами намерения, не выполняется в режиме «Только смотреть».
  bool get isAction => group == ToolGroup.action || group == ToolGroup.money;
  bool get needsMoneyConfirm => confirmInAskMode ?? group == ToolGroup.money;
}

/// Запись журнала вызовов (в памяти; В3а добавит запись на диск `ai_actions.jsonl` и отмену).
class ToolLogEntry {
  final DateTime at;
  final String callId;
  final String tool;
  final ToolGroup? group;
  final ToolStatus status;
  final String say;

  /// Что сказал пользователь (последняя реплика) — для разбора «кто просил».
  final String said;
  final Map<String, dynamic> args;
  final AssistantMode mode;
  final bool undoable;
  const ToolLogEntry({
    required this.at,
    required this.callId,
    required this.tool,
    required this.group,
    required this.status,
    required this.say,
    required this.said,
    required this.args,
    required this.mode,
    required this.undoable,
  });

  Map<String, dynamic> toJson() => {
    'at': at.toIso8601String(),
    'call': callId,
    'tool': tool,
    'status': status.wire,
    'say': say,
    'said': said,
    'args': args,
    'mode': mode.wire,
    if (undoable) 'undo': true,
  };
}

class ToolJournal {
  final int limit;
  final entries = <ToolLogEntry>[];
  ToolJournal({this.limit = 200});

  void add(ToolLogEntry e) {
    entries.add(e);
    while (entries.length > limit) {
      entries.removeAt(0);
    }
  }

  List<ToolLogEntry> recent([int n = 10]) => entries.length <= n ? List.of(entries) : entries.sublist(entries.length - n);

  /// Телефон или длинная цифровая последовательность: 7 и больше цифр подряд (с пробелами, скобками, дефисами, «+»).
  static final _longDigits = RegExp(r'\+?\d(?:[\s\-().]*\d){6,}');
  static final _plainDate = RegExp(r'^(\d{4}-\d{2}-\d{2}|\d{1,2}\.\d{1,2}\.\d{2,4})$');

  /// 2026-10-26 (В3а, замечание В1): маскирование по ЗНАЧЕНИЮ, а не только по имени ключа. Номер телефона клиента в названии
  /// товара, в просьбе («пробей на 0555 123 456») или в штрихкоде в журнал не попадает. Даты (2026-10-26, 26.10.2026) остаются.
  static String maskDigits(String s) =>
      s.replaceAllMapped(_longDigits, (m) => _plainDate.hasMatch(m.group(0)!.trim()) ? m.group(0)! : '***');

  /// В журнал не попадают ключи, токены, пароли и телефоны (раздел 2.6 ТЗ): по имени ключа и по значению.
  static Map<String, dynamic> redact(Map<String, dynamic> args) {
    final secret = RegExp(r'key|token|secret|password|pass|phone|tel|jwt|auth|телефон|ключ|пароль', caseSensitive: false);
    Object? walk(Object? v, String name) {
      if (secret.hasMatch(name)) return '***';
      if (v is Map) return {for (final e in v.entries) '${e.key}': walk(e.value, '${e.key}')};
      if (v is List) return [for (final x in v) walk(x, name)];
      if (v is String) return maskDigits(v);
      return v;
    }

    return walk(args, '') as Map<String, dynamic>;
  }
}

class _Recent {
  final DateTime at;
  final Future<ToolResult> result;
  _Recent(this.at, this.result);
}

class _Clarify {
  final String said;
  final DateTime at;
  _Clarify(this.said, this.at);
}

class ToolRegistry {
  final _tools = <String, AssistantTool>{};
  final IntentGate gate;
  final journal = ToolJournal();

  /// Тот же отпечаток действия (инструмент + аргументы без регистра + экран) за это время — тот же ответ, без повтора.
  final Duration dedupeWindow;

  /// Уточнение ждёт ответа не дольше этого — потом просьба из прежней реплики уже не засчитывается.
  final Duration clarifyWindow;
  final _recent = <String, _Recent>{};
  final _clarifying = <String, _Clarify>{};

  /// 2026-10-26 (В3а): режим «Только смотреть» — инструменты-действия не объявляются вовсе (экономит ~9 тыс. символов объявлений
  /// звонка). По умолчанию выключено: тогда они объявлены, а при вызове программа отвечает `view_only` готовой фразой, и ИИ
  /// произносит её. Включать вместе с фразой в инструкции звонка («в режиме „только смотреть“ действий нет»).
  final bool hideActionsInViewOnly;

  ToolRegistry({
    this.gate = const BasicIntentGate(numberExtractor: spokenNumbers),
    this.dedupeWindow = const Duration(seconds: 10),
    this.clarifyWindow = const Duration(seconds: 60),
    this.hideActionsInViewOnly = false,
  });

  /// Реестр со служебными тестовыми инструментами каркаса (безопасными, только чтение).
  factory ToolRegistry.withBuiltins({IntentGate gate = const BasicIntentGate(numberExtractor: spokenNumbers)}) {
    final r = ToolRegistry(gate: gate);
    r.register(appStatusTool());
    return r;
  }

  void register(AssistantTool t) {
    if (_tools.containsKey(t.name)) throw ArgumentError('Инструмент уже зарегистрирован: ${t.name}');
    if (t.isAction && t.intentWords.isEmpty && t.gateFor == null) {
      throw ArgumentError('У действия ${t.name} нет слов-просьбы (intentWords): без них ворота не пропустят ни одной просьбы');
    }
    _tools[t.name] = t;
  }

  AssistantTool? operator [](String name) => _tools[name];
  Iterable<String> get names => _tools.keys;

  /// 2026-10-26 (В3а): забыть недавние действия — после «отмени» та же команда («поставь цену 120») должна выполниться снова,
  /// а не вернуть «уже сделано».
  void clearRecent() => _recent.clear();

  /// Инструменты, доступные этой сессии (раздел 2.5 в ТЗ): в setup объявляются только они.
  List<AssistantTool> forSession(Session session, [AssistantMode mode = AssistantMode.defaultMode]) => [
    for (final t in _tools.values)
      if ((t.allowed == null || t.allowed!(session)) && !(hideActionsInViewOnly && mode == AssistantMode.viewOnly && t.isAction)) t,
  ];

  /// Объявления для setup (functionDeclarations).
  List<Map<String, dynamic>> declarations(Session session, [AssistantMode mode = AssistantMode.defaultMode]) => [
    for (final t in forSession(session, mode)) t.decl.toJson(),
  ];

  /// Обработчик для LiveCall.onToolCall: окружение собирается заново на каждый вызов.
  ToolHandler handler({
    required Session Function() session,
    required UserSpeechLog speech,
    AssistantMode Function()? mode,
    String Function()? lang,
    String Function()? screen,
    Future<bool> Function(AssistantTool tool, Map<String, dynamic> args)? confirm,
  }) =>
      (call) async {
        final env = ToolEnv(
          session: session(),
          speech: speech,
          mode: mode?.call() ?? AssistantMode.defaultMode,
          // Язык ответа программы — язык ПОСЛЕДНЕЙ реплики пользователя (ru / ky / uz / en / tr), иначе русский.
          lang: lang?.call() ?? detectLang(speech.last?.text ?? ''),
          screen: screen?.call() ?? '',
          confirm: confirm,
        );
        return (await execute(call, env)).toJson();
      };

  /// Выполнить вызов: права → режим → ворота → повтор → инструмент. Исключения наружу не уходят.
  Future<ToolResult> execute(LiveToolCall call, ToolEnv env) async {
    final tool = _tools[call.name];
    ToolResult res;
    try {
      res = await _execute(tool, call, env);
    } catch (_) {
      res = ToolResult(ToolStatus.error, toolMsg('error', env.lang));
    }
    final cur = env.speech.last;
    journal.add(
      ToolLogEntry(
        at: env.now(),
        callId: call.id,
        tool: call.name,
        group: tool?.group,
        status: res.status,
        say: res.say,
        said: ToolJournal.maskDigits(cur?.text ?? ''),
        args: ToolJournal.redact(call.args),
        mode: env.mode,
        undoable: res.undo,
      ),
    );
    return res;
  }

  Future<ToolResult> _execute(AssistantTool? tool, LiveToolCall call, ToolEnv env) async {
    final lang = env.lang;
    if (call.cancel.isCancelled) return ToolResult(ToolStatus.error, toolMsg('cancelled', lang));
    if (tool == null) return ToolResult(ToolStatus.error, toolMsg('unknown', lang));
    // Право проверяется при каждом вызове: сессия могла измениться после объявления в setup.
    if (tool.allowed != null && !tool.allowed!(env.session)) {
      final n = tool.needs;
      return ToolResult(ToolStatus.denied, n == null ? toolMsg('denied', lang) : '${toolMsg('denied', lang)} ($n)');
    }
    if (tool.isAction) {
      if (env.mode == AssistantMode.viewOnly) return ToolResult(ToolStatus.viewOnly, toolMsg('viewOnly', lang));
      final now = env.now();
      final cur = env.speech.last;
      final pending = _clarifying[tool.name];
      String? earlier;
      if (pending != null && now.difference(pending.at) <= clarifyWindow && pending.said != cur?.text) earlier = pending.said;
      // Ворота: по требованиям инструмента (у cart_edit и подобных — по полям аргументов). Первый отказ возвращается как есть.
      for (final rq in tool.gateReqs(call.args)) {
        final numeric = <String, num>{};
        for (final a in rq.numeric) {
          final v = call.args[a];
          final n = v is num ? v : (v is String ? num.tryParse(v.trim().replaceAll(',', '.').replaceAll(RegExp(r'[#№\s]'), '')) : null);
          if (n == null) continue;
          final dflt = tool.numericDefaults[a];
          if (dflt != null && (dflt - n).abs() < 1e-9) continue; // «добавь Колу» — одна штука, называть не нужно
          numeric[a] = n;
        }
        numeric.addAll(rq.values);
        final objects = <String>[
          for (final a in rq.objects)
            if (call.args[a] is String && (call.args[a] as String).trim().isNotEmpty) (call.args[a] as String).trim(),
        ];
        final verdict = gate.check(intentWords: rq.words, numeric: numeric, speech: GateSpeech(cur, earlier, now), objects: objects);
        if (verdict != null) {
          if (verdict.status == 'clarify') {
            // Ответ на этот вопрос — продолжение просьбы: запоминаем, что именно пользователь просил (цепочкой, чтобы
            // второй вопрос подряд — «какого Асана?» после «на кого?» — не терял исходное слово-просьбу).
            _rememberClarify(tool, cur, earlier, now);
            return ToolResult(ToolStatus.clarify, toolMsg(verdict.reasonKey, lang, verdict.params));
          }
          return ToolResult(ToolStatus.notRequested, toolMsg(verdict.reasonKey, lang, verdict.params));
        }
      }
      if (env.mode == AssistantMode.askMoney && tool.needsMoneyConfirm) {
        final ok = env.confirm == null ? false : await env.confirm!(tool, call.args);
        if (!ok) return ToolResult(ToolStatus.clarify, toolMsg('needsConfirm', lang));
      }
      return _runOnce(tool, call, env, now, earlier);
    }
    return _run(tool, call, env);
  }

  void _rememberClarify(AssistantTool tool, UserTurn? cur, String? earlier, DateTime now) {
    if (cur == null || cur.echoSuspect) return;
    _clarifying[tool.name] = _Clarify(earlier == null ? cur.text : '$earlier ${cur.text}', now);
  }

  /// Повтор того же действия за [dedupeWindow] не выполняется второй раз (ни после обрыва, ни при двойной команде).
  Future<ToolResult> _runOnce(AssistantTool tool, LiveToolCall call, ToolEnv env, DateTime now, String? earlier) async {
    if (!tool.dedupe) {
      final res = await _run(tool, call, env);
      _afterRun(tool, res, env, now, earlier);
      return res;
    }
    _recent.removeWhere((_, v) => now.difference(v.at) > dedupeWindow);
    // Деньги — строго: та же команда за 10 с = то же действие, кто бы и сколько раз ни сказал. Прочие (perTurnDedupe) — в
    // рамках одной реплики: «ещё одну Колу» новой фразой — уже новое действие.
    final turn = tool.perTurnDedupe && tool.group != ToolGroup.money ? '|t${env.speech.last?.at.microsecondsSinceEpoch ?? 0}' : '';
    final fp = '${tool.name}|${jsonEncode(_canon(call.args))}|${env.screen.toLowerCase()}$turn';
    final seen = _recent[fp];
    if (seen != null) {
      final prev = await seen.result.then<ToolResult?>((v) => v, onError: (Object _) => null);
      if (prev != null && prev.status == ToolStatus.ok) return prev.withSay('${toolMsg('already', env.lang)} ${prev.say}');
    }
    final fut = _run(tool, call, env);
    _recent[fp] = _Recent(now, fut);
    final ToolResult res;
    try {
      res = await fut;
    } catch (_) {
      _recent.remove(fp); // упало — повторить можно сразу
      rethrow;
    }
    if (res.status != ToolStatus.ok) _recent.remove(fp); // не получилось — повторить можно сразу
    _afterRun(tool, res, env, now, earlier);
    return res;
  }

  void _afterRun(AssistantTool tool, ToolResult res, ToolEnv env, DateTime now, String? earlier) {
    if (res.status == ToolStatus.ok) {
      _clarifying.remove(tool.name);
    } else if (res.status == ToolStatus.clarify || res.status == ToolStatus.ambiguous) {
      _rememberClarify(tool, env.speech.last, earlier, now);
    }
  }

  Future<ToolResult> _run(AssistantTool tool, LiveToolCall call, ToolEnv env) async {
    final res = await tool.run(ToolRequest(call, env));
    if (call.cancel.isCancelled) return ToolResult(ToolStatus.error, toolMsg('cancelled', env.lang));
    return res;
  }

  /// Аргументы без регистра и порядка ключей — для отпечатка действия.
  static Object? _canon(Object? v) {
    if (v is String) return v.trim().toLowerCase();
    if (v is Map) {
      final keys = v.keys.map((k) => '$k').toList()..sort();
      return {for (final k in keys) k: _canon(v[k])};
    }
    if (v is List) return [for (final x in v) _canon(x)];
    return v;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Сообщения программы модели (say). Собираются на языке пользователя (ru, ky, uz, en, tr), без BuildContext — звонок живёт
// и вне экранов. Новые тексты — сюда же, все пять языков сразу.
// ---------------------------------------------------------------------------------------------------------------------

const _msg = <String, Map<String, String>>{
  'denied': {
    'ru': 'У вас нет права на это действие.',
    'ky': 'Бул аракетке сизде укук жок.',
    'uz': "Sizda bu amal uchun huquq yo'q.",
    'en': "You don't have permission for this action.",
    'tr': 'Bu işlem için yetkiniz yok.',
  },
  'notRequested': {
    'ru': 'Скажите, что именно сделать.',
    'ky': 'Эмне кылуу керектигин так айтыңыз.',
    'uz': 'Aynan nima qilish kerakligini ayting.',
    'en': 'Tell me what exactly to do.',
    'tr': 'Tam olarak ne yapılacağını söyleyin.',
  },
  'viewOnly': {
    'ru': 'В режиме «только смотреть» я не меняю данные — сделайте на экране или смените режим.',
    'ky': '«Көрүү гана» режиминде мен маалыматты өзгөртпөйм — экрандан өзүңүз жасаңыз же режимди алмаштырыңыз.',
    'uz': "«Faqat ko'rish» rejimida men ma'lumotni o'zgartirmayman — ekranda o'zingiz bajaring yoki rejimni almashtiring.",
    'en': 'In "view only" mode I do not change data — do it on the screen or switch the mode.',
    'tr': '"Sadece görüntüle" modunda veri değiştirmem — ekrandan yapın veya modu değiştirin.',
  },
  'confirmNumber': {
    'ru': 'Число {n} — правильно?',
    'ky': '{n} саны — туурабы?',
    'uz': "{n} raqami — to'g'rimi?",
    'en': 'The number {n} — is that right?',
    'tr': '{n} sayısı — doğru mu?',
  },
  'needObject': {
    'ru': 'Не услышала в вашей просьбе «{what}» — назовите ещё раз.',
    'ky': 'Өтүнүчүңүздө «{what}» угулган жок — дагы бир жолу айтыңыз.',
    'uz': "So'rovingizda «{what}» eshitilmadi — yana bir bor ayting.",
    'en': 'I did not hear "{what}" in your request — please say it again.',
    'tr': 'İsteğinizde "{what}" duyulmadı — lütfen tekrar söyleyin.',
  },
  'needsConfirm': {
    'ru': 'Нужно подтверждение на экране.',
    'ky': 'Экранда ырастоо керек.',
    'uz': 'Ekranda tasdiqlash kerak.',
    'en': 'Confirmation on the screen is needed.',
    'tr': 'Ekranda onay gerekli.',
  },
  'already': {
    'ru': 'Уже сделано:',
    'ky': 'Мурунтан эле жасалды:',
    'uz': 'Allaqachon bajarilgan:',
    'en': 'Already done:',
    'tr': 'Zaten yapıldı:',
  },
  'error': {
    'ru': 'Не получилось выполнить.',
    'ky': 'Аткаруу мүмкүн болгон жок.',
    'uz': "Bajarib bo'lmadi.",
    'en': 'Could not do it.',
    'tr': 'Yapılamadı.',
  },
  'unknown': {
    'ru': 'Такого инструмента нет.',
    'ky': 'Андай шайман жок.',
    'uz': "Bunday vosita yo'q.",
    'en': 'No such tool.',
    'tr': 'Böyle bir araç yok.',
  },
  'cancelled': {
    'ru': 'Отменено.',
    'ky': 'Жокко чыгарылды.',
    'uz': 'Bekor qilindi.',
    'en': 'Cancelled.',
    'tr': 'İptal edildi.',
  },
  'status': {
    'ru': 'Сейчас {time}. Вы — {role}. Режим помощника: {mode}.',
    'ky': 'Азыр {time}. Сиз — {role}. Жардамчынын режими: {mode}.',
    'uz': 'Hozir {time}. Siz — {role}. Yordamchi rejimi: {mode}.',
    'en': 'It is {time}. You are the {role}. Assistant mode: {mode}.',
    'tr': 'Şu an saat {time}. Rolünüz: {role}. Yardımcı modu: {mode}.',
  },
  'role.owner': {'ru': 'владелец', 'ky': 'ээси', 'uz': 'egasi', 'en': 'owner', 'tr': 'sahip'},
  'role.admin': {'ru': 'администратор', 'ky': 'администратор', 'uz': 'administrator', 'en': 'administrator', 'tr': 'yönetici'},
  'role.staff': {'ru': 'сотрудник', 'ky': 'кызматкер', 'uz': 'xodim', 'en': 'employee', 'tr': 'çalışan'},
  'mode.act': {
    'ru': 'выполнять по просьбе',
    'ky': 'суранычка жараша аткаруу',
    'uz': "so'rov bo'yicha bajarish",
    'en': 'act on request',
    'tr': 'istek üzerine yap',
  },
  'mode.view_only': {'ru': 'только смотреть', 'ky': 'көрүү гана', 'uz': "faqat ko'rish", 'en': 'view only', 'tr': 'sadece görüntüle'},
  'mode.ask_money': {
    'ru': 'спрашивать перед деньгами',
    'ky': 'акчадан мурун сурап алуу',
    'uz': "pul oldidan so'rash",
    'en': 'ask before money',
    'tr': 'paradan önce sor',
  },
};

/// Сообщение по ключу на языке [lang] (нет перевода — русский); `{имя}` заменяются из [params].
String toolMsg(String key, String lang, [Map<String, String> params = const {}]) {
  final m = _msg[key];
  var s = m == null ? key : (m[lang] ?? m['ru'] ?? key);
  params.forEach((k, v) => s = s.replaceAll('{$k}', v));
  return s;
}

/// Тестовый инструмент каркаса: текущее время, роль и режим. Только чтение, никаких данных магазина.
AssistantTool appStatusTool() => AssistantTool(
  decl: const ToolDecl(
    'app_status',
    'Read-only. Returns the current local time, the user role and the assistant mode. '
    'Call when the user asks what time it is, who he is in the app or which assistant mode is on '
    '(ru: который час, кто я; ky: саат канча; uz: soat nechi; en: what time is it; tr: saat kaç).',
  ),
  group: ToolGroup.service,
  run: (r) async {
    final now = r.env.now();
    final hh = now.hour.toString().padLeft(2, '0');
    final mm = now.minute.toString().padLeft(2, '0');
    final s = r.env.session;
    final role = s.isOwner ? 'owner' : (s.isAdmin ? 'admin' : 'staff');
    final lang = r.env.lang;
    return ToolResult(
      ToolStatus.ok,
      toolMsg('status', lang, {
        'time': '$hh:$mm',
        'role': toolMsg('role.$role', lang),
        'mode': toolMsg('mode.${r.env.mode.wire}', lang),
      }),
      data: {'time': '$hh:$mm', 'date': now.toIso8601String().substring(0, 10), 'role': role, 'mode': r.env.mode.wire},
    );
  },
);
