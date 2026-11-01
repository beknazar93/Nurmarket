// 2026-10-31 («в мобилке не отображаются все товары!», магазин NBS: 15 451 товар, у 15 129 остаток 10): загрузка каталога на фикстурах
// без сети. Сервер по факту отдаёт не больше 100 строк на страницу (page_size=300 игнорируется): 155 страниц подряд ≈ 3,5 минуты.
// Раньше любой сбой страницы (429, обрыв) выбрасывал все прочитанные страницы, телефон со 100 товарами из 15 451 ждал 12 часов,
// «Нет в наличии» искал среди первых 500 товаров. Здесь: постраничная запись, повтор страницы, докачка, сверка count, окно списка.
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/ui/link_banner.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef _Handler = Future<dynamic> Function(String path, Map<String, dynamic>? query);

class _FakeApi extends ApiClient {
  _FakeApi(this.handler) : super(SessionStore());
  _Handler handler;
  final listPages = <int>[]; // номера запрошенных страниц списка товаров (page_size=1 — проверка count, не считаем)
  var countCalls = 0;
  var feedCalls = 0;

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    if (path == 'api/main/products/list/') {
      if (query?['page_size'] == 1) {
        countCalls++;
      } else {
        listPages.add(query!['page'] as int);
      }
    }
    if (path == 'api/main/products/changes/') feedCalls++;
    ifNoneMatch = null;
    return handler(path, query);
  }
}

Map<String, dynamic> _p(int i) => {
  'id': 'p$i',
  'name': 'Товар $i',
  'barcode': '46${i.toString().padLeft(11, '0')}',
  'price': 100,
  'quantity': i % 100 == 0 ? 0 : 10, // у большинства остаток 10, как в NBS
  'category': 'Еда',
};

/// Сервер NBS: [n] товаров, не больше 100 строк на страницу при любом page_size, count и next — как у настоящего.
/// [hidden] — сколько товаров сервер считает в count, но списком не отдаёт.
_Handler _server(int n, {int hidden = 0, Future<void> Function(int page)? onPage}) {
  final shown = n - hidden;
  return (path, q) async {
    if (path == 'api/main/products/list/') {
      final size = q!['page_size'] as int;
      final page = q['page'] as int;
      if (size == 1) {
        return {'count': n, 'next': null, 'results': [_p(0)]};
      }
      await onPage?.call(page);
      final from = (page - 1) * 100;
      final rows = [for (var i = from; i < from + 100 && i < shown; i++) _p(i)];
      return {'count': n, 'next': from + 100 < shown ? 'https://x/api/main/products/list/?page=${page + 1}' : null, 'results': rows};
    }
    if (path == 'api/main/products/changes/') {
      return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
    }
    throw ApiException('нет', status: 404);
  };
}

void main() {
  late AppDatabase db;

  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_catalog1031_'));
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    CatalogRepository.pageRetryBase = Duration.zero;
  });
  tearDown(() => db.close());

  group('полная загрузка 15 451 товара', () {
    test('все 155 страниц по 100: в базе все 15 451, каталог полный, плашки нет', () async {
      final api = _FakeApi(_server(15451));
      final repo = CatalogRepository(api, db);
      expect(await repo.sync(), 15451);
      expect(await db.productCount(), 15451);
      expect(api.listPages.toSet().length, 155); // каждая страница ровно один раз
      expect(api.listPages.reduce((a, b) => a > b ? a : b), 155);
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNotNull);
      expect(await db.getSetting(CatalogRepository.incompleteKey), '');
      expect(await db.getSetting(CatalogRepository.gapKey), '0');
      expect(repo.progress.value.running, isFalse);
      expect(repo.progress.value.incomplete, isFalse);
      expect(repo.progress.value.loaded, 15451);
      expect(repo.progress.value.total, 15451);
      // остаток 10 у 15 129 товаров (у каждого сотого — 0): из базы телефона считается так же, как на сервере
      expect(await db.countProducts('', extra: (p) => p.quantity.equals(10)), 15451 - 155);
    });

    test('429 и обрыв на отдельных страницах повторяются: ничего не теряется', () async {
      final failedOnce = <int>{};
      final api = _FakeApi(_server(15451, onPage: (page) async {
        if ((page == 7 || page == 50 || page == 120) && failedOnce.add(page)) throw ApiException('Слишком часто', status: 429);
        if (page == 90 && failedOnce.add(page)) throw ApiException('Нет связи с сервером.'); // status null
        if (page == 33 && failedOnce.add(page)) throw ApiException('Ошибка сервера 502.', status: 502);
      }));
      final repo = CatalogRepository(api, db);
      expect(await repo.sync(), 15451);
      expect(await db.productCount(), 15451);
      expect(failedOnce, {7, 50, 120, 90, 33});
      expect(api.listPages.length, 155 + 5); // 155 страниц + по одному повтору пяти упавших
    });

    test('обрыв посреди загрузки: прочитанное остаётся, каталог помечен неполным, плашка «Загружено N из M»; следующая попытка докачивает с места', () async {
      var healthy = false;
      final inner = _server(15451);
      final api = _FakeApi((path, q) async {
        if (!healthy && path == 'api/main/products/list/' && q!['page_size'] != 1 && (q['page'] as int) >= 70) throw ApiException('Нет связи с сервером.');
        return inner(path, q);
      });
      final repo = CatalogRepository(api, db);
      await expectLater(repo.sync(), throwsA(isA<ApiException>()));
      final got = await db.productCount();
      expect(got, greaterThanOrEqualTo(6900)); // страницы 1–69 записаны сразу, а не потеряны
      expect(got, lessThan(15451));
      expect(await db.getSetting(CatalogRepository.incompleteKey), '1');
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNull); // «успехом» загрузка не считается
      await repo.publishIdle();
      expect(repo.progress.value.incomplete, isTrue);
      expect(repo.progress.value.loaded, got);
      expect(repo.progress.value.total, 15451);

      healthy = true;
      api.listPages.clear();
      expect(await repo.sync(), greaterThan(0));
      expect(api.listPages.first, greaterThan(60)); // докачка с места обрыва, а не со страницы 1
      expect(api.listPages.where((p) => p < 60), isEmpty);
      expect(api.listPages.length, lessThan(100));
      expect(await db.productCount(), 15451);
      expect(await db.getSetting(CatalogRepository.incompleteKey), '');
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNotNull);
      expect(repo.progress.value.incomplete, isFalse);
    });

    test('постоянная ошибка сервера в конце списка не убивает загрузку: 502 на каждой попытке — ошибка наверх, прочитанное сохранено', () async {
      final inner = _server(1000);
      final api = _FakeApi((path, q) async {
        if (path == 'api/main/products/list/' && q!['page_size'] != 1 && (q['page'] as int) == 6) throw ApiException('Ошибка сервера 502.', status: 502);
        return inner(path, q);
      });
      await expectLater(CatalogRepository(api, db).sync(), throwsA(isA<ApiException>()));
      expect(await db.productCount(), greaterThanOrEqualTo(500));
      expect(api.listPages.where((p) => p == 6).length, CatalogRepository.pageRetries); // страница пробовалась, а не бросалась сразу
    });
  });

  group('телефон со старой/неполной базой и расхождение count', () {
    test('на телефоне 100 товаров из 15 451 (старая версия): лента не успокаивает — число товаров сверяется и каталог догружается', () async {
      // как после обновления с версии, которая грузила одну страницу: ключи «каталог полный» есть, товаров 100
      await db.replaceProducts([for (var i = 0; i < 100; i++) ProductsCompanion.insert(id: 'p$i', name: 'Товар $i')]);
      final api = _FakeApi(_server(15451));
      final repo = CatalogRepository(api, db);
      final scope = '|${''}|${ApiClient.defaultBaseUrl}';
      await db.setSetting(CatalogRepository.scopeKey, scope);
      await db.setSetting(CatalogRepository.feedScopeKey, scope);
      await db.setSetting(CatalogRepository.feedCursorKey, 'c0');
      await db.setSetting(CatalogRepository.fullAtKey, DateTime.now().toIso8601String());
      await repo.sync();
      expect(await db.productCount(), 15451);
      expect(api.countCalls, greaterThanOrEqualTo(1));
      expect(await db.getSetting(CatalogRepository.gapKey), '0');
    });

    test('сервер считает в count товары, которых не отдаёт списком (11 из 15 451): после двух одинаковых загрузок это принимается, лишних загрузок нет', () async {
      final api = _FakeApi(_server(15451, hidden: 11));
      final repo = CatalogRepository(api, db);
      await repo.sync(); // первая: 15 440 из 15 451 — недобор, каталог пока не «полный»
      expect(await db.productCount(), 15440);
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNull);
      await repo.sync(); // вторая: тот же недобор → сервер просто считает лишнее; принимаем, запоминаем разницу
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNotNull);
      expect(await db.getSetting(CatalogRepository.gapKey), '11');
      api.listPages.clear();
      await repo.sync(); // дальше — только лента, без страниц списка
      await repo.sync();
      expect(api.listPages, isEmpty);
    });

    test('товар удалён на сервере: полная загрузка убирает его с телефона; докачка чужих не стирает', () async {
      await db.replaceProducts([ProductsCompanion.insert(id: 'gone', name: 'Удалённый на сервере')]);
      final repo = CatalogRepository(_FakeApi(_server(250)), db);
      await repo.sync();
      expect(await db.productCount(), 250);
      expect(await db.productById('gone'), isNull);
    });

    test('пустой ответ сервера не стирает телефон и не считается загрузкой', () async {
      await db.replaceProducts([for (var i = 0; i < 50; i++) ProductsCompanion.insert(id: 'p$i', name: 'Товар $i')]);
      final repo = CatalogRepository(_FakeApi(_server(0)), db);
      await repo.sync();
      expect(await db.productCount(), 50);
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNull);
    });
  });

  group('списки: фильтр остатка и «найдено» берутся из базы, а не из первых 500', () {
    test('«Нет в наличии» находит товар, стоящий за первыми 500 по алфавиту; countProducts = всей базе', () async {
      await db.replaceProducts([
        for (var i = 0; i < 1200; i++)
          ProductsCompanion.insert(
            id: 'p$i',
            name: 'Товар ${i.toString().padLeft(5, '0')}',
            quantity: Value(i == 1150 ? 0 : 10),
            searchKey: Value('товар ${i.toString().padLeft(5, '0')}'),
          ),
      ]);
      final first = await db.searchProducts('', limit: 500); // как раньше: первые 500, фильтр уже в памяти
      expect(first.where((p) => stockFilterMatch(p, StockFilter.out)), isEmpty);
      final out = await db.searchProducts('', limit: 501, extra: (p) => stockFilterSql(p, StockFilter.out));
      expect(out.map((p) => p.id), ['p1150']);
      expect(await db.countProducts('', extra: (p) => stockFilterSql(p, StockFilter.out)), 1);
      expect(await db.countProducts('', extra: (p) => stockFilterSql(p, StockFilter.inStock)), 1199);
      expect(await db.countProducts(''), 1200);
      expect(await db.countProducts('00007'), 1);
      expect(await db.countProducts('нет такого'), 0);
    });
  });

  testWidgets('плашка каталога: «Загружаем… N из M», при обрыве «Загружено N из M» и кнопка «Обновить каталог»', (t) async {
    final api = _FakeApi(_server(300));
    final repo = CatalogRepository(api, db);
    await t.runAsync(repo.restoreProgress); // состояние из настроек телефона — до показа плашки
    await t.pumpWidget(
      ProviderScope(
        overrides: [apiProvider.overrideWithValue(api), catalogRepoProvider.overrideWithValue(repo), databaseProvider.overrideWithValue(db)],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: const Scaffold(body: LinkBanner(catalog: true)),
        ),
      ),
    );
    expect(find.textContaining('Загру'), findsNothing); // каталог полный — плашки нет
    repo.progress.value = const CatalogProgress(running: true, loaded: 4200, total: 15451);
    await t.pump();
    expect(find.textContaining('4200 из 15451'), findsOneWidget);
    expect(find.textContaining('Загружаем каталог'), findsOneWidget);
    repo.progress.value = const CatalogProgress(loaded: 6900, total: 15451, incomplete: true);
    await t.pump();
    expect(find.textContaining('Загружено 6900 из 15451'), findsOneWidget);
    expect(find.text('Обновить каталог'), findsOneWidget);
    repo.progress.value = const CatalogProgress(loaded: 15451, total: 15451);
    await t.pump();
    expect(find.text('Обновить каталог'), findsNothing);
  });
}
