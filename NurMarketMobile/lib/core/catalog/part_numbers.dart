// 2026-11-22 (сфера «Автозапчасти», владелец: «в мобилке нет сферы Базар и Автозапчасти»): каталожные номера в поиске телефона.
// Перенос NurMarketKassa.Core/Application/PartNumbers.cs (Norm): сервер пробелы и дефисы в номерах НЕ нормализует (ответ Умара 19.11),
// поэтому номер хранится и ищется в одном виде — заглавные латинские буквы и цифры без пробелов, дефисов, точек и слэшей.
// Данные берутся из attributes товара (ключи part_no_norm, oem / oem_N, cross_N — как AutoPartInfo.Parse кассы) и кладутся в ключ поиска
// товара (products.search_key) отдельным хвостом после [marker]; новых колонок и серверных контрактов не нужно.

class PartNumbers {
  /// Невидимый разделитель: всё после него в ключе поиска — нормализованные каталожные номера.
  static const marker = '⁣';

  /// Самый длинный номер (значение атрибута на сервере — до 64 знаков).
  static const maxLength = 64;

  static const _lookalike = {
    'А': 'A', 'а': 'A', 'В': 'B', 'в': 'B', 'Е': 'E', 'е': 'E', 'К': 'K', 'к': 'K', 'М': 'M', 'м': 'M', 'Н': 'H', 'н': 'H',
    'О': 'O', 'о': 'O', 'Р': 'P', 'р': 'P', 'С': 'C', 'с': 'C', 'Т': 'T', 'т': 'T', 'Х': 'X', 'х': 'X', 'У': 'Y', 'у': 'Y',
    'Й': 'Q', 'й': 'Q', 'Ц': 'W', 'ц': 'W',
  };

  static final RegExp _letterOrDigit = RegExp(r'[0-9A-Za-zЀ-ӿ]');

  /// «0 986 452-041» → «0986452041», «04465-0К090» (К — кириллица) → «044650K090». Пусто, если букв и цифр нет.
  static String norm(String? value) {
    if (value == null || value.isEmpty) return '';
    final sb = StringBuffer();
    var n = 0;
    for (final r in value.runes) {
      final ch = String.fromCharCode(r);
      if (!_letterOrDigit.hasMatch(ch)) continue;
      sb.write(_lookalike[ch] ?? ch.toUpperCase());
      if (++n >= maxLength) break;
    }
    return sb.toString();
  }

  static bool _isNumberKey(String key) => key == 'part_no_norm' || key == 'oem' || key.startsWith('oem_') || key.startsWith('cross_');

  /// Нормализованные номера товара из ответа сервера (`attributes: [{key, value}]`). Нет поля — пусто.
  static List<String> fromProduct(Map<dynamic, dynamic> product) {
    final attrs = product['attributes'];
    if (attrs is! List) return const [];
    final out = <String>[];
    for (final a in attrs) {
      if (a is! Map) continue;
      final key = (a['key'] ?? '').toString().trim();
      if (!_isNumberKey(key)) continue;
      final v = norm((a['value'] ?? '').toString());
      if (v.isNotEmpty && !out.contains(v)) out.add(v);
    }
    return out;
  }

  /// Ключ поиска + хвост с номерами. Номеров нет — ключ как был.
  static String withNumbers(String key, Iterable<String> numbers) {
    final list = numbers.where((x) => x.isNotEmpty).toList();
    return list.isEmpty ? key : '$key $marker ${list.join(' ').toLowerCase()}';
  }

  /// Хвост с номерами из готового ключа (пусто, если его нет).
  static List<String> tailOf(String? key) {
    final i = (key ?? '').indexOf(marker);
    if (i < 0) return const [];
    return key!.substring(i + marker.length).trim().split(' ').where((x) => x.isNotEmpty).toList();
  }

  /// Слово запроса как номер: «0986-452-041» → «0986452041» (если в слове есть цифра, знаки-разделители и результат не короче 3 знаков).
  static String? queryVariant(String token) {
    if (!RegExp(r'\d').hasMatch(token) || !RegExp(r'[-./\_]').hasMatch(token)) return null;
    final n = norm(token).toLowerCase();
    return n.length >= 3 && n != token ? n : null;
  }
}
