import '../payment/receipt_pdf.dart';

import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:share_plus/share_plus.dart';

import '../../core/util/access.dart';
import '../../core/api/session_store.dart';
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../payment/receipt_qr.dart';
import '../admin/admin_more.dart';
import '../ops/ops_screens.dart';
import '../receipt/receipt.dart';
import '../receipt/receipt_view.dart';
import '../profile/printer_settings.dart';

/// Строка чека для списка: с сервера или из очереди телефона.
class CheckRow {
  final String id;
  final String number;
  final double total;
  final DateTime? at;
  final String payment;
  final String summary;
  final String status;
  final bool pending;
  final OutboxData? local;
  const CheckRow({
    required this.id,
    required this.number,
    required this.total,
    this.at,
    this.payment = '',
    this.summary = '',
    this.status = '',
    this.pending = false,
    this.local,
  });

  static CheckRow fromServer(Map<String, dynamic> m) => CheckRow(
    id: str(m, 'id') ?? '',
    number: str(m, 'number') ?? str(m, 'receipt_number') ?? '',
    total: num0(m, 'total'),
    at: DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal(),
    payment: str(m, 'payment_method') ?? '',
    summary: str(m, 'first_item_name') ?? '',
    status: str(m, 'status') ?? '',
  );
}

String paymentLabel(BuildContext context, String m) => switch (m) {
  'cash' => context.l.cash,
  'transfer' || 'card' => context.l.card,
  'debt' => context.l.debt,
  'mixed' => context.l.mixed,
  _ => m,
};

/// Мои чеки (Т5): «Эта смена» и «Не отправленные».
class ChecksScreen extends ConsumerStatefulWidget {
  const ChecksScreen({super.key});
  @override
  ConsumerState<ChecksScreen> createState() => _ChecksScreenState();
}

class _ChecksScreenState extends ConsumerState<ChecksScreen> {
  bool _unsent = false;
  final _q = TextEditingController();
  List<CheckRow> _server = [];
  bool _loading = false;
  String? _error;

  /// 2026-10-22 (мобилка, деньги): продажи смены — фильтр на сервере и «Ещё» (раньше только 1-я страница всей компании).
  int _page = 1;
  bool _hasMore = false;
  String? _legacyOwner;

  /// 2026-10-30 (оптимизация, п. 2): поток очереди создаётся один раз. Раньше — прямо в build(): на каждую букву поиска он
  /// пересоздавался и заново читал всю таблицу очереди. Отправленные и снятые отсекаются в SQL, строк не больше 200.
  late final Stream<List<OutboxData>> _outbox;

  bool _foreign(CheckRow r) =>
      r.local != null && !OutboxMeta.belongsTo(r.local!, OutboxMeta.ownerOf(ref.read(sessionProvider)), legacyOwner: _legacyOwner);

  @override
  void initState() {
    super.initState();
    _outbox = ref.read(databaseProvider).watchOutbox(kind: 'sale', hideSent: true, hideDropped: true, limit: 200);
    ref.read(databaseProvider).getSetting(OutboxMeta.legacyOwnerSetting).then((v) => mounted ? setState(() => _legacyOwner = v) : null);
    _load();
  }

  Future<void> _load({bool more = false}) async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final shiftId = ref.read(shiftProvider).shift?.id;
      final page = more ? _page + 1 : 1;
      final (rows, hasMore) = await ref.read(opsRepoProvider).shiftSales(shiftId, page: page);
      final mapped = rows.map(CheckRow.fromServer).toList();
      _server = more ? [..._server, ...mapped.where((r) => !_server.any((x) => x.id == r.id))] : mapped;
      _page = page;
      _hasMore = hasMore;
    } on ApiException catch (e) {
      _error = e.message;
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return StreamBuilder<List<OutboxData>>(
      stream: _outbox,
      builder: (c, snap) {
        // 2026-10-29 (полная изоляция аккаунтов): чужие неотправленные продажи под этим входом не показываются (плашка на главной)
        final me = OutboxMeta.ownerOf(ref.read(sessionProvider));
        final local = (snap.data ?? [])
            .where((o) => o.status != 'sent' && o.status != OutboxMeta.dropped && OutboxMeta.belongsTo(o, me, legacyOwner: _legacyOwner))
            .toList();
        final rows = _unsent
            ? local
                  .map(
                    (o) =>
                        CheckRow(id: o.id, number: '', total: o.total, at: o.createdAt, payment: o.paymentMethod, summary: o.summary, pending: true, local: o),
                  )
                  .toList()
            : _server;
        final q = _q.text.trim().toLowerCase();
        final shown = q.isEmpty ? rows : rows.where((r) => r.number.contains(q) || r.summary.toLowerCase().contains(q)).toList();
        return Column(
          children: [
            ScreenTitle(l.myChecks, back: false),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: SegmentedButton<bool>(
                showSelectedIcon: false,
                segments: [
                  ButtonSegment(value: false, label: Text(l.thisShift)),
                  ButtonSegment(value: true, label: Text(l.unsentN(local.length))),
                ],
                selected: {_unsent},
                onSelectionChanged: (s) => setState(() => _unsent = s.first),
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 10, 16, 6),
              child: TextField(
                controller: _q,
                onChanged: (_) => setState(() {}),
                decoration: InputDecoration(hintText: l.checkSearch, prefixIcon: const Icon(Icons.search_rounded)),
              ),
            ),
            if (_loading && !_unsent) const LinearProgressIndicator(),
            // 2026-10-26 (живой аудит, П5): ошибка с «Повторить»; последний загруженный список остаётся на экране.
            if (_error != null && !_unsent)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 8, 4),
                child: Row(
                  children: [
                    Icon(Icons.wifi_off_rounded, color: pal.danger, size: 18),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        _server.isEmpty ? '${l.checksLoadFailed}: $_error' : l.listCachedNote,
                        style: TextStyle(color: pal.danger, fontSize: 12.5),
                      ),
                    ),
                    TextButton(onPressed: _loading ? null : _load, child: Text(l.retry)),
                  ],
                ),
              ),
            Expanded(
              child: RefreshIndicator(
                onRefresh: _load,
                child: shown.isEmpty
                    ? ListView(
                        children: [
                          Padding(
                            padding: const EdgeInsets.all(32),
                            child: Center(
                              child: Text(l.noChecks, style: TextStyle(color: pal.textSoft)),
                            ),
                          ),
                        ],
                      )
                    : ListView.separated(
                        padding: const EdgeInsets.fromLTRB(16, 6, 16, 16),
                        itemCount: shown.length + (!_unsent && _hasMore ? 1 : 0),
                        separatorBuilder: (_, _) => const SizedBox(height: 8),
                        itemBuilder: (c, i) => i < shown.length
                            ? _CheckTile(shown[i], foreign: _foreign(shown[i]))
                            : OutlinedButton(onPressed: _loading ? null : () => _load(more: true), child: Text(l.loadMore)),
                      ),
              ),
            ),
          ],
        );
      },
    );
  }
}

class _CheckTile extends StatelessWidget {
  final CheckRow r;

  /// 2026-10-22 (мобилка, деньги, К-6): чек другого аккаунта — не отправляется под этим входом.
  final bool foreign;
  const _CheckTile(this.r, {this.foreign = false});
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final time = r.at == null ? '' : TimeOfDay.fromDateTime(r.at!).format(context);
    final returned = r.status.contains('return') || r.status == 'canceled';
    return Panel(
      onTap: () => context.push('/checks/detail', extra: r),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(r.pending ? l.checkNotSent : '${l.check} №${r.number}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                if (r.summary.isNotEmpty) Text(r.summary, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13)),
                Text('$time · ${paymentLabel(context, r.payment)}', style: TextStyle(fontSize: 12, color: pal.textSoft)),
              ],
            ),
          ),
          Column(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text('${money(r.total)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
              if (r.pending)
                Text(
                  foreign ? l.otherAccount : (r.local?.lastError != null && r.local!.status == 'failed' ? l.notAccepted : l.waitsForNetwork),
                  style: TextStyle(fontSize: 11.5, color: r.local?.status == 'failed' ? T.danger : T.warn, fontWeight: FontWeight.w700),
                )
              else if (returned)
                Text(
                  l.returned,
                  style: const TextStyle(fontSize: 11.5, color: T.danger, fontWeight: FontWeight.w700),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

/// Карточка чека (Т6): состав, итог, отправить покупателю; возврат — с разрешения администратора.
class CheckDetailScreen extends ConsumerStatefulWidget {
  final CheckRow row;
  const CheckDetailScreen({super.key, required this.row});
  @override
  ConsumerState<CheckDetailScreen> createState() => _CheckDetailScreenState();
}

class _CheckDetailScreenState extends ConsumerState<CheckDetailScreen> {
  Map<String, dynamic>? _sale;
  String? _error;

  @override
  void initState() {
    super.initState();
    if (!widget.row.pending) _load();
  }

  Future<void> _load() async {
    try {
      final s = await ref.read(salesRepoProvider).sale(widget.row.id);
      if (mounted) setState(() => _sale = s);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  /// Строки чека: с сервера (items / lines / cart.items) или из тела запроса в очереди.
  List<(String, double, double, double)> _lines() {
    if (widget.row.pending) {
      return [
        for (final m in (jsonDecode(widget.row.local!.display) as List).cast<List>())
          (m[0].toString(), (m[1] as num).toDouble(), (m[2] as num).toDouble(), (m[3] as num).toDouble()),
      ];
    }
    final s = _sale;
    if (s == null) return const [];
    dynamic items = s['items'] ?? s['lines'];
    if (items == null && s['cart'] is Map) items = (s['cart'] as Map)['items'];
    return [
      for (final m in asList(items))
        (
          str(m, 'product_name') ?? str(m, 'name') ?? str(m, 'title') ?? '',
          num0(m, 'quantity') > 0 ? num0(m, 'quantity') : num0(m, 'qty'),
          num0(m, 'unit_price') > 0 ? num0(m, 'unit_price') : num0(m, 'price'),
          num0(m, 'total') > 0 ? num0(m, 'total') : num0(m, 'line_total'),
        ),
    ];
  }

  /// Чек для ленты, PDF и QR: с сервера — полный (смена, кассир, фискальные реквизиты), без связи — из сохранённых строк.
  Receipt? _receipt() {
    final r = widget.row;
    final session = ref.read(sessionProvider);
    if (r.pending) {
      return Receipt(
        shop: (session?.companyName.isNotEmpty ?? false) ? session!.companyName : 'NurCRM',
        inn: session?.companyInn ?? '',
        address: session?.companyAddress ?? '',
        at: r.at ?? DateTime.now(),
        cashier: session?.userName ?? '',
        lines: [for (final (name, qty, price, total) in _lines()) ReceiptLine(name: name, qty: qty, price: price, total: total)],
        total: r.total,
        payment: r.payment,
        offline: true,
      );
    }
    final sale = _sale;
    if (sale == null) return null;
    return Receipt.fromSale(session: session, sale: sale, number: r.number, total: r.total, at: r.at, payment: r.payment, status: r.status);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final r = widget.row;
    final s = ref.watch(sessionProvider);
    final at = r.at == null
        ? ''
        : '${r.at!.day.toString().padLeft(2, '0')}.${r.at!.month.toString().padLeft(2, '0')} ${TimeOfDay.fromDateTime(r.at!).format(context)}';
    return Scaffold(
      appBar: ScreenTitle(
        r.pending ? l.checkNotSent : '${l.check} №${r.number}',
        subtitle: [at, s?.userName].whereType<String>().where((e) => e.isNotEmpty).join(' · '),
      ),
      body: ListView(
        // 2026-10-26 (живой аудит, П5): кнопки действий — в прокрутке с отступом снизу не меньше 24 dp (и над жестовой полосой).
        padding: EdgeInsets.fromLTRB(16, 4, 16, 24 + MediaQuery.paddingOf(context).bottom),
        children: [
          if (r.pending)
            Container(
              margin: const EdgeInsets.only(bottom: 10),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: context.pal.warnSoft, borderRadius: BorderRadius.circular(12)),
              child: Row(
                children: [
                  const Icon(Icons.wifi_off_rounded, color: T.warn, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(r.local?.lastError ?? l.offlineCheckHint, style: const TextStyle(color: T.warn, fontSize: 12.5)),
                  ),
                ],
              ),
            ),
          // 13.10, владелец: «отображение чека сделай красивым» — бумажная лента (ReceiptView).
          if (_sale == null && !r.pending && _error == null)
            const Padding(
              padding: EdgeInsets.all(24),
              child: Center(child: CircularProgressIndicator()),
            ),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.all(8),
              child: Row(
                children: [
                  Expanded(child: Text(_error!, style: TextStyle(color: pal.danger))),
                  TextButton(
                    onPressed: () {
                      setState(() => _error = null);
                      _load();
                    },
                    child: Text(l.retry),
                  ),
                ],
              ),
            ),
          if (_receipt() case final receipt?) ReceiptView(receipt: receipt),
          const SizedBox(height: 14),
          FieldLabel(l.sendToCustomer),
          OutlinedButton.icon(
            style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
            onPressed: _receipt() == null
                ? null
                : () => ReceiptQrScreen.show(context, receipt: _receipt()!, title: r.pending ? l.checkNotSent : '${l.check} №${r.number}'),
            icon: const Icon(Icons.qr_code_2_rounded),
            label: Text(l.showQr),
          ),
          const SizedBox(height: 8),
          // 2026-10-20: печать на принтер чеков.
          OutlinedButton.icon(
            onPressed: _receipt() == null ? null : () => printReceiptUi(context, _receipt()!),
            icon: const Icon(Icons.print_outlined),
            label: Text(l.prnPrint),
          ),
          const SizedBox(height: 8),
          // 13.10, макет Д5: по чеку — товарный чек, накладная или счёт PDF (A4).
          OutlinedButton.icon(
            onPressed: _receipt() == null ? null : () => chooseDocPdf(context, _receipt()!),
            icon: const Icon(Icons.description_outlined),
            label: Text(l.documentPdf),
          ),
          const SizedBox(height: 8),
          // 13.10, владелец: чек клиенту — PDF-файлом; текстом — запасной вариант.
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _receipt() == null ? null : () => shareReceiptPdf(_receipt()!),
                  icon: const Icon(Icons.picture_as_pdf_outlined),
                  label: const Text('PDF'),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _receipt() == null ? null : () => SharePlus.instance.share(ShareParams(text: _receipt()!.toText())),
                  icon: const Icon(Icons.chat_outlined),
                  label: Text(l.sendReceipt),
                ),
              ),
            ],
          ),
          // 13.10: возврат по чеку прямо с телефона (как в кассе): весь чек или выбранные строки.
          if (!r.pending && _sale != null && !(r.status.contains('return') || r.status == 'canceled')) ...[
            const SizedBox(height: 8),
            OutlinedButton.icon(
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48), foregroundColor: T.danger),
              onPressed: () async {
                if (!checkAccess(context, ref, Session.employeeReturn)) return;
                final done = await Navigator.of(context).push<bool>(
                  MaterialPageRoute(
                    builder: (_) => ReturnScreen(saleId: r.id, number: r.number, sale: _sale!),
                  ),
                );
                if (done == true && context.mounted) Navigator.of(context).pop();
              },
              icon: const Icon(Icons.undo_rounded),
              label: Text(l.returnTitle),
            ),
          ],
          if (r.pending && r.local?.status == 'failed') ...[
            const SizedBox(height: 10),
            FilledButton(
              onPressed: () async {
                await ref.read(databaseProvider).retryFailed(r.id);
                await ref.read(syncProvider).runOnce();
                if (context.mounted) Navigator.pop(context);
              },
              child: Text(l.sendAgain),
            ),
            // 2026-10-22 (мобилка, деньги): отклонённый сервером чек можно снять — он не уйдёт и не держит закрытие смены.
            const SizedBox(height: 8),
            OutlinedButton(
              style: OutlinedButton.styleFrom(foregroundColor: T.danger),
              onPressed: () async {
                final ok = await showDialog<bool>(
                  context: context,
                  builder: (c) => AlertDialog(
                    content: Text(l.dropRowAsk),
                    actions: [
                      TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                      FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.dropRow)),
                    ],
                  ),
                );
                if (ok != true) return;
                await OutboxMeta.drop(ref.read(databaseProvider), r.local!);
                if (context.mounted) Navigator.pop(context);
              },
              child: Text(l.dropRow),
            ),
          ],
          const SizedBox(height: 14),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              border: Border.all(color: pal.border),
              borderRadius: BorderRadius.circular(12),
            ),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(Icons.lock_outline_rounded, size: 16, color: pal.textSoft),
                const SizedBox(width: 6),
                Text(l.returnByAdmin, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
