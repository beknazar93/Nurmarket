// 2026-11-27 (просьба владельца «дай Нур-советнику доступы ко всем новым функциям и разделам … полный доступ»): инструменты ЧТЕНИЯ
// и ИЗУЧЕНИЯ для советника (чат и звонок): «объясни раздел», Маркетплейс (функции, статусы, цены с сервера, описание и демо из
// assets/marketplace/content.json), тариф, настройки без секретов, поставщики и приходы, «что нового». Ничего не меняют: ни один
// инструмент не пишет данные, не включает модули и не шлёт сообщения — только читает через существующие репозитории и кэш
// (MarketplaceRepository.load отдаёт последний ответ без связи). Права — как у самих экранов (Session.canRoute / canProcure);
// кассиру закупки, аналитика и Маркетплейс закрыты. Тексты для модели собирает программа (say), данные — компактным JSON ≤ 1500 знаков.
import 'dart:async';

import '../../../features/admin/admin_receive.dart';
import '../../../features/assistant/assistant_mode.dart';
import '../../../features/marketplace/marketplace_content.dart';
import '../../../features/marketplace/marketplace_data.dart';
import '../../../features/marketplace/marketplace_state.dart';
import '../../api/api_client.dart';
import '../../api/server_capabilities.dart';
import '../../api/session_store.dart';
import '../../mode/mode_provider.dart';
import '../../mode/plan_info.dart';
import '../../print/printer_service.dart';
import '../../providers.dart';
import '../../shop_mode.dart';
import '../live_tools.dart';
import 'call_table.dart' show tr5;
import 'read_tools.dart' show Reader;
import 'screen_registry.dart';
import 'section_guide.dart';
import 'tool_registry.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Источник данных
// ---------------------------------------------------------------------------------------------------------------------

/// Откуда инструменты берут данные. Рабочий источник — [RepoKnowledgeSource] (репозитории и кэш приложения), в тестах — фейк.
abstract class KnowledgeSource {
  /// Модули, тарифы и состояние компании: свежий ответ сервера или последний сохранённый (офлайн).
  Future<MpSnapshot> marketplace();
  Future<MarketplaceContent> content();

  /// Признаки возможностей сервера (capabilities.features).
  Set<String> get features;

  /// Тариф компании.
  PlanInfo get plan;

  /// Настройки приложения БЕЗ секретов (язык, тема, режим…): набор ключей фиксирует [RepoKnowledgeSource.settings].
  Future<Map<String, Object?>> settings();
  Future<List<({String id, String name})>> suppliers();

  /// Последние приходы от поставщиков: сырые строки ответа (разбирает инструмент, телефонов в ответ не кладёт).
  Future<List<Map<String, dynamic>>> supplierReceipts({int limit = 15});
}

class RepoKnowledgeSource implements KnowledgeSource {
  final Reader _read;
  RepoKnowledgeSource.fromReader(this._read);

  @override
  Future<MpSnapshot> marketplace() async {
    final repo = _read(marketplaceRepoProvider);
    final live = repo.load();
    try {
      return await live.timeout(const Duration(seconds: 4));
    } on TimeoutException {
      // сеть медленная — отвечаем последним сохранённым (с пометкой «офлайн»), а не ждём 20 с
      final saved = await repo.cachedSnapshot();
      return saved ?? await live;
    }
  }
  @override
  Future<MarketplaceContent> content() => MarketplaceContent.load();
  @override
  Set<String> get features => ServerCapabilities.featureSet;
  @override
  PlanInfo get plan => ServerCapabilities.plan;

  @override
  Future<Map<String, Object?>> settings() async {
    final prefs = _read(prefsProvider);
    final s = _read(sessionProvider);
    final db = _read(databaseProvider);
    var printer = false;
    String paper = '';
    try {
      final c = await _read(printerProvider).load();
      printer = c.ready;
      paper = '${c.paperMm} mm';
    } catch (_) {
      // принтер не настроен — остаётся «нет»
    }
    final mode = await AssistantModePrefs.load(db, s?.userId ?? '');
    // Только безопасные поля: ни почты, ни токенов, ни телефонов, ни ключей ИИ, ни адреса принтера.
    return {
      'language': prefs.locale.languageCode,
      'theme': prefs.system ? 'system' : (prefs.auto ? 'auto' : (prefs.dark ? 'dark' : 'light')),
      'shop_mode': prefs.shop.code,
      'simple_mode': _read(simpleModeProvider),
      'assistant_mode': mode.wire,
      'printer_ready': printer,
      'printer_paper': paper,
      'company': s?.companyName ?? '',
      'cashbox': s?.cashboxName ?? '',
      'role': s == null ? '' : (s.isOwner ? 'owner' : (s.isAdmin ? 'admin' : 'staff')),
    };
  }

  @override
  Future<List<({String id, String name})>> suppliers() async {
    final rows = await _read(adminReceiveRepoProvider).suppliers();
    return [for (final r in rows) (id: r.$1, name: r.$2)];
  }

  @override
  Future<List<Map<String, dynamic>>> supplierReceipts({int limit = 15}) async {
    final data = await _read(apiProvider).get('api/main/suppliers/receipts/', query: {'page': 1, 'limit': limit});
    return asList(data);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Поиск функции Маркетплейса по словам (используют инструменты и экран «Маркетплейс» для ?module=)
// ---------------------------------------------------------------------------------------------------------------------

const _moduleStop = {
  'маркетплейс', 'marketplace', 'функция', 'функцию', 'функции', 'функциясы', 'модуль', 'модуля', 'раздел', 'бөлүм', 'bolum', 'funksiya', 'funksiyasi',
  'function', 'feature', 'module', 'ozellik', 'открой', 'покажи', 'про', 'что', 'такое', 'what', 'is', 'the', 'open', 'show',
};

/// Результат поиска функции: [view] — одна подходящая; [tie] — несколько одинаково подходящих (переспросить).
class ModuleMatch {
  final MpModuleView? view;
  final List<MpModuleView> tie;
  const ModuleMatch(this.view, this.tie);
}

/// Функция по ключу (`bot_seller`) или словам на любом из 5 языков («бот-продавец», «ЭСФ», «смета»). Слова названия — целиком или по основе
/// в 5 букв ([tokenMatches]); лишние слова («функция», «открой») отбрасываются. Нет совпадения — пусто.
ModuleMatch findModule(List<MpModuleView> views, String text) {
  final key = text.trim().toLowerCase().replaceAll(RegExp(r'[\s\-]+'), '_');
  for (final v in views) {
    if (v.id == key) return ModuleMatch(v, const []);
  }
  final q = [for (final w in foldTokens(text)) if (!_moduleStop.contains(w)) w];
  if (q.isEmpty) return const ModuleMatch(null, []);
  final scored = <(MpModuleView, int)>[];
  for (final v in views) {
    final names = <String>[v.id.replaceAll('_', ' '), v.name, ...?_allNames(v)];
    final pool = <String>{for (final n in names) ...foldTokens(n.replaceAll(RegExp(r'\(.*?\)'), ''))};
    final ok = q.every((t) => pool.any((p) => tokenMatches(t, p)));
    if (!ok) continue;
    // чем меньше лишних слов в названии, тем точнее («смета» точнее «Калькулятор сметы и материалов»)
    final best = names.map((n) => foldTokens(n).length).where((n) => n > 0).fold<int>(99, (a, b) => a < b ? a : b);
    scored.add((v, best));
  }
  if (scored.isEmpty) return const ModuleMatch(null, []);
  scored.sort((a, b) => a.$2.compareTo(b.$2));
  final top = scored.first.$2;
  final ties = [for (final s in scored) if (s.$2 == top) s.$1];
  if (ties.length == 1) return ModuleMatch(ties.first, const []);
  return ModuleMatch(null, ties.length > 4 ? ties.sublist(0, 4) : ties);
}

List<String>? _allNames(MpModuleView v) {
  final c = v.content;
  if (c == null) return null;
  return [for (final l in kNavLangs) if (c.name.has(l)) c.name.of(l)];
}

// ---------------------------------------------------------------------------------------------------------------------
// Когда вопросу нужны инструменты (чат: без них запрос идёт потоком и быстрее)
// ---------------------------------------------------------------------------------------------------------------------

/// Вопрос похож на «открой / объясни / покажи данные раздела»: тогда в чат добавляются инструменты. Любое сомнение — без инструментов
/// (обычный быстрый ответ по сводке магазина). Основы слов — на 5 языках.
bool advisorNeedsTools(String text) {
  if (hasOpenVerb(text)) return true;
  final w = foldTokens(text);
  if (w.isEmpty) return false;
  const stems = [
    // ru
    'объясн', 'расскаж', 'функци', 'маркетпл', 'тариф', 'подключ', 'включ', 'настро', 'поставщ', 'приход', 'приемк', 'чек', 'возврат',
    'смен', 'акци', 'диалог', 'ремонт', 'нового', 'новин', 'раздел', 'модул', 'пакет', 'подписк', 'касс', 'клиент',
    // ky
    'тушунд', 'тушундур', 'функциял', 'тарифт', 'жеткируу', 'кабыл', 'чектер', 'сменал', 'жондоо', 'кардар',
    // uz
    'tushuntir', 'funksiya', 'tarif', 'taminot', 'yetkazib', 'chek', 'smena', 'sozlam', 'mijoz', 'yangilik', 'nima yangi',
    // en
    'explain', 'feature', 'function', 'marketplace', 'plan', 'enable', 'setting', 'supplier', 'receipt', 'shift', 'return', 'promo', 'repair', 'tell me about', 'client',
    // tr
    'acikla', 'ozellik', 'tarife', 'ayar', 'tedarik', 'fis', 'vardiya', 'iade', 'musteri',
  ];
  return w.any((x) => stems.any((s) => s.length <= 3 ? x == s : x.startsWith(s)));
}

// ---------------------------------------------------------------------------------------------------------------------
// Инструменты
// ---------------------------------------------------------------------------------------------------------------------

const _marketRoute = '/admin/marketplace';

/// Подключить инструменты изучения к реестру (чата или звонка).
void registerKnowledgeTools(ToolRegistry reg, KnowledgeSource src) {
  for (final t in buildKnowledgeTools(src)) {
    reg.register(t);
  }
}

/// Номер языка в [kNavLangs]; незнакомый — русский.
int _li(String lang) => kNavLangs.contains(lang) ? kNavLangs.indexOf(lang) : 0;

String _cut(String s, int n) => s.length <= n ? s : '${s.substring(0, n - 1)}…';

/// Деньги без копеек: 1 300 → «1300».
String _num(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);

Future<ToolResult> _guard(ToolEnv env, Future<ToolResult> Function() f) async {
  final lang = env.lang;
  try {
    return await f().timeout(const Duration(seconds: 25));
  } on TimeoutException {
    return ToolResult(ToolStatus.offline, tr5(lang, 'Сервер отвечает слишком медленно — этих данных у меня пока нет.', 'Сервер өтө жай жооп берүүдө — бул маалымат азырынча жок.', "Server juda sekin javob beryapti — bu ma'lumot hozircha yo'q.", 'The server is too slow — I do not have this data yet.', 'Sunucu çok yavaş — bu veri henüz yok.'));
  } on ApiException catch (e) {
    if (e.status == 403) return ToolResult(ToolStatus.denied, toolMsg('denied', lang));
    if (e.isTransient) {
      return ToolResult(ToolStatus.offline, tr5(lang, 'Нет связи с сервером — этих данных у меня сейчас нет.', 'Сервер менен байланыш жок — бул маалымат азыр жок.', "Server bilan aloqa yo'q — bu ma'lumot hozir yo'q.", 'No connection to the server — I cannot get this data now.', 'Sunucu ile bağlantı yok — bu veri şu an alınamıyor.'));
    }
    return ToolResult(ToolStatus.error, toolMsg('error', lang));
  }
}

String _needsRu(String route) {
  if (route == _marketRoute) return 'владелец или администратор';
  return switch (Session.routePermission(route)) {
    Session.viewProducts => 'право «Склад»',
    Session.viewAnalytics => 'право «Аналитика»',
    Session.viewClients => 'право «Клиенты»',
    Session.viewOrders => 'право «Заказы»',
    Session.viewShifts => 'право «Смены»',
    Session.viewSettings => 'право «Настройки»',
    Session.viewSalaryFlag => 'право «Зарплата»',
    _ => 'доступ к управлению',
  };
}

const _absentModule = {'bot_dialogs': 'bot_seller', 'repair_orders': 'repair', 'mailings': 'client_mailing', 'onec': 'onec_export'};

List<AbsentSection> _absentMatches(String text) {
  final words = foldTokens(text);
  if (words.isEmpty) return const [];
  final out = <(AbsentSection, int)>[];
  for (final a in kAbsentSections) {
    var best = 0;
    for (final line in a.words) {
      for (final p in line.split('|')) {
        final tokens = foldTokens(p);
        if (tokens.isEmpty) continue;
        if (tokens.every((t) => words.any((w) => tokenMatches(w, t)))) {
          if (tokens.length > best) best = tokens.length;
        }
      }
    }
    if (best > 0) out.add((a, best));
  }
  out.sort((a, b) => b.$2.compareTo(a.$2));
  return [for (final o in out) o.$1];
}

String _absentTitle(AbsentSection a, String lang) => a.titles[_li(lang)];

String _absentSay(AbsentSection a, String lang) {
  final t = _absentTitle(a, lang);
  final owner = a.where == 'owner';
  return tr5(
    lang,
    'Раздела «$t» в мобильном приложении нет — он ${owner ? 'в программе владельца (на компьютере)' : 'на сайте NurCRM'}.',
    '«$t» бөлүмү мобилдик колдонмодо жок — ал ${owner ? 'ээсинин программасында (компьютерде)' : 'NurCRM сайтында'}.',
    "«$t» bo'limi mobil ilovada yo'q — u ${owner ? "egasining dasturida (kompyuterda)" : 'NurCRM saytida'}.",
    'The "$t" section is not in the mobile app — it is ${owner ? 'in the owner program (on the computer)' : 'on the NurCRM website'}.',
    '«$t» bölümü mobil uygulamada yok — ${owner ? 'sahip programında (bilgisayarda)' : 'NurCRM web sitesinde'}.',
  );
}

List<AssistantTool> buildKnowledgeTools(KnowledgeSource src) {
  bool admin(Session s) => s.canRoute(_marketRoute);
  final reg = const ScreenRegistry();
  return [
    _explainSection(src, reg),
    _marketplaceInfo(src, admin),
    _tariffInfo(src, admin),
    _appSettingsInfo(src),
    _suppliersInfo(src),
    _whatsNew(reg),
  ];
}

// ------------------------------------------------------------------------------------------------ explain_section

AssistantTool _explainSection(KnowledgeSource src, ScreenRegistry reg) => AssistantTool(
  group: ToolGroup.read,
  decl: ToolDecl(
    'explain_section',
    'Read-only. Explain what an app section or Marketplace feature does, who it is for, how to turn it on. '
    'Use for "what is X / explain X / how to enable X" and when asked to open a section the phone does not have '
    '(ru: акции, диалоги бота, ремонт: owner program or website). topic = name as the user said it.',
    parameters: sObj({'topic': sStr(desc: 'Section or feature name or key')}, required: ['topic']),
  ),
  run: (r) => _guard(r.env, () async {
    final lang = r.env.lang;
    final s = r.env.session;
    final topic = '${r.args['topic'] ?? ''}'.trim();
    if (topic.isEmpty) return ToolResult(ToolStatus.clarify, tr5(lang, 'О каком разделе рассказать?', 'Кайсы бөлүм жөнүндө айтып берейин?', "Qaysi bo'lim haqida aytib beray?", 'Which section should I explain?', 'Hangi bölümü anlatayım?'));
    // 1) ключ экрана или название экрана целиком
    ScreenDef? def = reg.byKey(topic);
    if (def == null) {
      final tw = foldTokens(topic);
      for (final d in reg.all) {
        if (!d.exists) continue;
        if (d.titles.any((t) => foldTokens(t.replaceAll(RegExp(r'\(.*?\)'), '')).join(' ') == tw.join(' '))) {
          def = d;
          break;
        }
      }
    }
    // 2) раздел, которого нет в телефоне
    final absent = def == null ? _absentMatches(topic) : const <AbsentSection>[];
    if (absent.isNotEmpty) {
      final a = absent.first;
      var say = _absentSay(a, lang);
      // у «Бота-продавца», «Ремонта» есть и функция в Маркетплейсе: добавляем её описание (если роль видит Маркетплейс)
      final modId = _absentModule[a.key];
      if (modId != null && s.canRoute(_marketRoute)) {
        final c = await src.content();
        final m = c.module(modId);
        final line = m == null ? '' : (m.line.isEmpty ? m.short.of(lang) : m.line.of(lang));
        if (line.isNotEmpty) say = '$say ${_cut(line, 220)}';
      }
      return ToolResult(ToolStatus.ok, say, data: {'kind': 'absent', 'key': a.key, 'in_mobile': false, 'where': a.where});
    }
    // 3) функция Маркетплейса
    if (def == null) {
      final snap = await src.marketplace();
      final content = await src.content();
      final views = buildModuleViews(snap, content, lang, features: src.features);
      final m = findModule(views, topic);
      if (m.view != null) {
        if (!s.canRoute(_marketRoute)) return ToolResult(ToolStatus.denied, '${toolMsg('denied', lang)} (${_needsRu(_marketRoute)})');
        return _moduleResult(m.view!, snap, lang);
      }
      if (m.tie.length > 1) return _tieResult(m.tie, lang);
    }
    // 4) экран по словам
    if (def == null) {
      final found = reg.find(topic, onlyOpenable: false);
      if (found.isEmpty) return ToolResult(ToolStatus.notFound, tr5(lang, 'Такого раздела или функции я не нашла. Скажите название иначе.', 'Мындай бөлүм же функция табылган жок. Атын башкача айтыңыз.', "Bunday bo'lim yoki funksiya topilmadi. Nomini boshqacha ayting.", 'I could not find such a section or feature. Say the name differently.', 'Böyle bir bölüm veya özellik bulunamadı. Adını farklı söyleyin.'));
      final top = found.first.score;
      final tied = [for (final f in found) if (f.score == top) f.def];
      if (tied.length > 1) {
        return ToolResult(ToolStatus.ambiguous, tr5(lang, 'Подходит несколько разделов — какой именно?', 'Бир нече бөлүм туура келет — кайсынысы?', "Bir nechta bo'lim mos — qaysi biri?", 'Several sections match — which one?', 'Birkaç bölüm uyuyor — hangisi?'), options: [
          for (var i = 0; i < tied.length && i < 4; i++) {'n': i + 1, 'title': tied[i].fullTitle(lang), 'key': tied[i].key},
        ]);
      }
      def = tied.first;
    }
    final d = def;
    final title = d.fullTitle(lang);
    if (!d.allowedFor(s)) {
      return ToolResult(ToolStatus.denied, '${toolMsg('denied', lang)} («$title»: ${_needsRu(d.route ?? '')})', data: {'kind': 'screen', 'key': d.key, 'allowed': false});
    }
    final g = kSectionGuide[d.key];
    final about = g == null ? title : g[_li(lang)];
    final extra = d.key == 'marketplace'
        ? tr5(lang, ' Чтобы увидеть список функций, спросите «какие функции доступны».', ' Функциялардын тизмесин көрүү үчүн «кандай функциялар бар» деп сураңыз.', " Funksiyalar ro'yxatini ko'rish uchun «qanday funksiyalar bor» deb so'rang.", ' To see the list of features, ask "which features are available".', ' Özellik listesi için «hangi özellikler var» diye sorun.')
        : '';
    return ToolResult(
      ToolStatus.ok,
      '$about$extra',
      data: {'kind': 'screen', 'key': d.key, 'title': title, 'allowed': true, 'in_mobile': true, 'openable_by_voice': d.voiceOpen},
    );
  }),
);

ToolResult _tieResult(List<MpModuleView> tie, String lang) => ToolResult(
  ToolStatus.ambiguous,
  tr5(lang, 'Подходит несколько функций — какая именно?', 'Бир нече функция туура келет — кайсынысы?', "Bir nechta funksiya mos — qaysi biri?", 'Several features match — which one?', 'Birkaç özellik uyuyor — hangisi?'),
  options: [
    for (var i = 0; i < tie.length; i++) {'n': i + 1, 'title': tie[i].name, 'key': tie[i].id},
  ],
);

String _statusWord(MpModuleView v, String lang) {
  if (v.soon) return tr5(lang, 'скоро', 'жакында', 'tez orada', 'coming soon', 'yakında');
  if (v.on) {
    if (v.onTrial) return tr5(lang, 'проба', 'сынак', 'sinov', 'on trial', 'deneme');
    if (v.included) return tr5(lang, 'входит в вашу сферу', 'сиздин чөйрөгө кирет', 'sohangizga kiradi', 'included in your sphere', 'alanınıza dahil');
    return tr5(lang, 'подключена', 'кошулган', 'ulangan', 'on', 'açık');
  }
  return tr5(lang, 'доступна', 'жеткиликтүү', 'mavjud', 'available', 'mevcut');
}

ToolResult _moduleResult(MpModuleView v, MpSnapshot snap, String lang) {
  final c = v.content;
  final what = c == null ? '' : (c.what.isEmpty ? c.short.of(lang) : c.what.of(lang));
  final line = v.lineFor(lang) ?? '';
  final audience = [for (final a in c?.audience ?? const <LText>[]) _cut(a.of(lang), 60)].take(4).toList();
  final steps = [for (final a in c?.steps ?? const <LText>[]) _cut(a.of(lang), 100)].take(5).toList();
  final demo = [for (final st in c?.demo ?? const <MpStep>[]) if (!st.caption.isEmpty) _cut(st.caption.of(lang), 90)].take(5).toList();
  final price = v.on || v.included || v.linked || v.isFeatureCard ? null : v.monthly;
  final sb = StringBuffer('${v.name} — ${_statusWord(v, lang)}.');
  final desc = what.isNotEmpty ? what : line;
  if (desc.isNotEmpty) sb.write(' ${_cut(desc, 420)}');
  if (price != null) {
    sb.write(' ${tr5(lang, 'Цена с сервера', 'Сервердин баасы', 'Serverdan narx', 'Server price', 'Sunucu fiyatı')}: ${_num(price)} ${snap.currency}/${tr5(lang, 'мес', 'ай', 'oy', 'mo', 'ay')}.');
  } else if (!v.on && !v.included && !v.linked && !v.isFeatureCard) {
    sb.write(' ${tr5(lang, 'Цена уточняется.', 'Баасы тактала элек.', 'Narxi aniqlanmoqda.', 'The price is to be confirmed.', 'Fiyat netleşiyor.')}');
  }
  if (v.trialDays > 0 && v.trialAvailable) sb.write(' ${tr5(lang, 'Можно попробовать ${v.trialDays} дн.', '${v.trialDays} күн сынап көрсө болот.', "${v.trialDays} kun sinab ko'rish mumkin.", 'A ${v.trialDays}-day trial is possible.', '${v.trialDays} gün denenebilir.')}');
  if (audience.isNotEmpty) sb.write(' ${tr5(lang, 'Для кого', 'Кимдер үчүн', 'Kimlar uchun', 'For', 'Kimler için')}: ${audience.join('; ')}.');
  if (steps.isNotEmpty) sb.write(' ${tr5(lang, 'Как включить', 'Кантип кошуу', 'Qanday yoqish', 'How to turn on', 'Nasıl açılır')}: ${steps.join(' → ')}.');
  if (demo.isNotEmpty) sb.write(' ${tr5(lang, 'Как это работает', 'Бул кантип иштейт', 'Bu qanday ishlaydi', 'How it works', 'Nasıl çalışır')}: ${demo.join(' → ')}.');
  return ToolResult(
    ToolStatus.ok,
    sb.toString(),
    data: {
      'kind': 'module',
      'id': v.id,
      'name': v.name,
      'status': v.soon ? 'soon' : (v.on ? 'on' : 'available'),
      'price_month': ?price,
      'currency': snap.currency,
      'trial_days': v.trialDays,
      if (snap.offline) 'offline_cache': true,
    },
  );
}

// ------------------------------------------------------------------------------------------------ marketplace_info

AssistantTool _marketplaceInfo(KnowledgeSource src, bool Function(Session) allowed) => AssistantTool(
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'владелец или администратор',
  decl: ToolDecl(
    'marketplace_info',
    'Read-only. Marketplace of the company: features (modules) on / available, server prices, trial days; or one feature in detail via module. Changes nothing '
    '(ru: какие функции подключены, сколько стоит бот-продавец).',
    parameters: sObj({
      'module': sStr(desc: 'Feature name or key (bot_seller, estimate); empty = list'),
      'status': sStr(desc: 'List filter', values: ['all', 'on', 'available']),
      'group': sStr(desc: 'Sphere key, e.g. grocery'),
      'limit': sInt(desc: 'Rows 1-12, default 8'),
    }),
  ),
  run: (r) => _guard(r.env, () async {
    final lang = r.env.lang;
    final snap = await src.marketplace();
    final content = await src.content();
    final views = buildModuleViews(snap, content, lang, features: src.features);
    if (views.isEmpty) {
      return ToolResult(ToolStatus.offline, tr5(lang, 'Список функций не загрузился и сохранённого ответа нет — откройте «Маркетплейс» со связью.', 'Функциялар тизмеси жүктөлгөн жок жана сакталган жооп жок — «Маркетплейсти» байланыш менен ачыңыз.', "Funksiyalar ro'yxati yuklanmadi va saqlangan javob yo'q — «Marketplace»ni aloqa bilan oching.", 'The feature list did not load and there is no saved answer — open the Marketplace with a connection.', 'Özellik listesi yüklenmedi ve kayıtlı yanıt yok — Marketplace’i bağlantıyla açın.'));
    }
    final one = '${r.args['module'] ?? ''}'.trim();
    if (one.isNotEmpty) {
      final m = findModule(views, one);
      if (m.view != null) return _moduleResult(m.view!, snap, lang);
      if (m.tie.length > 1) return _tieResult(m.tie, lang);
      return ToolResult(ToolStatus.notFound, tr5(lang, 'Такой функции в Маркетплейсе не нашла.', 'Маркетплейстен мындай функция табылган жок.', "Marketplace'da bunday funksiya topilmadi.", 'No such feature in the Marketplace.', 'Marketplace’te böyle bir özellik bulunamadı.'));
    }
    final status = '${r.args['status'] ?? 'all'}';
    final group = foldText('${r.args['group'] ?? ''}').trim();
    final limitRaw = r.args['limit'];
    final limit = ((limitRaw is num ? limitRaw.toInt() : int.tryParse('${limitRaw ?? ''}')) ?? 8).clamp(1, 12);
    bool inGroup(MpModuleView v) {
      if (group.isEmpty) return true;
      final names = <String>[v.group, ...?(content.groups[v.group] == null ? null : [for (final l in kNavLangs) content.groups[v.group]!.of(l)])];
      return names.any((n) => foldText(n) == group || foldText(n).startsWith(group));
    }

    final shown = [
      for (final v in views)
        if (inGroup(v) && (status == 'on' ? v.on : (status == 'available' ? (!v.on && !v.soon) : true))) v,
    ];
    final on = views.where((v) => v.on).length;
    final soon = views.where((v) => v.soon).length;
    final rows = [
      for (final v in shown.take(limit))
        {
          'id': v.id,
          'name': _cut(v.name, 40),
          'status': v.soon ? 'soon' : (v.on ? 'on' : 'available'),
          if (!v.on && !v.included && !v.linked && !v.isFeatureCard && !v.soon) 'price_month': v.monthly, // null — «цена уточняется»
        },
    ];
    final names = [for (final v in shown.take(limit)) '${_cut(v.name, 32)} (${_statusWord(v, lang)})'].join('; ');
    final say = tr5(
      lang,
      'В Маркетплейсе ${views.length} функций: подключено $on, скоро $soon. ${shown.isEmpty ? 'По фильтру ничего нет.' : 'Показываю ${rows.length} из ${shown.length}: $names.'}${snap.offline ? ' (данные из сохранённого ответа, связи нет)' : ''}',
      'Маркетплейсте ${views.length} функция: кошулган $on, жакында $soon. ${shown.isEmpty ? 'Чыпка боюнча эч нерсе жок.' : '${shown.length} ичинен ${rows.length} көрсөтүлдү: $names.'}${snap.offline ? ' (сакталган жооптон, байланыш жок)' : ''}',
      "Marketplace'da ${views.length} funksiya: ulangan $on, tez orada $soon. ${shown.isEmpty ? "Filtr bo'yicha hech narsa yo'q." : '${shown.length} tadan ${rows.length} tasi: $names.'}${snap.offline ? " (saqlangan javobdan, aloqa yo'q)" : ''}",
      'The Marketplace has ${views.length} features: $on on, $soon coming soon. ${shown.isEmpty ? 'Nothing matches the filter.' : 'Showing ${rows.length} of ${shown.length}: $names.'}${snap.offline ? ' (from the saved answer, no connection)' : ''}',
      'Marketplace’te ${views.length} özellik var: $on açık, $soon yakında. ${shown.isEmpty ? 'Filtreye uyan yok.' : '${shown.length} içinden ${rows.length} gösteriliyor: $names.'}${snap.offline ? ' (kayıtlı yanıttan, bağlantı yok)' : ''}',
    );
    return ToolResult(ToolStatus.ok, say, data: {'total': views.length, 'on': on, 'soon': soon, 'matched': shown.length, 'currency': snap.currency, 'items': rows, if (snap.offline) 'offline_cache': true});
  }),
);

// ------------------------------------------------------------------------------------------------ tariff_info

AssistantTool _tariffInfo(KnowledgeSource src, bool Function(Session) allowed) => AssistantTool(
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'владелец или администратор',
  decl: const ToolDecl(
    'tariff_info',
    'Read-only. Company plan with limits and server plan prices (ru: какой у меня тариф).',
  ),
  run: (r) => _guard(r.env, () async {
    final lang = r.env.lang;
    final snap = await src.marketplace();
    final plan = src.plan;
    String lim(int? v) => v == null ? '∞' : '$v';
    final plans = [
      for (final p in snap.tariffs.plans.take(6))
        {'code': p.code, 'name': p.names[lang] ?? p.names['ru'] ?? p.code, 'price_month': p.price, 'price_year': p.yearPrice, 'trial_days': p.trialDays},
    ];
    final own = plan.present
        ? tr5(
            lang,
            'Ваш тариф — ${plan.nameFor(lang)}: сотрудников ${lim(plan.limits.maxStaff)}, устройств ${lim(plan.limits.maxDevices)}, касс ${lim(plan.limits.maxCashboxes)}.',
            'Сиздин тариф — ${plan.nameFor(lang)}: кызматкер ${lim(plan.limits.maxStaff)}, түзмөк ${lim(plan.limits.maxDevices)}, касса ${lim(plan.limits.maxCashboxes)}.',
            'Sizning tarif — ${plan.nameFor(lang)}: xodim ${lim(plan.limits.maxStaff)}, qurilma ${lim(plan.limits.maxDevices)}, kassa ${lim(plan.limits.maxCashboxes)}.',
            'Your plan is ${plan.nameFor(lang)}: staff ${lim(plan.limits.maxStaff)}, devices ${lim(plan.limits.maxDevices)}, cashboxes ${lim(plan.limits.maxCashboxes)}.',
            'Tarifeniz ${plan.nameFor(lang)}: personel ${lim(plan.limits.maxStaff)}, cihaz ${lim(plan.limits.maxDevices)}, kasa ${lim(plan.limits.maxCashboxes)}.',
          )
        : tr5(lang, 'Тариф компании сервер не передал — лимиты не ограничены.', 'Компаниянын тарифин сервер бербеди — чектөөлөр жок.', "Kompaniya tarifini server bermadi — cheklovlar yo'q.", 'The server did not send the company plan — no limits apply.', 'Sunucu şirket tarifesini göndermedi — sınır yok.');
    final pub = plans.isEmpty
        ? tr5(lang, ' Цены тарифов не загрузились.', ' Тариф баалары жүктөлгөн жок.', " Tarif narxlari yuklanmadi.", ' Plan prices did not load.', ' Tarife fiyatları yüklenmedi.')
        : ' ${tr5(lang, 'Тарифы с сервера', 'Сервердин тарифтери', 'Serverdan tariflar', 'Server plans', 'Sunucu tarifeleri')}: ${[for (final p in plans) '${p['name']}${p['price_month'] == null ? '' : ' ${_num(p['price_month'] as double)} ${snap.currency}/${tr5(lang, 'мес', 'ай', 'oy', 'mo', 'ay')}'}'].join('; ')}.';
    return ToolResult(
      ToolStatus.ok,
      '$own$pub',
      data: {
        'plan': plan.present ? {'code': plan.code, 'name': plan.nameFor(lang), 'max_staff': plan.limits.maxStaff, 'max_devices': plan.limits.maxDevices, 'max_cashboxes': plan.limits.maxCashboxes} : null,
        'plans': plans,
        'currency': snap.currency,
        if (snap.offline) 'offline_cache': true,
      },
    );
  }),
);

// ------------------------------------------------------------------------------------------------ app_settings_info

/// Ключи, которые разрешено отдавать из настроек: всё остальное (даже если источник вернёт лишнее) отбрасывается.
const kSettingsAllowed = {'language', 'theme', 'shop_mode', 'simple_mode', 'assistant_mode', 'printer_ready', 'printer_paper', 'company', 'cashbox', 'role'};

AssistantTool _appSettingsInfo(KnowledgeSource src) => AssistantTool(
  group: ToolGroup.read,
  decl: const ToolDecl(
    'app_settings_info',
    'Read-only. Non-secret app settings: language, theme, shop mode, assistant mode, printer set up or not. No keys, tokens or PIN (ru: какие у меня настройки).',
  ),
  run: (r) => _guard(r.env, () async {
    final lang = r.env.lang;
    final raw = await src.settings();
    final d = <String, Object?>{for (final e in raw.entries) if (kSettingsAllowed.contains(e.key)) e.key: e.value};
    final printer = d['printer_ready'] == true;
    final say = tr5(
      lang,
      'Язык ${d['language']}, тема ${d['theme']}, режим магазина ${d['shop_mode']}, режим помощника ${d['assistant_mode']}, принтер ${printer ? 'настроен' : 'не настроен'}.',
      'Тил ${d['language']}, тема ${d['theme']}, дүкөн режими ${d['shop_mode']}, жардамчы режими ${d['assistant_mode']}, принтер ${printer ? 'жөндөлгөн' : 'жөндөлө элек'}.',
      "Til ${d['language']}, mavzu ${d['theme']}, do'kon rejimi ${d['shop_mode']}, yordamchi rejimi ${d['assistant_mode']}, printer ${printer ? "sozlangan" : "sozlanmagan"}.",
      'Language ${d['language']}, theme ${d['theme']}, shop mode ${d['shop_mode']}, assistant mode ${d['assistant_mode']}, printer ${printer ? 'set up' : 'not set up'}.',
      'Dil ${d['language']}, tema ${d['theme']}, mağaza modu ${d['shop_mode']}, yardımcı modu ${d['assistant_mode']}, yazıcı ${printer ? 'ayarlı' : 'ayarlı değil'}.',
    );
    return ToolResult(ToolStatus.ok, say, data: d.map((k, v) => MapEntry(k, v)).cast<String, dynamic>());
  }),
);

// ------------------------------------------------------------------------------------------------ suppliers_info

double? _amount(Map<String, dynamic> m) {
  for (final k in const ['total', 'total_amount', 'amount', 'sum', 'total_price']) {
    final v = m[k];
    if (v is num) return v.toDouble();
    if (v is String) {
      final n = double.tryParse(v.replaceAll(',', '.'));
      if (n != null) return n;
    }
  }
  return null;
}

String _str(Map<String, dynamic> m, List<String> keys) {
  for (final k in keys) {
    final v = m[k];
    if (v is String && v.trim().isNotEmpty) return v.trim();
  }
  return '';
}

AssistantTool _suppliersInfo(KnowledgeSource src) => AssistantTool(
  group: ToolGroup.read,
  allowed: (s) => s.canProcure,
  needs: 'Закупки или Поставщики',
  decl: ToolDecl(
    'suppliers_info',
    'Read-only. Suppliers (names only) and latest goods receipts (date, supplier, total) (ru: поставщики, последние приходы).',
    parameters: sObj({
      'query': sStr(desc: 'Supplier name part'),
      'receipts': sBool(desc: 'true = add latest receipts'),
      'limit': sInt(desc: 'Rows 1-10'),
    }),
  ),
  run: (r) => _guard(r.env, () async {
    final lang = r.env.lang;
    final q = foldText('${r.args['query'] ?? ''}').trim();
    final limitRaw = r.args['limit'];
    final limit = ((limitRaw is num ? limitRaw.toInt() : int.tryParse('${limitRaw ?? ''}')) ?? 6).clamp(1, 10);
    final all = await src.suppliers();
    final list = [for (final s in all) if (q.isEmpty || foldText(s.name).contains(q)) s];
    final names = [for (final s in list.take(limit)) _cut(s.name, 40)];
    final rec = <Map<String, Object?>>[];
    if (r.args['receipts'] == true) {
      final raw = await src.supplierReceipts(limit: limit);
      for (final m in raw.take(limit)) {
        final items = m['items'];
        rec.add({
          'date': _cut(_str(m, const ['created_at', 'date', 'created']), 10),
          'supplier': _cut(_str(m, const ['supplier_name', 'supplier']), 40),
          'total': _amount(m),
          'lines': items is List ? items.length : null,
        });
      }
    }
    final say = tr5(
      lang,
      'Поставщиков: ${list.length}${names.isEmpty ? '' : ' — ${names.join(', ')}'}.${rec.isEmpty ? '' : ' Последних приходов: ${rec.length}.'}',
      'Жеткирүүчүлөр: ${list.length}${names.isEmpty ? '' : ' — ${names.join(', ')}'}.${rec.isEmpty ? '' : ' Акыркы кирим: ${rec.length}.'}',
      "Yetkazib beruvchilar: ${list.length}${names.isEmpty ? '' : ' — ${names.join(', ')}'}.${rec.isEmpty ? '' : " Oxirgi kirimlar: ${rec.length}."}",
      'Suppliers: ${list.length}${names.isEmpty ? '' : ' — ${names.join(', ')}'}.${rec.isEmpty ? '' : ' Latest receipts: ${rec.length}.'}',
      'Tedarikçiler: ${list.length}${names.isEmpty ? '' : ' — ${names.join(', ')}'}.${rec.isEmpty ? '' : ' Son girişler: ${rec.length}.'}',
    );
    return ToolResult(ToolStatus.ok, say, data: {'suppliers_total': list.length, 'suppliers': names, if (rec.isNotEmpty) 'receipts': rec});
  }),
);

// ------------------------------------------------------------------------------------------------ whats_new

AssistantTool _whatsNew(ScreenRegistry reg) => AssistantTool(
  group: ToolGroup.read,
  decl: ToolDecl(
    'whats_new',
    'Read-only. What is new in the phone app (ru: что нового).',
    parameters: sObj({'limit': sInt(desc: 'Items 1-6')}),
  ),
  run: (r) async {
    final lang = r.env.lang;
    final s = r.env.session;
    final limitRaw = r.args['limit'];
    final limit = ((limitRaw is num ? limitRaw.toInt() : int.tryParse('${limitRaw ?? ''}')) ?? 5).clamp(1, 6);
    final i = _li(lang);
    final items = <String>[];
    for (final n in kWhatsNew) {
      final d = reg.byKey(n.screen);
      if (d != null && !d.allowedFor(s)) continue;
      items.add(n.text[i]);
      if (items.length >= limit) break;
    }
    if (items.isEmpty) return ToolResult(ToolStatus.notFound, tr5(lang, 'Новых пунктов для вашей роли нет.', 'Сиздин ролуңуз үчүн жаңы пункттар жок.', "Rolingiz uchun yangi bandlar yo'q.", 'No new items for your role.', 'Rolünüz için yeni madde yok.'));
    return ToolResult(ToolStatus.ok, items.join(' '), data: {'count': items.length});
  },
);
