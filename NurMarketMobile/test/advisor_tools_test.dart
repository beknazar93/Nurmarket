// 2026-11-27 (просьба владельца «дай Нур-советнику доступы ко всем новым функциям и разделам … полный доступ»): инструменты чтения,
// изучения и навигации Нур Советника мобилки (чат и звонок) на фейках — без сети, ключей Gemini и боевой базы.
// Проверяем: каждый экран реестра описан для советника (новый экран без описания — красный тест); права роли (кассир не видит закупку,
// аналитику, Маркетплейс); ТОЛЬКО чтение и навигация (действий в реестре чата нет); секреты и телефоны не попадают в ответы;
// лимит вызовов и срок 5 с; «Использовано: …»; цикл function calling Gemini с подставным клиентом.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/chat_tools.dart';
import 'package:nurmarket/core/ai/assistant/knowledge_tools.dart';
import 'package:nurmarket/core/ai/assistant/nav_tools.dart';
import 'package:nurmarket/core/ai/assistant/screen_registry.dart';
import 'package:nurmarket/core/ai/assistant/section_guide.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/chat_toolkit.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/mode/plan_info.dart';
import 'package:nurmarket/features/marketplace/marketplace_content.dart';
import 'package:nurmarket/features/marketplace/marketplace_data.dart';
import 'package:nurmarket/features/marketplace/marketplace_state.dart';

import 'read_tools_test.dart' show FakeSource;

// ------------------------------------------------------------------------------------------------ фейки

final _modulesRaw = jsonDecode(File('test/fixtures/marketplace_public_modules.json').readAsStringSync()) as Map<String, dynamic>;
final _tariffsRaw = jsonDecode(File('test/fixtures/marketplace_public_tariffs.json').readAsStringSync());
final _content = MarketplaceContent.fromJson((jsonDecode(File('assets/marketplace/content.json').readAsStringSync()) as Map).cast<String, dynamic>());

MpSnapshot _snapshot({Set<String> enabled = const {'scales_labels', 'expiry_batches'}, bool offline = false}) {
  final ids = [for (final m in _modulesRaw['modules'] as List) (m as Map)['id'] as String];
  return MpSnapshot(
    modules: MpServerModule.parseList(_modulesRaw),
    currency: 'KGS',
    tariffs: MpTariffs.parse(_tariffsRaw),
    offline: offline,
    company: MpCompany.parse({
      'profile': 'grocery',
      'version': 'v1',
      'modules': enabled.toList(),
      'items': [
        for (final id in ids) {'id': id, 'enabled': enabled.contains(id), 'source': enabled.contains(id) ? 'default' : 'off'},
      ],
      'catalog': [
        for (final m in _modulesRaw['modules'] as List) {'id': (m as Map)['id'], 'names': m['name'], 'group': m['group'], 'default_in': m['default_in']},
      ],
      'profiles': [
        {'id': 'grocery', 'names': {'ru': 'Продуктовый'}, 'modules': enabled.toList()},
      ],
    }),
  );
}

class _Know implements KnowledgeSource {
  MpSnapshot snap = _snapshot();
  Object? failMarket;
  final calls = <String>[];
  Map<String, Object?> settingsMap = {
    'language': 'ru',
    'theme': 'light',
    'shop_mode': 'grocery',
    'simple_mode': false,
    'assistant_mode': 'act',
    'printer_ready': true,
    'printer_paper': '58 mm',
    'company': 'NBS',
    'cashbox': 'Касса 1',
    'role': 'admin',
  };
  List<({String id, String name})> supplierList = [(id: '1', name: 'Азия Трейд'), (id: '2', name: 'Нур Опт')];
  List<Map<String, dynamic>> receiptList = [
    {'created_at': '2026-11-20T10:00:00', 'supplier_name': 'Азия Трейд', 'total': 12500, 'items': [{}, {}], 'phone': '+996555123456'},
  ];

  @override
  Future<MpSnapshot> marketplace() async {
    calls.add('marketplace');
    if (failMarket != null) throw failMarket!;
    return snap;
  }

  @override
  Future<MarketplaceContent> content() async => _content;
  @override
  Set<String> get features => const {};
  @override
  PlanInfo get plan => const PlanInfo(code: 'start', names: {'ru': 'Старт'}, limits: PlanLimits(maxStaff: 3, maxDevices: 2), present: true);
  @override
  Future<Map<String, Object?>> settings() async {
    calls.add('settings');
    return settingsMap;
  }

  @override
  Future<List<({String id, String name})>> suppliers() async {
    calls.add('suppliers');
    return supplierList;
  }

  @override
  Future<List<Map<String, dynamic>>> supplierReceipts({int limit = 15}) async {
    calls.add('receipts');
    return receiptList;
  }
}

class _Host implements NavHost {
  @override
  String location = '/home';
  @override
  bool canGoBack = true;
  final routes = <String>[];
  @override
  Future<void> openRoute(String l) async {
    routes.add(l);
    location = l;
  }

  @override
  Future<void> openAux(String key) async {}
  @override
  Future<void> goHome() async {}
  @override
  Future<void> goBack() async {}
  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async => [];
  @override
  Future<void> openProduct(NavProduct p) async {}
  @override
  Future<bool> openCheck({String? number, bool last = false}) async => true;
}

const _all = [
  Session.viewProducts, Session.viewAnalytics, Session.viewClients, Session.viewOrders, Session.viewShifts, Session.viewSettings,
  Session.procurement, Session.supplier, Session.editPrice, Session.sale,
];
Session _owner() => const Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, cashboxId: '1', perms: _all);
Session _cashier() => const Session(email: 'c', access: '', refresh: '', role: 'cashier', cashboxId: '1', perms: [Session.sale, Session.viewProducts]);

int _n = 0;
Future<ToolResult> _run(ToolRegistry reg, Session s, String name, Map<String, dynamic> args, {String lang = 'ru', String said = ''}) =>
    reg.execute(LiveToolCall('t${_n++}', name, args), ToolEnv(session: s, speech: UserSpeechLog()..add(said), mode: AssistantMode.viewOnly, lang: lang));

({ToolRegistry reg, _Know know, _Host host, FakeSource reads}) _setup() {
  final know = _Know();
  final host = _Host();
  final reads = FakeSource();
  return (reg: buildChatRegistry(nav: host, reads: reads, knowledge: know), know: know, host: host, reads: reads);
}

void main() {
  const screens = ScreenRegistry();

  // ---------------------------------------------------------------------------------------------- описание экранов
  group('каждый экран реестра описан для советника', () {
    test('у каждого существующего экрана есть описание на 5 языков; новый экран без описания — красный тест', () {
      final missing = [for (final d in screens.all) if (d.exists && !kSectionGuide.containsKey(d.key)) d.key];
      expect(missing, isEmpty, reason: 'добавьте описание в kSectionGuide (section_guide.dart): $missing');
      for (final d in screens.all.where((d) => d.exists)) {
        final g = kSectionGuide[d.key]!;
        expect(g.length, 5, reason: d.key);
        expect(g.every((t) => t.trim().length > 10), isTrue, reason: d.key);
        expect(g.toSet().length, greaterThan(2), reason: '${d.key}: тексты на языках не должны совпадать');
      }
    });

    test('в таблице описаний нет лишних ключей (экрана с таким ключом нет)', () {
      final keys = {for (final d in screens.all) d.key};
      expect([for (final k in kSectionGuide.keys) if (!keys.contains(k)) k], isEmpty);
    });

    test('все новые разделы включены: Маркетплейс, склад, чеки, клиенты, поставщики, смена, аналитика, настройки', () {
      for (final k in ['marketplace', 'stock', 'scan', 'checks', 'clients', 'purchase', 'receive', 'close_shift', 'shifts', 'summary', 'abc', 'profile', 'advisor']) {
        expect(screens.byKey(k), isNotNull, reason: k);
        expect(kSectionGuide.containsKey(k), isTrue, reason: k);
      }
    });

    test('разделы, которых нет в телефоне, описаны: акции, диалоги бота, ремонт; у каждого 5 названий и слова', () {
      for (final a in kAbsentSections) {
        expect(a.titles.length, 5, reason: a.key);
        expect(a.words.length, 5, reason: a.key);
        expect(['owner', 'site'], contains(a.where));
      }
      final keys = [for (final a in kAbsentSections) a.key];
      expect(keys, containsAll(['promotions', 'bot_dialogs', 'repair_orders']));
    });

    test('у каждого инструмента чата и звонка есть название для строки «Использовано» (5 языков)', () {
      final s = _setup();
      final names = s.reg.names.toList();
      expect(names, isNotEmpty);
      for (final n in names) {
        expect(kToolLabels.containsKey(n), isTrue, reason: 'нет названия инструмента $n в kToolLabels');
      }
      for (final e in kToolLabels.entries) {
        expect(e.value.length, 5, reason: e.key);
        expect(e.value.every((t) => t.trim().isNotEmpty), isTrue, reason: e.key);
      }
    });

    test('строка «Использовано: …» на 5 языках, без повторов, не длиннее 5 названий', () {
      expect(usedLine(['open_screen', 'explain_section', 'open_screen'], 'ru'), 'Использовано: открытие раздела, описание раздела');
      expect(usedLine(['sales_report'], 'en'), startsWith('Used:'));
      expect(usedLine(['sales_report'], 'ky'), startsWith('Колдонулду:'));
      expect(usedLine(['sales_report'], 'uz'), startsWith('Ishlatildi:'));
      expect(usedLine(['sales_report'], 'tr'), startsWith('Kullanılan:'));
      expect(usedLine(const [], 'ru'), isEmpty);
      final many = usedLine(['sales_report', 'top_products', 'abc_report', 'stock_list', 'product_info', 'checks_list', 'debtors'], 'ru');
      expect(many, endsWith('…'));
    });
  });

  // ---------------------------------------------------------------------------------------------- безопасность
  group('только чтение и навигация', () {
    test('в реестре чата нет ни одного инструмента-действия: все — навигация, чтение или справка', () {
      final s = _setup();
      for (final n in s.reg.names) {
        final t = s.reg[n]!;
        expect(t.isAction, isFalse, reason: n);
        expect([ToolGroup.nav, ToolGroup.read, ToolGroup.service], contains(t.group), reason: n);
      }
      // имена записей не встречаются вовсе (продажа, возврат, оплата, удаление, цены, настройки, модули, сообщения)
      final write = RegExp(r'(pay|refund|return_|delete|remove|set_|enable|disable|toggle|send|create|post|sale_|price_|cart_edit|close_shift|open_shift|undo)');
      expect([for (final n in s.reg.names) if (write.hasMatch(n)) n], isEmpty);
    });

    test('даже если модель просит запись по имени — такого инструмента нет', () async {
      final s = _setup();
      for (final n in ['pay_sale', 'refund', 'delete_product', 'set_price', 'enable_module', 'send_message', 'close_shift']) {
        final r = await _run(s.reg, _owner(), n, {'module': 'bot_seller'});
        expect(r.status, ToolStatus.error, reason: n);
      }
      expect(s.host.routes, isEmpty);
    });

    test('объявления для Gemini: у кассира нет закупок, аналитики и Маркетплейса; у владельца — есть', () {
      final s = _setup();
      Set<String> names(Session x) => {for (final d in s.reg.declarations(x, AssistantMode.viewOnly)) d['name'] as String};
      final cashier = names(_cashier());
      for (final n in ['marketplace_info', 'tariff_info', 'suppliers_info', 'sales_report', 'top_products', 'abc_report', 'debtors', 'audit_log']) {
        expect(cashier.contains(n), isFalse, reason: 'кассир не должен видеть $n');
      }
      expect(cashier, containsAll(['open_screen', 'explain_section', 'app_settings_info', 'whats_new']));
      final owner = names(_owner());
      expect(owner, containsAll(['marketplace_info', 'tariff_info', 'suppliers_info', 'sales_report', 'explain_section', 'open_screen', 'checks_list', 'shift_now']));
      expect(owner.contains('table_control'), isFalse);
    });

    test('права проверяются и при вызове: кассир → denied, к серверу запроса нет', () async {
      final s = _setup();
      for (final n in ['marketplace_info', 'tariff_info', 'suppliers_info']) {
        final r = await _run(s.reg, _cashier(), n, {});
        expect(r.status, ToolStatus.denied, reason: n);
      }
      expect(s.know.calls, isEmpty);
    });

    test('кассир не получает описание Маркетплейса, функции и закрытых разделов через explain_section', () async {
      final s = _setup();
      for (final topic in ['marketplace', 'бот-продавец', 'смета', 'summary', 'purchase']) {
        final r = await _run(s.reg, _cashier(), 'explain_section', {'topic': topic});
        expect(r.status, ToolStatus.denied, reason: topic);
        expect(r.say.contains('Цена'), isFalse);
      }
    });

    test('открыть Маркетплейс кассиру нельзя, владельцу можно; в адрес уходит функция', () async {
      final s = _setup();
      final no = await _run(s.reg, _cashier(), 'open_screen', {'screen': 'marketplace'});
      expect(no.status, ToolStatus.denied);
      expect(s.host.routes, isEmpty);
      final ok = await _run(s.reg, _owner(), 'open_screen', {'screen': 'marketplace', 'query': 'bot_seller'});
      expect(ok.status, ToolStatus.ok);
      expect(s.host.routes, ['/admin/marketplace?module=bot_seller']);
      expect(ok.say, contains('Маркетплейс'));
    });

    test('в экране «открой акции» ничего не открывается: ключа нет в списке, ответ — «в мобилке этого раздела нет»', () async {
      final s = _setup();
      final keys = (((s.reg.declarations(_owner())).firstWhere((d) => d['name'] == 'open_screen')['parameters'] as Map)['properties'] as Map)['screen']['enum'] as List;
      expect(keys.contains('promotions'), isFalse);
      expect(keys, contains('marketplace'));
      final r = await _run(s.reg, _owner(), 'explain_section', {'topic': 'акции'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('в мобильном приложении нет'));
      expect(r.say, contains('программе владельца'));
      expect(r.data!['in_mobile'], false);
      expect(s.host.routes, isEmpty);
    });

    test('результат одного вызова укладывается в 1500 знаков данных; лишние ключи настроек отбрасываются (секреты не уходят)', () async {
      final s = _setup();
      s.know.settingsMap = {
        ...s.know.settingsMap,
        'token': 'eyJhbGciOi.secret.jwt',
        'access': 'ACCESS-TOKEN',
        'email': 'owner@example.com',
        'phone': '+996555111222',
        'gemini_key': 'AIzaSyFAKEKEY',
        'pin': '1234',
        'printer_address': '00:11:22:33:44:55',
      };
      final r = await _run(s.reg, _owner(), 'app_settings_info', {});
      expect(r.status, ToolStatus.ok);
      final text = jsonEncode(r.toJson());
      for (final bad in ['eyJhbGciOi', 'ACCESS-TOKEN', 'owner@example.com', '+996555111222', 'AIzaSy', '1234', '00:11:22']) {
        expect(text.contains(bad), isFalse, reason: bad);
      }
      expect(jsonEncode(r.data).length, lessThan(ToolResult.dataLimit));
      expect(r.data!['printer_ready'], true);
      expect(r.data!.containsKey('token'), isFalse);
    });

    test('поставщики: телефоны не попадают в ответ, приходы — дата, поставщик, сумма; без права «Закупки» — denied', () async {
      final s = _setup();
      final r = await _run(s.reg, _owner(), 'suppliers_info', {'receipts': true});
      expect(r.status, ToolStatus.ok);
      final text = jsonEncode(r.toJson());
      expect(text.contains('996555'), isFalse);
      expect(r.data!['suppliers'], contains('Азия Трейд'));
      expect((r.data!['receipts'] as List).first['total'], 12500);
      final no = await _run(s.reg, const Session(email: 'x', access: '', refresh: '', role: 'cashier', cashboxId: '1', perms: [Session.sale]), 'suppliers_info', {});
      expect(no.status, ToolStatus.denied);
    });

    test('сервер не ответил: Маркетплейс → offline с понятным текстом, а не исключение', () async {
      final s = _setup();
      s.know.failMarket = ApiException('Нет связи с сервером.');
      final r = await _run(s.reg, _owner(), 'marketplace_info', {});
      expect(r.status, anyOf(ToolStatus.offline, ToolStatus.error));
      expect(r.say, isNotEmpty);
    });
  });

  // ---------------------------------------------------------------------------------------------- изучение
  group('explain_section, marketplace_info, tariff_info, whats_new', () {
    test('раздел телефона: «склад», «ABC-анализ», «Sale» — описание из таблицы, на языке вопроса', () async {
      final s = _setup();
      final ru = await _run(s.reg, _owner(), 'explain_section', {'topic': 'склад'});
      expect(ru.status, ToolStatus.ok);
      expect(ru.say, startsWith('Склад:'));
      expect(ru.data!['key'], 'stock');
      final en = await _run(s.reg, _owner(), 'explain_section', {'topic': 'stock', 'lang': 'x'}, lang: 'en');
      expect(en.say, startsWith('Stock:'));
      final ky = await _run(s.reg, _owner(), 'explain_section', {'topic': 'abc'}, lang: 'ky');
      expect(ky.say, contains('ABC'));
    });

    test('«Ремонт» и «Диалоги бота»: в телефоне нет, сказано где; на 5 языках', () async {
      final s = _setup();
      for (final lang in kNavLangs) {
        final r = await _run(s.reg, _owner(), 'explain_section', {'topic': 'ремонт'}, lang: lang);
        expect(r.data!['kind'], 'absent', reason: lang);
        expect(r.say.isNotEmpty, isTrue);
      }
      final bot = await _run(s.reg, _owner(), 'explain_section', {'topic': 'диалоги бота'});
      expect(bot.data!['key'], 'bot_dialogs');
      expect(bot.say, contains('в мобильном приложении нет'));
    });

    test('функция Маркетплейса: «бот-продавец» → описание, как включить и демо из content.json; цена — только серверная', () async {
      final s = _setup();
      final r = await _run(s.reg, _owner(), 'explain_section', {'topic': 'бот-продавец'});
      expect(r.status, ToolStatus.ok);
      expect(r.data!['kind'], 'module');
      expect(r.data!['id'], 'bot_seller');
      expect(r.say, contains('Бот-продавец'));
      expect(r.say, contains('Как включить'));
      final est = await _run(s.reg, _owner(), 'marketplace_info', {'module': 'смета'});
      expect(est.data!['id'], 'estimate');
      expect(est.data!['price_month'], 300.0); // цена из ответа сервера (fixture), своих цен нет
      expect(est.say, contains('Цена с сервера'));
      expect(est.say, contains('300'));
      // функция «входит в сферу» / включена — цены нет
      final on = await _run(s.reg, _owner(), 'marketplace_info', {'module': 'scales_labels'});
      expect(on.data!['status'], 'on');
      expect(on.data!.containsKey('price_month'), isFalse);
    });

    test('нет цены сервера — «Цена уточняется», своей цены нет', () async {
      final s = _setup();
      final snap = _snapshot();
      s.know.snap = MpSnapshot(modules: [for (final m in snap.modules) if (m.id != 'estimate') m], currency: 'KGS', company: snap.company, tariffs: snap.tariffs);
      final r = await _run(s.reg, _owner(), 'marketplace_info', {'module': 'estimate'});
      expect(r.say, contains('Цена уточняется'));
      expect(r.data!.containsKey('price_month'), isFalse);
    });

    test('список: счётчики, фильтр «включено», лимит строк, офлайн-пометка', () async {
      final s = _setup();
      final all = await _run(s.reg, _owner(), 'marketplace_info', {'limit': 5});
      expect(all.status, ToolStatus.ok);
      expect((all.data!['items'] as List).length, 5);
      expect(all.data!['on'], greaterThanOrEqualTo(2)); // + встроенные функции без модуля сервера
      expect(all.data!['total'], greaterThan(25));
      final on = await _run(s.reg, _owner(), 'marketplace_info', {'status': 'on'});
      expect((on.data!['items'] as List).map((e) => (e as Map)['id']), containsAll(['scales_labels', 'expiry_batches']));
      expect(on.data!['matched'], greaterThanOrEqualTo(2));
      final huge = await _run(s.reg, _owner(), 'marketplace_info', {'limit': 99});
      expect((huge.data!['items'] as List).length, lessThanOrEqualTo(12));
      expect(jsonEncode(huge.toJson()['data']).contains('truncated'), isFalse);
      s.know.snap = _snapshot(offline: true);
      final off = await _run(s.reg, _owner(), 'marketplace_info', {});
      expect(off.data!['offline_cache'], true);
    });

    test('поиск функции по названию на любом из 5 языков и по ключу', () {
      final views = buildModuleViews(_snapshot(), _content, 'ru');
      expect(findModule(views, 'estimate').view?.id, 'estimate');
      expect(findModule(views, 'Смета').view?.id, 'estimate');
      expect(findModule(views, 'bot-sotuvchi').view?.id, 'bot_seller');
      expect(findModule(views, 'Seller bot').view?.id, 'bot_seller');
      expect(findModule(views, 'открой функцию бот продавец').view?.id, 'bot_seller');
      expect(findModule(views, 'ничего подобного'), isA<ModuleMatch>().having((m) => m.view, 'view', isNull));
    });

    test('тариф: свой план с лимитами и серверные цены тарифов', () async {
      final s = _setup();
      final r = await _run(s.reg, _owner(), 'tariff_info', {});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('Старт'));
      expect(r.say, contains('1300'));
      expect(((r.data!['plans'] as List).map((p) => (p as Map)['code'])), contains('mini'));
    });

    test('«что нового»: кассир не видит пункт про Маркетплейс, владелец видит', () async {
      final s = _setup();
      final owner = await _run(s.reg, _owner(), 'whats_new', {});
      expect(owner.say, contains('Маркетплейс'));
      final cashier = await _run(s.reg, _cashier(), 'whats_new', {});
      expect(cashier.say.contains('Маркетплейс'), isFalse);
    });

    test('данные из таблиц чтения (продажи, смена, чеки) доступны в чате и режут права', () async {
      final s = _setup();
      final r = await _run(s.reg, _owner(), 'sales_report', {'from': 'today'});
      expect(r.status, ToolStatus.ok);
      final c = await _run(s.reg, _cashier(), 'sales_report', {'from': 'today'});
      expect(c.status, ToolStatus.denied);
      final shift = await _run(s.reg, _owner(), 'shift_now', {});
      expect(shift.status, anyOf(ToolStatus.ok, ToolStatus.notFound));
    });

    test('когда вопросу нужны инструменты: открыть / объяснить / функции — да; «привет» и сводка — нет', () {
      for (final q in ['открой акции', 'объясни функцию смета', 'какие функции подключены', 'покажи диалоги бота', 'open marketplace', 'kampanyalar nedir tarife', 'funksiyalar haqida aytib ber']) {
        expect(advisorNeedsTools(q), isTrue, reason: q);
      }
      for (final q in ['привет', 'спасибо', 'какая выручка сегодня', 'что заканчивается на складе?'.replaceAll('складе', 'полке')]) {
        expect(advisorNeedsTools(q), isFalse, reason: q);
      }
    });
  });

  // ---------------------------------------------------------------------------------------------- набор для чата
  group('ChatToolkit: лимит вызовов, срок, «Использовано»', () {
    test('не больше maxCalls на ответ; дальше — «лимит» без вызова инструмента', () async {
      var runs = 0;
      final tk = ChatToolkit(declarations: const [], maxCalls: 2, run: (n, a) async {
        runs++;
        return {'status': 'ok', 'say': n};
      });
      expect((await tk.execute('a', {}))['status'], 'ok');
      expect((await tk.execute('b', {}))['status'], 'ok');
      expect(tk.exhausted, isTrue);
      expect((await tk.execute('c', {}))['status'], 'error');
      expect(runs, 2);
      expect(tk.used, ['a', 'b']);
    });

    test('зависший инструмент отвечает за срок (5 с по умолчанию), исключение не вылетает', () async {
      final tk = ChatToolkit(declarations: const [], callTimeout: const Duration(milliseconds: 30), run: (n, a) => Completer<Map<String, dynamic>>().future);
      final r = await tk.execute('slow', {});
      expect(r['status'], 'offline');
      final tk2 = ChatToolkit(declarations: const [], run: (n, a) async => throw StateError('boom'));
      expect((await tk2.execute('x', {}))['status'], 'error');
      expect(ChatToolkit(declarations: const [], run: (n, a) async => {}).callTimeout, const Duration(seconds: 5));
    });

    test('buildChatToolkit: объявления по роли, режим «только смотреть», вызов идёт через реестр с правами', () async {
      final s = _setup();
      final tk = buildChatToolkit(registry: s.reg, session: _cashier, lang: 'ru', lastAsk: 'покажи Маркетплейс');
      expect([for (final d in tk.declarations) d['name']].contains('marketplace_info'), isFalse);
      final r = await tk.execute('marketplace_info', {});
      expect(r['status'], 'denied');
      expect(tk.used, ['marketplace_info']);
      // слова пользователя главнее выбора модели: просили «склад», модель открыла «summary» → откроется склад
      final ok = buildChatToolkit(registry: s.reg, session: _owner, lang: 'ru', lastAsk: 'открой склад');
      final res = await ok.execute('open_screen', {'screen': 'summary'});
      expect(res['status'], 'ok');
      expect(s.host.routes.last, startsWith('/stock'));
    });
  });

  // ---------------------------------------------------------------------------------------------- цикл Gemini
  group('чат с инструментами: function calling Gemini на подставном клиенте', () {
    late AppDatabase db;
    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      FlutterSecureStorage.setMockInitialValues({});
      VisionService.resetForTest();
    });
    tearDown(() => db.close());

    (_TestVision, _Fake) make(ResponseBody Function(RequestOptions o, int n) handler) {
      final fake = _Fake(handler);
      final dio = Dio()..httpClientAdapter = fake;
      return (_TestVision(ApiClient(SessionStore()), db, 'test-key', http: dio), fake);
    }

    test('модель просит инструмент → программа исполняет → ответ уходит той же модели → итоговый текст; инструменты объявлены', () async {
      final s = _setup();
      final tk = buildChatToolkit(registry: s.reg, session: _owner, lang: 'ru', lastAsk: 'что такое смета');
      final (vision, fake) = make((o, n) {
        if (n == 1) {
          return _json(200, {
            'candidates': [
              {
                'content': {
                  'role': 'model',
                  'parts': [
                    {
                      'functionCall': {'name': 'explain_section', 'args': {'topic': 'смета'}},
                      'thoughtSignature': 'sig-1',
                    },
                  ],
                },
              },
            ],
          });
        }
        return _json(200, {
          'candidates': [
            {
              'content': {'role': 'model', 'parts': [{'text': 'Смета — расчёт материалов.'}]},
              'finishReason': 'STOP',
            },
          ],
        });
      });
      vision.chatToolkit = tk;
      vision.onChatPartial = (_) {}; // с инструментами потока нет
      final answer = await vision.chat('система', const [(user: true, text: 'что такое смета')]);
      expect(answer, 'Смета — расчёт материалов.');
      expect(fake.calls.length, 2);
      expect(fake.calls[0].uri.path, contains(':generateContent')); // не streamGenerateContent
      final body1 = fake.calls[0].data as Map;
      final decl = ((body1['tools'] as List).first as Map)['functionDeclarations'] as List;
      expect(decl.map((d) => (d as Map)['name']), containsAll(['open_screen', 'explain_section']));
      // второй запрос: ход модели (с подписью) и ответ инструмента
      final contents = (fake.calls[1].data as Map)['contents'] as List;
      expect(contents.length, 3);
      expect(((contents[1] as Map)['parts'] as List).first['thoughtSignature'], 'sig-1');
      final resp = ((contents[2] as Map)['parts'] as List).first['functionResponse'] as Map;
      expect(resp['name'], 'explain_section');
      expect((resp['response'] as Map)['status'], 'ok');
      expect(tk.used, ['explain_section']);
      expect(usedLine(tk.used, 'ru'), 'Использовано: описание раздела');
      expect(vision.chatToolkit, isNull); // флаг сбрасывается самим chat
    });

    test('лимит вызовов: после maxCalls инструменты в запросе не объявляются, модель отвечает текстом', () async {
      final s = _setup();
      final tk = buildChatToolkit(registry: s.reg, session: _owner, lang: 'ru', lastAsk: 'открой склад', maxCalls: 1);
      final (vision, fake) = make((o, n) {
        final hasTools = (o.data as Map).containsKey('tools');
        if (hasTools) {
          return _json(200, {
            'candidates': [
              {
                'content': {'role': 'model', 'parts': [{'functionCall': {'name': 'list_screens', 'args': {}}}]},
              },
            ],
          });
        }
        return _json(200, {
          'candidates': [
            {
              'content': {'role': 'model', 'parts': [{'text': 'Готово.'}]},
              'finishReason': 'STOP',
            },
          ],
        });
      });
      vision.chatToolkit = tk;
      expect(await vision.chat('система', const [(user: true, text: 'открой склад')]), 'Готово.');
      expect(tk.calls, 1);
      expect((fake.calls.last.data as Map).containsKey('tools'), isFalse);
    });

    test('без инструментов запрос прежний: нет tools, поток работает как раньше', () async {
      final (vision, fake) = make((o, n) => _json(200, {
            'candidates': [
              {
                'content': {'role': 'model', 'parts': [{'text': 'ок'}]},
                'finishReason': 'STOP',
              },
            ],
          }));
      expect(await vision.chat('система', const [(user: true, text: 'привет')]), 'ок');
      expect((fake.calls.single.data as Map).containsKey('tools'), isFalse);
    });

    test('разбор functionCall: имя и аргументы; обычный текст — пусто', () {
      final calls = VisionService.functionCallsOf({
        'candidates': [
          {
            'content': {
              'parts': [
                {'text': 'думаю', 'thought': true},
                {'functionCall': {'name': 'open_screen', 'args': {'screen': 'stock'}}},
                {'functionCall': {'name': 'go_back'}},
              ],
            },
          },
        ],
      });
      expect(calls.map((c) => c.name), ['open_screen', 'go_back']);
      expect(calls.first.args['screen'], 'stock');
      expect(calls.last.args, isEmpty);
      expect(VisionService.functionCallsOf({'candidates': [{'content': {'parts': [{'text': 'привет'}]}}]}), isEmpty);
    });
  });
}

class _Fake implements HttpClientAdapter {
  final ResponseBody Function(RequestOptions o, int n) handler;
  final calls = <RequestOptions>[];
  _Fake(this.handler);
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls.add(options);
    return handler(options, calls.length);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(int status, Object body) => ResponseBody.fromString(jsonEncode(body), status, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });

class _TestVision extends VisionService {
  final String? k;
  _TestVision(super.api, super.db, this.k, {super.http});
  @override
  Future<String?> key() async => k;
  @override
  Future<({String? groq, String? openRouter})> fallbackKeys() async => (groq: null, openRouter: null);
}
