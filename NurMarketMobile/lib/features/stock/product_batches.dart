import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart';
import '../admin/admin_receive.dart' show adminReceiveRepoProvider;
import '../admin/admin_screens.dart' show openWhatsApp;

// 2026-10-30 (1.18.12, product-batches): владелец — «в мобилке при нажатии на товар в складе — инфо как в десктопе».
// «Поступления и партии» товара: приходы поставщиков (GET api/main/suppliers/receipts/ — тот же список, что «Закупки» кассы),
// в которых есть этот товар. Фильтра по товару у сервера нет — приходы читаются страницами по 100 и отбираются на телефоне.
// Закупка, поставщики и накладная — только Session.canSeePurchase; кассиру — дата и количество.

/// Одна строка списка: поступление этого товара в одном приходе (строки одного товара в приходе сложены).
class BatchEntry {
  final String receiptId;
  final String number;
  final String supplierId;
  final String supplierName;
  final String createdBy;
  final String payment;
  final String unit;
  final DateTime? at;
  final double qty;
  final double returned;
  final double cost;

  /// Приход целиком, как пришёл со списка (для накладной без повторного запроса); в кэш не пишется.
  final Map<String, dynamic>? raw;
  const BatchEntry({
    required this.receiptId,
    this.number = '',
    this.supplierId = '',
    this.supplierName = '',
    this.createdBy = '',
    this.payment = '',
    this.unit = '',
    this.at,
    required this.qty,
    this.returned = 0,
    this.cost = 0,
    this.raw,
  });

  double get sum => qty * cost;

  /// Принято за вычетом возврата поставщику — из этого количества считается «остаток партии».
  double get net => math.max(0, qty - returned);

  /// Номер для показа: из прихода, иначе короткий код по id.
  String get shownNumber {
    if (number.isNotEmpty) return number;
    return receiptId.length > 8 ? receiptId.substring(0, 8).toUpperCase() : receiptId;
  }

  Map<String, dynamic> toJson() => {
    'id': receiptId,
    'no': number,
    'sid': supplierId,
    'sn': supplierName,
    'by': createdBy,
    'pay': payment,
    'u': unit,
    'at': at?.millisecondsSinceEpoch,
    'q': qty,
    'r': returned,
    'c': cost,
  };

  static BatchEntry? fromJson(dynamic j) {
    if (j is! Map) return null;
    double d(dynamic v) => v is num ? v.toDouble() : 0;
    String s(dynamic v) => v is String ? v : '';
    final id = s(j['id']);
    if (id.isEmpty) return null;
    final ms = j['at'];
    return BatchEntry(
      receiptId: id,
      number: s(j['no']),
      supplierId: s(j['sid']),
      supplierName: s(j['sn']),
      createdBy: s(j['by']),
      payment: s(j['pay']),
      unit: s(j['u']),
      at: ms is int ? DateTime.fromMillisecondsSinceEpoch(ms) : null,
      qty: d(j['q']),
      returned: d(j['r']),
      cost: d(j['c']),
    );
  }
}

/// Время сервера → местное. Без пояса («2026-10-15T14:57:59») — время NurCRM, Бишкек (+06:00), как на кассе.
DateTime? parseBatchDate(String? s) {
  if (s == null || s.isEmpty) return null;
  final hasZone = RegExp(r'(Z|[+-]\d\d:?\d\d)$').hasMatch(s);
  final d = DateTime.tryParse(s);
  if (d == null) return null;
  if (hasZone || !s.contains('T')) return d.toLocal();
  return DateTime.utc(d.year, d.month, d.day, d.hour, d.minute, d.second).subtract(const Duration(hours: 6)).toLocal();
}

String _nameOf(dynamic v) => v is Map ? (v['full_name'] ?? v['name'] ?? v['llc'] ?? '').toString() : '';

/// Идентификатор товара в строке прихода (поле product / product_id — число, строка или вложенный объект).
String _lineProductId(Map<String, dynamic> it) => str(it, 'product_id') ?? str(it, 'product') ?? '';

List<Map<String, dynamic>> _lines(Map<String, dynamic> r) {
  final raw = r['items'] is List && (r['items'] as List).isNotEmpty ? r['items'] : r['lines'];
  return raw is List ? raw.whereType<Map>().map((e) => e.cast<String, dynamic>()).toList() : const [];
}

/// Поступление товара [productId] из прихода [r]; null — товара в приходе нет.
BatchEntry? batchFromReceipt(Map<String, dynamic> r, String productId) {
  final mine = [
    for (final it in _lines(r))
      if (_lineProductId(it) == productId) it,
  ];
  if (mine.isEmpty) return null;
  var qty = 0.0, returned = 0.0, money = 0.0;
  for (final it in mine) {
    final q = num0(it, 'qty') != 0 ? num0(it, 'qty') : num0(it, 'quantity');
    qty += q;
    returned += num0(it, 'returned_qty');
    money += q * num0(it, 'purchase_price');
  }
  final id = str(r, 'id') ?? '';
  final sid = str(r, 'supplier_id') ?? str(r, 'supplier') ?? '';
  var sname = str(r, 'supplier_name') ?? '';
  if (sname.isEmpty) sname = _nameOf(r['supplier']);
  final by = str(r, 'created_by_name') ?? str(r, 'created_by_fullname') ?? _nameOf(r['created_by']);
  return BatchEntry(
    receiptId: id,
    number: str(r, 'number') ?? str(r, 'doc_number') ?? str(r, 'document_number') ?? str(r, 'receipt_number') ?? str(r, 'invoice_number') ?? '',
    supplierId: sid,
    supplierName: sname,
    createdBy: by,
    payment: str(r, 'payment_type') ?? str(r, 'payment_method') ?? '',
    unit: str(mine.first, 'unit') ?? '',
    at: parseBatchDate(str(r, 'created_at') ?? str(r, 'date')),
    qty: qty,
    returned: returned,
    cost: qty > 0 ? money / qty : num0(mine.first, 'purchase_price'),
    raw: r,
  );
}

/// Новые сверху, без повторов одного прихода.
List<BatchEntry> mergeBatches(Iterable<BatchEntry> a, Iterable<BatchEntry> b) {
  final seen = <String>{};
  final out = <BatchEntry>[];
  for (final e in [...a, ...b]) {
    if (e.receiptId.isNotEmpty && !seen.add(e.receiptId)) continue;
    out.add(e);
  }
  final idx = {for (var i = 0; i < out.length; i++) out[i]: i};
  out.sort((x, y) {
    final ax = x.at, ay = y.at;
    if (ax != null && ay != null) {
      final c = ay.compareTo(ax);
      if (c != 0) return c;
    } else if (ax != null) {
      return -1;
    } else if (ay != null) {
      return 1;
    }
    return idx[x]!.compareTo(idx[y]!);
  });
  return out;
}

/// «Остаток партии» — ОЦЕНКА по FIFO: текущий остаток [stock] раскладывается по самым свежим поступлениям (список — новые
/// сверху), потому что продаётся сначала старое. Сервер остаток партии не хранит.
List<double> fifoRemaining(List<BatchEntry> newestFirst, double stock) {
  var left = math.max(0.0, stock);
  final out = <double>[];
  for (final e in newestFirst) {
    final take = math.min(e.net, left);
    out.add(double.parse(take.toStringAsFixed(3)));
    left -= take;
  }
  return out;
}

/// Итоги над загруженными поступлениями (новые сверху).
class BatchSummary {
  final double totalQty;
  final double avgCost;
  final double lastCost;
  final String lastSupplier;
  final int count;
  const BatchSummary({required this.totalQty, required this.avgCost, required this.lastCost, required this.lastSupplier, required this.count});

  static BatchSummary of(List<BatchEntry> newestFirst) {
    var qty = 0.0, money = 0.0, priced = 0.0;
    for (final e in newestFirst) {
      qty += e.qty;
      if (e.cost > 0 && e.qty > 0) {
        money += e.qty * e.cost;
        priced += e.qty;
      }
    }
    final last = newestFirst.where((e) => e.cost > 0).firstOrNull;
    return BatchSummary(
      totalQty: qty,
      avgCost: priced > 0 ? money / priced : 0,
      lastCost: last?.cost ?? 0,
      lastSupplier: (newestFirst.where((e) => e.supplierName.isNotEmpty).firstOrNull)?.supplierName ?? '',
      count: newestFirst.length,
    );
  }
}

/// Страница результата: что нашли, с какой страницы сервера читать дальше, есть ли ещё.
class BatchPage {
  final List<BatchEntry> entries;
  final int nextPage;
  final bool hasMore;
  const BatchPage(this.entries, this.nextPage, this.hasMore);
}

class BatchCached {
  final DateTime at;
  final List<BatchEntry> entries;
  const BatchCached(this.at, this.entries);
}

class ProductBatchesRepository {
  final ApiClient api;
  final AppDatabase db;
  ProductBatchesRepository(this.api, this.db);

  /// Размер страницы сервера и предел чтения за один шаг: сервер не умеет фильтровать приходы по товару, поэтому за шаг читаем
  /// до [maxPages] страниц, пока не наберём [target] поступлений этого товара.
  static const pageSize = 100;

  Future<BatchPage> load(String productId, {int page = 1, DateTime? from, int target = 20, int maxPages = 5}) async {
    final found = <BatchEntry>[];
    var p = page;
    var hasMore = true;
    for (var n = 0; n < maxPages && hasMore; n++) {
      final data = await api.get(
        'api/main/suppliers/receipts/',
        query: {'page': p, 'limit': pageSize, if (from != null) 'date_from': DateFormat('yyyy-MM-dd').format(from)},
      );
      final rows = asList(data);
      DateTime? firstAt, lastAt;
      for (final r in rows) {
        final e = batchFromReceipt(r, productId);
        final at = parseBatchDate(str(r, 'created_at') ?? str(r, 'date'));
        firstAt ??= at;
        lastAt = at ?? lastAt;
        if (e != null && (from == null || e.at == null || !e.at!.isBefore(from))) found.add(e);
      }
      hasMore = rows.isNotEmpty && (data is Map ? data['next'] != null : rows.length >= pageSize);
      // сервер отдаёт новые сверху: если вся страница уже старше начала периода, дальше читать нечего
      if (from != null && firstAt != null && lastAt != null && !firstAt.isBefore(lastAt) && lastAt.isBefore(from)) hasMore = false;
      p++;
      if (found.length >= target) break;
    }
    return BatchPage(found, p, hasMore);
  }

  /// Приход целиком (GET suppliers/receipts/{id}/) — для накладной и поиска файла.
  Future<Map<String, dynamic>> receipt(String id) async {
    final data = await api.get('api/main/suppliers/receipts/${Uri.encodeComponent(id)}/');
    if (data is Map) return data.cast<String, dynamic>();
    throw ApiException(ApiText.networkError);
  }

  /// Телефон поставщика (клиенты с type=suppliers); null — не нашли.
  Future<String?> supplierPhone(String id, String name, AdminSuppliers suppliers) async {
    final all = await suppliers();
    for (final s in all) {
      if ((id.isNotEmpty && s.$1 == id) || (id.isEmpty && s.$2.trim().toLowerCase() == name.trim().toLowerCase())) {
        return s.$3.isEmpty ? null : s.$3;
      }
    }
    return null;
  }

  // ----- кэш «данные от ЧЧ:ММ» — запись в существующую таблицу настроек (чистится при выходе и смене аккаунта вместе с account_wipe)
  static const cacheKey = 'batches.cache.v1';
  static const _cacheProducts = 12;
  static const _cacheRows = 60;

  Future<BatchCached?> cacheRead(String productId) async {
    try {
      final raw = await db.getSetting(cacheKey);
      if (raw == null || raw.isEmpty) return null;
      final m = jsonDecode(raw);
      final c = m is Map ? m[productId] : null;
      if (c is! Map) return null;
      final at = DateTime.tryParse('${c['at']}');
      final rows = c['rows'];
      if (at == null || rows is! List) return null;
      return BatchCached(at, [for (final j in rows) ?BatchEntry.fromJson(j)]);
    } catch (_) {
      return null;
    }
  }

  Future<void> cacheWrite(String productId, List<BatchEntry> entries, {DateTime? now}) async {
    try {
      final raw = await db.getSetting(cacheKey);
      dynamic decoded;
      try {
        decoded = raw == null || raw.isEmpty ? null : jsonDecode(raw);
      } catch (_) {
        decoded = null; // испорченный кэш — перезаписываем с нуля
      }
      final m = <String, dynamic>{if (decoded is Map) ...decoded.cast<String, dynamic>()};
      m[productId] = {'at': (now ?? DateTime.now()).toIso8601String(), 'rows': [for (final e in entries.take(_cacheRows)) e.toJson()]};
      if (m.length > _cacheProducts) {
        final byAge = m.entries.toList()..sort((a, b) => '${(b.value as Map)['at']}'.compareTo('${(a.value as Map)['at']}'));
        for (final e in byAge.skip(_cacheProducts)) {
          m.remove(e.key);
        }
      }
      await db.setSetting(cacheKey, jsonEncode(m));
    } catch (_) {
      // кэш — удобство: не записался, список всё равно показан
    }
  }
}

/// Список поставщиков с телефонами (admin_receive.dart): id, название, телефон.
typedef AdminSuppliers = Future<List<(String, String, String)>> Function();

final productBatchesRepoProvider = Provider((ref) => ProductBatchesRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

/// Открыть «Поступления и партии» товара.
Future<void> openProductBatches(BuildContext context, Product p) =>
    Navigator.of(context).push<void>(MaterialPageRoute(builder: (_) => ProductBatchesScreen(product: p)));

// ======================================================================== экран

enum _Period { week, month, year, all }

class ProductBatchesScreen extends ConsumerStatefulWidget {
  final Product product;
  const ProductBatchesScreen({super.key, required this.product});
  @override
  ConsumerState<ProductBatchesScreen> createState() => _ProductBatchesScreenState();
}

class _ProductBatchesScreenState extends ConsumerState<ProductBatchesScreen> {
  final _scroll = ScrollController();
  final _search = TextEditingController();
  List<BatchEntry> _entries = [];
  int _next = 1;
  bool _hasMore = false;
  bool _loading = true;
  bool _loadingMore = false;
  String? _error;
  DateTime? _cachedAt; // не null — на экране кэш, связи нет
  _Period _period = _Period.all;
  bool _showTop = false;
  int _gen = 0;

  bool get _money => ref.read(sessionProvider)?.canSeePurchase ?? false;

  DateTime? get _from {
    final n = DateTime.now();
    final today = DateTime(n.year, n.month, n.day);
    return switch (_period) {
      _Period.week => today.subtract(const Duration(days: 7)),
      _Period.month => DateTime(today.year, today.month - 1, today.day),
      _Period.year => DateTime(today.year - 1, today.month, today.day),
      _Period.all => null,
    };
  }

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() {
      final show = _scroll.hasClients && _scroll.offset > 600;
      if (show != _showTop) setState(() => _showTop = show);
    });
    _reload();
  }

  @override
  void dispose() {
    _scroll.dispose();
    _search.dispose();
    super.dispose();
  }

  Future<void> _reload() async {
    final gen = ++_gen;
    setState(() {
      _loading = true;
      _error = null;
      _cachedAt = null;
      _entries = [];
      _hasMore = false;
      _next = 1;
    });
    final repo = ref.read(productBatchesRepoProvider);
    try {
      final page = await repo.load(widget.product.id, from: _from);
      if (!mounted || gen != _gen) return;
      setState(() {
        _entries = mergeBatches(const [], page.entries);
        _next = page.nextPage;
        _hasMore = page.hasMore;
        _loading = false;
      });
      if (_period == _Period.all) unawaited(repo.cacheWrite(widget.product.id, _entries));
    } catch (e) {
      if (!mounted || gen != _gen) return;
      final cached = _period == _Period.all ? await repo.cacheRead(widget.product.id) : null;
      if (!mounted || gen != _gen) return;
      setState(() {
        _loading = false;
        if (cached != null) {
          _entries = cached.entries;
          _cachedAt = cached.at;
        } else {
          _error = e is ApiException ? e.message : '$e';
        }
      });
    }
  }

  Future<void> _more() async {
    if (_loadingMore || !_hasMore || _loading || _error != null || _cachedAt != null) return;
    final gen = _gen;
    setState(() => _loadingMore = true);
    try {
      final page = await ref.read(productBatchesRepoProvider).load(widget.product.id, page: _next, from: _from);
      if (!mounted || gen != _gen) return;
      setState(() {
        _entries = mergeBatches(_entries, page.entries);
        _next = page.nextPage;
        _hasMore = page.hasMore;
        _loadingMore = false;
      });
    } catch (e) {
      if (!mounted || gen != _gen) return;
      setState(() {
        _loadingMore = false;
        _error = e is ApiException ? e.message : '$e';
      });
    }
  }

  List<BatchEntry> get _visible {
    final q = _search.text.trim().toLowerCase();
    if (q.isEmpty) return _entries;
    final showMoney = _money;
    return [
      for (final e in _entries)
        if (e.shownNumber.toLowerCase().contains(q) || e.number.toLowerCase().contains(q) || (showMoney && e.supplierName.toLowerCase().contains(q))) e,
    ];
  }

  String _payText(String raw) {
    final l = context.l;
    switch (raw.toLowerCase()) {
      case '':
        return '';
      case 'cash':
        return l.pbPayCash;
      case 'debt':
        return l.pbPayDebt;
      case 'prepayment':
        return l.pbPayPrepay;
      default:
        return raw;
    }
  }

  String _periodLabel(_Period p) {
    final l = context.l;
    return switch (p) {
      _Period.week => l.pbWeek,
      _Period.month => l.pbMonth,
      _Period.year => l.pbYear,
      _Period.all => l.pbAllTime,
    };
  }

  Future<void> _showSupplier(BatchEntry e) async {
    final l = context.l;
    final repo = ref.read(productBatchesRepoProvider);
    final suppliers = ref.read(adminReceiveRepoProvider).supplierContacts;
    String? phone;
    try {
      phone = await repo.supplierPhone(e.supplierId, e.supplierName, suppliers);
    } catch (_) {
      phone = null;
    }
    if (!mounted) return;
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (c) => SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l.pbSupplier, style: TextStyle(fontSize: 12, color: c.pal.textSoft)),
              const SizedBox(height: 2),
              Text(e.supplierName, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
              const SizedBox(height: 8),
              if (phone == null)
                Text(l.pbNoPhone, style: TextStyle(color: c.pal.textSoft))
              else ...[
                Text(phone, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
                const SizedBox(height: 12),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    FilledButton.icon(
                      onPressed: () => launchUrl(Uri(scheme: 'tel', path: phone), mode: LaunchMode.externalApplication),
                      icon: const Icon(Icons.call_rounded),
                      label: Text(l.pbCall),
                    ),
                    OutlinedButton.icon(
                      onPressed: () => openWhatsApp(phone!, ''),
                      icon: const Icon(Icons.chat_rounded),
                      label: Text(l.pbWhatsApp),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _explain(BuildContext context, String unit) {
    final l = context.l;
    return showDialog<void>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.pbTitle),
        content: SingleChildScrollView(child: Text(l.pbRemainTip(qtyText(widget.product.quantity), unit))),
        actions: [TextButton(onPressed: () => Navigator.pop(c), child: Text(l.close))],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final showMoney = ref.watch(sessionProvider.select((s) => s?.canSeePurchase ?? false));
    final visible = _visible;
    final remain = fifoRemaining(_entries, widget.product.quantity);
    final remainOf = {for (var i = 0; i < _entries.length; i++) _entries[i].receiptId: remain[i]};
    final summary = BatchSummary.of(_entries);
    final unit = widget.product.unit;
    final timeFmt = DateFormat('HH:mm');
    final dateFmt = DateFormat('dd.MM.yyyy HH:mm');

    Widget body;
    if (_loading) {
      body = const Center(child: CircularProgressIndicator());
    } else if (_error != null && _entries.isEmpty) {
      body = Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(l.pbLoadError(_error!), textAlign: TextAlign.center),
              const SizedBox(height: 12),
              FilledButton(onPressed: _reload, child: Text(l.retry)),
            ],
          ),
        ),
      );
    } else {
      body = ListView.builder(
        controller: _scroll,
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        itemCount: visible.length + 1,
        itemBuilder: (c, i) {
          if (i == visible.length) {
            if (_error != null) {
              return Padding(
                padding: const EdgeInsets.all(12),
                child: Column(
                  children: [
                    Text(l.pbLoadError(_error!), textAlign: TextAlign.center),
                    TextButton(
                      onPressed: () {
                        setState(() => _error = null);
                        _more();
                      },
                      child: Text(l.retry),
                    ),
                  ],
                ),
              );
            }
            if (_hasMore && _cachedAt == null) {
              WidgetsBinding.instance.addPostFrameCallback((_) {
                if (mounted) _more();
              });
              return Padding(
                padding: const EdgeInsets.all(16),
                child: Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)),
                    const SizedBox(width: 10),
                    Flexible(child: Text(l.pbLoadingMore, style: TextStyle(color: pal.textSoft))),
                  ],
                ),
              );
            }
            return Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                visible.isEmpty ? (_search.text.trim().isEmpty ? l.pbEmpty : l.pbEmptySearch) : l.pbEnd,
                textAlign: TextAlign.center,
                style: TextStyle(color: pal.textSoft),
              ),
            );
          }
          final e = visible[i];
          final pay = _payText(e.payment);
          final eu = e.unit.isNotEmpty ? e.unit : unit;
          return Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Panel(
              padding: const EdgeInsets.fromLTRB(12, 10, 12, 6),
              child: Column(
                key: ValueKey('pbRow-${e.receiptId}'),
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: Text(e.at == null ? '—' : dateFmt.format(e.at!), style: const TextStyle(fontWeight: FontWeight.w700)),
                      ),
                      const SizedBox(width: 8),
                      Flexible(
                        child: Text(
                          '+${qtyText(e.qty)} $eu',
                          textAlign: TextAlign.end,
                          style: TextStyle(fontWeight: FontWeight.w800, color: pal.success),
                        ),
                      ),
                    ],
                  ),
                  if (showMoney && e.supplierName.isNotEmpty)
                    InkWell(
                      onTap: () => _showSupplier(e),
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(minHeight: 40),
                        child: Align(
                          alignment: AlignmentDirectional.centerStart,
                          child: Text(
                            e.supplierName,
                            style: TextStyle(color: pal.action, fontWeight: FontWeight.w700, decoration: TextDecoration.underline),
                          ),
                        ),
                      ),
                    ),
                  Text(l.pbNumber(e.shownNumber), style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
                  if (showMoney) ...[
                    const SizedBox(height: 4),
                    Wrap(
                      spacing: 14,
                      runSpacing: 2,
                      children: [
                        if (e.cost > 0) Text(l.pbCostLine(money(e.cost)), style: const TextStyle(fontSize: 13)),
                        if (e.cost > 0) Text(l.pbSumLine(money(e.sum)), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700)),
                        if (pay.isNotEmpty) Text(l.pbPayLine(pay), style: const TextStyle(fontSize: 13)),
                      ],
                    ),
                    if (e.createdBy.isNotEmpty) Text(l.pbReceivedBy(e.createdBy), style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
                  ],
                  if (e.returned > 0) Text(l.pbReturned(qtyText(e.returned), eu), style: TextStyle(fontSize: 12.5, color: pal.danger)),
                  InkWell(
                    key: ValueKey('pbRemain-${e.receiptId}'),
                    onTap: () => _explain(context, eu),
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(minHeight: 40),
                      child: Row(
                        children: [
                          Expanded(
                            child: Text(
                              l.pbRemainLine(qtyText(remainOf[e.receiptId] ?? 0), eu),
                              style: TextStyle(fontSize: 13, color: pal.textSoft),
                            ),
                          ),
                          Icon(Icons.info_outline_rounded, size: 18, color: pal.textSoft),
                        ],
                      ),
                    ),
                  ),
                  if (showMoney)
                    Align(
                      alignment: AlignmentDirectional.centerEnd,
                      child: TextButton.icon(
                        key: ValueKey('pbInvoice-${e.receiptId}'),
                        onPressed: () => Navigator.of(context).push<void>(
                          MaterialPageRoute(builder: (_) => ReceiptDocScreen(entry: e, productId: widget.product.id)),
                        ),
                        icon: const Icon(Icons.receipt_long_rounded, size: 18),
                        label: Text(l.pbInvoice),
                      ),
                    ),
                ],
              ),
            ),
          );
        },
      );
    }

    return Scaffold(
      appBar: ScreenTitle(l.pbTitle, subtitle: widget.product.name),
      floatingActionButton: _showTop
          ? FloatingActionButton.extended(
              key: const Key('pbTop'),
              onPressed: () => _scroll.animateTo(0, duration: const Duration(milliseconds: 300), curve: Curves.easeOut),
              icon: const Icon(Icons.vertical_align_top_rounded),
              label: Text(l.pbToTop),
            )
          : null,
      body: Column(
        children: [
          if (_cachedAt != null)
            Container(
              width: double.infinity,
              color: pal.warnSoft,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Row(
                children: [
                  Expanded(child: Text(l.pbDataFrom(timeFmt.format(_cachedAt!)), style: const TextStyle(fontSize: 13))),
                  TextButton(onPressed: _reload, child: Text(l.retry)),
                ],
              ),
            ),
          if (!_loading && _entries.isNotEmpty || _period != _Period.all)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 4),
              child: _Totals(summary: summary, showMoney: showMoney, partial: _hasMore, unit: unit),
            ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 0),
            child: TextField(
              key: const Key('pbSearch'),
              controller: _search,
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                hintText: showMoney ? l.pbSearchHint : l.pbSearchHintNo,
                prefixIcon: const Icon(Icons.search_rounded),
                suffixIcon: _search.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.close_rounded),
                        onPressed: () => setState(_search.clear),
                      ),
              ),
            ),
          ),
          SizedBox(
            height: 52,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
              children: [
                for (final p in _Period.values)
                  Padding(
                    padding: const EdgeInsetsDirectional.only(end: 8),
                    child: ChoiceChip(
                      key: Key('pbPeriod-${p.name}'),
                      label: Text(_periodLabel(p)),
                      selected: _period == p,
                      onSelected: (_) {
                        if (_period == p) return;
                        setState(() => _period = p);
                        _reload();
                      },
                    ),
                  ),
              ],
            ),
          ),
          Expanded(child: body),
        ],
      ),
    );
  }
}

/// Закреплённые итоги: всего получено, средняя и последняя закупка, последний поставщик. Кассиру — только количество.
class _Totals extends StatelessWidget {
  final BatchSummary summary;
  final bool showMoney;
  final bool partial;
  final String unit;
  const _Totals({required this.summary, required this.showMoney, required this.partial, required this.unit});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    Widget tile(String label, String value, {Key? key}) => Container(
      key: key,
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(color: pal.soft, borderRadius: BorderRadius.circular(10), border: Border.all(color: pal.border)),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
          Text(value, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w800)),
        ],
      ),
    );
    final tiles = <Widget>[
      tile(l.pbTotalReceived, '${qtyText(summary.totalQty)} $unit', key: const Key('pbTotalQty')),
      if (showMoney) tile(l.pbAvgCost, summary.avgCost > 0 ? '${money(summary.avgCost)} ${l.som}' : '—', key: const Key('pbAvgCost')),
      if (showMoney) tile(l.pbLastCost, summary.lastCost > 0 ? '${money(summary.lastCost)} ${l.som}' : '—', key: const Key('pbLastCost')),
      if (showMoney) tile(l.pbLastSupplier, summary.lastSupplier.isEmpty ? '—' : summary.lastSupplier, key: const Key('pbLastSupplier')),
      if (!showMoney) tile(l.pbCount(summary.count), '', key: const Key('pbCount')),
    ];
    return LayoutBuilder(
      builder: (c, box) {
        final w = (box.maxWidth - 8) / 2;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Wrap(spacing: 8, runSpacing: 8, children: [for (final t in tiles) SizedBox(width: w, child: t)]),
            if (partial)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(l.pbPartial, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
              ),
          ],
        );
      },
    );
  }
}

// ======================================================================== накладная

/// Ключи, под которыми сервер мог бы отдать исходный файл накладной (поле может появиться позже; пока приходы его не содержат).
const _fileKeys = ['file', 'file_url', 'attachment', 'source_file', 'original_file', 'invoice_file', 'document', 'document_url', 'scan'];

/// Ссылка на исходный файл прихода; null — файл не сохранён.
String? receiptFileUrl(Map<String, dynamic>? r) {
  if (r == null) return null;
  for (final k in _fileKeys) {
    final v = r[k];
    final s = v is Map ? (v['url'] ?? v['file'] ?? '').toString() : (v is String ? v : '');
    if (s.trim().isEmpty) continue;
    final uri = Uri.tryParse(s.trim());
    if (uri == null) continue;
    final abs = uri.hasScheme ? uri : Uri.parse(ApiClient.defaultBaseUrl).resolveUri(uri);
    if (abs.scheme == 'http' || abs.scheme == 'https') return abs.toString();
  }
  return null;
}

class ReceiptDocScreen extends ConsumerStatefulWidget {
  final BatchEntry entry;
  final String productId;
  const ReceiptDocScreen({super.key, required this.entry, required this.productId});
  @override
  ConsumerState<ReceiptDocScreen> createState() => _ReceiptDocScreenState();
}

class _ReceiptDocScreenState extends ConsumerState<ReceiptDocScreen> {
  Map<String, dynamic>? _doc;
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _doc = widget.entry.raw; // сразу показываем то, что уже пришло со списка
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final d = await ref.read(productBatchesRepoProvider).receipt(widget.entry.receiptId);
      if (!mounted) return;
      setState(() {
        _doc = {...?_doc, ...d};
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = e is ApiException ? e.message : '$e';
      });
    }
  }

  Future<void> _openFile(String url) async {
    var ok = false;
    try {
      ok = await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
    } catch (_) {
      ok = false;
    }
    if (!ok && mounted) toast(context, context.l.pbFileError, error: true);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final e = widget.entry;
    final doc = _doc;
    final lines = doc == null ? const <Map<String, dynamic>>[] : _lines(doc);
    final fileUrl = receiptFileUrl(doc);
    final dateFmt = DateFormat('dd.MM.yyyy HH:mm');
    var total = doc == null ? 0.0 : num0(doc, 'total_amount');
    if (total == 0) {
      for (final it in lines) {
        total += (num0(it, 'qty') != 0 ? num0(it, 'qty') : num0(it, 'quantity')) * num0(it, 'purchase_price');
      }
    }
    return Scaffold(
      appBar: ScreenTitle(l.pbDocTitle, subtitle: l.pbNumber(e.shownNumber)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          Panel(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(e.at == null ? '—' : dateFmt.format(e.at!), style: const TextStyle(fontWeight: FontWeight.w800)),
                if (e.supplierName.isNotEmpty) Text(e.supplierName, style: const TextStyle(fontWeight: FontWeight.w600)),
                if (e.createdBy.isNotEmpty) Text(l.pbReceivedBy(e.createdBy), style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Text(l.pbDocLines, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
          const SizedBox(height: 6),
          if (_loading && lines.isEmpty) const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator())),
          if (_error != null && lines.isEmpty)
            Column(
              children: [
                Text(l.pbLoadError(_error!), textAlign: TextAlign.center),
                TextButton(onPressed: _load, child: Text(l.retry)),
              ],
            ),
          for (final it in lines) _docLine(context, it),
          if (lines.isNotEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Text(l.pbDocTotal(money(total)), textAlign: TextAlign.end, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
            ),
          const SizedBox(height: 8),
          if (fileUrl != null)
            FilledButton.icon(
              key: const Key('pbOpenFile'),
              onPressed: () => _openFile(fileUrl),
              icon: const Icon(Icons.attach_file_rounded),
              label: Text(l.pbOpenFile),
            )
          else if (!_loading || lines.isNotEmpty)
            Row(
              key: const Key('pbNoFile'),
              children: [
                Icon(Icons.info_outline_rounded, size: 18, color: pal.textSoft),
                const SizedBox(width: 8),
                Expanded(child: Text(l.pbNoFile, style: TextStyle(color: pal.textSoft))),
              ],
            ),
        ],
      ),
    );
  }

  Widget _docLine(BuildContext context, Map<String, dynamic> it) {
    final l = context.l;
    final pal = context.pal;
    final mine = _lineProductId(it) == widget.productId;
    final q = num0(it, 'qty') != 0 ? num0(it, 'qty') : num0(it, 'quantity');
    final cost = num0(it, 'purchase_price');
    final name = str(it, 'product_name') ?? str(it, 'name') ?? _nameOf(it['product']);
    final unit = str(it, 'unit') ?? '';
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Panel(
        color: mine ? pal.accentSoft : null,
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          key: ValueKey('pbDocLine-${_lineProductId(it)}'),
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(name.isEmpty ? '—' : name, style: const TextStyle(fontWeight: FontWeight.w700)),
            if (mine) Text(l.pbThisProduct, style: TextStyle(fontSize: 12, color: pal.textSoft)),
            Text(
              '${qtyText(q)} $unit × ${money(cost)} ${l.som} = ${money(q * cost)} ${l.som}',
              style: const TextStyle(fontSize: 13),
            ),
          ],
        ),
      ),
    );
  }
}
