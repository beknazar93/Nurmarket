// 2026-10-27 (мобилка, видеозвонок, М2): поиск товара в звонке. Жалоба владельца: «видео вызов не находит товар на складе».
// Причина (расследование 27.10, раздел 5а): `db.searchProducts` требовал, чтобы КАЖДОЕ слово запроса целиком входило в карточку:
// «Асу», «асу клубника», «винстон», «адыгене литр», «пепси полтора литра» не находили ничего. Теперь — общий модуль сопоставления
// ([CatalogMatcher] / [ProductMatcher], его же используют умная камера и накладная): одна азбука для латиницы и кириллицы,
// объём отдельно от слов, весь каталог в памяти, топ-N с оценкой. Штрихкод и артикул — точное совпадение первым.
// Решение «нашла» принимает [confidentHit]: лучший ≥ 0,85 И отрыв от второго ≥ 0,15; иначе звонок переспрашивает по кандидатам.
import '../../catalog/catalog_matcher.dart';
import '../../db/database.dart';

export '../../catalog/catalog_matcher.dart' show ProductHit;

/// Необязательная возможность источника данных звонка ([ReadSource], [ActionHost]): поиск с оценкой совпадения. Фейки без неё
/// работают по-старому (подстроки).
abstract interface class ScoredProductSearch {
  Future<List<ProductHit>> findScored(String query, {int top = 5});
}

/// Минимальная оценка кандидата, которого показываем в звонке (ниже — шум).
const double callMinScore = 0.45;

/// Товары по запросу из речи или фото: 1) штрихкод / артикул — точно; 2) матчер по всему каталогу; 3) если матчер ничего не
/// дал — прежний поиск по подстрокам (чтобы не потерять то, что находилось раньше). Пустой запрос — пустой список.
Future<List<ProductHit>> searchForCall(AppDatabase db, String query, {int top = 5, double minScore = callMinScore}) async {
  final q = query.trim();
  if (q.isEmpty) return const [];
  final digits = q.replaceAll(RegExp(r'[\s-]'), '');
  if (RegExp(r'^\d{4,}$').hasMatch(digits)) {
    final hit = await db.productByScan(digits, allowWeightLabel: false);
    if (hit != null) {
      return [ProductHit(hit.product, 1.0, const ['barcode'])];
    }
  }
  final hits = await CatalogMatcher.find(db, q, top: top, minScore: minScore);
  if (hits.isNotEmpty) return hits;
  return [
    for (final p in await db.searchProducts(q, limit: top)) ProductHit(p, 0.5, const ['substring']),
  ];
}

/// Один уверенный ответ: лучший ≥ 0,85 и оторвался от второго на ≥ 0,15. Иначе null — решает человек.
ProductHit? confidentHit(List<ProductHit> hits) => CatalogMatcher.confident(hits);
