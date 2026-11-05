import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart' show ApiException, ApiText;
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'barcode_lookup.dart';

/// 2026-11-05 (мобилка, М5-12, М5-13, М5-14): всё, что сканер и ручной сканер делают с ВЕСОВОЙ этикеткой сверх обычного поиска.
/// Как на кассе (ProductCodeLookup, 31.10): неизвестный PLU не ведёт к «Создать товар со штрихкодом этикетки» (штрихкод этикетки
/// каждый раз другой — такой товар-двойник бесполезен), повтор той же этикетки не ложится в чек сам, а раскладка «не задана»
/// не угадывается молча.

/// Коды весовых этикеток, уже лежащие в ТЕКУЩЕМ чеке. Тот же код второй раз без вопроса не добавляется; чек опустел — память чистая.
class ScaleLabelMemory {
  /// код этикетки → строка чека, которую она создала (identityHashCode; null — строка неизвестна)
  final _codes = <String, int?>{};

  bool contains(String code) => _codes.containsKey(code);

  /// [line] — строка чека, созданная этой этикеткой: удалили строку — этикетку можно добавить снова без вопроса (2026-11-05, М6-27).
  void add(String code, {Object? line}) => _codes[code] = line == null ? null : identityHashCode(line);

  /// Чек пуст (оплатили, очистили) — прежние этикетки больше не «в чеке». Если передан [lines] (строки чека сейчас), из памяти уходят
  /// и этикетки, чьих строк в чеке уже нет (2026-11-05, аудит №4, М6-27: после удаления строки скан спрашивал «уже в чеке»).
  void syncWithCart({required bool cartEmpty, Iterable<Object>? lines}) {
    if (cartEmpty) {
      _codes.clear();
      return;
    }
    if (lines == null) return;
    final alive = {for (final l in lines) identityHashCode(l)};
    _codes.removeWhere((_, id) => id != null && !alive.contains(id));
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
      // 2026-11-05 (аудит №4, М6-26): у товара уже другой PLU — предупредить, что весы с прежним PLU перестанут его находить
      content: Text(scaleBindPrompt(l, name: p.name, oldPlu: p.plu, plu: pluV)), // 2026-11-06 (М7-07): аргументы окна были перепутаны
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
    // 2026-11-05 (М6-26): понятная причина, а не сырое «Instance of …»
    if (context.mounted) toast(context, e is ApiException ? e.message : ApiText.badResponse, error: true);
    return null;
  }
  final hit = (await db.weightLabelHits(code, includePiece: true)).where((h) => h.product.id == p.id).firstOrNull;
  if (hit == null) return null;
  if (!hit.weighed && context.mounted && !await confirmPieceProduct(context, hit.product)) return null;
  return (product: hit.product, qty: hit.weighed ? hit.qty : null);
}

/// 2026-11-06 (аудит №5, М7-07): текст окна привязки PLU. У товара уже другой PLU — предупреждение «У товара «Молоко» PLU 15, будет 12»
/// (раньше название, прежний и новый PLU стояли не на своих местах). Иначе — обычный вопрос.
String scaleBindPrompt(AppLocalizations l, {required String name, String? oldPlu, required String plu}) {
  final old = (oldPlu ?? '').trim();
  return old.isNotEmpty && old != plu ? l.scaleBindOverwrite(name, old, plu) : l.scaleBindAsk(plu, name);
}
