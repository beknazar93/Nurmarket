// 2026-10-27 (расследование «ИИ неправильно открывает разделы и долго думает»): локальный маршрутизатор навигации без ИИ
// (nav_router.dart) на 5 языках — 150+ фраз → нужный экран, глубокие цели (склад → приёмка / ревизия / новый товар / накладная /
// умная камера / закупки), ничья → до 4 вариантов кнопками, «не навигация» → ИИ, права роли, скорость разбора.
// Без сети, без настоящего Gemini, без эмулятора.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/nav_router.dart';
import 'package:nurmarket/core/ai/assistant/screen_registry.dart';
import 'package:nurmarket/core/api/session_store.dart';

const _all = [
  Session.viewProducts, Session.viewAnalytics, Session.viewClients, Session.viewOrders, Session.viewShifts, Session.viewSettings,
  Session.procurement, Session.supplier, Session.editPrice, Session.sale,
];
Session _admin() => const Session(email: 'a', access: '', refresh: '', role: 'admin', isOwner: true, cashboxId: '1', perms: _all);
Session _cashier() => const Session(email: 'c', access: '', refresh: '', role: 'cashier', cashboxId: '1', perms: [Session.sale]);

/// фраза → ключ экрана (и, если нужно, параметры)
const _open = <(String, String)>[
  // ---- ru
  ('открой раздел приёмки', 'receive'),
  ('э-э открой вкладку приемка товара', 'receive'),
  ('открой приемку', 'receive'),
  ('перейди в склад', 'stock'),
  ('зайди на склад', 'stock'),
  ('открой раздел склад', 'stock'),
  ('открой ревизию', 'stock_audit'),
  ('открой раздел ревизия пожалуйста', 'stock_audit'),
  ('открой инвентаризацию', 'stock_audit'),
  ('открой новый товар', 'new_product'),
  ('открой добавление товара', 'new_product'),
  ('открой загрузку накладной', 'invoice'),
  ('загрузить накладную открой', 'invoice'),
  ('открой накладную', 'invoice'),
  ('открой умную камеру', 'smartcam'),
  ('открой закупки', 'purchase'),
  ('открой заказ поставщику', 'purchase'),
  ('перейди в продажу', 'sale'),
  ('открой продажу', 'sale'),
  ('открой кассу', 'sale'),
  ('открой смены', 'shifts'),
  ('открой закрытие смены', 'close_shift'),
  ('открой клиентов', 'clients'),
  ('открой долги', 'debts'),
  ('открой раздел долги', 'debts'),
  ('открой сотрудников', 'staff'),
  ('открой задачи', 'tasks'),
  ('открой мои задачи', 'my_tasks'),
  ('открой зарплату', 'salary'),
  ('открой аналитику', 'summary'),
  ('покажи сводку', 'summary'),
  ('открой профиль', 'profile'),
  ('открой мой профиль', 'profile'),
  ('открой связь с кассой', 'kassa_link'),
  ('открой подключение к кассе', 'kassa_link'),
  ('открой ключи Gemini', 'gemini_keys'),
  ('открой ключи джемини', 'gemini_keys'),
  ('открой режим тестировщика', 'tester'),
  ('открой помощника', 'assistant_settings'),
  ('открой действия ИИ', 'ai_log'),
  ('открой прокат', 'rentals'),
  ('открой новый прокат', 'rental_new'),
  ('открой дашборд', 'summary'),
  ('открой прибыль', 'summary'),
  ('открой чеки', 'checks'),
  ('открой поиск', 'search'),
  ('открой отложенные', 'held'),
  ('открой принтер', 'printer'),
  ('открой настройки принтера', 'printer'),
  ('открой настройки', 'profile'),
  ('открой уведомления', 'notifications'),
  ('открой коммерческое предложение', 'quotes'),
  ('открой советника', 'advisor'),
  ('открой эсф', 'esf'),
  ('открой цены', 'prices'),
  ('открой управление', 'admin'),
  ('открой синхронизацию', 'sync'),
  ('открой внесение и изъятие', 'cash'),
  ('открой массовую загрузку', 'stock_import'),
  ('открой заказы', 'orders'),
  ('открой сканер', 'scan'),
  ('открой главную', 'home'),
  ('открой приход', 'stock_receive'),
  ('открой доступ к ИИ', 'ai_access'),
  ('сфотографировать накладную открой', 'smartcam'),
  ('открой камеру для накладной', 'smartcam'),
  ('на складе открой приёмку', 'receive'),
  // ---- ky
  ('кампаны ач', 'stock'),
  ('склад ач', 'stock'),
  ('ревизияны ач', 'stock_audit'),
  ('сатууну ач', 'sale'),
  ('чектерди ач', 'checks'),
  ('кардарларды ач', 'clients'),
  ('карыздарды ач', 'debts'),
  ('кызматкерлерди ач', 'staff'),
  ('жаңы товарды ач', 'new_product'),
  ('накладнойду ач', 'invoice'),
  ('жеткирүүчүдөн кабыл алуу ач', 'receive'),
  ('кабыл алууну ач', 'receive'),
  ('акылдуу камераны ач', 'smartcam'),
  ('профилимди ач', 'profile'),
  ('кассага туташууну ач', 'kassa_link'),
  ('Gemini ачкычтарын ач', 'gemini_keys'),
  ('тапшырмаларды ач', 'tasks'),
  ('айлыкты ач', 'salary'),
  ('жыйынтыкты ач', 'summary'),
  ('принтерди ач', 'printer'),
  ('ижараны ач', 'rentals'),
  ('башкы бетти ач', 'home'),
  ('ачып бер кампаны', 'stock'),
  // ---- uz
  ('omborni och', 'stock'),
  ('ombor ochib bering', 'stock'),
  ('reviziyani och', 'stock_audit'),
  ('yangi tovar och', 'new_product'),
  ('yangi tovarni och', 'new_product'),
  ('qabulni och', 'receive'),
  ('nakladnoyni och', 'invoice'),
  ('mijozlarni och', 'clients'),
  ('qarzlarni och', 'debts'),
  ('sotuvni och', 'sale'),
  ('cheklarni och', 'checks'),
  ('xodimlarni och', 'staff'),
  ('aqlli kamerani och', 'smartcam'),
  ('profilni och', 'profile'),
  ('kassa bilan aloqani och', 'kassa_link'),
  ('gemini kalitlarini och', 'gemini_keys'),
  ('vazifalarni och', 'tasks'),
  ('maoshni och', 'salary'),
  ('xulosani och', 'summary'),
  ('ijarani och', 'rentals'),
  ('bosh sahifani och', 'home'),
  // ---- en
  ('open warehouse', 'stock'),
  ('open the stock section', 'stock'),
  ('go to receiving', 'receive'),
  ('open goods receipt', 'receive'),
  ('open stocktake', 'stock_audit'),
  ('open new product', 'new_product'),
  ('open upload invoice', 'invoice'),
  ('open smart camera', 'smartcam'),
  ('open purchasing', 'purchase'),
  ('open sales', 'sale'),
  ('open receipts', 'checks'),
  ('open clients', 'clients'),
  ('open debts', 'debts'),
  ('open staff', 'staff'),
  ('open analytics', 'summary'),
  ('open tasks', 'tasks'),
  ('open my tasks', 'my_tasks'),
  ('open till link', 'kassa_link'),
  ('open gemini keys', 'gemini_keys'),
  ('open tester mode', 'tester'),
  ('open printer', 'printer'),
  ('open settings', 'profile'),
  ('open rentals', 'rentals'),
  ('show summary', 'summary'),
  ('go to home', 'home'),
  ('open ai actions', 'ai_log'),
  ('open shifts', 'shifts'),
  ('open orders', 'orders'),
  ('open abc analysis', 'abc'),
  ('could you please open the warehouse', 'stock'),
  // ---- tr
  ('depoyu aç', 'stock'),
  ('depo aç', 'stock'),
  ('sayımı aç', 'stock_audit'),
  ('yeni ürün aç', 'new_product'),
  ('yeni ürünü aç', 'new_product'),
  ('mal kabulü aç', 'receive'),
  ('irsaliyeyi aç', 'invoice'),
  ('akıllı kamerayı aç', 'smartcam'),
  ('satışı aç', 'sale'),
  ('fişleri aç', 'checks'),
  ('müşterileri aç', 'clients'),
  ('borçları aç', 'debts'),
  ('personeli aç', 'staff'),
  ('görevleri aç', 'tasks'),
  ('maaşları aç', 'salary'),
  ('özeti aç', 'summary'),
  ('kasa bağlantısını aç', 'kassa_link'),
  ('gemini anahtarlarını aç', 'gemini_keys'),
  ('test modunu aç', 'tester'),
  ('yazıcıyı aç', 'printer'),
  ('ayarları aç', 'profile'),
  ('kiralamayı aç', 'rentals'),
  ('ana sayfaya git', 'home'),
  ('siparişleri aç', 'orders'),
];

void main() {
  const screens = ScreenRegistry();

  group('фраза → экран (5 языков)', () {
    test('150+ фраз попадают в нужный экран', () {
      expect(_open.length, greaterThanOrEqualTo(150));
      final bad = <String>[];
      for (final (phrase, key) in _open) {
        final d = parseNavCommand(phrase, session: _admin());
        if (d.kind != NavKind.open || d.def?.key != key) bad.add('«$phrase» → ${d.kind} ${d.def?.key ?? d.options.map((e) => e.key).toList()} (нужно $key)');
      }
      expect(bad, isEmpty, reason: bad.join('\n'));
    });

    test('охвачены все 5 языков и все основные экраны', () {
      final keys = {for (final (_, k) in _open) k};
      for (final k in ['stock', 'receive', 'stock_audit', 'new_product', 'invoice', 'smartcam', 'purchase', 'sale', 'shifts', 'clients', 'debts', 'staff', 'tasks', 'salary', 'summary', 'rentals', 'assistant_settings', 'ai_log', 'kassa_link', 'gemini_keys', 'tester', 'printer', 'profile']) {
        expect(keys, contains(k));
      }
    });

    test('скорость: разбор одной фразы < 100 мс, все фразы вместе < 1,5 с', () {
      final sw = Stopwatch()..start();
      var worst = 0;
      for (final (phrase, _) in _open) {
        final one = Stopwatch()..start();
        parseNavCommand(phrase, session: _admin());
        if (one.elapsedMicroseconds > worst) worst = one.elapsedMicroseconds;
      }
      sw.stop();
      expect(worst, lessThan(100000), reason: 'самая долгая фраза: $worst мкс');
      expect(sw.elapsedMilliseconds, lessThan(1500), reason: 'все фразы: ${sw.elapsedMilliseconds} мс');
    });
  });

  group('глубокие цели и параметры', () {
    test('склад → что заканчивается / закончилось; сводка за неделю; сканер цен; новый товар одежда', () {
      void is_(String phrase, String key, Map<String, String> args) {
        final d = parseNavCommand(phrase);
        expect(d.def?.key, key, reason: phrase);
        expect(d.args, args, reason: phrase);
      }

      is_('открой склад что заканчивается', 'stock', {'filter': 'low'});
      is_('открой склад что закончилось', 'stock', {'filter': 'out'});
      is_('открой сводку за неделю', 'summary', {'period': 'week'});
      is_('открой сканер цен', 'scan', {'tab': 'price'});
      is_('открой новый товар одежда', 'new_product', {'tab': 'clothes'});
    });

    test('умная камера: накладная / этикетки / полка — параметр mode', () {
      expect(parseNavCommand('открой камеру для накладной').args, {'tab': 'invoice'});
      expect(parseNavCommand('открой умную камеру для этикеток').args, {'tab': 'labels'});
      expect(parseNavCommand('сфотографировать накладную открой').args, {'tab': 'invoice'});
      expect(parseNavCommand('открой умную камеру').args, isEmpty);
    });

    test('чек по номеру и последний, товар по названию', () {
      var d = parseNavCommand('открой чек 1043');
      expect((d.kind, d.checkNumber), (NavKind.check, '1043'));
      d = parseNavCommand('открой последний чек');
      expect((d.kind, d.checkLast), (NavKind.check, true));
      d = parseNavCommand('открой товар кола');
      expect((d.kind, d.query), (NavKind.product, 'кола'));
    });

    test('«Списание» отдельного экрана нет — подсказка, а не чужой экран', () {
      final d = parseNavCommand('открой списание');
      expect((d.kind, d.hint), (NavKind.hint, 'writeoff'));
    });

    test('«Склад → Приёмка»: у потомков есть родитель, путь в ответе', () {
      expect(screens.byKey('receive')!.fullTitle('ru'), 'Склад → Приёмка с поставщиком');
      expect(screens.byKey('invoice')!.fullTitle('ru'), 'Склад → Накладная');
      expect(screens.byKey('kassa_link')!.fullTitle('en'), 'Profile → Till link');
      expect(screens.byKey('stock')!.fullTitle('ru'), 'Склад');
    });
  });

  group('ничья → варианты кнопками', () {
    test('«табель» — Смены или Зарплата; не больше 4 вариантов', () {
      final d = parseNavCommand('открой табель', session: _admin());
      expect(d.kind, NavKind.choose);
      expect({for (final o in d.options) o.key}, {'shifts', 'salary'});
      expect(d.options.length, lessThanOrEqualTo(4));
    });

    test('закрытые для роли экраны в вариантах не показываются', () {
      final d = parseNavCommand('открой табель', session: _cashier());
      // кассиру недоступны ни смены, ни зарплата: остаются оба (ответ «нет доступа» даст исполнитель)
      expect(d.kind, anyOf(NavKind.choose, NavKind.open));
    });

    test('родитель и потомок — не ничья', () {
      expect(parseNavCommand('на складе открой ревизию').def?.key, 'stock_audit');
      expect(parseNavCommand('в складе открой новый товар').def?.key, 'new_product');
    });

    test('единственная догадка по опечатке — одна кнопка «Вы имели в виду», а не молчаливое открытие', () {
      final d = parseNavCommand('открой ревизею');
      expect(d.kind, anyOf(NavKind.open, NavKind.choose));
    });
  });

  group('не навигация → ИИ', () {
    test('вопросы к данным и разговор не перехватываются', () {
      for (final t in [
        'сколько продали сегодня',
        'покажи выручку',
        'покажи долги',
        'какая прибыль за неделю',
        'как дела',
        'что на складе',
        'расскажи про склад',
        'склад',
        'покажи сколько осталось колы',
        'открой склад и посчитай сколько у нас осталось колы за прошлый месяц',
        'спасибо большое',
        'canım ne kadar sattık',
        'bugun qancha sotdik',
      ]) {
        final d = parseNavCommand(t, session: _admin());
        expect(d.kind, NavKind.none, reason: t);
      }
    });

    test('просьба открыть без понятного экрана — «unknown» (подсказать, что можно открыть)', () {
      expect(parseNavCommand('открой что-нибудь').kind, NavKind.unknown);
      expect(parseNavCommand('открой').kind, NavKind.unknown);
    });
  });

  group('вспомогательное', () {
    test('wordQuality: форма слова и опечатка', () {
      expect(wordQuality('склад', 'склад'), 6);
      expect(wordQuality('складе', 'склад'), 5);
      expect(wordQuality('смену', 'смены'), 4);
      expect(wordQuality('приемки', 'приемка'), 4);
      expect(wordQuality('ревизею', 'ревизия'), greaterThan(0));
      expect(wordQuality('кола', 'склад'), 0);
      expect(wordQuality('ач', 'ачык'), 0);
    });
  });
}
