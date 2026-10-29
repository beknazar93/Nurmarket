// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»): логика инфографики без экрана — сокращения чисел, дельта к
// прошлому периоду, прошлый период, деления оси, шаг подписей, секторы кольца, текстовые описания для TalkBack на 5 языках.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ui/infographic/chart_logic.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

void main() {
  const ru = CompactWords('тыс', 'млн', 'млрд');
  final nb = RegExp('[  ]');
  String n(String s) => s.replaceAll(nb, ' '); // intl печатает неразрывный пробел

  group('сокращение чисел', () {
    test('до 10 тысяч — целиком, дальше — тыс/млн/млрд с запятой', () {
      expect(n(compactNumber(0, ru)), '0');
      expect(n(compactNumber(9800, ru)), '9 800');
      expect(n(compactNumber(12300, ru)), '12,3 тыс');
      expect(n(compactNumber(1234567, ru)), '1,2 млн');
      expect(n(compactNumber(2000000, ru)), '2 млн');
      expect(n(compactNumber(7.5e9, ru)), '7,5 млрд');
    });
    test('граница разряда не даёт «1 000 тыс»', () {
      expect(n(compactNumber(999990, ru)), '1 млн');
      expect(n(compactNumber(999990000, ru)), '1 млрд');
    });
    test('отрицательные — со знаком «−», минус ноль не печатается, мусор — тире', () {
      expect(n(compactNumber(-12300, ru)), '−12,3 тыс');
      expect(n(compactNumber(-0.001, ru)), '0');
      expect(compactNumber(double.nan, ru), '—');
      expect(compactNumber(double.infinity, ru), '—');
    });
    test('огромные числа не ломаются', () {
      expect(n(compactNumber(1e15, ru)), contains('млрд'));
      expect(compactNumber(1e15, ru).length, lessThan(24));
    });
    test('в разных языках — свои сокращения', () {
      const en = CompactWords('K', 'M', 'B');
      expect(n(compactNumber(12300, en)), '12,3 K');
      expect(n(compactNumber(3500000, en)), '3,5 M');
    });
    test('проценты', () {
      expect(n(percentText(25, 200)), '12,5 %');
      expect(percentText(1, 0), '—');
      expect(percentText(double.nan, 5), '—');
    });
  });

  group('дельта к прошлому периоду', () {
    test('рост и падение', () {
      final up = DeltaInfo.of(112, 100);
      expect(up.kind, DeltaKind.up);
      expect(n(up.text()), '▲ +12 %');
      final down = DeltaInfo.of(96.5, 100);
      expect(down.kind, DeltaKind.down);
      expect(n(down.text()), '▼ −3,5 %');
    });
    test('цвет: рост хорош, у долгов и возвратов — наоборот', () {
      expect(DeltaInfo.of(120, 100).isGood(), isTrue);
      expect(DeltaInfo.of(80, 100).isGood(), isFalse);
      expect(DeltaInfo.of(120, 100).isGood(inverse: true), isFalse);
      expect(DeltaInfo.of(80, 100).isGood(inverse: true), isTrue);
      expect(DeltaInfo.of(100, 100).isGood(), isNull);
      expect(DeltaInfo.of(5, null).isGood(), isNull);
    });
    test('прошлое = 0: «новое» или «без изменений», деления на ноль нет', () {
      expect(DeltaInfo.of(50, 0).kind, DeltaKind.fresh);
      expect(DeltaInfo.of(50, 0).percent, isNull);
      expect(DeltaInfo.of(50, 0).text(freshWord: 'новое'), '▲ новое');
      expect(DeltaInfo.of(0, 0).kind, DeltaKind.same);
    });
    test('прошлого нет или мусор — дельты нет', () {
      expect(DeltaInfo.of(10, null).kind, DeltaKind.none);
      expect(DeltaInfo.of(10, null).text(), '');
      expect(DeltaInfo.of(double.nan, 5).kind, DeltaKind.none);
    });
    test('микроскопическое изменение — «без изменений»; огромное — «>999 %»', () {
      expect(DeltaInfo.of(100.01, 100).kind, DeltaKind.same);
      expect(DeltaInfo.of(1e6, 10).text(), '▲ >999 %');
    });
    test('от отрицательной базы знак по смыслу: было −100, стало −50 — рост', () {
      expect(DeltaInfo.of(-50, -100).kind, DeltaKind.up);
    });
  });

  group('прошлый период', () {
    DateTime d(int m, int day) => DateTime(2026, m, day);
    test('сегодня → вчера', () {
      final p = previousPeriod('today', d(10, 29), d(10, 29));
      expect(p.from, d(10, 28));
      expect(p.to, d(10, 28));
    });
    test('сегодня 1-го числа → последний день прошлого месяца', () {
      final p = previousPeriod('today', d(11, 1), d(11, 1));
      expect(p.from, d(10, 31));
    });
    test('неделя → 7 дней до начала', () {
      final p = previousPeriod('week', d(10, 23), d(10, 29));
      expect(p.from, d(10, 16));
      expect(p.to, d(10, 22));
    });
    test('месяц → тот же отрезок прошлого месяца, не дальше его конца', () {
      final p = previousPeriod('month', d(10, 1), d(10, 29));
      expect(p.from, d(9, 1));
      expect(p.to, d(9, 29));
      final q = previousPeriod('month', d(3, 1), d(3, 31)); // в феврале 28 дней
      expect(q.from, d(2, 1));
      expect(q.to, d(2, 28));
      final y = previousPeriod('month', DateTime(2026, 1, 1), DateTime(2026, 1, 15));
      expect(y.from, DateTime(2025, 12, 1));
      expect(y.to, DateTime(2025, 12, 15));
    });
    test('свой период → столько же дней сразу перед ним', () {
      final p = previousPeriod('custom', d(10, 10), d(10, 14)); // 5 дней
      expect(p.from, d(10, 5));
      expect(p.to, d(10, 9));
    });
  });

  group('ось и подписи', () {
    test('деления включают 0 и ровные шаги', () {
      final t = niceTicks(0, 12300);
      expect(t.first, 0);
      expect(t.last, greaterThanOrEqualTo(12300));
      final step = t[1] - t[0];
      for (var i = 2; i < t.length; i++) {
        expect(t[i] - t[i - 1], closeTo(step, 1e-6));
      }
    });
    test('все нули или одно значение — ось не схлопывается', () {
      expect(niceTicks(0, 0).length, greaterThanOrEqualTo(2));
      expect(niceTicks(5, 5).length, greaterThanOrEqualTo(2));
    });
    test('отрицательные значения — ось уходит ниже нуля и содержит 0', () {
      final t = niceTicks(-500, 1500);
      expect(t.first, lessThan(0));
      expect(t, contains(0));
      expect(t.last, greaterThanOrEqualTo(1500));
    });
    test('мусор вместо границ не падает', () {
      expect(niceTicks(double.nan, double.infinity).length, greaterThanOrEqualTo(2));
    });
    test('шаг подписей: широкие слоты — все, узкие — через несколько', () {
      expect(labelStep(7, 40, 30), 1);
      expect(labelStep(30, 10, 30), 4);
      expect(labelStep(365, 1, 30), greaterThan(30));
      expect(labelStep(1, 300, 30), 1);
      expect(labelStep(5, 0, 30), 1);
    });
    test('подписываемые номера: последний всегда, без наезда на предпоследний', () {
      expect(labelIndices(7, 1), [0, 1, 2, 3, 4, 5, 6]);
      final ix = labelIndices(10, 4);
      expect(ix.first, 0);
      expect(ix.last, 9);
      for (var i = 1; i < ix.length; i++) {
        expect(ix[i] - ix[i - 1], greaterThanOrEqualTo(2));
      }
      expect(labelIndices(0, 3), isEmpty);
      expect(labelIndices(1, 3), [0]);
    });
    test('значения над столбцами — только когда место есть', () {
      expect(showBarValues(7, 40, 30), isTrue);
      expect(showBarValues(30, 10, 30), isFalse);
      expect(showBarValues(7, 20, 30), isFalse);
    });
  });

  group('секторы кольца', () {
    test('нули и отрицательные отбрасываются, остальное по убыванию', () {
      final s = groupSlices(const [Slice('a', 'A', 10), Slice('b', 'B', 0), Slice('c', 'C', -5), Slice('d', 'D', 30)]);
      expect([for (final x in s) x.key], ['d', 'a']);
    });
    test('сверх лимита — «Прочее» с суммой остатка', () {
      final src = [for (var i = 1; i <= 9; i++) Slice('k$i', 'K$i', i.toDouble())];
      final s = groupSlices(src, maxSlices: 6, otherLabel: 'Прочее');
      expect(s.length, 6);
      expect(s.last.key, '_other');
      expect(s.last.label, 'Прочее');
      expect(s.fold<double>(0, (a, x) => a + x.value), 45);
    });
    test('один сектор — вся окружность; пусто — пусто', () {
      expect(sliceFractions(groupSlices(const [Slice('a', 'A', 5)])), [1.0]);
      expect(sliceFractions(const []), isEmpty);
    });
    test('целые проценты всегда дают ровно 100', () {
      for (final f in [
        [1 / 3, 1 / 3, 1 / 3],
        [0.5, 0.25, 0.25],
        [0.126, 0.126, 0.748],
        [1.0],
        [0.005, 0.995],
      ]) {
        expect(wholePercents(f).fold<int>(0, (a, b) => a + b), 100, reason: '$f');
      }
      expect(wholePercents(const []), isEmpty);
    });
  });

  group('статистика ряда', () {
    test('максимум, минимум, сумма; пустой ряд; мусор считается нулём', () {
      final s = BarStats.of([100, 500, 0, 300]);
      expect(s.total, 900);
      expect(s.max, 500);
      expect(s.maxIndex, 1);
      expect(s.min, 0);
      expect(s.minIndex, 2);
      expect(BarStats.of(const []).count, 0);
      expect(BarStats.of([double.nan, 10]).total, 10);
    });
  });

  group('описание для TalkBack — все 5 языков', () {
    for (final code in ['ru', 'ky', 'en', 'tr', 'uz']) {
      test('язык $code', () {
        final l = lookupAppLocalizations(Locale(code));
        final labels = ['Пн 21.10', 'Вт 22.10', 'Ср 23.10'];
        final text = n(describeBars(l, name: 'Выручка по дням', labels: labels, values: [100, 12300, 0], unit: l.som));
        expect(text, contains('Выручка по дням'));
        expect(text, contains('12 300'));
        expect(text, contains('Вт 22.10'));
        expect(text, contains('Ср 23.10'));
        expect(text, contains('12 400')); // всего
        expect(text, isNot(contains('{')));
        // одна точка и пусто
        final one = n(describeBars(l, name: 'Выручка', labels: ['Пн 21.10'], values: [250], unit: l.som));
        expect(one, contains('Пн 21.10'));
        expect(one, contains('250'));
        final none = describeBars(l, name: 'Выручка', labels: const [], values: const [], unit: l.som);
        expect(none, startsWith('Выручка'));
        expect(none.length, greaterThan('Выручка'.length + 3));
        // дельта словами
        final up = DeltaInfo.of(112, 100).semantics(l, versusLabel(l, 'week'));
        expect(up, contains('12'));
        expect(up, isNot(contains('{')));
        expect(DeltaInfo.of(5, null).semantics(l, 'x'), '');
        expect(DeltaInfo.of(0, 0).semantics(l, versusLabel(l, 'today')), isNotEmpty);
        // кольцо
        final ring = describeSlices(l, name: 'Оплата', slices: const [Slice('cash', 'Нал', 50), Slice('card', 'Карта', 30), Slice('t', 'Перевод', 20)]);
        expect(ring, contains('50 %'));
        expect(ring, contains('20 %'));
        // все подписи режимов — разные и не пустые
        final vs = {for (final p in ['today', 'week', 'month', 'custom']) versusLabel(l, p)};
        expect(vs.length, 4);
        // сокращения для чисел есть и различаются
        final w = CompactWords.of(l);
        expect({w.thousand, w.million, w.billion}.length, greaterThanOrEqualTo(2));
      });
    }

    test('по-русски: фраза про максимум читается целиком', () {
      final l = lookupAppLocalizations(const Locale('ru'));
      final text = n(describeBars(l, name: 'Выручка по дням', labels: ['вт', 'ср'], values: [12300, 0], unit: 'сом'));
      expect(text, 'Выручка по дням: максимум 12 300 сом (вт), минимум 0 сом (ср), всего 12 300 сом');
    });
  });
}
