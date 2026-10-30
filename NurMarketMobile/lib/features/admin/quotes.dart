import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../payment/payment_screen.dart';
import '../payment/sales_repository.dart' show Client;
import '../receipt/receipt.dart';
import '../sale/cart.dart';
import 'admin_screens.dart' show openWhatsApp;
import 'quotes_store.dart';
import 'waybill_pdf.dart';

/// 13.10, владелец: «коммерческое предложение и ЭСФ в мобилку добавь» (доски КП1–КП2, СЧ1–СЧ2). КП и счета на телефоне:
/// клиент из NurCRM, товары из каталога или из текущего чека, оптовые цены, скидка, срок; PDF по форме КР (как на кассе)
/// — отправить в WhatsApp/почту; «Выставить счёт по КП», оплата частями, «Провести продажу» — обычный чек NurCRM.
/// 2026-10-21: модель и хранение — quotes_store.dart; документы общие с программой владельца (ключ «quotes» общих настроек).
export 'quotes_store.dart';

String _d(DateTime t) => '${t.day.toString().padLeft(2, '0')}.${t.month.toString().padLeft(2, '0')}.${t.year}';

/// Бланк документа (PDF) из КП/счёта — тот же генератор, что накладная и счёт по чеку.
Receipt quoteReceipt(QuoteDoc d, {String shop = '', String inn = '', String address = '', String cashier = ''}) => Receipt(
  shop: shop,
  inn: inn,
  address: address,
  number: d.number,
  at: d.createdAt,
  cashier: cashier,
  client: [d.company, d.client, if (d.inn.isNotEmpty) 'ИНН ${d.inn}', d.phone].where((x) => x.trim().isNotEmpty).join(', '),
  lines: [for (final l in d.lines) ReceiptLine(name: l.name, qty: l.qty, unit: l.unit, price: l.price, total: l.sum)],
  discount: d.discount,
  total: d.total,
);

Future<void> shareQuotePdf(WidgetRef ref, QuoteDoc d) async {
  final s = ref.read(sessionProvider);
  final r = quoteReceipt(d, shop: s?.companyName ?? '', inn: s?.companyInn ?? '', address: s?.companyAddress ?? '', cashier: s?.userName ?? '');
  // 2026-10-22 (мобилка, админ): П13 — накладная ПК печатается накладной (не счётом), НДС документа — в PDF.
  await shareDocPdf(
    r,
    quoteDocKind(d),
    basis: d.isWaybill
        ? (d.basedOn.isNotEmpty ? 'документ ${d.basedOn}' : 'отпуск товара')
        : d.isQuote
        ? 'коммерческое предложение'
        : (d.basedOn.isNotEmpty ? 'коммерческое предложение ${d.basedOn}' : 'счёт на оплату'),
    note: d.isWaybill
        ? null
        : d.isQuote
        ? 'Предложение действует до ${_d(d.until)}.'
        : 'Оплатить до ${_d(d.until)}. В назначении платежа укажите номер счёта.',
    requisites: await ref.read(quotesStoreProvider).requisites(),
    paid: d.isInvoice ? d.paid : 0,
    vatPercent: d.vatPercent,
  );
}

/// 2026-10-22 (мобилка, админ): П13 — вид бланка документа.
DocKind quoteDocKind(QuoteDoc d) => d.isWaybill ? DocKind.waybill : (d.isQuote ? DocKind.quote : DocKind.invoice);

/// Название вида документа на языке кассира.
String quoteKindName(AppLocalizations l, QuoteDoc d) => d.isWaybill ? l.waybillDoc : (d.isQuote ? l.quoteNew : l.invoiceNew);

/// 2026-10-22 (мобилка, админ): П13 — «Продать» из документа: строки с ценой документа и скидка документа на чек
/// (раньше скидка терялась и чек выходил дороже КП). Возвращает строки, которых нет в каталоге телефона.
Future<List<String>> sellDocToCart(QuoteDoc d, Future<Product?> Function(String id) productById, CartController cart) async {
  final missing = <String>[];
  for (final x in d.lines) {
    final p = x.productId.isEmpty ? null : await productById(x.productId);
    if (p != null) {
      cart.add(p, qty: x.qty, variantPrice: x.price);
    } else {
      missing.add(x.name);
    }
  }
  if (d.discount > 0.005) cart.setOrderDiscount(sum: d.discount);
  return missing;
}

/// Плашка статуса: текст, фон, цвет текста. Красный — только просроченный счёт (правило редизайна 4).
(String, Color, Color) quoteStatus(BuildContext context, QuoteDoc d) {
  final l = context.l;
  final p = context.pal;
  // 14.10, обход мобилки: светлые плашки и тёмно-коричневый текст в тёмной теме не читались — цвета палитры.
  final red = p.dark ? const Color(0xFFF97066) : T.danger;
  final yellow = p.dark ? T.accent : const Color(0xFF7A5A00);
  final green = p.dark ? const Color(0xFF4ADE80) : T.success;
  final orange = p.dark ? const Color(0xFFFBBF24) : const Color(0xFFB54708);
  final grey = p.dark ? const Color(0x22FFFFFF) : const Color(0xFFF2F4F7);
  if (d.overdue) return (l.invoicesOverdue, p.dangerSoft, red);
  if (d.expired) return (l.stExpired, grey, p.textSoft);
  return switch (d.status) {
    'sent' => (l.stSent, p.accentSoft, yellow),
    'accepted' => (l.stAccepted, p.successSoft, green),
    'declined' => (l.stDeclined, grey, p.textSoft),
    'issued' => (l.stIssued, p.accentSoft, yellow),
    'partial' => (l.stPartial, p.warnSoft, orange),
    'paid' => (l.stPaid, p.successSoft, green),
    _ => (l.stDraft, grey, p.textSoft),
  };
}

class QuoteStatusChip extends StatelessWidget {
  final QuoteDoc doc;
  const QuoteStatusChip(this.doc, {super.key});
  @override
  Widget build(BuildContext context) {
    final (text, bg, fg) = quoteStatus(context, doc);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(99)),
      child: Text(
        text,
        style: TextStyle(color: fg, fontSize: 11.5, fontWeight: FontWeight.w700),
      ),
    );
  }
}

class QuotesScreen extends ConsumerStatefulWidget {
  const QuotesScreen({super.key});
  @override
  ConsumerState<QuotesScreen> createState() => _QuotesScreenState();
}

class _QuotesScreenState extends ConsumerState<QuotesScreen> {
  List<QuoteDoc> _docs = [];
  String _filter = 'all';
  final _q = TextEditingController();

  /// 2026-10-21: общий список с программой владельца — что ещё не ушло на сервер и как прошла синхронизация.
  Set<String> _unsent = {};
  QuotesSyncResult? _sync;
  DateTime? _syncedAt;
  bool _syncing = false;

  @override
  void initState() {
    super.initState();
    _reload().then((_) => _syncNow());
  }

  @override
  void dispose() {
    _q.dispose();
    super.dispose();
  }

  Future<void> _reload() async {
    final store = ref.read(quotesStoreProvider);
    final docs = await store.load();
    final unsent = await store.unsentIds();
    final at = await store.syncedAt();
    if (mounted) {
      setState(() {
        _docs = docs..sort((a, b) => b.createdAt.compareTo(a.createdAt));
        _unsent = unsent;
        _syncedAt = at;
      });
    }
  }

  /// 2026-10-21: свежий общий список (открытие раздела, «потянуть вниз», после каждого изменения). Без связи — копия
  /// телефона, изменения помечены «не отправлено» и уйдут позже.
  Future<void> _syncNow() async {
    if (_syncing || !mounted) return;
    setState(() => _syncing = true);
    final res = await ref.read(quotesStoreProvider).sync();
    if (!mounted) return;
    await _reload();
    if (!mounted) return;
    setState(() {
      _sync = res;
      _syncing = false;
    });
    final l = context.l;
    for (final n in res.lost) {
      toast(context, l.quoteChangedElsewhere(n));
    }
    for (final (a, b) in res.renumbered) {
      toast(context, l.quoteRenumbered(a, b));
    }
  }

  /// Записать документ на телефоне и сразу попробовать отправить.
  Future<void> _put(QuoteDoc d) async {
    await ref.read(quotesStoreProvider).put(d);
    await _reload();
    unawaited(_syncNow());
  }

  Future<QuoteDoc?> _edit(QuoteDoc? doc, {required bool quote}) async {
    final saved = await Navigator.of(context).push<QuoteDoc>(
      MaterialPageRoute(
        builder: (_) => QuoteEditScreen(doc: doc, quote: quote),
      ),
    );
    if (saved == null) return null;
    await _put(saved);
    return saved;
  }

  /// 2026-10-21: «Создать» — одна главная кнопка экрана, вид документа выбирается в листе.
  Future<void> _create() async {
    final l = context.l;
    final quote = await showModalBottomSheet<bool>(
      context: context,
      builder: (c) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(leading: const Icon(Icons.description_outlined), title: Text(l.quoteNew), onTap: () => Navigator.pop(c, true)),
            ListTile(leading: const Icon(Icons.request_quote_outlined), title: Text(l.invoiceNew), onTap: () => Navigator.pop(c, false)),
          ],
        ),
      ),
    );
    if (quote == null || !mounted) return;
    final d = await _edit(null, quote: quote);
    if (d != null && mounted) await _open(d);
  }

  Future<void> _open(QuoteDoc d) => Navigator.of(context).push(
    MaterialPageRoute(
      builder: (_) => QuoteViewScreen(doc: d, onAction: _run),
    ),
  );

  /// Действие с документом из экрана документа. Возвращает документ, который показать дальше (тот же или новый счёт),
  /// null — документ удалён или ушли на другой экран.
  Future<QuoteDoc?> _run(QuoteDoc d, String a) async {
    final l = context.l;
    final now = DateTime.now();
    String stamp(String text) => '${_d(now)} ${now.hour.toString().padLeft(2, '0')}:${now.minute.toString().padLeft(2, '0')} · $text';
    switch (a) {
      case 'pdf':
        await shareQuotePdf(ref, d);
        if (d.status == 'draft') d.status = 'sent';
        d.history.add(stamp('PDF · ${l.stSent}'));
        await _put(d);
      case 'wa':
        final text = l.docWaText(quoteKindName(l, d), d.number, money(d.isInvoice ? d.left : d.total), d.isWaybill ? '' : (d.isQuote ? l.validUntil(_d(d.until)) : l.payUntil(_d(d.until))));
        await openWhatsApp(d.phone, text);
        if (d.status == 'draft') d.status = 'sent';
        d.history.add(stamp('WhatsApp · ${l.stSent}'));
        await _put(d);
      case 'invoice':
        final inv = QuoteDoc(
          id: now.microsecondsSinceEpoch.toString(),
          number: await ref.read(quotesStoreProvider).nextNumber(false),
          isQuote: false,
          client: d.client,
          phone: d.phone,
          inn: d.inn,
          company: d.company,
          lines: [for (final x in d.lines) x.copy()],
          discount: d.discount,
          until: DateUtils.dateOnly(now).add(const Duration(days: 3)),
          status: 'issued',
          createdAt: now,
          basedOn: d.number,
          // 2026-10-22 (мобилка, админ): П13 — НДС КП переходит в счёт.
          extra: {if (d.extra['vat'] != null) 'vat': d.extra['vat']},
        );
        // 2026-10-21: номер счёта мог смениться при записи (занят на ПК) — в истории КП итоговый номер.
        await ref.read(quotesStoreProvider).put(inv);
        d.status = 'accepted';
        d.history.add(stamp('${l.invoiceNew} ${inv.number}'));
        await _put(d);
        return inv;
      case 'pay':
        // 2026-10-22 (мобилка, админ): П13 — «Принять оплату» — только отметка в документе: деньги в кассу смены и долг
        // NurCRM не записываются. Предупреждаем, чтобы не путали с продажей.
        final go = await showDialog<bool>(
          context: context,
          builder: (c) => AlertDialog(
            title: Text(l.takePayment),
            content: Text(l.docPayMarkOnly),
            actions: [
              TextButton(style: TextButton.styleFrom(foregroundColor: context.pal.text), onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
              FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(110, 44)), onPressed: () => Navigator.pop(c, true), child: Text(l.docPayMarkOk)),
            ],
          ),
        );
        if (go != true || !mounted) return d;
        final sum = await askNumber(context, title: '${l.takePayment} · ${d.number}', hint: money(d.left), initial: d.left);
        if (sum == null || sum <= 0) return d;
        d.paid = roundMoney((d.paid + sum).clamp(0, d.total).toDouble());
        d.status = d.left <= 0.005 ? 'paid' : 'partial';
        d.history.add(stamp('${l.takePayment}: ${money(sum)}'));
        await _put(d);
      case 'sell':
        final db = ref.read(databaseProvider);
        final cart = ref.read(cartProvider.notifier);
        // 2026-10-22 (мобилка, админ): П13 — в чеке уже есть товары: заменить, а не смешать с документом.
        if (ref.read(cartProvider).lines.isNotEmpty) {
          final replace = await showDialog<bool>(
            context: context,
            builder: (c) => AlertDialog(
              title: Text(l.sellFromDoc),
              content: Text(l.docSellCartBusy),
              actions: [
                TextButton(style: TextButton.styleFrom(foregroundColor: context.pal.text), onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(110, 44)), onPressed: () => Navigator.pop(c, true), child: Text(l.docSellReplace)),
              ],
            ),
          );
          if (replace != true || !mounted) return d;
          cart.clear();
        }
        final missing = await sellDocToCart(d, db.productById, cart);
        // отметка в документе: передан в продажу (сама продажа — обычный чек NurCRM)
        d.history.add(stamp(l.docSentToSale));
        if (d.isQuote && (d.status == 'draft' || d.status == 'sent')) d.status = 'accepted';
        await _put(d);
        if (!mounted) return null;
        if (missing.isNotEmpty) toast(context, l.docSellMissing(missing.join(', ')), error: true);
        context.go('/sale');
        return null;
      case 'edit':
        return await _edit(d, quote: d.isQuote) ?? d;
      case 'declined':
        d.status = 'declined';
        d.history.add(stamp(l.stDeclined));
        await _put(d);
      case 'delete':
        // 2026-10-21: удаление — «надгробие» в общем списке, документ пропадёт и в программе владельца.
        await ref.read(quotesStoreProvider).remove(d.id);
        await _reload();
        unawaited(_syncNow());
        return null;
    }
    return d;
  }

  Future<void> _editRequisites() async {
    final l = context.l;
    final store = ref.read(quotesStoreProvider);
    final ctrl = TextEditingController(text: await store.requisites());
    if (!mounted) return;
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.requisitesTitle),
        scrollable: true,
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l.requisitesHint, style: TextStyle(color: context.pal.textSoft, fontSize: 12.5)),
            const SizedBox(height: 8),
            TextField(controller: ctrl, minLines: 3, maxLines: 6, keyboardType: TextInputType.multiline),
          ],
        ),
        actions: [
          TextButton(
            style: TextButton.styleFrom(foregroundColor: context.pal.text),
            onPressed: () => Navigator.pop(c, false),
            child: Text(l.cancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size(110, 44)),
            onPressed: () => Navigator.pop(c, true),
            child: Text(l.save),
          ),
        ],
      ),
    );
    if (ok == true) await store.setRequisites(ctrl.text);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final waiting = _docs.where((d) => d.isQuote && (d.status == 'sent' || d.status == 'draft') && !d.expired);
    final unpaid = _docs.where((d) => d.isInvoice && d.status != 'paid');
    final overdue = unpaid.where((d) => d.overdue);
    final list = _docs
        .where(
          (d) => switch (_filter) {
            'quotes' => d.isQuote,
            'invoices' => d.isInvoice,
            'waybills' => d.isWaybill,
            'waiting' => d.isQuote && (d.status == 'sent' || d.status == 'draft') && !d.expired,
            'unpaid' => d.isInvoice && d.status != 'paid',
            _ => true,
          },
        )
        .where((d) => d.matches(_q.text))
        .toList();
    // 2026-10-21, правило редизайна 3: суммы не обрезаются «…» — число уменьшается под ширину плитки.
    Widget kpi(String title, String value, String note, {Color? color}) => Expanded(
      child: Panel(
        padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, maxLines: 2, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(
                value,
                style: TextStyle(fontSize: 16, fontWeight: FontWeight.w800, color: color),
              ),
            ),
            Text(note, style: TextStyle(fontSize: 11, color: pal.textSoft)),
          ],
        ),
      ),
    );
    // 2026-10-21: состояние общего списка — «не отправлено» (без связи / нет прав) сверху, время обновления внизу.
    final warn = pal.dark ? const Color(0xFFFBBF24) : T.warn;
    final forbidden = _sync?.state == QuotesSyncState.forbidden;
    String two(int v) => v.toString().padLeft(2, '0');
    final syncNote = _syncing
        ? l.quotesSyncing
        : switch (_sync?.state) {
            QuotesSyncState.synced when _syncedAt != null => l.quotesSharedAt('${_d(_syncedAt!)} ${two(_syncedAt!.hour)}:${two(_syncedAt!.minute)}'),
            QuotesSyncState.synced => l.quotesShared,
            _ => l.quotesOffline,
          };
    return Scaffold(
      appBar: ScreenTitle(
        l.quotesTitle,
        actions: [IconButton(tooltip: l.requisitesTitle, onPressed: _editRequisites, icon: const Icon(Icons.account_balance_outlined))],
      ),
      floatingActionButton: FloatingActionButton.extended(
        backgroundColor: T.accent,
        foregroundColor: T.navy,
        onPressed: _create,
        icon: const Icon(Icons.add_rounded),
        label: Text(l.createDoc, style: const TextStyle(fontWeight: FontWeight.w800)),
      ),
      body: RefreshIndicator(
        onRefresh: _syncNow,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                kpi(l.quotesWaiting, money(waiting.fold(0.0, (s, d) => s + d.total)), '${waiting.length}'),
                const SizedBox(width: 6),
                kpi(l.invoicesUnpaid, money(unpaid.fold(0.0, (s, d) => s + d.left)), '${unpaid.length}'),
                const SizedBox(width: 6),
                kpi(l.invoicesOverdue, money(overdue.fold(0.0, (s, d) => s + d.left)), '${overdue.length}', color: overdue.isEmpty ? null : T.danger),
              ],
            ),
            if (_unsent.isNotEmpty || forbidden) ...[
              const SizedBox(height: 8),
              Panel(
                color: pal.warnSoft,
                onTap: _syncing ? null : _syncNow,
                padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
                child: Row(
                  children: [
                    Icon(forbidden ? Icons.lock_outline_rounded : Icons.cloud_off_rounded, color: warn, size: 20),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        forbidden ? l.quotesForbidden : l.quotesNotSent(_unsent.length),
                        style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: pal.text),
                      ),
                    ),
                    if (!forbidden) Icon(Icons.refresh_rounded, color: pal.textSoft, size: 20),
                  ],
                ),
              ),
            ],
            const SizedBox(height: 10),
            TextField(
              controller: _q,
              textInputAction: TextInputAction.search,
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                hintText: l.quoteSearch,
                prefixIcon: const Icon(Icons.search_rounded),
                suffixIcon: _q.text.isEmpty
                    ? null
                    : IconButton(
                        tooltip: l.cancel,
                        icon: const Icon(Icons.close_rounded),
                        onPressed: () => setState(() => _q.clear()),
                      ),
              ),
            ),
            const SizedBox(height: 6),
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  for (final (id, text) in [
                    ('all', l.filterAll),
                    ('quotes', l.filterQuotes),
                    ('invoices', l.filterInvoices),
                    ('waiting', l.quotesWaiting),
                    ('unpaid', l.invoicesUnpaid),
                    // 2026-10-22 (мобилка, админ): П13 — накладные программы владельца отдельным фильтром.
                    if (_docs.any((d) => d.isWaybill)) ('waybills', l.waybillsFilter),
                  ])
                    Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: ChoiceChip(label: Text(text), selected: _filter == id, onSelected: (_) => setState(() => _filter = id)),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 8),
            // 2026-10-21: пустое состояние с подсказкой, что делать дальше.
            if (list.isEmpty)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 28, horizontal: 12),
                child: Column(
                  children: [
                    Icon(Icons.request_quote_outlined, size: 44, color: pal.textSoft),
                    const SizedBox(height: 8),
                    Text(
                      _docs.isEmpty ? l.quotesEmpty : l.quotesNoMatch,
                      textAlign: TextAlign.center,
                      style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15),
                    ),
                    if (_docs.isEmpty) ...[
                      const SizedBox(height: 4),
                      Text(
                        l.quotesEmptyHint,
                        textAlign: TextAlign.center,
                        style: TextStyle(color: pal.textSoft, fontSize: 13),
                      ),
                    ],
                  ],
                ),
              ),
            for (final d in list)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Panel(
                  onTap: () => _open(d),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Wrap(
                              spacing: 6,
                              runSpacing: 2,
                              crossAxisAlignment: WrapCrossAlignment.center,
                              children: [
                                Text(d.number, style: const TextStyle(fontWeight: FontWeight.w800)),
                                QuoteStatusChip(d),
                                if (_unsent.contains(d.id))
                                  Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Icon(Icons.cloud_upload_outlined, size: 15, color: warn),
                                      const SizedBox(width: 3),
                                      Text(l.notSentMark, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: warn)),
                                    ],
                                  ),
                              ],
                            ),
                            const SizedBox(height: 2),
                            Text(
                              d.buyer.isEmpty ? l.clientNotChosen : d.buyer,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(fontWeight: FontWeight.w600, color: d.buyer.isEmpty ? pal.textSoft : null),
                            ),
                            Text(
                              [
                                _d(d.createdAt),
                                if (d.basedOn.isNotEmpty) l.basedOnQuote(d.basedOn),
                                if (d.isWaybill) l.waybillDoc,
                                if (d.isInvoice && d.paid > 0) l.paidOf(money(d.paid), money(d.total)),
                              ].join(' · '),
                              style: TextStyle(color: pal.textSoft, fontSize: 12),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(width: 10),
                      Text(
                        '${money(d.total)} ${l.som}',
                        style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: pal.price),
                      ),
                    ],
                  ),
                ),
              ),
            const SizedBox(height: 10),
            Text(syncNote, style: TextStyle(color: pal.textSoft, fontSize: 12, fontWeight: FontWeight.w600)),
            const SizedBox(height: 2),
            Text(l.quotesLocalNote, style: TextStyle(color: pal.textSoft, fontSize: 12)),
          ],
        ),
      ),
    );
  }
}

/// 2026-10-21, владелец: «проработай дизайн» КП и счёта. Экран документа: клиент сверху, строки «2 × 85 = 170», итог
/// крупно; внизу «PDF», «Отправить» (WhatsApp клиенту) и одна главная кнопка — «Сделать счёт» у КП или «Принять оплату»
/// у счёта; редкие действия (изменить, продажа, отказ, удалить) — в «⋯». Раньше по нажатию на документ был только список действий.
class QuoteViewScreen extends StatefulWidget {
  final QuoteDoc doc;
  final Future<QuoteDoc?> Function(QuoteDoc doc, String action) onAction;
  const QuoteViewScreen({super.key, required this.doc, required this.onAction});
  @override
  State<QuoteViewScreen> createState() => _QuoteViewScreenState();
}

class _QuoteViewScreenState extends State<QuoteViewScreen> {
  late QuoteDoc _doc = widget.doc;
  bool _busy = false;

  Future<void> _act(String a) async {
    if (_busy) return;
    final l = context.l;
    if (a == 'delete') {
      final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: Text(l.deleteDocAsk(_doc.number)),
          content: Text(l.deleteDocAskSub),
          actions: [
            TextButton(
            style: TextButton.styleFrom(foregroundColor: context.pal.text),
            onPressed: () => Navigator.pop(c, false),
            child: Text(l.cancel),
          ),
            FilledButton(
              style: FilledButton.styleFrom(backgroundColor: T.danger, foregroundColor: Colors.white, minimumSize: const Size(110, 44)),
              onPressed: () => Navigator.pop(c, true),
              child: Text(l.deleteDoc),
            ),
          ],
        ),
      );
      if (ok != true || !mounted) return;
    }
    setState(() => _busy = true);
    try {
      final next = await widget.onAction(_doc, a);
      if (!mounted) return;
      if (next == null) {
        if (a == 'delete') Navigator.of(context).pop();
        return;
      }
      if (next.id != _doc.id) toast(context, l.invoiceCreated(next.number));
      setState(() => _doc = next);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final d = _doc;
    final red = pal.dark ? const Color(0xFFF97066) : T.danger;
    final primary = d.isWaybill
        ? null
        : d.isQuote
        ? (d.status == 'accepted' || d.status == 'declined' ? null : ('invoice', l.makeInvoice, Icons.request_quote_rounded))
        : (d.status == 'paid' ? null : ('pay', l.takePayment, Icons.payments_rounded));
    Widget sumRow(String label, String value, {bool big = false, Color? color}) => Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(label, style: TextStyle(color: big ? null : pal.textSoft, fontWeight: big ? FontWeight.w800 : FontWeight.w500)),
          const SizedBox(width: 12),
          Expanded(
            child: FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerRight,
              child: Text(
                value,
                style: TextStyle(fontSize: big ? 26 : 14, fontWeight: big ? FontWeight.w800 : FontWeight.w700, color: color ?? (big ? pal.price : null)),
              ),
            ),
          ),
        ],
      ),
    );
    return Scaffold(
      appBar: ScreenTitle(
        d.number,
        subtitle: quoteKindName(l, d),
        actions: [
          PopupMenuButton<String>(
            icon: const Icon(Icons.more_horiz_rounded),
            onSelected: _act,
            itemBuilder: (_) => [
              PopupMenuItem(value: 'edit', child: Text(l.editDoc)),
              PopupMenuItem(value: 'sell', child: Text(l.sellFromDoc)),
              if (d.isQuote && d.status != 'declined' && d.status != 'accepted') PopupMenuItem(value: 'declined', child: Text(l.markDeclined)),
              PopupMenuItem(
                value: 'delete',
                child: Text(l.deleteDoc, style: TextStyle(color: red)),
              ),
            ],
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
        children: [
          Wrap(
            spacing: 10,
            runSpacing: 4,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              QuoteStatusChip(d),
              Text(l.docFrom(_d(d.createdAt)), style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
              if (!d.isWaybill)
                Text(
                  d.isQuote ? l.validUntil(_d(d.until)) : l.payUntil(_d(d.until)),
                  style: TextStyle(fontSize: 12.5, color: d.overdue ? red : pal.textSoft, fontWeight: d.overdue ? FontWeight.w700 : null),
                ),
            ],
          ),
          const SizedBox(height: 10),
          Panel(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(d.company.isNotEmpty ? Icons.business_rounded : Icons.person_outline_rounded, color: pal.textSoft),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        d.buyer.isEmpty ? l.clientNotChosen : (d.company.isNotEmpty ? d.company : d.client),
                        style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: d.buyer.isEmpty ? pal.textSoft : null),
                      ),
                      if (d.company.isNotEmpty && d.client.isNotEmpty) Text(d.client),
                      if (d.phone.isNotEmpty || d.inn.isNotEmpty)
                        Text(
                          [d.phone, if (d.inn.isNotEmpty) '${l.innLabel} ${d.inn}'].where((x) => x.isNotEmpty).join(' · '),
                          style: TextStyle(color: pal.textSoft, fontSize: 12.5),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 6),
          FieldLabel('${l.docLines} · ${d.lines.length}', upper: false),
          Panel(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
            child: Column(
              children: [
                for (var i = 0; i < d.lines.length; i++) ...[
                  if (i > 0) Divider(height: 1, color: pal.border),
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 8),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text(d.lines[i].name, style: const TextStyle(fontWeight: FontWeight.w700)),
                        const SizedBox(height: 2),
                        Row(
                          children: [
                            Expanded(
                              child: Text(
                                '${qtyText(d.lines[i].qty)} ${d.lines[i].unit} × ${money(d.lines[i].price)} =',
                                style: TextStyle(color: pal.textSoft, fontSize: 13),
                              ),
                            ),
                            Text(money(d.lines[i].sum), style: const TextStyle(fontWeight: FontWeight.w800)),
                          ],
                        ),
                      ],
                    ),
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(height: 8),
          Panel(
            child: Column(
              children: [
                if (d.discount > 0.005) ...[
                  sumRow(l.docSubtotal, '${money(d.subtotal)} ${l.som}'),
                  sumRow(l.discountShort, '−${money(d.subtotal - d.total)} ${l.som}'),
                ],
                sumRow(l.total, '${money(d.total)} ${l.som}', big: true),
                // 2026-10-22 (мобилка, админ): П13 — НДС «в том числе», как в PDF и на ПК.
                if (d.vatPercent > 0) sumRow(l.docVatIncl(d.vatPercent.toStringAsFixed(d.vatPercent == d.vatPercent.roundToDouble() ? 0 : 1)), '${money(vatIncluded(d.total, d.vatPercent))} ${l.som}'),
                if (d.isInvoice && d.paid > 0.005) ...[
                  sumRow(l.paidLabel, '${money(d.paid)} ${l.som}'),
                  sumRow(l.leftToPay, '${money(d.left)} ${l.som}', color: d.overdue ? red : null),
                ],
              ],
            ),
          ),
          if (d.history.isNotEmpty) ...[
            const SizedBox(height: 6),
            FieldLabel(l.historyLabel, upper: false),
            for (final h in d.history.reversed)
              Padding(
                padding: const EdgeInsets.only(bottom: 2),
                child: Text(h, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
              ),
          ],
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _busy ? null : () => _act('pdf'),
                      icon: const Icon(Icons.picture_as_pdf_outlined),
                      label: const Text('PDF'),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _busy ? null : () => _act('wa'),
                      icon: const Icon(Icons.chat_outlined),
                      label: Text(l.sendToClient),
                    ),
                  ),
                ],
              ),
              if (primary != null) ...[
                const SizedBox(height: 8),
                FilledButton.icon(
                  onPressed: _busy ? null : () => _act(primary.$1),
                  icon: Icon(primary.$3),
                  label: Text(primary.$2),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// КП2 / СЧ1: документ — клиент (из NurCRM, новый или вручную), ИНН, товары, оптовые цены, скидка, срок.
class QuoteEditScreen extends ConsumerStatefulWidget {
  final QuoteDoc? doc;
  final bool quote;
  const QuoteEditScreen({super.key, this.doc, required this.quote});
  @override
  ConsumerState<QuoteEditScreen> createState() => _QuoteEditScreenState();
}

class _QuoteEditScreenState extends ConsumerState<QuoteEditScreen> {
  late final _client = TextEditingController(text: widget.doc?.client ?? '');
  late final _phone = TextEditingController(text: widget.doc?.phone ?? '');
  late final _inn = TextEditingController(text: widget.doc?.inn ?? '');
  late final _company = TextEditingController(text: widget.doc?.company ?? '');
  // 2026-10-21: скидка 12,5 сом показывалась как «13» и при сохранении превращалась в 13 — без округления.
  late final _discount = TextEditingController(text: (widget.doc?.discount ?? 0) > 0 ? qtyText(widget.doc!.discount).replaceAll(' ', '') : '');
  late final List<QuoteLine> _lines = [
    for (final x in widget.doc?.lines ?? <QuoteLine>[]) x.copy(),
  ];
  late DateTime _until = widget.doc?.until ?? DateUtils.dateOnly(DateTime.now()).add(Duration(days: widget.quote ? 7 : 3));
  bool _wholesale = false;
  bool _saving = false;

  double get _subtotal => linesSubtotal(_lines);
  double get _discountSum => parseDiscount(_discount.text, _subtotal);
  double get _total => roundMoney((_subtotal - _discountSum).clamp(0, double.infinity).toDouble());

  void _setClient(Client c) {
    setState(() {
      _client.text = c.name;
      _phone.text = c.phone;
      if (c.company.isNotEmpty) _company.text = c.company;
      if (c.inn.isNotEmpty) _inn.text = c.inn;
    });
  }

  Future<void> _pickClient() async {
    final c = await pickClient(context, requisites: true);
    if (c != null && mounted) _setClient(c);
  }

  /// 2026-10-21, владелец: «КП — создание нового контакта». Новый клиент прямо из документа: имя, телефон, компания, ИНН —
  /// сохраняется в NurCRM и сразу подставляется в документ.
  Future<void> _newClient() async {
    final c = await createClientDialog(context, initial: _client.text, requisites: true);
    if (c != null && mounted) _setClient(c);
  }

  double _priceOf(Product p) => _wholesale && p.wholesalePrice > 0 ? p.wholesalePrice : p.price;

  void _addProduct(Product p, {double qty = 1}) {
    setState(() => addQuoteLine(_lines, QuoteLine(productId: p.id, name: p.name, unit: p.unit, qty: qty, price: _priceOf(p)), anyPrice: true));
  }

  Future<void> _pickProduct() async {
    // 14.10, владелец: «в КП не выходит список товаров» — список был пуст, пока не введены 2 буквы. Теперь общий выбор
    // товара: список сразу, поиск и фильтры (остаток, категория).
    final l = context.l;
    final p = await pickProduct(context, subtitle: (x) => '${money(_priceOf(x))} ${l.som}${x.isService ? '' : ' · ${qtyText(x.quantity)} ${x.unit}'}');
    if (p == null || !mounted) return;
    _addProduct(p);
  }

  /// 2026-10-21: сканер камерой — товары добавляются подряд, тот же штрихкод в течение 1,5 с не считается повторно.
  Future<void> _scan() => showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    builder: (_) => _QuoteScanSheet(onProduct: (p, qty) => _addProduct(p, qty: qty)),
  );

  void _fromCart() {
    final cart = ref.read(cartProvider);
    setState(() {
      for (final x in cart.lines) {
        addQuoteLine(_lines, QuoteLine(productId: x.productId, name: x.name, unit: x.unit, qty: x.qty, price: x.price));
      }
    });
  }

  Future<void> _applyWholesale(bool on) async {
    final db = ref.read(databaseProvider);
    _wholesale = on;
    for (final x in _lines) {
      final p = x.productId.isEmpty ? null : await db.productById(x.productId);
      if (p != null) x.price = _priceOf(p);
    }
    if (mounted) setState(() {});
  }

  Future<void> _save() async {
    // 2026-10-21: двойное касание «Сохранить», пока выдаётся номер, создавало два документа и второе закрытие уводило
    // со списка (документ терялся). Сохранение — один раз.
    if (_saving) return;
    final l = context.l;
    if (_lines.isEmpty) {
      toast(context, l.addLine, error: true);
      return;
    }
    _saving = true;
    final d =
        widget.doc ??
        QuoteDoc(
          id: DateTime.now().microsecondsSinceEpoch.toString(),
          number: await ref.read(quotesStoreProvider).nextNumber(widget.quote),
          isQuote: widget.quote,
          until: _until,
          status: widget.quote ? 'draft' : 'issued',
          createdAt: DateTime.now(),
        );
    // 2026-10-22 (мобилка, админ): П13 — новый документ получает НДС по умолчанию из реквизитов программы владельца.
    if (widget.doc == null) {
      final vat = await ref.read(quotesStoreProvider).defaultVat();
      if (vat > 0) d.extra['vat'] = vat;
    }
    d
      ..client = _client.text.trim()
      ..phone = _phone.text.trim()
      ..inn = _inn.text.trim()
      ..company = _company.text.trim()
      ..lines = _lines
      ..discount = _discountSum
      ..until = _until;
    if (!d.isQuote && d.paid > 0) d.status = d.left <= 0.005 ? 'paid' : 'partial';
    if (mounted) Navigator.of(context).pop(d);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final pct = _discount.text.trim().endsWith('%');
    final compact = OutlinedButton.styleFrom(padding: const EdgeInsets.symmetric(horizontal: 10), textStyle: const TextStyle(fontFamily: 'Montserrat', fontSize: 13.5, fontWeight: FontWeight.w700));
    Widget field(String label, TextEditingController c, {TextInputType? type, TextInputAction action = TextInputAction.next}) => Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FieldLabel(label, upper: false),
        TextField(controller: c, keyboardType: type, textInputAction: action, onChanged: (_) => setState(() {})),
      ],
    );
    // 14.10, владелец: случайное «назад» — спросить, если в документе уже что-то есть.
    return LeaveGuard(
      ask: () => !_saving && (_lines.isNotEmpty || _client.text.trim().isNotEmpty),
      child: Scaffold(
        appBar: ScreenTitle(widget.doc?.number ?? (widget.quote ? l.quoteNew : l.invoiceNew)),
        body: ListView(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
          children: [
            // 2026-10-21: клиент сверху — найти в NurCRM или создать нового, поля можно поправить вручную.
            Panel(
              padding: const EdgeInsets.fromLTRB(14, 10, 14, 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: OutlinedButton.icon(
                          style: compact,
                          onPressed: _pickClient,
                          icon: const Icon(Icons.person_search_rounded, size: 20),
                          label: FittedBox(fit: BoxFit.scaleDown, child: Text(l.findClient, maxLines: 1)),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: OutlinedButton.icon(
                          style: compact,
                          onPressed: _newClient,
                          icon: const Icon(Icons.person_add_alt_1_rounded, size: 20),
                          label: FittedBox(fit: BoxFit.scaleDown, child: Text(l.newClient, maxLines: 1)),
                        ),
                      ),
                    ],
                  ),
                  field(l.client, _client),
                  field(l.companyLabel, _company),
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(child: field(l.phone, _phone, type: TextInputType.phone)),
                      const SizedBox(width: 10),
                      Expanded(child: field(l.innLabel, _inn, type: TextInputType.number, action: TextInputAction.done)),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                ActionChip(avatar: const Icon(Icons.add_rounded, size: 18), label: Text(l.addLine), onPressed: _pickProduct),
                ActionChip(avatar: const Icon(Icons.qr_code_scanner_rounded, size: 18), label: Text(l.scanProduct), onPressed: _scan),
                ActionChip(avatar: const Icon(Icons.shopping_cart_outlined, size: 18), label: Text(l.fromCart), onPressed: _fromCart),
                FilterChip(label: Text(l.useWholesale), selected: _wholesale, onSelected: _applyWholesale),
              ],
            ),
            const SizedBox(height: 8),
            if (_lines.isEmpty)
              Panel(
                child: Text(l.linesEmptyHint, textAlign: TextAlign.center, style: TextStyle(color: pal.textSoft)),
              ),
            // 14.10, владелец (снимок «Счёт на оплату»): единица «шт.» в узком поле переносилась по буквам, количество не
            // вводилось. Строка — название и ✕; ниже −/+ и количество с единицей (нажатие — ввести число), цена (нажатие —
            // ввести цену) и сумма строки.
            for (final x in _lines)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Panel(
                  key: ObjectKey(x),
                  padding: const EdgeInsets.fromLTRB(14, 8, 6, 10),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          Expanded(
                            child: Text(x.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                          ),
                          IconButton(tooltip: l.removeAction, icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _lines.remove(x))),
                        ],
                      ),
                      Row(
                        children: [
                          IconButton.outlined(onPressed: x.qty <= 1 ? null : () => setState(() => x.qty = x.qty - 1), icon: const Icon(Icons.remove)),
                          InkWell(
                            borderRadius: BorderRadius.circular(10),
                            onTap: () async {
                              final v = await askNumber(context, title: x.name, hint: x.unit, initial: x.qty);
                              if (v != null && v > 0) setState(() => x.qty = v);
                            },
                            child: Container(
                              constraints: const BoxConstraints(minWidth: 64),
                              height: 44,
                              alignment: Alignment.center,
                              padding: const EdgeInsets.symmetric(horizontal: 8),
                              child: Text('${qtyText(x.qty)} ${x.unit}', maxLines: 1, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                            ),
                          ),
                          IconButton.filled(
                            style: IconButton.styleFrom(backgroundColor: pal.action, foregroundColor: pal.onAction),
                            onPressed: () => setState(() => x.qty = x.qty + 1),
                            icon: const Icon(Icons.add),
                          ),
                          const Spacer(),
                          InkWell(
                            borderRadius: BorderRadius.circular(10),
                            onTap: () async {
                              final v = await askNumber(context, title: '${l.price} · ${x.name}', hint: l.som, initial: x.price);
                              if (v != null && v >= 0) setState(() => x.price = v);
                            },
                            child: Padding(
                              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.end,
                                children: [
                                  Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Text('× ${money(x.price)} =', style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                                      const SizedBox(width: 4),
                                      Icon(Icons.edit_outlined, size: 14, color: pal.textSoft),
                                    ],
                                  ),
                                  Text(
                                    '${money(x.sum)} ${l.som}',
                                    style: TextStyle(fontWeight: FontWeight.w800, color: pal.price),
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
            FieldLabel(l.discountShort, upper: false),
            TextField(
              controller: _discount,
              keyboardType: TextInputType.text,
              textInputAction: TextInputAction.done,
              decoration: InputDecoration(
                hintText: l.discountHint,
                suffixText: pct && _discountSum > 0 ? '−${money(_discountSum)} ${l.som}' : null,
              ),
              onChanged: (_) => setState(() {}),
            ),
            FieldLabel(widget.quote ? l.validUntil('') : l.payUntil(''), upper: false),
            OutlinedButton.icon(
              onPressed: () async {
                // 2026-10-21: у просроченного счёта срок в прошлом — календарь падал (дата раньше первой допустимой).
                final today = DateUtils.dateOnly(DateTime.now());
                final first = _until.isBefore(today) ? _until : today.subtract(const Duration(days: 1));
                final d = await showDatePicker(
                  context: context,
                  initialDate: _until,
                  firstDate: first,
                  lastDate: today.add(const Duration(days: 365)),
                );
                if (d != null) setState(() => _until = d);
              },
              icon: const Icon(Icons.event_rounded),
              label: Text(_d(_until)),
            ),
            const SizedBox(height: 12),
            if (_discountSum > 0) ...[
              _TotalRow(l.docSubtotal, '${money(_subtotal)} ${l.som}'),
              _TotalRow(l.discountShort, '−${money(_discountSum)} ${l.som}'),
            ],
            const SizedBox(height: 8),
            Text(l.quotesLocalNote, style: TextStyle(color: pal.textSoft, fontSize: 12)),
          ],
        ),
        // 2026-10-21: итог и «Сохранить» всегда видны внизу — одна главная кнопка экрана.
        bottomNavigationBar: SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(l.total, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                      FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: Alignment.centerLeft,
                        child: Text('${money(_total)} ${l.som}', style: TextStyle(fontSize: 22, fontWeight: FontWeight.w800, color: pal.price)),
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 10),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size(140, 54)),
                  onPressed: _save,
                  child: Text(l.save),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _TotalRow extends StatelessWidget {
  final String label;
  final String value;
  const _TotalRow(this.label, this.value);
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 1),
    child: Row(
      children: [
        Expanded(
          child: Text(label, style: TextStyle(color: context.pal.textSoft)),
        ),
        Text(value, style: const TextStyle(fontWeight: FontWeight.w700)),
      ],
    ),
  );
}

/// 2026-10-21: сканер камерой для КП/счёта — каждый скан добавляет товар (повтор того же кода в течение 1,5 с не считается).
class _QuoteScanSheet extends ConsumerStatefulWidget {
  final void Function(Product p, double qty) onProduct;
  const _QuoteScanSheet({required this.onProduct});
  @override
  ConsumerState<_QuoteScanSheet> createState() => _QuoteScanSheetState();
}

class _QuoteScanSheetState extends ConsumerState<_QuoteScanSheet> {
  final _camera = MobileScannerController(formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.itf14, BarcodeFormat.qrCode], detectionSpeed: DetectionSpeed.normal);
  String _last = '';
  DateTime _lastAt = DateTime(2000);
  String? _msg;
  bool _error = false;

  @override
  void dispose() {
    _camera.dispose();
    super.dispose();
  }

  Future<void> _onCode(String code) async {
    final now = DateTime.now();
    if (code == _last && now.difference(_lastAt).inMilliseconds < 1500) return;
    _last = code;
    _lastAt = now;
    final l = context.l;
    final hit = await ref.read(databaseProvider).productByScan(code);
    if (!mounted) return;
    if (hit == null) {
      setState(() {
        _msg = l.barcodeNotFound(code);
        _error = true;
      });
      return;
    }
    widget.onProduct(hit.product, hit.qty ?? 1);
    setState(() {
      _msg = l.scannedAdded(hit.product.name);
      _error = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(
              height: 260,
              child: ClipRRect(
                borderRadius: BorderRadius.circular(16),
                child: MobileScanner(
                  controller: _camera,
                  onDetect: (cap) {
                    final v = cap.barcodes.firstOrNull?.rawValue;
                    if (v != null && v.isNotEmpty) _onCode(v);
                  },
                ),
              ),
            ),
            const SizedBox(height: 10),
            Text(
              _msg ?? l.scanHint,
              textAlign: TextAlign.center,
              style: TextStyle(fontWeight: FontWeight.w700, color: _error ? T.warn : null),
            ),
            const SizedBox(height: 10),
            FilledButton(onPressed: () => Navigator.pop(context), child: Text(l.done)),
          ],
        ),
      ),
    );
  }
}

/// Ф4 + Ф6: ККМ и ЭСФ на телефоне. Связь кассы (очередь чеков без связи — настоящая), черновики ЭСФ продажи юрлицу.
/// Отправки в налоговую и входящих ЭСФ нет, пока сервер NurCRM не подключит ГНС — честно сказано на экране.
class EsfScreen extends ConsumerStatefulWidget {
  const EsfScreen({super.key});
  @override
  ConsumerState<EsfScreen> createState() => _EsfScreenState();
}

class _EsfScreenState extends ConsumerState<EsfScreen> {
  List<Map<String, dynamic>> _drafts = [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final raw = await ref.read(databaseProvider).getSetting('esf.drafts');
    if (!mounted) return;
    setState(() => _drafts = raw == null ? [] : (jsonDecode(raw) as List).map((e) => (e as Map).cast<String, dynamic>()).toList());
  }

  Future<void> _issue({Map<String, dynamic>? edit}) async {
    final l = context.l;
    final inn = TextEditingController(text: edit?['inn'] as String? ?? '');
    final name = TextEditingController(text: edit?['name'] as String? ?? '');
    final contract = TextEditingController(text: edit?['contract'] as String? ?? '');
    final sum = TextEditingController(text: edit?['sum']?.toString() ?? '');
    var cash = edit?['cash'] as bool? ?? false;
    final ok = await showModalBottomSheet<Object>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(16, 12, 16, MediaQuery.viewInsetsOf(c).bottom + 16),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.esfIssue, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                FieldLabel(l.esfBuyerInn),
                TextField(controller: inn, keyboardType: TextInputType.number),
                FieldLabel(l.esfBuyerName),
                TextField(controller: name),
                FieldLabel(l.esfPayForm),
                SegmentedButton<bool>(
                  segments: [
                    ButtonSegment(value: true, label: Text(l.esfCash)),
                    ButtonSegment(value: false, label: Text(l.esfNonCash)),
                  ],
                  selected: {cash},
                  onSelectionChanged: (s) => set(() => cash = s.first),
                ),
                FieldLabel(l.esfContract),
                TextField(controller: contract),
                FieldLabel(l.esfSum),
                TextField(controller: sum, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
                const SizedBox(height: 14),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                  onPressed: () => Navigator.pop(c, true),
                  child: Text(l.save),
                ),
                const SizedBox(height: 6),
                // 14.10, владелец: «ЭСФ не только в налоговую, но и в Excel/CSV»; неактивная кнопка в светлой теме была не видна.
                OutlinedButton.icon(onPressed: () => Navigator.pop(c, 'csv'), icon: const Icon(Icons.table_chart_outlined), label: Text(l.esfExportCsv)),
                const SizedBox(height: 6),
                OutlinedButton.icon(onPressed: () => toast(c, l.esfTaxSoon), icon: const Icon(Icons.account_balance_outlined), label: Text(l.esfSendTax)),
                SizedBox(height: MediaQuery.paddingOf(c).bottom),
              ],
            ),
          ),
        ),
      ),
    );
    if ((ok != true && ok != 'csv') || !mounted) return;
    final draft = {
      'id': edit?['id'] ?? DateTime.now().microsecondsSinceEpoch.toString(),
      'inn': inn.text.trim(),
      'name': name.text.trim(),
      'cash': cash,
      'contract': contract.text.trim(),
      'sum': parseNum(sum.text) ?? 0,
      'at': DateTime.now().toIso8601String(),
    };
    setState(() {
      _drafts.removeWhere((d) => d['id'] == draft['id']);
      _drafts.insert(0, draft);
    });
    await ref.read(databaseProvider).setSetting('esf.drafts', jsonEncode(_drafts));
    if (!mounted) return;
    toast(context, l.esfDraftSaved);
    if (ok == 'csv') await _exportCsv([draft]);
  }

  /// 14.10: ЭСФ в Excel — CSV с «;» и BOM (Excel открывает кириллицу и колонки сразу), отправка через «Поделиться».
  Future<void> _exportCsv(List<Map<String, dynamic>> rows) async {
    if (rows.isEmpty) return;
    final l = context.l;
    String q(Object? v) => '"${(v ?? '').toString().replaceAll('"', '""')}"';
    final b = StringBuffer('﻿')..writeln([l.dateLabel, l.esfBuyerInn, l.esfBuyerName, l.esfPayForm, l.esfContract, l.esfSum].map(q).join(';'));
    for (final d in rows) {
      final at = DateTime.tryParse(d['at'] as String? ?? '');
      final sum = ((d['sum'] as num?)?.toDouble() ?? 0).toStringAsFixed(2).replaceAll('.', ',');
      b.writeln(
        [
          at == null ? '' : '${at.day.toString().padLeft(2, '0')}.${at.month.toString().padLeft(2, '0')}.${at.year}',
          d['inn'],
          d['name'],
          (d['cash'] as bool? ?? false) ? l.esfCash : l.esfNonCash,
          d['contract'],
          sum,
        ].map(q).join(';'),
      );
    }
    final dir = await getTemporaryDirectory();
    final now = DateTime.now();
    final name = 'esf-${now.year}${now.month.toString().padLeft(2, '0')}${now.day.toString().padLeft(2, '0')}-${now.hour}${now.minute}.csv';
    final file = File('${dir.path}/$name');
    await file.writeAsString(b.toString());
    await SharePlus.instance.share(
      ShareParams(
        files: [XFile(file.path, mimeType: 'text/csv', name: name)],
        text: l.esfTitle,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final pending = ref.watch(pendingCountProvider).value ?? 0;
    final s = ref.watch(sessionProvider);
    return Scaffold(
      appBar: ScreenTitle(l.esfTitle),
      floatingActionButton: FloatingActionButton.extended(onPressed: () => _issue(), icon: const Icon(Icons.receipt_long_rounded), label: Text(l.esfIssue)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
        children: [
          Panel(
            color: pending > 0 ? pal.warnSoft : null,
            child: Row(
              children: [
                Icon(pending > 0 ? Icons.cloud_off_rounded : Icons.cloud_done_rounded, color: pending > 0 ? T.warn : T.success),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(s?.cashboxName ?? '—', style: const TextStyle(fontWeight: FontWeight.w800)),
                      Text(pending > 0 ? l.esfQueue(pending) : l.esfOnline, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                    ],
                  ),
                ),
              ],
            ),
          ),
          FieldLabel(l.esfDrafts),
          if (_drafts.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: OutlinedButton.icon(onPressed: () => _exportCsv(_drafts), icon: const Icon(Icons.table_chart_outlined), label: Text(l.esfExportAll)),
            ),
          if (_drafts.isEmpty) Text(l.nothingFound, style: TextStyle(color: pal.textSoft)),
          for (final d in _drafts)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Panel(
                onTap: () => _issue(edit: d),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(d['name'] as String? ?? '', style: const TextStyle(fontWeight: FontWeight.w700)),
                          Text('${l.innLabel} ${d['inn']} · ${l.stDraft}', style: TextStyle(color: pal.textSoft, fontSize: 12)),
                        ],
                      ),
                    ),
                    const SizedBox(width: 10),
                    Text(
                      money((d['sum'] as num?)?.toDouble() ?? 0),
                      style: TextStyle(fontWeight: FontWeight.w800, color: pal.price),
                    ),
                  ],
                ),
              ),
            ),
          FieldLabel(l.esfIncoming),
          Panel(
            child: Text(l.esfStub, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
          ),
        ],
      ),
    );
  }
}
