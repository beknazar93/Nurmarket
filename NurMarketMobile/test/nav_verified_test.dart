// 2026-10-27 (расследование «ИИ открывает не тот раздел, говорит „открыла“, не открыв, и долго думает»): навигация ИИ на
// подставных хостах — результат проверяется РЕАЛЬНЫМ открытием экрана (NavVerifier), слова пользователя главнее выбора модели,
// ничья -> варианты и pick, локальный путь без ИИ < 100 мс, повтор ИИ после локального перехода не врёт «уже открыт», журнал
// замеров ai_perf без текста и ключей. Настоящий Gemini и эмулятор не нужны.
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_perf.dart';
import 'package:nurmarket/core/ai/assistant/nav_router.dart';
import 'package:nurmarket/core/ai/assistant/nav_tools.dart';
import 'package:nurmarket/core/ai/assistant/screen_registry.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/session_store.dart';

/// Хост с подтверждением: [arrives] — экран реально открывается; false — переход «начат», но экран не появился.
class _VHost implements NavHost, NavVerifier {
  @override
  String location = '/home';
  @override
  bool canGoBack = true;
  bool arrives = true;
  String? redirectTo; // роутер перенаправил (например /denied)
  final routes = <String>[];
  final aux = <String>[];
  final products = <String>[];
  String? auxShown;
  List<NavProduct> catalog = [];

  @override
  Future<void> openRoute(String l) async {
    routes.add(l);
    if (redirectTo != null) {
      location = redirectTo!;
    } else if (arrives) {
      location = l;
    }
  }

  @override
  Future<void> openAux(String key) async {
    aux.add(key);
    if (arrives) auxShown = key;
  }

  @override
  Future<bool> confirmed(String where, {Duration wait = const Duration(milliseconds: 1500)}) async {
    if (where.startsWith('/')) return Uri.parse(location).path == Uri.parse(where).path;
    return auxShown == where;
  }

  @override
  Future<void> goHome() async => location = '/home';
  @override
  Future<void> goBack() async {}
  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async {
    final words = foldTokens(query ?? '');
    return catalog.where((p) => words.every((w) => foldText(p.name).contains(w))).toList();
  }

  @override
  Future<void> openProduct(NavProduct p) async => products.add(p.id);
  @override
  Future<bool> openCheck({String? number, bool last = false}) async => true;
}

const _all = [
  Session.viewProducts, Session.viewAnalytics, Session.viewClients, Session.viewOrders, Session.viewShifts, Session.viewSettings,
  Session.procurement, Session.supplier, Session.editPrice, Session.sale,
];
Session _admin() => const Session(email: 'a', access: '', refresh: '', role: 'admin', isOwner: true, cashboxId: '1', perms: _all);
Session _cashier() => const Session(email: 'c', access: '', refresh: '', role: 'cashier', cashboxId: '1', perms: [Session.sale]);

int _n = 0;

Future<ToolResult> _run(ToolRegistry reg, Session s, String name, Map<String, dynamic> args, {UserSpeechLog? speech, String lang = 'ru'}) =>
    reg.execute(LiveToolCall('v${_n++}', name, args), ToolEnv(session: s, speech: speech ?? UserSpeechLog(), lang: lang));

UserSpeechLog _said(String text) => UserSpeechLog()..add(text);

(ToolRegistry, _VHost) _setup() {
  final host = _VHost();
  final reg = ToolRegistry();
  registerNavTools(reg, host, hostTimeout: const Duration(milliseconds: 40));
  return (reg, host);
}

void main() {
  group('результат навигации = факт открытия', () {
    test('экран открылся -> «Открыла: Склад → Приёмка с поставщиком», data.opened = true', () async {
      final (reg, host) = _setup();
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'receive'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, 'Открыла: Склад → Приёмка с поставщиком.');
      expect(r.data!['opened'], true);
      expect(host.location, '/admin/receive');
    });

    test('экран НЕ появился -> error «Не получилось открыть …», слова «Открыла» нет, data.opened = false', () async {
      final (reg, host) = _setup();
      host.arrives = false;
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'receive'});
      expect(r.status, ToolStatus.error);
      expect(r.say, startsWith('Не получилось открыть'));
      expect(r.say, isNot(contains('Открыла')));
      expect(r.data!['opened'], false);
    });

    test('роутер перенаправил в /denied -> denied «нет доступа», а не «открыла»', () async {
      final (reg, host) = _setup();
      host.redirectTo = '/denied';
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'summary'});
      expect(r.status, ToolStatus.denied);
      expect(r.say, contains('нет доступа'));
    });

    test('экран вне маршрутов: Связь с кассой, Ключи Gemini, Доступ к ИИ — через openAux с подтверждением', () async {
      final (reg, host) = _setup();
      for (final k in ['kassa_link', 'gemini_keys', 'ai_access']) {
        final r = await _run(reg, _admin(), 'open_screen', {'screen': k});
        expect(r.status, ToolStatus.ok, reason: k);
        expect(r.say, startsWith('Открыла: Профиль → '), reason: k);
      }
      expect(host.aux, ['kassa_link', 'gemini_keys', 'ai_access']);
      host.arrives = false;
      expect((await _run(reg, _admin(), 'open_screen', {'screen': 'printer'})).status, ToolStatus.error);
    });

    test('«Доступ к ИИ» — только владельцу / праву «Настройки»; кассиру denied, перехода нет', () async {
      final (reg, host) = _setup();
      final r = await _run(reg, _cashier(), 'open_screen', {'screen': 'ai_access'});
      expect(r.status, ToolStatus.denied);
      expect(host.aux, isEmpty);
    });

    test('«Режим тестировщика» — настоящий экран реестра (/tester)', () async {
      final (reg, host) = _setup();
      final r = await _run(reg, _cashier(), 'open_screen', {'screen': 'tester'});
      expect(r.status, ToolStatus.ok);
      expect(host.routes, ['/tester']);
    });
  });

  group('слова пользователя главнее выбора модели', () {
    test('сказано «открой приёмку», модель выбрала «Склад» -> открывается приёмка', () async {
      final (reg, host) = _setup();
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'stock'}, speech: _said('открой раздел приёмки'));
      expect(r.status, ToolStatus.ok);
      expect(host.location, '/admin/receive');
      expect(r.say, contains('Приёмка'));
    });

    test('модель выбрала «Сводку» (как на ПК: «Прибыль и деньги»), пользователь просил приёмку -> приёмка', () async {
      final (reg, host) = _setup();
      await _run(reg, _admin(), 'open_screen', {'screen': 'summary'}, speech: _said('открой приёмку'));
      expect(host.routes, ['/admin/receive']);
    });

    test('выбор модели совпал со словами пользователя -> не трогаем; неуверенный разбор -> тоже не трогаем', () async {
      final (reg, host) = _setup();
      await _run(reg, _admin(), 'open_screen', {'screen': 'stock'}, speech: _said('открой склад'));
      expect(host.routes, ['/stock']);
      host.location = '/home';
      await _run(reg, _admin(), 'open_screen', {'screen': 'summary'}, speech: _said('ну что там у нас по деньгам'));
      expect(host.routes.last, '/admin/summary');
    });

    test('эхо динамика (echoSuspect) слова пользователя не заменяют', () async {
      final (reg, host) = _setup();
      final sp = UserSpeechLog()..add('открой приёмку', echoSuspect: true);
      await _run(reg, _admin(), 'open_screen', {'screen': 'stock'}, speech: sp);
      expect(host.routes, ['/stock']);
    });
  });

  group('ничья -> варианты и pick', () {
    test('«табель» словами -> ambiguous: Смены / Зарплата, до 4 вариантов; pick 2 открывает второй', () async {
      final (reg, host) = _setup();
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'табель'});
      expect(r.status, ToolStatus.ambiguous);
      expect(r.options!.length, inInclusiveRange(2, 4));
      expect(host.routes, isEmpty);
      final second = await _run(reg, _admin(), 'open_screen', {'screen': 'табель', 'pick': 2});
      expect(second.status, ToolStatus.ok);
      expect(host.routes.length, 1);
    });

    test('схема open_screen содержит названия экранов (раньше — только ключи) и pick', () {
      final (reg, _) = _setup();
      final decl = reg.declarations(_admin()).firstWhere((d) => d['name'] == 'open_screen');
      final desc = decl['description'] as String;
      expect(desc, contains('receive=Склад → Приёмка с поставщиком'));
      expect(desc, contains('stock_receive=Склад → Приход (простой)'));
      expect(desc, contains('kassa_link=Профиль → Связь с кассой'));
      expect(desc, contains('NAMED'));
      expect(((decl['parameters'] as Map)['properties'] as Map).containsKey('pick'), isTrue);
      expect(ToolDecl.sizeOf(reg.declarations(_admin())), lessThan(7500));
    });
  });

  group('локальный путь без ИИ', () {
    test('«открой раздел приёмки» -> открыто и подтверждено за < 100 мс (без сети и ИИ)', () async {
      final host = _VHost();
      final sw = Stopwatch()..start();
      final d = parseNavCommand('э-э открой раздел приёмки', session: _admin());
      final res = await LocalNav(host).run(d, _admin());
      sw.stop();
      expect(res.status, ToolStatus.ok);
      expect(res.say, 'Открыла: Склад → Приёмка с поставщиком.');
      expect(host.location, '/admin/receive');
      expect(sw.elapsedMilliseconds, lessThan(100));
    });

    test('не открылось -> «Не получилось открыть …»; нет права -> denied; ничья -> ambiguous с кнопками', () async {
      final host = _VHost()..arrives = false;
      var res = await LocalNav(host).run(parseNavCommand('открой ревизию'), _admin());
      expect(res.status, ToolStatus.error);
      expect(res.say, startsWith('Не получилось открыть «Склад → Ревизия»'));
      host.arrives = true;
      res = await LocalNav(host).run(parseNavCommand('открой сводку'), _cashier());
      expect(res.status, ToolStatus.denied);
      res = await LocalNav(host).run(parseNavCommand('открой табель', session: _admin()), _admin());
      expect(res.status, ToolStatus.ambiguous);
      expect(res.options!.length, inInclusiveRange(2, 4));
    });

    test('«Списание» -> честная подсказка; «открой что-нибудь» -> «не нашла»', () async {
      final host = _VHost();
      var res = await LocalNav(host).run(parseNavCommand('открой списание'), _admin());
      expect(res.say, contains('карточке товара'));
      expect(host.routes, isEmpty);
      res = await LocalNav(host).run(parseNavCommand('открой что-нибудь'), _admin());
      expect(res.status, ToolStatus.notFound);
    });

    test('чек по номеру и товар по названию (ничья -> варианты, pick)', () async {
      final host = _VHost()
        ..catalog = [NavProduct('1', 'Кола 1,5 л', price: 120), NavProduct('2', 'Кола 0,5 л', price: 60)];
      final nav = LocalNav(host);
      var res = await nav.run(parseNavCommand('открой чек 1043'), _admin());
      expect(res.status, ToolStatus.ok);
      final d = parseNavCommand('открой товар кола');
      res = await nav.run(d, _admin());
      expect(res.status, ToolStatus.ambiguous);
      expect(host.products, isEmpty);
      res = await nav.run(d, _admin(), pick: 2);
      expect(res.status, ToolStatus.ok);
      expect(host.products, ['1']);
    });

    test('локальный переход и повторный вызов ИИ того же экрана: ИИ получает «Открыла», а не «Уже открыт»; второго перехода нет', () async {
      final host = _VHost();
      final reg = ToolRegistry();
      registerNavTools(reg, host, hostTimeout: const Duration(milliseconds: 40));
      await LocalNav(host).run(parseNavCommand('открой приёмку'), _admin());
      final r = await _run(reg, _admin(), 'open_screen', {'screen': 'receive'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, startsWith('Открыла'));
      expect(host.routes, ['/admin/receive']);
    });

    test('ответы на 5 языков: открыла / не получилось', () async {
      for (final (lang, ok, fail) in [
        ('ru', 'Открыла', 'Не получилось'),
        ('ky', 'Ачтым', 'ачуу мүмкүн болбоду'),
        ('uz', 'Ochdim', 'ochib bo'),
        ('en', 'Opened', 'Could not open'),
        ('tr', 'Açtım', 'açılamadı'),
      ]) {
        final host = _VHost();
        final res = await LocalNav(host).run(parseNavCommand('открой склад'), _admin(), lang: lang);
        expect(res.say, startsWith(ok), reason: lang);
        final bad = _VHost()..arrives = false;
        final f = await LocalNav(bad).run(parseNavCommand('открой склад'), _admin(), lang: lang);
        expect(f.say, contains(fail), reason: lang);
      }
    });
  });

  group('журнал замеров ai_perf', () {
    test('этапы пишутся в журнал без текста вопросов и ключей; строк не больше 200', () async {
      final dir = Directory.systemTemp.createTempSync('aiperf');
      addTearDown(() => dir.deleteSync(recursive: true));
      AiPerf.testDirectory = dir;
      AiPerf.echo = false;
      AiPerf.clearForTests();
      final t = AiPerfTrace('chat.llm')
        ..tag('ctx', 'skip')
        ..tag('model', 'gemini-3.5-flash-lite')
        ..ms('key', 3)
        ..ms('ask', 1800);
      t.finish('ok');
      t.finish('again'); // повтор молчит
      await AiPerf.flush();
      expect(AiPerf.recent.length, 1);
      final text = File('${dir.path}/ai_perf.log').readAsStringSync();
      expect(text, contains('chat.llm'));
      expect(text, contains('ask=1800ms'));
      expect(text, contains('total='));
      expect(text, isNot(contains('AIza')));
      expect(jsonEncode(AiPerf.recent.first.tags), isNot(contains('открой')));
      for (var i = 0; i < 230; i++) {
        AiPerf.record('chat.nav', {'open': i});
      }
      await AiPerf.flush();
      expect(File('${dir.path}/ai_perf.log').readAsLinesSync().length, lessThanOrEqualTo(AiPerf.maxLines));
      AiPerf.testDirectory = null;
      AiPerf.echo = true;
    });
  });
}
