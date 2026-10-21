// 2026-10-21: КП и счета общие с программой владельца — слияние по id и updatedAt, номера без повторов, очередь без
// связи, незнакомые поля ПК не теряются, PATCH только ключа «quotes». Сервер NurCRM не трогается — всё на фейке.
import 'dart:async';
import 'dart:convert';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/quotes.dart';

/// Общие настройки компании в памяти: PATCH сливает верхний уровень, как сервер NurCRM.
class FakeCloud implements QuotesCloud {
  Map<String, dynamic> settings;
  FakeCloud([Map<String, dynamic>? s]) : settings = s ?? {};
  bool offline = false;
  int? failStatus;
  String scopeValue = 'нур маркет';
  int fetches = 0;
  final patched = <List<Map<String, dynamic>>>[];

  /// Сразу после нашего PATCH «кто-то ещё» пишет свой список (одновременная запись ПК).
  void Function()? afterPatch;

  /// Задержка чтения — чтобы успеть изменить документ, пока идёт синхронизация.
  Completer<void>? gate;

  @override
  String get scope => scopeValue;

  @override
  Future<Map<String, dynamic>> fetch() async {
    fetches++;
    if (gate != null) await gate!.future;
    if (offline) throw ApiException('нет связи');
    return (jsonDecode(jsonEncode(settings)) as Map).cast<String, dynamic>();
  }

  @override
  Future<void> patchQuotes(List<Map<String, dynamic>> quotes) async {
    if (offline) throw ApiException('нет связи');
    if (failStatus != null) throw ApiException('Нет прав', status: failStatus);
    final copy = (jsonDecode(jsonEncode(quotes)) as List).cast<Map<String, dynamic>>();
    patched.add(copy);
    settings = {...settings, 'quotes': copy};
    final f = afterPatch;
    afterPatch = null;
    f?.call();
  }

  List<Map<String, dynamic>> get quotes => parseRemoteQuotes(settings['quotes']);
  Map<String, dynamic>? doc(String id) => quotes.where((m) => m['id'] == id).firstOrNull;
}

/// Документ программы владельца: свои поля (kind, vat, note, by), у строки — pr0, dp, f.
Map<String, dynamic> pcDoc(String id, String number, {String updatedAt = '2026-10-20T08:00:00.000Z', bool quote = true}) => {
  'id': id,
  'number': number,
  'quote': quote,
  'kind': quote ? 'quote' : 'invoice',
  'client': 'Бакыт',
  'clientId': 'c-77',
  'phone': '0555 777 888',
  'inn': '',
  'lines': [
    {'p': 'w', 'n': 'Вода', 'u': 'шт', 'q': 10, 'pr': 76.5, 'pr0': 85, 'dp': 10},
    {'p': '', 'n': 'Доставка', 'u': 'усл', 'q': 1, 'pr': 300, 'f': true},
  ],
  'discount': 0,
  'until': '2026-10-27T00:00:00.000',
  'status': 'sent',
  'paid': 0,
  'created': '2026-10-20T10:00:00.000',
  'basedOn': '',
  'history': ['20.10.2026 10:00 · Создан'],
  'note': 'Доставка до склада',
  'vat': 12,
  'template': 'modern',
  'by': 'Владелец',
  'updatedAt': updatedAt,
};

void main() {
  late AppDatabase db;
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  QuoteDoc phoneDoc(String id, String number, {bool quote = true}) => QuoteDoc(
    id: id,
    number: number,
    isQuote: quote,
    client: 'Айгүл',
    company: 'ИП Токтосунова',
    lines: [QuoteLine(productId: 's', name: 'Сок', qty: 2, price: 140)],
    until: DateTime(2026, 10, 28),
    status: quote ? 'draft' : 'issued',
    createdAt: DateTime(2026, 10, 21, 9),
  );

  Map<String, dynamic> others() => {
    'PaymentQr': {'bank': 'mbank', 'url': 'https://example/qr'},
    'DebtAccess': [
      {'user': '*', 'mode': 'allow'},
    ],
    'waybills': [
      {'id': 'wb1', 'number': 'НК-0001', 'kind': 'waybill', 'quote': false, 'updatedAt': '2026-10-20T08:00:00.000Z'},
    ],
    'docsProfile': {
      'name': 'Нур Маркет',
      'bank': 'Бай-Түшүм',
      'account': '1280016030170412',
      'bik': '128001',
      'counters': {'kp': 12, 'inv': 3, 'wb': 1},
    },
  };

  test('первая синхронизация: документы телефона уходят на сервер, другие ключи настроек не тронуты', () async {
    await QuotesStore(db).save([phoneDoc('p1', 'КП-0001'), phoneDoc('p2', 'СЧ-0001', quote: false)]);
    expect(await QuotesStore(db).unsentIds(), {'p1', 'p2'}, reason: 'до общего списка все документы «не отправлены»');
    final cloud = FakeCloud({...others(), 'quotes': [pcDoc('pc1', 'КП-0012')]});
    final store = QuotesStore(db, cloud: cloud);
    final res = await store.sync();
    expect(res.state, QuotesSyncState.synced);
    expect(cloud.patched.length, 1);
    expect(cloud.quotes.map((m) => m['id']).toSet(), {'p1', 'p2', 'pc1'});
    // Ключи QR оплаты, правил долга, накладных и реквизитов — те же.
    for (final k in ['PaymentQr', 'DebtAccess', 'waybills', 'docsProfile']) {
      expect(jsonEncode(cloud.settings[k]), jsonEncode(others()[k]), reason: k);
    }
    expect(jsonEncode(cloud.doc('pc1')), jsonEncode(pcDoc('pc1', 'КП-0012')), reason: 'чужой документ без изменений');
    expect(cloud.doc('p1')!['updatedAt'], isNotNull);
    expect(cloud.doc('p1')!['company'], 'ИП Токтосунова');
    expect(await store.unsentIds(), isEmpty);
    expect((await store.load()).length, 3);
    // Ничего не меняли — следующая синхронизация не пишет.
    await store.sync();
    expect(cloud.patched.length, 1);
  });

  test('документ ПК: незнакомые поля и строки сохраняются при изменении на телефоне', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012')]});
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    final d = (await store.load()).single;
    expect(d.extra['vat'], 12);
    expect(d.lines.first.extra['pr0'], 85);
    d
      ..status = 'accepted'
      ..history.add('21.10.2026 · Принято');
    d.lines[1].qty = 2; // доставка ×2, цена та же — «свободная строка» остаётся
    await store.put(d);
    expect(await store.unsentIds(), {'pc1'});
    await store.sync();
    final s = cloud.doc('pc1')!;
    expect(s['status'], 'accepted');
    for (final k in ['kind', 'clientId', 'note', 'vat', 'template', 'by']) {
      expect(s[k], pcDoc('pc1', '')[k], reason: k);
    }
    expect((s['lines'] as List)[0]['pr0'], 85);
    expect((s['lines'] as List)[0]['dp'], 10);
    expect((s['lines'] as List)[1]['f'], true);
    expect(quoteUpdatedAt(s)!.isAfter(DateTime.utc(2026, 10, 20, 8)), isTrue);

    // Цену строки поменяли на телефоне — скидка строки ПК (pr0, dp) больше не про неё.
    final d2 = (await store.load()).single;
    d2.lines.first.price = 80;
    await store.put(d2);
    await store.sync();
    final line = (cloud.doc('pc1')!['lines'] as List)[0] as Map;
    expect(line['pr'], 80);
    expect(line.containsKey('pr0'), isFalse);
    expect(line.containsKey('dp'), isFalse);
  });

  test('поле, которое ПК добавил позже, не теряется, когда побеждает версия телефона', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012')]});
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    final d = (await store.load()).single..status = 'declined';
    await store.put(d);
    // ПК без updatedAt дописал своё поле (старый формат) — телефон новее, но поле остаётся.
    cloud.settings['quotes'] = [
      {...pcDoc('pc1', 'КП-0012'), 'address': 'Бишкек, Чүй 1'}..remove('updatedAt'),
    ];
    await store.sync();
    expect(cloud.doc('pc1')!['status'], 'declined');
    expect(cloud.doc('pc1')!['address'], 'Бишкек, Чүй 1');
  });

  test('конфликт: побеждает более позднее изменение (ПК позже — версия ПК, телефон позже — версия телефона)', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('a', 'КП-0001'), pcDoc('b', 'КП-0002')]});
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    final docs = await store.load();
    final a = docs.firstWhere((d) => d.id == 'a')..status = 'declined';
    final b = docs.firstWhere((d) => d.id == 'b')..status = 'accepted';
    await store.put(a);
    await store.put(b);
    // На ПК «a» изменили позже телефона, «b» — раньше.
    cloud.settings['quotes'] = [
      {...pcDoc('a', 'КП-0001', updatedAt: quoteStamp(DateTime.now().add(const Duration(minutes: 5)))), 'status': 'accepted'},
      {...pcDoc('b', 'КП-0002', updatedAt: '2026-10-20T09:00:00.000Z'), 'status': 'declined'},
    ];
    final res = await store.sync();
    expect(cloud.doc('a')!['status'], 'accepted', reason: 'ПК позже — его версия');
    expect(cloud.doc('b')!['status'], 'accepted', reason: 'телефон позже — его версия');
    expect(res.lost, ['КП-0001']);
    expect((await store.load()).firstWhere((d) => d.id == 'a').status, 'accepted', reason: 'на телефоне — последняя версия');
    expect(await store.unsentIds(), isEmpty);
  });

  test('документ без updatedAt старше любого с полем; равные — уже на сервере', () {
    final remote = [
      {'id': 'x', 'number': 'КП-0001', 'status': 'sent'},
    ];
    final local = [
      {'id': 'x', 'number': 'КП-0001', 'status': 'declined', 'updatedAt': '2026-01-01T00:00:00.000Z'},
    ];
    final m = mergeQuotes(remote, local, {'x'});
    expect(m.list.single['status'], 'declined');
    expect(m.sent.keys, ['x']);
    final eq = mergeQuotes(local, local, {'x'});
    expect(eq.sent, isEmpty);
    expect(eq.same, {'x'});
    // Без updatedAt с обеих сторон — сервер.
    final none = mergeQuotes(remote, [
      {'id': 'x', 'status': 'paid'},
    ], {'x'});
    expect(none.list.single['status'], 'sent');
  });

  test('удаление: надгробие в общем списке, удалённое на ПК пропадает и на телефоне, старые надгробия чистятся', () async {
    final old = quoteStamp(DateTime.now().subtract(const Duration(days: 120)));
    final cloud = FakeCloud({
      'quotes': [
        pcDoc('a', 'КП-0001'),
        pcDoc('b', 'КП-0002'),
        {'id': 'gone', 'deleted': true, 'updatedAt': old},
      ],
    });
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    expect((await store.load()).map((d) => d.id), ['a', 'b']);
    await store.remove('a');
    expect((await store.load()).map((d) => d.id), ['b'], reason: 'удалённый не показывается');
    await store.sync();
    final t = cloud.doc('a')!;
    expect(t['deleted'], true);
    expect(t['updatedAt'], isNotNull);
    expect(t.containsKey('client'), isFalse, reason: 'надгробие — без данных документа');
    expect(cloud.doc('gone'), isNull, reason: 'надгробие старше 90 дней выброшено');
    // ПК удалил «b» — у телефона он пропадает.
    cloud.settings['quotes'] = [
      ...cloud.quotes.where((m) => m['id'] != 'b'),
      {'id': 'b', 'deleted': true, 'updatedAt': quoteStamp(DateTime.now().add(const Duration(minutes: 1)))},
    ];
    await store.sync();
    expect(await store.load(), isEmpty);
  });

  test('номер нового: максимум по общему списку и счётчикам ПК + 1; занятый на ПК — следующий свободный', () async {
    final cloud = FakeCloud({
      ...others(),
      'quotes': [pcDoc('a', 'КП-0007'), pcDoc('b', 'СЧ-0009', quote: false)],
    });
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    expect(await store.nextNumber(true), 'КП-0013', reason: 'docsProfile.counters.kp = 12');
    expect(await store.nextNumber(false), 'СЧ-0010', reason: 'в списке СЧ-0009 больше счётчика inv = 3');
    expect(await store.nextNumber(true), 'КП-0014', reason: 'свой счётчик телефона');

    // Без связи телефон создал КП-0015, а ПК за это время выдал тот же номер.
    cloud.offline = true;
    final mine = phoneDoc('m', 'КП-0015');
    await store.put(mine);
    await store.sync();
    cloud
      ..offline = false
      ..settings['quotes'] = [...cloud.quotes, pcDoc('pc15', 'КП-0015', updatedAt: quoteStamp())];
    final res = await store.sync();
    expect(res.renumbered, [('КП-0015', 'КП-0016')]);
    expect(cloud.doc('m')!['number'], 'КП-0016');
    expect(cloud.doc('pc15')!['number'], 'КП-0015');
    expect((await store.load()).firstWhere((d) => d.id == 'm').number, 'КП-0016');
  });

  test('номер: перепроверка при записи нового документа на телефоне', () async {
    final store = QuotesStore(db);
    await store.put(phoneDoc('a', 'КП-0004'));
    final b = phoneDoc('b', 'КП-0004');
    await store.put(b);
    expect(b.number, 'КП-0005');
  });

  test('без связи: документ на телефоне с пометкой «не отправлено», уходит при следующей синхронизации', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012')]})..offline = true;
    final store = QuotesStore(db, cloud: cloud);
    await store.put(phoneDoc('p1', 'КП-0013'));
    var res = await store.sync();
    expect(res.state, QuotesSyncState.offline);
    expect(await store.unsentIds(), {'p1'});
    expect((await store.load()).single.id, 'p1');
    // Фоновая досылка (SyncService) без своих изменений сервер не спрашивает.
    cloud.offline = false;
    res = await store.sync(onlyIfDirty: true);
    expect(res.state, QuotesSyncState.synced);
    expect(cloud.doc('p1'), isNotNull);
    expect(await store.unsentIds(), isEmpty);
    final n = cloud.fetches;
    await store.sync(onlyIfDirty: true);
    expect(cloud.fetches, n, reason: 'нечего отправлять — без запроса');
  });

  test('одновременная запись ПК затёрла нашу — слияние повторяется', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012')]});
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    await store.put(phoneDoc('p1', 'КП-0013'));
    // ПК прочитал список до нашей записи и записал свой — без нашего документа, но со своим новым.
    cloud.afterPatch = () => cloud.settings['quotes'] = [pcDoc('pc1', 'КП-0012'), pcDoc('pc2', 'СЧ-0001', quote: false)];
    final res = await store.sync();
    expect(res.state, QuotesSyncState.synced);
    expect(cloud.patched.length, 2);
    expect(cloud.quotes.map((m) => m['id']).toSet(), {'pc1', 'pc2', 'p1'});
  });

  test('нет прав (кассир, 403): документы остаются на телефоне, «не отправлено»', () async {
    final cloud = FakeCloud({'quotes': []})..failStatus = 403;
    final store = QuotesStore(db, cloud: cloud);
    await store.put(phoneDoc('p1', 'КП-0001'));
    final res = await store.sync();
    expect(res.state, QuotesSyncState.forbidden);
    expect(await store.unsentIds(), {'p1'});
    expect((await store.load()).single.id, 'p1');
    // Фоновая досылка не стучится сразу снова.
    final n = cloud.fetches;
    await store.sync(onlyIfDirty: true);
    expect(cloud.fetches, n);
  });

  test('изменение во время синхронизации не теряется', () async {
    final cloud = FakeCloud({'quotes': []});
    final store = QuotesStore(db, cloud: cloud);
    final d = phoneDoc('p1', 'КП-0001');
    await store.put(d);
    cloud.gate = Completer<void>();
    final running = store.sync();
    await Future<void>.delayed(const Duration(milliseconds: 20));
    d.status = 'sent';
    await store.put(d);
    cloud.gate!.complete();
    cloud.gate = null;
    await running;
    expect((await store.load()).single.status, 'sent');
    expect(await store.unsentIds(), {'p1'}, reason: 'новая версия ещё не ушла');
    await store.sync();
    expect(cloud.doc('p1')!['status'], 'sent');
    expect(await store.unsentIds(), isEmpty);
  });

  test('вход в другую компанию: документы прошлой ей не отправляются', () async {
    final cloud = FakeCloud({'quotes': []});
    final store = QuotesStore(db, cloud: cloud);
    await store.put(phoneDoc('p1', 'КП-0001'));
    cloud.offline = true;
    await store.sync(); // компания запомнена, документ не ушёл
    cloud
      ..offline = false
      ..scopeValue = 'другая компания'
      ..settings = {'quotes': [pcDoc('x', 'КП-0100')]};
    await store.sync();
    expect(cloud.doc('p1'), isNull);
    expect((await store.load()).map((d) => d.id), ['x']);
    expect(await db.getSetting('quotes.backup'), contains('p1'));
  });

  test('«quotes» строкой с JSON-массивом и реквизиты из программы владельца', () async {
    final cloud = FakeCloud({...others(), 'quotes': jsonEncode([pcDoc('pc1', 'КП-0012')])});
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    expect((await store.load()).single.number, 'КП-0012');
    expect(await store.requisites(), 'Бай-Түшүм\nр/с 1280016030170412\nБИК 128001');
    await store.setRequisites('Свои реквизиты');
    expect(await store.requisites(), 'Свои реквизиты');
  });

  test('отложенная проверка: запись ПК по старой копии легла после нашей — версия телефона возвращается один раз', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012')]});
    final store = QuotesStore(db, cloud: cloud, recheckDelay: const Duration(milliseconds: 30));
    await store.sync();
    final d = (await store.load()).single..status = 'accepted';
    await store.put(d);
    await store.put(phoneDoc('p1', 'КП-0013'));
    await store.sync();
    expect(cloud.patched.length, 1);
    // Уже после нашей проверки ПК записал список по старой копии: p1 пропал без надгробия, pc1 — старой версии.
    final stale = [pcDoc('pc1', 'КП-0012'), pcDoc('pc2', 'СЧ-0001', quote: false)];
    cloud.settings['quotes'] = stale;
    await store.recheck;
    expect(cloud.patched.length, 2, reason: 'одна запись-исправление');
    expect(cloud.doc('p1'), isNotNull);
    expect(cloud.doc('pc1')!['status'], 'accepted');
    expect(cloud.doc('pc2'), isNotNull, reason: 'новый документ ПК не потерян');
    // Исправление своей проверки не заводит — снова затёрли, повторов нет (до следующей своей записи).
    final before = store.recheck;
    cloud.settings['quotes'] = stale;
    await Future<void>.delayed(const Duration(milliseconds: 80));
    expect(identical(store.recheck, before), isTrue);
    expect(cloud.patched.length, 2);
  });

  test('отложенная проверка: удалённое на ПК надгробием и более новая версия ПК не перезаписываются', () async {
    final cloud = FakeCloud({'quotes': [pcDoc('pc1', 'КП-0012'), pcDoc('pc2', 'КП-0014')]});
    final store = QuotesStore(db, cloud: cloud, recheckDelay: const Duration(milliseconds: 30));
    await store.sync();
    for (final d in await store.load()) {
      await store.put(d..status = 'declined');
    }
    await store.sync();
    final later = quoteStamp(DateTime.now().add(const Duration(minutes: 1)));
    cloud.settings['quotes'] = [
      {'id': 'pc1', 'deleted': true, 'updatedAt': later},
      {...pcDoc('pc2', 'КП-0014', updatedAt: later), 'status': 'accepted'},
    ];
    await store.recheck;
    expect(cloud.patched.length, 1, reason: 'нечего исправлять');
    expect(cloud.doc('pc1')!['deleted'], true);
    expect(cloud.doc('pc2')!['status'], 'accepted');
  });

  test('ПК: старое поле updated обновляется рядом с updatedAt, счётчики kp/inv/wb читаются', () async {
    final cloud = FakeCloud({
      ...others(),
      'quotes': [
        {...pcDoc('pc1', 'КП-0012'), 'updated': '2026-10-20T14:00:00.000'},
      ],
    });
    final store = QuotesStore(db, cloud: cloud);
    await store.sync();
    final d = (await store.load()).single..status = 'accepted';
    await store.put(d);
    await store.sync();
    final s = cloud.doc('pc1')!;
    expect(s['updated'], isNot('2026-10-20T14:00:00.000'));
    expect(DateTime.parse(s['updated'] as String).isAfter(DateTime(2026, 10, 20, 14)), isTrue);
    expect(RegExp(r'^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}$').hasMatch(s['updated'] as String), isTrue, reason: 'формат ПК, без зоны');
    expect(s['updatedAt'], endsWith('Z'));
    // Документ без updated не получает его.
    await store.put(phoneDoc('p1', await store.nextNumber(true)));
    await store.sync();
    expect(cloud.doc('p1')!.containsKey('updated'), isFalse);
    expect(cloud.doc('p1')!['number'], 'КП-0013', reason: 'docsProfile.counters.kp = 12');
    expect(jsonEncode(cloud.settings['docsProfile']), jsonEncode(others()['docsProfile']), reason: 'счётчики ПК (kp/inv/wb) не тронуты');
  });
}
