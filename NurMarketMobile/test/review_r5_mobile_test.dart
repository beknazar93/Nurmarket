// 2026-10-27 (поток Р-5, мобилка по ревью денежных правок «Ревью денежных правок 2026-10-27»): М-1 (голосовое удаление и очередь),
// М-3 (отмена запомненного названия камеры), М-4 (ссылка «этот товар»), М-5 (накладная в упаковках), М-6 (продажа штуки из
// пачки: флаг и сверка остатка), Ф-4 (подпись ответа кассы), С-1/С-2 протокола. Сеть и Gemini — подставные, база в памяти.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/ai_handles.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/call_focus.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/invoice/invoice_parser.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/invoice_import.dart' show invoicePackSize;
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/piece_sale_mode.dart';

import 'action_tools_test.dart' show FakeHost;
import 'admin_stock_fixes_test.dart' show FakeApi;

Product _p(String id, String name, {double price = 100, double qty = 5, String unit = 'шт', String alt = ''}) => Product(
  id: id,
  name: name,
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: qty,
  unit: unit,
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: alt,
  category: '',
  searchKey: name.toLowerCase(),
);

const _owner = Session(email: 'o', access: '', refresh: '', role: 'owner', isOwner: true, perms: [Session.viewProducts]);

/// Хозяин действий с очередью: у товара `p4` есть неотправленные операции.
class _QueuedHost extends FakeHost implements PendingOpsSource {
  final queued = <String, int>{};
  @override
  Future<int> pendingOpsFor(String productId) async => queued[productId] ?? 0;
}

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_r5_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('М-1: голосовое удаление не обходит очередь неотправленных операций', () {
    late _QueuedHost host;
    late ToolRegistry reg;
    late UserSpeechLog speech;
    var now = DateTime(2026, 10, 27, 12);
    var n = 0;

    setUp(() {
      host = _QueuedHost()..products = [_p('p4', 'Вода', price: 30, qty: 10), _p('p5', 'Сок', price: 50, qty: 0)];
      reg = ToolRegistry();
      speech = UserSpeechLog();
      registerActionTools(reg, ActionKit(host: host, tables: TableStore(), handles: AiScreenHandles(), now: () => now, handleTimeout: const Duration(milliseconds: 60)));
    });

    Future<ToolResult> say(String phrase, Map<String, dynamic> args) {
      now = now.add(const Duration(seconds: 2));
      speech.closeTurn();
      speech.add(phrase, echoSuspect: false, now: now);
      return reg.execute(LiveToolCall('c${++n}', 'delete_product', args), ToolEnv(session: _owner, speech: speech, mode: AssistantMode.act, lang: 'ru', now: () => now));
    }

    test('есть неотправленный чек с товаром: отказ с причиной, карточки и «да» нет, товар не удалён', () async {
      host.queued['p4'] = 2;
      final ask = await say('удали товар Вода', {'query': 'Вода'});
      expect(ask.status, ToolStatus.clarify);
      expect(ask.say, contains('неотправленные'));
      expect(ask.say, contains('(2)'));
      final yes = await say('да, удали', {'query': 'Вода', 'confirm': true});
      expect(yes.say, contains('неотправленные'), reason: 'даже с «да» — очередь блокирует');
      expect(host.deleted, isEmpty);
    });

    test('очередь пуста — удаление идёт как раньше (два шага, «да» вслух)', () async {
      final ask = await say('удали товар Вода', {'query': 'Вода'});
      expect(ask.say, isNot(contains('неотправленные')));
      final ok = await say('да, удали', {'query': 'Вода', 'confirm': true});
      expect(ok.status, ToolStatus.ok);
      expect(host.deleted, ['p4']);
    });

    test('другой товар с очередью не мешает', () async {
      host.queued['p4'] = 1;
      await say('удали товар Сок', {'query': 'Сок'});
      final ok = await say('да, удали', {'query': 'Сок', 'confirm': true});
      expect(ok.status, ToolStatus.ok);
      expect(host.deleted, ['p5']);
    });
  });

  group('М-3: отмена запомненного названия камеры не возвращается слиянием', () {
    late AppDatabase db;
    late Map<String, dynamic> shared;
    late FakeApi api;
    setUp(() {
      CamAliases.resetForTest();
      db = AppDatabase(NativeDatabase.memory());
      shared = {};
      api = FakeApi((m, p, b, q) async {
        if (p == 'api/main/app-settings/nurmarket-mobile/') {
          if (m == 'PATCH') shared.addAll((b as Map).cast<String, dynamic>());
          return {'settings': shared};
        }
        throw ApiException('нет $m $p', status: 404);
      })..attach(_owner);
    });
    tearDown(() => db.close());

    test('запомнили -> отменили: названия нет на телефоне и на сервере, "удалено" записано', () async {
      expect(await CamAliases.add(db, api, 'p1', 'Coca Cola'), isTrue);
      expect(CamAliases.current['p1'], ['coca cola']);
      expect(await CamAliases.remove(db, api, 'p1', text: 'Coca Cola'), isTrue);
      expect(CamAliases.current['p1'], isNull);
      expect(CamAliases.parse(shared['CamAliases'])['p1'], isNull);
      expect(CamAliases.parse(shared['CamAliasesRemoved'])['p1'], ['coca cola']);
    });

    test('слияние с сервера (через 10 минут) отмену не отменяет; чужое остаётся', () async {
      shared['CamAliases'] = {'p1': ['coca cola', 'кока кола'], 'p2': ['sprite']};
      await CamAliases.add(db, api, 'p9', 'Ещё одно'); // подтянуть сервер
      expect(CamAliases.current['p1'], ['coca cola', 'кока кола']);
      await CamAliases.remove(db, api, 'p1', text: 'coca cola');
      // «другой телефон» ещё держит старое название и пишет его на сервер обратно
      shared['CamAliases'] = {'p1': ['coca cola', 'кока кола'], 'p2': ['sprite']};
      await CamAliases.ensure(db, api, force: true);
      expect(CamAliases.current['p1'], ['кока кола'], reason: 'отменённое название не вернулось');
      expect(CamAliases.current['p2'], ['sprite']);
      expect(CamAliases.parse(shared['CamAliases'])['p1'], ['кока кола'], reason: 'и с сервера оно убрано');
    });

    test('после перезапуска (копия на телефоне) отметка «удалено» помнится', () async {
      await CamAliases.add(db, api, 'p1', 'Coca Cola');
      await CamAliases.remove(db, api, 'p1', text: 'Coca Cola');
      CamAliases.resetForTest();
      shared['CamAliases'] = {'p1': ['coca cola']};
      await CamAliases.ensure(db, api, force: true);
      expect(CamAliases.current['p1'], isNull);
    });

    test('то же название можно запомнить заново — отметка снимается', () async {
      await CamAliases.add(db, api, 'p1', 'Coca Cola');
      await CamAliases.remove(db, api, 'p1', text: 'Coca Cola');
      await CamAliases.add(db, api, 'p1', 'Coca Cola');
      expect(CamAliases.current['p1'], ['coca cola']);
      expect(CamAliases.removed['p1'], isNull);
      expect(CamAliases.parse(shared['CamAliases'])['p1'], ['coca cola']);
    });

    test('без связи: отмена действует на телефоне, на сервер не ушла (false)', () async {
      await CamAliases.add(db, api, 'p1', 'Coca Cola');
      final off = FakeApi((m, p, b, q) async => throw ApiException('Нет связи'))..attach(_owner);
      expect(await CamAliases.remove(db, off, 'p1', text: 'coca cola'), isFalse);
      expect(CamAliases.current['p1'], isNull);
    });

    test('«забыть все названия товара» (удалён товар) тоже с отметкой', () async {
      await CamAliases.add(db, api, 'p1', 'Coca Cola');
      await CamAliases.add(db, api, 'p1', 'Кока кола');
      await CamAliases.forget(db, 'p1');
      expect(CamAliases.current['p1'], isNull);
      expect(CamAliases.removed['p1'], ['coca cola', 'кока кола']);
    });
  });

  group('М-4: «этот товар» не указывает на прошлый товар камеры', () {
    final cola = _p('c1', 'Кола');
    final fanta = _p('f1', 'Фанта');
    late DateTime t;
    late CallFocus f;
    setUp(() {
      t = DateTime(2026, 10, 27, 12);
      f = CallFocus()..now = () => t;
    });

    test('новый кадр: узнали товар — он «этот»', () {
      f.newFrame(cola);
      expect(f.byRef('camera')?.id, 'c1');
      expect(f.byRef('this')?.id, 'c1');
    });

    test('Кола нашлась, Фанта не узнана -> «поставь этому цену» не находит Колу (переспросит)', () {
      f.newFrame(cola);
      f.newFrame(null); // следующий кадр: варианты / не найдено / ошибка
      expect(f.byRef('camera'), isNull);
      expect(f.byRef('this'), isNull);
      expect(f.byRef('last'), isNull);
    });

    test('через 2 минуты после кадра ссылка протухает (и last, полученный с камеры)', () {
      f.newFrame(cola);
      t = t.add(CallFocus.cameraTtl + const Duration(seconds: 1));
      expect(f.byRef('camera'), isNull);
      expect(f.byRef('this'), isNull);
      expect(f.camera, isNull);
    });

    test('last от инструмента (не с камеры) сроком камеры не ограничен', () {
      f.setLast(fanta);
      t = t.add(const Duration(hours: 1));
      expect(f.byRef('last')?.id, 'f1');
    });

    test('неоднозначно: на камере Кола, а последним правили Фанту -> "этот" не угадывается, camera — по-прежнему Кола', () {
      f.newFrame(cola);
      f.setLast(fanta);
      expect(f.byRef('camera')?.id, 'c1');
      expect(f.byRef('this'), isNull);
      f.setLast(cola);
      expect(f.byRef('this')?.id, 'c1');
    });

    test('прежнее присваивание камеры (удаление товара) работает', () {
      f.newFrame(cola);
      f.camera = null;
      expect(f.camera, isNull);
    });
  });

  group('М-5 / Ск-11: накладная в упаковках', () {
    test('единицы-упаковки узнаются, штуки и килограммы — нет', () {
      for (final u in ['кор', 'Кор.', 'коробка', 'упак', 'уп.', 'блок', 'ящик', 'пачка', 'box', '778']) {
        expect(isPackUnit(u), isTrue, reason: u);
      }
      for (final u in ['шт', 'шт.', 'кг', 'л', '', 'штука', '796']) {
        expect(isPackUnit(u), isFalse, reason: u);
      }
    });

    test('штук в упаковке — по доп. штрихкоду товара (код|штук)', () {
      final p = _p('p1', 'Кола 0,5', alt: '4600000000017|24\n4600000000024');
      expect(invoicePackSize(p, const InvoiceLine(row: 1, name: 'Кола', barcode: '4600000000017', qty: 2, cost: 600)), 24);
      expect(invoicePackSize(p, const InvoiceLine(row: 1, name: 'Кола', barcode: '4600000000024', qty: 2, cost: 600)), isNull, reason: 'код без количества');
      expect(invoicePackSize(p, const InvoiceLine(row: 1, name: 'Кола', barcode: '123', qty: 2, cost: 600)), isNull);
    });
  });

  group('М-6: продажа штуки из пачки — флаг и сверка остатка после продажи', () {
    late AppDatabase db;
    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Винстон', price: const Value(200), quantity: const Value(10), searchKey: const Value('винстон')));
    });
    tearDown(() => db.close());

    CartLine piece(double qty) => CartLine(productId: 'p1', name: 'Винстон', price: 12, qty: qty, unit: 'шт', salePackageId: 'pk1', perPack: 20, packName: 'Пачка');

    test('флаг pack_checkout_mode: по умолчанию штуки включены; off выключает, on возвращает', () async {
      expect(await PieceSaleMode.isOn(db), isTrue);
      await PieceSaleMode.set(db, false);
      expect(await db.getSetting('pack_checkout_mode'), 'off');
      expect(await PieceSaleMode.isOn(db), isFalse);
      await PieceSaleMode.set(db, true);
      expect(await PieceSaleMode.isOn(db), isTrue);
    });

    test('сервер списал штуки как положено (10 → 9,75): предупреждений нет', () async {
      var serverQty = 10.0;
      final api = FakeApi((m, p, b, q) async {
        if (p == 'api/main/pos/checkout/') {
          serverQty -= 0.25;
          return {'id': 's1', 'number': '1', 'total': '60.00'};
        }
        if (p == 'api/main/products/p1/') return {'quantity': serverQty};
        throw ApiException('нет $m $p', status: 404);
      })..attach(_owner);
      final r = await SalesRepository(api, db).checkout(cart: Cart(lines: [piece(5)]), shiftId: 'sh', method: PayMethod.cash, received: 100);
      expect(r.queued, isFalse);
      expect(r.stockWarnings, isEmpty);
    });

    test('сервер НЕ знает sale_package_id и списал 5 пачек вместо 5 штук (10 → 5): жёлтая плашка и журнал', () async {
      var serverQty = 10.0;
      final api = FakeApi((m, p, b, q) async {
        if (p == 'api/main/pos/checkout/') {
          serverQty -= 5; // 5 «штук» списаны как 5 пачек
          return {'id': 's1', 'number': '1', 'total': '60.00'};
        }
        if (p == 'api/main/products/p1/') return {'quantity': serverQty};
        throw ApiException('нет $m $p', status: 404);
      })..attach(_owner);
      final r = await SalesRepository(api, db).checkout(cart: Cart(lines: [piece(5)]), shiftId: 'sh', method: PayMethod.cash, received: 100);
      expect(r.stockWarnings.length, 1);
      expect(r.stockWarnings.single.productId, 'p1');
      expect(r.stockWarnings.single.expected, closeTo(9.75, 1e-9));
      expect(r.stockWarnings.single.actual, 5);
      expect((await db.productById('p1'))!.quantity, closeTo(9.75, 1e-9), reason: 'остаток телефона — по правилам, предупреждение только подсказка');
    });

    test('сервер не ответил на сверку / продажа без штук: чек проведён, предупреждений нет, лишних запросов нет', () async {
      final calls = <String>[];
      final api = FakeApi((m, p, b, q) async {
        calls.add('$m $p');
        if (p == 'api/main/pos/checkout/') return {'id': 's1', 'number': '1', 'total': '200.00'};
        throw ApiException('Нет связи');
      })..attach(_owner);
      final whole = CartLine(productId: 'p1', name: 'Винстон', price: 200, qty: 1);
      final r1 = await SalesRepository(api, db).checkout(cart: Cart(lines: [whole]), shiftId: 'sh', method: PayMethod.cash, received: 200);
      expect(r1.stockWarnings, isEmpty);
      expect(calls, ['POST api/main/pos/checkout/'], reason: 'у обычного чека сверки остатка нет');
      calls.clear();
      final r2 = await SalesRepository(api, db).checkout(cart: Cart(lines: [piece(5)]), shiftId: 'sh', method: PayMethod.cash, received: 100);
      expect(r2.queued, isFalse);
      expect(r2.stockWarnings, isEmpty, reason: 'не смогли сверить — молча пропускаем');
    });

    test('тело продажи не изменилось: sale_package_id и price_override у штуки, как раньше', () {
      final body = SalesRepository.buildBody(cart: Cart(lines: [piece(3)]), shiftId: 's1', method: PayMethod.cash, received: 100);
      expect((body['items'] as List).single, {'product': 'p1', 'qty': '3', 'price': '12.00', 'sale_package_id': 'pk1', 'price_override': true});
    });
  });

  group('Ф-4: подпись ответа кассы (необязательная проверка) и С-1/С-2 протокола', () {
    final key = List<int>.generate(32, (i) => i);
    const phone = 'phone-1234567890';
    const nonce = 'AAAAAAAAAAAAAAAAAAAAAA';

    test('векторы (посчитаны отдельным расчётом по формуле кассы PhoneLinkProtocol.ReplySigningString)', () {
      final raw = {'ok': true, 'code': 'added', 'name': 'Кола', 'shift_open': true, 'n': 5};
      expect(
        KassaProtocol.replySigningText(op: 'scan', phone: phone, nonce: nonce, ok: true, code: 'added', raw: raw),
        '1r|scan|$phone|$nonce|1|added|bde1297dd9cbb85eb0b26e0e2be5eb8bec2faf2f5bae5a3b6cd764887112bdc6',
      );
      expect(KassaProtocol.signReply(key: key, op: 'scan', phone: phone, nonce: nonce, ok: true, code: 'added', raw: raw), 'wH4Utbv7ghD0EngOyppW4KsQn-FTHbnuktPuCM-xpx4');
      expect(KassaProtocol.signReply(key: key, op: 'hello', phone: phone, nonce: nonce, ok: true, code: 'ok', raw: const {'ok': true, 'code': 'ok'}), 'BIs2FosXlV7TP77kP_q55VWHDuqWgkYPvMeNAmSUs9E');
      expect(KassaProtocol.signReply(key: key, op: 'scan', phone: phone, nonce: nonce, ok: false, code: 'not_found', raw: const {'ok': false, 'code': 'not_found', 'msg': 'нет'}), 'ZuNk-vcc1jOeWiyximVisFFQIhlbHbPSCSgEGHPLFiE');
    });

    test('векторы из docs/Протокол PhoneLink v1 (раздел 7): Python и C# дали те же подписи', () {
      const dphone = 'AAAAAAAAAAAAAAAAAAAAAA';
      const dnonce = 'AQIDBAUGBwgJCgsMDQ4PEA';
      String sig(bool ok, String code, Map<String, dynamic> raw) => KassaProtocol.signReply(key: key, op: 'scan', phone: dphone, nonce: dnonce, ok: ok, code: code, raw: raw);
      expect(sig(true, 'added', {'ok': true, 'code': 'added', 'seq': 1730000000, 'name': 'Товар 1'}), 'QqWfeYfZUTG3lkbblS5JFKDlM1eGGO32e6IxR6q6NNw');
      expect(sig(true, 'ok', {'ok': true, 'code': 'ok', 'kassa': 'Касса 1', 'company': 'a1b2c3d4', 'shift_open': true}), 'i18YJKB0neTp1zveeMZ2kUtTQrXZWeuv2NYQ7s8Za9Y');
      expect(sig(false, 'till_inactive', {'ok': false, 'code': 'till_inactive', 'seq': 1730000001}), 'oDhQZyLa0a5Mp6Mp5MEUkaZ79RGhdJty7BM9RPcLTA8');
      expect(sig(false, 'bad_time', {'ok': false, 'code': 'bad_time'}), '_bK5taXFXDEvzzU8xnLfon1Np-J12lcJY4lFy0x-_oY');
      expect(
        KassaProtocol.replySigningText(op: 'scan', phone: dphone, nonce: dnonce, ok: true, code: 'added', raw: {'ok': true, 'code': 'added', 'seq': 1730000000, 'name': 'Товар 1'}),
        '1r|scan|AAAAAAAAAAAAAAAAAAAAAA|AQIDBAUGBwgJCgsMDQ4PEA|1|added|5fcdb8e62dd2c568e12b78ece116bf24ab9333644f82bd802acaa2ab1efb6a5d',
      );
    });

    KassaReply reply(Map<String, dynamic> m) => KassaReply.parse(jsonEncode(m));

    test('verifyReply: верная — true, чужая подпись/другой nonce/другой код — false, без подписи — null', () {
      final raw = {'ok': true, 'code': 'added', 'name': 'Кола'};
      final sig = KassaProtocol.signReply(key: key, op: 'scan', phone: phone, nonce: nonce, ok: true, code: 'added', raw: raw);
      final good = reply({...raw, 'sv': 1, 'sig': sig});
      expect(KassaProtocol.verifyReply(key: key, op: 'scan', phone: phone, nonce: nonce, reply: good), isTrue);
      expect(KassaProtocol.verifyReply(key: key, op: 'scan', phone: phone, nonce: 'BBBBBBBBBBBBBBBBBBBBBB', reply: good), isFalse, reason: 'повтор старого ответа');
      expect(KassaProtocol.verifyReply(key: key, op: 'hello', phone: phone, nonce: nonce, reply: good), isFalse);
      expect(KassaProtocol.verifyReply(key: List<int>.filled(32, 7), op: 'scan', phone: phone, nonce: nonce, reply: good), isFalse, reason: 'чужой ключ');
      expect(KassaProtocol.verifyReply(key: key, op: 'scan', phone: phone, nonce: nonce, reply: reply({...raw, 'name': 'Подделка', 'sig': sig})), isFalse, reason: 'поле изменили');
      expect(KassaProtocol.verifyReply(key: key, op: 'scan', phone: phone, nonce: nonce, reply: reply(raw)), isNull);
      expect(KassaProtocol.verifyReply(key: key, op: 'scan', phone: phone, nonce: nonce, reply: reply({...raw, 'sig': '!!!'})), isFalse);
    });

    // клиент на подставной сети: «касса» подписывает ответ по запросу, как PhoneLinkServer
    const keyB64 = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
    const qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$keyB64&n=%D0%9A&c=1a2b3c4d';
    late DateTime now;
    late String Function(Map<String, dynamic> req) answer;

    KassaLink make() => KassaLink(
      store: MemoryKassaLinkStore(),
      clock: () => now,
      retryEvery: const Duration(milliseconds: 20),
      subnets: () async => <String>[],
      transport: (host, port, line, connect, replyT) async => answer(jsonDecode(line) as Map<String, dynamic>),
    );

    String signed(Map<String, dynamic> req, Map<String, dynamic> raw, {bool tamper = false}) {
      final ok = raw['ok'] as bool;
      final code = raw['code'] as String;
      final sig = KassaProtocol.signReply(key: key, op: req['op'] as String, phone: req['phone'] as String, nonce: tamper ? 'CCCCCCCCCCCCCCCCCCCCCC' : req['nonce'] as String, ok: ok, code: code, raw: raw);
      return jsonEncode({...raw, 'sv': 1, 'sig': sig});
    }

    setUp(() {
      now = DateTime.fromMillisecondsSinceEpoch(1761500000 * 1000);
      KassaLink.requireSignedReplies = false;
    });
    tearDown(() => KassaLink.requireSignedReplies = false);

    test('касса подписывает ответы — связь работает; подпись под чужой nonce — "не касса" (notFound)', () async {
      answer = (req) => signed(req, {'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
      final link = make();
      expect(await link.pairFromQr(qr), KassaLinkStatus.online);
      answer = (req) => signed(req, {'ok': true, 'code': 'ok', 'kassa': 'Основная', 'shift_open': true}, tamper: true);
      expect(await link.check(discover: false), KassaLinkStatus.notFound);
    });

    test('чужой узел отвечает ok:true без подписи: пока принимается (старая касса), в строгом режиме — нет', () async {
      answer = (req) => jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Чужая'});
      final link = make();
      expect(await link.pairFromQr(qr), KassaLinkStatus.online, reason: 'подпись необязательна, пока касса её не присылает');
      KassaLink.requireSignedReplies = true;
      expect(await link.check(discover: false), KassaLinkStatus.notFound);
      // отказ revoked может прийти без подписи — состояние связи всё равно обновляется
      answer = (req) => jsonEncode({'ok': false, 'code': 'revoked'});
      expect(await link.check(discover: false), KassaLinkStatus.revoked);
    });

    test('С-1 протокола: скан живёт в очереди не дольше 30 секунд', () async {
      expect(KassaLink.scanTtl, const Duration(seconds: 30));
      var down = true;
      answer = (req) {
        if (down) throw const KassaNetException('socket');
        return signed(req, {'ok': true, 'code': 'added', 'name': 'Молоко'});
      };
      final link = make();
      answer = (req) => signed(req, {'ok': true, 'code': 'ok', 'kassa': 'К', 'shift_open': true});
      await link.pairFromQr(qr);
      answer = (req) {
        if (down) throw const KassaNetException('socket');
        return signed(req, {'ok': true, 'code': 'added', 'name': 'Молоко'});
      };
      final events = <KassaScanEvent>[];
      link.events.listen(events.add);
      link.submitScan('4870001234567');
      await Future<void>.delayed(const Duration(milliseconds: 50));
      now = now.add(const Duration(seconds: 31));
      down = false;
      await Future<void>.delayed(const Duration(milliseconds: 120));
      expect(events.map((e) => e.outcome), contains(KassaScanOutcome.expired));
      expect(events.map((e) => e.outcome), isNot(contains(KassaScanOutcome.added)), reason: 'в чужой чек через минуты не попадёт');
    });

    test('в теле scan есть at — секунды UTC момента скана', () async {
      Map<String, dynamic>? scanBody;
      answer = (req) {
        if (req['op'] == 'scan') scanBody = jsonDecode(req['body'] as String) as Map<String, dynamic>;
        return signed(req, req['op'] == 'scan' ? {'ok': true, 'code': 'added'} : {'ok': true, 'code': 'ok', 'kassa': 'К', 'shift_open': true});
      };
      final link = make();
      await link.pairFromQr(qr);
      link.submitScan('4870001234567');
      await Future<void>.delayed(const Duration(milliseconds: 80));
      expect(scanBody!['at'], 1761500000);
      expect(scanBody!['code'], '4870001234567');
      expect(scanBody!['seq'], isA<int>());
    });

    test('stale: код убран из очереди, повторов нет, событие stale', () async {
      var scans = 0;
      answer = (req) {
        if (req['op'] != 'scan') return signed(req, {'ok': true, 'code': 'ok', 'kassa': 'К', 'shift_open': true});
        scans++;
        return signed(req, {'ok': false, 'code': 'stale', 'seq': 1});
      };
      final link = make();
      await link.pairFromQr(qr);
      final events = <KassaScanOutcome>[];
      link.events.listen((e) => events.add(e.outcome));
      link.submitScan('4870001234567');
      await Future<void>.delayed(const Duration(milliseconds: 150));
      expect(events, [KassaScanOutcome.stale]);
      expect(scans, 1);
      expect(link.pendingCount, 0);
      expect(link.status, KassaLinkStatus.online, reason: 'stale — не ошибка связи');
    });

    test('busy (идёт оплата): связь не «потеряна», подсказка один раз, повтор тем же seq, потом added', () async {
      var scans = 0;
      final seqs = <int>[];
      answer = (req) {
        if (req['op'] != 'scan') return signed(req, {'ok': true, 'code': 'ok', 'kassa': 'К', 'shift_open': true});
        scans++;
        seqs.add((jsonDecode(req['body'] as String) as Map)['seq'] as int);
        return scans < 3 ? signed(req, {'ok': false, 'code': 'busy'}) : signed(req, {'ok': true, 'code': 'added', 'name': 'Молоко'});
      };
      final link = make();
      await link.pairFromQr(qr);
      final events = <KassaScanOutcome>[];
      link.events.listen((e) => events.add(e.outcome));
      link.submitScan('4870001234567');
      await Future<void>.delayed(const Duration(milliseconds: 300));
      expect(events, [KassaScanOutcome.busy, KassaScanOutcome.added], reason: 'подсказка один раз, не на каждый повтор');
      expect(seqs.toSet().length, 1, reason: 'тот же seq');
      expect(link.status, KassaLinkStatus.online);
    });

    test('после первой верной подписи касса обязана подписывать: поддельный ok без подписи не принимается', () async {
      answer = (req) => signed(req, {'ok': true, 'code': 'ok', 'kassa': 'Основная', 'shift_open': true});
      final link = make();
      expect(await link.pairFromQr(qr), KassaLinkStatus.online);
      answer = (req) => jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Подделка'});
      expect(await link.check(discover: false), KassaLinkStatus.notFound);
    });
  });
}
