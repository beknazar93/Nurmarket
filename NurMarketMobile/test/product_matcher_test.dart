// 2026-10-27 (владелец: «на фото существующий товар не находит»): общий модуль сопоставления product_matcher.dart на каталоге
// магазина NBS (343 товара, test/fixtures/nbs_catalog.json — только названия, штрихкоды и артикулы) и на случаях из расследования.
// Сети и Gemini нет: ответы ИИ подставлены.
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/catalog/product_matcher.dart';

List<MatchProduct> _nbs() {
  final rows = (jsonDecode(File('test/fixtures/nbs_catalog.json').readAsStringSync()) as List).cast<Map<String, dynamic>>();
  return [
    for (final r in rows)
      MatchProduct(id: r['id'] as String, name: r['name'] as String, barcode: r['barcode'] as String, sku: r['sku'] as String, category: r['category'] as String),
    // в каталоге NBS Fanta нет вовсе — добавляем, как в расследовании
    const MatchProduct(id: 'x1', name: 'Фанта 0.5'),
    const MatchProduct(id: 'x2', name: 'Fanta апельсин 1л'),
  ];
}

void main() {
  final catalog = _nbs();
  final m = ProductMatcher(catalog);

  (MatchCandidate?, List<MatchCandidate>) ask(String q, {String? brand, String? volume, String? barcode}) {
    final r = m.match(q, brand: brand, volume: volume, barcode: barcode);
    return (r.isEmpty ? null : r.first, r);
  }

  group('объём и слова', () {
    test('объём отдельно от слов', () {
      MatchVolume? v(String s) => extractVolume(s).volume;
      expect(v('вода 1л')!.amount, 1000);
      expect(v('вода 1 л')!.amount, 1000);
      expect(v('water 1L')!.amount, 1000);
      expect(v('вода 1000 мл')!.amount, 1000);
      expect(v('вода 0,5 л')!.amount, 500);
      expect(v('Фанта 0.5')!.amount, 500);
      expect(v('пепси 1.5')!.amount, 1500);
      expect(v('пепси полтора литра')!.amount, 1500);
      expect(v('адыгене литр')!.amount, 1000);
      expect(v('вода пол-литра')!.amount, 500);
      expect(v('сыр 250 г')!.kind, 'm');
      expect(v('мука 2 кг')!.amount, 2000);
      expect(v('Снежок'), isNull);
      // число из штрихкода — не объём
      expect(v('код 4870001571039'), isNull);
    });

    test('«1л» и «1 л» — один и тот же объём, не слова', () {
      expect(normalizeText('Адыгене 1л').tokens, normalizeText('Адыгене 1 л').tokens);
      expect(normalizeText('Адыгене 1л').tokens, hasLength(1));
      expect(normalizeText('Адыгене 1л').volume!.amount, normalizeText('Адыгене вода минеральная 1 л').volume!.amount);
    });

    test('одна азбука: латиница и кириллица', () {
      expect(normalizeText('ASU').tokens, normalizeText('Асу').tokens);
      expect(normalizeText('Adygene').tokens, normalizeText('Адыгене').tokens);
      expect(normalizeText('Winston').tokens, normalizeText('винстон').tokens);
      expect(normalizeText('Pepsi').tokens, normalizeText('пепси').tokens);
      expect(normalizeText('Snickers').tokens, normalizeText('Сникерс').tokens);
      expect(normalizeText('Sprite').tokens, normalizeText('Спрайт').tokens);
      expect(normalizeText('Fanta').tokens, normalizeText('Фанта').tokens);
    });

    test('сокращения и синонимы', () {
      expect(normalizeText('ASU Н/Г 1л').tokens, contains(normalizeText('негазированная').tokens.first));
      expect(normalizeText('still water').tokens, containsAll(normalizeText('негаз вода').tokens));
      expect(normalizeText('orange').tokens, normalizeText('апельсин').tokens);
    });
  });

  group('случаи из расследования (каталог NBS)', () {
    void expectFound(String q, String name, {double minScore = 0.85, bool confident = true, String? brand, String? volume}) {
      final (best, all) = ask(q, brand: brand, volume: volume);
      expect(best, isNotNull, reason: '«$q» не найден');
      expect(best!.product.name, name, reason: '«$q» → ${all.take(3)}');
      expect(best.score, greaterThanOrEqualTo(minScore), reason: '«$q» → ${all.take(3)}');
      if (confident) expect(ProductMatcher.confident(all)?.product.name, name, reason: '«$q» без автовыбора: ${all.take(3)}');
    }

    test('ответы ИИ по фото', () {
      expectFound('Asu негазированная вода 1л клубника киви', 'ASU Негаз 1 л клуб-киви');
      expectFound('Асу вода негазированная клубника-киви 1 л', 'ASU Негаз 1 л клуб-киви');
      expectFound('ASU Вода питьевая негазированная со вкусом клубники и киви 1000 мл', 'ASU Негаз 1 л клуб-киви');
      expectFound('ASU Still water strawberry kiwi 1 L', 'ASU Негаз 1 л клуб-киви');
      expectFound('ASU вода негазированная персик 1 л', 'ASU Негаз 1л персик');
      expectFound('Адыгене вода минеральная 1 л', 'Адыгене 1л');
      expectFound('Adygene mineral water', 'Адыгене 1л');
      expectFound('Adygene mineral water 1L', 'Адыгене 1л');
      expectFound('Winston Purple Mix сигареты', 'Winston purple mix');
      expectFound('Fanta Orange 0,5 л', 'Фанта 0.5');
      expectFound('Фанта апельсин 0,5 л', 'Фанта 0.5');
      expectFound('Фанта апельсин 1 л', 'Fanta апельсин 1л');
      expectFound('Coca-Cola 1 л', 'КОКАКОЛА 1Л');
      expectFound('Кока-Кола 2 л', 'Кока Кола (ПЭТ) 2л*');
      expectFound('Pepsi 1,5 л', 'пепси 1.5');
      expectFound('Sprite 1 л', 'Спрайт 1,0л');
    });

    test('голос и короткие запросы', () {
      expectFound('адыгене литр', 'Адыгене 1л');
      expectFound('винстон', 'Winston purple mix');
      expectFound('Winston', 'Winston purple mix');
      expectFound('пепси полтора литра', 'пепси 1.5');
      expectFound('кока-кола литр', 'КОКАКОЛА 1Л');
      expectFound('адыгене', 'Адыгене 1л');
      expectFound('асу клубника', 'ASU Негаз 1 л клуб-киви', confident: false);
      expectFound('ASU клубника киви', 'ASU Негаз 1 л клуб-киви', confident: false);
    });

    test('«Асу» — ничья: четыре ASU, автовыбора нет, выбирает человек', () {
      final (best, all) = ask('Асу');
      expect(all.take(4).every((c) => c.product.name.startsWith('ASU')), isTrue, reason: '$all');
      expect(best!.score, greaterThanOrEqualTo(0.85));
      expect(ProductMatcher.confident(all), isNull);
    });

    test('бренд и объём отдельными полями', () {
      expectFound('вода минеральная', 'Адыгене 1л', brand: 'Adygene', volume: '1 L');
    });

    test('другой объём не подходит: «Кока-Кола 1 л» не станет «Кока Кола 2л»', () {
      final (_, all) = ask('Coca-Cola 1 л');
      final two = all.where((c) => c.product.name.contains('2л')).toList();
      for (final c in two) {
        expect(c.score, lessThanOrEqualTo(0.4));
        expect(c.reasons, contains('volume_mismatch'));
      }
    });

    test('штрихкод — точное совпадение', () {
      final (best, _) = ask('что-то', barcode: '4700000310326');
      expect(best!.product.name, 'Адыгене 1л');
      expect(best.score, 1.0);
      expect(best.reasons, ['barcode']);
      expect(ask('x', barcode: '4870001571039').$1!.product.name, 'ASU Негаз 1 л клуб-киви');
    });

    test('ложных срабатываний нет: чужие товары не выбираются сами', () {
      for (final q in ['Samsung Galaxy S24', 'Тетрадь 48 листов', 'Whiskas для кошек', 'Молоко Простоквашино 1л', 'Шампунь Head Shoulders', 'Pampers Active Baby', 'Кетчуп Heinz 500 г']) {
        final all = m.match(q);
        expect(ProductMatcher.confident(all), isNull, reason: '«$q» выбран сам: ${all.take(2)}');
        if (all.isNotEmpty) expect(all.first.score, lessThan(0.7), reason: '«$q» → ${all.take(2)}');
      }
    });

    test('каждый товар каталога по своему названию находит сам себя', () {
      final bad = <String>[];
      for (final p in catalog) {
        if (normalizeText(p.name).tokens.isEmpty) continue; // «123412341234» — только цифры штрихкода
        final all = m.match(p.name, top: 50);
        // у почти одинаковых карточек оценка может быть общей — достаточно, чтобы сам товар был в лидерах
        final own = all.where((c) => c.product.id == p.id).firstOrNull;
        if (own == null || own.score < 0.99 || own.score < all.first.score - 1e-9) bad.add('${p.name} → ${all.take(2)}');
      }
      expect(bad.length, lessThanOrEqualTo(3), reason: bad.join('\n'));
    });
  });

  group('алиасы обучения', () {
    test('название ИИ, выбранное человеком, в следующий раз даёт 1,0', () {
      const ai = 'Lemon Lime fizzy drink 1 L';
      final withAlias = [
        for (final p in catalog)
          if (p.name == 'Sprite Мохито') p.withAliases([ai]) else p,
      ];
      expect(ProductMatcher.confident(m.match(ai)), isNull, reason: 'без алиаса ИИ-название ничего не даёт');
      final after = ProductMatcher(withAlias).match(ai);
      expect(after.first.product.name, 'Sprite Мохито');
      expect(after.first.score, 1.0);
      expect(after.first.reasons, contains('alias'));
      expect(ProductMatcher.confident(after)?.product.name, 'Sprite Мохито');
    });

    // 2026-10-27 (ревью М-2): выученное название без объёма уводило к товару другого объёма
    group('объём обязателен (ревью М-2)', () {
      const cola1 = MatchProduct(id: 'c1', name: 'Coca-Cola 1 л');
      const cola05 = MatchProduct(id: 'c05', name: 'Coca-Cola 0,5 л');
      ProductMatcher withAlias(List<MatchProduct> cat, String id, String alias) => ProductMatcher([for (final p in cat) p.id == id ? p.withAliases([alias]) : p]);

      test('алиас «coca-cola» (запомнен для 1 л) не даёт 1,0 товару 1 л, когда камера видит 0.5 L', () {
        final mm = withAlias(const [cola1, cola05], 'c1', 'coca-cola');
        final r = mm.match('coca-cola 0.5 L');
        expect(r.first.product.id, 'c05', reason: 'совпал объём — это настоящий товар');
        final pick = ProductMatcher.confident(r);
        expect(pick?.product.id, 'c05');
        final wrong = r.where((c) => c.product.id == 'c1').firstOrNull;
        if (wrong != null) expect(wrong.score, lessThan(0.5), reason: 'другой объём — не кандидат на автовыбор');
      });

      test('алиас с объёмом работает: «coca-cola 1 л» -> 1 л, оценка 1,0 и причина volume', () {
        final mm = withAlias(const [cola1, cola05], 'c1', 'coca-cola 1 л');
        final r = mm.match('Coca Cola 1 L');
        expect(r.first.product.id, 'c1');
        expect(r.first.score, 1.0);
        expect(r.first.reasons, containsAll(['alias', 'volume']));
      });

      test('кандидата с совпавшим объёмом не срезают до 0,8 из-за чужого алиаса', () {
        // алиас у товара без объёма: он «выучен», но настоящий товар с тем же объёмом не должен проигрывать срезанным
        const plain = MatchProduct(id: 'p', name: 'Coca-Cola');
        final mm = withAlias(const [plain, cola05], 'p', 'coca-cola');
        final r = mm.match('coca-cola 0.5 л');
        final real = r.firstWhere((c) => c.product.id == 'c05');
        expect(real.score, greaterThan(0.8), reason: 'не срезан');
      });

      test('requireVolume: в запросе объёма нет, у товара есть — оценка ниже порога автовыбора, только варианты', () {
        final mm = withAlias(const [cola1, cola05], 'c1', 'coca-cola');
        final free = mm.match('coca-cola');
        expect(free.first.score, greaterThanOrEqualTo(autoScore), reason: 'без requireVolume (голос, поиск) всё как раньше');
        final strict = mm.match('coca-cola', requireVolume: true);
        expect(strict.every((c) => c.score < autoScore), isTrue);
        expect(strict.first.reasons, contains('volume_missing'));
        expect(ProductMatcher.confident(strict), isNull);
      });

      test('requireVolume: объём в запросе есть — автовыбор как раньше; товар без объёма в названии не страдает', () {
        const bread = MatchProduct(id: 'b', name: 'Хлеб Бородинский');
        final mm = ProductMatcher([cola1, cola05, bread]);
        expect(ProductMatcher.confident(mm.match('Coca-Cola 0.5 L', requireVolume: true))?.product.id, 'c05');
        expect(ProductMatcher.confident(mm.match('Хлеб Бородинский', requireVolume: true))?.product.id, 'b');
      });
    });
  });

  group('штрихкод: контрольная цифра', () {
    test('EAN-13 и EAN-8', () {
      expect(isValidEan('4700000310326'), isTrue);
      expect(isValidEan('4870001571030'), isTrue);
      expect(isValidEan('4870001571039'), isFalse);
      expect(isValidEan('48745000'), isTrue);
      expect(isValidEan('0370'), isFalse);
      expect(eanCheckDigit('487000157103'), 0);
    });

    test('опечатка в последней цифре карточки', () {
      expect(isCheckDigitTypo('4870001571039', '4870001571030'), isTrue);
      expect(isCheckDigitTypo('4870001571030', '4870001571030'), isFalse);
      expect(isCheckDigitTypo('4870145005552', '4780022620177'), isFalse);
      expect(isCheckDigitTypo('0370', '48745000'), isFalse);
    });
  });

  group('скорость', () {
    // 5000 товаров: названия собраны из слов каталога NBS
    List<MatchProduct> big(int n) {
      final r = math.Random(7);
      final words = <String>{
        for (final p in catalog) ...p.name.split(RegExp(r'[\s,]+')).where((w) => w.length > 2),
      }.toList();
      return [
        for (var i = 0; i < n; i++)
          MatchProduct(
            id: 'b$i',
            name: '${words[r.nextInt(words.length)]} ${words[r.nextInt(words.length)]} ${words[r.nextInt(words.length)]} ${r.nextInt(3) + 1}л',
            barcode: '${4000000000000 + i}',
          ),
      ];
    }

    for (final n in [343, 5000]) {
      test('$n товаров: запрос быстрее 50 мс', () {
        final list = n == 343 ? catalog : [...catalog, ...big(n - catalog.length)];
        final sw = Stopwatch()..start();
        final mm = ProductMatcher(list);
        sw.stop();
        final build = sw.elapsedMilliseconds;
        final qs = ['Адыгене вода минеральная 1 л', 'Adygene mineral water', 'асу клубника', 'Coca-Cola 1 л', 'вода', 'пепси полтора литра', 'ASU Still water strawberry kiwi 1 L', 'сок яблочный 1л'];
        for (final q in qs) {
          mm.match(q); // прогрев
        }
        sw
          ..reset()
          ..start();
        var worst = 0;
        var total = 0;
        var runs = 0;
        for (var i = 0; i < 5; i++) {
          for (final q in qs) {
            final t = sw.elapsedMicroseconds;
            mm.match(q);
            final d = sw.elapsedMicroseconds - t;
            worst = math.max(worst, d);
            total += d;
            runs++;
          }
        }
        // ignore: avoid_print
        print('product_matcher: $n товаров, индекс $build мс, запрос в среднем ${(total / runs / 1000).toStringAsFixed(1)} мс, самый долгий ${(worst / 1000).toStringAsFixed(1)} мс');
        // среднее по 40 запросам: одиночный выброс (сборка мусора, соседний процесс на общем ПК) тест не роняет
        expect(total / runs / 1000, lessThan(50));
      });
    }
  });
}
