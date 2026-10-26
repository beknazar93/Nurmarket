// 2026-10-27, владелец: «в мобилке дополнительного ключа нет — добавь! и смена ключа не работает!»
// (А) список ключей Gemini: формат хранения (общий с кассой), «первый рабочий», ротация при лимите и недействительном ключе, звонок;
// (Б) смена ключа: замена → следующий запрос уходит с новым ключом, облако не затирает свежую замену, «плохой» ключ снимается
// при замене, вставка с пробелами и переводами строк очищается. Всё на фейках — без сети, Google и боевого сервера.
import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/gemini_keys.dart';
import 'package:nurmarket/core/ai/live_call.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/advisor.dart' show AiSettingsScreen;
import 'package:nurmarket/features/admin/gemini_keys_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fake implements HttpClientAdapter {
  final ResponseBody Function(RequestOptions o, int n) handler;
  final calls = <RequestOptions>[];
  _Fake(this.handler);
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    calls.add(options);
    return handler(options, calls.length);
  }

  @override
  void close({bool force = false}) {}

  /// Ключи, с которыми уходили запросы (по порядку).
  List<String> get keys => [
    for (final c in calls) '${c.headers['x-goog-api-key']}',
  ];
}

ResponseBody _json(int status, Object body) => ResponseBody.fromString(
  jsonEncode(body),
  status,
  headers: {
    Headers.contentTypeHeader: [Headers.jsonContentType],
  },
);

Map<String, dynamic> _ok(String text) => {
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
};

Map<String, dynamic> _quota(String retry) => {
  'error': {
    'code': 429,
    'message': 'You exceeded your current quota',
    'status': 'RESOURCE_EXHAUSTED',
    'details': [
      {
        '@type': 'type.googleapis.com/google.rpc.RetryInfo',
        'retryDelay': retry,
      },
    ],
  },
};

const _invalidKey = {
  'error': {
    'code': 400,
    'message': 'API key not valid. Please pass a valid API key.',
    'status': 'INVALID_ARGUMENT',
  },
};

/// Облако и право ИИ подменены (без сети). cloud — «секреты личных настроек NurCRM»; pushSecrets пишет в них, как сервер.
class _V extends VisionService {
  Map<String, dynamic>? cloud;
  bool failPush = false;
  final pushes = <Map<String, Object?>>[];
  _V(super.api, super.db, this.cloud, {super.http});
  @override
  Future<Map<String, dynamic>?> cloudSecrets() async =>
      cloud == null ? null : Map.of(cloud!);
  @override
  Future<void> pushSecrets(Map<String, Object?> secrets) async {
    if (failPush) throw ApiException('нет связи с сервером');
    pushes.add(Map.of(secrets));
    secrets.forEach((k, v) {
      if (v == null) {
        cloud?.remove(k);
      } else {
        cloud?[k] = v;
      }
    });
  }

  @override
  Future<AiAccessPolicy> accessPolicy({bool force = false}) async =>
      const AiAccessPolicy.unset();
}

const _history = [(user: true, text: 'Какая выручка сегодня?')];

void main() {
  late AppDatabase db;
  late ApiClient api;
  var now = DateTime(2026, 10, 27, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest();
    now = DateTime(2026, 10, 27, 12);
    GeminiKeyPool.clock = () => now;
    api = ApiClient(SessionStore())
      ..attach(
        Session(
          email: 'o@x',
          access: 'a',
          refresh: 'r',
          userId: 'owner',
          companyName: 'NBS',
          companyInn: '1',
        ),
      );
  });
  tearDown(() async {
    GeminiKeyPool.clock = DateTime.now;
    await db.close();
  });

  (_V, _Fake) make(
    Map<String, dynamic>? cloud,
    ResponseBody Function(RequestOptions o, int n) handler,
  ) {
    final fake = _Fake(handler);
    return (_V(api, db, cloud, http: Dio()..httpClientAdapter = fake), fake);
  }

  ResponseBody answer(RequestOptions o, int n) => _json(200, _ok('ответ'));

  // ------------------------------------------------------------------------------------------------ формат хранения
  group('формат списка (общий с кассой)', () {
    test('основной ключ — как раньше (TelegramAiKey), остальные — TelegramAiKeysExtra: строка JSON v:1', () {
      final (main, extra) = GeminiKeys.encode([
        const GeminiKeyEntry(label: 'Аккаунт 1', key: 'AIzaAAAA1111'),
        const GeminiKeyEntry(
          label: 'Аккаунт 2',
          key: 'AIzaBBBB2222',
          enabled: false,
        ),
      ]);
      expect(main, 'AIzaAAAA1111');
      expect(jsonDecode(extra!), {
        'v': 1,
        'mainLabel': 'Аккаунт 1',
        'mainEnabled': true,
        'extra': [
          {'label': 'Аккаунт 2', 'key': 'AIzaBBBB2222', 'enabled': false},
        ],
      });
    });

    test('один ключ без метки — extra не нужен (в облаке только TelegramAiKey, как у старых версий)', () {
      expect(GeminiKeys.encode([const GeminiKeyEntry(key: 'AIzaONLY0001')]), (
        'AIzaONLY0001',
        null,
      ));
      expect(GeminiKeys.encode(const []), (null, null));
    });

    test('строка кассы читается: порядок «основной, затем extra», повторы и пустые отброшены', () {
      final list = GeminiKeys.parse(
        'AIzaAAAA1111',
        '{"v":1,"mainLabel":"Аккаунт 1","mainEnabled":true,"extra":[{"label":"Аккаунт 2","key":"AIzaBBBB2222","enabled":true},'
            '{"label":"Копия","key":"AIzaAAAA1111"},{"label":"Пусто","key":"  "},{"label":"Аккаунт 3","key":"AIzaCCCC3333","enabled":false}]}',
      );
      expect(
        [for (final e in list) e.key],
        ['AIzaAAAA1111', 'AIzaBBBB2222', 'AIzaCCCC3333'],
      );
      expect(list.first.label, 'Аккаунт 1');
      expect(list.last.enabled, isFalse);
    });

    test('битый JSON — остаётся основной ключ; старая версия (нет extra) — один ключ', () {
      expect(
        [for (final e in GeminiKeys.parse('AIzaAAAA1111', '{не json')) e.key],
        ['AIzaAAAA1111'],
      );
      expect(
        [for (final e in GeminiKeys.parse('AIzaAAAA1111', null)) e.key],
        ['AIzaAAAA1111'],
      );
    });

    test('не больше 10 ключей', () {
      final many = [
        for (var i = 0; i < 14; i++)
          GeminiKeyEntry(key: 'AIzaKEY${i.toString().padLeft(6, '0')}'),
      ];
      expect(GeminiKeys.normalize(many).length, 10);
      final (main, extra) = GeminiKeys.encode(many);
      expect(GeminiKeys.parse(main, extra).length, 10);
    });

    test('вставка с пробелами, переводами строк, кавычками и невидимыми символами очищается', () {
      expect(GeminiKeys.clean(' AIza\nABC 123 \n'), 'AIzaABC123');
      expect(GeminiKeys.clean('"AIzaABC123"\r\n'), 'AIzaABC123');
      expect(GeminiKeys.clean('​AIza​ABC123﻿'), 'AIzaABC123');
      expect(GeminiKeys.clean(null), '');
    });
  });

  // ------------------------------------------------------------------------------------------------ лимит и время
  group('лимит: до какого времени ключ исчерпан', () {
    test('минутный: retryDelay из ответа (+1 с), без него — 60 с', () {
      final t = DateTime(2026, 10, 27, 12);
      expect(
        GeminiKeyPool.quotaUntil(jsonEncode(_quota('30s')), t),
        t.add(const Duration(seconds: 31)),
      );
      expect(
        GeminiKeyPool.quotaUntil(
          '{"error":{"status":"RESOURCE_EXHAUSTED"}}',
          t,
        ),
        t.add(const Duration(seconds: 60)),
      );
    });

    test('суточный (PerDay) — до 00:00 по тихоокеанскому времени, с переходом на зимнее время', () {
      DateTime pac(DateTime utc) =>
          GeminiKeyPool.nextPacificMidnight(utc).toUtc();
      expect(
        pac(DateTime.utc(2026, 10, 27, 12)),
        DateTime.utc(2026, 10, 28, 7),
      ); // PDT, −7
      expect(
        pac(DateTime.utc(2026, 12, 15, 12)),
        DateTime.utc(2026, 12, 16, 8),
      ); // PST, −8
      expect(
        pac(DateTime.utc(2026, 11, 1, 5)),
        DateTime.utc(2026, 11, 1, 7),
      ); // ещё PDT: полночь 1 ноября
      expect(
        pac(DateTime.utc(2026, 11, 1, 12)),
        DateTime.utc(2026, 11, 2, 8),
      ); // уже PST
      expect(
        GeminiKeyPool.quotaUntil(
          '{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel"}]}',
          DateTime.utc(2026, 10, 27, 12),
        ).toUtc(),
        DateTime.utc(2026, 10, 28, 7),
      );
    });
  });

  // ------------------------------------------------------------------------------------------------ ротация
  group('ротация: первый рабочий, при лимите — следующий ключ', () {
    final cloud2 = {
      'TelegramAiKey': 'AIzaAAAA1111',
      'TelegramAiKeysExtra': jsonEncode({
        'v': 1,
        'mainLabel': 'Аккаунт 1',
        'mainEnabled': true,
        'extra': [
          {'label': 'Аккаунт 2', 'key': 'AIzaBBBB2222', 'enabled': true},
        ],
      }),
    };

    test('429 у первого → тот же запрос со вторым; ответ получен; первый «исчерпан до …»; следующий запрос сразу со вторым', () async {
      final (v, fake) = make(
        Map.of(cloud2),
        (o, n) => o.headers['x-goog-api-key'] == 'AIzaAAAA1111'
            ? _json(429, _quota('30s'))
            : _json(200, _ok('ответ 2')),
      );
      expect(await v.chat('система', _history), 'ответ 2');
      expect(fake.keys, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      final list = await v.geminiEntries();
      final (st, until) = v.geminiStateOf(list.first);
      expect(st, GeminiKeyState.exhausted);
      expect(until, now.add(const Duration(seconds: 31)));
      expect(v.geminiCounts(list), (1, 2)); // «Рабочих ключей: 1 из 2»
      // второй запрос — первый ключ не пробуем вовсе
      fake.calls.clear();
      await v.chat('система', _history);
      expect(fake.keys, ['AIzaBBBB2222']);
      // лимит прошёл — снова «первый рабочий» первый
      now = now.add(const Duration(seconds: 40));
      fake.calls.clear();
      await v.chat('система', _history);
      expect(fake.keys.first, 'AIzaAAAA1111');
    });

    test(
      'все ключи исчерпаны — понятное сообщение со временем, не «нет ключа»',
      () async {
        final (v, fake) = make(
          Map.of(cloud2),
          (o, n) => _json(429, _quota('60s')),
        );
        final e = await v
            .chat('система', _history)
            .then<Object?>((_) => null, onError: (Object e) => e);
        expect(e, isA<VisionException>());
        expect((e as VisionException).noKey, isFalse);
        expect(e.message, contains('всех ключей Gemini (2)'));
        expect(
          fake.calls.length,
          lessThanOrEqualTo(2 * 5),
        ); // не больше (ключей × моделей) попыток
      },
    );

    test('недействительный (400 API key not valid) → следующий ключ; через 30 минут пробуется заново', () async {
      var firstFixed = false;
      final (v, fake) = make(Map.of(cloud2), (o, n) {
        if (o.headers['x-goog-api-key'] == 'AIzaAAAA1111' && !firstFixed) {
          return _json(400, _invalidKey);
        }
        return _json(200, _ok('ок'));
      });
      expect(await v.chat('система', _history), 'ок');
      expect(fake.keys, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      expect(
        v.geminiStateOf((await v.geminiEntries()).first).$1,
        GeminiKeyState.invalid,
      );
      fake.calls.clear();
      await v.chat('система', _history);
      expect(fake.keys, ['AIzaBBBB2222']);
      now = now.add(const Duration(minutes: 31)); // Google «починили»
      firstFixed = true;
      fake.calls.clear();
      await v.chat('система', _history);
      expect(fake.keys, ['AIzaAAAA1111']);
    });

    test(
      'выключенный ключ не берётся; порядок списка = порядок попыток',
      () async {
        final c = {
          'TelegramAiKey': 'AIzaAAAA1111',
          'TelegramAiKeysExtra': jsonEncode({
            'v': 1,
            'mainEnabled': false,
            'extra': [
              {'key': 'AIzaBBBB2222'},
              {'key': 'AIzaCCCC3333'},
            ],
          }),
        };
        final (v, fake) = make(c, answer);
        await v.chat('система', _history);
        expect(fake.keys, ['AIzaBBBB2222']);
        expect(await v.key(), 'AIzaBBBB2222');
      },
    );

    test(
      'умная камера, описание товара и накладная идут через ту же ротацию',
      () async {
        final (v, fake) = make(
          Map.of(cloud2),
          (o, n) => o.headers['x-goog-api-key'] == 'AIzaAAAA1111'
              ? _json(429, _quota('30s'))
              : _json(200, _ok('Тёплое молоко.')),
        );
        expect(await v.describe('Молоко', ''), 'Тёплое молоко.');
        expect(fake.keys, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      },
    );

    test('«Проверить»: лёгкий GET списка моделей (не generateContent); рабочий снимает «исчерпан»; ключ из ошибки скрыт', () async {
      final (v, fake) = make(Map.of(cloud2), (o, n) {
        if (o.method == 'GET') {
          return o.headers['x-goog-api-key'] == 'AIzaBADBAD99'
              ? _json(400, {
                  'error': {'message': 'API key not valid: AIzaBADBAD99'},
                })
              : _json(200, {'models': []});
        }
        return _json(429, _quota('30s'));
      });
      await v
          .chat('система', _history)
          .then<Object?>(
            (_) => null,
            onError: (Object e) => e,
          ); // оба исчерпаны
      expect(
        (await v.geminiEntries()).map((e) => v.geminiStateOf(e).$1),
        everyElement(GeminiKeyState.exhausted),
      );
      fake.calls.clear();
      final ok = await v.checkGeminiKey('AIzaAAAA1111');
      expect(ok.state, GeminiKeyState.working);
      expect(fake.calls.single.method, 'GET');
      expect(fake.calls.single.uri.path, '/v1beta/models');
      expect(
        v.geminiStateOf((await v.geminiEntries()).first).$1,
        GeminiKeyState.working,
      );
      final bad = await v.checkGeminiKey('AIzaBADBAD99');
      expect(bad.state, GeminiKeyState.invalid);
      expect(bad.error, isNot(contains('AIzaBADBAD99')));
      expect(bad.error, contains('***AD99'));
    });

    test('rotateLiveKey (звонок): лимит/недействителен → следующий ключ; не про ключ → null', () async {
      final (v, _) = make(Map.of(cloud2), answer);
      await v.geminiEntries();
      expect(
        v.rotateLiveKey('AIzaAAAA1111', 'RESOURCE_EXHAUSTED: quota exceeded'),
        'AIzaBBBB2222',
      );
      expect(
        v.rotateLiveKey('AIzaBBBB2222', 'API key not valid'),
        isNull,
      ); // других нет
      expect(v.rotateLiveKey('AIzaAAAA1111', 'Internal error'), isNull);
      expect(
        v.rotateLiveKey('AIzaЧужой0001', 'quota'),
        isNull,
      ); // ключ не из списка
    });
  });

  // ------------------------------------------------------------------------------------------------ смена ключа
  group('смена ключа (Б)', () {
    test('замена → следующий запрос уходит с НОВЫМ ключом', () async {
      final (v, fake) = make({'TelegramAiKey': 'AIzaOLDOLD001'}, answer);
      await v.chat('система', _history);
      expect(fake.keys.last, 'AIzaOLDOLD001');
      expect(await v.saveAllKeys(gemini: 'AIzaNEWNEW002'), isTrue);
      fake.calls.clear();
      await v.chat('система', _history);
      expect(fake.keys, ['AIzaNEWNEW002']);
      expect(
        v.pushes.single['TelegramAiKey'],
        'AIzaNEWNEW002',
      ); // облако получило новый ключ
    });

    test('облако НЕ затирает свежую замену: сервер не принял запись, прошло 10 минут и перезапуск — всё равно новый ключ', () async {
      final (v, fake) = make({'TelegramAiKey': 'AIzaOLDOLD001'}, answer);
      await v.key();
      v.failPush = true; // в облако не ушло
      expect(await v.saveAllKeys(gemini: 'AIzaNEWNEW002'), isFalse);
      VisionService.expireCacheForTest(); // прошло 10 минут: список перечитывается, облако снова отвечает СТАРЫМ ключом
      expect(await v.key(), 'AIzaNEWNEW002');
      VisionService.resetForTest(); // перезапуск приложения (хранилище телефона остаётся)
      final again = _V(api, db, {
        'TelegramAiKey': 'AIzaOLDOLD001',
      }, http: Dio()..httpClientAdapter = fake);
      expect(await again.key(), 'AIzaNEWNEW002');
    });

    test('новый ключ, сменённый на ПК (другой, не тот что заменили), подхватывается из облака', () async {
      final (v, _) = make({'TelegramAiKey': 'AIzaOLDOLD001'}, answer);
      await v.key();
      v.failPush = true;
      await v.saveAllKeys(gemini: 'AIzaNEWNEW002');
      v.cloud = {'TelegramAiKey': 'AIzaFROMPC003'};
      VisionService.expireCacheForTest();
      expect(await v.key(), 'AIzaFROMPC003');
    });

    test('«плохой» ключ: замена снимает пометку; тот же ключ, вставленный заново, снова в работе', () async {
      final (v, fake) = make(null, answer);
      await v.saveKey('AIzaBADBAD01');
      expect(await v.key(), 'AIzaBADBAD01');
      await v.saveKey(''); // умная камера: «ключ не подошёл»
      expect(await v.key(), isNull);
      await v.saveAllKeys(gemini: 'AIzaGOODGOOD2');
      expect(await v.key(), 'AIzaGOODGOOD2');
      await v.chat('система', _history);
      expect(fake.keys, ['AIzaGOODGOOD2']);
      // старый «плохой» вставили заново (его починили в Google) — он снова берётся
      await v.saveAllKeys(gemini: 'AIzaBADBAD01');
      expect(await v.key(), 'AIzaBADBAD01');
      VisionService.resetForTest();
      expect(
        await _V(api, db, null).key(),
        'AIzaBADBAD01',
      ); // и после перезапуска
    });

    test('недействительный (в памяти) ключ заменили — новый работает сразу, без ожидания 30 минут', () async {
      final (v, fake) = make(
        {'TelegramAiKey': 'AIzaOLDOLD001'},
        (o, n) => o.headers['x-goog-api-key'] == 'AIzaOLDOLD001'
            ? _json(400, _invalidKey)
            : _json(200, _ok('ок')),
      );
      await v
          .chat('система', _history)
          .then<Object?>((_) => null, onError: (Object e) => e);
      expect(await v.key(), isNull);
      await v.saveAllKeys(gemini: 'AIzaNEWNEW002');
      expect(await v.key(), 'AIzaNEWNEW002');
      fake.calls.clear();
      expect(await v.chat('система', _history), 'ок');
      expect(fake.keys, ['AIzaNEWNEW002']);
    });

    test('вставка с пробелом и переводом строки: сохраняется и уходит в облако очищенной', () async {
      final (v, fake) = make({}, answer);
      await v.saveAllKeys(gemini: ' AIzaPASTE\nKEY 003 \n');
      expect(await v.key(), 'AIzaPASTEKEY003');
      expect(v.pushes.single['TelegramAiKey'], 'AIzaPASTEKEY003');
      await v.chat('система', _history);
      expect(fake.keys.single, 'AIzaPASTEKEY003');
    });

    test('смена основного не трогает дополнительные ключи; пустое значение убирает основной', () async {
      final (v, _) = make({}, answer);
      await v.saveGeminiKeys([
        const GeminiKeyEntry(label: 'Аккаунт 1', key: 'AIzaAAAA1111'),
        const GeminiKeyEntry(label: 'Аккаунт 2', key: 'AIzaBBBB2222'),
      ]);
      await v.saveAllKeys(gemini: 'AIzaNEWNEW002');
      expect(
        [for (final e in await v.geminiEntries()) e.key],
        ['AIzaNEWNEW002', 'AIzaBBBB2222'],
      );
      expect((await v.geminiEntries()).first.label, 'Аккаунт 1');
      await v.saveAllKeys(gemini: '');
      expect(
        [for (final e in await v.geminiEntries()) e.key],
        ['AIzaBBBB2222'],
      );
      expect(v.cloud!['TelegramAiKey'], 'AIzaBBBB2222');
    });

    test('удалённый из списка ключ не возвращается из облака', () async {
      final (v, _) = make({}, answer);
      await v.saveGeminiKeys([
        const GeminiKeyEntry(key: 'AIzaAAAA1111'),
        const GeminiKeyEntry(key: 'AIzaBBBB2222'),
      ]);
      v.failPush = true;
      await v.saveGeminiKeys([const GeminiKeyEntry(key: 'AIzaBBBB2222')]);
      VisionService.expireCacheForTest();
      expect(
        [for (final e in await v.geminiEntries(refresh: true)) e.key],
        ['AIzaBBBB2222'],
      );
    });

    test('сохранение списка: основной — TelegramAiKey, остальные — TelegramAiKeysExtra; читается обратно после перезапуска', () async {
      final (v, _) = make(null, answer); // облака нет: только телефон
      expect(
        await v.saveGeminiKeys([
          const GeminiKeyEntry(label: 'Аккаунт 1', key: 'AIzaAAAA1111'),
          const GeminiKeyEntry(
            label: 'Аккаунт 2',
            key: 'AIzaBBBB2222',
            enabled: false,
          ),
        ]),
        isTrue,
      );
      VisionService.resetForTest();
      final again = _V(api, db, null);
      final list = await again.geminiEntries();
      expect(
        [for (final e in list) (e.label, e.key, e.enabled)],
        [
          ('Аккаунт 1', 'AIzaAAAA1111', true),
          ('Аккаунт 2', 'AIzaBBBB2222', false),
        ],
      );
    });

    test('в облако уходит ровно то, что читает касса; ключи не попадают в тексты ошибок', () async {
      final (v, _) = make({}, answer);
      await v.saveGeminiKeys([
        const GeminiKeyEntry(label: 'А1', key: 'AIzaAAAA1111'),
        const GeminiKeyEntry(label: 'А2', key: 'AIzaBBBB2222'),
      ]);
      final secrets = v.pushes.single;
      expect(secrets.keys.toSet(), {'TelegramAiKey', 'TelegramAiKeysExtra'});
      expect(
        GeminiKeys.parse(
          secrets['TelegramAiKey'] as String,
          secrets['TelegramAiKeysExtra'],
        ).map((e) => e.key),
        ['AIzaAAAA1111', 'AIzaBBBB2222'],
      );
    });
  });

  // ------------------------------------------------------------------------------------------------ экран
  group('экран «Ключи Gemini»', () {
    const owner = Session(
      email: 'o@x',
      access: 'a',
      refresh: 'r',
      userId: 'OWN',
      userName: 'Бакыт',
      role: 'owner',
      isOwner: true,
      companyName: 'NBS',
      companyInn: '1',
      perms: [],
    );
    const cashier = Session(
      email: 'c@x',
      access: 'a',
      refresh: 'r',
      userId: 'u-c',
      userName: 'Анна',
      role: 'cashier',
      companyName: 'NBS',
      companyInn: '1',
      perms: [],
    );
    final two = {
      'TelegramAiKey': 'AIzaAAAA1111',
      'TelegramAiKeysExtra': jsonEncode({
        'v': 1,
        'mainLabel': 'Аккаунт 1',
        'mainEnabled': true,
        'extra': [
          {'label': 'Аккаунт 2', 'key': 'AIzaBBBB2222', 'enabled': true},
        ],
      }),
    };

    Widget app(_V v, Session s, {String lang = 'ru'}) => ProviderScope(
      key: UniqueKey(),
      overrides: [
        visionProvider.overrideWithValue(v),
        sessionProvider.overrideWith(() => _Fixed(s)),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          GlobalMaterialLocalizations.delegate,
          GlobalWidgetsLocalizations.delegate,
          GlobalCupertinoLocalizations.delegate,
        ],
        home: const GeminiKeysScreen(),
      ),
    );

    void tall(WidgetTester t) {
      t.view.physicalSize = const Size(900, 3600);
      t.view.devicePixelRatio = 2.0;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
    }

    testWidgets(
      'владелец: два ключа, счётчик, предупреждение; ключ скрыт; добавить → сохранить уходит в облако',
      (t) async {
        tall(t);
        final apiO = ApiClient(SessionStore())..attach(owner);
        final v = _V(apiO, db, Map.of(two));
        await t.pumpWidget(app(v, owner));
        await t.pumpAndSettle();
        expect(find.text('Рабочих ключей: 2 из 2'), findsOneWidget);
        expect(find.textContaining('Правила Google'), findsOneWidget);
        expect(find.text('AIza…1111'), findsOneWidget); // ключ скрыт
        expect(find.text('AIzaAAAA1111'), findsNothing);
        await t.tap(find.text('Показать').first);
        await t.pump();
        expect(find.text('AIzaAAAA1111'), findsOneWidget);
        // добавить третий ключ — со «грязной» вставкой (пробелы, перевод строки)
        await t.enterText(
          find.widgetWithText(TextField, 'Вставьте ключ из Google AI Studio'),
          ' AIzaCCCC 3333 ',
        );
        await t.pump();
        await t.tap(find.text('Добавить').last);
        await t.pump();
        expect(find.text('Рабочих ключей: 3 из 3'), findsOneWidget);
        expect(find.text('3.'), findsOneWidget);
        // такой же ключ второй раз — понятная ошибка, а не повтор
        await t.enterText(
          find.widgetWithText(TextField, 'Вставьте ключ из Google AI Studio'),
          'AIzaCCCC3333',
        );
        await t.pump();
        await t.tap(find.text('Добавить').last);
        await t.pump();
        expect(find.text('Такой ключ уже есть в списке.'), findsOneWidget);
        await t.tap(find.text('Сохранить'));
        await t.pumpAndSettle();
        final secrets = v.pushes.single;
        expect(
          GeminiKeys.parse(
            secrets['TelegramAiKey'] as String,
            secrets['TelegramAiKeysExtra'],
          ).map((e) => e.key),
          ['AIzaAAAA1111', 'AIzaBBBB2222', 'AIzaCCCC3333'],
        );
      },
    );

    testWidgets(
      '«Ниже» меняет порядок: новый первый ключ становится TelegramAiKey; удаление спрашивает подтверждение',
      (t) async {
        tall(t);
        final v = _V(ApiClient(SessionStore())..attach(owner), db, Map.of(two));
        await t.pumpWidget(app(v, owner));
        await t.pumpAndSettle();
        await t.tap(find.byTooltip('Ниже').first);
        await t.pump();
        await t.tap(find.text('Сохранить'));
        await t.pumpAndSettle();
        expect(v.pushes.single['TelegramAiKey'], 'AIzaBBBB2222');
        await t.tap(find.byTooltip('Удалить').last);
        await t.pumpAndSettle();
        expect(find.textContaining('Удалить ключ'), findsOneWidget);
        await t.tap(find.text('Отмена'));
        await t.pumpAndSettle();
        expect(find.text('Рабочих ключей: 2 из 2'), findsOneWidget);
      },
    );

    testWidgets(
      'сотрудник с правом на ИИ: список виден скрытым, менять нельзя (нет добавления, порядка, удаления, «Показать»)',
      (t) async {
        tall(t);
        final v = _V(
          ApiClient(SessionStore())..attach(cashier),
          db,
          Map.of(two),
        );
        await t.pumpWidget(app(v, cashier));
        await t.pumpAndSettle();
        expect(find.text('Рабочих ключей: 2 из 2'), findsOneWidget);
        expect(find.text('AIza…1111'), findsOneWidget);
        expect(find.text('Показать'), findsNothing);
        expect(find.text('Добавить ключ'), findsNothing);
        expect(find.byTooltip('Удалить'), findsNothing);
        expect(find.text('Сохранить'), findsNothing);
        expect(
          find.textContaining('меняют владелец и администратор'),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      '«Настройка ИИ»: у Gemini нет поля ключа — счётчик и кнопка «Ключи Gemini»; Groq и OpenRouter как раньше',
      (t) async {
        tall(t);
        final apiO = _NoServer()..attach(owner);
        final v = _V(apiO, db, Map.of(two));
        await t.pumpWidget(
          ProviderScope(
            key: UniqueKey(),
            overrides: [
              visionProvider.overrideWithValue(v),
              sessionProvider.overrideWith(() => _Fixed(owner)),
              apiProvider.overrideWithValue(apiO),
              databaseProvider.overrideWithValue(db),
            ],
            child: MaterialApp(
              theme: buildTheme(dark: false),
              locale: const Locale('ru'),
              supportedLocales: AppLocalizations.supportedLocales,
              localizationsDelegates: const [
                AppLocalizations.delegate,
                GlobalMaterialLocalizations.delegate,
                GlobalWidgetsLocalizations.delegate,
                GlobalCupertinoLocalizations.delegate,
              ],
              home: const AiSettingsScreen(),
            ),
          ),
        );
        await t.pumpAndSettle();
        expect(find.text('Рабочих ключей: 2 из 2'), findsOneWidget);
        expect(find.text('Ключи Gemini (несколько аккаунтов)'), findsOneWidget);
        expect(
          find.widgetWithText(TextField, 'Вставьте новый ключ'),
          findsNWidgets(2),
        ); // Groq и OpenRouter
        await t.tap(find.text('Ключи Gemini (несколько аккаунтов)'));
        await t.pumpAndSettle();
        expect(
          find.text('Добавить ключ'),
          findsOneWidget,
        ); // открылся экран списка
      },
    );

    testWidgets(
      'исчерпанный ключ: «исчерпан до HH:mm», счётчик «1 из 2»; на пяти языках без ошибок вёрстки',
      (t) async {
        tall(t);
        for (final lang in ['ru', 'ky', 'en', 'tr', 'uz']) {
          VisionService.resetForTest();
          now = DateTime(2026, 10, 27, 12);
          GeminiKeyPool.clock = () => now;
          final v = _V(
            ApiClient(SessionStore())..attach(owner),
            db,
            Map.of(two),
          );
          await v.geminiEntries();
          GeminiKeyPool.markQuota(
            'AIzaAAAA1111',
            'gemini-3.5-flash-lite',
            jsonEncode(_quota('120s')),
          );
          await t.pumpWidget(app(v, owner, lang: lang));
          await t.pumpAndSettle();
          final l = lookupAppLocalizations(Locale(lang));
          expect(
            find.text(l.gkWorkingCount(1, 2)),
            findsOneWidget,
            reason: lang,
          );
          expect(
            find.text(
              l.gkStateExhausted(
                VisionService.hm(now.add(const Duration(seconds: 121))),
              ),
            ),
            findsOneWidget,
            reason: lang,
          );
          expect(t.takeException(), isNull, reason: lang);
        }
      },
    );
  });

  // ------------------------------------------------------------------------------------------------ звонок
  group('звонок: ключ отклонён до начала сеанса → следующий ключ', () {
    test('LiveCall при отказе по лимиту просит следующий ключ и подключается с ним', () async {
      final sockets = <_Sock>[];
      final headers = <String>[];
      final call = LiveCall(
        apiKey: 'AIzaAAAA1111',
        instruction: 'ДАННЫЕ',
        audio: _Audio(),
        connect: (uri, h) async {
          headers.add('${h['x-goog-api-key']}');
          final s = _Sock();
          sockets.add(s);
          return s;
        },
      );
      final asked = <String>[];
      call.onKeyRejected = (failed, reason) {
        asked.add(failed);
        return 'AIzaBBBB2222';
      };
      final started = call.start();
      await Future<void>.delayed(const Duration(milliseconds: 30));
      sockets.first
        ..closeCode = 1011
        ..closeReason = 'RESOURCE_EXHAUSTED: quota exceeded';
      await sockets.first.ctrl.close();
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(headers, ['AIzaAAAA1111', 'AIzaBBBB2222']);
      expect(asked, ['AIzaAAAA1111']);
      expect(call.apiKey, 'AIzaBBBB2222');
      sockets.last.ctrl.add(jsonEncode({'setupComplete': <String, dynamic>{}}));
      await started;
      expect(call.state.value, CallState.listening);
      call.end();
    });

    test('других ключей нет (onKeyRejected вернул null) — звонок кончается ошибкой, как раньше', () async {
      final sockets = <_Sock>[];
      final call = LiveCall(
        apiKey: 'AIzaAAAA1111',
        instruction: 'ДАННЫЕ',
        audio: _Audio(),
        connect: (uri, h) async {
          final s = _Sock();
          sockets.add(s);
          return s;
        },
      );
      call.onKeyRejected = (failed, reason) => null;
      CallEnd? ended;
      call.onEnded = (why, detail) => ended = why;
      final started = call.start();
      await Future<void>.delayed(const Duration(milliseconds: 30));
      sockets.first
        ..closeCode = 1007
        ..closeReason = 'API key not valid. Please pass a valid API key.';
      await sockets.first.ctrl.close();
      await started.timeout(const Duration(seconds: 3), onTimeout: () {});
      expect(ended, CallEnd.error);
      expect(sockets.length, 1);
    });
  });
}

class _NoServer extends ApiClient {
  _NoServer() : super(SessionStore());
  @override
  Future<dynamic> get(
    String path, {
    Map<String, dynamic>? query,
    Duration? timeout,
  }) async => throw ApiException('нет $path', status: 404);
  @override
  Future<dynamic> patch(String path, {Object? body}) async =>
      throw ApiException('нет $path', status: 404);
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _Sock implements LiveSocket {
  final ctrl = StreamController<Object?>();
  @override
  int? closeCode;
  @override
  String? closeReason;
  @override
  Stream<Object?> get frames => ctrl.stream;
  @override
  void send(String text) {}
  @override
  Future<void> close() async {
    if (!ctrl.isClosed) await ctrl.close();
  }
}

class _Audio implements LiveAudio {
  final ctrl = StreamController<Object?>.broadcast();
  @override
  Stream<Object?> get events => ctrl.stream;
  @override
  Future<bool> start({required bool speaker}) async => true;
  @override
  Future<void> play(Uint8List pcm) async {}
  @override
  Future<void> flush() async {}
  @override
  Future<void> setMuted(bool muted) async {}
  @override
  Future<void> setSpeaker(bool on) async {}
  @override
  Future<void> stop() async {}
}
