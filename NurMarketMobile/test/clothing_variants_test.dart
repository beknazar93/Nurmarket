// 2026-10-31 (владелец: «почему продажа одежды в мобилке нету? Реализуй» + «режим магазина тоже добавь»): размеры и цвета при
// продаже, остаток по размеру (в том числе без связи), штрихкод размера, режим магазина (сфера кассы). Без сети и без боевой
// базы: база в памяти, фейковый клиент; NBS из тестов не трогается.
import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/shop_mode.dart';
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/sale/variant_picker.dart';
import 'package:nurmarket/features/sale/variant_service.dart';
import 'package:nurmarket/features/profile/profile_screen.dart' show chooseShopMode;
import 'package:nurmarket/features/scan/code_lookup.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

/// Фейковый сервер: пути → ответы; post записывает тела; сбой связи — status null.
class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final Map<String, dynamic Function(Map<String, dynamic>? query)> routes = {};
  final List<String> gets = [];
  final List<({String path, Object? body, Map<String, String>? headers})> posts = [];
  final List<({String path, Object? body})> patches = [];
  Future<dynamic> Function(Object? body, Map<String, String>? headers)? onPost;
  bool offline = false;

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    ifNoneMatch = null;
    gets.add(path);
    if (offline) throw ApiException('нет связи');
    final r = routes[path];
    if (r == null) throw ApiException('404', status: 404);
    return r(query);
  }

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    posts.add((path: path, body: body, headers: headers));
    if (onPost != null) return onPost!(body, headers);
    if (offline) throw ApiException('нет связи');
    return {'id': 'srv1', 'number': '12', 'total': '2400.00'};
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) async {
    patches.add((path: path, body: body));
    if (offline) throw ApiException('нет связи');
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

Map<String, dynamic> _v(String id, String size, String color, num qty, {String? barcode, num? price, bool active = true}) => {
  'id': id,
  'product': 'tee',
  'size': size,
  'color': color,
  'quantity': '$qty.000',
  'price': price == null ? null : '$price.00',
  'barcode': barcode,
  'is_active': active,
};

final _teeVariants = [
  _v('v-m-red', 'M', 'Красный', 3, barcode: '2900000000011'),
  _v('v-l-red', 'L', 'Красный', 0, barcode: '2900000000028'), // закончился
  _v('v-m-blue', 'M', 'Синий', 5, price: 1500, barcode: '2900000000035'),
  _v('v-xl-blue', 'XL', 'Синий', 4, active: false, barcode: '2900000000042'), // отключён
  _v('v-s-red', 'S', 'Красный', 2),
];

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  late _Api api;
  late Product teeP;

  Future<void> seed() async {
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'tee', name: 'Футболка', price: const Value(1200), quantity: const Value(14), barcode: const Value('4600000000017'), searchKey: const Value('футболка 4600000000017')),
      ProductsCompanion.insert(id: 'mug', name: 'Кружка', price: const Value(300), quantity: const Value(9), barcode: const Value('4600000000024'), searchKey: const Value('кружка 4600000000024')),
    ]);
  }

  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    api = _Api();
    api.attach(_owner);
    api.routes['api/main/products/tee/variants/'] = (_) => _teeVariants;
    api.routes['api/main/products/mug/variants/'] = (_) => <dynamic>[];
    await seed();
    teeP = (await db.productById('tee'))!;
  });
  tearDown(() => db.close());

  Product tee() => teeP;

  // ------------------------------------------------------------------ база и миграция
  group('база: варианты товара', () {
    test('миграция 7 → 8: старые данные целы, таблицы вариантов создаются и работают', () async {
      final old = AppDatabase(
        NativeDatabase.memory(
          setup: (raw) {
            raw.execute('''CREATE TABLE products (
              id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL DEFAULT 0, wholesale_price REAL NOT NULL DEFAULT 0,
              barcode TEXT NULL, alt_barcodes TEXT NOT NULL DEFAULT '', sku TEXT NULL, plu TEXT NULL, unit TEXT NOT NULL DEFAULT 'шт',
              is_weight INTEGER NOT NULL DEFAULT 0, is_service INTEGER NOT NULL DEFAULT 0, is_favorite INTEGER NOT NULL DEFAULT 0,
              quantity REAL NOT NULL DEFAULT 0, category TEXT NOT NULL DEFAULT '', image_url TEXT NULL,
              search_key TEXT NOT NULL DEFAULT '', promo_rules TEXT NOT NULL DEFAULT '', min_quantity REAL NOT NULL DEFAULT 0, purchase_price REAL NULL)''');
            raw.execute('CREATE TABLE outbox (id TEXT NOT NULL PRIMARY KEY, kind TEXT NOT NULL, body TEXT NOT NULL, created_at INTEGER NOT NULL, '
                "status TEXT NOT NULL DEFAULT 'pending', attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL, total REAL NOT NULL DEFAULT 0, "
                "summary TEXT NOT NULL DEFAULT '', payment_method TEXT NOT NULL DEFAULT '', server_id TEXT NULL, server_number TEXT NULL, "
                "shift_id TEXT NULL, display TEXT NOT NULL DEFAULT '[]')");
            raw.execute('CREATE TABLE settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)');
            raw.execute("INSERT INTO settings (key, value) VALUES ('shop.clothing', '1')");
            raw.execute("INSERT INTO products (id, name, price, quantity) VALUES ('old1', 'Старая футболка', 900, 7)");
            raw.execute('PRAGMA user_version = 7');
          },
        ),
      );
      expect((await old.productById('old1'))!.quantity, 7);
      expect(await old.hasAnyVariants(), isFalse);
      await old.replaceVariants('old1', [VariantService.toRow('old1', const ProductVariant('a', 'M', 'Красный', 3, null, true, barcode: '2900000000099'))]);
      expect((await old.variantsOf('old1')).single.size, 'M');
      expect((await old.variantByBarcode('2900000000099'))!.id, 'a');
      expect(await old.getSetting('shop.clothing'), '1', reason: 'настройки не тронуты');
      // старая настройка «Размеры и цвета при продаже» = режим «Одежда»
      expect(await ShopModeSync.read(old), ShopMode.clothing);
      await old.close();
    });

    test('сводка по размерам: неактивные не считаются, остаток — только положительный; бесхозные варианты чистятся', () async {
      await db.replaceVariants('tee', [for (final m in _teeVariants) VariantService.toRow('tee', VariantService.parse([m]).single)]);
      final s = (await db.variantSummary(['tee', 'mug']));
      expect(s.containsKey('mug'), isFalse, reason: 'у кружки размеров нет');
      expect(s['tee']!.count, 4, reason: '4 активных из 5');
      expect(s['tee']!.qty, 10, reason: '3 + 0 + 5 + 2 (отключённый XL не считается)');
      // бесхозные: товар пропал из каталога (полная загрузка заменила список) — варианты и отметка уходят вместе с ним
      await db.replaceProducts([ProductsCompanion.insert(id: 'mug', name: 'Кружка', price: const Value(300))]);
      expect(await db.variantsOf('tee'), isEmpty);
      expect(await db.variantCheck('tee'), isNull);
      // и удаление товара из ленты изменений
      await seed();
      await db.replaceVariants('tee', [VariantService.toRow('tee', const ProductVariant('x', 'S', '', 1, null, true))]);
      await db.applyCatalogChanges(deletedIds: ['tee']);
      expect(await db.variantsOf('tee'), isEmpty);
    });

    test('выход и смена аккаунта стирают размеры, отметки и режим магазина', () async {
      await db.replaceVariants('tee', [VariantService.toRow('tee', const ProductVariant('x', 'S', '', 1, null, true))]);
      await ShopModeSync.write(db, ShopMode.clothing);
      await AccountSwitch.wipeAll(db, directory: null, deleteSecret: (_) async {});
      expect(await db.variantsOf('tee'), isEmpty);
      expect(await db.variantCheck('tee'), isNull);
      expect(await ShopModeSync.read(db), isNull, reason: 'режим магазина прошлого аккаунта не переходит к новому');
      expect(await db.getSetting('shop.clothing'), isNull);
    });
  });

  // ------------------------------------------------------------------ служба вариантов
  group('VariantService: загрузка, кэш, офлайн', () {
    test('первый раз — с сервера в базу; второй раз — из базы без запроса; без вариантов — тоже запоминается', () async {
      final svc = VariantService(api, db);
      final first = await svc.forProduct('tee');
      expect(first.has, isTrue);
      expect(first.variants.length, 5);
      expect(first.active.length, 4, reason: 'отключённый размер не продаётся');
      final n = api.gets.length;
      final again = await svc.forProduct('tee');
      expect(again.variants.length, 5);
      expect(api.gets.length, n, reason: 'свежие данные (моложе 30 с) — без запроса');

      final mug = await svc.forProduct('mug');
      expect(mug.has, isFalse);
      expect(mug.unknown, isFalse);
      final m = api.gets.length;
      await svc.forProduct('mug');
      expect(api.gets.length, m, reason: 'товар без размеров сутки не переспрашивается');
    });

    test('без связи: известный товар — из базы, неизвестный — «не узнать», режим без обращений к серверу — тоже', () async {
      final svc = VariantService(api, db);
      await svc.forProduct('tee'); // записали
      api.offline = true;
      final known = await svc.forProduct('tee');
      expect(known.has, isTrue);
      expect(known.unknown, isFalse);
      final unknown = await svc.forProduct('mug');
      expect(unknown.unknown, isTrue, reason: 'о кружке ничего нет, а сервер не ответил');
      api.offline = false;
      final noServer = await VariantService(api, db).forProduct('mug', allowServer: false);
      expect(noServer.unknown, isTrue);
      expect(api.gets.where((g) => g.contains('mug')).length, 1, reason: 'allowServer=false — ни одного запроса');
    });

    test('свежие данные сервера не теряют неотправленную офлайн-продажу размера', () async {
      final svc = VariantService(api, db);
      await svc.forProduct('tee');
      // продали M красный ×2 без связи: остаток размера −2 на телефоне, чек в очереди
      final cart = const Cart().add(CartLine(productId: 'tee', name: 'Футболка · M · Красный', price: 1200, qty: 2, variantId: 'v-m-red'));
      api.offline = true;
      final repo = SalesRepository(api, db);
      final r = await repo.checkout(cart: cart, shiftId: 'sh1', method: PayMethod.cash);
      expect(r.queued, isTrue);
      expect((await db.variantById('v-m-red'))!.quantity, 1);
      expect((await db.productById('tee'))!.quantity, 12);
      // сервер ещё не знает о чеке и снова отдаёт 3
      api.offline = false;
      final fresh = await svc.reload('tee');
      expect(fresh!.firstWhere((v) => v.id == 'v-m-red').quantity, 1, reason: '3 с сервера минус 2 из очереди');
    });

    test('снять чек из очереди — остаток размера возвращается', () async {
      final svc = VariantService(api, db);
      await svc.forProduct('tee');
      final cart = const Cart().add(CartLine(productId: 'tee', name: 'Футболка · M · Синий', price: 1500, qty: 2, variantId: 'v-m-blue'));
      api.offline = true;
      await SalesRepository(api, db).checkout(cart: cart, shiftId: 'sh1', method: PayMethod.cash);
      expect((await db.variantById('v-m-blue'))!.quantity, 3);
      final row = (await db.pending()).single;
      await OutboxMeta.drop(db, row);
      expect((await db.variantById('v-m-blue'))!.quantity, 5);
      expect((await db.productById('tee'))!.quantity, 14);
    });
  });

  // ------------------------------------------------------------------ чек и деньги
  group('чек: два размера одного товара, деньги, ключ идемпотентности', () {
    test('два размера — две строки «Футболка · M · Красный»; тот же размер прибавляется; в тело уходит variant', () {
      final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api)]);
      addTearDown(c.dispose);
      final ctrl = c.read(cartProvider.notifier);
      final p = tee();
      ctrl.add(p, qty: 1, variantId: 'v-m-red', variantLabel: 'M · Красный');
      ctrl.add(p, qty: 2, variantId: 'v-m-blue', variantLabel: 'M · Синий', variantPrice: 1500);
      ctrl.add(p, qty: 1, variantId: 'v-m-red', variantLabel: 'M · Красный');
      final cart = c.read(cartProvider);
      expect(cart.count, 2);
      expect(cart.lines.first.name, 'Футболка · M · Красный');
      expect(cart.lines.first.qty, 2);
      expect(cart.lines[1].price, 1500, reason: 'цена размера, а не товара');
      expect(cart.total, 2 * 1200 + 2 * 1500);
      final body = SalesRepository.buildBody(cart: cart, shiftId: 'sh1', method: PayMethod.cash);
      final items = (body['items'] as List).cast<Map<String, dynamic>>();
      expect(items.map((i) => i['variant']), ['v-m-red', 'v-m-blue']);
      expect(items.map((i) => i['qty']), ['2', '2']);
      expect(items[1]['price'], '1500.00');
    });

    test('ключ идемпотентности записан на диск ДО отправки; повтор после обрыва — тот же ключ и то же тело; продажа в долг/скидка работают с variant', () async {
      final cart = Cart(
        lines: [CartLine(productId: 'tee', name: 'Футболка · M · Красный', price: 1200, qty: 2, variantId: 'v-m-red', discount: 100)],
        orderDiscountPercent: 10,
      );
      String? keyOnDisk;
      String? sentKey;
      Map<String, dynamic>? sentBody;
      api.onPost = (body, headers) async {
        final raw = await db.getSetting(SalesRepository.pendingKeySetting);
        keyOnDisk = (jsonDecode(raw!) as Map)['key'] as String;
        sentKey = headers!['Idempotency-Key'];
        sentBody = (body as Map).cast<String, dynamic>();
        throw ApiException('обрыв', status: 504); // исход неизвестен
      };
      final repo = SalesRepository(api, db);
      // в долг без сервера не оформляется: исход неизвестен, ключ остаётся
      await expectLater(
        repo.checkout(cart: cart, shiftId: 'sh1', method: PayMethod.debt, received: 0, client: const Client('c1', 'Айгуль', '')),
        throwsA(isA<CheckoutUnknownException>()),
      );
      expect(keyOnDisk, isNotNull, reason: 'ключ уже на диске в момент запроса');
      expect(sentKey, keyOnDisk);
      expect((sentBody!['items'] as List).single['variant'], 'v-m-red');
      expect((sentBody!['items'] as List).single['discount'], '100.00');
      expect(sentBody!['order_discount_percent'], '10.00');
      expect(sentBody!['client'], 'c1');
      // повтор той же оплаты — тот же ключ
      final firstKey = sentKey;
      api.onPost = (body, headers) async {
        sentKey = headers!['Idempotency-Key'];
        return {'id': 'srv9', 'number': '77', 'total': '1980.00'};
      };
      final r = await repo.checkout(cart: cart, shiftId: 'sh1', method: PayMethod.debt, received: 0, client: const Client('c1', 'Айгуль', ''));
      expect(sentKey, firstKey, reason: 'один ключ на операцию');
      expect(r.queued, isFalse);
    });

    test('офлайн-очередь: чек с variant лежит в очереди, остаток размера и товара уменьшен, повторная отправка — с тем же ключом', () async {
      await VariantService(api, db).forProduct('tee');
      final cart = const Cart().add(CartLine(productId: 'tee', name: 'Футболка · S · Красный', price: 1200, qty: 1, variantId: 'v-s-red'));
      api.offline = true;
      final repo = SalesRepository(api, db);
      final r = await repo.checkout(cart: cart, shiftId: 'sh1', method: PayMethod.cash);
      expect(r.queued, isTrue);
      final row = (await db.pending()).single;
      expect(row.id, r.key, reason: 'ключ идемпотентности = id записи очереди');
      final body = SalesRepository.replayBody(row);
      expect((body['items'] as List).single['variant'], 'v-s-red');
      expect((await db.variantById('v-s-red'))!.quantity, 1);
      // досылка уходит под тем же ключом
      api.offline = false;
      String? key;
      api.onPost = (b, h) async {
        key = h!['Idempotency-Key'];
        return {'id': 's', 'number': '1'};
      };
      await repo.replay(row);
      expect(key, row.id);
    });

    test('цена размера изменилась на сервере: сверка цен видит её, строка размера обновляется (не зацикливается на price_changed)', () async {
      api.routes['api/main/products/tee/'] = (_) => {'id': 'tee', 'price': '1200.00', 'wholesale_price': '0'};
      await VariantService(api, db).forProduct('tee');
      final cart = const Cart().add(CartLine(productId: 'tee', name: 'Футболка · M · Синий', price: 1400, qty: 1, variantId: 'v-m-blue'));
      final check = await SalesRepository(api, db).checkPrices(cart);
      expect(check.changes.single.variantId, 'v-m-blue');
      expect(check.changes.single.newPrice, 1500);
      final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api)]);
      addTearDown(c.dispose);
      c.read(cartProvider.notifier).restore(cart);
      c.read(cartProvider.notifier).applyPriceChanges(check.changes);
      expect(c.read(cartProvider).lines.single.price, 1500);
    });

    test('название строки с сервера: размер и цвет из variant_size / variant_color, без дублей', () {
      expect(saleItemName({'product_name': 'Футболка', 'variant_size': 'M', 'variant_color': 'Красный'}), 'Футболка · M · Красный');
      expect(saleItemName({'product_name': 'Футболка Mango', 'variant_size': 'M'}), 'Футболка Mango · M', reason: '«M» внутри названия — не повод пропускать размер');
      expect(saleItemName({'product_name': 'Футболка · M · Красный', 'variant_size': 'M', 'variant_color': 'Красный'}), 'Футболка · M · Красный');
      expect(saleItemName({'product_name': 'Кружка'}), 'Кружка');
    });
  });

  // ------------------------------------------------------------------ штрихкод размера
  group('штрихкод размера', () {
    test('локально: штрихкод размера находит товар; в разных написаниях (UPC/EAN с нулём) тоже', () async {
      await db.replaceVariants('tee', [for (final m in _teeVariants) VariantService.toRow('tee', VariantService.parse([m]).single)]);
      final hit = await db.productByScan('2900000000011');
      expect(hit!.product.id, 'tee');
      expect((await db.variantByBarcode('2900000000011'))!.id, 'v-m-red');
      expect(await db.variantByBarcode('1111111111111'), isNull);
    });

    test('сервер: products/barcode/{код}/ отдаёт товар и variant — размер и товар записываются, остальные размеры подтягиваются', () async {
      api.routes['api/main/products/list/'] = (_) => {'results': <dynamic>[], 'count': 0};
      api.routes['api/main/products/barcode/2900000000035/'] = (_) => {
        'id': 'tee',
        'name': 'Футболка',
        'price': '1200.00',
        'quantity': '14.000',
        'barcode': '4600000000017',
        'variant': _teeVariants[2],
      };
      CodeLookup.clearCache(gap: Duration.zero);
      await db.setSetting('shop.clothing', '1');
      await db.delete(db.products).go();
      final r = await CodeLookup.findOnServer(db, api, '2900000000035');
      expect(r.product!.id, 'tee');
      expect((await db.variantByBarcode('2900000000035'))!.id, 'v-m-blue');
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect((await db.variantsOf('tee')).length, greaterThanOrEqualTo(1));
    });

    test('в магазине без размеров лишнего запроса products/barcode/ нет', () async {
      api.routes['api/main/products/list/'] = (_) => {'results': <dynamic>[], 'count': 0};
      CodeLookup.clearCache(gap: Duration.zero);
      final r = await CodeLookup.findOnServer(db, api, '4999999999999');
      expect(r.product, isNull);
      expect(api.gets.any((g) => g.contains('barcode')), isFalse);
    });
  });

  // ------------------------------------------------------------------ окно выбора
  group('окно «Размер и цвет»', () {
    Widget host(Future<VariantPick?> Function(BuildContext) open, {double textScale = 1.0}) => MaterialApp(
      theme: buildTheme(dark: false),
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: Scaffold(
        body: Builder(builder: (c) => Center(child: ElevatedButton(onPressed: () => open(c), child: const Text('open')))),
      ),
    );

    void phone(WidgetTester t, {Size size = const Size(360, 640), double scale = 1.0}) {
      t.view.physicalSize = size * 2;
      t.view.devicePixelRatio = 2.0;
      t.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
        t.platformDispatcher.clearTextScaleFactorTestValue();
      });
    }

    final all = VariantService.parse(_teeVariants);

    testWidgets('сетка: размеры по порядку, закончившийся неактивен, отключённого нет; выбор → «В чек»; 360×640 без переполнения', (t) async {
      phone(t);
      VariantPick? got;
      await t.pumpWidget(host((c) async => got = await pickVariant(c, tee(), all.where((v) => v.active).toList())));
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.byKey(const Key('variant_v-xl-blue')), findsNothing, reason: 'отключённый размер не показывается');
      // закончившийся L — неактивная ячейка
      await t.tap(find.byKey(const Key('variant_v-l-red')), warnIfMissed: false);
      await t.pump();
      expect(t.widget<FilledButton>(find.byKey(const Key('variantAdd'))).onPressed, isNull, reason: 'недоступный размер выбрать нельзя');
      // порядок внутри красного: S, M, L
      final sx = t.getTopLeft(find.byKey(const Key('variant_v-s-red'))).dx;
      final mx = t.getTopLeft(find.byKey(const Key('variant_v-m-red'))).dx;
      expect(sx, lessThan(mx));
      // сенсорная зона >= 44
      expect(t.getSize(find.byKey(const Key('variant_v-m-red'))).height, greaterThanOrEqualTo(44));
      expect(t.getSize(find.byKey(const Key('variant_v-m-red'))).width, greaterThanOrEqualTo(44));
      await t.tap(find.byKey(const Key('variant_v-m-blue')));
      await t.pump();
      await t.tap(find.byKey(const Key('qtyPlus')));
      await t.pump();
      await t.tap(find.byKey(const Key('variantAdd')));
      await t.pumpAndSettle();
      expect(got!.variant.id, 'v-m-blue');
      expect(got!.qty, 2);
    });

    testWidgets('остаток по размеру: больше, чем есть, не набрать; то, что уже в чеке, вычитается; всё в чеке — размер неактивен', (t) async {
      phone(t);
      VariantPick? got;
      // M красный: 3 на складе, 2 уже в чеке → доступна 1 шт.; S красный: 2 в чеке из 2 → неактивен
      await t.pumpWidget(host((c) async => got = await pickVariant(c, tee(), all.where((v) => v.active).toList(), inCart: {'v-m-red': 2, 'v-s-red': 2})));
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('variant_v-s-red')), warnIfMissed: false);
      await t.pump();
      expect(t.widget<FilledButton>(find.byKey(const Key('variantAdd'))).onPressed, isNull);
      await t.tap(find.byKey(const Key('variant_v-m-red')));
      await t.pump();
      expect(t.widget<IconButton>(find.byKey(const Key('qtyPlus'))).onPressed, isNull, reason: 'доступна одна штука — «+» неактивен');
      await t.tap(find.byKey(const Key('variantAdd')));
      await t.pumpAndSettle();
      expect(got!.qty, 1);
    });

    testWidgets('большой шрифт и тёмная тема: без переполнения на 360×640', (t) async {
      phone(t, scale: 1.6);
      await t.pumpWidget(
        MaterialApp(
          theme: buildTheme(dark: true),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Scaffold(body: Builder(builder: (c) => ElevatedButton(onPressed: () => pickVariant(c, tee(), all.where((v) => v.active).toList()), child: const Text('open')))),
        ),
      );
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.byKey(const Key('variantAdd')), findsOneWidget);
    });

    testWidgets('все размеры закончились — сообщение, «В чек» не активна', (t) async {
      phone(t);
      final empty = [for (final v in all) if (v.active) ProductVariant(v.id, v.size, v.color, 0, v.price, true)];
      await t.pumpWidget(host((c) => pickVariant(c, tee(), empty)));
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('variantAllOut')), findsOneWidget);
      expect(t.widget<FilledButton>(find.byKey(const Key('variantAdd'))).onPressed, isNull);
    });

    testWidgets('свежие остатки приходят в открытое окно: размер, который закончился, перестаёт быть доступным', (t) async {
      phone(t);
      final fresh = [for (final v in all) if (v.active) ProductVariant(v.id, v.size, v.color, v.id == 'v-m-red' ? 0 : v.quantity, v.price, true)];
      await t.pumpWidget(host((c) => pickVariant(c, tee(), all.where((v) => v.active).toList(), refresh: Future.delayed(const Duration(milliseconds: 200), () => fresh))));
      await t.tap(find.text('open'));
      await t.pump(const Duration(milliseconds: 50));
      expect(find.byKey(const Key('variantSheetNote')), findsOneWidget, reason: '«Обновляем остатки…»');
      await t.pump(const Duration(milliseconds: 400));
      await t.tap(find.byKey(const Key('variant_v-m-red')), warnIfMissed: false);
      await t.pump();
      expect(t.widget<FilledButton>(find.byKey(const Key('variantAdd'))).onPressed, isNull);
    });
  });

  // ------------------------------------------------------------------ экран продажи
  group('экран продажи: плитка, выбор размера, строка чека, скан', () {
    Widget app() => ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        apiProvider.overrideWithValue(api),
        sessionProvider.overrideWith(() => _Fixed(_owner)),
        shiftProvider.overrideWith(_OpenShift.new),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: const Scaffold(body: SaleScreen()),
      ),
    );

    void phone(WidgetTester t) {
      t.view.physicalSize = const Size(720, 1280);
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

    testWidgets('режим «Одежда»: на плитке «N размеров/цветов» и остаток по размерам; тап → окно размеров → строка «Футболка · M · Красный»', (t) async {
      phone(t);
      await ShopModeSync.write(db, ShopMode.clothing);
      await t.runAsync(() async {}); // базы в памяти работают в реальном времени
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 1500)));
      await t.pump(const Duration(milliseconds: 400));
      await t.pump(const Duration(milliseconds: 400));
      expect(find.byKey(const Key('variantsTile_tee')), findsOneWidget, reason: 'у футболки в фоне подтянулись размеры');
      expect(find.byKey(const Key('variantsTile_mug')), findsNothing);
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.variantsTile(4)), findsOneWidget);
      expect(find.text('10 шт'), findsOneWidget, reason: 'остаток плитки — сумма по активным размерам с остатком');
      await t.tap(find.text('Футболка'));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('variant_v-m-red')));
      await t.pump();
      await t.tap(find.byKey(const Key('variantAdd')));
      await t.pumpAndSettle();
      final cart = of(t).read(cartProvider);
      expect(cart.lines.single.name, 'Футболка · M · Красный');
      expect(cart.lines.single.variantId, 'v-m-red');
      // +1 до остатка (3), дальше нельзя
      await t.tap(find.text(l.checkTab(1)));
      await t.pumpAndSettle();
      for (var i = 0; i < 4; i++) {
        await t.tap(find.byIcon(Icons.add).first);
        await t.pump(const Duration(milliseconds: 100));
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 30)));
        await t.pump(const Duration(milliseconds: 100));
      }
      expect(of(t).read(cartProvider).lines.single.qty, 3, reason: 'выше остатка размера (3) строку не поднять');
    });

    testWidgets('скан штрихкода размера кладёт нужный размер в чек сразу; отключённый и закончившийся — понятное сообщение', (t) async {
      phone(t);
      await ShopModeSync.write(db, ShopMode.clothing);
      await VariantService(api, db).forProduct('tee');
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.pump(const Duration(milliseconds: 400));
      Future<void> scan(String code) async {
        await t.enterText(find.byType(TextField).first, code);
        await t.testTextInput.receiveAction(TextInputAction.search);
        await t.pump(const Duration(milliseconds: 300));
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
        await t.pump(const Duration(milliseconds: 400));
      }

      final l = lookupAppLocalizations(const Locale('ru'));
      await scan('2900000000035');
      var lines = of(t).read(cartProvider).lines;
      expect(lines.single.variantId, 'v-m-blue');
      expect(lines.single.price, 1500);
      await scan('2900000000035');
      expect(of(t).read(cartProvider).lines.single.qty, 2, reason: 'тот же размер прибавляется');
      await scan('2900000000028'); // L красный — закончился
      expect(find.textContaining(l.variantScanOut('Футболка · L · Красный')), findsOneWidget);
      expect(of(t).read(cartProvider).lines.length, 1);
      await scan('2900000000042'); // XL синий — отключён
      expect(find.textContaining(l.variantScanInactive('Футболка · XL · Синий')), findsOneWidget);
      expect(of(t).read(cartProvider).lines.length, 1);
    });

    testWidgets('режим «Продукты» выбран явно: товар без размеров продаётся как раньше, лишних запросов размеров нет', (t) async {
      phone(t);
      await ShopModeSync.write(db, ShopMode.grocery);
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.pump(const Duration(milliseconds: 400));
      await t.tap(find.text('Кружка'));
      await t.pump(const Duration(milliseconds: 300));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pump(const Duration(milliseconds: 300));
      expect(of(t).read(cartProvider).lines.single.name, 'Кружка');
      expect(api.gets.any((g) => g.contains('variants')), isFalse, reason: 'режим выбран и размеров в магазине нет — товар не спрашиваем');
    });

    testWidgets('в режиме «Одежда» без связи и без данных о размерах продажа без размера не допускается', (t) async {
      phone(t);
      await ShopModeSync.write(db, ShopMode.clothing);
      api.offline = true;
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.pump(const Duration(milliseconds: 400));
      await t.tap(find.text('Кружка'));
      await t.pump(const Duration(milliseconds: 300));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump(const Duration(milliseconds: 500));
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.variantLoadFailed), findsOneWidget);
      expect(of(t).read(cartProvider).isEmpty, isTrue);
    });

    testWidgets('есть размеры, режим не «Одежда» — подсказка «Включить режим «Одежда»?»; «Включить» меняет режим и пишет его на сервер', (t) async {
      phone(t);
      await VariantService(api, db).forProduct('tee'); // размеры в базе
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.pump(const Duration(milliseconds: 600));
      expect(find.byKey(const Key('clothingHint')), findsOneWidget);
      await t.tap(find.byKey(const Key('clothingHintYes')));
      await t.pump(const Duration(milliseconds: 300));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump(const Duration(milliseconds: 600));
      expect(of(t).read(prefsProvider).clothing, isTrue);
      expect(await ShopModeSync.read(db), ShopMode.clothing);
      expect(api.patches.any((p) => p.path == ShopModeSync.companyPath && (p.body as Map)['market_sphere'] == 'clothing'), isTrue);
      expect(find.byKey(const Key('clothingHint')), findsNothing);
    });

    testWidgets('«Не нужно» убирает подсказку насовсем', (t) async {
      phone(t);
      await VariantService(api, db).forProduct('tee');
      await t.pumpWidget(app());
      await of(t).read(prefsProvider.notifier).load();
      await t.pump(const Duration(milliseconds: 600));
      await t.tap(find.byKey(const Key('clothingHintNo')));
      await t.pump(const Duration(milliseconds: 300));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      expect(find.byKey(const Key('clothingHint')), findsNothing);
      expect(await db.getSetting(ShopModeSync.hintOffKey), '1');
    });
  });

  // ------------------------------------------------------------------ режим магазина
  group('режим магазина (сфера кассы)', () {
    test('коды и базовые сферы как в кассе; hardware уходит на сервер как grocery', () {
      expect(ShopMode.values.map((m) => m.code), ['grocery', 'clothing', 'services', 'hardware']);
      expect(ShopMode.hardware.baseSphere, 'grocery');
      expect(ShopMode.clothing.baseSphere, 'clothing');
      expect(ShopModeX.fromServer('grocery', 'hardware'), ShopMode.hardware);
      expect(ShopModeX.fromServer('clothing', 'hardware'), ShopMode.clothing, reason: 'hardware совместима только с базовой grocery');
      expect(ShopModeX.fromServer('мусор', null), isNull);
      expect(ShopMode.clothing.variants, isTrue);
      expect(ShopMode.grocery.variants, isFalse);
      expect(ShopMode.grocery.weighing, isTrue);
      expect(ShopMode.clothing.weighing, isFalse);
    });

    test('выбор сохраняется и совместим со старой настройкой shop.clothing (её читают весовые этикетки)', () async {
      await ShopModeSync.write(db, ShopMode.clothing);
      expect(await db.getSetting('shop.clothing'), '1');
      await ShopModeSync.write(db, ShopMode.hardware);
      expect(await db.getSetting('shop.clothing'), '0');
      expect(await ShopModeSync.read(db), ShopMode.hardware);
    });

    test('сервер: новая установка берёт сферу; «Продукты» по умолчанию выбором не считается; выбор на настроенном телефоне не затирается', () async {
      // новая установка, сервер говорит «Одежда»
      expect(await ShopModeSync.applyServer(db, base: 'clothing'), ShopMode.clothing);
      expect(await ShopModeSync.read(db), ShopMode.clothing);
      // тот же ответ — без изменений
      expect(await ShopModeSync.applyServer(db, base: 'clothing'), isNull);
      // владелец сменил сферу на сайте — телефон берёт новую
      expect(await ShopModeSync.applyServer(db, base: 'services'), ShopMode.services);
      // hardware — из общих настроек компании
      expect(await ShopModeSync.applyServer(db, base: 'grocery', detail: 'hardware'), ShopMode.hardware);

      // телефон, где режим уже выбран, видит сервер впервые — выбор не меняется
      final fresh = AppDatabase(NativeDatabase.memory());
      await ShopModeSync.write(fresh, ShopMode.clothing);
      expect(await ShopModeSync.applyServer(fresh, base: 'grocery'), isNull);
      expect(await ShopModeSync.read(fresh), ShopMode.clothing);
      await fresh.close();

      // «Продукты» на сервере у новой установки — не выбор: режим остаётся «не выбирали» (чтобы сработала подсказка про одежду)
      final clean = AppDatabase(NativeDatabase.memory());
      expect(await ShopModeSync.applyServer(clean, base: 'grocery'), isNull);
      expect(await ShopModeSync.read(clean), isNull);
      // «не задан» не затирает ничего
      expect(await ShopModeSync.applyServer(clean, base: null), isNull);
      await clean.close();
    });

    test('чтение с сервера: market_sphere из api/users/company/ и MarketSphereDetail из общих настроек; нет связи — ничего не меняется', () async {
      api.routes[ShopModeSync.companyPath] = (_) => {'name': 'Мой магазин', 'market_sphere': 'grocery'};
      api.routes[ShopModeSync.settingsPath] = (_) => {
        'settings': {'MarketSphereDetail': 'hardware'},
      };
      expect(await ShopModeSync.syncFromServer(db, api), ShopMode.hardware);
      api.offline = true;
      expect(await ShopModeSync.syncFromServer(db, api), isNull);
      expect(await ShopModeSync.read(db), ShopMode.hardware);
    });

    test('запись на сервер: базовая сфера в company, точная (hardware) в общих настройках; нет прав/связи — только телефон', () async {
      api.routes[ShopModeSync.settingsPath] = (_) => {'settings': <String, dynamic>{}};
      expect(await ShopModeSync.saveToServer(db, api, ShopMode.hardware), isTrue);
      expect(api.patches.first.path, ShopModeSync.companyPath);
      expect((api.patches.first.body as Map)['market_sphere'], 'grocery');
      expect(api.patches.last.path, ShopModeSync.settingsPath);
      expect((api.patches.last.body as Map)[ShopModeSync.detailKey], 'hardware');
      api.patches.clear();
      expect(await ShopModeSync.saveToServer(db, api, ShopMode.clothing), isTrue);
      expect(api.patches.single.path, ShopModeSync.companyPath, reason: 'точной сферы в общих настройках не было — не создаём');
      api.offline = true;
      expect(await ShopModeSync.saveToServer(db, api, ShopMode.services), isFalse);
    });

    testWidgets('профиль: «Режим магазина» → выбор из четырёх режимов, галочка у текущего', (t) async {
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
      final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner))]);
      addTearDown(c.dispose);
      await t.pumpWidget(
        UncontrolledProviderScope(
          container: c,
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: Scaffold(
              body: Consumer(
                builder: (ctx, ref, _) => ElevatedButton(onPressed: () => chooseShopMode(ctx, ref), child: const Text('open')),
              ),
            ),
          ),
        ),
      );
      await t.tap(find.text('open'));
      await t.pumpAndSettle();
      for (final m in ShopMode.values) {
        expect(find.byKey(Key('shopMode_${m.code}')), findsOneWidget);
      }
      await t.tap(find.byKey(const Key('shopMode_clothing')));
      await t.pump(const Duration(milliseconds: 400));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump(const Duration(milliseconds: 400));
      expect(c.read(prefsProvider).shop, ShopMode.clothing);
      expect(await ShopModeSync.read(db), ShopMode.clothing);
      await t.pumpWidget(const SizedBox());
      c.dispose(); // таймер смены темы по времени (PrefsController) не должен пережить тест
    });
  });
}
