import 'dart:convert';

import 'package:flutter/material.dart';

import '../../core/db/database.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

// 2026-10-27 (М4): владелец — «на складе нет поштучной продажи! сигареты можно пачку продавать и поштучно». Модель — ровно
// кассовая (ProductPackageOption / packages и packages_input сервера NurCRM), новый контракт не придумывался:
//  * сам товар — это упаковка (пачка): его цена — цена упаковки, его остаток — в упаковках (может быть дробным: 11,75 пачки);
//  * `packages[]` — поштучная опция: {id, name, quantity_in_package, unit, piece_unit_price};
//  * запись — `packages_input` (полная замена списка), как BuildProductBody кассы; кассa берёт первую упаковку,
//    поэтому и здесь одна поштучная опция (несколько уровней «штука → пачка → блок» — будущий `sale_units`, ТЗ Б-53);
//  * в продаже штуки — строка с `sale_package_id`, quantity = число штук, price = цена штуки; сервер списывает qty / quantity_in_package.

/// Поштучная опция товара: сколько штук в упаковке и цена одной штуки.
class PiecePack {
  /// id упаковки на сервере — без него штуку продать нельзя (в продаже уходит как sale_package_id).
  final String? id;
  final String name;
  final double qty;
  final double piecePrice;

  const PiecePack({this.id, this.name = '', required this.qty, required this.piecePrice});

  static double _n(Object? v) => v is num ? v.toDouble() : double.tryParse('${v ?? ''}'.replaceAll(',', '.')) ?? 0;

  /// Годные упаковки из `packages` сервера — как ProductCatalogMapper кассы: штук > 0 и цена штуки > 0.
  static List<PiecePack> parseAll(Object? raw) {
    if (raw is! List) return const [];
    final out = <PiecePack>[];
    for (final m in raw) {
      if (m is! Map) continue;
      final q = _n(m['quantity_in_package']);
      final price = _n(m['piece_unit_price']);
      if (!(q > 0) || !(price > 0)) continue;
      final id = m['id']?.toString();
      out.add(PiecePack(id: id == null || id.isEmpty ? null : id, name: (m['name'] ?? '').toString().trim(), qty: q, piecePrice: price));
    }
    return out;
  }

  /// Первая упаковка (её и продаёт касса); null — поштучной продажи нет.
  static PiecePack? first(Object? raw) {
    final all = parseAll(raw);
    return all.isEmpty ? null : all.first;
  }

  /// Название для людей: пустое и служебное «упаковка» кассы — «Упаковка» (ключ [fallback]).
  String label(String fallback) {
    final n = name.trim();
    if (n.isEmpty) return fallback;
    return n[0].toUpperCase() + n.substring(1);
  }

  /// Тело одного элемента `packages_input`, как CatalogApiService.BuildProductBody кассы.
  Map<String, Object?> toInput(String unit) => {
    'name': name.trim().isEmpty ? 'упаковка' : name.trim(),
    'quantity_in_package': qty == qty.roundToDouble() ? qty.toInt() : qty,
    'unit': unit,
    'piece_unit_price': piecePrice.toStringAsFixed(2),
  };

  Map<String, dynamic> toJson() => {if (id != null) 'id': id, 'n': name, 'q': qty, 'p': piecePrice};

  static PiecePack? fromJson(Object? m) {
    if (m is! Map) return null;
    final q = _n(m['q']), p = _n(m['p']);
    if (!(q > 0) || !(p > 0)) return null;
    return PiecePack(id: m['id']?.toString(), name: (m['n'] ?? '').toString(), qty: q, piecePrice: p);
  }

  /// Те же поля (id сервер выдаёт сам) — карточка не изменилась.
  bool sameAs(PiecePack? o) => o != null && name.trim() == o.name.trim() && (qty - o.qty).abs() < 0.0005 && (piecePrice - o.piecePrice).abs() < 0.004;
}

/// Цена штуки «сама»: цена упаковки ÷ штук, до копейки. 0 — считать не из чего.
double autoPiecePrice(double packPrice, double qty) {
  if (!(packPrice > 0) || !(qty > 0)) return 0;
  return (packPrice / qty * 100).roundToDouble() / 100;
}

/// Что не так с введёнными упаковками. [qtyBad], [pieceBad] — не сохраняем; [mismatch] — предупреждение (спрашиваем).
enum PackProblem { qtyBad, pieceBad, mismatch }

/// Расхождение «цена штуки × штук» и цены упаковки, при котором спрашиваем (доля).
const packMismatchLimit = 0.05;

PackProblem? packProblem({required double packPrice, required double qty, required double piece}) {
  if (!(qty >= 2) || qty != qty.roundToDouble()) return PackProblem.qtyBad;
  if (!(piece > 0)) return PackProblem.pieceBad;
  if (packPrice > 0 && (piece * qty - packPrice).abs() / packPrice > packMismatchLimit) return PackProblem.mismatch;
  return null;
}

/// Остаток в упаковках → целые упаковки и штуки сверху. null — остаток не складывается в целые штуки (11,7 пачки) или меньше нуля.
({int packs, int pieces})? splitStock(double stockPacks, double perPack) {
  if (!(perPack >= 2) || !stockPacks.isFinite || stockPacks < 0) return null;
  final total = stockPacks * perPack;
  final rounded = total.roundToDouble();
  if ((total - rounded).abs() > 0.01) return null;
  final t = rounded.toInt();
  final per = perPack.round();
  return (packs: t ~/ per, pieces: t % per);
}

/// «12 пачек + 5 шт» / «12 пачек» / «5 шт» / «245» (без упаковки). Остаток хранится в упаковках, штуки — доли упаковки.
String stockWithPack(String Function(double) fmt, double stockPacks, PiecePack? pack, {required String packWord, required String pcs, String unit = ''}) {
  final tail = unit.isEmpty ? '' : ' $unit';
  if (pack == null) return '${fmt(stockPacks)}$tail';
  final s = splitStock(stockPacks, pack.qty);
  if (s == null) return '${fmt(stockPacks)} $packWord';
  if (s.packs == 0) return '${s.pieces} $pcs';
  if (s.pieces == 0) return '${s.packs} $packWord';
  return '${s.packs} $packWord + ${s.pieces} $pcs';
}

/// Остаток в штуках (для проверки «хватает ли штук»).
double stockInPieces(double stockPacks, PiecePack pack) => stockPacks * pack.qty;

/// Упаковки всех товаров — в настройках телефона одной записью (схема базы не меняется): {productId: упаковка}.
/// Заполняется при обновлении каталога и после правки карточки; читается при продаже и на складе.
class PackageStore {
  static const setting = 'catalog.packages';

  static Map<String, PiecePack> decode(String? raw) {
    if (raw == null || raw.isEmpty) return {};
    try {
      final m = jsonDecode(raw);
      if (m is! Map) return {};
      final out = <String, PiecePack>{};
      for (final e in m.entries) {
        final p = PiecePack.fromJson(e.value);
        if (p != null) out['${e.key}'] = p;
      }
      return out;
    } catch (_) {
      return {}; // испорченная запись — упаковок «нет», продавать целиком по-прежнему можно
    }
  }

  static String encode(Map<String, PiecePack> m) => jsonEncode({for (final e in m.entries) e.key: e.value.toJson()});

  static Future<Map<String, PiecePack>> read(AppDatabase db) async {
    try {
      return decode(await db.getSetting(setting));
    } catch (_) {
      return {};
    }
  }

  static Future<void> replaceAll(AppDatabase db, Map<String, PiecePack> m) => db.setSetting(setting, encode(m));

  static Future<void> put(AppDatabase db, String productId, PiecePack? p) async {
    final m = await read(db);
    if (p == null) {
      m.remove(productId);
    } else {
      m[productId] = p;
    }
    await replaceAll(db, m);
  }
}

String _fmt(double v) {
  if (v == v.roundToDouble()) return v.toInt().toString();
  return v.toStringAsFixed(2).replaceFirst(RegExp(r'0+$'), '').replaceFirst(RegExp(r'\.$'), '');
}

/// Состояние блока «Упаковки» в форме товара (новый и правка): переключатель, поля, цена штуки «сама», пока её не правили руками.
class PackFormController extends ChangeNotifier {
  PackFormController({required this.price, required this.cost}) {
    price.addListener(_auto);
    qty.addListener(_auto);
  }

  /// Цена упаковки = цена продажи товара и закупка упаковки — поля самой формы.
  final TextEditingController price;
  final TextEditingController cost;
  final name = TextEditingController();
  final qty = TextEditingController();
  final piece = TextEditingController();
  bool enabled = false;

  /// Цену штуки правили руками — «сама» не пересчитывается (кнопка «Авто» возвращает).
  bool manual = false;
  String? id;

  /// У товара на сервере несколько упаковок или это комплект — блок только читается, `packages_input` не уходит.
  bool locked = false;

  void setEnabled(bool v) {
    if (enabled == v) return;
    enabled = v;
    if (v) _auto();
    notifyListeners();
  }

  /// Шаблон «Пачка 20»: название и штуки одним нажатием.
  void applyTemplate(String n, int q) {
    name.text = n;
    qty.text = '$q';
    manual = false;
    _auto();
    notifyListeners();
  }

  void pieceEdited() {
    manual = piece.text.trim().isNotEmpty;
    if (!manual) _auto();
    notifyListeners();
  }

  void resetAuto() {
    manual = false;
    _auto();
    notifyListeners();
  }

  void _auto() {
    if (!enabled || manual) return;
    final v = autoPiecePrice(parseNum(price.text) ?? 0, parseNum(qty.text) ?? 0);
    final t = v > 0 ? _fmt(v) : '';
    if (piece.text != t) piece.text = t;
  }

  /// Заполнить из карточки сервера (null — поштучной продажи нет).
  void load(PiecePack? p, {bool lock = false}) {
    locked = lock;
    enabled = p != null;
    id = p?.id;
    name.text = p?.name ?? '';
    qty.text = p == null ? '' : _fmt(p.qty);
    piece.text = p == null ? '' : _fmt(p.piecePrice);
    manual = p != null && (autoPiecePrice(parseNum(price.text) ?? 0, p.qty) - p.piecePrice).abs() > 0.004;
    notifyListeners();
  }

  double get qtyValue => parseNum(qty.text) ?? 0;
  double get pieceValue => parseNum(piece.text) ?? 0;
  double get packPrice => parseNum(price.text) ?? 0;

  /// Введённая упаковка; null — переключатель выключен.
  PiecePack? toPack() => enabled ? PiecePack(id: id, name: name.text.trim(), qty: qtyValue, piecePrice: pieceValue) : null;

  PackProblem? get problem => enabled ? packProblem(packPrice: packPrice, qty: qtyValue, piece: pieceValue) : null;

  @override
  void dispose() {
    price.removeListener(_auto);
    qty.removeListener(_auto);
    name.dispose();
    qty.dispose();
    piece.dispose();
    super.dispose();
  }
}

/// Текст ошибки по [PackProblem] (mismatch здесь не ошибка — его спрашивает [confirmPackMismatch]).
String? packErrorText(BuildContext context, PackProblem? p) {
  final l = context.l;
  return switch (p) {
    PackProblem.qtyBad => l.pkQtyBad,
    PackProblem.pieceBad => l.pkPieceBad,
    _ => null,
  };
}

/// «Штука × штук ≠ упаковка» — спросить. true — сохранить как есть.
Future<bool> confirmPackMismatch(BuildContext context, PackFormController c) async {
  final l = context.l;
  final sum = c.pieceValue * c.qtyValue;
  final ok = await showDialog<bool>(
    context: context,
    builder: (d) => AlertDialog(
      key: const Key('packMismatchDialog'),
      title: Text(l.pkMismatchTitle),
      content: Text(l.pkMismatch(_fmt(c.qtyValue), money(c.pieceValue), money(sum), money(c.packPrice))),
      actions: [
        TextButton(
          key: const Key('packMismatchFix'),
          style: TextButton.styleFrom(minimumSize: const Size(64, 48)),
          onPressed: () => Navigator.pop(d, false),
          child: Text(l.peFixPrice),
        ),
        FilledButton(
          key: const Key('packMismatchKeep'),
          style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
          onPressed: () => Navigator.pop(d, true),
          child: Text(l.peSaveAsIs),
        ),
      ],
    ),
  );
  return ok == true;
}

/// Блок «Упаковки / поштучная продажа» в форме товара. [blocked] — весовой товар: переключатель недоступен.
class PackSection extends StatelessWidget {
  final PackFormController ctrl;
  final bool blocked;
  const PackSection({super.key, required this.ctrl, this.blocked = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return ListenableBuilder(
      listenable: Listenable.merge([ctrl, ctrl.price, ctrl.cost]),
      builder: (context, _) {
        final on = ctrl.enabled && !blocked;
        final cost = parseNum(ctrl.cost.text) ?? 0;
        final qty = ctrl.qtyValue;
        final piece = ctrl.pieceValue;
        final perPieceCost = qty > 0 ? cost / qty : 0.0;
        final problem = ctrl.problem;
        final err = packErrorText(context, problem);
        return Column(
          key: const Key('packSection'),
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            FieldLabel(l.pkTitle),
            if (ctrl.locked)
              Text(l.pkMulti, style: TextStyle(color: pal.textSoft, fontSize: 12.5))
            else ...[
              Semantics(
                container: true,
                label: l.pkSwitch,
                child: SwitchListTile(
                  key: const Key('packSwitch'),
                  contentPadding: EdgeInsets.zero,
                  title: Text(l.pkSwitch, style: const TextStyle(fontWeight: FontWeight.w700)),
                  subtitle: Text(blocked ? l.pkWeightBlocked : l.pkSwitchHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
                  value: on,
                  onChanged: blocked ? null : ctrl.setEnabled,
                ),
              ),
              if (on) ...[
                FieldLabel(l.pkQuick),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final (n, q) in [(l.pkWordPack, 20), (l.pkWordPackage, 6), (l.pkWordPackage, 12), (l.pkWordCrate, 24)])
                      ActionChip(key: Key('packTpl_${q}_$n'), materialTapTargetSize: MaterialTapTargetSize.padded, label: Text('$n $q'), onPressed: () => ctrl.applyTemplate(n, q)),
                  ],
                ),
                FieldLabel(l.pkName),
                TextField(
                  key: const Key('packName'),
                  controller: ctrl.name,
                  textCapitalization: TextCapitalization.sentences,
                  decoration: InputDecoration(hintText: l.pkNameHint),
                ),
                FieldLabel(l.pkQty),
                TextField(
                  key: const Key('packQty'),
                  controller: ctrl.qty,
                  keyboardType: TextInputType.number,
                  decoration: InputDecoration(suffixText: l.pkPcs),
                ),
                Padding(
                  padding: const EdgeInsets.only(top: 6),
                  child: Text(l.pkPackPrice(money(ctrl.packPrice)), style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                ),
                FieldLabel(l.pkPiecePrice),
                TextField(
                  key: const Key('packPiece'),
                  controller: ctrl.piece,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  onChanged: (_) => ctrl.pieceEdited(),
                  decoration: InputDecoration(
                    suffixIcon: IconButton(
                      key: const Key('packAuto'),
                      tooltip: l.pkAutoTip,
                      constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
                      icon: Icon(Icons.auto_fix_high_rounded, color: ctrl.manual ? null : T.success),
                      onPressed: ctrl.resetAuto,
                    ),
                  ),
                ),
                if (piece > 0 && perPieceCost > 0)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      piece < perPieceCost ? l.pkBelowCost(money(perPieceCost)) : l.pkMarkup(((piece - perPieceCost) / perPieceCost * 100).round()),
                      style: TextStyle(color: piece < perPieceCost ? T.danger : T.success, fontWeight: FontWeight.w700),
                    ),
                  ),
                if (err != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      err,
                      key: const Key('packError'),
                      style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700),
                    ),
                  ),
                if (problem == PackProblem.mismatch)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      l.pkMismatch(_fmt(qty), money(piece), money(piece * qty), money(ctrl.packPrice)),
                      key: const Key('packWarn'),
                      style: const TextStyle(color: T.warn, fontWeight: FontWeight.w700, fontSize: 12.5),
                    ),
                  ),
              ],
            ],
          ],
        );
      },
    );
  }
}
