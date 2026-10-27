// 2026-10-26 (владелец: «максимум 2 сотрудника имеют права использовать ИИ» — общий бесплатный лимит Gemini):
// модель «кому разрешён ИИ», выбор в экране владельца (счётчик, замена, третий — предупреждение), старый магазин без списка,
// отказ без права во ВСЕХ входах ИИ (одна проверка в VisionService), тексты на 5 языках. Сеть и сервер — только фейки.
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
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/ai_access_ui.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body);

class _Api extends ApiClient {
  _Api(this.handler, Session s) : super(SessionStore()) {
    attach(s);
  }
  Handler handler;
  final calls = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    calls.add('GET $path');
    return handler('GET', path, null);
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) {
    calls.add('PATCH $path');
    return handler('PATCH', path, body);
  }
}

class _Counting implements HttpClientAdapter {
  int calls = 0;
  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls++;
    return ResponseBody.fromString(
      jsonEncode({
        'candidates': [
          {
            'content': {
              'role': 'model',
              'parts': [
                {'text': '[{"name":"Вода"}]'},
              ],
            },
            'finishReason': 'STOP',
          },
        ],
      }),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'OWN', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'Нур', companyInn: '1', perms: []);
const _anna = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'u-anna', userName: 'Анна', role: 'cashier', companyName: 'Нур', companyInn: '1', perms: []);
const _boris = Session(email: 'b@x', access: 'a', refresh: 'r', userId: 'u-boris', userName: 'Борис', role: 'cashier', companyName: 'Нур', companyInn: '1', perms: []);
const _vera = Session(email: 'v@x', access: 'a', refresh: 'r', userId: 'u-vera', userName: 'Вера', role: 'cashier', companyName: 'Нур', companyInn: '1', perms: []);
const _admin = Session(email: 'ad@x', access: 'a', refresh: 'r', userId: 'u-adm', userName: 'Админ', role: 'admin', companyName: 'Нур', companyInn: '1', perms: [Session.viewSettings]);

const _settingsPath = 'api/main/app-settings/nurmarket-mobile/';

Map<String, dynamic> _policyJson(List<(String, String)> users) => {
  'users': [for (final u in users) {'id': u.$1, 'name': u.$2}],
  'limit': 2,
};

void main() {
  late AppDatabase db;

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    VisionService.resetForTest(); // сбрасывает и запомненное правило ИИ
  });
  tearDown(() async {
    ApiText.l = null;
    await db.close();
  });

  // ---------------------------------------------------------------------------------------------- модель
  group('модель правила', () {
    test('лимит — 2 (константа), из трёх сохранённых берутся первые двое, повторы и пустые id выкидываются', () {
      expect(AiAccess.limit, 2);
      final p = AiAccessPolicy.fromJson(_policyJson([('a', 'А'), ('A', 'А ещё раз'), ('', 'без id'), ('b', 'Б'), ('c', 'В')]));
      expect(p.isSet, isTrue);
      expect([for (final u in p.users!) u.id], ['a', 'b']);
    });

    test('мусор и отсутствие ключа — «не задано» (ИИ как раньше), а не «ИИ выключен»', () {
      expect(AiAccessPolicy.fromJson(null).isSet, isFalse);
      expect(AiAccessPolicy.fromJson('{мусор').isSet, isFalse);
      expect(AiAccessPolicy.fromJson({'users': 'x'}).isSet, isFalse);
      expect(AiAccessPolicy.fromJson(const <String, dynamic>{}).isSet, isFalse);
      expect(AiAccessPolicy.fromJson({'users': []}).isSet, isTrue); // пустой список — задан: ИИ выключен для всех
    });

    test('туда и обратно: JSON правила читается так же', () {
      final p = AiAccessPolicy.set(const [AiAccessUser('x', 'Икс'), AiAccessUser('y', 'Игрек')], by: 'Бакыт', at: '2026-10-26T10:00:00Z');
      final back = AiAccessPolicy.fromJson(jsonDecode(jsonEncode(p.toJson())));
      expect([for (final u in back.users!) '${u.id}:${u.name}'], ['x:Икс', 'y:Игрек']);
      expect(back.updatedBy, 'Бакыт');
      expect(p.toJson()['limit'], 2);
    });

    test('старый магазин без списка: ИИ доступен всем (и без входа), владельцу — рекомендация ограничить', () {
      const none = AiAccessPolicy.unset();
      for (final s in [_owner, _anna, _boris, _vera, null]) {
        expect(AiAccess.canUse(s, none), isTrue);
      }
      expect(AiAccess.shouldRecommend(_owner, none), isTrue);
      expect(AiAccess.shouldRecommend(_admin, none), isTrue);
      expect(AiAccess.shouldRecommend(_anna, none), isFalse);
      expect(AiAccess.shouldRecommend(_owner, AiAccessPolicy.set(const [])), isFalse); // уже задано — не рекомендуем
    });

    test('список задан: право только у перечисленных (id без учёта регистра); без входа — нет', () {
      final p = AiAccessPolicy.set(const [AiAccessUser('U-ANNA', 'Анна'), AiAccessUser('u-boris', 'Борис')]);
      expect(AiAccess.canUse(_anna, p), isTrue);
      expect(AiAccess.canUse(_boris, p), isTrue);
      expect(AiAccess.canUse(_vera, p), isFalse);
      // 2026-10-27: владелец и администратор пользуются ИИ всегда, список ограничивает остальных
      expect(AiAccess.canUse(_owner, p), isTrue, reason: 'владелец вне списка всё равно имеет ИИ');
      expect(AiAccess.canUse(_admin, p), isTrue, reason: 'администратор вне списка всё равно имеет ИИ');
      expect(AiAccess.canUse(null, p), isFalse);
    });

    test('владелец по умолчанию входит в список первым; кто может менять список', () {
      expect([for (final u in AiAccess.defaultUsers(_owner)) u.id], ['OWN']);
      expect(AiAccess.defaultUsers(_anna), isEmpty);
      expect(AiAccess.defaultUsers(null), isEmpty);
      expect(AiAccess.canManage(_owner), isTrue);
      expect(AiAccess.canManage(_admin), isTrue);
      expect(AiAccess.canManage(_anna), isFalse);
      expect(AiAccess.canManage(null), isFalse);
    });
  });

  // ---------------------------------------------------------------------------------------------- выбор
  group('выбор сотрудников', () {
    const a = AiAccessUser('a', 'Анна'), b = AiAccessUser('b', 'Борис'), c = AiAccessUser('c', 'Вера');

    test('«2 из 2»: третьего выбрать нельзя — результат full, список не меняется', () {
      final pk = AiPicker();
      expect(pk.toggle(a), AiPick.added);
      expect(pk.toggle(b), AiPick.added);
      expect(pk.isFull, isTrue);
      expect(pk.chosen.length, 2);
      expect(pk.toggle(c), AiPick.full);
      expect(pk.chosen, [a, b]);
    });

    test('замена: снять одного — можно выбрать другого', () {
      final pk = AiPicker([a, b]);
      expect(pk.toggle(c), AiPick.full);
      expect(pk.toggle(a), AiPick.removed);
      expect(pk.toggle(c), AiPick.added);
      expect([for (final u in pk.chosen) u.id], ['b', 'c']);
    });

    test('начальный список длиннее лимита обрезается до двух', () {
      expect(AiPicker([a, b, c]).chosen.length, 2);
    });
  });

  // ---------------------------------------------------------------------------------------------- хранение
  group('хранение: общие настройки компании + копия телефона', () {
    test('читает ключ AiAccess из api/main/app-settings/nurmarket-mobile/, кладёт копию на телефон', () async {
      final api = _Api((m, p, b) async => {'settings': {'AiAccess': _policyJson([('OWN', 'Бакыт'), ('u-anna', 'Анна')]), 'DebtAccess': []}}, _owner);
      final p = await AiAccessRepository(api, db).load();
      expect([for (final u in p.users!) u.id], ['OWN', 'u-anna']);
      expect(api.calls, ['GET $_settingsPath']);
      expect(await db.getSetting('ai.access'), contains('u-anna'));
    });

    test('второй вызов — из памяти (key() зовут часто), force — снова на сервер', () async {
      final api = _Api((m, p, b) async => {'settings': {'AiAccess': _policyJson([('OWN', 'Бакыт')])}}, _owner);
      final repo = AiAccessRepository(api, db);
      await repo.load();
      await repo.load();
      expect(api.calls.length, 1);
      await repo.load(force: true);
      expect(api.calls.length, 2);
    });

    test('нет связи — копия телефона; копии нет — «не задано»', () async {
      await db.setSetting('ai.access', jsonEncode(_policyJson([('u-boris', 'Борис')])));
      final api = _Api((m, p, b) async => throw ApiException('Нет связи с сервером.'), _anna);
      final p = await AiAccessRepository(api, db).load();
      expect([for (final u in p.users!) u.id], ['u-boris']);
      AiAccessRepository.resetForTest();
      await db.setSetting('ai.access', '');
      expect((await AiAccessRepository(api, db).load()).isSet, isFalse);
    });

    test('сервер ответил без ключа AiAccess — «не задано», и старая копия не воскресает', () async {
      await db.setSetting('ai.access', jsonEncode(_policyJson([('u-boris', 'Борис')])));
      final api = _Api((m, p, b) async => {'settings': {'DebtAccess': []}}, _anna);
      expect((await AiAccessRepository(api, db).load()).isSet, isFalse);
      expect(await db.getSetting('ai.access'), '');
    });

    test('сохранение: PATCH только ключа AiAccess; потом читается из памяти', () async {
      Object? sent;
      final api = _Api((m, p, b) async {
        if (m == 'PATCH') sent = b;
        return {'settings': <String, dynamic>{}};
      }, _owner);
      final repo = AiAccessRepository(api, db);
      await repo.save(AiAccessPolicy.set(const [AiAccessUser('OWN', 'Бакыт'), AiAccessUser('u-anna', 'Анна')], by: 'Бакыт'));
      final body = sent as Map;
      expect(body.keys, [AiAccessRepository.key]);
      expect(((body['AiAccess'] as Map)['users'] as List).length, 2);
      expect(api.calls, ['PATCH $_settingsPath']);
      final p = await repo.load(); // из памяти — без нового запроса
      expect(p.contains('u-anna'), isTrue);
      expect(api.calls.length, 1);
    });
  });

  // ---------------------------------------------------------------------------------------------- единая проверка
  group('отказ без права — во всех входах ИИ (одна проверка в VisionService)', () {
    Handler server(Object? ai) => (m, p, b) async {
      if (p == _settingsPath) return {'settings': ai == null ? <String, dynamic>{} : {'AiAccess': ai}};
      if (p.startsWith('api/users/app-settings/')) {
        return {'settings': {'secrets': {'TelegramAiKey': 'AIzaTestKey', 'GroqApiKey': 'gsk_test'}}};
      }
      throw ApiException('нет такого адреса $p', status: 404);
    };

    (VisionService, _Counting, _Api) make(Session s, Object? ai) {
      final adapter = _Counting();
      final api = _Api(server(ai), s);
      return (VisionService(api, db, http: Dio()..httpClientAdapter = adapter), adapter, api);
    }

    final jpeg = Uint8List.fromList([0xFF, 0xD8, 1, 2, 3]);
    final entries = <String, Future<Object?> Function(VisionService v)>{
      'key (умная камера, звонок: ключ для LiveCall)': (v) => v.key(),
      'hasChatKey (чат Советника)': (v) => v.hasChatKey(),
      'fallbackKeys (запасные ключи чата)': (v) => v.fallbackKeys(),
      'chat (ответ Советника)': (v) => v.chat('system', const [(user: true, text: 'Привет')]),
      'recognize (умная камера, накладная по фото, полка, этикетки)': (v) => v.recognize(jpeg),
      'readScale (весы по виду)': (v) => v.readScale(jpeg),
      'describe (описание товара)': (v) => v.describe('Вода', ''),
      'identify (эталоны умной камеры)': (v) => v.identify(jpeg, [
        ('Вода', [jpeg]),
      ]),
      'allKeys (настройка ИИ у сотрудника без права менять)': (v) => v.allKeys(),
      'geminiEntries (список ключей Gemini, экран «Ключи Gemini»)': (v) => v.geminiEntries(),
    };

    final policy = _policyJson([('OWN', 'Бакыт'), ('u-anna', 'Анна')]);

    for (final e in entries.entries) {
      test('${e.key}: без права — AiAccessDenied, ни одного запроса к Google', () async {
        final (v, adapter, _) = make(_vera, policy);
        await expectLater(
          e.value(v),
          throwsA(
            isA<AiAccessDenied>()
                .having((x) => x.noKey, 'не «нет ключа»', isFalse)
                .having((x) => x.message, 'кому разрешено', allOf(contains('Бакыт'), contains('Анна'), contains('2'), contains('владельцу')))
                .having((x) => x.names, 'names', ['Бакыт', 'Анна']),
          ),
        );
        expect(adapter.calls, 0);
      });
    }

    test('AiAccessDenied — это VisionException: старые `on VisionException` его ловят и показывают message', () async {
      final (v, _, _) = make(_vera, policy);
      try {
        await v.key();
        fail('должен был отказать');
      } on VisionException catch (e) {
        expect(e, isA<AiAccessDenied>());
        expect(e.toString(), e.message);
      }
    });

    test('с правом: ключ выдаётся, чат-ключ есть, запрос к Google идёт', () async {
      final (v, adapter, _) = make(_anna, policy);
      expect(await v.key(), 'AIzaTestKey');
      expect(await v.hasChatKey(), isTrue);
      expect((await v.recognize(jpeg)).single.name, 'Вода');
      expect(adapter.calls, 1);
    });

    test('владелец и администратор вне списка ИИ всё равно получают ключ (2026-10-27); кассир вне списка — отказ', () async {
      final (v, _, _) = make(_owner, _policyJson([('u-anna', 'Анна'), ('u-boris', 'Борис')]));
      expect(await v.key(), 'AIzaTestKey');
      expect(await v.hasChatKey(), isTrue);
      expect((await v.allKeys()).gemini, 'AIzaTestKey');
      VisionService.resetForTest();
      final (va, _, _) = make(_admin, _policyJson([('u-anna', 'Анна'), ('u-boris', 'Борис')]));
      expect(await va.key(), 'AIzaTestKey');
      VisionService.resetForTest();
      final (vv, _, _) = make(_vera, _policyJson([('u-anna', 'Анна'), ('u-boris', 'Борис')]));
      await expectLater(vv.key(), throwsA(isA<AiAccessDenied>()));
    });

    test('владелец по умолчанию — в списке: ИИ работает', () async {
      final (v, _, _) = make(_owner, _policyJson([('OWN', 'Бакыт')]));
      expect(await v.key(), 'AIzaTestKey');
    });

    test('старый магазин (список не задан): ИИ у всех, как раньше', () async {
      for (final s in [_owner, _anna, _vera]) {
        VisionService.resetForTest();
        final (v, _, _) = make(s, null);
        expect(await v.key(), 'AIzaTestKey', reason: s.userName);
        expect(await v.hasChatKey(), isTrue);
      }
    });

    test('замена сотрудника: сняли Анну, дали Вере — Анна получает отказ, Вера допущена (после обновления правила)', () async {
      final before = _policyJson([('u-anna', 'Анна'), ('OWN', 'Бакыт')]);
      final after = _policyJson([('u-vera', 'Вера'), ('OWN', 'Бакыт')]);
      final (anna, _, annaApi) = make(_anna, before);
      expect(await anna.key(), 'AIzaTestKey');
      annaApi.handler = server(after);
      await anna.accessPolicy(force: true); // телефон перечитал общие настройки
      await expectLater(anna.key(), throwsA(isA<AiAccessDenied>()));
      VisionService.resetForTest();
      final (vera, _, _) = make(_vera, after);
      expect(await vera.key(), 'AIzaTestKey');
    });

    test('пустой список: ИИ выключен для всех, текст без списка имён', () async {
      final (v, _, _) = make(_anna, _policyJson([]));
      await expectLater(
        v.key(),
        throwsA(isA<AiAccessDenied>().having((x) => x.message, 'message', 'ИИ в магазине сейчас выключен для всех сотрудников. Обратитесь к владельцу.')),
      );
    });

    test('нет связи: действует копия телефона — отказ остаётся отказом', () async {
      await db.setSetting('ai.access', jsonEncode(_policyJson([('OWN', 'Бакыт'), ('u-anna', 'Анна')])));
      final adapter = _Counting();
      final api = _Api((m, p, b) async => throw ApiException('Нет связи с сервером.'), _vera);
      final v = VisionService(api, db, http: Dio()..httpClientAdapter = adapter);
      await expectLater(v.key(), throwsA(isA<AiAccessDenied>()));
    });
  });

  // ---------------------------------------------------------------------------------------------- тексты
  group('тексты на 5 языков', () {
    final langs = ['ru', 'ky', 'uz', 'en', 'tr'];
    final policy = AiAccessPolicy.set(const [AiAccessUser('1', 'Бакыт'), AiAccessUser('2', 'Анна')]);

    test('сообщение об отказе — на языке интерфейса (ApiText.l), с именами и числом', () {
      final seen = <String>{};
      for (final code in langs) {
        final l = lookupAppLocalizations(Locale(code));
        final text = AiAccess.deniedText(l, policy);
        expect(text, l.aiAccDenied(AiAccess.limit, 'Бакыт, Анна'));
        expect(text, allOf(contains('Бакыт'), contains('Анна'), contains('2')));
        seen.add(text);
      }
      expect(seen.length, 5, reason: 'у каждого языка свой текст');
      expect(AiAccess.deniedText(null, policy), startsWith('ИИ доступен только 2 сотрудникам магазина: Бакыт, Анна.'));
    });

    test('все новые надписи заполнены в каждом языке и не пустые; «ИИ выключен для всех» тоже', () {
      for (final code in langs) {
        final l = lookupAppLocalizations(Locale(code));
        final all = [
          l.aiAccTitle, l.aiAccSection, l.aiAccLimitInfo(2), l.aiAccCounter(2, 2), l.aiAccLimitReached(2), l.aiAccDenied(2, 'А'), l.aiAccDeniedNone,
          l.aiAccRecommend, l.aiAccConfigure, l.aiAccSaved, l.aiAccOwnerTag, l.aiAccYouTag, l.aiAccStaffNotLoaded, l.aiAccNobody, l.aiAccManagerOnly,
          l.aiAccNoticeTitle, l.aiAccNoticeBody(2), l.aiAccGotIt, l.aiAccMineYes, l.aiAccMineNo, l.aiAccMineOpen, l.aiAccNotSet,
        ];
        for (final t in all) {
          expect(t.trim(), isNotEmpty, reason: code);
          expect(t, isNot(contains('{')), reason: '$code: параметр не подставлен в «$t»');
        }
        expect(l.aiAccCounter(2, 2), contains('2'));
      }
    });

    test('исключение несёт текст того языка, что выставлен в приложении', () async {
      for (final code in langs) {
        VisionService.resetForTest();
        final l = lookupAppLocalizations(Locale(code));
        ApiText.l = l;
        final api = _Api((m, p, b) async => {'settings': {'AiAccess': _policyJson([('1', 'Бакыт'), ('2', 'Анна')])}}, _vera);
        final v = VisionService(api, db, http: Dio()..httpClientAdapter = _Counting());
        await expectLater(v.key(), throwsA(isA<AiAccessDenied>().having((x) => x.message, 'message', l.aiAccDenied(2, 'Бакыт, Анна'))), reason: code);
      }
    });
  });

  // ---------------------------------------------------------------------------------------------- экран
  group('экран «Доступ к ИИ» и плашка', () {
    Widget app(Widget home, Session s, _Api api, {String lang = 'ru', bool scroll = true}) => ProviderScope(
      key: UniqueKey(), // новая область провайдеров при каждом pumpWidget (иначе запомненное правило переживает смену фейка)
      overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(s))],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: scroll ? Scaffold(body: SingleChildScrollView(child: home)) : home,
      ),
    );

    void tall(WidgetTester t) {
      t.view.physicalSize = const Size(800, 3200);
      t.view.devicePixelRatio = 2.0;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
    }

    Handler staffServer({Object? ai, List<Object?>? patches}) => (m, p, b) async {
      if (m == 'PATCH') {
        patches?.add(b);
        return {};
      }
      if (p == _settingsPath) return {'settings': ai == null ? <String, dynamic>{} : {'AiAccess': ai}};
      if (p == 'api/users/employees/') {
        return [
          {'id': 'u-anna', 'first_name': 'Анна', 'last_name': 'К.', 'role_display': 'Кассир'},
          {'id': 'u-boris', 'first_name': 'Борис', 'last_name': '', 'role_display': 'Кассир'},
          {'id': 'u-vera', 'first_name': 'Вера', 'last_name': '', 'role_display': 'Кассир'},
        ];
      }
      throw ApiException('нет $p', status: 404);
    };

    testWidgets('владелец по умолчанию выбран (1 из 2); второй — 2 из 2; третий — понятное предупреждение, а не тишина', (t) async {
      tall(t);
      final api = _Api(staffServer(), _owner);
      await t.pumpWidget(app(const AiAccessPanel(), _owner, api));
      await t.pumpAndSettle();
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.aiAccCounter(1, 2)), findsOneWidget);
      expect(find.textContaining('Бакыт'), findsOneWidget);
      expect(find.text(l.aiAccLimitInfo(2)), findsOneWidget);
      expect(find.text(l.aiAccRecommend), findsOneWidget, reason: 'список ещё не задан — рекомендация на виду');

      await t.tap(find.byKey(const Key('aiAccRow_u-anna')));
      await t.pumpAndSettle();
      expect(find.text(l.aiAccCounter(2, 2)), findsOneWidget);

      await t.tap(find.byKey(const Key('aiAccRow_u-boris')));
      await t.pumpAndSettle();
      expect(find.text(l.aiAccCounter(2, 2)), findsOneWidget, reason: 'третий не выбран');
      expect(find.byKey(const Key('aiAccWarn')), findsOneWidget);
      expect(find.text(l.aiAccLimitReached(2)), findsWidgets);
      expect(t.widget<CheckboxListTile>(find.byKey(const Key('aiAccRow_u-boris'))).value, isFalse);
      expect(t.widget<CheckboxListTile>(find.byKey(const Key('aiAccRow_u-anna'))).value, isTrue);

      // замена: снять Анну — выбрать Бориса
      await t.tap(find.byKey(const Key('aiAccRow_u-anna')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('aiAccRow_u-boris')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccWarn')), findsNothing);
      expect(find.text(l.aiAccCounter(2, 2)), findsOneWidget);
    });

    testWidgets('«Сохранить» отправляет в общие настройки ровно выбранных двоих', (t) async {
      tall(t);
      final patches = <Object?>[];
      final api = _Api(staffServer(patches: patches), _owner);
      await t.pumpWidget(app(const AiAccessPanel(), _owner, api));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('aiAccRow_u-vera')));
      await t.pumpAndSettle();
      await t.tap(find.byType(FilledButton));
      await t.pumpAndSettle();
      final users = ((patches.single as Map)['AiAccess'] as Map)['users'] as List;
      expect([for (final u in users) (u as Map)['id']], ['OWN', 'u-vera']);
      expect(find.text(lookupAppLocalizations(const Locale('ru')).aiAccSaved), findsOneWidget);
    });

    testWidgets('сохранённый список показывается как есть; сотрудник без права менять видит только статус', (t) async {
      tall(t);
      final api = _Api(staffServer(ai: _policyJson([('u-anna', 'Анна К.'), ('u-boris', 'Борис')])), _anna);
      await t.pumpWidget(app(const AiAccessPanel(), _anna, api));
      await t.pumpAndSettle();
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.aiAccCounter(2, 2)), findsOneWidget);
      expect(find.text(l.aiAccManagerOnly), findsOneWidget);
      expect(find.byType(FilledButton), findsNothing, reason: 'кассир список не меняет');
      expect(find.text(l.aiAccRecommend), findsNothing);
      expect(find.textContaining('Анна К.'), findsOneWidget);
    });

    testWidgets('плашка «Рекомендуем ограничить»: владельцу при незаданном списке — есть, после задания и кассиру — нет', (t) async {
      tall(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      // владелец, список не задан
      var api = _Api(staffServer(), _owner);
      await t.pumpWidget(app(const AiAccessBanner(), _owner, api));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccBanner')), findsOneWidget);
      expect(find.text(l.aiAccRecommend), findsOneWidget);
      expect(find.text(l.aiAccConfigure), findsOneWidget);
      // владелец, список задан
      AiAccessRepository.resetForTest();
      api = _Api(staffServer(ai: _policyJson([('OWN', 'Бакыт')])), _owner);
      await t.pumpWidget(app(const AiAccessBanner(), _owner, api));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccBanner')), findsNothing);
      // кассир, список не задан
      AiAccessRepository.resetForTest();
      api = _Api(staffServer(), _anna);
      await t.pumpWidget(app(const AiAccessBanner(), _anna, api));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccBanner')), findsNothing);
    });

    testWidgets('AiAccessGate: без права вместо экрана ИИ — понятный отказ с именами; с правом и в старом магазине — сам экран', (t) async {
      tall(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      const screen = Text('ЭКРАН ИИ');
      final ai = _policyJson([('OWN', 'Бакыт'), ('u-anna', 'Анна')]);
      // без права
      await t.pumpWidget(app(const AiAccessGate(child: screen), _vera, _Api(staffServer(ai: ai), _vera), scroll: false));
      await t.pumpAndSettle();
      expect(find.text('ЭКРАН ИИ'), findsNothing);
      expect(find.text(l.aiAccDenied(2, 'Бакыт, Анна')), findsOneWidget);
      expect(find.text(l.aiAccConfigure), findsNothing, reason: 'кассиру менять список нельзя');
      // с правом
      AiAccessRepository.resetForTest();
      await t.pumpWidget(app(const AiAccessGate(child: screen), _anna, _Api(staffServer(ai: ai), _anna), scroll: false));
      await t.pumpAndSettle();
      expect(find.text('ЭКРАН ИИ'), findsOneWidget);
      // 2026-10-27: владелец вне списка — экран ИИ открыт (владелец и администратор пользуются ИИ всегда)
      AiAccessRepository.resetForTest();
      await t.pumpWidget(app(const AiAccessGate(child: screen), _owner, _Api(staffServer(ai: _policyJson([('u-anna', 'Анна')])), _owner), scroll: false));
      await t.pumpAndSettle();
      expect(find.text('ЭКРАН ИИ'), findsOneWidget);
      // старый магазин без списка
      AiAccessRepository.resetForTest();
      await t.pumpWidget(app(const AiAccessGate(child: screen), _vera, _Api(staffServer(), _vera), scroll: false));
      await t.pumpAndSettle();
      expect(find.text('ЭКРАН ИИ'), findsOneWidget);
    });

    testWidgets('«Настроить» в плашке открывает экран «Доступ к ИИ»', (t) async {
      tall(t);
      final api = _Api(staffServer(), _owner);
      await t.pumpWidget(app(const AiAccessBanner(), _owner, api));
      await t.pumpAndSettle();
      await t.tap(find.text(lookupAppLocalizations(const Locale('ru')).aiAccConfigure));
      await t.pumpAndSettle();
      expect(find.byType(AiAccessScreen), findsOneWidget);
      expect(find.byType(AiAccessPanel), findsOneWidget);
    });

    testWidgets('пояснение при первом открытии: один раз, только у сотрудника с правом; без права — тишина', (t) async {
      tall(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final api = _Api(staffServer(ai: _policyJson([('u-anna', 'Анна')])), _anna);
      late BuildContext ctx;
      late WidgetRef wref;
      await t.pumpWidget(
        app(
          Consumer(
            builder: (c, ref, _) {
              ctx = c;
              wref = ref;
              return const SizedBox();
            },
          ),
          _anna,
          api,
        ),
      );
      final first = showAiNoticeOnce(ctx, wref);
      await t.pumpAndSettle();
      expect(find.text(l.aiAccNoticeTitle), findsOneWidget);
      expect(find.text(l.aiAccNoticeBody(2)), findsOneWidget);
      await t.tap(find.text(l.aiAccGotIt));
      await t.pumpAndSettle();
      await first;
      expect(find.text(l.aiAccNoticeTitle), findsNothing);
      // второй раз — молча
      final second = showAiNoticeOnce(ctx, wref);
      await t.pumpAndSettle();
      await second;
      expect(find.text(l.aiAccNoticeTitle), findsNothing);
    });

    testWidgets('пояснение не показывается тому, у кого нет права (у него будет сообщение об отказе)', (t) async {
      tall(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final api = _Api(staffServer(ai: _policyJson([('u-anna', 'Анна')])), _vera);
      late BuildContext ctx;
      late WidgetRef wref;
      await t.pumpWidget(
        app(
          Consumer(
            builder: (c, ref, _) {
              ctx = c;
              wref = ref;
              return const SizedBox();
            },
          ),
          _vera,
          api,
        ),
      );
      final f = showAiNoticeOnce(ctx, wref);
      await t.pumpAndSettle();
      await f;
      expect(find.text(l.aiAccNoticeTitle), findsNothing);
    });
  });
}
