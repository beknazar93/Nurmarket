// 2026-10-29 (аварийный аудит мобилки, пп. 2, 3, 5, 10 и «Мелкое»): каталог по ленте изменений, единый поиск (ё=е, раскладка,
// транслит), замок очереди между изолятами, плашка «Нет связи · данные от ЧЧ:ММ», страница входа Wi-Fi вместо JSON.
// Сервер не трогается — запросы идут в фейк или в локальный HttpServer.
import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/search_match.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/ui/link_banner.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef _Handler = Future<dynamic> Function(String path, Map<String, dynamic>? query);

class _FakeApi extends ApiClient {
  _FakeApi(this.handler) : super(SessionStore());
  _Handler handler;
  final calls = <String>[];
  final queries = <Map<String, dynamic>?>[];
  final matches = <String?>[]; // If-None-Match каждого запроса

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    calls.add(path);
    queries.add(query);
    matches.add(ifNoneMatch);
    ifNoneMatch = null;
    return handler(path, query);
  }
}

Map<String, dynamic> _p(String id, String name, {String? barcode, String? sku, num price = 100, num qty = 5}) => {
  'id': id,
  'name': name,
  'barcode': ?barcode,
  'sku': ?sku,
  'price': price,
  'quantity': qty,
  'category': 'Еда',
};

void main() {
  group('поиск как у кассы (п. 10)', () {
    test('normalize: ё=е, кыргызские буквы, регистр, пробелы', () {
      expect(SearchMatch.normalize('  ЁЛКА   Үй  '), 'елка уй');
      expect(SearchMatch.normalize('Өрүк Аңдоо'), 'орук андоо');
    });

    test('слова в любом порядке, раскладка и транслит', () {
      final key = SearchMatch.key(['Асу негазированная 1л', 'asu-1', '4600000000001', 'Вода', '']);
      expect(SearchQuery('негаз асу').matches(key), isTrue);
      expect(SearchQuery('ASU').matches(key), isTrue); // asu-1 в ключе
      expect(SearchQuery('ФЫГ').matches(key), isFalse);
      expect(SearchQuery('ФЫГ').matchesFuzzy(key), isTrue); // «не та раскладка»: ФЫГ → asu
      final k2 = SearchMatch.key(['Асу негазированная']);
      expect(SearchQuery('Asu').matchesFuzzy(k2), isTrue); // транслит Asu → Асу
      expect(SearchQuery('').isEmpty, isTrue);
    });

    test('db.searchProducts: елка находит ёлку, Asu — Асу, фыг — ASU; без совпадений — пусто', () async {
      final db = AppDatabase(NativeDatabase.memory());
      await db.replaceProducts([
        ProductsCompanion.insert(id: '1', name: 'Ёлка новогодняя', searchKey: Value(SearchMatch.key(['Ёлка новогодняя']))),
        ProductsCompanion.insert(id: '2', name: 'Асу негазированная 1л', searchKey: Value(SearchMatch.key(['Асу негазированная 1л']))),
        ProductsCompanion.insert(id: '3', name: 'ASU cola', searchKey: Value(SearchMatch.key(['ASU cola']))),
      ]);
      expect((await db.searchProducts('елка')).map((p) => p.id), ['1']);
      expect((await db.searchProducts('негаз асу')).map((p) => p.id), ['2']);
      expect((await db.searchProducts('Asu')).map((p) => p.id), ['3']); // как набрано есть — транслит не нужен
      expect((await db.searchProducts('ФЫГ')).map((p) => p.id), ['3']); // ничего как набрано → раскладка: ФЫГ → asu
      expect(await db.searchProducts('qqqqq'), isEmpty);
      expect((await db.searchProducts('')).length, 3);
      await db.close();
    });
  });

  group('каталог по ленте изменений (п. 2)', () {
    late AppDatabase db;
    late List<Map<String, dynamic>> list;
    late Future<dynamic> Function(Map<String, dynamic>? q) feed;
    late _FakeApi api;
    var version = 5;

    setUp(() {
      version = 5;
      db = AppDatabase(NativeDatabase.memory());
      list = [_p('p1', 'Вода', barcode: '4600000000001', sku: 'V1'), _p('p2', 'Хлеб', barcode: '4600000000002')];
      feed = (q) async => {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
      api = _FakeApi((path, q) async {
        if (path == 'api/main/products/list/') return {'results': list, 'next': null};
        if (path == 'api/main/products/catalog-version/') return {'catalog_version': version};
        if (path == 'api/main/products/changes/') return feed(q);
        throw ApiException('нет', status: 404);
      });
    });
    tearDown(() => db.close());

    test('первый раз — целиком; потом только лента (без списка товаров)', () async {
      final repo = CatalogRepository(api, db);
      expect(await repo.sync(), 2);
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 1);
      expect(await db.getSetting(CatalogRepository.feedCursorKey), 'c1');
      expect(repo.feedActive, isTrue);

      api.calls.clear();
      api.queries.clear();
      expect(await repo.sync(), 0); // пустая лента
      expect(api.calls, ['api/main/products/changes/']);
      expect(api.queries.single!['since'], 'c1');
      expect(await db.getSetting(CatalogRepository.checkedAtKey), isNotNull);
    });

    test('лента: цена и остаток без названия; карточка без штрихкода не затирает штрихкод и артикул (урок Б-1)', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      feed = (q) async => {
        'cursor': 'c2',
        'changes': [
          {'id': 'p1', 'price': 120, 'quantity': 7}, // без названия
          {'id': 'p2', 'name': 'Ёлочка', 'price': 55}, // сокращённая карточка: ни штрихкода, ни артикула
          _p('p3', 'Новый товар', barcode: '4600000000003'),
        ],
        'deleted': [],
        'has_more': false,
      };
      api.calls.clear();
      expect(await repo.sync(), 3);
      expect(api.calls, ['api/main/products/changes/']);
      final p1 = (await db.productById('p1'))!;
      expect(p1.price, 120);
      expect(p1.quantity, 7);
      expect(p1.barcode, '4600000000001');
      expect(p1.sku, 'V1');
      final p2 = (await db.productById('p2'))!;
      expect(p2.name, 'Ёлочка');
      expect(p2.price, 55);
      expect(p2.barcode, '4600000000002'); // не затёрт
      expect(p2.searchKey, contains('елочка')); // ключ нормализован
      expect(p2.searchKey, contains('4600000000002'));
      expect(await db.productById('p3'), isNotNull);
      expect(await db.getSetting(CatalogRepository.feedCursorKey), 'c2');
      expect((await db.searchProducts('елочка')).single.id, 'p2');
    });

    test('лента: удалённые товары убираются; страницы по has_more', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      var page = 0;
      feed = (q) async {
        page++;
        if (page == 1) {
          return {'cursor': 'c2', 'changes': [{'id': 'p1', 'quantity': 1}], 'deleted': ['p2'], 'has_more': true};
        }
        return {'cursor': 'c3', 'changes': [{'id': 'p1', 'quantity': 2}], 'deleted': [], 'has_more': false};
      };
      await repo.sync();
      expect(page, 2);
      expect(await db.productById('p2'), isNull);
      expect((await db.productById('p1'))!.quantity, 2);
      expect(await db.getSetting(CatalogRepository.feedCursorKey), 'c3');
    });

    test('остаток неотправленной продажи вычитается из остатка ленты', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      await db.enqueue(
        OutboxCompanion.insert(
          id: 'k1',
          kind: 'sale',
          body: jsonEncode({'items': [{'product': 'p1', 'qty': 2}]}),
          createdAt: DateTime.now(),
        ),
      );
      feed = (q) async => {'cursor': 'c2', 'changes': [{'id': 'p1', 'quantity': 10}], 'deleted': [], 'has_more': false};
      await repo.sync();
      expect((await db.productById('p1'))!.quantity, 8); // 10 с сервера − 2 в очереди
    });

    test('ленты нет (404) — прежняя полная загрузка, лента не мучает сервер', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      feed = (q) async => throw ApiException('нет', status: 404);
      version = 6; // каталог изменился
      api.calls.clear();
      await repo.sync(); // лента 404, версия другая → полная загрузка
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 1);
      expect(repo.feedActive, isFalse);
      api.calls.clear();
      await repo.sync(); // лента на паузе: версия каталога та же и загрузка недавняя — качать нечего
      expect(api.calls.where((c) => c == 'api/main/products/list/'), isEmpty);
      expect(api.calls.where((c) => c == 'api/main/products/changes/'), isEmpty);
    });

    test('курсор устарел (410) — полная загрузка; другой магазин — тоже', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      feed = (q) async => throw ApiException('устарел', status: 410);
      api.calls.clear();
      await repo.sync();
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 1);
      // другой магазин: область ленты не совпала — целиком
      final repo2 = CatalogRepository(api, db);
      await db.setSetting(CatalogRepository.feedScopeKey, 'другой|магазин|x');
      feed = (q) async => {'cursor': 'z', 'changes': [], 'deleted': [], 'has_more': false};
      api.calls.clear();
      await repo2.sync();
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 1);
    });

    test('сеть оборвалась — исключение наверх, каталог не тронут', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      feed = (q) async => throw ApiException('Нет связи с сервером.');
      expect(repo.sync(), throwsA(isA<ApiException>()));
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(await db.productCount(), 2);
    });

    test('369 товаров по 100 на страницу: грузятся все до конца по next/count (раньше — 100, остальные стирались)', () async {
      list = [for (var i = 0; i < 369; i++) _p('p$i', 'Товар $i', barcode: '46000${i.toString().padLeft(8, '0')}')];
      api.handler = (path, q) async {
        if (path == 'api/main/products/list/') {
          final page = q!['page'] as int;
          final slice = list.skip((page - 1) * 100).take(100).toList(); // сервер отдаёт максимум 100 строк, что бы ни просили
          return {'count': list.length, 'next': page * 100 < list.length ? 'x?page=${page + 1}' : null, 'results': slice};
        }
        if (path == 'api/main/products/changes/') return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
        throw ApiException('нет', status: 404);
      };
      expect(await CatalogRepository(api, db).sync(), 369);
      expect(await db.productCount(), 369);
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 4);
    });

    test('список без next и count: идём, пока страницы не пусты; страница-500 за концом — конец списка', () async {
      list = [for (var i = 0; i < 250; i++) _p('p$i', 'Товар $i')];
      api.handler = (path, q) async {
        if (path == 'api/main/products/list/') {
          final page = q!['page'] as int;
          if (page > 3) throw ApiException('Ошибка сервера 500.', status: 500);
          return list.skip((page - 1) * 100).take(100).toList();
        }
        if (path == 'api/main/products/changes/') return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
        throw ApiException('нет', status: 404);
      };
      expect(await CatalogRepository(api, db).sync(), 250);
      expect(await db.productCount(), 250);
    });

    test('неполная загрузка (count не набран) локальные товары не стирает', () async {
      await db.replaceProducts([for (var i = 0; i < 369; i++) ProductsCompanion.insert(id: 'old$i', name: 'Старый $i')]);
      list = [for (var i = 0; i < 200; i++) _p('n$i', 'Новый $i')];
      api.handler = (path, q) async {
        if (path == 'api/main/products/list/') {
          final page = q!['page'] as int;
          return {'count': 369, 'next': null, 'results': list.skip((page - 1) * 100).take(100).toList()};
        }
        throw ApiException('нет', status: 404);
      };
      await CatalogRepository(api, db).sync();
      expect(await db.productCount(), 369 + 200); // старые на месте, полученные добавлены
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNull); // загрузка не считается полной — повторится
    });

    test('обрыв посреди загрузки — база не тронута', () async {
      await db.replaceProducts([for (var i = 0; i < 369; i++) ProductsCompanion.insert(id: 'old$i', name: 'Старый $i')]);
      api.handler = (path, q) async {
        if (path == 'api/main/products/list/') {
          if ((q!['page'] as int) > 1) throw ApiException('Нет связи с сервером.');
          return {'count': 369, 'next': 'x', 'results': [for (var i = 0; i < 100; i++) _p('n$i', 'Новый $i')]};
        }
        throw ApiException('нет', status: 404);
      };
      await expectLater(CatalogRepository(api, db).sync(), throwsA(isA<ApiException>()));
      expect(await db.productCount(), 369);
      expect(await db.productById('old0'), isNotNull);
    });

    test('ETag ленты: If-None-Match уходит без W/, ответ 304 — ничего не меняется', () async {
      final repo = CatalogRepository(api, db);
      feed = (q) async {
        api.lastEtag = 'W/"abc"'; // так ETag приписывает Cloudflare
        return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
      };
      await repo.sync(); // полная загрузка + первый проход ленты (курсор '' → c1)
      api.matches.clear();
      await repo.sync(); // запрос с курсором c1: ETag ещё не знаем, получаем его
      expect(api.matches.single, isNull);
      api.matches.clear();
      await repo.sync();
      expect(api.matches.single, '"abc"'); // без W/
      feed = (q) async => ApiClient.notModified;
      expect(await repo.sync(), 0);
      expect(await db.getSetting(CatalogRepository.feedCursorKey), 'c1');
    });

    test('непонятный ответ ленты — как «ленты нет»', () {
      expect(FeedPage.parse('<html>'), isNull);
      expect(FeedPage.parse({'foo': 1}), isNull);
      expect(FeedPage.parse({'results': [1, 2]}), isNull); // только идентификаторы
      final ok = FeedPage.parse({'data': {'changes': [{'id': 'a', 'deleted': true}, {'id': 'b'}], 'cursor': 7, 'has_more': true}});
      expect(ok!.deleted, ['a']);
      expect(ok.items.single['id'], 'b');
      expect(ok.cursor, '7');
      expect(ok.hasMore, isTrue);
    });
  });

  group('замок очереди между изолятами (п. 3)', () {
    test('второй владелец не берёт замок, пока первый не отпустил; просроченный забирается', () async {
      final db = AppDatabase(NativeDatabase.memory());
      expect(await db.tryAcquireQueueLock('app'), isTrue);
      expect(await db.tryAcquireQueueLock('bg'), isFalse);
      await db.releaseQueueLock('bg'); // чужой отпустить не может
      expect(await db.tryAcquireQueueLock('bg'), isFalse);
      await db.releaseQueueLock('app');
      expect(await db.tryAcquireQueueLock('bg'), isTrue);
      await db.releaseQueueLock('bg');
      // просрочен (ttl 0) — забирается
      expect(await db.tryAcquireQueueLock('app', ttl: Duration.zero), isTrue);
      await Future<void>.delayed(const Duration(milliseconds: 5));
      expect(await db.tryAcquireQueueLock('bg'), isTrue);
      await db.close();
    });
  });

  testWidgets('плашка «Нет связи · данные от ЧЧ:ММ» появляется и уходит сама', (t) async {
    final api = ApiClient(SessionStore());
    await t.pumpWidget(
      ProviderScope(
        overrides: [apiProvider.overrideWithValue(api)],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Scaffold(body: LinkBanner(dataAt: DateTime(2026, 10, 29, 14, 5))),
        ),
      ),
    );
    expect(find.textContaining('Нет связи'), findsNothing);
    api.link.value = LinkStatus(offline: true, lastOk: DateTime(2026, 10, 29, 14, 5));
    await t.pump();
    expect(find.textContaining('Нет связи · данные от'), findsOneWidget);
    api.link.value = LinkStatus(lastOk: DateTime(2026, 10, 29, 14, 20));
    await t.pump();
    expect(find.textContaining('Нет связи'), findsNothing);
  });
}
