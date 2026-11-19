// 2026-11-13 (Л-7 / Н-9, аудит UX мобилки): планшетная раскладка продажи («товары | чек»), понятные тексты ошибок сервера на 5 языках,
// единое пустое состояние, каркас push (разбор сообщений, безопасные переходы, настройки видов, регистрация токена только при трёх условиях).
// База в памяти, фейковый клиент: сервер и боевая база не трогаются.
import 'dart:async';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/push/push_service.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final posts = <String>[];
  final deletes = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'results': <dynamic>[]};
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    posts.add('$path $body');
    return {};
  }

  @override
  Future<dynamic> delete(String path) async {
    deletes.add(path);
    return {};
  }
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

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u', userName: 'Тест', role: 'owner', isOwner: true, cashboxId: 'b');

class _FakePush implements PushTransport {
  final bool allow;
  final tokens = StreamController<String>.broadcast();
  final opened = StreamController<Map<String, dynamic>>.broadcast();
  _FakePush({this.allow = true});
  @override
  bool get available => true;
  @override
  Future<bool> requestPermission() async => allow;
  @override
  Future<String?> token() async => 'tok-1';
  @override
  Stream<String> get onTokenRefresh => tokens.stream;
  @override
  Stream<Map<String, dynamic>> get onOpened => opened.stream;
}

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'w', name: 'Вода «Нур» 1,5 л', price: const Value(85), quantity: const Value(12), barcode: const Value('4870001112223'), searchKey: const Value('вода')),
      ProductsCompanion.insert(id: 's', name: 'Сок', price: const Value(140), quantity: const Value(5), barcode: const Value('4870001119999'), searchKey: const Value('сок')),
    ]);
    ApiText.l = null;
    ServerCapabilities.debugSet(null);
  });
  tearDown(() async {
    ApiText.l = null;
    ServerCapabilities.debugSet(null);
    await db.close();
  });

  Widget app(Widget home, {String lang = 'ru'}) => ProviderScope(
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(_Api()),
      sessionProvider.overrideWith(() => _Fixed(_owner)),
      shiftProvider.overrideWith(_OpenShift.new),
    ],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: Scaffold(body: home),
    ),
  );

  void screen(WidgetTester t, Size logical) {
    t.view.physicalSize = logical * 2;
    t.view.devicePixelRatio = 2.0;
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() {
      FlutterError.onError = prev;
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
    });
  }

  ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(SaleScreen)));

  group('Планшет: продажа — товары слева, чек справа', () {
    testWidgets('планшет 1280×800: вкладок «Товары / Чек» нет, плитки и строка чека видны одновременно', (t) async {
      screen(t, const Size(1280, 800));
      await t.pumpWidget(app(const SaleScreen()));
      await t.pumpAndSettle();
      expect(find.byType(SegmentedButton<bool>), findsOneWidget, reason: 'остался только переключатель «Розница / Опт» в шапке чека — вкладок нет');
      await t.tap(find.text('Вода «Нур» 1,5 л'));
      await t.pumpAndSettle();
      final cart = of(t).read(cartProvider);
      expect(cart.count, 1);
      // и плитка товара, и позиция чека на экране сразу; плитки слева, чек справа
      final tile = t.getRect(find.text('Сок'));
      final l = lookupAppLocalizations(const Locale('ru'));
      final inCheck = t.getRect(find.text(l.inCheck));
      expect(tile.center.dx, lessThan(inCheck.center.dx), reason: 'товары слева, чек справа');
      expect(find.widgetWithText(FilledButton, l.payAmount('85')), findsOneWidget, reason: 'строка «Оплатить» на месте');
      expect(find.text(l.toProductsBtn), findsNothing, reason: 'кнопка «К товарам» на планшете не нужна');
    });

    testWidgets('телефон 360×800: как раньше — вкладки «Товары / Чек», чек открывается отдельной вкладкой', (t) async {
      screen(t, const Size(360, 800));
      await t.pumpWidget(app(const SaleScreen()));
      await t.pumpAndSettle();
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.productsTitle), findsOneWidget);
      expect(find.text(l.inCheck), findsNothing, reason: 'чек спрятан за вкладкой');
      await t.tap(find.textContaining(l.checkTab(0).split(' ').first).first);
      await t.pumpAndSettle();
      expect(find.text(l.inCheck), findsOneWidget);
      expect(find.text(l.toProductsBtn), findsOneWidget, reason: 'пустой чек на телефоне: кнопка «К товарам»');
    });

    testWidgets('планшет, 5 языков: без переполнения вёрстки', (t) async {
      screen(t, const Size(1024, 600));
      for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
        await t.pumpWidget(app(const SaleScreen(), lang: lang));
        await t.pumpAndSettle();
        of(t).read(cartProvider.notifier).add((await db.productById('s'))!);
        await t.pumpAndSettle();
        expect(t.takeException(), isNull, reason: lang);
        of(t).read(cartProvider.notifier).clear();
      }
    });
  });

  group('Тексты ошибок сервера', () {
    test('английские фразы Django → текст приложения; незнакомый текст не трогается', () {
      expect(ApiText.friendly({'detail': 'You do not have permission to perform this action.'}, 403), ApiText.forbidden);
      expect(ApiText.friendly({'detail': 'Not found.'}, 404), ApiText.notFound);
      expect(ApiText.friendly({'detail': 'Request was throttled. Expected available in 30 seconds.'}, 429), ApiText.throttled);
      expect(ApiText.friendly({'name': ['This field is required.']}, 400), ApiText.fieldRequired);
      expect(ApiText.friendly({'price': ['A valid number is required.']}, 400), ApiText.numberInvalid);
      expect(ApiText.friendly({'barcode': ['product with this barcode already exists.']}, 400), ApiText.alreadyExists);
      expect(ApiText.friendly({'detail': 'Смена уже закрыта'}, 400), 'Смена уже закрыта', reason: 'русский текст сервера — как есть');
      expect(ApiText.friendly({'qty': ['слишком много']}, 400), 'qty: слишком много', reason: 'незнакомое — прежний вид');
    });

    test('ответ без тела: 403, 404, 429 — понятные тексты, остальное — общий путь', () {
      expect(ApiText.friendly(null, 403), ApiText.forbidden);
      expect(ApiText.friendly('', 404), ApiText.notFound);
      expect(ApiText.friendly(null, 429), ApiText.throttled);
      expect(ApiText.friendly(null, 502), isNull);
      expect(ApiText.friendly(<String, dynamic>{}, 400), isNull);
    });

    test('все 5 языков: новые тексты есть и отличаются от русского', () async {
      final ru = await AppLocalizations.delegate.load(const Locale('ru'));
      for (final lang in ['ky', 'uz', 'en', 'tr']) {
        final l = await AppLocalizations.delegate.load(Locale(lang));
        for (final (a, b) in [
          (l.apiForbidden, ru.apiForbidden),
          (l.apiNotFound, ru.apiNotFound),
          (l.apiThrottled, ru.apiThrottled),
          (l.apiFieldRequired, ru.apiFieldRequired),
          (l.apiNumberInvalid, ru.apiNumberInvalid),
          (l.apiAlreadyExists, ru.apiAlreadyExists),
          (l.receiptOfflineNote, ru.receiptOfflineNote),
        ]) {
          expect(a.trim(), isNotEmpty, reason: lang);
          expect(a, isNot(b), reason: lang);
        }
      }
      ApiText.l = await AppLocalizations.delegate.load(const Locale('en'));
      expect(ApiText.friendly({'detail': 'Not found.'}, 404), 'Not found — the record may have been deleted. Refresh the list.');
    });
  });

  group('Пустое состояние', () {
    testWidgets('значок + фраза (+ кнопка по желанию), цель касания кнопки ≥ 48 dp', (t) async {
      screen(t, const Size(360, 640));
      var taps = 0;
      await t.pumpWidget(app(EmptyState(icon: Icons.inbox_outlined, text: 'Пусто', actionLabel: 'К продаже', onAction: () => taps++)));
      expect(find.byIcon(Icons.inbox_outlined), findsOneWidget);
      expect(find.text('Пусто'), findsOneWidget);
      expect(t.getSize(find.byType(OutlinedButton)).height, greaterThanOrEqualTo(48));
      await t.tap(find.text('К продаже'));
      expect(taps, 1);
    });
  });

  group('Push: каркас', () {
    test('вид по коду: регистр и дефисы не важны, незнакомый — unknown', () {
      expect(PushKind.fromCode('Site-Order'), PushKind.siteOrder);
      expect(PushKind.fromCode('LOW_STOCK'), PushKind.lowStock);
      expect(PushKind.fromCode('что-то'), PushKind.unknown);
      expect(PushKind.fromCode(null), PushKind.unknown);
    });

    test('разбор сообщения: путь по виду; чужие ссылки отбрасываются', () {
      expect(PushMessage.parse({'type': 'site_order', 'title': 'Заказ', 'body': '№5'}).route, '/admin/orders');
      expect(PushMessage.parse({'type': 'low_stock'}).route, '/stock?filter=low');
      expect(PushMessage.parse({'type': 'sale', 'route': '/checks'}).route, '/checks');
      expect(PushMessage.parse({'type': 'sale', 'route': 'https://evil.example/x'}).route, '/checks', reason: 'внешняя ссылка → путь по виду');
      expect(PushMessage.parse({'type': 'sale', 'route': '//evil.example'}).route, '/checks');
      expect(PushMessage.parse({'route': 'javascript:alert(1)'}).route, isNull);
      expect(PushMessage.parse(null).kind, PushKind.unknown);
    });

    test('каждый вид ведёт на существующий маршрут приложения', () {
      for (final k in PushKind.values.where((k) => k != PushKind.unknown)) {
        final r = k.route!;
        expect(r.startsWith('/'), isTrue);
        expect(const {'/checks', '/admin/shifts', '/admin/orders', '/stock'}.contains(r.split('?').first), isTrue, reason: k.name);
      }
    });

    test('настройки видов: по умолчанию включены все; выключение переживает чтение', () async {
      expect(await PushPrefs.enabled(db, PushKind.sale), isTrue);
      await PushPrefs.set(db, PushKind.sale, false);
      await PushPrefs.set(db, PushKind.lowStock, false);
      expect(await PushPrefs.enabled(db, PushKind.sale), isFalse);
      expect(await PushPrefs.enabled(db, PushKind.siteOrder), isTrue);
      await PushPrefs.set(db, PushKind.sale, true);
      expect(await PushPrefs.disabled(db), {PushKind.lowStock});
      await PushPrefs.set(db, PushKind.unknown, false);
      expect(await PushPrefs.disabled(db), {PushKind.lowStock});
    });

    test('заглушка NoPushTransport ничего не делает: ни разрешения, ни запросов на сервер', () async {
      final api = _Api();
      ServerCapabilities.debugSet([PushService.capability]);
      final s = PushService(transport: const NoPushTransport(), api: api, open: (_) {});
      expect(s.ready, isFalse);
      expect(await s.start(platform: 'android', lang: 'ru'), isFalse);
      expect(api.posts, isEmpty);
    });

    test('токен уходит на сервер только при трёх условиях: транспорт, признак сервера, разрешение', () async {
      // нет признака сервера
      var api = _Api();
      var s = PushService(transport: _FakePush(), api: api, open: (_) {});
      expect(await s.start(platform: 'android', lang: 'ru'), isFalse);
      expect(api.posts, isEmpty);
      // признак есть, но человек отказал
      ServerCapabilities.debugSet([PushService.capability]);
      api = _Api();
      s = PushService(transport: _FakePush(allow: false), api: api, open: (_) {});
      expect(await s.start(platform: 'android', lang: 'ru'), isFalse);
      expect(api.posts, isEmpty);
      // всё есть
      api = _Api();
      final fake = _FakePush();
      final opened = <String>[];
      s = PushService(transport: fake, api: api, open: opened.add);
      expect(await s.start(platform: 'android', lang: 'ky'), isTrue);
      expect(api.posts.single, contains('tok-1'));
      expect(api.posts.single, contains('mobile'));
      // тот же токен повторно не отправляется, новый — отправляется
      fake.tokens.add('tok-1');
      fake.tokens.add('tok-2');
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(api.posts.length, 2);
      expect(api.posts.last, contains('tok-2'));
      // нажатие на уведомление ведёт на экран
      fake.opened.add({'type': 'site_order'});
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(opened, ['/admin/orders']);
      // выход снимает токен
      await s.stop();
      expect(api.deletes, [PushService.registerPath]);
    });
  });
}
