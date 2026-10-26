// 2026-10-22 (советник мобилки): Нур Советник на телефоне — разбор ответа Gemini и запасные модели, на фейках (без сети):
// части «thought» не показываются, пустой ответ, 404 модели → следующая, неподходящий ключ, нет ключа → понятное сообщение.
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/advisor.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

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

ResponseBody _json(int status, Object body) =>
    ResponseBody.fromString(jsonEncode(body), status, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });

Map<String, dynamic> _answer(List<Map<String, dynamic>> parts, {String finish = 'STOP'}) => {
  'candidates': [
    {
      'content': {'role': 'model', 'parts': parts},
      'finishReason': finish,
    },
  ],
};

class _TestVision extends VisionService {
  final String? k;
  _TestVision(super.api, super.db, this.k, {super.http});
  @override
  Future<String?> key() async => k;
  @override
  Future<({String? groq, String? openRouter})> fallbackKeys() async => (groq: null, openRouter: null);
}

/// 2026-10-22 (аудит С-1…С-3): облако подменено — без сети; хранилище телефона — из setMockInitialValues.
class _CloudVision extends VisionService {
  Map<String, dynamic>? cloud;
  int calls = 0;
  _CloudVision(super.api, super.db, this.cloud);
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async {
    calls++;
    return cloud;
  }

  // 2026-10-26 (ИИ — не больше 2 сотрудникам): правило «кому разрешён ИИ» тоже без сети — список не задан, ИИ у всех, как раньше.
  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async => const AiAccessPolicy.unset();
}

void main() {
  late AppDatabase db;
  final l = lookupAppLocalizations(const Locale('ru'));
  const history = [(user: true, text: 'Какая выручка сегодня?')];

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
  });
  ApiClient apiFor(String userId) =>
      ApiClient(SessionStore())..attach(Session(email: '$userId@x', access: 'a', refresh: 'r', userId: userId, companyName: 'NBS', companyInn: '1'));
  tearDown(() => db.close());

  (_TestVision, _Fake) make(String? key, ResponseBody Function(RequestOptions o, int n) handler) {
    final fake = _Fake(handler);
    final dio = Dio()..httpClientAdapter = fake;
    return (_TestVision(ApiClient(SessionStore()), db, key, http: dio), fake);
  }

  test('разбор ответа: части thought пропускаются, пустой и без candidates — null', () {
    expect(
      VisionService.answerText(
        _answer([
          {'text': 'Считаю выручку…', 'thought': true},
          {'text': 'Выручка сегодня 12 500 сом.', 'thoughtSignature': 'abc'},
        ]),
      ),
      'Выручка сегодня 12 500 сом.',
    );
    expect(
      VisionService.answerText(
        _answer([
          {'text': 'только размышления', 'thought': true},
        ]),
      ),
      isNull,
    );
    expect(VisionService.answerText({'candidates': []}), isNull);
    expect(VisionService.answerText({'promptFeedback': {'blockReason': 'SAFETY'}}), isNull);
  });

  test('404 модели → следующая; запрос как на ПК: x-goog-api-key, лимит 4096, thinkingLevel low', () async {
    final (vision, fake) = make('test-key', (o, n) {
      if (n == 1) return _json(404, {'error': {'code': 404, 'message': 'models/gemini-3.5-flash-lite is not found', 'status': 'NOT_FOUND'}});
      return _json(200, _answer([
        {'text': 'думаю', 'thought': true},
        {'text': 'Выручка 12 500 сом'},
      ]));
    });
    expect(await vision.chat('система', history), 'Выручка 12 500 сом');
    expect(fake.calls.length, 2);
    expect(fake.calls[0].uri.path, contains('gemini-3.5-flash-lite:generateContent'));
    expect(fake.calls[1].uri.path, contains('gemini-flash-lite-latest:generateContent'));
    final first = fake.calls[0];
    expect(first.uri.queryParameters.containsKey('key'), isFalse);
    expect(first.headers['x-goog-api-key'], 'test-key');
    final cfg = (first.data as Map)['generationConfig'] as Map;
    expect(cfg['maxOutputTokens'], 4096);
    expect(cfg['thinkingConfig'], {'thinkingLevel': 'low'});
  });

  test('модель не принимает настройку размышлений (400) — тот же запрос без неё', () async {
    final (vision, fake) = make('test-key', (o, n) {
      final cfg = (o.data as Map)['generationConfig'] as Map;
      if (cfg.containsKey('thinkingConfig')) return _json(400, {'error': {'code': 400, 'message': 'Request contains an invalid argument.'}});
      return _json(200, _answer([
        {'text': 'ок'},
      ]));
    });
    expect(await vision.chat('система', history), 'ок');
    expect(fake.calls.length, 2);
    expect(fake.calls[1].uri.path, fake.calls[0].uri.path);
  });

  test('пустой ответ всех моделей и страница ошибки вместо JSON — понятная ошибка, а не падение', () async {
    final (vision, _) = make('test-key', (o, n) {
      if (n == 1) {
        return ResponseBody.fromString('<html>502 Bad Gateway</html>', 502, headers: {
          Headers.contentTypeHeader: ['text/html'],
        });
      }
      return _json(200, _answer(const [], finish: 'MAX_TOKENS'));
    });
    final e = await vision.chat('система', history).then<Object?>((_) => null, onError: (Object e) => e);
    expect(e, isA<VisionException>());
    expect((e as VisionException).noKey, isFalse);
    expect(e.message, contains('пустой ответ'));
    expect(advisorErrorText(l, e), l.advisorError(e.message));
  });

  test('неподходящий ключ — сразу ошибка про ключ, без перебора моделей', () async {
    final (vision, fake) = make('bad-key', (o, n) => _json(400, {'error': {'code': 400, 'message': 'API key not valid. Please pass a valid API key.', 'status': 'INVALID_ARGUMENT'}}));
    final e = await vision.chat('система', history).then<Object?>((_) => null, onError: (Object e) => e);
    expect(fake.calls.length, 1);
    expect((e as VisionException).message, contains('ключ Google не подходит'));
  });

  test('нет ни одного ключа — «Добавьте ключ ИИ в программе владельца…»', () async {
    final (vision, fake) = make(null, (o, n) => _json(500, const {}));
    expect(await vision.hasChatKey(), isFalse);
    final e = await vision.chat('система', history).then<Object?>((_) => null, onError: (Object e) => e);
    expect(fake.calls, isEmpty);
    expect((e as VisionException).noKey, isTrue);
    expect(advisorErrorText(l, e), l.advisorNoKey);
    expect(l.advisorNoKey, contains('Модели ИИ и ключи'));
  });

  test('С-1: кассир без своего ключа берёт ключ владельца, сохранённый на этом телефоне (как ключ на ПК)', () async {
    final owner = _CloudVision(apiFor('owner'), db, {'TelegramAiKey': 'AQ.owner-key'});
    expect(await owner.key(), 'AQ.owner-key');
    final cashier = _CloudVision(apiFor('cashier'), db, {});
    expect(await cashier.key(), 'AQ.owner-key');
  });

  test('С-2: неподошедший облачный ключ больше не берётся, пока его не сменят; вставленный вручную — выше', () async {
    final v = _CloudVision(apiFor('owner'), db, {'TelegramAiKey': 'AQ.bad'});
    expect(await v.key(), 'AQ.bad');
    await v.saveKey(''); // так умная камера сообщает «ключ не подошёл»
    expect(await v.key(), isNull);
    await v.saveKey('AQ.manual');
    expect(await v.key(), 'AQ.manual');
    VisionService.resetForTest(); // перезапуск: «плохой» помнится в хранилище телефона
    final again = _CloudVision(apiFor('owner'), db, {'TelegramAiKey': 'AQ.bad'});
    expect(await again.key(), 'AQ.manual');
    VisionService.resetForTest();
    expect(await _CloudVision(apiFor('owner'), db, {'TelegramAiKey': 'AQ.new'}).key(), 'AQ.new');
  });

  test('С-3: «ключа нет» помнится — второй вызов не ждёт облако', () async {
    final v = _CloudVision(apiFor('cashier'), db, null);
    expect(await v.key(), isNull);
    expect(await v.key(), isNull);
    expect(v.calls, 1);
  });

  test('история: реплики одной стороны склеиваются, разговор начинается с вопроса', () async {
    final (vision, fake) = make('test-key', (o, n) => _json(200, _answer([
      {'text': 'ок'},
    ])));
    await vision.chat('система', const [(user: false, text: 'старый ответ'), (user: true, text: 'вопрос 1'), (user: true, text: 'вопрос 2')]);
    final contents = (fake.calls.single.data as Map)['contents'] as List;
    expect(contents.length, 1);
    expect(contents.single['role'], 'user');
    expect(((contents.single['parts'] as List).single as Map)['text'], 'вопрос 1\n\nвопрос 2');
  });

  test('коды должников [Д1] → имя и телефон только на телефоне; «витамин Д3» не трогается', () {
    expect(revealDebtorCodes('Напомните [Д1] и [Д2]; витамин Д3; [Д9]', {1: 'Айбек (тел. 0555)', 2: 'Нурлан'}), 'Напомните Айбек (тел. 0555) и Нурлан; витамин Д3; [Д9]');
  });
}
