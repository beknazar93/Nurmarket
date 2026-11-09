// 2026-11-07: режим «Сканер для кассы» — тот же код держат в кадре: вместо молчаливых повторов каждые 1,5 с — вопрос «Сколько штук?»
// (askRepeat), «Отмена» ничего не шлёт, N — шлёт N кодов. Касса подставная, сеть и база не трогаются.
import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/link/kassa_link.dart';
import 'package:nurmarket/features/scan/kassa_scanner_mode.dart';

const _key = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8';
const _qr = 'nurmarket-link:v1?d=KASSA-1&h=192.168.1.20&p=47830&k=$_key&n=%D0%9E%D1%81%D0%BD%D0%BE%D0%B2%D0%BD%D0%B0%D1%8F&c=1a2b3c4d';

void main() {
  late List<String> scans;

  KassaLink makeLink() => KassaLink(
    store: MemoryKassaLinkStore(),
    subnets: () async => <String>[],
    retryEvery: const Duration(milliseconds: 20),
    transport: (host, port, line, c, r) async {
      final req = jsonDecode(line) as Map<String, dynamic>;
      if (req['op'] == 'hello') return jsonEncode({'ok': true, 'code': 'ok', 'kassa': 'Основная', 'company': '1a2b3c4d', 'shift_open': true});
      scans.add((jsonDecode(req['body'] as String) as Map)['code'] as String);
      return jsonEncode({'ok': true, 'code': 'added', 'name': 'Молоко'});
    },
  );

  setUp(() {
    scans = [];
    TestWidgetsFlutterBinding.ensureInitialized();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (c) async => null);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(const MethodChannel('kg.nurcrm.nurmarket/beep'), (c) async => null);
  });

  /// Камера держит код около [ms] мс: кадры каждые 100 мс (часы настоящие).
  Future<void> hold(KassaScannerController c, String code, int ms) async {
    for (var t = 0; t < ms; t += 100) {
      c.onCode(code);
      await Future<void>.delayed(const Duration(milliseconds: 100));
    }
  }

  test('код в кадре 2 с: первый уходит сразу, потом один вопрос; «3» — ещё три; пока код лежит — второго вопроса нет', () async {
    final link = makeLink();
    await link.pairFromQr(_qr);
    final asked = <String>[];
    final c = KassaScannerController(
      link: link,
      askRepeat: (code, product, sent) async {
        asked.add('$code/$sent');
        return 3;
      },
    );
    await c.start();
    await hold(c, '4870001234567', 2000);
    await Future<void>.delayed(const Duration(milliseconds: 100));
    expect(asked, ['4870001234567/1']);
    expect(scans.length, 1 + 3);
    // товар так и лежит перед камерой — окно второй раз не открывается и ничего не добавляется
    await hold(c, '4870001234567', 2000);
    await Future<void>.delayed(const Duration(milliseconds: 100));
    expect(asked.length, 1);
    expect(scans.length, 4);
    c.dispose();
    link.dispose();
  });

  test('«Отмена» — ничего не отправляется; другой код — без вопроса', () async {
    final link = makeLink();
    await link.pairFromQr(_qr);
    var asks = 0;
    final c = KassaScannerController(
      link: link,
      askRepeat: (code, product, sent) async {
        asks++;
        return null;
      },
    );
    await c.start();
    await hold(c, '4870001234567', 1800);
    await Future<void>.delayed(const Duration(milliseconds: 100));
    expect(asks, 1);
    expect(scans, ['4870001234567']);
    c.onCode('4600000000011');
    await Future<void>.delayed(const Duration(milliseconds: 100));
    expect(asks, 1);
    expect(scans, ['4870001234567', '4600000000011']);
    c.dispose();
    link.dispose();
  });
}
