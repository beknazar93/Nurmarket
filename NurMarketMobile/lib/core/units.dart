// 2026-10-29: единый каталог единиц измерения товара (владелец: «в добавлении товара нет цены, количества и единицы
// шт/кг/литр»). Раньше четыре кнопки «шт, кг, л, упак» были переписаны в трёх экранах, а файл Excel/CSV и накладная
// понимали единицы каждый по-своему. Здесь: код единицы (как хранится на сервере и в кассе — по-русски, «шт», «кг»),
// можно ли дробное количество, весовой ли товар (is_weight) и все написания на ru/ky/uz/en/tr для разбора файлов.
// Чистый Dart без Flutter — проверяется тестами без экрана.

class UnitDef {
  /// Код единицы: ровно то, что уходит в поле unit сервера и видно на кассе.
  final String code;

  /// Количество (остаток, приход) может быть дробным: 0,5 кг, 1,25 л, 2,5 м. Для «шт» и «пара» — только целое.
  final bool fractional;

  /// Весовой товар (is_weight=true): продаётся с весов/вводом веса. Совпадает с isWeightUnit() карточки товара.
  final bool weight;

  /// Написания в файле/речи (уже в нормальном виде — см. [unitKey]).
  final Set<String> aliases;
  const UnitDef(this.code, {this.fractional = false, this.weight = false, this.aliases = const {}});
}

/// Порядок — как кнопки на экране. «упак» не дробная: коробки считают целыми (долю пачки даёт поштучная продажа).
const kUnits = <UnitDef>[
  UnitDef('шт', aliases: {'шт', 'штука', 'штук', 'штуки', 'sht', 'pcs', 'pc', 'piece', 'pieces', 'дана', 'даана', 'dona', 'ta', 'adet', 'ед', '796'}),
  UnitDef('кг', fractional: true, weight: true, aliases: {'кг', 'килограмм', 'килограммы', 'килограмма', 'килограм', 'кило', 'kg', 'kgs', 'kilo', 'kilogram', 'kilograms', 'kilogramm', 'кгр', '166'}),
  UnitDef('г', fractional: true, weight: true, aliases: {'г', 'гр', 'грамм', 'грамма', 'граммы', 'g', 'gr', 'gram', 'gramm', '163'}),
  UnitDef('л', fractional: true, aliases: {'л', 'литр', 'литра', 'литры', 'l', 'lt', 'ltr', 'litr', 'liter', 'litre', '112'}),
  UnitDef('мл', fractional: true, aliases: {'мл', 'миллилитр', 'миллилитры', 'ml', 'millilitr', 'milliliter', '111'}),
  UnitDef('м', fractional: true, aliases: {'м', 'метр', 'метра', 'метры', 'пог м', 'погм', 'погонный метр', 'm', 'mt', 'metr', 'meter', 'metre', '006', '6'}),
  UnitDef('упак', aliases: {'упак', 'уп', 'упаковка', 'упаковки', 'пач', 'пачка', 'pack', 'pk', 'package', 'upak', 'qadoq', 'paket', 'таңгак', 'коробка', 'кор', 'box', 'quti', '778'}),
  UnitDef('пара', aliases: {'пара', 'пар', 'пары', 'pair', 'juft', 'çift', 'cift', 'жуп', '715'}),
  UnitDef('компл', aliases: {'компл', 'комплект', 'комплекты', 'набор', 'наб', 'set', 'toplam', 'takım', 'takim', 'жыйнак', '839', '704'}),
];

/// Ключ для сравнения: нижний регистр, без точек, апострофов и лишних пробелов («Ед. изм.» → «ед изм»).
String unitKey(String raw) => raw
    .toLowerCase()
    .replaceAll('ё', 'е')
    .replaceAll(RegExp("[.ʻʼ’'`]"), '')
    .replaceAll(RegExp(r'\s+'), ' ')
    .trim();

/// Единица из каталога по любому написанию («Штука», «pcs», «килограмм», «Lt.») или null — не знаем.
UnitDef? unitOf(String? raw) {
  final k = unitKey(raw ?? '');
  if (k.isEmpty) return null;
  for (final u in kUnits) {
    if (u.code == k || u.aliases.contains(k)) return u;
  }
  return null;
}

/// Код единицы для отправки: известная — к коду каталога, пустая — «шт», незнакомая — как ввели (своя единица).
String normalizeUnit(String? raw) {
  final t = (raw ?? '').trim();
  if (t.isEmpty) return 'шт';
  return unitOf(t)?.code ?? t;
}

/// Можно ли дробное количество. Своя (незнакомая) единица — можно: мы не знаем, что в ней считают.
bool unitIsFractional(String? unit) => unitOf(unit)?.fractional ?? true;

/// Количество допустимо для этой единицы? null — да; иначе причина.
enum QtyProblem { negative, notWhole }

QtyProblem? qtyProblem(String? unit, double qty, {bool packed = false}) {
  if (qty < 0) return QtyProblem.negative;
  // товар с упаковкой: остаток в упаковках бывает дробным («12 уп. + 5 шт» хранится как 12,25)
  if (!packed && !unitIsFractional(unit) && (qty - qty.roundToDouble()).abs() > 1e-9) return QtyProblem.notWhole;
  return null;
}
