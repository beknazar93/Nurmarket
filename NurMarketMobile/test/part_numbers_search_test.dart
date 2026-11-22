// 2026-11-22 (сфера «Автозапчасти»): поиск по каталожному номеру / OEM в телефоне — номера из attributes кладутся в ключ поиска.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/catalog/part_numbers.dart';
import 'package:nurmarket/core/catalog/search_match.dart';

void main() {
  group('каталожные номера (как PartNumbers.Norm кассы)', () {
    test('нормализация: пробелы, дефисы, слэши убираются, регистр верхний, похожая кириллица → латиница', () {
      expect(PartNumbers.norm('0 986 452-041'), '0986452041');
      expect(PartNumbers.norm('W 712/75'), 'W71275');
      expect(PartNumbers.norm('04465-0К090'), '044650K090'); // К — кириллица
      expect(PartNumbers.norm(''), '');
      expect(PartNumbers.norm('---'), '');
    });

    test('из attributes берутся part_no_norm, oem, oem_N, cross_N; чужие ключи и fits_N — нет; без поля — пусто', () {
      final p = {
        'attributes': [
          {'key': 'part_no_norm', 'value': 'W71294'},
          {'key': 'oem', 'value': '0 986-452-041'},
          {'key': 'oem_2', 'value': 'oe-1'},
          {'key': 'cross_1', 'value': 'MANN W 712/94'},
          {'key': 'fits_1', 'value': 'Toyota Camry'},
          {'key': 'condition', 'value': 'new'},
        ],
      };
      expect(PartNumbers.fromProduct(p), ['W71294', '0986452041', 'OE1', 'MANNW71294']);
      expect(PartNumbers.fromProduct({'name': 'Масло'}), isEmpty);
    });
  });

  group('поиск', () {
    final key = PartNumbers.withNumbers(SearchMatch.key(['Фильтр масляный', 'ART-1', '', 'Фильтры', 'MANN']), ['W71294', '0986452041']);

    test('ключ с номерами находится по номеру, без номеров ключ прежний', () {
      expect(SearchMatch.key(['Фильтр масляный']), PartNumbers.withNumbers(SearchMatch.key(['Фильтр масляный']), const []));
      expect(SearchQuery('w71294').matches(key), isTrue);
      expect(SearchQuery('фильтр 986452').matches(key), isTrue);
    });

    test('номер, набранный с дефисами и слэшами, находится второй попыткой', () {
      final q = SearchQuery('0986-452-041');
      expect(q.matches(key), isFalse);
      expect(q.matchesFuzzy(key), isTrue);
      expect(SearchQuery('W712/94').matchesFuzzy(key), isTrue);
      expect(SearchQuery('W712/95').matchesFuzzy(key), isFalse);
    });

    test('хвост номеров читается из готового ключа (частичное обновление карточки его сохраняет)', () {
      expect(PartNumbers.tailOf(key), ['w71294', '0986452041']);
      expect(PartNumbers.tailOf('просто ключ'), isEmpty);
    });
  });
}
