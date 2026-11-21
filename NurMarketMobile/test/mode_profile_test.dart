// 2026-11-20 (тариф «Мини», этап E1): ModeProfile.resolve — таблица «вход → режим»; разбор плана из capabilities и его кэш; чистые расчёты
// торга и сдачи; предупреждения «не потерять продажу». Сервер не трогается — запросы идут в фейк, база в памяти.
import 'dart:convert';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/mode/login_expiry.dart';
import 'package:nurmarket/core/mode/mode_profile.dart';
import 'package:nurmarket/core/mode/plan_info.dart';
import 'package:nurmarket/core/mode/plan_limit_event.dart';
import 'package:nurmarket/features/simple/simple_math.dart';

/// Ответ Умара 20.11 для «Мини».
const _mini = {
  'plan': {
    'code': 'mini',
    'names': {'ru': 'Мини', 'ky': 'Мини', 'en': 'Mini'},
    'limits': {'max_staff': 1, 'max_devices': 1, 'max_cashboxes': 1},
    'allowed_clients': ['mobile'],
    'upgrade_plan': 'start',
  },
};

class _CapsApi extends ApiClient {
  final Object? body;
  bool fail = false;
  _CapsApi(this.body) : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (fail) throw ApiException('Нет связи с сервером.');
    return body;
  }
}

void main() {
  tearDown(() => ServerCapabilities.debugSet(null));

  group('PlanInfo.parse', () {
    test('ответ Умара для «Мини»', () {
      final p = PlanInfo.parse(_mini);
      expect(p.present, isTrue);
      expect(p.isMini, isTrue);
      expect(p.limits.maxStaff, 1);
      expect(p.limits.maxDevices, 1);
      expect(p.limits.maxCashboxes, 1);
      expect(p.allowedClients, {'mobile'});
      expect(p.upgradePlan, 'start');
      expect(p.nameFor('ky'), 'Мини');
      expect(p.nameFor('tr'), 'Мини', reason: 'нет перевода — русское название');
    });

    test('plan: null, нет ключа, пустой объект, мусор — «плана нет» (как сегодня)', () {
      for (final data in [
        {'plan': null},
        <String, dynamic>{},
        {'plan': <String, dynamic>{}},
        {'plan': 5},
        'строка',
        null,
        [1, 2],
      ]) {
        final p = PlanInfo.parse(data);
        expect(p.present, isFalse, reason: '$data');
        expect(p.isMini, isFalse);
      }
    });

    test('у других тарифов limits: null = без лимита', () {
      final p = PlanInfo.parse({
        'plan': {
          'code': 'Standard',
          'limits': {'max_staff': null, 'max_devices': null, 'max_cashboxes': null},
          'allowed_clients': ['mobile', 'pos', 'web', 'owner'],
        },
      });
      expect(p.code, 'standard');
      expect(p.isKnown, isTrue);
      expect(p.limits.maxStaff, isNull);
      expect(p.allowedClients, containsAll(['pos', 'owner']));
    });

    test('кэш: toJson → parse даёт тот же план', () {
      final p = PlanInfo.parse(_mini);
      final back = PlanInfo.parse({'plan': jsonDecode(jsonEncode(p.toJson()))});
      expect(back, p);
    });
  });

  group('ModeProfile.resolve', () {
    test('нет плана → «как сегодня»: 5 вкладок, ничего не скрыто', () {
      final m = ModeProfile.resolve();
      expect(m.simple, isFalse);
      expect(m.source, ModeSource.today);
      expect(m.tabs, ModeProfile.todayTabs);
      expect(m.hidesRoute('/home'), isFalse);
      expect(m.hidesRoute('/admin'), isFalse);
      expect(m.hidesRoute('/admin/staff'), isFalse);
      expect(m.homeRoute, '/home');
    });

    test('plan.mini → Простой режим: 3 вкладки + «Ещё», админ-разделы скрыты', () {
      final m = ModeProfile.resolve(plan: PlanInfo.parse(_mini), role: 'owner');
      expect(m.simple, isTrue);
      expect(m.source, ModeSource.plan);
      expect(m.tabs, [ModeTab.sale, ModeTab.receive, ModeTab.totals, ModeTab.more]);
      expect(m.homeRoute, '/sale');
      for (final hidden in ['/home', '/admin', '/admin/staff', '/admin/quotes', '/admin/orders', '/admin/advisor', '/rentals', '/ai/settings', '/stock/receive', '/stock/audit']) {
        expect(m.hidesRoute(hidden), isTrue, reason: hidden);
      }
      for (final open in [
        '/sale',
        '/in',
        '/totals',
        '/more',
        '/stock',
        '/checks',
        '/debts',
        '/pay',
        '/paid',
        '/scan?mode=sale',
        '/profile',
        '/sync',
        '/close-shift',
        '/admin/new-product',
      ]) {
        expect(m.hidesRoute(open), isFalse, reason: open);
      }
      expect(m.staffHidden, isTrue, reason: 'max_staff = 1 — сотрудников нет, «Добавить сотрудника» не показываем');
      expect(m.canLeaveSimple, isFalse, reason: 'на «Мини» обычный вид недоступен');
    });

    test('ручной «Простой режим» без плана', () {
      final m = ModeProfile.resolve(simpleManual: true);
      expect(m.simple, isTrue);
      expect(m.source, ModeSource.manual);
      expect(m.canLeaveSimple, isTrue);
      expect(m.staffHidden, isFalse, reason: 'лимитов нет — сотрудников не прячем');
    });

    test('переключатель выключен и план — «Старт»/«Стандарт»/«Своё» → обычный вид', () {
      for (final code in ['start', 'standard', 'custom']) {
        final m = ModeProfile.resolve(
          plan: PlanInfo.parse({
            'plan': {
              'code': code,
              'limits': {'max_staff': null},
            },
          }),
        );
        expect(m.simple, isFalse, reason: code);
        expect(m.tabs, ModeProfile.todayTabs);
        expect(m.unknownPlan, isFalse);
      }
    });

    test('неизвестный план НЕ режет и НЕ открывает лишнего (работает как сегодня)', () {
      final m = ModeProfile.resolve(
        plan: PlanInfo.parse({
          'plan': {
            'code': 'gold',
            'limits': {'max_staff': 1},
          },
        }),
        role: 'owner',
      );
      expect(m.unknownPlan, isTrue);
      expect(m.simple, isFalse);
      expect(m.tabs, ModeProfile.todayTabs);
      expect(m.hidesRoute('/home'), isFalse);
      expect(m.hidesRoute('/admin'), isFalse);
      expect(m.hidesRoute('/sale'), isFalse);
      expect(m.staffHidden, isFalse, reason: 'лимит неизвестного плана не применяется');
    });

    test('в обычном виде вкладок Простого режима нет (адреса закрыты, ведут домой)', () {
      final m = ModeProfile.resolve();
      for (final p in ['/in', '/totals', '/more']) {
        expect(m.hidesRoute(p), isTrue, reason: p);
      }
    });

    test('продажа и оплата открыты в ЛЮБОМ профиле', () {
      final profiles = [
        ModeProfile.resolve(),
        ModeProfile.resolve(simpleManual: true),
        ModeProfile.resolve(plan: PlanInfo.parse(_mini)),
        ModeProfile.resolve(
          plan: PlanInfo.parse({
            'plan': {'code': 'gold'},
          }),
        ),
        ModeProfile.resolve(
          plan: PlanInfo.parse({
            'plan': {'code': 'expired-something', 'status': 'expired'},
          }),
          simpleManual: true,
        ),
      ];
      for (final m in profiles) {
        for (final p in ['/sale', '/pay', '/paid', '/scan']) {
          expect(m.hidesRoute(p), isFalse, reason: '${m.source} $p');
        }
        expect(m.accessTo(ModeScreen.sale), ScreenAccess.open);
      }
    });

    test('параметр screen: доступ к спрошенному экрану', () {
      final mini = PlanInfo.parse(_mini);
      expect(ModeProfile.resolve(plan: mini, screen: ModeScreen.admin).access, ScreenAccess.hidden);
      expect(ModeProfile.resolve(plan: mini, screen: ModeScreen.totals).access, ScreenAccess.open);
      expect(ModeProfile.resolve(screen: ModeScreen.admin).access, ScreenAccess.open);
      expect(ModeProfile.resolve(screen: ModeScreen.totals).access, ScreenAccess.hidden);
    });

    test('роль и признаки сервера доходят до профиля', () {
      final m = ModeProfile.resolve(role: 'Владелец', features: {'receipt_delete'}, sphere: 'Clothing');
      expect(m.isAdmin, isTrue);
      expect(m.has('receipt_delete'), isTrue);
      expect(m.sphere, 'clothing');
      expect(ModeProfile.resolve(role: 'Кассир').isAdmin, isFalse);
    });
  });

  group('capabilities → план (кэш и сбои)', () {
    test('refresh читает plan, кэширует; офлайн остаётся последний известный; plan: null стирает', () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      final api = _CapsApi({
        'features': {'receipt_delete': true},
        ..._mini,
      });
      expect(await ServerCapabilities.refresh(api, db, 'o@x.kg'), isTrue);
      expect(ServerCapabilities.plan.isMini, isTrue);
      expect(ServerCapabilities.has('receipt_delete'), isTrue);

      // перезапуск без связи: план берётся из кэша
      ServerCapabilities.debugSet(null);
      expect(ServerCapabilities.plan.present, isFalse);
      await ServerCapabilities.loadCached(db, 'o@x.kg');
      expect(ServerCapabilities.plan.isMini, isTrue);
      expect(ServerCapabilities.plan.limits.maxStaff, 1);

      // чужой аккаунт плана не получает
      ServerCapabilities.debugSet(null);
      await ServerCapabilities.loadCached(db, 'other@x.kg');
      expect(ServerCapabilities.plan.present, isFalse);

      // сбой сети не стирает последний известный план
      await ServerCapabilities.loadCached(db, 'o@x.kg');
      api.fail = true;
      expect(await ServerCapabilities.refresh(api, db, 'o@x.kg'), isFalse);
      expect(ServerCapabilities.plan.isMini, isTrue);

      // сервер без тарифа (plan: null) — «как сегодня»
      final api2 = _CapsApi({'features': <String, dynamic>{}, 'plan': null});
      expect(await ServerCapabilities.refresh(api2, db, 'o@x.kg'), isTrue);
      expect(ServerCapabilities.plan.present, isFalse);
      ServerCapabilities.debugSet(null);
      await ServerCapabilities.loadCached(db, 'o@x.kg');
      expect(ServerCapabilities.plan.present, isFalse, reason: 'пустой план тоже сохранён — старый не оживает');
    });

    test('старый сервер (список строк, без plan) — план не появляется', () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      await ServerCapabilities.refresh(_CapsApi(['receipt_delete']), db, 'o@x.kg');
      expect(ServerCapabilities.plan.present, isFalse);
      expect(ServerCapabilities.has('receipt_delete'), isTrue);
    });
  });

  group('лимиты тарифа: ответ сервера → событие', () {
    test('403 plan_limit с detail и upgrade_plan', () {
      final e = PlanLimitEvent.parse(403, {
        'code': 'plan_limit',
        'limit': 'max_staff',
        'max': 1,
        'current': 1,
        'upgrade_plan': 'start',
        'detail': 'На тарифе «Мини» один пользователь.',
      });
      expect(e, isNotNull);
      expect(e!.limit, 'max_staff');
      expect(e.max, 1);
      expect(e.upgradePlan, 'start');
      expect(e.detail, contains('Мини'));
    });
    test('403 plan_client_not_allowed; чужие ошибки — не лимит', () {
      expect(
        PlanLimitEvent.parse(403, {
          'code': 'plan_client_not_allowed',
          'allowed_clients': ['mobile'],
        })?.code,
        'plan_client_not_allowed',
      );
      expect(PlanLimitEvent.parse(403, {'detail': 'Нет прав'}), isNull);
      expect(PlanLimitEvent.parse(400, {'code': 'plan_limit'}), isNull);
      expect(PlanLimitEvent.parse(409, {'code': 'device_slot_taken'}), isNull, reason: 'слот устройства — отдельный диалог');
      expect(PlanLimitEvent.parse(403, 'text'), isNull);
    });
  });

  group('торг и сдача (простой режим)', () {
    test('«До 10 / 50 / 100»: округление вниз; нечего округлять — null', () {
      expect(roundDownTotal(437, 10), 430);
      expect(roundDownTotal(437, 50), 400);
      expect(roundDownTotal(437, 100), 400);
      expect(roundDownTotal(430, 10), isNull, reason: 'уже кратно');
      expect(roundDownTotal(40, 50), isNull, reason: 'получился бы ноль');
      expect(roundDownTotal(1234.5, 100), 1200);
    });
    test('«Договорились за …»: скидка = разница', () {
      expect(agreedDiscount(430, 400), 30);
      expect(agreedDiscount(430, 430), isNull);
      expect(agreedDiscount(430, 500), isNull);
      expect(agreedDiscount(430, 0), isNull);
      expect(agreedDiscount(430.5, 400), 30.5);
      expect(discountPercentOf(400, 20), 5);
    });
    test('быстрые суммы «Получено»: «Без сдачи» и ближайшие 100 / 500 / 1000 / 5000', () {
      expect(receivedQuickAmounts(430), [430, 500, 1000, 5000, 10000]);
      expect(receivedQuickAmounts(500), [500, 1000, 5000, 10000]);
      expect(receivedQuickAmounts(1200), [1200, 1500, 2000, 5000, 10000]);
      expect(receivedQuickAmounts(5000), [5000, 10000]);
      expect(receivedQuickAmounts(0), isEmpty);
    });
  });

  group('предупреждения «не потерять продажу»', () {
    String jwt(DateTime exp) {
      String b(Object o) => base64Url.encode(utf8.encode(jsonEncode(o))).replaceAll('=', '');
      return '${b({'alg': 'HS256'})}.${b({'exp': exp.millisecondsSinceEpoch ~/ 1000})}.sig';
    }

    final now = DateTime(2026, 11, 20, 12);
    test('срок входа читается из JWT; не JWT — null', () {
      expect(jwtExpiry(jwt(DateTime(2026, 12, 1, 8)))?.toUtc(), DateTime(2026, 12, 1, 8).toUtc());
      expect(jwtExpiry('abc'), isNull);
      expect(jwtExpiry(''), isNull);
      expect(jwtExpiry(null), isNull);
    });
    test('за 2 суток до конца входа — предупреждение; раньше — нет; после — «истёк»', () {
      expect(simpleWarnings(now: now, loginExpiry: now.add(const Duration(days: 3))), isEmpty);
      final w = simpleWarnings(now: now, loginExpiry: now.add(const Duration(days: 1, hours: 20)));
      expect(w.single.kind, SimpleWarningKind.loginExpires);
      expect(simpleWarnings(now: now, loginExpiry: now.subtract(const Duration(hours: 1))).single.kind, SimpleWarningKind.loginExpired);
      expect(simpleWarnings(now: now), isEmpty);
    });
    test('чеки без отправки: 5+ суток — «скоро», 7+ — «PIN владельца»', () {
      expect(simpleWarnings(now: now, pendingSales: [now.subtract(const Duration(days: 2))]), isEmpty);
      final soon = simpleWarnings(now: now, pendingSales: [now.subtract(const Duration(days: 5, hours: 1)), now.subtract(const Duration(days: 1))]).single;
      expect(soon.kind, SimpleWarningKind.oldSalesSoon);
      expect(soon.count, 1);
      final late = simpleWarnings(now: now, pendingSales: [now.subtract(const Duration(days: 8)), now.subtract(const Duration(days: 6))]).single;
      expect(late.kind, SimpleWarningKind.oldSalesLate);
      expect(late.count, 2);
      expect(late.days, 8);
    });
  });
}
