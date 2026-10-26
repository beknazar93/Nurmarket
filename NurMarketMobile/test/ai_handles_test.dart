// 2026-10-26 (голос-управление, В3б): «ручки» денежных экранов на фейках — без сети, без микрофона, без боевой базы.
// Проверяем по ТЗ (раздел 3.5, 10.2 В3б) и поправке владельца («просьба = одобрение», без диалога «вы уверены?»):
//  - ручка запускает ТОТ ЖЕ путь экрана (`_pay`, `_submit`, `_post`, `_close`, `_save`): ключ операции, доступ к долгу, очередь;
//  - вопросы экрана («кто клиент», «сколько в кассе», «какие позиции») возвращаются как clarify, а не диалогом;
//  - ответ сервера не дошёл → unknown, повтор той же просьбы идёт ТЕМ ЖЕ ключом, второй операции нет;
//  - открыватель экранов (`opener`): маршруты, возврат по номеру чека и по id продажи;
//  - тексты «неизвестного исхода» больше не обещают «не повторяю».
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/assistant/action_texts.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart' show OpenResult;
import 'package:nurmarket/core/ai/assistant/ai_handles.dart';
import 'package:nurmarket/core/ai/assistant/screen_opener.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart' show ToolStatus;
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/admin/new_item.dart';
import 'package:nurmarket/features/ops/ops_screens.dart';
import 'package:nurmarket/features/payment/payment_screen.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/shift/close_shift_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, dynamic>? query, Map<String, String>? headers);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => handler('GET', path, null, query, null);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => handler('POST', path, body, null, headers);
  @override
  Future<dynamic> patch(String path, {Object? body}) => handler('PATCH', path, body, null, null);
  @override
  Future<dynamic> delete(String path) => handler('DELETE', path, null, null, null);
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'k1', userName: 'Кассир', role: 'cashier', cashboxId: 'b', cashboxName: 'Касса 1');

const _session = Session(
  email: 'a@x.kg',
  access: 'a',
  refresh: 'r',
  userId: 'u',
  userName: 'Айгерим Т.',
  role: 'owner',
  isOwner: true,
  companyName: 'Нур',
  cashboxId: 'b',
  cashboxName: 'Касса 1',
);

ApiException _lost() => ApiException('Сервер не ответил вовремя.');

/// «Сервер» в памяти: клиенты, продажи, склад, приходы (идемпотентные по ключу), проводки с журналом запросов.
class Server {
  final log = <String>[];
  final checkouts = <Map<String, dynamic>>[];
  final checkoutKeys = <String>[];
  final returnBodies = <Map<String, dynamic>>[];
  final receipts = <String, Map<String, dynamic>>{};
  final receiptPosts = <Map<String, dynamic>>[];
  final patches = <String>[];
  final created = <Map<String, dynamic>>[];
  final closes = <Map<String, dynamic>>[];
  int loseCheckout = 0, loseReturn = 0, loseReceipt = 0, loseCreate = 0;
  int? failReturnStatus;
  double purchasePrice = 80;
  List<Map<String, dynamic>> debtRules = [];

  final clients = [
    {'id': 'c1', 'full_name': 'Асель Нурова', 'phone': '0700123456'},
    {'id': 'c2', 'full_name': 'Асель Кадырова', 'phone': '0555000111'},
    {'id': 'c3', 'full_name': 'Бакыт', 'phone': '0777111222'},
  ];
  final suppliers = [
    {'id': 'sup1', 'full_name': 'Асан Опт', 'type': 'suppliers'},
    {'id': 'sup2', 'full_name': 'Асан Мебель', 'type': 'suppliers'},
  ];
  final sales = [
    {
      'id': 's1',
      'number': '001044',
      'payment_method': 'cash',
      'total': '250.00',
      'items': [
        {'id': 'i1', 'name': 'Кофе', 'quantity': 2, 'unit_price': 100, 'line_total': 200},
        {'id': 'i2', 'name': 'Чай', 'quantity': 1, 'unit_price': 50, 'line_total': 50},
      ],
    },
    {'id': 's2', 'number': '1043', 'payment_method': 'cash', 'total': '90.00', 'items': <Map<String, dynamic>>[]},
  ];

  Future<dynamic> handle(String m, String p, Object? b, Map<String, dynamic>? q, Map<String, String>? h) async {
    final body = b is Map ? b.cast<String, dynamic>() : <String, dynamic>{};
    log.add('$m $p');
    if (m == 'GET' && p == 'api/main/clients/') {
      if (q?['type'] == 'suppliers') return {'results': suppliers};
      final s = '${q?['search'] ?? ''}'.toLowerCase();
      return {
        'results': [
          for (final c in clients)
            if ('${c['full_name']}'.toLowerCase().contains(s)) c,
        ],
      };
    }
    if (m == 'GET' && p == 'api/main/clients/debtors/') return {'results': []};
    if (m == 'GET' && p == 'api/main/app-settings/nurmarket-mobile/') return {'settings': {'DebtAccess': debtRules}};
    if (m == 'POST' && p == 'api/main/pos/checkout/') {
      checkouts.add(body);
      checkoutKeys.add(h?['Idempotency-Key'] ?? '');
      if (loseCheckout > 0) {
        loseCheckout--;
        throw _lost();
      }
      return {'id': 's-new', 'number': '1044', 'total': 430};
    }
    if (m == 'GET' && p == 'api/main/pos/sales/') {
      return {'results': (q?['page'] ?? 1) == 1 ? sales : []};
    }
    final sm = RegExp(r'^api/main/pos/sales/(\w+)/$').firstMatch(p);
    if (m == 'GET' && sm != null) {
      final s = sales.where((x) => x['id'] == sm.group(1)).firstOrNull;
      if (s == null) throw ApiException('Не найдено', status: 404);
      return s;
    }
    if (m == 'POST' && RegExp(r'^api/main/pos/sales/\w+/return/$').hasMatch(p)) {
      returnBodies.add(body);
      if (failReturnStatus != null) throw ApiException('Нельзя вернуть', status: failReturnStatus);
      if (loseReturn > 0) {
        loseReturn--;
        throw _lost();
      }
      return {'id': 'r1'};
    }
    if (m == 'POST' && RegExp(r'^api/construction/shifts/\w+/close/$').hasMatch(p)) {
      closes.add(body);
      return {};
    }
    if (m == 'GET' && p == 'api/main/products/p1/') return {'id': 'p1', 'name': 'Мука', 'purchase_price': purchasePrice, 'quantity': 3};
    if (m == 'PATCH' && p == 'api/main/products/p1/') {
      patches.add(jsonEncode(body));
      return {};
    }
    final rm = RegExp(r'^api/main/suppliers/(\w+)/receipt/$').firstMatch(p);
    if (m == 'POST' && rm != null) {
      receiptPosts.add(body);
      receipts.putIfAbsent(body['idempotency_key'] as String, () => body);
      if (loseReceipt > 0) {
        loseReceipt--;
        throw _lost();
      }
      return {'id': 'rc${receipts.length}'};
    }
    if (m == 'POST' && p == 'api/main/products/create-manual/') {
      created.add(body);
      if (loseCreate > 0) {
        loseCreate--;
        throw _lost();
      }
      return {'id': 'n${created.length}'};
    }
    if (m == 'GET' && p == 'api/main/products/list/') {
      final s = '${q?['search'] ?? ''}'.toLowerCase();
      return {
        'results': [
          for (var i = 0; i < created.length; i++)
            if ('${created[i]['name']}'.toLowerCase().contains(s) || '${created[i]['barcode']}' == s) {'id': 'n${i + 1}', 'name': created[i]['name'], 'barcode': created[i]['barcode']},
        ],
      };
    }
    throw ApiException('Нет связи с сервером.');
  }
}

Cart _cart() => Cart(lines: [CartLine(productId: 'p', name: 'Кофе', price: 430)]);

Future<void> _putProduct(AppDatabase db, String id, String name, {double price = 100, double qty = 3, String? barcode}) => db.upsertProduct(
  ProductsCompanion.insert(
    id: id,
    name: name,
    price: Value(price),
    quantity: Value(qty),
    barcode: Value(barcode),
    searchKey: Value(name.toLowerCase()),
  ),
);

void main() {
  late AppDatabase db;
  late Server srv;
  late AiScreenHandles handles;
  late GoRouter router;
  late ProviderContainer container;

  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_handles_'));
  tearDownAll(() {
    try {
      Audit.testDirectory?.deleteSync(recursive: true);
    } catch (_) {}
  });
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    srv = Server();
    handles = AiScreenHandles();
  });
  tearDown(() => db.close());

  /// Пустой «дом» + экран открывается поверх (push) — как в приложении. [extra] — запасные маршруты.
  Future<void> open(WidgetTester t, String path, Map<String, Widget Function(BuildContext)> routes, {Handler? api, Session who = _session}) async {
    t.view.physicalSize = const Size(720, 1400);
    t.view.devicePixelRatio = 2.0;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() => FlutterError.onError = prev);
    router = GoRouter(
      initialLocation: '/',
      routes: [
        GoRoute(path: '/', builder: (_, _) => const Scaffold(body: Text('HOME'))),
        GoRoute(path: '/home', builder: (_, _) => const Scaffold(body: Text('HOME2'))),
        GoRoute(path: '/paid', builder: (_, _) => const Scaffold(body: Text('PAID'))),
        for (final e in routes.entries) GoRoute(path: e.key, builder: (c, _) => e.value(c)),
      ],
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(FakeApi(api ?? srv.handle)),
          sessionProvider.overrideWith(() => _Fixed(who)),
          shiftProvider.overrideWith(_OpenShift.new),
          aiHandlesProvider.overrideWithValue(handles),
        ],
        child: MaterialApp.router(
          routerConfig: router,
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        ),
      ),
    );
    await t.pumpAndSettle();
    container = ProviderScope.containerOf(t.element(find.byType(Scaffold).first));
    router.push(path);
    await t.pumpAndSettle();
  }

  /// Ручка → итог: `fill`, потом `run`, оба под прокруткой кадров (экран живёт в тестовых часах).
  Future<(HandleOutcome? ask, HandleOutcome? out)> drive(WidgetTester t, String key, Map<String, dynamic> args, {bool run = true}) async {
    final h = handles.get(key);
    expect(h, isNotNull, reason: 'экран зарегистрировал ручку «$key»');
    HandleOutcome? ask, out;
    var done = false;
    () async {
      ask = await h!.fill(args);
      if (ask == null && run) out = await h.run();
      done = true;
    }();
    for (var i = 0; i < 60 && !done; i++) {
      await t.pump(const Duration(milliseconds: 100));
    }
    expect(done, isTrue, reason: 'ручка «$key» завершилась');
    return (ask, out);
  }

  // ------------------------------------------------------------------------------------------------------------------
  group('реестр, доклад экрана, подбор по названию', () {
    test('register / unregister: чужую ручку не снимает; waitFor ждёт регистрацию', () async {
      final r = AiScreenHandles();
      final a = AiHandle(fill: (_) async => null, run: () async => HandleOutcome.ok());
      final b = AiHandle(fill: (_) async => null, run: () async => HandleOutcome.ok());
      final wait = r.waitFor('pay', timeout: const Duration(seconds: 1));
      r.register('pay', a);
      expect(await wait, same(a));
      r.register('pay', b);
      r.unregister('pay', a);
      expect(r.get('pay'), same(b), reason: 'новый экран уже зарегистрировал свою ручку');
      r.unregister('pay', b);
      expect(r.get('pay'), isNull);
      expect(await r.waitFor('pay', timeout: const Duration(milliseconds: 20)), isNull);
    });

    test('OutcomeSink: первый доклад побеждает; экран молчит → «не завершил»; падение после денег → афтер-итог', () async {
      final s = OutcomeSink();
      final o = await s.run(() async {
        s.report(HandleOutcome.ok({'x': 1}));
        s.report(HandleOutcome.error('второй доклад не нужен'));
      });
      expect(o.status, ToolStatus.ok);
      final silent = await s.run(() async {});
      expect(silent.askKey, 'h.silent');
      final crashed = await s.run(() async => throw StateError('boom'));
      expect(crashed.askKey, 'h.failed');
      final afterMoney = await s.run(() async => throw StateError('boom'), afterEnd: () => HandleOutcome.ok({'amount': 5}));
      expect(afterMoney.status, ToolStatus.ok, reason: 'деньги уже возвращены — не «ошибка»');
      s.report(HandleOutcome.ok()); // без запуска — ничего не делает
    });

    test('aiMatchByName: слова с начала; при нескольких точное имя побеждает', () {
      final names = ['Асель Нурова', 'Асель Кадырова', 'Асель', 'Бакыт'];
      expect(aiMatchByName(names, 'Асель', (x) => x), ['Асель'], reason: 'точное совпадение выбирает одного');
      expect(aiMatchByName(names, 'сель кад', (x) => x), isEmpty, reason: 'слово «сель» не начало «Асель»');
      expect(aiMatchByName(names, 'Асель кад', (x) => x), ['Асель Кадырова']);
      expect(aiMatchByName(names.take(2), 'Асель', (x) => x), hasLength(2));
    });

    test('цена прихода голосом: не ниже текущей и не ниже закупки + 20 %', () {
      expect(aiReceivePrice(90, 100), 108);
      expect(aiReceivePrice(80, 100), 100);
      expect(aiReceivePrice(100, 0), 120);
      expect(aiReceivePrice(33, 40), 40);
    });
  });

  group('тексты ручек: 5 языков, неизвестный исход не обещает «не повторяю»', () {
    test('ключи h.*, unknownMoney, receiveSummary есть на пяти языках с теми же подстановками', () {
      final t = actionTextsForTest;
      final keys = t.keys.where((k) => k.startsWith('h.') || k == 'unknownMoney' || k == 'receiveSummary').toList();
      expect(keys.length, greaterThan(15));
      final holes = RegExp(r'\{(\w+)\}');
      for (final k in keys) {
        expect(t[k]!.length, 5, reason: k);
        final base = holes.allMatches(t[k]![0]).map((m) => m.group(1)).toSet();
        for (final s in t[k]!) {
          expect(s.trim(), isNotEmpty, reason: k);
          expect(holes.allMatches(s).map((m) => m.group(1)).toSet(), base, reason: k);
        }
      }
      for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
        final s = am('unknownMoney', lang);
        expect(s, isNot(contains('Не повторяю')), reason: lang);
        expect(s.toLowerCase(), isNot(contains('i do not repeat')), reason: lang);
      }
      expect(am('unknownMoney', 'ru'), contains('тем же ключом'));
      expect(am('unknownMoney', 'en'), contains('same operation key'));
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('opener: ключ ручки → экран', () {
    ProviderContainer cont() {
      final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(FakeApi(srv.handle))]);
      addTearDown(c.dispose);
      return c;
    }

    test('pay / close_shift / receive / new_item → push маршрута; уже открыт — не открывает второй; чужой ключ — unsupported', () async {
      final host = _Host();
      final open = buildScreenOpener(host: host, read: cont().read);
      expect(await open('pay', const {}), OpenResult.ok);
      expect(host.pushed, ['/pay']);
      host.location = '/pay';
      expect(await open('pay', const {}), OpenResult.ok);
      expect(host.pushed, ['/pay'], reason: 'экран оплаты уже на виду');
      expect(await open('close_shift', const {}), OpenResult.ok);
      expect(await open('receive', const {}), OpenResult.ok);
      expect(await open('new_item', const {}), OpenResult.ok);
      expect(host.pushed, ['/pay', '/close-shift', '/admin/receive', '/admin/new-product']);
      expect(await open('stock_count', const {}), OpenResult.unsupported);
      expect(host.pushed.length, 4);
    });

    test('return: чек по номеру (нули не мешают) и по id продажи; нет чека → notFound; нет связи → unsupported', () async {
      final host = _Host();
      final c = cont();
      final open = buildScreenOpener(host: host, read: c.read);
      expect(await open('return', {'check_number': '№1044'}), OpenResult.ok);
      expect(host.pages.single, isA<ReturnScreen>());
      var scr = host.pages.single as ReturnScreen;
      expect((scr.saleId, scr.number), ('s1', '001044'));
      expect(await open('return', {'sale_id': 's2', 'check_number': '1043'}), OpenResult.ok);
      scr = host.pages.last as ReturnScreen;
      expect(scr.saleId, 's2');
      expect(await open('return', {'check_number': '77777'}), OpenResult.notFound);
      expect(await open('return', {}), OpenResult.notFound);
      expect(await open('return', {'sale_id': 'zzz'}), OpenResult.notFound, reason: '404 сервера');
      expect(host.pages.length, 2);
      final offline = buildScreenOpener(host: host, read: ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(FakeApi((m, p, b, q, h) async => throw ApiException('Нет связи.')))]).read);
      expect(await offline('return', {'check_number': '1044'}), OpenResult.unsupported);
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('оплата: ручка нажимает ту же _pay', () {
    Future<void> payScreen(WidgetTester t) async {
      await open(t, '/pay', {'/pay': (_) => const PaymentScreen()});
      container.read(cartProvider.notifier).restore(_cart());
      await t.pumpAndSettle();
    }

    testWidgets('наличные «ровно»: один checkout с ключом, чек очищен, переход на «Оплачено», без диалогов', (t) async {
      await payScreen(t);
      final (ask, out) = await drive(t, 'pay', {'method': 'cash'});
      expect(ask, isNull);
      expect(out!.status, ToolStatus.ok);
      expect(out.data, containsPair('total', 430.0));
      expect(out.data, containsPair('number', '1044'));
      expect(out.data, containsPair('sale_id', 's-new'));
      expect(out.data, containsPair('queued', false));
      expect(out.data['change'], 0.0);
      expect(srv.checkouts.length, 1, reason: 'ровно одна продажа');
      expect(srv.checkoutKeys.single, isNotEmpty, reason: 'Idempotency-Key на месте');
      expect(container.read(cartProvider).isEmpty, isTrue);
      await t.pumpAndSettle();
      expect(find.text('PAID'), findsOneWidget);
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('«дали 500» → сдача 70; «дали 100» → вопрос «не хватает 330», продажи нет', (t) async {
      await payScreen(t);
      final (ask, _) = await drive(t, 'pay', {'method': 'cash', 'cash_given': 100});
      expect(ask!.status, ToolStatus.clarify);
      expect(ask.askKey, 'h.payShort');
      expect(ask.askParams, {'given': 100.0, 'total': 430.0, 'short': 330.0});
      expect(srv.checkouts, isEmpty);
      final (_, out) = await drive(t, 'pay', {'method': 'cash', 'cash_given': 500});
      expect(out!.data['change'], 70.0);
      expect((srv.checkouts.single['payment'] as Map)['method'], 'cash');
      expect((srv.checkouts.single['payment'] as Map)['received'], '500.00');
    });

    testWidgets('в долг без клиента → clarify (нужен клиент); имя двух Асель → список; имя целиком → клиент выбран, долг записан на него', (t) async {
      await payScreen(t);
      var r = await drive(t, 'pay', {'method': 'debt'});
      expect(r.$1!.askKey, 'needDebtClient');
      r = await drive(t, 'pay', {'method': 'debt', 'client': 'Асель'});
      expect(r.$1!.status, ToolStatus.ambiguous);
      expect(r.$1!.askKey, 'h.clientMany');
      expect(r.$1!.askParams['list'], allOf(contains('Асель Нурова'), contains('Асель Кадырова')));
      r = await drive(t, 'pay', {'method': 'debt', 'client': 'Нет такого'});
      expect(r.$1!.status, ToolStatus.notFound);
      expect(srv.checkouts, isEmpty);
      r = await drive(t, 'pay', {'method': 'debt', 'client': 'Асель Нурова', 'cash_given': 100});
      expect(r.$1, isNull);
      expect(r.$2!.status, ToolStatus.ok);
      expect((srv.checkouts.single['payment'] as Map)['method'], 'debt');
      expect((srv.checkouts.single['payment'] as Map)['cash_amount'], '100.00');
      expect(srv.checkouts.single['client'], 'c1');
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('«Доступ к долгу»: у кассира долг запрещён → denied с текстом экрана, продажи нет; наличные того же кассира проходят', (t) async {
      srv.debtRules = [
        {'user': '*', 'mode': 'deny'},
      ];
      await open(t, '/pay', {'/pay': (_) => const PaymentScreen()}, who: _cashier);
      container.read(cartProvider.notifier).restore(_cart());
      await t.pumpAndSettle();
      final r = await drive(t, 'pay', {'method': 'debt', 'client': 'Бакыт'});
      expect(r.$2!.status, ToolStatus.denied);
      expect(r.$2!.ask, isNotEmpty);
      expect(srv.checkouts, isEmpty, reason: 'проверка доступа — в той же _pay, мимо неё не пройти');
      final cash = await drive(t, 'pay', {'method': 'cash'});
      expect(cash.$2!.status, ToolStatus.ok);
      expect(srv.checkouts.length, 1);
    });

    testWidgets('пустой чек → вопрос, а не пустая продажа', (t) async {
      await payScreen(t);
      container.read(cartProvider.notifier).clear();
      await t.pump();
      final empty = await drive(t, 'pay', {'method': 'cash'});
      expect(empty.$1!.askKey, 'emptyForPay');
    });

    testWidgets('долг: ответ не дошёл → unknown; та же просьба ещё раз → ТОТ ЖЕ ключ, сервер один раз проводит', (t) async {
      await payScreen(t);
      srv.loseCheckout = 1;
      final a = await drive(t, 'pay', {'method': 'debt', 'client': 'Бакыт'});
      expect(a.$2!.unknown, isTrue);
      expect(a.$2!.status, ToolStatus.error);
      expect(container.read(cartProvider).isEmpty, isFalse, reason: 'чек не очищен — продажа не подтверждена');
      final b = await drive(t, 'pay', {'method': 'debt', 'client': 'Бакыт'});
      expect(b.$2!.status, ToolStatus.ok);
      expect(srv.checkouts.length, 2);
      expect(srv.checkoutKeys[0], srv.checkoutKeys[1], reason: 'повтор идёт тем же ключом операции');
    });

    testWidgets('наличные без связи → в очередь телефона (queued), чек уйдёт сам', (t) async {
      await payScreen(t);
      srv.loseCheckout = 1;
      final r = await drive(t, 'pay', {'method': 'cash'});
      expect(r.$2!.status, ToolStatus.ok);
      expect(r.$2!.data, containsPair('queued', true));
      expect(r.$2!.data['sale_id'], '');
      expect((await db.pending()).length, 1);
    });

    testWidgets('экран закрыли — ручка снята', (t) async {
      await payScreen(t);
      expect(handles.get('pay'), isNotNull);
      router.pop();
      await t.pumpAndSettle();
      expect(handles.get('pay'), isNull);
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('возврат: ручка нажимает ту же _submit без диалога', () {
    Future<void> returnScreen(WidgetTester t, {int sale = 0}) async {
      final s = (srv.sales[sale] as Map).cast<String, dynamic>();
      await open(t, '/return', {'/return': (_) => ReturnScreen(saleId: '${s['id']}', number: '${s['number']}', sale: s)});
    }

    testWidgets('весь чек: один запрос возврата без позиций, диалога нет, сумма в итоге', (t) async {
      await returnScreen(t);
      final (ask, out) = await drive(t, 'return', {'check_number': '1044', 'whole': true});
      expect(ask, isNull);
      expect(out!.status, ToolStatus.ok);
      expect(out.data['amount'], greaterThan(0));
      expect(srv.returnBodies.length, 1);
      expect(srv.returnBodies.single.containsKey('items'), isFalse);
      expect(srv.returnBodies.single['idempotency_key'], isNotEmpty);
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('часть чека: «Кофе 1 шт» → одна строка с количеством; больше остатка → вопрос; нет такой позиции → не нашла', (t) async {
      await returnScreen(t);
      var r = await drive(t, 'return', {
        'check_number': '1044',
        'lines': [
          {'query': 'Кофе', 'qty': 5},
        ],
      });
      expect(r.$1!.askKey, 'h.retQty');
      expect(r.$1!.askParams, {'name': 'Кофе', 'maxQty': 2.0});
      r = await drive(t, 'return', {
        'check_number': '1044',
        'lines': [
          {'query': 'Пирог', 'qty': 1},
        ],
      });
      expect(r.$1!.status, ToolStatus.notFound);
      expect(r.$1!.askKey, 'h.lineNone');
      expect(srv.returnBodies, isEmpty);
      r = await drive(t, 'return', {
        'check_number': '1044',
        'lines': [
          {'query': 'Кофе', 'qty': 1},
        ],
        'reason': 'брак',
      });
      expect(r.$2!.status, ToolStatus.ok);
      final items = (srv.returnBodies.single['items'] as List).cast<Map>();
      expect(items.single['sale_item_id'], 'i1');
      expect(items.single['quantity'], '1');
    });

    testWidgets('ответ не дошёл → unknown; повтор той же просьбы идёт тем же ключом', (t) async {
      await returnScreen(t);
      srv.loseReturn = 1;
      final a = await drive(t, 'return', {'check_number': '1044', 'whole': true});
      expect(a.$2!.unknown, isTrue);
      final b = await drive(t, 'return', {'check_number': '1044', 'whole': true});
      expect(b.$2!.status, ToolStatus.ok);
      expect(srv.returnBodies.length, 2);
      expect(srv.returnBodies[0]['idempotency_key'], srv.returnBodies[1]['idempotency_key']);
    });

    testWidgets('сервер отказал по существу → ошибка с его текстом, не «неизвестно»', (t) async {
      await returnScreen(t);
      srv.failReturnStatus = 400;
      final r = await drive(t, 'return', {'check_number': '1044', 'whole': true});
      expect(r.$2!.unknown, isFalse);
      expect(r.$2!.status, ToolStatus.error);
      expect(r.$2!.ask, 'Нельзя вернуть');
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('закрытие смены: ручка нажимает ту же _close', () {
    testWidgets('без суммы → clarify (сумма обязательна), смена не закрыта; с суммой → один запрос закрытия, диалога нет', (t) async {
      await open(t, '/close-shift', {'/close-shift': (_) => const CloseShiftScreen()});
      final no = await drive(t, 'close_shift', {});
      expect(no.$1!.askKey, 'needCounted');
      expect(srv.closes, isEmpty);
      final (ask, out) = await drive(t, 'close_shift', {'counted_cash': 1500});
      expect(ask, isNull);
      expect(out!.status, ToolStatus.ok);
      expect(out.data['counted'], 1500.0);
      expect(srv.closes.single['closing_cash'], '1500.00');
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('в очереди остался неотправленный чек → не закрывает, говорит об очереди (чеки не потеряются)', (t) async {
      await db.enqueue(
        OutboxCompanion.insert(
          id: 'k1',
          kind: 'sale',
          body: jsonEncode(OutboxMeta.attach({'x': 1}, owner: OutboxMeta.ownerOf(_session), cashboxId: 'b')),
          createdAt: DateTime.now(),
        ),
      );
      await open(t, '/close-shift', {'/close-shift': (_) => const CloseShiftScreen()});
      final (_, out) = await drive(t, 'close_shift', {'counted_cash': 100});
      expect(out!.status, ToolStatus.error);
      expect(out.ask, contains('1'), reason: 'текст экрана с числом неотправленных');
      expect(srv.closes, isEmpty);
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('приёмка с поставщиком: ручка нажимает ту же _post', () {
    Future<void> receiveScreen(WidgetTester t) async {
      await _putProduct(db, 'p1', 'Мука', price: 100);
      await open(t, '/admin/receive', {'/admin/receive': (_) => const AdminReceiveScreen()});
    }

    Map<String, dynamic> args({String supplier = 'Асан Опт', num? cost = 90, String pay = 'debt'}) => {
      'supplier': supplier,
      'pay': pay,
      'lines': [
        {'query': 'Мука', 'qty': 5, 'cost': ?cost},
      ],
    };

    testWidgets('приход в долг: один документ с ключом, строка qty/закупка, цена по правилу ПК (≥ закупка + 20 %), диалога нет', (t) async {
      await receiveScreen(t);
      final (ask, out) = await drive(t, 'receive', args());
      expect(ask, isNull);
      expect(out!.status, ToolStatus.ok);
      expect(out.data, containsPair('total', 450.0));
      expect(out.data, containsPair('lines', 1));
      expect(srv.receiptPosts.length, 1);
      final body = srv.receiptPosts.single;
      expect(body['payment_type'], 'debt');
      expect((body['items'] as List).single, {'product': 'p1', 'qty': 5.0, 'purchase_price': 90.0});
      expect(body['idempotency_key'], isNotEmpty);
      expect(srv.patches.single, contains('"price":"108.00"'), reason: '90 × 1,2 = 108 выше текущих 100');
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('закупка не названа → берётся прежняя (80); цена не падает ниже текущей (100) — PATCH цены не нужен; оплата сразу → cash', (t) async {
      await receiveScreen(t);
      final (_, out) = await drive(t, 'receive', args(cost: null, pay: 'cash'));
      expect(out!.status, ToolStatus.ok);
      expect(srv.receiptPosts.single['payment_type'], 'cash');
      expect(((srv.receiptPosts.single['items'] as List).single as Map)['purchase_price'], 80.0);
      expect(srv.patches, isEmpty);
    });

    testWidgets('«Асан» — два поставщика → вопрос; неизвестный → не нашла; товара нет → не нашла; закупки нет нигде → вопрос про цену', (t) async {
      await receiveScreen(t);
      var r = await drive(t, 'receive', args(supplier: 'Асан'));
      expect(r.$1!.status, ToolStatus.ambiguous);
      expect(r.$1!.askKey, 'h.supplierMany');
      r = await drive(t, 'receive', args(supplier: 'Омск'));
      expect(r.$1!.askKey, 'h.supplierNone');
      r = await drive(t, 'receive', {
        'supplier': 'Асан Опт',
        'lines': [
          {'query': 'Гвозди', 'qty': 1, 'cost': 5},
        ],
      });
      expect(r.$1!.askKey, 'productNone');
      srv.purchasePrice = 0;
      r = await drive(t, 'receive', args(cost: null));
      expect(r.$1!.askKey, 'h.costNeeded');
      expect(srv.receiptPosts, isEmpty);
    });

    testWidgets('ответ не дошёл → unknown; та же просьба снова → тот же состав и тот же ключ, на сервере один приход', (t) async {
      await receiveScreen(t);
      srv.loseReceipt = 1;
      final a = await drive(t, 'receive', args());
      expect(a.$2!.unknown, isTrue);
      final b = await drive(t, 'receive', args());
      expect(b.$2!.status, ToolStatus.ok);
      expect(srv.receiptPosts.length, 2);
      expect(srv.receiptPosts[0]['idempotency_key'], srv.receiptPosts[1]['idempotency_key']);
      expect(srv.receipts.length, 1, reason: 'сервер не провёл приход второй раз');
    });
  });

  // ------------------------------------------------------------------------------------------------------------------
  group('новый товар: ручка нажимает ту же _save', () {
    Future<void> itemScreen(WidgetTester t) => open(t, '/admin/new-product', {'/admin/new-product': (_) => const NewItemScreen()});

    testWidgets('название, цена, закупка → один товар создан, итог с id', (t) async {
      await itemScreen(t);
      final (ask, out) = await drive(t, 'new_item', {'name': 'Вода 0,5', 'price': 25, 'purchase': 18, 'unit': 'шт', 'category': 'Напитки'});
      expect(ask, isNull);
      expect(out!.status, ToolStatus.ok);
      expect(out.data, containsPair('id', 'n1'));
      expect(srv.created.length, 1);
      expect(srv.created.single['name'], 'Вода 0,5');
      expect(srv.created.single['price'], '25.00');
      expect(srv.created.single['purchase_price'], '18.00');
      expect(srv.created.single['category_name'], 'Напитки');
    });

    testWidgets('нет имени / цены / непонятная единица → вопрос, ничего не создано', (t) async {
      await itemScreen(t);
      expect((await drive(t, 'new_item', {'price': 5})).$1!.askKey, 'needProduct');
      expect((await drive(t, 'new_item', {'name': 'Х'})).$1!.askKey, 'needPriceValue');
      expect((await drive(t, 'new_item', {'name': 'Х', 'price': 5, 'unit': 'пуд'})).$1!.askKey, 'h.unitBad');
      expect(srv.created, isEmpty);
    });

    testWidgets('штрихкод уже есть в базе → ошибка «уже в базе», второй товар не создан', (t) async {
      await _putProduct(db, 'p9', 'Сок', barcode: '4600000000011');
      await itemScreen(t);
      final (_, out) = await drive(t, 'new_item', {'name': 'Сок новый', 'price': 90, 'barcode': '4600000000011'});
      expect(out!.status, ToolStatus.error);
      expect(srv.created, isEmpty);
    });

    testWidgets('создание без ответа → unknown; повтор находит созданный товар, а не создаёт второй', (t) async {
      await itemScreen(t);
      srv.loseCreate = 1;
      final a = await drive(t, 'new_item', {'name': 'Квас', 'price': 60});
      expect(a.$2!.unknown, isTrue);
      final b = await drive(t, 'new_item', {'name': 'Квас', 'price': 60});
      expect(b.$2!.status, ToolStatus.ok);
      expect(srv.created.length, 1, reason: 'второй раз товар не создан — найден прошлый');
    });
  });
}

/// Фейковая оболочка: запоминает переходы.
class _Host implements ScreenOpenerHost {
  @override
  String location = '/home';
  final pushed = <String>[];
  final pages = <Widget>[];
  @override
  Future<void> push(String l) async => pushed.add(l);
  @override
  Future<void> pushPage(Widget page) async => pages.add(page);
}
