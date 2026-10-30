// 2026-10-29 (срочно, владелец): смена аккаунта стирает товары прошлого аккаунта; каталог грузится целиком (2500 товаров) и поиск
// идёт по всей локальной базе, а не по первым 100. Сервер не трогается — фейковый ApiClient, база в памяти.
import 'dart:convert';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';

typedef _Handler = Future<dynamic> Function(String path, Map<String, dynamic>? query);

class _FakeApi extends ApiClient {
  _FakeApi(this.handler) : super(SessionStore());
  _Handler handler;
  final calls = <String>[];

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    calls.add(path);
    ifNoneMatch = null;
    return handler(path, query);
  }
}

Map<String, dynamic> _p(String id, String name, {num qty = 5}) => {'id': id, 'name': name, 'price': 100, 'quantity': qty, 'category': 'Еда'};

Session _s(String email, String company) => Session(email: email, access: 'a', refresh: 'r', companyName: company);

/// Сервер магазина: по 100 строк на страницу, что бы ни просили (как настоящий), с count и next.
_Handler _server(List<Map<String, dynamic>> all) => (path, q) async {
  if (path == 'api/main/products/list/') {
    final page = q!['page'] as int;
    return {'count': all.length, 'next': page * 100 < all.length ? 'x?page=${page + 1}' : null, 'results': all.skip((page - 1) * 100).take(100).toList()};
  }
  if (path == 'api/main/products/changes/') return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
  throw ApiException('нет', status: 404);
};

void main() {
  late AppDatabase db;
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  group('смена аккаунта', () {
    test('вход под A → товары → выход → вход под B: в базе нет товаров A, настройки каталога сброшены, очередь A цела', () async {
      final a = _s('a@shop.kg', 'Магазин A');
      final apiA = _FakeApi(_server([for (var i = 0; i < 30; i++) _p('a$i', 'Яблоко $i')]))..attach(a);
      // вход A (как SessionController.login)
      expect(await AccountSwitch.prepare(db, OutboxMeta.ownerOf(a)), isFalse);
      await db.setSetting(AccountSwitch.lastOwnerKey, OutboxMeta.ownerOf(a));
      expect(await CatalogRepository(apiA, db).sync(), 30);
      expect(await db.productCount(), 30);
      expect(await db.getSetting(CatalogRepository.scopeKey), isNotEmpty);
      // у A есть неотправленная продажа, смена и ключ ИИ (не должны пропасть)
      await db.enqueue(
        OutboxCompanion.insert(
          id: 'sale-a',
          kind: 'sale',
          body: jsonEncode(OutboxMeta.attach({'items': [{'product': 'a1', 'qty': 2}]}, owner: OutboxMeta.ownerOf(a))),
          createdAt: DateTime.now(),
        ),
      );
      await db.setSetting('shift.id', '{"id":"7"}');
      await db.setSetting('ai.key.gemini', 'секрет');

      // выход A, вход B
      final b = _s('b@shop.kg', 'Магазин B');
      expect(await AccountSwitch.prepare(db, OutboxMeta.ownerOf(b)), isTrue);
      expect(await db.productCount(), 0, reason: 'товаров A в базе нет');
      expect(await db.getSetting(CatalogRepository.scopeKey), isNull);
      expect(await db.getSetting(CatalogRepository.feedCursorKey), isNull);
      expect(await db.getSetting(CatalogRepository.fullAtKey), isNull);
      expect(await db.getSetting('shift.id'), isNull, reason: '2026-10-29: смена прошлого аккаунта не переходит к новому');
      expect(await db.getSetting('ai.key.gemini'), isNull, reason: '2026-10-29: настройки ИИ прошлого аккаунта стёрты');
      expect(await db.pending(), isEmpty, reason: '2026-10-29: «ничего не должно остаться» — очередь прошлого аккаунта стёрта вместе со всем (окно «отправить/выгрузить/удалить» было до этого)');
      await db.setSetting(AccountSwitch.lastOwnerKey, OutboxMeta.ownerOf(b));

      // каталог B: только его товары; продажа A остаток B не меняет, даже при совпадении id
      final apiB = _FakeApi(_server([for (var i = 0; i < 10; i++) _p('a$i', 'Груша $i', qty: 5)]))..attach(b);
      expect(await CatalogRepository(apiB, db).sync(), 10);
      expect(await db.productCount(), 10);
      expect((await db.searchProducts('яблоко')), isEmpty);
      expect((await db.productById('a1'))!.quantity, 5, reason: 'продажа A остаток B не меняет');

      // вернулись A: B стирается
      expect(await AccountSwitch.prepare(db, OutboxMeta.ownerOf(a)), isTrue);
      expect(await db.productCount(), 0);
    });

    test('тот же аккаунт заново (истёк вход): товары остаются', () async {
      final a = _s('A@Shop.kg', 'Магазин A');
      final api = _FakeApi(_server([for (var i = 0; i < 5; i++) _p('a$i', 'Товар $i')]))..attach(a);
      await db.setSetting(AccountSwitch.lastOwnerKey, OutboxMeta.ownerOf(a));
      await CatalogRepository(api, db).sync();
      expect(await AccountSwitch.prepare(db, OutboxMeta.ownerOf(a)), isFalse);
      expect(await db.productCount(), 5);
    });

    test('прошлый вход неизвестен (обновление после выхода), а в базе товары другого аккаунта — стираются', () async {
      final a = _s('a@shop.kg', 'Магазин A');
      await CatalogRepository(_FakeApi(_server([_p('x', 'Чужой')]))..attach(a), db).sync();
      await db.delete(db.settings).go(); // нет session.lastOwner
      await db.setSetting(CatalogRepository.scopeKey, 'a@shop.kg|Магазин A|https://x');
      expect(await AccountSwitch.prepare(db, 'b@shop.kg'), isTrue);
      expect(await db.productCount(), 0);
    });

    test('страховка в самой загрузке: товары чужой области стираются, даже если вход не вызвал стирание, и связи нет', () async {
      final a = _s('a@shop.kg', 'Магазин A');
      await CatalogRepository(_FakeApi(_server([_p('x', 'Чужой')]))..attach(a), db).sync();
      final apiB = _FakeApi((path, q) async => throw ApiException('Нет связи с сервером.'))..attach(_s('b@shop.kg', 'Магазин B'));
      await expectLater(CatalogRepository(apiB, db).sync(), throwsA(isA<ApiException>()));
      expect(await db.productCount(), 0, reason: 'чужие товары не показываются другому аккаунту');
    });
  });

  group('каталог больше 100 товаров', () {
    test('2500 товаров: идём по next до конца, поиск находит товар с любого места, а не из первых 100', () async {
      final all = [for (var i = 0; i < 2500; i++) _p('p$i', 'Товар номер $i ${i % 2 == 0 ? 'чётный' : 'нечётный'}', qty: 3)];
      final api = _FakeApi(_server(all))..attach(_s('a@shop.kg', 'Магазин A'));
      expect(await CatalogRepository(api, db).sync(), 2500);
      expect(await db.productCount(), 2500);
      expect(api.calls.where((c) => c == 'api/main/products/list/').length, 25);
      // товары далеко за сотым
      expect((await db.searchProducts('номер 2499')).map((p) => p.id), ['p2499']);
      expect((await db.searchProducts('номер 150 ')).any((p) => p.id == 'p150'), isTrue);
      expect((await db.searchProducts('номер 1200 нечётный')), isEmpty);
      expect((await db.searchProducts('номер 1201 нечётный')).map((p) => p.id), ['p1201']);
      // как в экранах: продажа — limit 5000, склад — 20000; в списке все 2500, а не 100
      expect((await db.searchProducts('', limit: 5000)).length, 2500);
      expect((await db.searchProducts('', limit: 20000)).length, 2500);
      expect((await db.searchProducts('чётный', limit: 5000)).length, 2500, reason: '«чётный» и «нечётный» — оба содержат «чётный»');
      expect((await db.searchProducts('нечётный', limit: 5000)).length, 1250);
    });

    test('сервер отдаёт страницы по 100 без count и next: идём, пока страницы не пусты', () async {
      final all = [for (var i = 0; i < 1050; i++) _p('p$i', 'Товар $i')];
      final api = _FakeApi((path, q) async {
        if (path == 'api/main/products/list/') return all.skip(((q!['page'] as int) - 1) * 100).take(100).toList();
        if (path == 'api/main/products/changes/') return {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
        throw ApiException('нет', status: 404);
      })..attach(_s('a@shop.kg', 'Магазин A'));
      expect(await CatalogRepository(api, db).sync(), 1050);
      expect(await db.productCount(), 1050);
    });
  });
}
