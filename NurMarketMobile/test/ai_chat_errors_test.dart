// 2026-10-27, владелец: «в мобилке ИИ не отвечает в переписке». Расследование Opus нашло, что чат врёт или держит ошибку долго:
// (1) любой 403 без слова model выключал ключ на 30 минут → «Добавьте ключ ИИ…»; (2) умная камера и звонок помечали облачный
// ключ плохим НАВСЕГДА; (3) при одном ключе и кончившемся лимите чат советовал «через минуту»; (4) индикатор мог крутиться минуты;
// (5) ошибки нигде не записывались. Эти тесты — на фейках (без сети, Google и боевого сервера).
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/gemini_keys.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
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

  List<String> get keys => [for (final c in calls) '${c.headers['x-goog-api-key']}'];
}

/// Google не отвечает вовсе (обрыв связи, зависший запрос).
class _Hang implements HttpClientAdapter {
  int calls = 0;
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) {
    calls++;
    return Completer<ResponseBody>().future;
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(int status, Object body) =>
    ResponseBody.fromString(jsonEncode(body), status, headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});

Map<String, dynamic> _ok(String t) => {
  'candidates': [
    {
      'content': {
        'role': 'model',
        'parts': [
          {'text': t},
        ],
      },
      'finishReason': 'STOP',
    },
  ],
};

const _denied403 = {
  'error': {'code': 403, 'message': 'The caller does not have permission', 'status': 'PERMISSION_DENIED'},
};
const _invalid400 = {
  'error': {'code': 400, 'message': 'API key not valid. Please pass a valid API key.', 'status': 'INVALID_ARGUMENT'},
};
const _unauth401 = {
  'error': {'code': 401, 'message': 'Request had invalid authentication credentials.', 'status': 'UNAUTHENTICATED'},
};
const _perDay429 = {
  'error': {
    'code': 429,
    'message': 'You exceeded your current quota.',
    'status': 'RESOURCE_EXHAUSTED',
    'details': [
      {
        '@type': 'type.googleapis.com/google.rpc.QuotaFailure',
        'violations': [
          {'quotaId': 'GenerateRequestsPerDayPerProjectPerModel-FreeTier'},
        ],
      },
    ],
  },
};

class _V extends VisionService {
  Map<String, dynamic>? cloud;
  _V(super.api, super.db, this.cloud, {super.http});
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async => cloud == null ? null : Map.of(cloud!);
  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async => const AiAccessPolicy.unset();
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _ProfileApi extends ApiClient {
  _ProfileApi(Session s) : super(SessionStore()) {
    attach(s);
  }
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {
    'user': {'id': 'u-777', 'role': 'owner', 'is_owner': true},
  };
}

const _h = [(user: true, text: 'Какая выручка сегодня?')];
final _png = Uint8List.fromList([0xFF, 0xD8, 1, 2, 3]);

Future<Object?> _err(Future<Object?> f) => f.then<Object?>((_) => null, onError: (Object e) => e);

void main() {
  late AppDatabase db;
  late ApiClient api;
  final l = lookupAppLocalizations(const Locale('ru'));
  var now = DateTime(2026, 10, 27, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
    AiJournal.resetQueueForTests(); // запись журнала из виджет-теста (часы подделаны) могла зависнуть — очередь не тянем в другие тесты
    now = DateTime(2026, 10, 27, 12);
    GeminiKeyPool.clock = () => now;
    api = ApiClient(SessionStore())
      ..attach(const Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'owner', companyName: 'NBS', companyInn: '1', role: 'owner', isOwner: true));
  });
  tearDown(() async {
    GeminiKeyPool.clock = DateTime.now;
    AiJournal.testDirectory = null;
    await db.close();
  });

  (_V, _Fake) make(Map<String, dynamic>? cloud, ResponseBody Function(RequestOptions o, int n) h) {
    final fake = _Fake(h);
    return (_V(api, db, cloud, http: Dio()..httpClientAdapter = fake), fake);
  }

  Map<String, dynamic> twoKeys() => {
    'TelegramAiKey': 'AIzaAAAA1111',
    'TelegramAiKeysExtra': jsonEncode({
      'v': 1,
      'mainLabel': '',
      'mainEnabled': true,
      'extra': [
        {'label': '', 'key': 'AIzaBBBB2222', 'enabled': true},
      ],
    }),
  };

  group('правило «ключ недействителен» — только явная причина', () {
    test('таблица: 401 и явные тексты — да; голый 403, 400 про регион, 403 про модель — нет', () {
      expect(GeminiKeyPool.isInvalidKey(401, '{}'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(400, 'API key not valid. Please pass a valid API key.'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(400, '{"error":{"details":[{"reason":"API_KEY_INVALID"}]}}'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(400, 'API key expired. Please renew the API key.'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(403, 'Your API key was reported as leaked. Please use another API key.'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(403, 'PERMISSION_DENIED: Consumer \'api_key:AIza\' has been suspended.'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(403, '{"status":"PERMISSION_DENIED","message":"API key not found."}'), isTrue);
      expect(GeminiKeyPool.isInvalidKey(403, jsonEncode(_denied403)), isFalse, reason: '«The caller does not have permission» — не вина ключа');
      expect(GeminiKeyPool.isInvalidKey(403, 'PERMISSION_DENIED'), isFalse);
      expect(GeminiKeyPool.isInvalidKey(403, 'Your project does not have access to model gemini-x'), isFalse);
      expect(GeminiKeyPool.isInvalidKey(400, 'User location is not supported for the API use.'), isFalse);
      expect(GeminiKeyPool.isInvalidKey(500, 'API key not valid'), isFalse);
    });

    test('403 без причины (один ключ): ключ не помечается, пробуются все модели, чат отвечает ошибкой Google, а не «Добавьте ключ»', () async {
      final (v, fake) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(403, _denied403));
      final e = await _err(v.chat('s', _h)) as VisionException;
      expect(fake.calls.length, 5, reason: 'по одному запросу на каждую из 5 моделей');
      expect(GeminiKeyPool.isInvalid('AIza-one'), isFalse);
      expect(e.noKey, isFalse);
      expect(e.issue, isNull);
      expect(advisorErrorText(l, e), contains('ошибка Google 403'));
      expect(await v.hasChatKey(), isTrue);
      // следующий вопрос снова уходит в Google (раньше 30 минут писал «Добавьте ключ» без запроса)
      await _err(v.chat('s', _h));
      expect(fake.calls.length, 10);
    });

    test('403 без причины у первого ключа: на той же модели пробуется второй, первый не помечается', () async {
      final (v, fake) = make(twoKeys(), (o, n) => o.headers['x-goog-api-key'] == 'AIzaAAAA1111' ? _json(403, _denied403) : _json(200, _ok('ответ')));
      expect(await v.chat('s', _h), 'ответ');
      expect(fake.keys, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      expect(GeminiKeyPool.isInvalid('AIzaAAAA1111'), isFalse);
    });

    test('явное «API key not valid»: ключ помечается на 30 минут, берётся следующий; через 31 минуту пробуется снова', () async {
      final (v, fake) = make(twoKeys(), (o, n) => o.headers['x-goog-api-key'] == 'AIzaAAAA1111' ? _json(400, _invalid400) : _json(200, _ok('ответ')));
      expect(await v.chat('s', _h), 'ответ');
      expect(fake.keys, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      expect(GeminiKeyPool.isInvalid('AIzaAAAA1111'), isTrue);
      now = now.add(const Duration(minutes: 31));
      expect(GeminiKeyPool.isInvalid('AIzaAAAA1111'), isFalse);
    });
  });

  group('умная камера и звонок не помечают общий ключ «навсегда»', () {
    test('камера получила явный отказ ключа: noKey, но ключ не стёрт и после перезапуска снова берётся', () async {
      final (v, _) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(400, _invalid400));
      final e = await _err(v.recognize(_png));
      expect(e, isA<VisionException>());
      expect((e as VisionException).noKey, isTrue);
      expect(GeminiKeyPool.isInvalid('AIza-one'), isTrue, reason: 'временная пометка на 30 минут — да');
      expect(await const FlutterSecureStorage().read(key: 'ai.gemini.bad'), isNull, reason: '«навсегда» (хранилище телефона) — нет');
      VisionService.resetForTest(); // перезапуск приложения
      final (v2, _) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(200, _ok('ответ')));
      expect(await v2.key(), 'AIza-one');
      expect(await v2.chat('s', _h), 'ответ');
    });

    test('камера получила 403 без причины: это не «ключ не подошёл» (noKey=false), ключ не помечен', () async {
      final (v, _) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(403, _denied403));
      final e = await _err(v.recognize(_png)) as VisionException;
      expect(e.noKey, isFalse);
      expect(GeminiKeyPool.isInvalid('AIza-one'), isFalse);
      expect(await v.key(), 'AIza-one');
    });

    test('saveKey("") (старый сигнал «ключ не подошёл») — только 30 минут, не навсегда', () async {
      final (v, _) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(200, _ok('ответ')));
      expect(await v.key(), 'AIza-one');
      await v.saveKey('');
      expect(await v.key(), isNull);
      now = now.add(const Duration(minutes: 31));
      expect(await v.key(), 'AIza-one');
      expect(await const FlutterSecureStorage().read(key: 'ai.gemini.bad'), isNull);
    });

    test('звонок: явное «API key expired» — временная пометка; «PERMISSION_DENIED … no permission» ключ не трогает', () async {
      final (v, _) = make(twoKeys(), (o, n) => _json(200, _ok('ответ')));
      await v.key();
      expect(v.rotateLiveKey('AIzaAAAA1111', 'The caller does not have permission (PERMISSION_DENIED)'), isNull);
      expect(GeminiKeyPool.isInvalid('AIzaAAAA1111'), isFalse);
      expect(v.rotateLiveKey('AIzaAAAA1111', 'API key expired. Please renew the API key.'), 'AIzaBBBB2222');
      expect(GeminiKeyPool.isInvalid('AIzaAAAA1111'), isTrue);
      VisionService.resetForTest(); // перезапуск: «навсегда» не осталось
      final (v2, _) = make(twoKeys(), (o, n) => _json(200, _ok('ответ')));
      expect(await v2.key(), 'AIzaAAAA1111');
      expect(await const FlutterSecureStorage().read(key: 'ai.gemini.bad'), isNull);
    });
  });

  group('понятные сообщения вместо «попробуйте через минуту» и «Добавьте ключ»', () {
    test('один ключ, суточный лимит: «Лимит ИИ исчерпан до HH:mm (общий с кассой и ботом)»; повтор без запросов', () async {
      final (v, fake) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(429, _perDay429));
      final e1 = await _err(v.chat('s', _h)) as VisionException;
      expect(e1.issue, VisionIssue.quota);
      expect(e1.until, isNotNull);
      final text = advisorErrorText(l, e1);
      expect(text, l.advisorQuota(VisionService.hm(e1.until!)));
      expect(text, contains('Лимит ИИ исчерпан до'));
      expect(text, contains('общий с кассой и ботом'));
      expect(text, isNot(contains('через минуту')));
      expect(e1.showSettings, isTrue);
      final n1 = fake.calls.length;
      now = now.add(const Duration(minutes: 30));
      final e2 = await _err(v.chat('s', _h)) as VisionException;
      expect(fake.calls.length, n1, reason: 'лимит до полуночи по Тихому океану — Google не дёргаем');
      expect(e2.issue, VisionIssue.quota);
      expect(advisorErrorText(l, e2), contains('Лимит ИИ исчерпан до'));
    });

    test('ключ есть, но Google его отклонил (401): «Google отклонил ключ, повтор после HH:mm» + кнопка настроек, а не «Добавьте ключ ИИ»', () async {
      final (v, fake) = make({'TelegramAiKey': 'AIza-one'}, (o, n) => _json(401, _unauth401));
      final e1 = await _err(v.chat('s', _h)) as VisionException;
      expect(e1.issue, VisionIssue.keyRejected);
      expect(e1.showSettings, isTrue);
      expect(advisorErrorText(l, e1), startsWith('Google отклонил ключ, повтор после '));
      final n1 = fake.calls.length;
      // второй вопрос: экран спрашивает hasChatKey() → false → noKeyFailure()
      expect(await v.hasChatKey(), isFalse);
      final e2 = v.noKeyFailure();
      expect(e2.issue, VisionIssue.keyRejected);
      expect(e2.until, e1.until);
      final text = advisorErrorText(l, e2);
      expect(text, l.advisorKeyRejected(VisionService.hm(e2.until!)));
      expect(text, isNot(contains('Добавьте ключ')));
      expect(e2.showSettings, isTrue);
      expect(fake.calls.length, n1);
      now = now.add(const Duration(minutes: 31));
      expect(await v.hasChatKey(), isTrue);
    });

    test('ключей нет совсем — прежнее «Добавьте ключ ИИ…»', () async {
      final (v, _) = make({}, (o, n) => _json(200, _ok('ответ')));
      expect(await v.hasChatKey(), isFalse);
      final e = v.noKeyFailure();
      expect(e.noKey, isTrue);
      expect(e.issue, isNull);
      expect(advisorErrorText(l, e), l.advisorNoKey);
    });

    test('тексты есть на всех 5 языках и не пустые', () {
      for (final code in ['ru', 'ky', 'en', 'tr', 'uz']) {
        final x = lookupAppLocalizations(Locale(code));
        for (final s in [x.advisorQuota('14:05'), x.advisorQuotaLater, x.advisorKeyRejected('14:05'), x.advisorTimeout, x.gkLastError('403'), x.aiAccOwnerNote]) {
          expect(s.trim(), isNotEmpty, reason: code);
        }
        expect(x.advisorQuota('14:05'), contains('14:05'), reason: code);
        expect(x.advisorKeyRejected('14:05'), contains('14:05'), reason: code);
      }
    });
  });

  testWidgets('общий срок ответа чата — 45 с: потом «ИИ долго не отвечает» (VisionIssue.timeout), а не вечное ожидание', (t) async {
    final hang = _Hang();
    final v = _V(api, db, {'TelegramAiKey': 'AIza-one'}, http: Dio()..httpClientAdapter = hang);
    Object? result;
    var done = false;
    unawaited(
      v.chat('s', _h).then<void>(
        (_) => done = true,
        onError: (Object e) {
          result = e;
          done = true;
        },
      ),
    );
    await t.pump(const Duration(seconds: 40));
    expect(done, isFalse, reason: 'до 45 с ещё ждём');
    await t.pump(const Duration(seconds: 6));
    expect(done, isTrue);
    expect(result, isA<VisionException>());
    expect((result as VisionException).issue, VisionIssue.timeout);
    expect(advisorErrorText(l, result!), l.advisorTimeout);
    expect(hang.calls, greaterThan(0));
    expect(VisionService.lastChatError?.code, 'таймаут');
    // после срока фоновая работа запросов больше не шлёт (лимит общий с кассой и ботом) и не оставляет таймеров
    final sent = hang.calls;
    await t.pump(const Duration(seconds: 40));
    expect(hang.calls, sent);
  });

  group('последняя ошибка Советника — в журнале ИИ', () {
    test('403: код, модель и хвост ключа пишутся; сам ключ — нет; читается обратно из файла', () async {
      final dir = Directory.systemTemp.createTempSync('ai_last_error_test');
      addTearDown(() => dir.deleteSync(recursive: true));
      AiJournal.testDirectory = dir;
      final (v, _) = make({'TelegramAiKey': 'AIzaSECRET-KEY-1234'}, (o, n) => _json(403, _denied403));
      await _err(v.chat('s', _h));
      final last = VisionService.lastChatError!;
      expect(last.code, '403');
      expect(last.model, isNotEmpty);
      expect(last.keyTail, '1234');
      expect(last.line, isNot(contains('SECRET')));
      expect(last.line, contains('403'));
      final fromFile = await AiJournal.readChatError();
      expect(fromFile, isNotNull);
      expect(fromFile!.code, '403');
      expect(fromFile.keyTail, '1234');
      expect(File('${dir.path}/ai_last_error.json').readAsStringSync(), isNot(contains('SECRET')));
    });
  });

  group('владелец и администратор всегда имеют ИИ', () {
    const owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'OWN', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'NBS', companyInn: '1', perms: []);
    const admin = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'ADM', userName: 'Админ', role: 'admin', companyName: 'NBS', companyInn: '1', perms: []);
    const cashier = Session(email: 'c@x', access: 'a', refresh: 'r', userId: 'u-c', userName: 'Анна', role: 'cashier', companyName: 'NBS', companyInn: '1', perms: []);

    test('владелец и администратор вне списка из двух сотрудников — доступ есть, кассир вне списка — нет', () {
      final p = AiAccessPolicy.set(const [AiAccessUser('u-1', 'Один'), AiAccessUser('u-2', 'Два')]);
      expect(AiAccess.canUse(owner, p), isTrue);
      expect(AiAccess.canUse(admin, p), isTrue);
      expect(AiAccess.canUse(cashier, p), isFalse);
      expect(AiAccess.canUse(owner, AiAccessPolicy.set(const [])), isTrue, reason: 'даже «ИИ выключен для всех» не закрывает владельца');
      expect(AiAccess.canUse(cashier, AiAccessPolicy.set(const [])), isFalse);
    });

    test('refreshProfile подгружает номер пользователя, если при входе профиль не загрузился', () async {
      FlutterSecureStorage.setMockInitialValues({});
      const noId = Session(email: 'o@x', access: 'a', refresh: 'r', userId: '', userName: 'o@x', role: '', companyName: 'NBS', companyInn: '1', perms: []);
      final c = ProviderContainer(
        overrides: [
          apiProvider.overrideWithValue(_ProfileApi(noId)),
          sessionProvider.overrideWith(() => _Fixed(noId)),
        ],
      );
      addTearDown(c.dispose);
      await c.read(sessionProvider.notifier).refreshProfile();
      expect(c.read(sessionProvider)!.userId, 'u-777');
      expect(c.read(sessionProvider)!.isOwner, isTrue);
    });
  });
}
