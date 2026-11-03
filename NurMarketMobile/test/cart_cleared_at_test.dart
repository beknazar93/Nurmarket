// 2026-11-02 (финальная приёмка, В-3): метка `cart.clearedAt` ставится только при реальной очистке чека, а не при построении
// провайдера (Riverpod вызывает listenSelf и при первом построении). Иначе после падения между оплатой и очисткой ключ проведённой
// продажи считался «зомби», и повторная оплата шла с новым ключом (вторая продажа). Сервер не трогается — запросы идут в фейк.
import 'package:drift/native.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final List<String> keys = [];

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    keys.add(headers?['Idempotency-Key'] ?? '');
    return {'id': 'srv1', 'number': '12', 'total': '10.00'};
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _session = Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'u', userName: 'Тест', role: 'owner', isOwner: true, cashboxId: 'b');

Future<void> _settle() async {
  for (var i = 0; i < 20; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

Cart _cart() => Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: 10, qty: 1)]);

void main() {
  late AppDatabase db;
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  ProviderContainer container() {
    final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), sessionProvider.overrideWith(() => _Fixed(_session))]);
    addTearDown(c.dispose);
    return c;
  }

  test('запуск приложения (первое построение провайдера чека) не меняет cart.clearedAt и не стирает черновик', () async {
    await db.setSetting('cart.clearedAt', '2026-01-01T00:00:00.000');
    await db.setSetting('cart.draft', '{"owner":"a@x.kg"}');
    final c = container();
    c.read(cartProvider);
    c.read(cartProvider.notifier);
    await c.read(cartProvider.notifier).flush();
    await _settle();
    expect(await db.getSetting('cart.clearedAt'), '2026-01-01T00:00:00.000');
    expect(await db.getSetting('cart.draft'), '{"owner":"a@x.kg"}');
  });

  test('clear() после непустого чека ставит cart.clearedAt', () async {
    final c = container();
    final n = c.read(cartProvider.notifier);
    n.restore(_cart());
    await n.flush();
    expect(await db.getSetting('cart.clearedAt'), isNull, reason: 'чек не очищали');
    n.clear();
    await n.flush();
    await _settle();
    expect(DateTime.tryParse(await db.getSetting('cart.clearedAt') ?? ''), isNotNull);
    expect(await db.getSetting('cart.clearedSeq'), '1');
  });

  test('М3-01: пока оплата идёт (frozen), состав чека не меняется; очистка работает всегда', () async {
    final c = container();
    final n = c.read(cartProvider.notifier);
    n.restore(_cart());
    n.frozen = true;
    n.setQty(0, 5);
    n.remove(0);
    n.addCustom('Услуга', 50, 1);
    expect(c.read(cartProvider).lines.single.qty, 1);
    expect(c.read(cartProvider).lines.length, 1);
    n.clear(); // очистка после оплаты — всегда
    expect(c.read(cartProvider).isEmpty, isTrue);
    n.frozen = false;
    await n.flush();
  });

  test('падение между оплатой и очисткой: после перезапуска повтор идёт ТЕМ ЖЕ ключом; после очистки — новый чек получает новый ключ', () async {
    final api = _Api();
    final repo = SalesRepository(api, db);
    await repo.checkout(cart: _cart(), shiftId: 'sh1', method: PayMethod.cash, deferForget: true);
    final first = api.keys.single;

    // «перезапуск»: новый контейнер строит провайдер чека (clearedAt не пишется), чек не очищался
    final c = container();
    c.read(cartProvider);
    await c.read(cartProvider.notifier).flush();
    await _settle();
    await SalesRepository(api, db).checkout(cart: _cart(), shiftId: 'sh1', method: PayMethod.cash, deferForget: true);
    expect(api.keys.last, first, reason: 'ключ остаётся рабочим: повтор вернёт прежнюю продажу');

    // чек очистили (реальная очистка пишет clearedAt позже doneAt) — такой же новый чек получает новый ключ
    // метка очистки — счётчик очисток (не часы телефона)
    await db.setSetting('cart.clearedSeq', '1');
    await SalesRepository(api, db).checkout(cart: _cart(), shiftId: 'sh1', method: PayMethod.cash, deferForget: true);
    expect(api.keys.last, isNot(first));
  });
}
