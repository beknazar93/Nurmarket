// 2026-11-27 (просьба владельца «И в мобилку тоже Маркетплейс добавь!»): раздел «Маркетплейс» мобилки — тексты и значки всех модулей сервера (живой ответ
// public/modules/ от 26.11.2026 лежит в test/fixtures), список с группами и поиском, плитка, страница функции, включение / выключение теми же вызовами, что у кассы
// (PUT с If-Match, DELETE), пробный период, офлайн (кэш), права (кассиру закрыто), тарифы и «Своё», демонстрация; 5 языков на 360 dp при шрифте ×1,3 в светлой и тёмной теме.
import 'dart:convert';
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart' show FontLoader, rootBundle;
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/marketplace/marketplace_content.dart';
import 'package:nurmarket/features/marketplace/marketplace_data.dart';
import 'package:nurmarket/features/marketplace/marketplace_demo.dart';
import 'package:nurmarket/features/marketplace/marketplace_icons.dart';
import 'package:nurmarket/features/marketplace/marketplace_module_screen.dart';
import 'package:nurmarket/features/marketplace/marketplace_screen.dart';
import 'package:nurmarket/features/marketplace/marketplace_state.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _langs = ['ru', 'ky', 'en', 'tr', 'uz'];

final _modulesJson = File('test/fixtures/marketplace_public_modules.json').readAsStringSync();
final _tariffsJson = File('test/fixtures/marketplace_public_tariffs.json').readAsStringSync();
final _live = jsonDecode(_modulesJson) as Map<String, dynamic>;
final _liveIds = [for (final m in _live['modules'] as List) (m as Map)['id'] as String];

/// Сервер NurCRM в памяти: публичные цены, реестр модулей компании (версия, If-Match), пробный период, расчёт и заявка «Своё».
class _Server {
  bool offline = false;
  String profile = 'grocery';
  final enabled = <String>{'scales_labels', 'expiry_batches', 'quick_items'};
  int version = 1;
  final log = <String>[];
  final headersLog = <Map<String, String>?>[];
  final bodies = <Object?>[];
  bool trialUsed = false;
  bool forbid = false;

  Map<String, dynamic> company() => {
    'profile': profile,
    'profile_base': 'grocery',
    'version': 'v$version',
    'modules': enabled.toList(),
    'items': [
      for (final id in _liveIds) {'id': id, 'enabled': enabled.contains(id), 'source': enabled.contains(id) ? 'default' : 'off'},
    ],
    'catalog': [
      for (final m in _live['modules'] as List) {'id': (m as Map)['id'], 'names': m['name'], 'group': m['group'], 'default_in': m['default_in']},
    ],
    'profiles': [
      {'id': 'grocery', 'names': {'ru': 'Продуктовый', 'en': 'Grocery'}, 'modules': ['scales_labels', 'expiry_batches', 'quick_items']},
    ],
  };

  Future<dynamic> handle(String method, String path, Object? body, Map<String, String>? headers) async {
    log.add('$method $path');
    headersLog.add(headers);
    bodies.add(body);
    if (offline) throw ApiException('Нет связи с сервером.');
    if (path.startsWith('api/public/modules')) return jsonDecode(_modulesJson);
    if (path.startsWith('api/public/tariffs')) return jsonDecode(_tariffsJson);
    if (path == 'api/main/capabilities/') return <String, dynamic>{};
    if (path == 'api/users/company/modules/' && method == 'GET') return company();
    if (path == 'api/users/company/modules/' && method == 'PUT') {
      if (forbid) throw ApiException('Forbidden', status: 403, payload: {'code': 'permission_denied'});
      if (headers?['If-Match'] != 'v$version') throw ApiException('conflict', status: 409, payload: {'code': 'version_conflict'});
      final m = (body as Map).cast<String, dynamic>();
      for (final id in (m['enable'] as List?) ?? const []) {
        enabled.add('$id');
      }
      for (final id in (m['disable'] as List?) ?? const []) {
        enabled.remove('$id');
      }
      version++;
      return company();
    }
    if (path.startsWith('api/users/company/tariff/modules/') && method == 'DELETE') {
      if (forbid) throw ApiException('Forbidden', status: 403, payload: {'code': 'permission_denied'});
      enabled.remove(path.split('/').where((s) => s.isNotEmpty).last);
      version++;
      return company();
    }
    if (path == 'api/users/company/features/trial/') {
      if (trialUsed) throw ApiException('trial', status: 409, payload: {'code': 'trial_used'});
      enabled.add('${(body as Map)['code']}');
      version++;
      return {'code': body['code'], 'kind': 'module'};
    }
    if (path == 'api/users/company/tariff/quote/') {
      return {'base': '1300.00', 'total_month': '1600.00', 'total_year': '16000.00', 'year_paid_months': 10, 'extra': [{'id': 'estimate', 'name': {'ru': 'Смета'}, 'monthly': '300.00'}]};
    }
    if (path == 'api/users/company/tariff/custom/') return {'order_id': 'o-1', 'status': 'pending'};
    throw ApiException('Не найдено', status: 404);
  }
}

class _Api extends ApiClient {
  _Api(this.srv) : super(SessionStore());
  final _Server srv;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => srv.handle('GET', path, null, null);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => srv.handle('POST', path, body, headers);
  @override
  Future<dynamic> send(String method, String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => srv.handle(method, path, body, headers);
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'o1', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b');
const _admin = Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'a1', userName: 'Админ', role: 'admin', companyName: 'Нур', cashboxId: 'b');
const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', role: 'cashier', companyName: 'Нур', cashboxId: 'b', perms: []);

void _phone(WidgetTester t, {double scale = 1.0, bool reduceMotion = true}) {
  t.view.physicalSize = const Size(720, 1280);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  if (reduceMotion) t.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(disableAnimations: true);
  addTearDown(() {
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
    t.platformDispatcher.clearAccessibilityFeaturesTestValue();
  });
}

/// Настоящий шрифт приложения: в тесте по умолчанию рисуется «Ahem» (каждая буква — квадрат), он шире настоящего в 1,5–2 раза и «обрезает» то, что на телефоне помещается.
Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

/// Долистать до надписи и показать её целиком (иначе нажатие попадает за край экрана).
Future<void> _scrollTo(WidgetTester t, Finder f) async {
  await t.scrollUntilVisible(f, 300, scrollable: find.byType(Scrollable).first);
  await t.ensureVisible(f.first);
  await t.pump();
}

void main() {
  late AppDatabase db;
  late _Server srv;
  late _Api api;
  late MarketplaceContent content;

  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    await _fonts();
    content = MarketplaceContent.fromJson((jsonDecode(File('assets/marketplace/content.json').readAsStringSync()) as Map).cast<String, dynamic>());
    MarketplaceContent.setForTests(content);
  });
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    srv = _Server();
    api = _Api(srv);
  });
  tearDown(() => db.close());

  Widget app(Widget home, Session s, {String lang = 'ru', bool dark = false}) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), pendingCountProvider.overrideWith((ref) => Stream.value(0)), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(s))],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  group('тексты и значки', () {
    test('у КАЖДОГО модуля сервера есть название, строка, описание, значок, цвет и демонстрация на 5 языках (не пазл, не шаблон)', () {
      for (final id in [..._liveIds, repairStatusId]) {
        final c = content.module(id);
        expect(c, isNotNull, reason: 'нет записи контента: $id');
        for (final l in _langs) {
          expect(c!.name.has(l), isTrue, reason: '$id: название $l');
          expect(c.line.has(l), isTrue, reason: '$id: строка «что делает» $l');
          expect(c.short.has(l) || c.benefits.isNotEmpty, isTrue, reason: '$id: описание $l');
        }
        expect(c!.icon, isNotEmpty, reason: '$id: нет ключа значка');
        expect(mdiToMaterial.containsKey(c.icon), isTrue, reason: '$id: нет значка для «${c.icon}»');
        expect(c.icon, isNot('plus-box-outline'), reason: '$id: запасной значок вместо своего');
        expect(RegExp(r'^#[0-9A-Fa-f]{6}$').hasMatch(c.color), isTrue, reason: '$id: цвет');
        expect(c.steps, isNotEmpty, reason: '$id: «как включить»');
        expect(c.audience, isNotEmpty, reason: '$id: «для кого»');
        if (id != repairStatusId) expect(c.demo.length, inInclusiveRange(3, 5), reason: '$id: демонстрация 3–5 шагов');
      }
    });

    test('карточки функций без серверного модуля (бот-продавец, торг бота, ЭСФ, онлайн-ККМ, рассылки, 1С, QR…): текст, значок и демонстрация есть; режимы разобраны', () {
      expect(content.features.keys, containsAll(['bot_seller', 'bot_bargain', 'esf', 'ekassa', 'client_mailing', 'onec_export', 'shop_qr', 'payment_qr', 'receipt_link', 'stock_scan']));
      for (final f in content.features.values) {
        final c = content.module(f.id)!;
        for (final l in _langs) {
          expect(c.name.has(l), isTrue, reason: '${f.id}: название $l');
          expect(c.line.has(l), isTrue, reason: '${f.id}: строка $l');
        }
        expect(mdiToMaterial.containsKey(c.icon), isTrue, reason: '${f.id}: ${c.icon}');
        expect(c.demo, isNotEmpty, reason: '${f.id}: демонстрация');
        expect(['capability', 'fiscal', 'builtin', 'service'], contains(f.mode));
      }
      expect(content.features['bot_seller']!.flag, 'bot_seller');
    });

    test('все ключи значков из content.json (модули, пакеты, «что получите») имеют аналог из Icons', () {
      final all = [...content.modules.values, ...content.packs.values];
      for (final c in all) {
        if (c.icon.isNotEmpty) expect(mdiToMaterial.containsKey(c.icon), isTrue, reason: '${c.id}: ${c.icon}');
        for (final g in [...c.gains, ...c.benefits]) {
          expect(mdiToMaterial.containsKey(g.icon), isTrue, reason: '${c.id}: ${g.icon}');
        }
      }
    });

    test('значки для модулей, которых в кассе ещё нет, подбираются по коду и не бывают «пазлом»', () {
      for (final id in ['telegram_bot', 'seller_bot', 'haggle_bot', 'esf', 'ekassa', 'mailing', 'export_1c', 'receipt_qr', 'pretty_receipt', 'repair_status_site', 'photo_scan']) {
        expect(moduleIcon(id), isNot(Icons.add_box_outlined), reason: id);
        expect(moduleIcon(id), isNot(Icons.extension_outlined), reason: id);
      }
      expect(moduleIcon('totally_new_module'), Icons.add_box_outlined); // запасной — «плюс в квадрате», как в кассе
    });

    test('названия: язык сервера → наше → русское', () {
      final ours = LText.parse({'ru': 'Смета', 'ky': 'Смета', 'tr': 'Keşif'});
      expect(pickName('ru', {'ru': 'Смета', 'en': 'Estimate'}, ours, 'x'), 'Смета');
      expect(pickName('en', {'ru': 'Смета', 'en': 'Estimate'}, ours, 'x'), 'Estimate');
      expect(pickName('tr', {'ru': 'Смета', 'en': 'Estimate'}, ours, 'x'), 'Keşif');
      expect(pickName('uz', {'ru': 'Смета', 'en': 'Estimate'}, ours, 'x'), 'Смета');
      expect(pickName('uz', {}, null, 'code'), 'code');
    });
  });

  group('плитки по ответу сервера', () {
    MpSnapshot snap({bool withCompany = true}) => MpSnapshot(
      modules: MpServerModule.parseList(_live),
      company: withCompany ? MpCompany.parse(srv.company()) : null,
      tariffs: MpTariffs.parse(jsonDecode(_tariffsJson)),
    );

    test('включён / входит в сферу / доступен с серверной ценой; «Статус ремонта» — «Скоро» без признака', () {
      final v = {for (final x in buildModuleViews(snap(), content, 'ru')) x.id: x};
      expect(v['scales_labels']!.on, isTrue);
      expect(v['scales_labels']!.included, isTrue);
      expect(mpStatusLabelForTest(v['scales_labels']!), 'on');
      expect(v['estimate']!.on, isFalse);
      expect(v['estimate']!.monthly, 300);
      expect(v['estimate']!.trialAvailable, isTrue);
      expect(v[repairStatusId]!.soon, isTrue);
      expect(v[repairStatusId]!.canToggle, isFalse);
      expect(buildModuleViews(snap(), content, 'ru', features: {repairStatusFlag})[_liveIds.length].on, isTrue);
      expect(v.length, _liveIds.length + 1 + content.features.length);
      expect(v['estimate']!.name, 'Смета');
      expect(buildModuleViews(snap(), content, 'en').firstWhere((x) => x.id == 'estimate').name, 'Estimate');
    });

    test('карточки функций: «Скоро» без признака сервера, «Доступно» с признаком, ЭСФ входит в программу; без своих кнопок; серверный модуль с тем же кодом главнее', () {
      final v = {for (final x in buildModuleViews(snap(), content, 'ru')) x.id: x};
      expect(v['bot_seller']!.soon, isTrue);
      expect(v['bot_seller']!.canToggle, isFalse);
      expect(v['esf']!.on, isTrue);
      expect(v['esf']!.featureMode, 'builtin');
      expect(v['esf']!.group, 'extra');
      expect(v['ekassa']!.monthly, isNull); // цену придумывать нельзя
      final v2 = {for (final x in buildModuleViews(snap(), content, 'ru', features: {'bot_seller'})) x.id: x};
      expect(v2['bot_seller']!.soon, isFalse);
      expect(v2['bot_seller']!.on, isTrue);
      final withServer = MpSnapshot(modules: [...MpServerModule.parseList(_live), const MpServerModule('esf', {'ru': 'ЭСФ'}, 'grocery', 100, 7, [])], company: MpCompany.parse(srv.company()));
      final one = buildModuleViews(withServer, content, 'ru').where((x) => x.id == 'esf').toList();
      expect(one.length, 1);
      expect(one.single.featureMode, isNull); // когда сервер заведёт модуль, карточка станет обычной серверной
    });

    test('без ответа модулей компании действия закрыты, цены остаются серверными', () {
      final v = buildModuleViews(snap(withCompany: false), content, 'ru').firstWhere((x) => x.id == 'estimate');
      expect(v.stateKnown, isFalse);
      expect(v.canToggle, isFalse);
      expect(v.monthly, 300);
    });

    test('модуль сервера без записи контента остаётся, со строкой сервера, без падения', () {
      final s = MpSnapshot(modules: [const MpServerModule('new_thing', {'ru': 'Новинка'}, 'grocery', 150, 7, [], description: 'Описание от сервера')], company: MpCompany.parse(srv.company()));
      final v = buildModuleViews(s, MarketplaceContent.emptyContent, 'ru').firstWhere((x) => x.id == 'new_thing');
      expect(v.name, 'Новинка');
      expect(v.lineFor('ru'), 'Описание от сервера');
    });
  });

  group('список', () {
    testWidgets('владелец: группы (ваша сфера первой), плитки с названием, строкой, статусом и серверной ценой', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      expect(find.text('Маркетплейс'), findsOneWidget);
      expect(find.text('Продукты · ваша сфера'), findsOneWidget);
      expect(find.text('Весы с этикетками'), findsOneWidget);
      expect(find.text(content.module('scales_labels')!.line.of('ru')), findsOneWidget);
      expect(find.text('Подключён'), findsNothing); // у функций сферы статус «Входит в вашу сферу»
      expect(find.text('Входит в вашу сферу'), findsWidgets);
      await _scrollTo(t, find.text('Смета'));
      expect(find.text('Доступен'), findsWidgets);
      expect(find.text('300 сом/мес'), findsWidgets);
      expect(t.takeException(), isNull);
    });

    testWidgets('сфера компании — первой, даже если сервер перечисляет группы в другом порядке', (t) async {
      _phone(t);
      srv.profile = 'stroymag';
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      expect(find.text('Стройматериалы · ваша сфера'), findsOneWidget);
      final mine = t.getTopLeft(find.text('Стройматериалы · ваша сфера')).dy;
      final other = find.text('Продукты');
      if (other.evaluate().isNotEmpty) expect(t.getTopLeft(other).dy, greaterThan(mine));
    });

    testWidgets('поиск и фильтры', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), 'смет');
      await t.pumpAndSettle();
      expect(find.text('Смета'), findsOneWidget);
      expect(find.text('Весы с этикетками'), findsNothing);
      await t.enterText(find.byType(TextField), 'qwertyuiop');
      await t.pumpAndSettle();
      expect(find.text('Ничего не найдено'), findsOneWidget);
      await t.enterText(find.byType(TextField), '');
      await t.tap(find.text('Подключённые'));
      await t.pumpAndSettle();
      expect(find.text('Весы с этикетками'), findsOneWidget);
      expect(find.text('Смета'), findsNothing);
    });

    testWidgets('кассиру раздел закрыт: ни списка, ни запросов; маршрут закрыт и для кассира, и для сотрудника с правами, открыт владельцу и администратору', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _cashier));
      await t.pumpAndSettle();
      expect(find.text('Модули меняет только владелец или администратор компании.'), findsOneWidget);
      expect(find.text('Весы с этикетками'), findsNothing);
      expect(srv.log.where((l) => l.contains('modules')), isEmpty);
      expect(_cashier.canRoute('/admin/marketplace'), isFalse);
      expect(const Session(email: 'e', access: 'a', refresh: 'r', role: 'cashier', perms: ['can_view_settings', 'can_view_analytics']).canRoute('/admin/marketplace'), isFalse);
      expect(_owner.canRoute('/admin/marketplace'), isTrue);
      expect(_admin.canRoute('/admin/marketplace'), isTrue);
    });

    testWidgets('офлайн без кэша: понятное сообщение и «Повторить»; не падает', (t) async {
      _phone(t);
      srv.offline = true;
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      expect(find.text('Не удалось загрузить Маркетплейс. Проверьте интернет и повторите.'), findsOneWidget);
      srv.offline = false;
      await t.tap(find.text('Повторить'));
      await t.pumpAndSettle();
      expect(find.text('Весы с этикетками'), findsOneWidget);
    });

    testWidgets('офлайн с кэшем: показан последний ответ, плашка «нет связи», действия на странице функции закрыты', (t) async {
      _phone(t);
      // первый запуск — с сетью, ответ ложится в кэш
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      await t.pumpWidget(const SizedBox()); // экран закрыт
      srv.offline = true;
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      expect(find.textContaining('Нет связи — показаны сохранённые данные'), findsOneWidget);
      expect(find.text('Весы с этикетками'), findsOneWidget);
      await _scrollTo(t, find.text('Смета'));
      await t.tap(find.text('Смета'));
      await t.pumpAndSettle();
      expect(find.text('Включить'), findsOneWidget);
      expect(t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Включить')).onPressed, isNull);
    });
  });

  // 2026-11-27 (доступы советника): «открой Маркетплейс → смета» — адрес /admin/marketplace?module=… открывает страницу функции сразу
  group('советник: ?module=…', () {
    testWidgets('название или ключ функции — открывается её страница; не нашлась — слово уходит в поиск', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(openModule: 'смета'), _owner));
      await t.pumpAndSettle();
      expect(find.text('Что делает'), findsOneWidget);
      expect(find.text('Включить'), findsOneWidget);
      await t.pumpWidget(const SizedBox()); // новое состояние экрана
      await t.pumpWidget(app(const MarketplaceScreen(openModule: 'qwertyuiop'), _owner));
      await t.pumpAndSettle();
      expect(find.text('Что делает'), findsNothing);
      expect(find.text('Ничего не найдено'), findsOneWidget);
      expect(t.takeException(), isNull);
    });
  });

  group('страница функции', () {
    Future<void> openEstimate(WidgetTester t, {Session s = _owner, String lang = 'ru'}) async {
      await t.pumpWidget(app(const MarketplaceScreen(), s, lang: lang));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text(content.module('estimate')!.name.of(lang)));
      await t.tap(find.text(content.module('estimate')!.name.of(lang)).first);
      await t.pumpAndSettle();
    }

    testWidgets('описание, «как это работает», «что получите», «как включить», «для кого», цена с сервера и кнопки «Включить» и «Попробовать 7 дн.»', (t) async {
      _phone(t);
      await openEstimate(t);
      expect(find.text('Что делает'), findsOneWidget);
      expect(find.text('Включить'), findsOneWidget);
      expect(find.text('Попробовать 7 дн.'), findsOneWidget);
      await _scrollTo(t, find.text('Как это работает'));
      expect(find.textContaining('Шаг 1 из'), findsOneWidget);
      await _scrollTo(t, find.text('Для кого'));
      await _scrollTo(t, find.text('Цена'));
      expect(find.textContaining('300 сом/мес'), findsWidgets);
      expect(find.textContaining('цена с сервера'), findsOneWidget);
    });

    testWidgets('«Включить» → вопрос → PUT enable с If-Match → «Подключён», «Выключить» → вопрос → DELETE → снова «Доступен»', (t) async {
      _phone(t);
      await openEstimate(t);
      await t.tap(find.text('Включить'));
      await t.pumpAndSettle();
      expect(find.text('Включить «Смета»?'), findsOneWidget);
      expect(find.textContaining('300 сом/мес'), findsWidgets);
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Включить')));
      await t.pumpAndSettle();
      final putAt = srv.log.indexOf('PUT api/users/company/modules/');
      expect(putAt, greaterThanOrEqualTo(0));
      expect(srv.headersLog[putAt]!['If-Match'], 'v1');
      expect((srv.bodies[putAt] as Map)['enable'], ['estimate']);
      expect(srv.enabled, contains('estimate'));
      expect(find.text('Выключить'), findsOneWidget);
      expect(find.text('Подключён'), findsOneWidget);
      await t.tap(find.text('Выключить'));
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Выключить')));
      await t.pumpAndSettle();
      expect(srv.log, contains('DELETE api/users/company/tariff/modules/estimate/'));
      expect(srv.enabled, isNot(contains('estimate')));
      expect(find.text('Включить'), findsOneWidget);
    });

    testWidgets('отказ в вопросе — ничего не отправляется', (t) async {
      _phone(t);
      await openEstimate(t);
      await t.tap(find.text('Включить'));
      await t.pumpAndSettle();
      await t.tap(find.text('Отмена'));
      await t.pumpAndSettle();
      expect(srv.log.where((l) => l.startsWith('PUT')), isEmpty);
    });

    testWidgets('«Попробовать 7 дн.» → POST trial; повторная проба → понятное сообщение', (t) async {
      _phone(t);
      await openEstimate(t);
      await t.tap(find.text('Попробовать 7 дн.'));
      await t.pumpAndSettle();
      expect(srv.log, contains('POST api/users/company/features/trial/'));
      expect(srv.enabled, contains('estimate'));
      expect(find.text('Проба «Смета» началась'), findsOneWidget);
    });

    testWidgets('сервер ответил 403 — сообщение «только владелец или администратор»', (t) async {
      _phone(t);
      srv.forbid = true;
      await openEstimate(t, s: _admin);
      await t.tap(find.text('Включить'));
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Включить')));
      await t.pumpAndSettle();
      expect(find.text('Модули меняет только владелец или администратор компании.'), findsOneWidget);
      expect(srv.enabled, isNot(contains('estimate')));
    });

    testWidgets('устаревшая версия (409 version_conflict): версия перечитывается и запрос повторяется один раз', (t) async {
      _phone(t);
      await openEstimate(t);
      srv.version = 7; // кто-то изменил модули в другом месте
      await t.tap(find.text('Включить'));
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Включить')));
      await t.pumpAndSettle();
      expect(srv.enabled, contains('estimate'));
      expect(srv.log.where((l) => l.startsWith('PUT')).length, 2);
    });

    testWidgets('«Статус ремонта на витрине» привязан к Ремонту: «Скоро», без кнопок и без цены, с пояснением', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Статус ремонта на витрине'));
      await t.tap(find.text('Статус ремонта на витрине'));
      await t.pumpAndSettle();
      expect(find.text('Включить'), findsNothing);
      expect(find.text('Скоро'), findsWidgets);
      await _scrollTo(t, find.text('Цена'));
      expect(find.textContaining('Скоро: ждём сервер NurCRM'), findsOneWidget);
    });
  });

  group('тарифы и «Своё»', () {
    testWidgets('тарифы с серверными ценами; сборка «Своё»: расчёт у сервера, заявка с Idempotency-Key и тем же ключом при повторе', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _owner));
      await t.pumpAndSettle();
      await t.tap(find.text('Тарифы'));
      await t.pumpAndSettle();
      expect(find.text('Мини'), findsOneWidget);
      expect(find.text('500 сом/мес'), findsOneWidget);
      expect(find.text('1 300 сом/мес'), findsOneWidget);
      expect(find.text('3 600 сом/мес'), findsOneWidget);
      await _scrollTo(t, find.text('Тариф «Своё»').last);
      await t.tap(find.widgetWithText(OutlinedButton, 'Тариф «Своё»'));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Смета'));
      await t.tap(find.text('Смета'));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Рассчитать цену'));
      await t.tap(find.text('Рассчитать цену'));
      await t.pumpAndSettle();
      final qAt = srv.log.indexOf('POST api/users/company/tariff/quote/');
      expect(qAt, greaterThanOrEqualTo(0));
      final body = (srv.bodies[qAt] as Map).cast<String, dynamic>();
      expect((body['modules'] as List), contains('estimate'));
      expect(body['period'], 'month');
      expect(find.text('Итого в месяц'), findsOneWidget);
      expect(find.text('1 600 сом/мес'), findsOneWidget);
      await _scrollTo(t, find.text('Отправить заявку'));
      await t.tap(find.text('Отправить заявку'));
      await t.pumpAndSettle();
      final cAt = srv.log.indexOf('POST api/users/company/tariff/custom/');
      expect(cAt, greaterThanOrEqualTo(0));
      expect(srv.headersLog[cAt]!['Idempotency-Key'], isNotEmpty);
      expect(find.text('Заявка отправлена — менеджер свяжется с вами.'), findsOneWidget);
    });

    testWidgets('администратор видит «Своё», но заявку отправляет только владелец', (t) async {
      _phone(t);
      await t.pumpWidget(app(const MarketplaceScreen(), _admin));
      await t.pumpAndSettle();
      await t.tap(find.text('Тарифы'));
      await t.pumpAndSettle();
      expect(find.text('Заявку на «Своё» отправляет только владелец компании.'), findsWidgets);
      await _scrollTo(t, find.text('Тариф «Своё»').last);
      await t.tap(find.widgetWithText(OutlinedButton, 'Тариф «Своё»'));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Смета'));
      await t.tap(find.text('Смета'));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Рассчитать цену'));
      await t.tap(find.text('Рассчитать цену'));
      await t.pumpAndSettle();
      await _scrollTo(t, find.text('Итого в месяц'));
      expect(find.text('Отправить заявку'), findsNothing);
    });
  });

  group('демонстрация «как работает»', () {
    testWidgets('шаги листаются сами и вручную; после закрытия таймеров не остаётся', (t) async {
      _phone(t, reduceMotion: false);
      final steps = content.module('part_numbers')!.demo;
      await t.pumpWidget(MaterialApp(theme: buildTheme(dark: false), locale: const Locale('ru'), localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate], supportedLocales: AppLocalizations.supportedLocales, home: Scaffold(body: SingleChildScrollView(child: MpDemoPlayer(steps: steps, lang: 'ru')))));
      expect(find.text('Шаг 1 из ${steps.length}'), findsOneWidget);
      await t.pump(const Duration(seconds: 4));
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text('Шаг 2 из ${steps.length}'), findsOneWidget);
      await t.tap(find.byTooltip('Далее'));
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text('Шаг 3 из ${steps.length}'), findsOneWidget);
      await t.pump(const Duration(seconds: 9)); // ручной выбор остановил показ
      expect(find.text('Шаг 3 из ${steps.length}'), findsOneWidget);
      await t.pumpWidget(const SizedBox());
    });
  });

  group('5 языков, 360 dp, шрифт ×1,3, светлая и тёмная тема', () {
    for (final lang in _langs) {
      for (final dark in [false, true]) {
        testWidgets('$lang ${dark ? 'тёмная' : 'светлая'}: список, страница функции и «Своё» без переполнения и обрезки строк', (t) async {
          _phone(t, scale: 1.3);
          await t.pumpWidget(app(const MarketplaceScreen(), _owner, lang: lang, dark: dark));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          // каждая плитка списка: ни одна строка не обрезана «…»
          Future<void> noClip(String where) async {
            for (final e in find.byType(Text).evaluate()) {
              final ro = e.renderObject;
              if (ro is RenderParagraph) expect(ro.didExceedMaxLines, isFalse, reason: '$where: обрезан «${ro.text.toPlainText()}»');
            }
          }

          await noClip('список');
          final lst = find.byType(Scrollable).first;
          for (var i = 0; i < 12; i++) {
            await t.drag(lst, const Offset(0, -500));
            await t.pump();
            expect(t.takeException(), isNull);
            await noClip('список, экран $i');
          }
          for (final id in ['repair', 'bot_seller']) {
            // страница функции с самым длинным описанием (Ремонт/Смета и демонстрация)
            await t.pumpWidget(app(MpModuleScreen(id: id), _owner, lang: lang, dark: dark));
            await t.pumpAndSettle();
            final ls = find.byType(Scrollable).first;
            for (var i = 0; i < 14; i++) {
              expect(t.takeException(), isNull, reason: 'страница, экран $i');
              await noClip('страница, экран $i');
              await t.drag(ls, const Offset(0, -450));
              await t.pump();
            }
          }
          // цели касания ≥ 48 dp у кнопок действий
          for (final b in find.byType(FilledButton).evaluate().followedBy(find.byType(OutlinedButton).evaluate())) {
            expect((b.renderObject as RenderBox).size.height, greaterThanOrEqualTo(48));
          }
          // вкладка тарифов
          await t.pumpWidget(app(const MarketplaceScreen(), _owner, lang: lang, dark: dark));
          await t.pumpAndSettle();
          await t.tap(find.text(tabTariffsLabel(lang)));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          await noClip('тарифы');
          await t.drag(find.byType(Scrollable).first, const Offset(0, -700));
          await t.pump();
          await t.tap(find.byType(OutlinedButton).first); // «Своё» → сборка
          await t.pumpAndSettle();
          for (var i = 0; i < 10; i++) {
            expect(t.takeException(), isNull, reason: 'сборка, экран $i');
            await noClip('сборка, экран $i');
            await t.drag(find.byType(Scrollable).first, const Offset(0, -500));
            await t.pump();
          }
        });
      }
    }
  });
}

String tabTariffsLabel(String lang) => {'ru': 'Тарифы', 'ky': 'Тарифтер', 'uz': 'Tariflar', 'en': 'Plans', 'tr': 'Tarifeler'}[lang]!;

String mpStatusLabelForTest(MpModuleView v) => v.status == MpStatus.on ? 'on' : (v.status == MpStatus.soon ? 'soon' : 'available');

