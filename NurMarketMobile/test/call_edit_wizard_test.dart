// 2026-10-27 (мобилка, видеозвонок, М2): «в видеозвонке нет возможности редактировать товары» и «при добавлении товара в звонке
// сразу диктуй: 3–4 стороны фото и сканер штрихкода». Проверяем без сети, камеры и Gemini (подставные хост, камера и справочник):
//  - правка товара голосом: `ref: "camera"` («этот товар»), название / категория / единица / доп. штрихкод, отмена и журнал;
//  - удаление — только после карточки и отдельного «да» вслух, только с правом;
//  - режимы «только смотреть» / «спрашивать перед деньгами», права, ворота (числа и слова — вслух);
//  - мастер «добавить товар»: шаги спереди → сзади → сбоку → штрихкод, дубль, название из справочника, цена / закупка / остаток
//    голосом, итог и «да», создание с фото, отмена («отмени» удаляет созданное), «отмена» сбрасывает черновик, пересъёмка стороны.
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/assistant_prompt.dart';
import 'package:nurmarket/core/ai/assistant/call_focus.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/journal_undo.dart';
import 'package:nurmarket/core/ai/assistant/undo.dart' show UndoStatus;
import 'package:nurmarket/core/ai/assistant/lang.dart';
import 'package:nurmarket/core/ai/assistant/product_wizard.dart';
import 'package:nurmarket/core/ai/assistant/read_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/assistant/wizard_texts.dart';
import 'package:nurmarket/core/ai/assistant/wizard_tools.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';

import 'support/call_rig.dart' show MemoryJournal, testProduct;

class _Host implements ActionHost {
  var products = <Product>[];
  final drafts = <String, ProductDraft>{};
  final deleted = <String>[];
  final saves = <String>[];
  final barcodeOwners = <String, String>{};

  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');

  @override
  Future<List<Product>> findProducts(String query, {int limit = 30}) async {
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty);
    return [
      for (final p in products)
        if (words.every(p.searchKey.contains)) p,
    ].take(limit).toList();
  }

  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async =>
      (product: products.where((p) => p.barcode == code).firstOrNull, unavailable: false);
  @override
  Future<Product?> productById(String id) async => products.where((p) => p.id == id).firstOrNull;
  @override
  Future<String?> barcodeOwner(String productId, String code) async => barcodeOwners[code];
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
    saves.add(id);
    drafts[id] = after;
  }

  @override
  Future<void> deleteProduct(String id) async {
    deleted.add(id);
    products = [
      for (final p in products)
        if (p.id != id) p,
    ];
  }
}

class _Backend implements WizardBackend {
  final host = <String, Product>{};
  final globalNames = <String, String>{};
  String? photoName;
  final created = <WizardDraft>[];
  final taught = <(String, int)>[];
  Object? createError;
  final _Host h;
  _Backend(this.h);

  @override
  Future<({String name, String? category, String? brand})?> globalInfo(String code) async {
    final n = globalNames[code];
    return n == null ? null : (name: n, category: 'Напитки', brand: null);
  }

  @override
  Future<String?> nameFromPhoto(Uint8List jpeg) async => photoName;

  @override
  Future<String> create(WizardDraft d) async {
    if (createError != null) throw createError!;
    created.add(d);
    final id = 'new${created.length}';
    h.products = [...h.products, testProduct(id, d.name, price: d.price, qty: d.qty, unit: d.unit, barcode: d.barcode.isEmpty ? null : d.barcode)];
    return id;
  }

  @override
  Future<int> teach(Product p, List<Uint8List> shots) async {
    taught.add((p.id, shots.length));
    return shots.length;
  }
}

Session _sess({bool owner = false, String role = 'cashier', List<String> perms = const [], String id = 'u1'}) =>
    Session(email: 't', access: '', refresh: '', userId: id, userName: 'Кассир', role: role, isOwner: owner, perms: perms, cashboxId: 'cb1');

final _owner = _sess(owner: true, role: 'owner', perms: const [Session.viewProducts, Session.viewAnalytics, Session.editPrice, Session.procurement]);
final _bare = _sess(perms: const []);

Uint8List _jpg(int n) => Uint8List.fromList([0xFF, 0xD8, n, 1, 2]);
WizardStill _still(int n, {String? barcode}) => (bytes: _jpg(n), barcode: barcode, error: null);

class _Rig {
  final host = _Host();
  final tables = TableStore();
  final speech = UserSpeechLog();
  final journal = MemoryJournal();
  final focus = CallFocus();
  final stills = <WizardStill>[];
  bool cameraOn = true;
  DateTime now = DateTime(2026, 10, 27, 12);
  var _n = 0;
  late final _Backend backend;
  late final ProductWizard wizard;
  late final ActionKit kit;
  late final ToolRegistry reg;

  _Rig() {
    backend = _Backend(host);
    wizard = ProductWizard(
      host: host,
      backend: backend,
      camera: () async => stills.isEmpty ? (bytes: null, barcode: null, error: 'нет кадра') : stills.removeAt(0),
      cameraOn: () => cameraOn,
      focus: focus,
      now: () => now,
    );
    kit = ActionKit(host: host, tables: tables, focus: focus, wizard: wizard, journal: journal, now: () => now);
    reg = ToolRegistry();
    registerActionTools(reg, kit);
    registerWizardTools(reg, kit, wizard);
  }

  void user(String text) {
    now = now.add(const Duration(seconds: 2));
    speech.closeTurn();
    speech.add(text, now: now);
  }

  Future<ToolResult> call(
    String tool, [
    Map<String, dynamic> args = const {},
    Session? s,
    AssistantMode mode = AssistantMode.act,
    Future<bool> Function(AssistantTool, Map<String, dynamic>)? confirm,
  ]) => reg.execute(
    LiveToolCall('c${++_n}', tool, args),
    ToolEnv(session: s ?? _owner, speech: speech, mode: mode, lang: detectLang(speech.last?.text ?? ''), now: () => now, confirm: confirm),
  );

  Future<ToolResult> say(String phrase, String tool, [Map<String, dynamic> args = const {}, Session? s, AssistantMode mode = AssistantMode.act]) {
    user(phrase);
    return call(tool, args, s, mode);
  }

  Future<ToolResult> wiz(String phrase, Map<String, dynamic> args) => say(phrase, 'product_wizard', args);
}

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_m2_');
    Audit.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  late _Rig rig;
  setUp(() {
    rig = _Rig();
    rig.host.products = [
      testProduct('p1', 'Сникерс', price: 110, qty: 8, barcode: '4600123456781'),
      testProduct('p2', 'Кола 0,5 л', price: 60, qty: 4),
      testProduct('p3', 'Кола 1,5 л', price: 120, qty: 9),
    ];
  });

  group('«этот товар» с камеры: ref = camera', () {
    test('нет карточки камеры → понятный вопрос, ничего не меняется', () async {
      final r = await rig.say('поставь этому цену 120', 'set_price', {'ref': 'camera', 'price': 120});
      expect(r.status, ToolStatus.clarify);
      expect(r.say, contains('Что это?'));
      expect(rig.host.saves, isEmpty);
    });

    test('после карточки камеры «поставь цену 120» меняет именно этот товар, без пересказа названия; отмена и журнал', () async {
      rig.focus.setCamera(rig.host.products[0]);
      final r = await rig.say('поставь этому товару цену 120', 'set_price', {'ref': 'camera', 'price': 120});
      expect(r.status, ToolStatus.ok, reason: r.say);
      expect(rig.host.drafts['p1']!.price, 120);
      expect(rig.journal.records.single.tool, 'set_price');
      final u = await rig.say('отмени', 'undo_last');
      expect(u.status, ToolStatus.ok);
      expect(rig.host.drafts['p1']!.price, 110);
    });

    test('ref = last: товар, с которым только что работал инструмент', () async {
      await rig.say('поставь Кола 0,5 цену 70', 'set_price', {'query': 'Кола 0,5', 'price': 70});
      final r = await rig.say('поставь закупку 40', 'set_price', {'ref': 'last', 'purchase': 40});
      expect(r.status, ToolStatus.ok, reason: r.say);
      expect(rig.host.drafts['p2']!.purchase, 40);
    });

    test('product_info с ref = camera: сколько стоит «этот»', () async {
      final reads = _Reads(rig.host);
      final reg = ToolRegistry();
      registerReadTools(reg, ReadContext(reads, rig.tables, focus: rig.focus));
      rig.focus.setCamera(rig.host.products[2]);
      rig.user('сколько стоит этот');
      final r = await reg.execute(LiveToolCall('r1', 'product_info', {'ref': 'camera'}), ToolEnv(session: _owner, speech: rig.speech, now: () => rig.now));
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('Кола 1,5 л'));
    });
  });

  group('правки карточки голосом: название, категория, единица, доп. штрихкод', () {
    setUp(() => rig.focus.setCamera(rig.host.products[1]));

    test('переименование: слова нового названия должны прозвучать; отмена возвращает прежнее; журнал хранит было/стало', () async {
      final no = await rig.say('переименуй этот товар', 'product_edit', {'ref': 'camera', 'new_name': 'Кола Классик'});
      expect(no.status, ToolStatus.clarify);
      expect(rig.host.saves, isEmpty);
      final ok = await rig.say('переименуй этот товар в Кола Классик', 'product_edit', {'ref': 'camera', 'new_name': 'Кола Классик'});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      expect(ok.say, contains('Кола Классик'));
      expect(rig.host.drafts['p2']!.name, 'Кола Классик');
      final rec = rig.journal.records.single;
      expect(rec.tool, 'product_edit');
      expect(rec.undo['name_from'], 'Кола 0,5 л');
      expect(rec.undo['name_to'], 'Кола Классик');
      expect(rec.reversible, isTrue);
      final u = await rig.say('отмени', 'undo_last');
      expect(u.status, ToolStatus.ok);
      expect(rig.host.drafts['p2']!.name, 'Кола 0,5 л');
    });

    test('категория и единица одной просьбой; без слова-просьбы — not_requested', () async {
      final no = await rig.say('что это?', 'product_edit', {'ref': 'camera', 'category': 'Напитки'});
      expect(no.status, ToolStatus.notRequested);
      final ok = await rig.say('поставь категорию Напитки', 'product_edit', {'ref': 'camera', 'category': 'Напитки'});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      final unit = await rig.say('поставь единицу измерения килограмм', 'product_edit', {'ref': 'camera', 'unit': 'кг'});
      expect(unit.status, ToolStatus.ok, reason: unit.say);
      expect(rig.host.drafts['p2']!.category, 'Напитки');
      expect(rig.host.drafts['p2']!.unit, 'кг');
    });

    test('дополнительный штрихкод (упаковка 12) добавляется и снимается; занятый код не ставится; откат возвращает список', () async {
      final add = await rig.say('добавь второй штрихкод 4600123456789 в упаковке 12', 'product_edit', {
        'ref': 'camera',
        'add_alt_barcode': '4600123456789',
        'pack_qty': 12,
      });
      expect(add.status, ToolStatus.ok, reason: add.say);
      expect(rig.host.drafts['p2']!.alt, ['4600123456789|12|']);
      expect(rig.host.drafts['p2']!.allCodes, contains('4600123456789'));
      final u = await rig.say('отмени', 'undo_last');
      expect(u.status, ToolStatus.ok);
      expect(rig.host.drafts['p2']!.alt, isEmpty);
      rig.host.barcodeOwners['4600123456789'] = 'Сникерс';
      final taken = await rig.say('добавь штрихкод 4600123456789', 'product_edit', {'ref': 'camera', 'add_alt_barcode': '4600123456789'});
      expect(taken.say, contains('Сникерс'));
      rig.host.barcodeOwners.clear();
      await rig.say('добавь штрихкод 4600123456789', 'product_edit', {'ref': 'camera', 'add_alt_barcode': '4600123456789'});
      final rm = await rig.say('убери штрихкод 4600123456789', 'product_edit', {'ref': 'camera', 'remove_alt_barcode': '4600123456789'});
      expect(rm.status, ToolStatus.ok, reason: rm.say);
      expect(rig.host.drafts['p2']!.alt, isEmpty);
      rig.now = rig.now.add(const Duration(seconds: 30)); // повтор той же команды в первые 10 с — «уже сделано»
      final none = await rig.say('убери штрихкод 4600123456789', 'product_edit', {'ref': 'camera', 'remove_alt_barcode': '4600123456789'});
      expect(none.status, ToolStatus.notFound);
    });

    test('«Действия ИИ» после звонка: правка названия, категории и доп. штрихкода отменяется из журнала; руками поправили — не затирается', () async {
      await rig.say('переименуй этот товар в Кола Классик', 'product_edit', {'ref': 'camera', 'new_name': 'Кола Классик'});
      await rig.say('поставь категорию Напитки', 'product_edit', {'ref': 'camera', 'category': 'Напитки'});
      await rig.say('добавь штрихкод 4600123456789', 'product_edit', {'ref': 'camera', 'add_alt_barcode': '4600123456789'});
      final undoer = JournalUndoer(rig.host);
      final byKey = {
        for (final r in rig.journal.records)
          if (r.undo['name_to'] != null) 'name': r,
        for (final r in rig.journal.records)
          if (r.undo['category_to'] != null) 'cat': r,
        for (final r in rig.journal.records)
          if (r.undo['alt_to'] != null) 'alt': r,
      };
      expect(byKey.keys, {'name', 'cat', 'alt'});
      for (final r in byKey.values) {
        expect(JournalUndo.classify(r, _owner, const {}).kind, JournalUndoKind.available);
      }
      // последнее ИИ-изменение (доп. штрихкод) откатывается из журнала
      final altOut = await undoer.undo(byKey['alt']!, _owner);
      expect(altOut.status, UndoStatus.ok);
      expect(rig.host.drafts['p2']!.alt, isEmpty);
      expect(rig.host.drafts['p2']!.name, 'Кола Классик');
      // имя потом поправили руками — откат названия не затирает чужую правку
      rig.host.drafts['p2'] = rig.host.drafts['p2']!.copyWith(name: 'Моё имя');
      final nameOut = await undoer.undo(byKey['name']!, _owner);
      expect(nameOut.status, UndoStatus.conflict);
      expect(rig.host.drafts['p2']!.name, 'Моё имя');
    });

    test('если уже так — менять нечего; если после ИИ товар поправили руками — откат не затирает (конфликт)', () async {
      final same = await rig.say('переименуй этот товар в Кола 0,5 л', 'product_edit', {'ref': 'camera', 'new_name': 'Кола 0,5 л'});
      expect(same.status, ToolStatus.ok);
      expect(same.say, contains('менять нечего'));
      expect(rig.host.saves, isEmpty);
      await rig.say('переименуй этот товар в Кола Классик', 'product_edit', {'ref': 'camera', 'new_name': 'Кола Классик'});
      rig.host.drafts['p2'] = rig.host.drafts['p2']!.copyWith(name: 'Другое имя'); // правка вручную
      final u = await rig.say('отмени', 'undo_last');
      expect(u.say, isNot(contains('Отменила: ')));
      expect(rig.host.drafts['p2']!.name, 'Другое имя');
    });

    test('права: без «Склад» инструмента нет и вызов — denied; «только смотреть» — view_only; режим «спрашивать» правку карточки не задерживает', () async {
      expect(rig.reg.forSession(_bare).map((t) => t.name), isNot(contains('product_edit')));
      expect(rig.reg.forSession(_bare).map((t) => t.name), isNot(contains('product_wizard')));
      final denied = await rig.say('переименуй в Кола Классик', 'product_edit', {'ref': 'camera', 'new_name': 'Кола Классик'}, _bare);
      expect(denied.status, ToolStatus.denied);
      final view = await rig.say(
        'переименуй этот товар в Кола Классик',
        'product_edit',
        {'ref': 'camera', 'new_name': 'Кола Классик'},
        _owner,
        AssistantMode.viewOnly,
      );
      expect(view.status, ToolStatus.viewOnly);
      expect(rig.host.saves, isEmpty);
      final ask = await rig.say(
        'переименуй этот товар в Кола Классик',
        'product_edit',
        {'ref': 'camera', 'new_name': 'Кола Классик'},
        _owner,
        AssistantMode.askMoney,
      );
      expect(ask.status, ToolStatus.ok);
      // цена в режиме «спрашивать» — через карточку «Сделать»: без согласия не меняется
      final price = await rig.say('поставь цену 99', 'set_price', {'ref': 'camera', 'price': 99}, _owner, AssistantMode.askMoney);
      expect(price.status, ToolStatus.clarify);
      expect(rig.host.drafts['p2']!.price, 60);
    });
  });

  group('удаление товара голосом: карточка, потом «да»', () {
    test('«удали товар» один товар не удаляет: показывает карточку и ждёт «да»; «да, удали» — удаляет, пишет в журнал', () async {
      final ask = await rig.say('удали товар Кола 0,5', 'delete_product', {'query': 'Кола 0,5'});
      expect(ask.status, ToolStatus.clarify);
      expect(ask.say, contains('Скажите «да, удалить»'));
      expect(rig.host.deleted, isEmpty);
      expect(rig.tables.current!.title, contains('Кола 0,5'));

      final same = await rig.call('delete_product', {'query': 'Кола 0,5', 'confirm': true});
      expect(same.status, ToolStatus.clarify, reason: 'в той же реплике подтверждения нет');
      expect(rig.host.deleted, isEmpty);

      // «нет, не удаляй» подтверждением не считается, даже если в речи есть слово «удал»
      final no = await rig.say('нет, не удаляй', 'delete_product', {'query': 'Кола 0,5', 'confirm': true});
      expect(no.status, ToolStatus.clarify);
      expect(no.say, contains('не удаляю'));
      expect(rig.host.deleted, isEmpty);

      // 2026-10-27 (ревью М-8): «нет» снимает ожидание — позднее случайное «да» без новой карточки не удаляет
      final late = await rig.say('да, удали', 'delete_product', {'query': 'Кола 0,5', 'confirm': true});
      expect(late.status, ToolStatus.clarify);
      expect(rig.host.deleted, isEmpty);
      await rig.say('удали товар Кола 0,5', 'delete_product', {'query': 'Кола 0,5'});
      final yes = await rig.say('да, удали', 'delete_product', {'query': 'Кола 0,5', 'confirm': true});
      expect(yes.status, ToolStatus.ok, reason: yes.say);
      expect(rig.host.deleted, ['p2']);
      final rec = rig.journal.records.single;
      expect(rec.tool, 'delete_product');
      expect(rec.undo['name'], 'Кола 0,5 л');
      expect(rec.reversible, isFalse);
    });

    test('подтверждение без показа карточки или про другой товар — снова карточка; товар с камеры удаляется по ref', () async {
      final blind = await rig.say('да, удали', 'delete_product', {'query': 'Кола 0,5', 'confirm': true});
      expect(blind.status, ToolStatus.clarify, reason: 'карточку ещё не показывали — «да» удалением не считается');
      expect(rig.host.deleted, isEmpty);
      rig.focus.setCamera(rig.host.products[2]);
      final ask = await rig.say('удали этот товар', 'delete_product', {'ref': 'camera'});
      expect(ask.status, ToolStatus.clarify);
      final other = await rig.say('да, удали', 'delete_product', {'query': 'Сникерс', 'confirm': true});
      expect(other.status, ToolStatus.clarify, reason: 'ждали «да» про Колу 1,5, а не про Сникерс');
      expect(rig.host.deleted, isEmpty);
      final ask2 = await rig.say('удали этот товар', 'delete_product', {'ref': 'camera'});
      expect(ask2.status, ToolStatus.clarify);
      final yes = await rig.say('да', 'delete_product', {'ref': 'camera', 'confirm': true});
      expect(yes.status, ToolStatus.ok, reason: yes.say);
      expect(rig.host.deleted, ['p3']);
      expect(rig.focus.camera, isNull);
    });

    test('только с правом: кассир не видит инструмента; в «только смотреть» — view_only', () async {
      expect(rig.reg.forSession(_sess(perms: const [Session.viewProducts])).map((t) => t.name), isNot(contains('delete_product')));
      final denied = await rig.say('удали товар Кола 0,5', 'delete_product', {'query': 'Кола 0,5'}, _sess(perms: const [Session.viewProducts]));
      expect(denied.status, ToolStatus.denied);
      final view = await rig.say('удали товар Кола 0,5', 'delete_product', {'query': 'Кола 0,5'}, _owner, AssistantMode.viewOnly);
      expect(view.status, ToolStatus.viewOnly);
      expect(rig.host.deleted, isEmpty);
    });
  });

  group('мастер «добавить товар»: спереди → сзади → сбоку → штрихкод', () {
    Future<void> shoot(String phrase, WizardStill s, [Map<String, dynamic> args = const {}]) async {
      rig.stills.add(s);
      final r = await rig.wiz(phrase, {'op': 'photo', ...args});
      expect(r.status, ToolStatus.ok, reason: r.say);
    }

    test(
      'полный сценарий на моках: 3 кадра + штрихкод, название из справочника, цена / закупка / остаток вслух, итог, «да», создание с фото, отмена удаляет',
      () async {
        rig.backend.globalNames['4870001571030'] = 'ASU Негаз 1 л клуб-киви';
        final start = await rig.wiz('добавь новый товар', {'op': 'start'});
        expect(start.status, ToolStatus.ok);
        expect(start.say, allOf(contains('спереди'), contains('сзади'), contains('сбоку'), contains('штрихкод')));
        expect(rig.wizard.active, isTrue);

        // модель сама, в той же реплике, где просили «добавить товар», кадр не снимает
        rig.stills.add(_still(1));
        final early = await rig.call('product_wizard', {'op': 'photo'});
        expect(early.status, ToolStatus.notRequested);
        expect(rig.stills, hasLength(1));
        rig.stills.clear();
        rig.wizard.reset();
        final start2 = await rig.wiz('добавь новый товар, готово', {'op': 'start'});
        expect(start2.status, ToolStatus.ok);
        rig.stills.add(_still(1));
        final early2 = await rig.call('product_wizard', {'op': 'photo'});
        expect(early2.status, ToolStatus.clarify, reason: 'слово «готово» было ДО старта — кадр ждёт новой реплики');
        expect(early2.say, contains('готово'));
        expect(rig.stills, hasLength(1));
        rig.stills.clear();

        await shoot('готово', _still(1));
        expect(rig.wizard.shots.keys, [WizardSide.front]);
        await shoot('готово, теперь сзади', _still(2));
        final side = await (() async {
          rig.stills.add(_still(3));
          return rig.wiz('сбоку', {'op': 'photo'});
        })();
        expect(side.say, allOf(contains('Сбоку снято'), contains('штрихкод'), contains('ещё')));
        expect(rig.wizard.shotCount, 3);

        rig.stills.add(_still(4, barcode: '4870001571030'));
        final code = await rig.wiz('вот штрихкод', {'op': 'photo', 'side': 'barcode'});
        expect(code.say, allOf(contains('4870001571030'), contains('ASU Негаз 1 л клуб-киви'), contains('цену продажи')));
        expect(rig.wizard.name, 'ASU Негаз 1 л клуб-киви');

        // цена — только вслух: ИИ передал 85, а сказано без числа → переспрос
        final blind = await rig.wiz('цена', {'op': 'set', 'price': 85});
        expect(blind.status, ToolStatus.clarify);
        expect(rig.wizard.price, isNull);
        final price = await rig.wiz('цена восемьдесят пять', {'op': 'set', 'price': 85});
        expect(price.status, ToolStatus.ok, reason: price.say);
        expect(price.say, allOf(contains('цена 85 сом'), contains('закупочную')));

        final more = await rig.wiz('закупка 60, остаток 12, да', {'op': 'set', 'purchase': 60, 'qty': 12});
        expect(
          more.say,
          allOf(
            contains('Создать товар «ASU Негаз 1 л клуб-киви»'),
            contains('цена 85 сом'),
            contains('закупка 60 сом'),
            contains('остаток 12'),
            contains('штрихкод 4870001571030'),
            contains('фото: 3'),
          ),
        );

        // «да» в ТОЙ ЖЕ реплике, где назвали числа, создание не запускает
        final same = await rig.call('wizard_create');
        expect(same.status, ToolStatus.clarify);
        expect(rig.backend.created, isEmpty);

        rig.user('нет, не создавай, подожди');
        final no = await rig.call('wizard_create');
        expect(no.status, ToolStatus.clarify);
        expect(rig.backend.created, isEmpty);
        rig.user('да');
        final done = await rig.call('wizard_create');
        expect(done.status, ToolStatus.ok, reason: done.say);
        expect(done.undo, isTrue);
        expect(rig.backend.created, hasLength(1));
        final d = rig.backend.created.single;
        expect(d.name, 'ASU Негаз 1 л клуб-киви');
        expect([d.price, d.purchase, d.qty], [85, 60, 12]);
        expect(d.barcode, '4870001571030');
        expect(d.shots, hasLength(3), reason: 'спереди (главное), сзади, сбоку; снимок штрихкода в карточку не идёт');
        expect(d.shots.first, _jpg(1));
        expect(rig.wizard.active, isFalse);
        expect(rig.journal.records.last.tool, 'wizard_create');
        expect(rig.journal.records.last.reversible, isTrue);
        expect(rig.journal.records.last.undo['id'], 'new1');
        expect(jsonOf(rig.journal.records.last.undo), isNot(contains('255, 216')), reason: 'кадры в журнал не попадают');

        // «отмени» удаляет созданный товар (он не менялся)
        final u = await rig.say('отмени', 'undo_last');
        expect(u.status, ToolStatus.ok, reason: u.say);
        expect(rig.host.deleted, ['new1']);
      },
    );

    test('отмена не удаляет, если созданный товар успели продать (остаток изменился)', () async {
      rig.backend.photoName = 'Вода Бон Аква';
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      await rig.wiz('хватит', {'op': 'set', 'finish': true});
      await rig.wiz('без штрихкода', {'op': 'set', 'no_barcode': true});
      await rig.wiz('цена 40 остаток 5', {'op': 'set', 'price': 40, 'qty': 5});
      rig.user('да, создавай');
      final done = await rig.call('wizard_create');
      expect(done.status, ToolStatus.ok, reason: done.say);
      rig.host.products = [for (final p in rig.host.products) p.id == 'new1' ? testProduct('new1', p.name, price: p.price, qty: 3) : p];
      final u = await rig.say('отмени', 'undo_last');
      expect(rig.host.deleted, isEmpty);
      expect(u.say, contains('уже менялся'));
    });

    test('повторное «сзади» переснимает сторону; кадр без штрихкода — подсказка, после второй неудачи — «скажите цифры или без штрихкода»', () async {
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      await shoot('сзади', _still(2));
      final again = await (() async {
        rig.stills.add(_still(9));
        return rig.wiz('сзади ещё раз', {'op': 'photo', 'side': 'back'});
      })();
      expect(again.say, startsWith('Переснято'));
      expect(rig.wizard.shots[WizardSide.back], _jpg(9));
      expect(rig.wizard.shotCount, 2);

      rig.stills.add(_still(5));
      final f1 = await rig.wiz('штрихкод', {'op': 'photo', 'side': 'barcode'});
      expect(f1.say, contains('не прочитался'));
      rig.stills.add(_still(6));
      final f2 = await rig.wiz('штрихкод', {'op': 'photo', 'side': 'barcode'});
      expect(f2.say, allOf(contains('цифры штрихкода'), contains('без штрихкода')));
    });

    test('штрихкод уже есть в базе: «такой товар уже есть», создать с ним нельзя; «обучи камеру» — снимки уходят в эталоны того товара', () async {
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      await shoot('сзади', _still(2));
      rig.stills.add(_still(3, barcode: '4600123456781'));
      final dup = await rig.wiz('вот штрихкод', {'op': 'photo', 'side': 'barcode'});
      expect(dup.say, allOf(contains('Такой товар уже есть: «Сникерс»'), contains('110'), contains('остаток 8')));
      expect(rig.wizard.duplicate?.id, 'p1');
      expect(rig.focus.last?.id, 'p1', reason: '«обнови остаток» дальше идёт по ref = last');

      final blocked = await rig.wiz('цена 50 создавай', {'op': 'set', 'name': 'Сникерс', 'price': 50});
      expect(blocked.status, ToolStatus.clarify); // имя «Сникерс» в речи не звучало
      final taught = await rig.wiz('обучи камеру этими снимками', {'op': 'teach'});
      expect(taught.status, ToolStatus.ok, reason: taught.say);
      expect(rig.backend.taught, [('p1', 2)], reason: 'спереди и сзади; снимок штрихкода эталоном не идёт');
      expect(rig.wizard.active, isFalse);
      expect(rig.journal.records.last.tool, 'product_wizard');
    });

    test('дубль: «без штрихкода» снимает запрет, товар создаётся без кода', () async {
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1, barcode: '4600123456781'));
      expect(rig.wizard.duplicate?.id, 'p1');
      rig.backend.photoName = 'Сникерс Макс';
      final no = await rig.wiz('без штрихкода', {'op': 'set', 'no_barcode': true});
      expect(no.status, ToolStatus.ok, reason: no.say);
      expect(rig.wizard.duplicate, isNull);
      expect(rig.wizard.barcode, isEmpty);
    });

    test('«отмена» вслух: сбрасывает черновик (и как product_wizard cancel, и если модель вызвала undo_last); созданное ранее не трогает', () async {
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      final c = await rig.wiz('отмена', {'op': 'cancel'});
      expect(c.status, ToolStatus.ok);
      expect(rig.wizard.active, isFalse);
      expect(rig.wizard.shots, isEmpty);

      await rig.wiz('добавь товар', {'op': 'start'});
      final u = await rig.say('отмена', 'undo_last');
      expect(u.status, ToolStatus.ok);
      expect(u.say, contains('черновик товара сброшен'));
      expect(rig.wizard.active, isFalse);
      expect(rig.host.deleted, isEmpty);
    });

    test('нет камеры → «включите камеру»; «только смотреть» → view_only; без права — denied; ошибка кадра — понятный текст', () async {
      rig.cameraOn = false;
      final noCam = await rig.wiz('добавь товар', {'op': 'start'});
      expect(noCam.status, ToolStatus.clarify);
      expect(noCam.say, contains('камеру'));
      rig.cameraOn = true;
      final view = await rig.say('добавь товар', 'product_wizard', {'op': 'start'}, _owner, AssistantMode.viewOnly);
      expect(view.status, ToolStatus.viewOnly);
      final denied = await rig.say('добавь товар', 'product_wizard', {'op': 'start'}, _bare);
      expect(denied.status, ToolStatus.denied);
      await rig.wiz('добавь товар', {'op': 'start'});
      rig.user('готово');
      final bad = await rig.call('product_wizard', {'op': 'photo'});
      expect(bad.status, ToolStatus.clarify);
      expect(bad.say, contains('нет кадра'));
      expect(rig.wizard.shots, isEmpty);
    });

    test('режим «спрашивать перед деньгами»: создание без согласия на карточке не выполняется', () async {
      rig.backend.photoName = 'Вода';
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      await rig.wiz('хватит', {'op': 'set', 'finish': true});
      await rig.wiz('без штрихкода', {'op': 'set', 'no_barcode': true});
      await rig.wiz('цена 40', {'op': 'set', 'price': 40});
      rig.user('да');
      final refused = await rig.call('wizard_create', const {}, _owner, AssistantMode.askMoney, (t, a) async => false);
      expect(refused.status, ToolStatus.clarify);
      expect(rig.backend.created, isEmpty);
      final accepted = await rig.call('wizard_create', const {}, _owner, AssistantMode.askMoney, (t, a) async => true);
      expect(accepted.status, ToolStatus.ok, reason: accepted.say);
      expect(rig.backend.created, hasLength(1));
    });

    test('обрыв связи при создании: честно «не знаю, создался ли», черновик остаётся, второй раз не создаём вслепую', () async {
      rig.backend.photoName = 'Вода';
      rig.backend.createError = ApiException('нет связи');
      await rig.wiz('добавь товар', {'op': 'start'});
      await shoot('готово', _still(1));
      await rig.wiz('хватит', {'op': 'set', 'finish': true});
      await rig.wiz('без штрихкода', {'op': 'set', 'no_barcode': true});
      await rig.wiz('цена 40', {'op': 'set', 'price': 40});
      rig.user('да');
      final r = await rig.call('wizard_create');
      expect(r.status, ToolStatus.offline);
      expect(r.say, contains('не знаю, создался ли'));
      expect(rig.wizard.active, isTrue);
      expect(rig.journal.records, isEmpty);
    });

    test('кнопки экрана ведут тот же мастер: «Снять» без слов, «Создать» без «да», но с журналом и отменой', () async {
      rig.backend.photoName = 'Вода';
      expect(rig.wizard.start('ru').ok, isTrue);
      rig.stills.add(_still(1));
      final shot = await rig.wizard.shoot(WizardSide.front, 'ru', viaUi: true);
      expect(shot.ok, isTrue);
      expect(rig.wizard.hasFront, isTrue);
      await rig.wizard.setFields('ru', finish: true, noBarcode: true);
      await rig.wizard.setFields('ru', price: 40);
      expect(rig.wizard.ready, isTrue);
      final out = await rig.wizard.create('ru', viaUi: true);
      expect(out.ok, isTrue, reason: out.say);
      expect(rig.backend.created.single.price, 40);
    });
  });

  group('подсказка модели и тексты', () {
    test('в подсказке видеозвонка правки по просьбе разрешены (ref = camera), мастер описан; старого запрета нет', () {
      final p = buildAssistantInstruction(shop: 'NBS', role: 'owner', mode: AssistantMode.act, storeData: '', today: '27 октября', video: true);
      expect(p, contains('ref = "camera"'));
      expect(p, contains('product_wizard'));
      expect(p, contains('wizard_create'));
      expect(p, isNot(contains('Ты ничего не проводишь и не меняешь по картинке')));
      expect(p, contains('не говори «провёл», «записал», «добавил»'));
      final noVideo = buildAssistantInstruction(shop: 'NBS', role: 'owner', mode: AssistantMode.act, storeData: '', today: '27 октября');
      expect(noVideo, isNot(contains('product_wizard')));
    });

    test('все тексты мастера есть на пяти языках и без потерянных {скобок}', () {
      final all = wizardTextsForTest;
      for (final e in all.entries) {
        expect(e.value, hasLength(5), reason: e.key);
        expect(e.value.every((t) => t.trim().isNotEmpty), isTrue, reason: e.key);
        final ph = RegExp(r'\{(\w+)\}');
        final base = {for (final m in ph.allMatches(e.value[0])) m.group(1)};
        for (final (i, t) in e.value.indexed) {
          expect({for (final m in ph.allMatches(t)) m.group(1)}, base, reason: '${e.key} / язык $i');
        }
      }
    });
  });
}

String jsonOf(Object? o) => '$o';

class _Reads implements ReadSource {
  final _Host host;
  _Reads(this.host);
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');
  @override
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000}) => host.findProducts(query, limit: limit);
  @override
  Future<double?> purchasePrice(String productId) async => null;
}
