import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/scan/shot_flow.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';

// 2026-10-22, владелец: «при обучении фоткает только 1 фото и потом чёрный экран», «умный сканер сам сразу сканирует —
// добавь кнопку». Состояния видоискателя без камеры: снимки подряд, двойное нажатие, «Переснять», продажа скан за сканом.
Uint8List _shot(int n) => Uint8List.fromList([n]);

Product _p(String id) => Product(
  id: id,
  name: 'p$id',
  price: 10,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 5,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: 'p$id',
);

void main() {
  test('обучение: 5 снимков подряд на одной странице, «Фото N из 5», «Готово» с первого снимка', () {
    final f = ShotFlow(maxShots: 5, minShots: 1);
    expect(f.phase, ShotPhase.aim);
    expect(f.current, 1);
    expect(f.canFinish, isFalse);
    for (var i = 1; i <= 5; i++) {
      expect(f.current, i);
      expect(f.begin(), isTrue);
      expect(f.busy, isTrue);
      f.taken(_shot(i));
      expect(f.phase, ShotPhase.aim);
      expect(f.shots.length, i);
      expect(f.canFinish, isTrue);
    }
    expect(f.full, isTrue);
    expect(f.canShoot, isFalse);
    expect(f.begin(), isFalse, reason: 'шестой снимок не делается');
    expect(f.current, 5);
    expect([for (final s in f.shots) s.first], [1, 2, 3, 4, 5]);
  });

  test('двойное нажатие «Сканировать» — один кадр', () {
    final f = ShotFlow(maxShots: 5);
    expect(f.begin(), isTrue);
    expect(f.begin(), isFalse);
    expect(f.canFinish, isFalse, reason: 'пока идёт снимок, «Готово» недоступна');
    f.taken(_shot(1));
    expect(f.shots.length, 1);
  });

  test('ошибка камеры и «Переснять»', () {
    final f = ShotFlow(maxShots: 3, minShots: 3);
    f.begin();
    f.failed();
    expect(f.phase, ShotPhase.aim);
    expect(f.shots, isEmpty);
    for (var i = 0; i < 3; i++) {
      f.begin();
      f.taken(_shot(i));
    }
    expect(f.canFinish, isTrue);
    f.removeLast();
    expect(f.shots.length, 2);
    expect(f.canFinish, isFalse);
    expect(f.current, 3);
    expect(f.canShoot, isTrue);
  });

  test('продажа: «Наведите» → «Сканирую…» до ответа ИИ → результат → следующий скан', () {
    final f = ShotFlow(maxShots: 0, keep: false);
    expect(f.begin(), isTrue);
    f.taken(_shot(1), waitResult: true);
    expect(f.phase, ShotPhase.scanning, reason: 'кадр есть, ИИ ещё думает');
    expect(f.begin(), isFalse);
    f.showResult();
    expect(f.phase, ShotPhase.result);
    expect(f.shots, isEmpty, reason: 'кадры продажи не копятся в памяти');
    for (var i = 0; i < 20; i++) {
      expect(f.begin(), isTrue, reason: 'без ограничения числа сканов');
      f.taken(_shot(i), waitResult: true);
      f.showResult(hasResult: i.isEven);
    }
    expect(f.phase, ShotPhase.aim);
  });

  test('точные совпадения — первыми, без повторов товара', () {
    final a = _p('a'), b = _p('b');
    final first = [
      SeenMatch(const SeenItem(name: 'Mars'), [(a, .7)]),
      SeenMatch(const SeenItem(name: 'Snickers'), [(b, .6)]),
      const SeenMatch(SeenItem(name: 'нет в базе'), []),
    ];
    final exact = [
      SeenMatch(const SeenItem(name: '✓ pa', count: 2), [(a, 1.0)]),
      SeenMatch(const SeenItem(name: '✓ pa'), [(a, 1.0)]),
    ];
    final merged = mergeExact(exact, first);
    expect(merged.map((m) => m.seen.name).toList(), ['✓ pa', 'Snickers', 'нет в базе']);
    expect(merged.first.seen.count, 2);
    expect(mergeExact(const [], first), same(first));
  });
}
