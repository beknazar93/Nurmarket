import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../core/units.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';

// 2026-10-29: выбор единицы измерения — один на «Новый товар», карточку товара, приёмку и массовую загрузку (владелец: «нет
// единицы — шт, кг, литр»). Кнопки шт, кг, г, л, мл, м, упак, пара, компл + «Своя»; под ними подпись: что это за единица и
// можно ли дробное количество (0,5) или только целое. Что хранится — код каталога ([UnitDef.code]); на экране его имя
// на языке приложения.

/// Название единицы на языке приложения («Килограмм»); своя единица — как ввели.
String unitName(AppLocalizations l, String code) => switch (code) {
  'шт' => l.unitNamePcs,
  'кг' => l.unitNameKg,
  'г' => l.unitNameG,
  'л' => l.unitNameL,
  'мл' => l.unitNameMl,
  'м' => l.unitNameM,
  'упак' => l.unitNamePack,
  'пара' => l.unitNamePair,
  'компл' => l.unitNameSet,
  _ => code,
};

/// Подпись под выбором: «Килограмм: можно дробное количество, например 0,5» / «Штука: только целое количество».
String unitHint(AppLocalizations l, String code) {
  final def = unitOf(code);
  if (def == null) return l.unitHintCustom;
  return def.fractional ? l.unitHintFrac(unitName(l, def.code)) : l.unitHintWhole(unitName(l, def.code));
}

/// Окно «Своя единица». null — отменили или пусто.
Future<String?> askCustomUnit(BuildContext context, {String initial = ''}) {
  final l = context.l;
  final ctrl = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(l.unitCustomTitle),
      content: TextField(
        key: const Key('unitCustomField'),
        controller: ctrl,
        autofocus: true,
        maxLength: 12,
        decoration: InputDecoration(hintText: l.unitCustomHint),
        onSubmitted: (v) => Navigator.pop(c, v.trim()),
      ),
      actions: [
        TextButton(style: TextButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: () => Navigator.pop(c), child: Text(l.cancel)),
        FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: () => Navigator.pop(c, ctrl.text.trim()), child: Text(l.done)),
      ],
    ),
  ).then((v) => (v == null || v.isEmpty) ? null : v);
}

class UnitPicker extends StatelessWidget {
  final String value;
  final ValueChanged<String> onChanged;

  /// Показывать подпись «можно дробное / только целое» (в массовых окнах она не нужна).
  final bool showHint;
  const UnitPicker({super.key, required this.value, required this.onChanged, this.showHint = true});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final known = unitOf(value);
    // своя единица (нет в каталоге) показывается отдельной выбранной кнопкой
    final custom = known == null && value.trim().isNotEmpty ? value.trim() : null;
    final current = known?.code ?? custom;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            for (final u in kUnits)
              ChoiceChip(key: Key('unit_${u.code}'), label: Text(u.code), selected: current == u.code, onSelected: (_) => onChanged(u.code)),
            if (custom != null) ChoiceChip(key: const Key('unit_custom_value'), label: Text(custom), selected: true, onSelected: (_) => _ask(context)),
            ActionChip(key: const Key('unit_custom'), avatar: const Icon(Icons.edit_outlined, size: 16), label: Text(l.unitCustom), onPressed: () => _ask(context)),
          ],
        ),
        if (showHint && current != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(unitHint(l, current), key: const Key('unitHint'), style: TextStyle(color: context.pal.textSoft, fontSize: 12)),
          ),
      ],
    );
  }

  Future<void> _ask(BuildContext context) async {
    final v = await askCustomUnit(context, initial: unitOf(value) == null ? value : '');
    if (v != null) onChanged(normalizeUnit(v));
  }
}

/// Предупреждение для поля количества: у целой единицы дробное число — ошибка. null — всё в порядке.
String? qtyErrorText(AppLocalizations l, String unit, double? qty, {bool packed = false}) {
  if (qty == null) return null;
  return switch (qtyProblem(unit, qty, packed: packed)) {
    QtyProblem.negative => l.nuQtyNegative,
    QtyProblem.notWhole => l.nuQtyWhole(unit, qtyText(qty)),
    null => null,
  };
}

/// Цвет подсказки об ошибке (общий для форм).
const unitErrorColor = T.danger;
