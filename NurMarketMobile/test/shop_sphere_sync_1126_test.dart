// 2026-11-26 (владелец: «В мобилке при смене сферы сфера не меняется»): сфера магазина берётся с сервера заново при входе под другим
// аккаунтом, при запуске с сохранённым входом, по «потянуть для обновления» и при возврате в приложение (не чаще раза в 5 минут) и сразу
// попадает в Prefs. Сервер отдаёт готовый профиль компании market_profile (у «Хозмага»/«Строймага» market_sphere = null). Сервер — фейк,
// база в памяти, боевые данные не трогаются.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/shop_mode.dart';
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/util/audit.dart';

class _MemStore extends SessionStore {
  Session? saved;
  @override
  Future<Session?> load() async => saved;
  @override
  Future<void> save(Session s) async => saved = s;
  @override
  Future<void> clear() async => saved = null;
}

/// Сервер: у каждого логина свой ответ api/users/company/ (как у тестовых аккаунтов NBS); меняется на лету.
class _Server extends ApiClient {
  _Server(super.store);
  final Map<String, Map<String, dynamic>> companies = {};
  Map<String, dynamic>? settingsDoc;
  bool offline = false;
  int companyGets = 0;

  @override
  Future<Map<String, dynamic>> login(String email, String password, {String? deviceKey, String? deviceName, bool replaceDevice = false}) async {
    attach(Session(email: email.trim(), access: 'tok-$email', refresh: 'r'));
    return {};
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (offline) throw ApiException('Нет связи с сервером.');
    final me = session!.email.toLowerCase();
    if (path == 'api/users/profile/') return {'user': {'id': me, 'first_name': me, 'role': 'owner', 'is_owner': true}};
    if (path == 'api/users/company/') {
      companyGets++;
      return {'name': 'Магазин $me', ...?companies[me]};
    }
    if (path == ShopModeSync.settingsPath && settingsDoc != null) return settingsDoc;
    throw ApiException('нет', status: 404);
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AppDatabase db;
  late Directory dir;
  late _MemStore store;
  late _Server api;
  late ProviderContainer c;

  setUp(() async {
    FlutterSecureStorage.setMockInitialValues({});
    db = AppDatabase(NativeDatabase.memory());
    dir = await Directory.systemTemp.createTemp('shop1126');
    Audit.testDirectory = dir;
    AccountSwitch.testDirectory = dir;
    store = _MemStore();
    api = _Server(store);
    api.companies.addAll({
      'standard@nur.kg': {'market_sphere': 'grocery', 'market_profile': 'grocery'},
      'hozmag@nur.kg': {'market_sphere': null, 'market_profile': 'hozmag'},
      'stroymag@nur.kg': {'market_sphere': null, 'market_profile': 'stroymag'},
      'autoparts@nur.kg': {'market_sphere': 'autoparts', 'market_profile': 'autoparts'},
      'bazaar@nur.kg': {'market_sphere': 'bazaar_retail', 'market_profile': 'bazaar_retail'},
      'clothing@nur.kg': {'market_sphere': 'clothing', 'market_profile': 'clothing'},
      'electronics@nur.kg': {'market_sphere': null, 'market_profile': 'electronics'},
    });
    c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), sessionStoreProvider.overrideWithValue(store), apiProvider.overrideWithValue(api)]);
  });
  tearDown(() async {
    c.dispose();
    await db.close();
    Audit.testDirectory = null;
    AccountSwitch.testDirectory = null;
    if (await dir.exists()) await dir.delete(recursive: true);
  });

  Future<void> loginAs(String email) async {
    await c.read(sessionProvider.notifier).login(email, 'pw');
    // сфера читается в фоне (unawaited) шагом загрузки аккаунта — дать закончиться
    await Future<void>.delayed(const Duration(milliseconds: 200));
  }

  Prefs prefs() => c.read(prefsProvider);

  test('профиль сервера: у Хозмага/Строймага market_sphere = null, но сфера — «Хозтовары»', () async {
    api.companies['mini@nur.kg'] = {'market_sphere': null, 'market_profile': 'hozmag'};
    final r = await ShopModeSync.fetchServer(api..attach(Session(email: 'mini@nur.kg', access: 'a', refresh: 'r')));
    expect(ShopModeX.fromServer(r!.base, r.detail), ShopMode.hardware);
    for (final p in ['stroymag', 'hozmag', 'bazaar_retail', 'bazaar_wholesale', 'autoparts', 'clothing', 'services']) {
      api.companies['mini@nur.kg'] = {'market_sphere': null, 'market_profile': p};
      final x = await ShopModeSync.fetchServer(api);
      expect(ShopModeX.fromServer(x!.base, x.detail), isNotNull, reason: p);
    }
    // профиль без своего режима в телефоне (электроника) — «Продукты», а не прежний режим
    api.companies['mini@nur.kg'] = {'market_sphere': null, 'market_profile': 'electronics'};
    final e = await ShopModeSync.fetchServer(api);
    expect(ShopModeX.fromServer(e!.base, e.detail), ShopMode.grocery);
    // старый сервер без market_profile — прежний путь: market_sphere + MarketSphereDetail
    api.companies['mini@nur.kg'] = {'market_sphere': 'grocery'};
    api.settingsDoc = {'settings': {'MarketSphereDetail': 'autoparts'}};
    final o = await ShopModeSync.fetchServer(api);
    expect(ShopModeX.fromServer(o!.base, o.detail), ShopMode.autoparts);
  });

  test('вход → выход → вход другим аккаунтом: каждый раз сфера своего аккаунта и сразу в Prefs', () async {
    await loginAs('standard@nur.kg');
    expect(prefs().shop, ShopMode.grocery);
    expect(prefs().shopChosen, isFalse);

    await loginAs('hozmag@nur.kg'); // без выхода: данные прошлого аккаунта стираются
    expect(prefs().shop, ShopMode.hardware);
    expect(prefs().shopChosen, isTrue);

    await c.read(sessionProvider.notifier).logout();
    expect(prefs().shopChosen, isFalse, reason: 'после выхода сфера прошлого аккаунта не остаётся');
    await loginAs('autoparts@nur.kg');
    expect(prefs().shop, ShopMode.autoparts);

    await loginAs('bazaar@nur.kg');
    expect(prefs().shop, ShopMode.bazaar);
    await loginAs('clothing@nur.kg');
    expect(prefs().shop, ShopMode.clothing);
    await loginAs('standard@nur.kg');
    expect(prefs().shop, ShopMode.grocery, reason: 'из «Одежды» в «Продукты» при входе другим аккаунтом');
    await loginAs('electronics@nur.kg');
    expect(prefs().shop, ShopMode.grocery);
  });

  test('владелец сменил сферу на сервере: телефон берёт новую при обновлении и при возврате (не чаще 5 минут), без перезапуска', () async {
    await loginAs('hozmag@nur.kg');
    expect(prefs().shop, ShopMode.hardware);
    // владелец сменил сферу на «Автозапчасти»
    api.companies['hozmag@nur.kg'] = {'market_sphere': 'autoparts', 'market_profile': 'autoparts'};
    final before = api.companyGets;
    // возврат в приложение сразу после входа — слишком рано, сервер не спрашиваем
    expect(await c.read(prefsProvider.notifier).syncShopFromServer(force: false), isFalse);
    expect(api.companyGets, before);
    expect(prefs().shop, ShopMode.hardware);
    // «потянуть для обновления» — спрашиваем всегда
    expect(await c.read(prefsProvider.notifier).syncShopFromServer(), isTrue);
    expect(prefs().shop, ShopMode.autoparts);
    // и ещё раз на «Базар» — тоже сразу
    api.companies['hozmag@nur.kg'] = {'market_sphere': 'bazaar_wholesale', 'market_profile': 'bazaar_wholesale'};
    expect(await c.read(prefsProvider.notifier).syncShopFromServer(), isTrue);
    expect(prefs().shop, ShopMode.bazaar);
    // нет связи — режим остаётся
    api.offline = true;
    expect(await c.read(prefsProvider.notifier).syncShopFromServer(), isFalse);
    expect(prefs().shop, ShopMode.bazaar);
  });

  test('запуск с сохранённым входом: сфера, которую владелец сменил, пока приложение было закрыто, подхватывается', () async {
    await loginAs('hozmag@nur.kg');
    expect(prefs().shop, ShopMode.hardware);
    api.companies['hozmag@nur.kg'] = {'market_sphere': 'clothing', 'market_profile': 'clothing'};
    // «перезапуск»: новое окружение на той же базе и том же сохранённом входе
    final c2 = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), sessionStoreProvider.overrideWithValue(store), apiProvider.overrideWithValue(api)]);
    addTearDown(c2.dispose);
    await c2.read(prefsProvider.notifier).load();
    await c2.read(sessionProvider.notifier).restore();
    expect(c2.read(prefsProvider).shop, ShopMode.hardware);
    expect(await c2.read(prefsProvider.notifier).syncShopFromServer(), isTrue);
    expect(c2.read(prefsProvider).shop, ShopMode.clothing);
  });

  test('телефон, где режим уже стоял (выбран раньше), при первой встрече с явной сферой сервера берёт серверную', () async {
    await ShopModeSync.write(db, ShopMode.clothing);
    expect(await ShopModeSync.applyServer(db, base: 'grocery', detail: 'hozmag'), ShopMode.hardware);
    // тот же ответ сервера, но на телефоне осталось другое — серверное главнее (выбора в телефоне больше нет)
    await ShopModeSync.write(db, ShopMode.clothing);
    expect(await ShopModeSync.applyServer(db, base: 'grocery', detail: 'hozmag'), ShopMode.hardware);
    // «Продукты» по умолчанию выбор телефона не затирают
    await ShopModeSync.write(db, ShopMode.clothing);
    await db.setSetting(ShopModeSync.serverSeenKey, 'grocery');
    expect(await ShopModeSync.applyServer(db, base: 'grocery'), isNull);
    expect(await ShopModeSync.read(db), ShopMode.clothing);
  });
}
