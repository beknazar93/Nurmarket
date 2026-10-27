import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/link/kassa_link.dart';

// 2026-10-27: связь с кассой (протокол v1). Тест без сети и без базы: подпись на фиксированных векторах (сверены отдельным
// расчётом HMAC-SHA256 на Python), разбор QR, формат запроса, клиент на подставной сети, одна проверка на loopback-сокете.

// Ключ 0..31, nonce — байты 0..15, телефон «phone-test-0001», время 1761500000.
const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
const _nonce = 'AAECAwQFBgcICQoLDA0ODw';
const _phone = 'phone-test-0001';
const _ts = 1761500000;
const _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=%D0%9E%D1%81%D0%BD%D0%BE%D0%B2%D0%BD%D0%B0%D1%8F&c=1a2b3c4d';

String _reply(Map<String, dynamic> m) => jsonEncode(m);

void main() {
  final key = KassaProtocol.fromB64u(_key);

  group('подпись', () {
    test('ключ и nonce из байтов 0..N', () {
      expect(key, List<int>.generate(32, (i) => i));
      expect(KassaProtocol.b64u(List<int>.generate(16, (i) => i)), _nonce);
    });

    test('hello: хэш тела и подпись — фиксированные векторы', () {
      const body = '{"name":"Айбек"}';
      expect(KassaProtocol.bodyHash(body), '128b5051a86667e493296fe749080a3f8c813a56f9ec1822a7f58420ce585746');
      expect(
        KassaProtocol.signedText(op: 'hello', phone: _phone, ts: _ts, nonce: _nonce, body: body),
        '1|hello|phone-test-0001|1761500000|$_nonce|128b5051a86667e493296fe749080a3f8c813a56f9ec1822a7f58420ce585746',
      );
      expect(KassaProtocol.sign(key: key, op: 'hello', phone: _phone, ts: _ts, nonce: _nonce, body: body), 'UT86tKpA44eoqvPUXsCMczaCiXo0Az3plwHtJMB-KcQ');
    });

    test('scan: подпись — фиксированный вектор', () {
      const body = '{"code":"4870001234567","seq":7}';
      expect(KassaProtocol.bodyHash(body), 'ee100b13db3df138bd45d02e72e0e6d78e396e7057ce645c86846140c638382c');
      expect(KassaProtocol.sign(key: key, op: 'scan', phone: _phone, ts: _ts, nonce: _nonce, body: body), 'GsHm4jzIWsMK_kdNLfyu5DG_X6UZVD0ihs3CweRJsmQ');
    });

    test('запрос: одна строка JSON с \\n, поля и подпись', () {
      const body = '{"code":"4870001234567","seq":7}';
      final line = KassaProtocol.buildRequest(op: 'scan', phone: _phone, key: key, ts: _ts, nonce: _nonce, body: body);
      expect(line.endsWith('\n'), isTrue);
      expect(line.indexOf('\n'), line.length - 1);
      final m = jsonDecode(line) as Map<String, dynamic>;
      expect(m.keys.toList(), ['v', 'op', 'phone', 'ts', 'nonce', 'body', 'sig']);
      expect(m['v'], 1);
      expect(m['op'], 'scan');
      expect(m['phone'], _phone);
      expect(m['ts'], _ts);
      expect(m['nonce'], _nonce);
      expect(m['body'], body); // тело — строка, не вложенный объект
      expect(m['sig'], 'GsHm4jzIWsMK_kdNLfyu5DG_X6UZVD0ihs3CweRJsmQ');
      expect(m['sig'], isNot(contains('=')));
    });

    test('nonce: 16 байт, base64url без «=», разные', () {
      final a = KassaProtocol.newNonce(), b = KassaProtocol.newNonce();
      expect(a.length, 22);
      expect(a, isNot(contains('=')));
      expect(KassaProtocol.fromB64u(a).length, 16);
      expect(a, isNot(b));
    });
  });

  group('QR сопряжения', () {
    test('разбор: адрес, порт, ключ, имя (url-кодировано), компания', () {
      final p = KassaPairing.parseQr(_qr);
      expect(p.deviceId, 'KASSA-1');
      expect(p.host, '192.168.1.20');
      expect(p.port, 47830);
      expect(p.key, key);
      expect(p.name, 'Основная');
      expect(p.company, '1a2b3c4d');
    });

    test('сохранение в строку и обратно', () {
      final p = KassaPairing.parseQr(_qr);
      final back = KassaPairing.fromJsonString(p.toJsonString())!;
      expect(back.host, p.host);
      expect(back.key, p.key);
      expect(back.name, p.name);
      expect(KassaPairing.fromJsonString('мусор'), isNull);
    });

    test('неверные QR', () {
      void bad(String s, String reason) {
        try {
          KassaPairing.parseQr(s);
          fail('ожидалась ошибка для $s');
        } on KassaQrException catch (e) {
          expect(e.reason, reason, reason: s);
        }
      }

      bad('https://example.com', 'format');
      bad('4870001234567', 'format');
      bad('nurmarket-link:v1', 'format');
      bad('nurmarket-link:v1?d=K&h=192.168.1.20&p=47830', 'format'); // нет ключа
      bad('nurmarket-link:v1?d=K&h=192.168.1.20&p=47830&k=AAEC', 'format'); // ключ короче 32 байт
      bad('nurmarket-link:v1?d=K&h=999.1.1.1&p=47830&k=$_key', 'format');
      bad('nurmarket-link:v1?d=K&h=192.168.1.20&p=0&k=$_key', 'format');
      bad('nurmarket-link:v1?d=K&h=192.168.1.20&p=70000&k=$_key', 'format');
      bad('nurmarket-link:v1?d=&h=192.168.1.20&p=47830&k=$_key', 'format');
      bad('nurmarket-link:v1?d=K&h=192.168.1.20&p=47830&k=!!!!', 'format');
      bad('nurmarket-link:v2?d=K&h=192.168.1.20&p=47830&k=$_key', 'version');
    });
  });

  group('клиент на подставной сети', () {
    late MemoryKassaLinkStore store;
    late List<Map<String, dynamic>> requests;
    late List<String> hosts;
    late KassaReply Function(Map<String, dynamic> req, String host, int port) handler;
    late bool netDown;
    late DateTime now;

    KassaLink make({Future<List<String>> Function()? subnets}) => KassaLink(
      store: store,
      clock: () => now,
      retryEvery: const Duration(milliseconds: 20),
      subnets: subnets ?? () async => <String>[],
      transport: (host, port, line, connect, reply) async {
        if (netDown) throw const KassaNetException('socket');
        final req = jsonDecode(line) as Map<String, dynamic>;
        // касса проверяет подпись так же, как телефон её считает
        final expected = KassaProtocol.sign(
          key: key,
          op: req['op'] as String,
          phone: req['phone'] as String,
          ts: req['ts'] as int,
          nonce: req['nonce'] as String,
          body: req['body'] as String,
        );
        expect(req['sig'], expected);
        requests.add(req);
        hosts.add('$host:$port');
        final r = handler(req, host, port);
        return jsonEncode(r.raw);
      },
    );

    KassaReply ok(Map<String, dynamic> extra) => KassaReply(ok: true, code: 'ok', msg: '', raw: {'ok': true, 'code': 'ok', ...extra});
    KassaReply fail(String code) => KassaReply(ok: false, code: code, msg: '', raw: {'ok': false, 'code': code, 'msg': ''});

    setUp(() {
      store = MemoryKassaLinkStore();
      requests = [];
      hosts = [];
      netDown = false;
      now = DateTime.fromMillisecondsSinceEpoch(_ts * 1000);
      handler = (req, host, port) => req['op'] == 'hello'
          ? ok({'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true})
          : KassaReply(ok: true, code: 'added', msg: '', raw: {'ok': true, 'code': 'added', 'name': 'Молоко'});
    });

    test('QR → hello → на связи, имя кассы и смена; ключ и id телефона сохранены', () async {
      final link = make()..deviceName = 'Айбек';
      expect(await link.pairFromQr(_qr), KassaLinkStatus.online);
      expect(link.kassaName, 'Основная');
      expect(link.shiftOpen, isTrue);
      expect(requests.single['op'], 'hello');
      expect(jsonDecode(requests.single['body'] as String), {'name': 'Айбек'});
      expect(hosts.single, '192.168.1.20:47830');
      final phone = requests.single['phone'] as String;
      expect(phone.length, inInclusiveRange(8, 64));
      // после «перезапуска» читается то же сопряжение и тот же id телефона
      final again = make();
      await again.ensureLoaded();
      expect(again.isPaired, isTrue);
      expect(again.status, KassaLinkStatus.unknown);
      await again.check();
      expect(requests.last['phone'], phone);
    });

    test('коды отказа → состояние: revoked, bad_time, disabled, bad_sig', () async {
      final link = make();
      await link.pairFromQr(_qr);
      for (final (code, st) in [
        ('revoked', KassaLinkStatus.revoked),
        ('bad_time', KassaLinkStatus.badTime),
        ('disabled', KassaLinkStatus.disabled),
        ('bad_sig', KassaLinkStatus.rePair),
        ('unknown_phone', KassaLinkStatus.rePair),
      ]) {
        handler = (req, host, port) => fail(code);
        expect(await link.check(discover: false), st, reason: code);
      }
    });

    test('касса не отвечает и в подсети нет → «не найдена»', () async {
      final link = make();
      netDown = true;
      expect(await link.pairFromQr(_qr), KassaLinkStatus.notFound);
      expect(link.isPaired, isTrue); // сопряжение не теряется
    });

    test('адрес из QR не отвечает → найдена в подсети телефона на другом порту, адрес запомнен', () async {
      handler = (req, host, port) {
        if (host == '192.168.1.77' && port == 47832) return ok({'kassa': 'Основная', 'shift_open': false});
        if (host == '192.168.1.5' && port == 47830) return fail('bad_sig'); // чужая касса — не наша
        throw const KassaNetException('socket');
      };
      final link = make(subnets: () async => ['192.168.1']);
      expect(await link.pairFromQr(_qr), KassaLinkStatus.online);
      expect(link.pairing!.host, '192.168.1.77');
      expect(link.pairing!.port, 47832);
      expect(link.shiftOpen, isFalse);
      final saved = KassaPairing.fromJsonString(store.data['kassa.link.pairing'])!;
      expect(saved.host, '192.168.1.77');
    });

    test('scan: тело {code, seq}, seq растёт, ответ added', () async {
      final link = make();
      await link.pairFromQr(_qr);
      final events = <KassaScanEvent>[];
      link.events.listen(events.add);
      expect(link.submitScan('4870001234567'), KassaSubmit.accepted);
      expect(link.submitScan('4870001234568'), KassaSubmit.accepted);
      await Future<void>.delayed(const Duration(milliseconds: 50));
      final scans = requests.where((r) => r['op'] == 'scan').toList();
      expect(scans.length, 2);
      final b0 = jsonDecode(scans[0]['body'] as String) as Map<String, dynamic>;
      final b1 = jsonDecode(scans[1]['body'] as String) as Map<String, dynamic>;
      expect(b0['code'], '4870001234567');
      expect(b1['seq'], greaterThan(b0['seq'] as int));
      expect(events.map((e) => e.outcome), [KassaScanOutcome.added, KassaScanOutcome.added]);
      expect(events.first.productName, 'Молоко');
      expect(int.parse(store.data['kassa.link.seq']!), b1['seq']);
    });

    test('scan: not_found, till_inactive, duplicate (успех), revoked (отказ)', () async {
      final link = make();
      await link.pairFromQr(_qr);
      final events = <KassaScanEvent>[];
      link.events.listen(events.add);
      for (final code in ['not_found', 'till_inactive', 'duplicate', 'revoked']) {
        handler = (req, host, port) => code == 'duplicate' ? KassaReply(ok: true, code: code, msg: '', raw: {'ok': true, 'code': code}) : fail(code);
        link.submitScan('1234');
        await Future<void>.delayed(const Duration(milliseconds: 30));
      }
      expect(events.map((e) => e.outcome), [KassaScanOutcome.notFound, KassaScanOutcome.tillInactive, KassaScanOutcome.added, KassaScanOutcome.failed]);
      expect(link.status, KassaLinkStatus.revoked);
    });

    test('нет связи: код в очереди, потом уходит сам; порядок сохраняется', () async {
      final link = make();
      await link.pairFromQr(_qr);
      final events = <KassaScanEvent>[];
      link.events.listen(events.add);
      netDown = true;
      link.submitScan('111');
      link.submitScan('222');
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(events.map((e) => e.outcome), [KassaScanOutcome.queued]); // «в очереди» — один раз на первый код
      expect(link.pendingCount, 2);
      expect(link.status, KassaLinkStatus.notFound);
      netDown = false;
      await Future<void>.delayed(const Duration(milliseconds: 150));
      expect(link.pendingCount, 0);
      final sent = requests.where((r) => r['op'] == 'scan').map((r) => (jsonDecode(r['body'] as String) as Map)['code']).toList();
      expect(sent, ['111', '222']);
      expect(events.where((e) => e.outcome == KassaScanOutcome.added).length, 2);
      expect(link.status, KassaLinkStatus.online);
    });

    test('очередь не больше 50, дальше отказ; старый код отбрасывается', () async {
      final link = make();
      await link.pairFromQr(_qr);
      final events = <KassaScanEvent>[];
      link.events.listen(events.add);
      netDown = true;
      for (var i = 0; i < KassaLink.maxQueue; i++) {
        expect(link.submitScan('c$i'), KassaSubmit.accepted);
      }
      expect(link.submitScan('лишний'), KassaSubmit.queueFull);
      expect(link.pendingCount, KassaLink.maxQueue);
      await Future<void>.delayed(const Duration(milliseconds: 30));
      now = now.add(KassaLink.scanTtl + const Duration(seconds: 1));
      await Future<void>.delayed(const Duration(milliseconds: 100));
      expect(link.pendingCount, 0);
      expect(events.where((e) => e.outcome == KassaScanOutcome.expired).length, KassaLink.maxQueue);
    });

    test('без сопряжения скан не принимается; «Отключить» забывает ключ', () async {
      final link = make();
      await link.ensureLoaded();
      expect(link.submitScan('1'), KassaSubmit.notPaired);
      await link.pairFromQr(_qr);
      await link.disconnect();
      expect(link.status, KassaLinkStatus.unpaired);
      expect(store.data.containsKey('kassa.link.pairing'), isFalse);
      expect(link.submitScan('1'), KassaSubmit.notPaired);
    });

    test('расширение: произвольная операция идёт тем же подписанным каналом', () async {
      final link = make();
      await link.pairFromQr(_qr);
      handler = (req, host, port) => ok({'job': 'j1'});
      final r = await link.call('print', {'job': 'j1'});
      expect(r.ok, isTrue);
      expect(r.raw['job'], 'j1');
      expect(requests.last['op'], 'print');
    });
  });

  test('сокет: одна строка туда, одна строка обратно (loopback)', () async {
    final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(server.close);
    String? got;
    server.listen((c) async {
      final line = await c.cast<List<int>>().transform(utf8.decoder).transform(const LineSplitter()).first;
      got = line;
      c.add(utf8.encode('${_reply({'ok': true, 'code': 'ok'})}\n'));
      await c.flush();
      await c.close();
    });
    final reply = await socketExchange('127.0.0.1', server.port, 'привет\n', const Duration(seconds: 2), const Duration(seconds: 2));
    expect(got, 'привет');
    expect(KassaReply.parse(reply).ok, isTrue);
    // порт без сервера → сетевая ошибка, а не зависание
    final dead = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final deadPort = dead.port;
    await dead.close();
    await expectLater(socketExchange('127.0.0.1', deadPort, 'x\n', const Duration(milliseconds: 400), const Duration(milliseconds: 400)), throwsA(isA<KassaNetException>()));
  });
}
