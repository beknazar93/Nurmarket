import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/checkout_device.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/language_pack.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';

// 2026-11-05 (сервер ceba67c): округление весовых строк (weight_line_total_floor), признаки сервера, поля чека, предупреждения, код языкового пакета.
// Чистая арифметика и разбор; база — в памяти, сети нет.
void main() {
  CartLine w(double qty, double price, {List<PromoRule> promo = const []}) => CartLine(productId: 'w', name: 'Весовой', price: price, qty: qty, isWeight: true, promo: promo);
  CartLine n(double qty, double price) => CartLine(productId: 'n', name: 'Маска', price: price, qty: qty);

  tearDown(() {
    ServerCapabilities.debugSet(null);
    CheckoutDevice.branch = '';
  });

  group('weight_line_total_floor: расчёт корзины', () {
    test('без признака — как раньше: вниз сумма всех весовых (4,875 + 22,638 → 27,51)', () {
      ServerRounding.reset();
      expect(Cart(lines: [w(0.375, 13), CartLine(productId: 'w2', name: 'Другой', price: 66, qty: 0.343, isWeight: true)]).total, 27.51);
    });

    test('с признаком: вниз каждая позиция (4,87 + 22,63 = 27,50)', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      final cart = Cart(lines: [w(0.375, 13), CartLine(productId: 'w2', name: 'Другой', price: 66, qty: 0.343, isWeight: true)]);
      expect(cart.subtotal, 27.50);
      expect(cart.total, 27.50);
    });

    test('проверка Умара: 0,486 × 66 + 3,00 = 35,07 (две строки по 0,243 склеены как одна позиция)', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      final cart = Cart(lines: [w(0.243, 66), n(1, 3), w(0.243, 66)]);
      expect(cart.total, 35.07);
      // без признака мобилка даёт то же: склеенная сумма 32,076 → 32,07
      ServerRounding.reset();
      expect(cart.total, 35.07);
    });

    test('66,00 × 0,343 с акцией 15 %: 22,63 − 3,40 = 19,23', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      const promo = [PromoRule(minAmount: 0, percent: 15)];
      final line = w(0.343, 66, promo: promo);
      final cart = Cart(lines: [line]);
      expect(line.total, 19.23);
      expect(cart.lineDiscounts, 3.40);
      expect(cart.total, 19.23);
    });

    test('скидка весовых позиций округляется по каждой: две позиции с акцией 15 % → 3,40 + 3,40 = 6,80 (а не 6,79)', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      const promo = [PromoRule(minAmount: 0, percent: 15)];
      final a = CartLine(productId: 'a', name: 'А', price: 66, qty: 0.343, isWeight: true, promo: promo);
      final b = CartLine(productId: 'b', name: 'Б', price: 66, qty: 0.343, isWeight: true, promo: promo);
      final cart = Cart(lines: [a, b]);
      expect(cart.lineDiscounts, 6.80);
      expect(cart.total, 38.46); // 2 × (22,63 − 3,40)
    });

    test('шаг весов som: вниз до целого сома (22,638 → 22)', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor}, stepCents: 100);
      expect(Cart(lines: [w(0.343, 66)]).total, 22.0);
      expect(ServerCapabilities.stepCentsOf('som'), 100);
      expect(ServerCapabilities.stepCentsOf('tiyin'), 1);
      expect(ServerCapabilities.stepCentsOf(null), 1);
    });

    test('сумма строк на бумаге: весовая печатается вниз, «Округление» сходится', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      final line = w(0.343, 66);
      final cart = Cart(lines: [line, n(1, 3)]);
      expect(line.printedGross, 22.63);
      expect(cart.printedSubtotal, 25.63);
      expect(cart.paperRounding, 0);
      expect(cart.total, 25.63);
    });

    test('weightRounding: «съеденная» доля по позициям', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      final cart = Cart(lines: [w(0.343, 66)]); // 22,638 → 22,63: 0,008
      expect(cart.weightRounding, closeTo(0.008, 1e-9));
    });

    test('обычные строки не меняются: 0,375 × 3,80 = 1,425 → 1,43 (HALF_UP)', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor});
      expect(Cart(lines: [n(0.375, 3.80)]).total, 1.43);
    });
  });

  group('признаки сервера', () {
    test('разбор: массив, объект «имя → true», вложенный features', () {
      expect(ServerCapabilities.parse(['a', 'b']), {'a', 'b'});
      expect(ServerCapabilities.parse({'a': true, 'b': false, 'c': 'true'}), {'a', 'c'});
      expect(ServerCapabilities.parse({'server_version': 'ceba67c', 'features': ['weight_line_total_floor', 'checkout_branch']}), {'weight_line_total_floor', 'checkout_branch'});
      expect(ServerCapabilities.parse({'capabilities': {'features': {'x': true}}}), {'x'});
      expect(ServerCapabilities.parse(null), isEmpty);
    });

    test('has: нет списка — false; debugSet(null) сбрасывает и правило округления', () {
      expect(ServerCapabilities.has('checkout_branch'), isFalse);
      ServerCapabilities.debugSet({'checkout_branch', ServerCapabilities.weightLineTotalFloor});
      expect(ServerCapabilities.has('checkout_branch'), isTrue);
      expect(ServerRounding.weightLineFloor, isTrue);
      ServerCapabilities.debugSet(null);
      expect(ServerCapabilities.known, isFalse);
      expect(ServerRounding.weightLineFloor, isFalse);
    });
  });

  group('поля чека и ответ checkout', () {
    test('device_key: 8–64 знака, латиница/цифры/_/-', () {
      expect(CheckoutDevice.isValidKey('mob-0123456789abcdef0123456789abcdef'), isTrue);
      expect(CheckoutDevice.isValidKey('short'), isFalse);
      expect(CheckoutDevice.isValidKey('с кириллицей и пробелом'), isFalse);
      expect(CheckoutDevice.isValidKey('a' * 65), isFalse);
      expect(CheckoutDevice.isValidKey(null), isFalse);
    });

    test('филиал из профиля: строка, число, объект с id; нет — пусто', () {
      expect(CheckoutDevice.branchOf({}, {'branch': 'b1'}), 'b1');
      expect(CheckoutDevice.branchOf({}, {'branch_id': 7}), '7');
      expect(CheckoutDevice.branchOf({'branch': {'id': 'x9', 'name': 'Центр'}}, {}), 'x9');
      expect(CheckoutDevice.branchOf({}, {'branch': null}), '');
    });

    test('device_key/device_name/branch — только при checkout_branch; ключ стабилен и допустим', () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      final body = <String, dynamic>{'shift': 's', 'items': <Object>[]};
      expect(await CheckoutDevice.apply(body, db), same(body)); // признака нет — тело прежнее

      ServerCapabilities.debugSet({ServerCapabilities.checkoutBranch});
      final one = await CheckoutDevice.apply(body, db);
      final two = await CheckoutDevice.apply(body, db);
      expect(CheckoutDevice.isValidKey(one['device_key'] as String), isTrue);
      expect(one['device_key'], two['device_key']);
      expect(one['device_name'], isNotEmpty);
      expect(one.containsKey('branch'), isFalse); // филиал неизвестен

      CheckoutDevice.branch = 'br-1';
      expect((await CheckoutDevice.apply(body, db))['branch'], 'br-1');
      expect(body.containsKey('device_key'), isFalse); // исходное тело не тронуто
    });

    test('warnings: lines_merged, below_cost; строки без code отбрасываются; не-список — пусто', () {
      final w = SalesRepository.parseWarnings({
        'warnings': [
          {'code': 'lines_merged', 'index': 1, 'into': 0},
          {'code': 'below_cost', 'index': 2, 'product_id': 'p', 'price': '3.00', 'purchase_price': '3.60'},
          {'index': 3},
          'мусор',
        ],
      });
      expect(w.map((x) => x.code), ['lines_merged', 'below_cost']);
      expect(w.first.data['into'], 0);
      expect(SalesRepository.parseWarnings({'warnings': 'нет'}), isEmpty);
      expect(SalesRepository.parseWarnings(null), isEmpty);
    });

    test('400 duplicate_line распознаётся в разных видах ответа', () {
      expect(SalesRepository.isDuplicateLine({'code': 'duplicate_line', 'duplicate_of': 0}), isTrue);
      expect(SalesRepository.isDuplicateLine({'detail': {'error': 'duplicate_line'}}), isTrue);
      expect(SalesRepository.isDuplicateLine({'code': 'other'}), isFalse);
      expect(SalesRepository.isDuplicateLine(null), isFalse);
    });
  });

  group('пакет «Язык»', () {
    test('код language (ответ ceba67c) и прежний kassa-language', () {
      final now = DateTime(2026, 11, 5);
      expect(LanguagePackGate.parseGranted([{'code': 'language', 'active': true}], now: now), isTrue);
      expect(LanguagePackGate.parseGranted([{'code': 'language', 'active': false}], now: now), isFalse);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-language', 'active': true}], now: now), isTrue);
      expect(LanguagePackGate.parseGranted([{'code': 'language', 'active': true, 'until': '2026-11-01'}], now: now), isFalse);
      expect(LanguagePackGate.parseGranted([{'code': 'language', 'active': false}, {'code': 'kassa-language', 'active': true}], now: now), isTrue);
      expect(LanguagePackGate.parseGranted([], now: now), isFalse);
    });
  });
}
