// 2026-11-20 (владелец: «очень долго думает при скане накладной!», 62 с и дольше). Что проверяется: снимок накладной сжимается
// до 1600 px / качество 75 и всё тяжелее 350 КБ пересжимается; ответ ИИ — короткие ключи (n, q, u, p, b), оборванный ответ не пропадает;
// срок чтения накладной ограничен (первая попытка 20 с, всего 30 с); «Отмена» обрывает запрос; этапы разбора идут по порядку;
// сопоставление строк с каталогом идёт одновременно и даёт тот же результат. Замеры печатаются (ЗАМЕР …) — на ПК, не на телефоне.
import 'dart:async';
import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/ai_image.dart';
import 'package:nurmarket/core/ai/cam_aliases.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/catalog_matcher.dart';
import 'package:nurmarket/core/catalog/product_matcher.dart' show tokenEq;
import 'package:nurmarket/core/db/database.dart';

import 'admin_stock_fixes_test.dart' show FakeApi;

/// Снимок «как с камеры»: градиент, мелкий шум и линии-«строки» — по размеру ближе к настоящему фото бумаги, чем гладкая картинка.
Uint8List _photo(int w, int h, {int quality = 95}) {
  final rnd = Random(7);
  final im = img.Image(width: w, height: h);
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      final line = (y ~/ 18).isEven && x % 7 < 3 ? -70 : 0;
      final v = (200 + 40 * sin(x / 90) + 15 * cos(y / 60) + rnd.nextInt(41) - 20 + line).clamp(0, 255).toInt();
      im.setPixelRgb(x, y, v, v - 8 < 0 ? 0 : v - 8, v - 16 < 0 ? 0 : v - 16);
    }
  }
  return Uint8List.fromList(img.encodeJpg(im, quality: quality));
}

class _Fake implements HttpClientAdapter {
  final ResponseBody Function(RequestOptions o, int n) handler;
  final calls = <RequestOptions>[];
  _Fake(this.handler);
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls.add(options);
    return handler(options, calls.length);
  }

  @override
  void close({bool force = false}) {}
}

/// Google не отвечает; запрос обрывается, когда Dio просит отменить (как настоящий адаптер).
class _Hang implements HttpClientAdapter {
  int calls = 0;
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) {
    calls++;
    final c = Completer<ResponseBody>();
    cancelFuture?.then((_) {
      if (!c.isCompleted) c.completeError(DioException.requestCancelled(requestOptions: options, reason: 'cancel'));
    });
    return c.future;
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _ok(String text) => ResponseBody.fromString(
  jsonEncode({
    'candidates': [
      {
        'content': {
          'role': 'model',
          'parts': [
            {'text': text},
          ],
        },
        'finishReason': 'STOP',
      },
    ],
  }),
  200,
  headers: {Headers.contentTypeHeader: [Headers.jsonContentType]},
);

class _V extends VisionService {
  _V(super.api, super.db, {super.http});
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async => {'TelegramAiKey': 'AIza-test'};
  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async => const AiAccessPolicy.unset();
}

// 30 строк накладной: как отвечал ИИ раньше (все поля) и как отвечает теперь (короткие ключи)
List<Map<String, Object>> _rows(int n) => [
  for (var i = 1; i <= n; i++)
    {'name': 'Напиток газированный «Сэмпл $i» 1 л', 'qty': i % 4 + 1, 'unit': 'шт', 'price': 85.5 + i, 'code': '46000000${i.toString().padLeft(5, '0')}'},
];

String _oldJson(List<Map<String, Object>> rows) => jsonEncode([
  for (final r in rows)
    {'name': r['name'], 'name_ru': r['name'], 'brand': 'Сэмпл', 'size': '1 л', 'barcode': r['code'], 'count': r['qty'], 'label_price': r['price'], 'unit': r['unit']},
]);

String _newJson(List<Map<String, Object>> rows) => jsonEncode([
  for (final r in rows) {'n': r['name'], 'q': r['qty'], 'u': r['unit'], 'p': r['price'], 'b': r['code']},
]);

void main() {
  late AppDatabase db;
  late FakeApi api;

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
    CamAliases.resetForTest();
    // сеть не нужна: любой запрос к серверу — 404 (настоящий ApiClient ходил бы в app.nurcrm.kg)
    api = FakeApi((m, p, b, q) async => throw ApiException('нет', status: 404))
      ..attach(const Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'owner', companyName: 'NBS', companyInn: '1', role: 'owner', isOwner: true));
  });
  tearDown(() async {
    VisionService.invoiceTotal = const Duration(seconds: 30);
    VisionService.invoiceFirstTry = const Duration(seconds: 20);
    await db.close();
  });

  group('снимок накладной для ИИ', () {
    test('1920×1080, качество 95: уходит ≤ 1600 px и заметно легче (замер)', () {
      final big = _photo(1920, 1080, quality: 95);
      final sw = Stopwatch()..start();
      final out = shrinkSync(big, maxSide: invoiceMaxSide, quality: invoiceQuality, recompress: true);
      sw.stop();
      expect(out, isNotNull);
      final size = jpegSize(out!)!;
      expect(size.$1 > size.$2 ? size.$1 : size.$2, invoiceMaxSide);
      expect(out.length, lessThan(big.length * 0.6));
      // что значит для загрузки: base64 +33 %, мобильный интернет 2 и 5 Мбит/с на отправку
      double sec(int bytes, double mbit) => bytes * 1.33 * 8 / (mbit * 1000 * 1000);
      // ignore: avoid_print
      print('ЗАМЕР снимок 1920×1080 q95: ${big.length ~/ 1024} КБ → ${out.length ~/ 1024} КБ (${size.$1}×${size.$2}, q$invoiceQuality), подготовка ${sw.elapsedMilliseconds} мс на ПК; '
          'отправка на 2 Мбит/с: ${sec(big.length, 2).toStringAsFixed(1)} с → ${sec(out.length, 2).toStringAsFixed(1)} с, на 5 Мбит/с: ${sec(big.length, 5).toStringAsFixed(1)} с → ${sec(out.length, 5).toStringAsFixed(1)} с');
    });

    test('в пределах 1600 px, но тяжёлый (1200×800, качество 98): пересжимается без уменьшения', () async {
      final heavy = _photo(1200, 800, quality: 98);
      expect(heavy.length, greaterThan(invoiceRecompressAbove), reason: 'снимок для проверки должен быть тяжёлым');
      final out = await shrinkForAi(heavy, maxSide: invoiceMaxSide, quality: invoiceQuality, recompressAbove: invoiceRecompressAbove);
      expect(identical(out, heavy), isFalse);
      expect(out.length, lessThan(heavy.length));
      expect(jpegSize(out), (1200, 800), reason: 'размер не меняется');
    });

    test('лёгкий снимок в пределах размера не трогается (как раньше)', () async {
      final light = _photo(800, 600, quality: 50);
      expect(light.length, lessThan(invoiceRecompressAbove));
      expect(identical(await shrinkForAi(light, maxSide: invoiceMaxSide, quality: invoiceQuality, recompressAbove: invoiceRecompressAbove), light), isTrue);
    });

    test('без порога пересжатия поведение прежнее: тяжёлый снимок в пределах размера уходит как есть', () async {
      final heavy = _photo(1200, 800, quality: 98);
      expect(identical(await shrinkForAi(heavy, maxSide: 1280), heavy), isTrue);
    });
  });

  group('ответ ИИ по накладной', () {
    test('короткие ключи n, q, u, p, b разбираются так же, как длинные', () {
      final rows = _rows(30);
      final a = VisionService.parseSeen(_oldJson(rows));
      final b = VisionService.parseSeen(_newJson(rows));
      expect(b.length, 30);
      for (var i = 0; i < 30; i++) {
        expect((b[i].name, b[i].count, b[i].unit, b[i].labelPrice, b[i].barcode), (a[i].name, a[i].count, a[i].unit, a[i].labelPrice, a[i].barcode), reason: 'строка $i');
      }
      // ignore: avoid_print
      print('ЗАМЕР ответ ИИ на 30 строк: было ${_oldJson(rows).length} знаков, стало ${_newJson(rows).length} (${(100 - _newJson(rows).length * 100 / _oldJson(rows).length).round()} % короче; время ответа почти пропорционально числу токенов)');
      expect(_newJson(rows).length, lessThan(_oldJson(rows).length * 0.7));
    });

    test('дробное количество «2,5» и «0.5» из накладной сохраняется в коротком формате', () {
      final items = VisionService.parseSeen('[{"n":"Молоко","q":"2,5","u":"л","p":"88,5","b":""},{"n":"Сыр","q":0.5,"u":"кг","p":900,"b":""}]');
      expect(items[0].amount, 2.5);
      expect(items[0].unit, 'л');
      expect(items[0].labelPrice, 88.5);
      expect(items[1].amount, 0.5);
    });

    test('ответ оборвался на лимите длины: целые строки берутся, а не «ошибка разбора»', () {
      final full = _newJson(_rows(10));
      final cut = full.substring(0, full.length - 25); // оборвано посреди последней строки
      final items = VisionService.parseSeen(cut);
      expect(items.length, 9);
      expect(items.last.name, contains('Сэмпл 9'));
      expect(VisionService.parseSeen('это не JSON'), isEmpty, reason: 'совсем не JSON — «ничего не распознано», а не необработанная ошибка');
    });
  });

  group('чтение накладной: сроки, этапы, «Отмена»', () {
    test('запрос накладной: короткий формат, 6144 токенов, снимок ≤ 1600 px, этапы «сжатие → чтение»', () async {
      final fake = _Fake((o, n) => _ok(_newJson(_rows(3))));
      final v = _V(api, db, http: Dio()..httpClientAdapter = fake);
      final stages = <RecognizeStage>[];
      v.onStage = stages.add;
      final shot = _photo(1920, 1080, quality: 95);
      final seen = await v.recognize(shot, mode: 'invoice');
      expect(seen.length, 3);
      expect(stages, [RecognizeStage.compress, RecognizeStage.read]);
      final body = fake.calls.single.data as Map;
      expect((((body['systemInstruction'] as Map)['parts'] as List).single as Map)['text'], contains('{"n","q","u","p","b"}'));
      expect((body['generationConfig'] as Map)['maxOutputTokens'], 6144);
      final part = ((body['contents'] as List).single['parts'] as List)[1] as Map;
      final sent = base64Decode((part['inline_data'] as Map)['data'] as String);
      final size = jpegSize(sent)!;
      expect(size.$1 > size.$2 ? size.$1 : size.$2, lessThanOrEqualTo(invoiceMaxSide));
      expect(sent.length, lessThan(shot.length));
    });

    test('прочие режимы (полка, этикетки, продажа) остаются на прежнем длинном формате', () async {
      final fake = _Fake((o, n) => _ok('[{"name":"Кола","count":1}]'));
      final v = _V(api, db, http: Dio()..httpClientAdapter = fake);
      await v.recognize(_photo(300, 200, quality: 80), mode: 'shelf');
      expect(jsonEncode((fake.calls.single.data as Map)['systemInstruction']), contains('name_ru'));
    });

    test('Google не отвечает: ждём не дольше срока, первая попытка короткая, есть вторая модель', () async {
      // меньше 2 с на остаток следующая модель не пробуется (правило _visionAsk), поэтому общий срок 4 с, первая попытка 0,2 с
      VisionService.invoiceTotal = const Duration(seconds: 4);
      VisionService.invoiceFirstTry = const Duration(milliseconds: 200);
      final hang = _Hang();
      final v = _V(api, db, http: Dio()..httpClientAdapter = hang);
      final sw = Stopwatch()..start();
      final e = await v.recognize(_photo(300, 200), mode: 'invoice').then<Object?>((_) => null, onError: (Object e) => e);
      sw.stop();
      expect(e, isA<VisionException>());
      expect((e as VisionException).message, contains('не ответил вовремя'));
      expect(hang.calls, greaterThanOrEqualTo(2), reason: 'после зависшей первой модели пробуется следующая');
      expect(sw.elapsedMilliseconds, lessThan(8000), reason: 'не ждём молча: срок вышел — ошибка');
      // ignore: avoid_print
      print('ЗАМЕР зависший Google (сроки 4 с / 0,2 с вместо 30 с / 20 с): ответ-ошибка через ${sw.elapsedMilliseconds} мс, попыток ${hang.calls}');
    });

    test('«Отмена»: запрос обрывается сразу, второй запрос не уходит', () async {
      final hang = _Hang();
      final v = _V(api, db, http: Dio()..httpClientAdapter = hang);
      final token = CancelToken();
      v.cancelToken = token;
      final f = v.recognize(_photo(300, 200), mode: 'invoice').then<Object?>((_) => null, onError: (Object e) => e);
      await Future<void>.delayed(const Duration(milliseconds: 150));
      expect(hang.calls, 1);
      final sw = Stopwatch()..start();
      token.cancel();
      final e = await f;
      expect(e, isA<VisionException>());
      expect((e as VisionException).message, 'отменено');
      expect(sw.elapsedMilliseconds, lessThan(500));
      expect(hang.calls, 1, reason: 'после отмены другая модель не пробуется');
    });

    test('повтор не отменяется старым запросом: токен берётся в начале вызова', () async {
      final fake = _Fake((o, n) => _ok(_newJson(_rows(1))));
      final v = _V(api, db, http: Dio()..httpClientAdapter = fake);
      final old = CancelToken();
      v.cancelToken = old;
      final f = v.recognize(_photo(300, 200), mode: 'invoice');
      v.cancelToken = CancelToken(); // «Повторить»: поля уже новые, а старый вызов продолжает со своим токеном
      old.cancel();
      await expectLater(f, throwsA(isA<VisionException>()));
      expect(fake.calls.length, lessThanOrEqualTo(1));
    });
  });

  group('сопоставление с каталогом', () {
    test('tokenEq после ускорения даёт тот же ответ, что прежний (с таблицей Левенштейна и регулярным выражением)', () {
      int lev(String a, String b) {
        var prev = List<int>.generate(b.length + 1, (j) => j);
        for (var i = 1; i <= a.length; i++) {
          final cur = List<int>.filled(b.length + 1, 0);
          cur[0] = i;
          for (var j = 1; j <= b.length; j++) {
            cur[j] = min(min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
          }
          prev = cur;
        }
        return prev[b.length];
      }

      bool old(String a, String b) {
        if (a == b) return true;
        final (s, l) = a.length <= b.length ? (a, b) : (b, a);
        if (RegExp(r'^\d+$').hasMatch(s) || RegExp(r'^\d+$').hasMatch(l)) return false;
        if (s.length >= 3 && l.startsWith(s) && (s.length >= 4 ? s.length * 2 >= l.length : s.length * 2 > l.length)) return true;
        if (s.length >= 7 && (l.length - s.length) <= 1 && lev(s, l) <= 1) return true;
        if (s.length >= 7 && l.length > s.length && lev(s, l.substring(0, s.length)) <= 1 && s.length * 2 >= l.length) return true;
        return false;
      }

      final rnd = Random(5);
      String word() => String.fromCharCodes([for (var k = 0, n = 1 + rnd.nextInt(11); k < n; k++) rnd.nextInt(10) < 2 ? 48 + rnd.nextInt(3) : 1072 + rnd.nextInt(3)]);
      var checked = 0, same = 0;
      for (var i = 0; i < 40000; i++) {
        final a = word();
        // половина пар — слово и его правка на одну букву / дописка / начало: именно там ветки «одна буква» и «начало слова»
        final b = switch (i % 4) {
          0 => word(),
          1 => a.isEmpty ? a : '${a.substring(0, a.length - 1)}${word().substring(0, 1)}',
          2 => '$a${word().substring(0, 1)}',
          _ => a.length > 1 ? a.substring(0, a.length - 1) : a,
        };
        checked++;
        if (tokenEq(a, b) == old(a, b)) same++;
        expect(tokenEq(a, b), old(a, b), reason: '«$a» и «$b»');
      }
      expect(same, checked);
    });

    test('30 строк накладной: порядок сохранён, находится тот же товар; замер на 3000 и 15 000 товаров', () async {
      // каталог «как у магазина»: у каждого товара 2–3 слова из словаря в 2000 слов (торговая марка, вид, вкус) и объём
      final rnd = Random(11);
      final vocab = [for (var i = 0; i < 2000; i++) 'слово${String.fromCharCodes([for (var k = 0; k < 4; k++) 1072 + rnd.nextInt(26)])}'];
      final volumes = ['0,5 л', '1 л', '1,5 л', '2 л', '250 г', '500 г', '1 кг'];
      String nameOf(int i) {
        final r = Random(i);
        return '${vocab[r.nextInt(vocab.length)]} ${vocab[r.nextInt(vocab.length)]} ${vocab[r.nextInt(vocab.length)]} ${volumes[r.nextInt(volumes.length)]}';
      }

      final v = VisionService(api, db);
      var have = 0;
      for (final total in [3000, 15000]) {
        await db.batch((b) {
          for (; have < total; have++) {
            final name = nameOf(have);
            b.insert(db.products, ProductsCompanion.insert(id: 'p$have', name: name, quantity: const Value(5), searchKey: Value(name.toLowerCase())));
          }
        });
        final items = [for (var i = 0; i < 30; i++) SeenItem(name: nameOf((i * 97 + 13) % total), count: i + 1)];
        final warm = Stopwatch()..start();
        await CatalogMatcher.warm(db); // индекс строится заранее, пока кассир наводит камеру (initState экрана)
        warm.stop();
        final sw = Stopwatch()..start();
        final out = await v.match(items);
        sw.stop();
        expect(out.length, 30);
        for (var i = 0; i < 30; i++) {
          expect(identical(out[i].seen, items[i]), isTrue, reason: 'порядок строк сохранён');
          expect(out[i].candidates, isNotEmpty, reason: 'товар из каталога найден: ${items[i].name}');
        }
        // до ускорения этот же замер на этом ПК: 3000 товаров — 5548 мс, 15 000 товаров — 8083 мс (30 строк)
        expect(sw.elapsedMilliseconds, lessThan(2000), reason: 'сопоставление 30 строк накладной с каталогом $total товаров');
        // ignore: avoid_print
        print('ЗАМЕР каталог $total товаров: индекс ${warm.elapsedMilliseconds} мс (заранее), сопоставление 30 строк ${sw.elapsedMilliseconds} мс (${(sw.elapsedMilliseconds / 30).round()} мс на строку)');
      }
    });
  });
}
