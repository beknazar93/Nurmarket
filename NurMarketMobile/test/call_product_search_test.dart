// 2026-10-27 (мобилка, видеозвонок, М2): «видео вызов не находит товар на складе». Поиск товара в звонке на каталоге магазина NBS
// (343 товара, test/fixtures/nbs_catalog.json) поверх общего матчера: «Асу», «асу клубника», «винстон», «адыгене литр»,
// «пепси полтора литра» раньше не находили ничего (все слова запроса должны были быть подстроками карточки). Сети и Gemini нет.
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:drift/drift.dart' show Value, driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/call_focus.dart';
import 'package:nurmarket/core/ai/assistant/call_product_search.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/lang.dart';
import 'package:nurmarket/core/ai/assistant/product_wizard.dart';
import 'package:nurmarket/core/ai/assistant/read_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';

import 'support/call_rig.dart' show MemoryJournal;

String _key(String name, String barcode, String sku, String category) => '$name $sku $barcode $category'.toLowerCase();

/// Поиск поверх базы в памяти: рабочий путь звонка (`searchForCall`) и таблица товаров, как у настоящего каталога.
class _Host implements ActionHost, ScoredProductSearch {
  final AppDatabase db;
  final drafts = <String, ProductDraft>{};
  final saved = <String>[];
  _Host(this.db);

  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');

  @override
  Future<List<Product>> findProducts(String query, {int limit = 30}) async => [for (final h in await searchForCall(db, query, top: limit)) h.product];
  @override
  Future<List<ProductHit>> findScored(String query, {int top = 5}) => searchForCall(db, query, top: top);
  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async => (product: (await db.productByScan(code))?.product, unavailable: false);
  @override
  Future<Product?> productById(String id) => db.productById(id);
  @override
  Future<String?> barcodeOwner(String productId, String code) async => null;
  @override
  Future<ProductDraft> loadDraft(Product p) async =>
      drafts[p.id] ??
      ProductDraft(
        name: p.name,
        unit: p.unit,
        category: p.category,
        barcode: p.barcode ?? '',
        alt: const [],
        price: p.price,
        purchase: 50,
        wholesale: 0,
        minQty: null,
      );
  @override
  Future<void> saveDraft(String id, ProductDraft before, ProductDraft after) async {
    drafts[id] = after;
    saved.add(id);
  }
}

class _Reads implements ReadSource, ScoredProductSearch {
  final AppDatabase db;
  _Reads(this.db);
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');
  @override
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000}) async {
    if (query.trim().isEmpty) return db.searchProducts(query, category: category, limit: limit);
    return [for (final h in await searchForCall(db, query, top: limit < 30 ? limit : 30)) h.product];
  }

  @override
  Future<List<ProductHit>> findScored(String query, {int top = 5}) => searchForCall(db, query, top: top);
  @override
  Future<double?> purchasePrice(String productId) async => 60;
}

final _owner = Session(
  email: 't',
  access: '',
  refresh: '',
  userId: 'u1',
  userName: 'Владелец',
  role: 'owner',
  isOwner: true,
  perms: const [Session.viewProducts, Session.viewAnalytics, Session.editPrice, Session.procurement],
  cashboxId: 'cb1',
);

void main() {
  late Directory dir;
  late AppDatabase db;
  late _Host host;
  late ToolRegistry reg;
  late TableStore tables;
  late UserSpeechLog speech;
  late CallFocus focus;
  var now = DateTime(2026, 10, 27, 12);
  var n = 0;

  setUpAll(() async {
    dir = Directory.systemTemp.createTempSync('nm_callsearch_');
    Audit.testDirectory = dir;
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    db = AppDatabase(NativeDatabase.memory());
    final rows = (jsonDecode(File('test/fixtures/nbs_catalog.json').readAsStringSync()) as List).cast<Map<String, dynamic>>();
    await db.replaceProducts([
      for (final (i, r) in rows.indexed)
        ProductsCompanion.insert(
          id: r['id'] as String,
          name: r['name'] as String,
          barcode: Value(r['barcode'] as String),
          sku: Value(r['sku'] as String),
          category: Value(r['category'] as String),
          price: Value(100.0 + i),
          quantity: Value(3.0 + (i % 9)),
          searchKey: Value(_key(r['name'] as String, r['barcode'] as String, r['sku'] as String, r['category'] as String)),
        ),
    ]);
    await db.setSetting('catalog.syncedAt', '2026-10-27T06:17:00Z');
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  setUp(() {
    host = _Host(db);
    tables = TableStore();
    speech = UserSpeechLog();
    focus = CallFocus();
    reg = ToolRegistry();
    registerReadTools(reg, ReadContext(_Reads(db), tables, focus: focus));
    registerActionTools(reg, ActionKit(host: host, tables: tables, focus: focus, journal: MemoryJournal(), now: () => now));
  });

  void user(String text) {
    now = now.add(const Duration(seconds: 2));
    speech.closeTurn();
    speech.add(text, now: now);
  }

  Future<ToolResult> call(String tool, Map<String, dynamic> args, [Session? s]) => reg.execute(
    LiveToolCall('c${++n}', tool, args),
    ToolEnv(session: s ?? _owner, speech: speech, lang: detectLang(speech.last?.text ?? ''), now: () => now),
  );

  Future<ToolResult> say(String phrase, String tool, Map<String, dynamic> args, [Session? s]) {
    user(phrase);
    return call(tool, args, s);
  }

  Future<String> nameOf(String id) async => (await db.productById(id))!.name;

  group('поиск товара в звонке: было «ничего», стало найдено', () {
    test('прежний поиск (все слова — подстроки) на этих запросах не находил НИЧЕГО — это и была жалоба', () async {
      for (final q in ['асу клубника', 'адыгене литр', 'пепси полтора литра']) {
        expect(await db.searchProducts(q, limit: 30), isEmpty, reason: q);
      }
      // 2026-10-29 (аудит мобилки, п. 10): общий поиск теперь знает транслит как касса — «винстон» находит Winston и без матчера звонка
      expect((await db.searchProducts('винстон', limit: 30)).map((p) => p.name.toLowerCase()), everyElement(contains('winston')));
    });

    test('«асу»: все варианты ASU; «асу клубника» → клуб-киви; «винстон» → Winston; «адыгене литр» → Адыгене 1л; «пепси полтора литра» → пепси 1.5', () async {
      final asu = await searchForCall(db, 'асу');
      expect(asu, isNotEmpty);
      expect(asu.every((h) => h.product.name.toLowerCase().contains('asu')), isTrue, reason: asu.join(', '));
      expect(asu.length, greaterThanOrEqualTo(3));
      expect(confidentHit(asu), isNull, reason: 'несколько вариантов ASU — не угадываем');

      final klub = await searchForCall(db, 'асу клубника');
      expect(klub.first.product.name, contains('клуб'));
      final win = await searchForCall(db, 'винстон');
      expect(win.first.product.name.toLowerCase(), contains('winston'));
      final ady = await searchForCall(db, 'адыгене литр');
      expect(ady.first.product.name, contains('Адыгене'));
      expect(confidentHit(ady)?.product.name, contains('Адыгене'), reason: 'единственный Адыгене 1 л — находим без вопроса');
      final pepsi = await searchForCall(db, 'пепси полтора литра');
      expect(pepsi.first.product.name.toLowerCase(), contains('пепси'));
      expect(pepsi.first.product.name, contains('1.5'));
    });

    test('штрихкод и артикул — точное совпадение первым; слова, которых нет в каталоге, — пусто', () async {
      final any = (await db.searchProducts('', limit: 3)).first;
      final byCode = await searchForCall(db, any.barcode!);
      expect(byCode.first.product.id, any.id);
      expect(byCode.first.score, 1.0);
      expect(await searchForCall(db, 'ёжикпрограмма'), isEmpty);
      expect(await searchForCall(db, ''), isEmpty);
    });

    test('product_info: «адыгене литр» → найден, в ответе цена, остаток и штрихкод', () async {
      user('сколько стоит адыгене литр');
      final r = await call('product_info', {'query': 'адыгене литр'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('Адыгене'));
      expect(r.say, contains('остаток'));
      final h = (r.data!['highlights'] as List).join(' ');
      expect(h, contains('price'));
      expect(h, contains('stock'));
      expect(h, contains('barcode 4700000310326'));
      expect(focus.last?.name, contains('Адыгене'));
    });

    test('product_info: «асу» → вопрос по кандидатам с ценой и остатком (ambiguous), pick выбирает', () async {
      user('сколько стоит асу');
      final r = await call('product_info', {'query': 'асу'});
      expect(r.status, ToolStatus.ambiguous);
      expect(r.options!.length, greaterThanOrEqualTo(2));
      expect(r.say, contains('остаток'));
      expect(r.options!.first, allOf(containsPair('score', isA<double>()), contains('barcode'), contains('stock'), contains('price'), contains('title')));
      final pick = await call('product_info', {'query': 'асу', 'pick': 2});
      expect(pick.status, ToolStatus.ok);
    });

    test('product_info: «винстон» (раньше «ничего») → найден; «спрайт» — два близких варианта, переспрашивает, а не угадывает', () async {
      user('почём винстон');
      final r = await call('product_info', {'query': 'винстон'});
      expect(r.status, ToolStatus.ok);
      expect(r.say.toLowerCase(), contains('winston'));
      user('сколько стоит спрайт');
      final sp = await call('product_info', {'query': 'спрайт'});
      expect(sp.status, ToolStatus.ambiguous);
      expect(sp.options!.length, 2);
      final yes = await call('product_info', {'query': 'спрайт', 'pick': 1});
      expect(yes.status, ToolStatus.ok);
    });

    test('stock_list с запросом: «асу» → список ASU по оценке', () async {
      user('что с остатками асу');
      final r = await call('stock_list', {'query': 'асу'});
      expect(r.status, ToolStatus.ok);
      final t = tables.current!;
      expect(t.rows.length, greaterThanOrEqualTo(3));
      expect(t.cellText(0, 0, revealNames: false).toLowerCase(), contains('asu'));
    });

    test('цена по сказанному названию: «поставь адыгене литр цену 85» меняет именно «Адыгене 1л»; «асу» — вопрос, ничего не меняется', () async {
      final ok = await say('поставь адыгене литр цену 85', 'set_price', {'query': 'адыгене литр', 'price': 85});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      expect(host.saved, hasLength(1));
      expect(await nameOf(host.saved.single), contains('Адыгене'));
      host.saved.clear();
      final amb = await say('поставь асу цену 90', 'set_price', {'query': 'асу', 'price': 90});
      expect(amb.status, ToolStatus.ambiguous);
      expect(amb.options!.length, greaterThanOrEqualTo(2));
      expect(host.saved, isEmpty);
    });
  });

  group('мастер «добавить товар»: подсказка о похожем товаре по названию (матчер по всему каталогу)', () {
    test('название определилось — «похожий товар уже есть»; создавать это не запрещает', () async {
      final backend = _WizBackend({'4870099900011': 'Адыгене 1 л'});
      final w = ProductWizard(
        host: host,
        backend: backend,
        camera: () async => (bytes: Uint8List.fromList([1, 2, 3]), barcode: '4870099900011', error: null),
        cameraOn: () => true,
        focus: focus,
      );
      expect(w.start('ru').ok, isTrue);
      await w.shoot(WizardSide.front, 'ru', viaUi: true);
      await w.shoot(WizardSide.back, 'ru', viaUi: true);
      final out = await w.shoot(WizardSide.side, 'ru', viaUi: true);
      expect(out.say, contains('Адыгене 1 л'));
      expect(out.say, contains('похожий товар уже есть'));
      expect(out.say, contains('«Адыгене 1л»'));
      expect(w.similar?.name, 'Адыгене 1л');
      expect(w.duplicate, isNull, reason: 'штрихкод другой — это не дубль по коду');
      expect(focus.last?.name, 'Адыгене 1л');
    });
  });
}

class _WizBackend implements WizardBackend {
  final Map<String, String> names;
  _WizBackend(this.names);
  @override
  Future<({String name, String? category, String? brand})?> globalInfo(String code) async =>
      names[code] == null ? null : (name: names[code]!, category: null, brand: null);
  @override
  Future<String?> nameFromPhoto(Uint8List jpeg) async => null;
  @override
  Future<String> create(WizardDraft d) async => 'x';
  @override
  Future<int> teach(Product p, List<Uint8List> shots) async => shots.length;
}
