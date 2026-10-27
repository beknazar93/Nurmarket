// 2026-10-26 (голос-управление, В5): «Отменить» в журнале «Действия ИИ» (/ai/log) — ВНЕ звонка. `kit.undoLast` и стек действий
// живут только во время звонка, а журнал `ai_actions.jsonl` хранит то, что осталось на диске: старые значения цены, число
// и смену внесения, статус заказа. Поэтому отменяем только записи, у которых способ отмены сохранён и операция обратима:
//   цена, закупка, оптовая цена (set_price), массовая наценка (bulk_markup), минимальный остаток и штрихкод (product_edit),
//   внесение/изъятие (cash_in_out, только в той же смене), статус заказа (set_order_status), тема и язык (ui_settings).
// Правила те же, что у undo.dart / action_tools.dart: изменили после ИИ — не перетираем («уже менялось после ИИ»); сумму и
// старые значения берём из записи, а не из памяти. Остальное — честно: корзина и чек были отменяемы только в звонке («срок
// отмены прошёл»), возврат, погашение долга, закрытие смены, проведённый приход — «нельзя отменить», выплата зарплаты —
// «запросом владельцу» (cashflows/{id}/cancel-request/ на сайте). Деньги идут теми же методами [ActionHost], что и в звонке.
import '../../api/session_store.dart';
import '../../util/audit.dart';
import 'action_tools.dart' show ActionHost;
import 'ai_journal.dart';
import 'undo.dart' show UndoStatus;

/// Группа записи — значок в журнале (навигация / корзина / деньги / товар / задачи / зарплата).
enum AiLogGroup { nav, cart, money, product, task, salary }

/// Что показать справа от записи.
enum JournalUndoKind {
  /// Кнопка «Отменить».
  available,

  /// «нельзя отменить» (серым).
  irreversible,

  /// «отмена — запросом владельцу» (выплата зарплаты).
  ownerRequest,

  /// Отмена была только в звонке (корзина, чек): «срок отмены прошёл».
  expired,

  /// «отменено 14:07».
  undone,

  /// Ничего (запись об отмене, не удалось, нет права).
  none,
}

class JournalUndoInfo {
  final JournalUndoKind kind;

  /// Когда отменено ([JournalUndoKind.undone]).
  final DateTime? undoneAt;

  /// «нельзя отменить · можно списать» — проведённый приход.
  final bool writeOffHint;
  const JournalUndoInfo(this.kind, {this.undoneAt, this.writeOffHint = false});
}

/// Итог отмены из журнала. [done]/[total] — для массовой наценки (частично: что-то вернули, остальное менялось после ИИ).
class JournalUndoOutcome {
  final UndoStatus status;
  final int done;
  final int total;

  /// Отмена внесения/изъятия невозможна: смена уже закрыта или в кассе меньше денег.
  final bool shiftClosed;
  const JournalUndoOutcome(this.status, {this.done = 1, this.total = 1, this.shiftClosed = false});
  bool get partial => status == UndoStatus.ok && done < total;
}

class JournalUndo {
  /// Инструменты, чью отмену можно выполнить по записи журнала.
  static const supported = {'set_price', 'bulk_markup', 'product_edit', 'cash_in_out', 'set_order_status', 'ui_settings'};

  static const _receive = {'receive_from_supplier', 'receive_simple'};
  static const _moneyTools = {'checkout', 'return_sale', 'close_shift', 'open_shift', 'cash_in_out', 'pay_debt', 'receive_from_supplier', 'receive_simple', 'write_off', 'stock_count', 'add_bonus'};
  static const _productTools = {'set_price', 'bulk_markup', 'product_edit', 'create_product', 'delete_product', 'product_wizard', 'wizard_create'};
  static const _cartTools = {'cart_edit', 'hold_cart', 'set_order_status', 'create_client'};

  /// Значок группы. Запись об отмене (`undo_last`) берёт группу отменённого действия.
  static AiLogGroup groupOf(AiActionRecord r) {
    var tool = r.tool;
    if (tool == 'undo_last') tool = '${r.args['undone'] ?? ''}';
    if (tool.startsWith('task_')) return AiLogGroup.task;
    if (tool.startsWith('salary_')) return AiLogGroup.salary;
    if (_moneyTools.contains(tool)) return AiLogGroup.money;
    if (_productTools.contains(tool)) return AiLogGroup.product;
    if (_cartTools.contains(tool)) return AiLogGroup.cart;
    return AiLogGroup.nav;
  }

  /// «Только деньги» в журнале: деньги и зарплата (как в макете).
  static bool isMoney(AiActionRecord r) {
    final g = groupOf(r);
    return g == AiLogGroup.money || g == AiLogGroup.salary;
  }

  /// Какие записи уже отменены: записью `undo_last` с `callId = undo:<callId отменённой>` и `args.undone = <инструмент>`.
  /// Google присылает `id` вызова внутри звонка, поэтому в разных звонках он может повториться — пару ищем по порядку времени:
  /// отмена закрывает ближайшую прежнюю запись с тем же id и инструментом, которая ещё не отменена. [all] — как отдаёт
  /// [AiJournal.read] (новые сверху).
  static Map<AiActionRecord, DateTime> undoneMap(List<AiActionRecord> all) {
    final out = Map<AiActionRecord, DateTime>.identity();
    final open = <String, List<AiActionRecord>>{};
    for (final r in all.reversed) {
      if (r.tool == 'undo_last') {
        if (!r.callId.startsWith('undo:')) continue;
        final key = '${r.callId.substring(5)}|${r.args['undone'] ?? ''}';
        final list = open[key];
        if (list != null && list.isNotEmpty) out[list.removeLast()] = r.at;
      } else {
        (open['${r.callId}|${r.tool}'] ??= []).add(r);
      }
    }
    return out;
  }

  /// Что показать справа. [viewer] — кто смотрит журнал (право на отмену проверяется ещё раз, как при вызове в звонке).
  static JournalUndoInfo classify(AiActionRecord r, Session? viewer, Map<AiActionRecord, DateTime> undone) {
    if (r.tool == 'undo_last' || r.status != 'ok') return const JournalUndoInfo(JournalUndoKind.none);
    final at = undone[r];
    if (at != null) return JournalUndoInfo(JournalUndoKind.undone, undoneAt: at);
    if (!r.reversible) {
      if (r.tool == 'salary_payout') return const JournalUndoInfo(JournalUndoKind.ownerRequest);
      return JournalUndoInfo(JournalUndoKind.irreversible, writeOffHint: _receive.contains(r.tool));
    }
    if (!supported.contains(r.tool) || !_dataOk(r)) return const JournalUndoInfo(JournalUndoKind.expired);
    if (viewer == null || !_allowed(r.tool, viewer)) return const JournalUndoInfo(JournalUndoKind.none);
    return const JournalUndoInfo(JournalUndoKind.available);
  }

  static bool _allowed(String tool, Session s) => switch (tool) {
    'set_price' || 'bulk_markup' => s.canEditPrices,
    'product_edit' => s.canRoute('/admin/product'),
    'set_order_status' => s.canRoute('/admin/orders'),
    _ => true,
  };

  // журнал маскирует длинные цифры по значению (7+ подряд) — такое значение вернуть уже нельзя
  static bool _masked(Object? v) => v is String && v.contains('***');
  static bool _okId(Object? v) => v is String && v.trim().isNotEmpty && !_masked(v);
  static num? _n(Object? v) => v is num ? v : null;
  static bool _diff(num a, num b) => (a - b).abs() > 0.004;

  static bool _dataOk(AiActionRecord r) {
    final d = r.undo;
    switch (r.tool) {
      case 'set_price':
        if (!_okId(d['id'])) return false;
        return ['price', 'purchase', 'wholesale'].any((k) => _n(d['${k}_from']) != null && _n(d['${k}_to']) != null && _diff(_n(d['${k}_from'])!, _n(d['${k}_to'])!));
      case 'bulk_markup':
        final items = d['items'];
        return items is List && items.isNotEmpty && items.every((e) => e is Map && _okId(e['id']) && _n(e['from']) != null && _n(e['to']) != null);
      case 'product_edit':
        if (!_okId(d['id'])) return false;
        final codeChanged = '${r.args['new_barcode'] ?? ''}'.trim().isNotEmpty;
        final minChanged = r.args['min_qty'] != null;
        // 2026-10-27 (М2): название, категория, единица и доп. штрихкоды тоже правятся голосом
        final nameCh = d['name_to'] is String;
        final catCh = d['category_to'] is String;
        final unitCh = d['unit_to'] is String;
        final altCh = d['alt_to'] is List;
        if (!codeChanged && !minChanged && !nameCh && !catCh && !unitCh && !altCh) return false;
        if (codeChanged && (d['barcode_from'] is! String || d['barcode_to'] is! String || _masked(d['barcode_from']) || _masked(d['barcode_to']))) return false;
        if (minChanged && (_n(d['min_from']) == null || _n(d['min_to']) == null)) return false;
        if ((nameCh && d['name_from'] is! String) || (catCh && d['category_from'] is! String) || (unitCh && d['unit_from'] is! String)) return false;
        if (altCh && (d['alt_from'] is! List || [...(d['alt_from'] as List), ...(d['alt_to'] as List)].any((e) => e is! String || _masked(e)))) return false;
        return true;
      case 'cash_in_out':
        return (d['kind'] == 'in' || d['kind'] == 'out') && (_n(d['amount']) ?? 0) > 0 && _okId(d['shift']);
      case 'set_order_status':
        return _okId(d['order']) && d['from'] is String && (d['from'] as String).isNotEmpty && d['to'] is String && (d['to'] as String).isNotEmpty;
      case 'ui_settings':
        final themeOk = '${d['theme_to'] ?? ''}'.isEmpty || d['theme_from'] is String;
        final langOk = '${d['lang_to'] ?? ''}'.isEmpty || d['lang_from'] is String;
        return themeOk && langOk && ('${d['theme_to'] ?? ''}'.isNotEmpty || '${d['lang_to'] ?? ''}'.isNotEmpty);
    }
    return false;
  }
}

/// Исполнитель отмены из журнала. [host] — те же репозитории, что у инструментов звонка ([RepoActionHost]); в тестах — фейк.
class JournalUndoer {
  final ActionHost host;
  final DateTime Function() now;
  JournalUndoer(this.host, {DateTime Function()? now}) : now = now ?? DateTime.now;

  Future<JournalUndoOutcome> undo(AiActionRecord r, Session viewer) async {
    final info = JournalUndo.classify(r, viewer, const {});
    // повторная проверка права и данных при нажатии (запись могла измениться, сессия — смениться)
    if (info.kind != JournalUndoKind.available) return const JournalUndoOutcome(UndoStatus.failed);
    final d = r.undo;
    switch (r.tool) {
      case 'set_price':
        return _price(d);
      case 'bulk_markup':
        return _bulk(d);
      case 'product_edit':
        return _productEdit(r);
      case 'cash_in_out':
        return _cash(d);
      case 'set_order_status':
        return _order(d);
      case 'ui_settings':
        return _ui(d);
    }
    return const JournalUndoOutcome(UndoStatus.failed);
  }

  Future<JournalUndoOutcome> _price(Map<String, dynamic> d) async {
    final p = await host.productById('${d['id']}');
    if (p == null) return const JournalUndoOutcome(UndoStatus.failed);
    final cur = await host.loadDraft(p);
    double? from(String k) {
      final a = JournalUndo._n(d['${k}_from']), b = JournalUndo._n(d['${k}_to']);
      return a != null && b != null && JournalUndo._diff(a, b) ? a.toDouble() : null;
    }

    double? to(String k) => JournalUndo._n(d['${k}_to'])?.toDouble();
    final price = from('price'), purchase = from('purchase'), wholesale = from('wholesale');
    // что-то из изменённого поправили после ИИ — не перетираем
    if (price != null && JournalUndo._diff(cur.price, to('price')!)) return const JournalUndoOutcome(UndoStatus.conflict);
    if (purchase != null && JournalUndo._diff(cur.purchase, to('purchase')!)) return const JournalUndoOutcome(UndoStatus.conflict);
    if (wholesale != null && JournalUndo._diff(cur.wholesale, to('wholesale')!)) return const JournalUndoOutcome(UndoStatus.conflict);
    await host.saveDraft(p.id, cur, cur.copyWith(price: price, purchase: purchase, wholesale: wholesale));
    Audit.log('price', 'ИИ: отмена цены · ${p.name}', amount: price ?? cur.price);
    return const JournalUndoOutcome(UndoStatus.ok);
  }

  Future<JournalUndoOutcome> _bulk(Map<String, dynamic> d) async {
    final items = (d['items'] as List).cast<Map>();
    var ok = 0, conflict = 0, failed = 0;
    for (final e in items) {
      try {
        final p = await host.productById('${e['id']}');
        if (p == null) {
          failed++;
          continue;
        }
        final cur = await host.loadDraft(p);
        final from = (e['from'] as num).toDouble(), to = (e['to'] as num).toDouble();
        if (JournalUndo._diff(cur.price, to)) {
          conflict++;
          continue;
        }
        await host.saveDraft(p.id, cur, cur.copyWith(price: from));
        ok++;
      } catch (_) {
        failed++;
      }
    }
    if (ok > 0) Audit.log('price', 'ИИ: отмена наценки · $ok товаров');
    if (conflict == 0 && failed == 0) return JournalUndoOutcome(UndoStatus.ok, done: ok, total: items.length);
    // частично: хоть что-то вернули — это успех с пояснением, как в звонке
    if (ok > 0) return JournalUndoOutcome(UndoStatus.ok, done: ok, total: items.length);
    return JournalUndoOutcome(conflict > 0 ? UndoStatus.conflict : UndoStatus.failed, done: 0, total: items.length);
  }

  Future<JournalUndoOutcome> _productEdit(AiActionRecord r) async {
    final d = r.undo;
    final p = await host.productById('${d['id']}');
    if (p == null) return const JournalUndoOutcome(UndoStatus.failed);
    final cur = await host.loadDraft(p);
    final codeChanged = '${r.args['new_barcode'] ?? ''}'.trim().isNotEmpty;
    final minChanged = r.args['min_qty'] != null;
    final nameCh = d['name_to'] is String;
    final catCh = d['category_to'] is String;
    final unitCh = d['unit_to'] is String;
    final altCh = d['alt_to'] is List;
    if (codeChanged && cur.barcode.trim() != '${d['barcode_to']}'.trim()) return const JournalUndoOutcome(UndoStatus.conflict);
    if (minChanged && JournalUndo._diff(cur.minQty ?? 0, (d['min_to'] as num).toDouble())) return const JournalUndoOutcome(UndoStatus.conflict);
    if (nameCh && cur.name.trim() != '${d['name_to']}'.trim()) return const JournalUndoOutcome(UndoStatus.conflict);
    if (catCh && cur.category.trim() != '${d['category_to']}'.trim()) return const JournalUndoOutcome(UndoStatus.conflict);
    if (unitCh && cur.unit.trim() != '${d['unit_to']}'.trim()) return const JournalUndoOutcome(UndoStatus.conflict);
    if (altCh && cur.alt.join('\n') != [for (final e in d['alt_to'] as List) '$e'].join('\n')) return const JournalUndoOutcome(UndoStatus.conflict);
    await host.saveDraft(
      p.id,
      cur,
      cur.copyWith(
        barcode: codeChanged ? '${d['barcode_from']}' : null,
        minQty: minChanged ? (d['min_from'] as num).toDouble() : null,
        name: nameCh ? '${d['name_from']}' : null,
        category: catCh ? '${d['category_from']}' : null,
        unit: unitCh ? '${d['unit_from']}' : null,
        alt: altCh ? [for (final e in d['alt_from'] as List) '$e'] : null,
      ),
    );
    Audit.log('product', 'ИИ: отмена правки карточки · ${p.name}');
    return const JournalUndoOutcome(UndoStatus.ok);
  }

  Future<JournalUndoOutcome> _cash(Map<String, dynamic> d) async {
    final shift = host.shift;
    final shiftId = '${d['shift']}';
    final amount = (d['amount'] as num).toDouble();
    final out = d['kind'] == 'out';
    // обратная операция — только в той же открытой смене (раздел 2.5 б ТЗ)
    if (shift == null || shift.id != shiftId || shift.cashboxId == null) return const JournalUndoOutcome(UndoStatus.failed, shiftClosed: true);
    // отменить внесение = изъять; изъять больше, чем лежит в ящике, нельзя (как экран «Внесение / изъятие»)
    if (!out && shift.expectedCash + 0.004 < amount) return const JournalUndoOutcome(UndoStatus.failed, shiftClosed: true);
    await host.cashFlow(shiftId: shiftId, cashboxId: shift.cashboxId!, withdrawal: !out, amount: amount, reason: 'отмена ИИ');
    await host.refreshShift();
    Audit.log('cash', 'ИИ: отмена ${out ? 'изъятия' : 'внесения'}', amount: out ? amount : -amount);
    return const JournalUndoOutcome(UndoStatus.ok);
  }

  Future<JournalUndoOutcome> _order(Map<String, dynamic> d) async {
    final number = '${d['order']}';
    final orders = await host.orders();
    String norm(String s) => s.replaceFirst(RegExp(r'^0+'), '');
    final o = orders.where((x) => x.number == number || x.number.endsWith('-$number') || norm(x.number) == norm(number)).firstOrNull;
    if (o == null) return const JournalUndoOutcome(UndoStatus.failed);
    if (o.status != d['to']) return const JournalUndoOutcome(UndoStatus.conflict);
    await host.setOrderStatus(o.id, '${d['from']}');
    Audit.log('open', 'ИИ: отмена статуса заказа ${o.number}');
    return const JournalUndoOutcome(UndoStatus.ok);
  }

  Future<JournalUndoOutcome> _ui(Map<String, dynamic> d) async {
    final themeTo = '${d['theme_to'] ?? ''}', langTo = '${d['lang_to'] ?? ''}';
    // тему/язык уже поменяли вручную — не трогаем (сравниваем с тем, что поставила ИИ; 'system' и 'auto' — как записала ИИ)
    if (langTo.isNotEmpty && host.language != langTo) return const JournalUndoOutcome(UndoStatus.conflict);
    if (themeTo.isNotEmpty && !_sameTheme(host.themeMode, themeTo)) return const JournalUndoOutcome(UndoStatus.conflict);
    if (themeTo.isNotEmpty) await host.setThemeMode('${d['theme_from']}');
    if (langTo.isNotEmpty) await host.setLanguage('${d['lang_from']}');
    return const JournalUndoOutcome(UndoStatus.ok);
  }

  static bool _sameTheme(String cur, String to) => cur == to || (cur == 'system' && to == 'auto') || (cur == 'auto' && to == 'system');

  /// Записать в журнал, что запись отменена (её увидят `undoneMap` и фильтр «Отменённые»).
  Future<void> markUndone(AiJournal journal, AiActionRecord r, Session viewer, String say) => journal.record(
    AiActionRecord(
      at: now(),
      user: viewer.userId,
      tool: 'undo_last',
      callId: 'undo:${r.callId}',
      said: '',
      args: {'undone': r.tool, 'source': 'journal'},
      status: 'ok',
      say: say,
      mode: 'act',
    ),
  );
}
