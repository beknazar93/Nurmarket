import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import 'barcode_lookup.dart';

/// 2026-11-05 (мобилка, М5-12, М5-13, М5-14): всё, что сканер и ручной сканер делают с ВЕСОВОЙ этикеткой сверх обычного поиска.
/// Как на кассе (ProductCodeLookup, 31.10): неизвестный PLU не ведёт к «Создать товар со штрихкодом этикетки» (штрихкод этикетки
/// каждый раз другой — такой товар-двойник бесполезен), повтор той же этикетки не ложится в чек сам, а раскладка «не задана»
/// не угадывается молча.

/// Коды весовых этикеток, уже лежащие в ТЕКУЩЕМ чеке. Тот же код второй раз без вопроса не добавляется; чек опустел — память чистая.
class ScaleLabelMemory {
  final _codes = <String>{};

  bool contains(String code) => _codes.contains(code);

  void add(String code) => _codes.add(code);

  /// Чек пуст (оплатили, очистили) — прежние этикетки больше не «в чеке».
  void syncWithCart({required bool cartEmpty}) {
    if (cartEmpty) _codes.clear();
  }

  bool get isEmpty => _codes.isEmpty;
}

/// «Эта этикетка уже в чеке. Добавить ещё раз?»
Future<bool> confirmLabelAgain(BuildContext context) async {
  final l = context.l;
  final r = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      content: Text(l.scaleLabelAgain),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.addToCheck)),
      ],
    ),
  );
  return r == true;
}

/// «Это весовая этикетка, а «X» — штучный товар. Добавить?»
Future<bool> confirmPieceProduct(BuildContext context, Product p) async {
  final l = context.l;
  final r = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      content: Text(l.scaleNotWeighed(p.name)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.addToCheck)),
      ],
    ),
  );
  return r == true;
}

/// Раскладка этикеток в настройках компании не задана, и два прочтения нашли РАЗНЫЕ товары — выбор из двух (с весом).
/// null — отказались.
Future<({Product product, double? qty})?> pickLabelReading(BuildContext context, List<({Product product, double? qty, bool weighed})> hits) {
  final l = context.l;
  return showDialog<({Product product, double? qty})>(
    context: context,
    builder: (c) => SimpleDialog(
      title: Text(l.scaleLayoutPick),
      children: [
        for (final h in hits)
          SimpleDialogOption(
            onPressed: () => Navigator.pop(c, (product: h.product, qty: h.qty)),
            child: Text(h.qty == null ? h.product.name : '${h.product.name} · ${qtyText(h.qty!)} ${h.product.unit}'),
          ),
      ],
    ),
  );
}

/// Код 5 цифр этикетки без ведущих нулей («00012» → «12») — тот PLU, который нужно записать товару; null — раскладка по коду.
String? labelPlu(String code, {bool clothing = false}) {
  final label = parseWeightLabel(code, clothing: clothing);
  final c = label?.candidates.where((x) => x.code.length == 5).firstOrNull;
  return c == null ? null : '${int.parse(c.code)}';
}

/// Неизвестный PLU: «Весовая этикетка: товар с PLU N не найден». Кнопка «Выбрать товар и записать ему PLU» — только у того, кто
/// может править товары: выбранный товар получает PLU на сервере (PATCH, после подтверждения), и та же этикетка сразу находит
/// его. Возвращает найденный после привязки товар с весом (для добавления в чек) или null.
Future<({Product product, double? qty})?> offerUnknownPlu(BuildContext context, WidgetRef ref, String code) async {
  final l = context.l;
  final db = ref.read(databaseProvider);
  final clothing = (await db.getSetting('shop.clothing')) == '1';
  final plu = labelPlu(code, clothing: clothing);
  final shown = plu ?? '${int.tryParse(parseWeightLabel(code, clothing: clothing)?.candidates.firstOrNull?.code ?? '') ?? ''}';
  final s = ref.read(sessionProvider);
  final canBind = plu != null && s != null && s.canRoute('/admin/new-product');
  if (!context.mounted) return null;
  final bind = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      content: Text(l.scaleNoPlu(shown)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
        if (canBind) FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.scaleBindPlu)),
      ],
    ),
  );
  final pluV = plu;
  if (bind != true || pluV == null || !context.mounted) return null;
  final p = await pickProduct(context, noServices: true);
  if (p == null || !context.mounted) return null;
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      content: Text(l.scaleBindAsk(pluV, p.name)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.save)),
      ],
    ),
  );
  if (ok != true || !context.mounted) return null;
  try {
    await ref.read(apiProvider).patch('api/main/products/${p.id}/', body: {'plu': int.parse(pluV)});
    await db.setProductPlu(p.id, pluV);
    // журнал: кто и кому записал PLU (в логе приложения; на сервере остаётся история изменения товара)
    debugPrint('scale-label: PLU $pluV записан товару ${p.id} «${p.name}»');
  } catch (e) {
    if (context.mounted) toast(context, '$e', error: true);
    return null;
  }
  final hit = (await db.weightLabelHits(code, includePiece: true)).where((h) => h.product.id == p.id).firstOrNull;
  if (hit == null) return null;
  if (!hit.weighed && context.mounted && !await confirmPieceProduct(context, hit.product)) return null;
  return (product: hit.product, qty: hit.weighed ? hit.qty : null);
}
