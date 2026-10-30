import 'dart:async';

import 'receipt_pdf.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'payment_qr.dart';
import '../sale/cart.dart';
import '../receipt/receipt.dart';
import '../receipt/receipt_view.dart';
import 'loyalty.dart';
import 'receipt_qr.dart';
import 'sales_repository.dart';
import '../../core/util/audit.dart';
import 'debt_access.dart';
import '../../core/print/printer_service.dart';
import '../profile/printer_settings.dart';
import '../ops/return_ledger.dart';
import '../../core/ai/assistant/ai_handles.dart';
import '../../core/ai/assistant/tool_registry.dart' show ToolStatus;

enum ReceiptTo { whatsapp, qr, none }

/// Оплата (Т4): наличные, карта, в долг; получено, сдача; чек покупателю.
class PaymentScreen extends ConsumerStatefulWidget {
  const PaymentScreen({super.key});
  @override
  ConsumerState<PaymentScreen> createState() => _PaymentScreenState();
}

class _PaymentScreenState extends ConsumerState<PaymentScreen> {
  PayMethod _method = PayMethod.cash;
  final _received = TextEditingController();
  ReceiptTo _receipt = ReceiptTo.none;
  Client? _client;
  bool _busy = false;

  /// 13.10: бонусы клиента — баланс, сколько списать, процент начисления магазина.
  double? _bonusBalance;
  bool _bonusEnabled = false;
  double _bonusPercent = 0;
  final _redeem = TextEditingController();

  /// 18.10: «Доступ к долгу» (правила владельца) — подсказка под «В долг» и проверка при оплате.
  List<DebtRule>? _debtRules;
  String _debtHint = '';

  /// 2026-10-22 (мобилка, деньги): срок долга «Вернуть до» — как окно оплаты кассы (по умолчанию 30 дней, как сервер).
  DateTime _debtDue = DateUtils.dateOnly(DateTime.now()).add(const Duration(days: 30));

  // 2026-10-26 (голос-управление, В3б): «ручка» для Нур Советника в звонке — заполняет поля экрана и нажимает ТУ ЖЕ `_pay`
  // (доступ к долгу, очередь, ключ операции, журнал возвратов — всё как при нажатии кнопки), итог докладывает `_aiSink`.
  final _aiSink = OutcomeSink();
  late final AiScreenHandles _aiHandles = ref.read(aiHandlesProvider);
  late final AiHandle _aiHandle = AiHandle(fill: _aiFill, run: () => _aiSink.run(_pay));

  @override
  void dispose() {
    _aiHandles.unregister('pay', _aiHandle);
    super.dispose();
  }

  /// Кнопка «Провести оплату» доступна: те же условия, что у кнопки (раньше выражение стояло в build).
  bool _canPayWith(Cart cart, double received) =>
      !_busy &&
      !cart.isEmpty &&
      switch (_method) {
        PayMethod.cash => _received.text.isEmpty || received + 0.004 >= cart.total,
        PayMethod.card => true,
        PayMethod.debt => _client != null && received <= cart.total,
      };

  /// Заполнить экран из просьбы: способ, клиент, сколько дали, срок долга. Клиент, выбранный на экране, остаётся.
  Future<HandleOutcome?> _aiFill(Map<String, dynamic> a) async {
    final base = ref.read(cartProvider);
    if (base.isEmpty) return HandleOutcome.clarifyKey('emptyForPay');
    final method = switch ('${a['method'] ?? 'cash'}') {
      'card' || 'qr' || 'transfer' => PayMethod.card,
      'debt' => PayMethod.debt,
      _ => PayMethod.cash,
    };
    final name = '${a['client'] ?? ''}'.trim();
    if (name.isNotEmpty && (_client == null || aiMatchByName([_client!], name, (c) => c.name).isEmpty)) {
      final List<Client> found;
      try {
        found = await ref.read(salesRepoProvider).clients(name);
      } on ApiException catch (e) {
        return HandleOutcome.error(e.message);
      }
      final digits = name.replaceAll(RegExp(r'\D'), '');
      final hits = digits.length >= 4
          ? found.where((c) => c.phone.replaceAll(RegExp(r'\D'), '').contains(digits)).toList()
          : aiMatchByName(found, name, (c) => c.name);
      if (hits.isEmpty) return HandleOutcome.notFoundKey('clientNone');
      if (hits.length > 1) {
        return HandleOutcome(ToolStatus.ambiguous, askKey: 'h.clientMany', askParams: {'list': hits.take(5).map((c) => c.display).join('; ')});
      }
      if (!mounted) return HandleOutcome.errorKey('h.screenNotReady');
      setState(() {
        _client = hits.first;
        _bonusBalance = null;
        _redeem.clear();
      });
      await _loadBonus();
    }
    DateTime? due;
    final dueText = '${a['due_date'] ?? ''}'.trim();
    if (method == PayMethod.debt && dueText.isNotEmpty) {
      due = DateTime.tryParse(dueText);
      if (due == null || !DateUtils.dateOnly(due).isAfter(DateUtils.dateOnly(DateTime.now()))) return HandleOutcome.clarifyKey('h.dueBad');
    }
    final given = a['cash_given'] is num ? (a['cash_given'] as num).toDouble() : parseNum('${a['cash_given'] ?? ''}');
    if (!mounted) return HandleOutcome.errorKey('h.screenNotReady');
    setState(() {
      _method = method;
      if (due != null) _debtDue = DateUtils.dateOnly(due);
      // без суммы: наличные — «ровно», в долг — ничего не внесено сейчас, карта — поле скрыто
      _received.text = method == PayMethod.card || given == null ? '' : given.toStringAsFixed(given.truncateToDouble() == given ? 0 : 2);
    });
    final cart = _payCart(base);
    final received = parseNum(_received.text) ?? 0;
    if (method == PayMethod.debt && _client == null) return HandleOutcome.clarifyKey('needDebtClient');
    if (method == PayMethod.cash && _received.text.isNotEmpty && received + 0.004 < cart.total) {
      return HandleOutcome.clarifyKey('h.payShort', {'given': received, 'total': cart.total, 'short': cart.total - received});
    }
    if (!_canPayWith(cart, received)) return HandleOutcome.clarifyKey('h.payNotReady');
    return null;
  }

  @override
  void initState() {
    super.initState();
    _aiHandles.register('pay', _aiHandle);
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      final repo = ref.read(debtAccessRepoProvider);
      final rules = await repo.load();
      if (!mounted) return;
      final hint = await repo.hint(context.l, ref.read(sessionProvider), rules);
      if (!mounted) return;
      setState(() {
        _debtRules = rules;
        _debtHint = hint;
      });
    });
  }

  double _redeemSum(Cart cart) {
    if (_client == null || !_bonusEnabled) return 0;
    final want = parseNum(_redeem.text) ?? 0;
    final balance = _bonusBalance ?? 0;
    final max = balance < cart.total ? balance : cart.total;
    final v = want.clamp(0, max).toDouble();
    return (v * 100).floorToDouble() / 100;
  }

  double _maxRedeem(Cart cart) {
    final balance = _bonusBalance ?? 0;
    return balance < cart.total ? balance : cart.total;
  }

  /// Чек к оплате: бонусы — как скидка суммой на чек вместе с обычной скидкой (сервер принимает одну сумму, как у кассы).
  Cart _payCart(Cart cart) {
    final r = _redeemSum(cart);
    if (r <= 0) return cart;
    return Cart(lines: cart.lines, orderDiscountSum: cart.orderDiscount + r);
  }

  Future<void> _loadBonus() async {
    final c = _client;
    if (c == null) return;
    final loyalty = ref.read(loyaltyRepoProvider);
    final (enabled, percent) = await loyalty.settings();
    double? balance;
    try {
      balance = await loyalty.balance(c.id);
    } on ApiException {
      balance = null;
    }
    if (!mounted || _client?.id != c.id) return;
    setState(() {
      _bonusEnabled = enabled && balance != null;
      _bonusPercent = percent;
      _bonusBalance = balance;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final baseCart = ref.watch(cartProvider);
    final cart = _payCart(baseCart);
    final redeem = _redeemSum(baseCart);
    final total = cart.total;
    final received = parseNum(_received.text) ?? 0;
    final pal = context.pal;
    // 2026-10-26 (живой аудит, П4, В-8): в альбомной ориентации (высота < 500 dp) закреплённая кнопка съедала полэкрана —
    // тогда «Провести оплату» стоит в конце прокручиваемого экрана, а не внизу
    final compact = MediaQuery.sizeOf(context).height < 500;
    final canPay = _canPayWith(cart, received);
    final payButton = FilledButton(
      onPressed: canPay ? _pay : null,
      child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5, color: T.navy)) : Text(l.doPay),
    );
    return Scaffold(
      appBar: ScreenTitle(l.payment),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
        children: [
          Container(
            padding: EdgeInsets.symmetric(vertical: compact ? 10 : 20),
            // 2026-10-26 (живой аудит, П1/П4, С-7): итог оплаты в тёмной теме — светлее фона и с рамкой, а не сливается с ним
            decoration: BoxDecoration(color: pal.heroBg, borderRadius: BorderRadius.circular(18), border: Border.all(color: pal.heroBorder)),
            child: Column(
              children: [
                Text(l.toPayPositions(cart.count), style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12.5)),
                const SizedBox(height: 4),
                Text(
                  '${money(total)} ${l.som}',
                  style: const TextStyle(color: T.accent, fontSize: 34, fontWeight: FontWeight.w800),
                ),
              ],
            ),
          ),
          const SizedBox(height: 16),
          FieldLabel(l.payMethod),
          Row(
            children: [
              for (final (m, icon, label) in [
                (PayMethod.cash, Icons.payments_outlined, l.cash),
                (PayMethod.card, Icons.credit_card_rounded, l.card),
                (PayMethod.debt, Icons.person_outline_rounded, l.debt),
              ])
                Expanded(
                  child: Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: _Choice(icon: icon, label: label, selected: _method == m, onTap: () => setState(() => _method = m)),
                  ),
                ),
            ],
          ),
          if (_method == PayMethod.debt && _debtHint.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(_debtHint, style: TextStyle(fontSize: 12.5, color: pal.warn, fontWeight: FontWeight.w600)),
            ),
          // 13.10, владелец: клиент — при любой оплате (бонусы, история); для «в долг» обязателен.
          const SizedBox(height: 14),
          FieldLabel(_method == PayMethod.debt ? l.client : l.clientOptional),
          Panel(
            onTap: _pickClient,
            child: Row(
              children: [
                const Icon(Icons.person_search_outlined),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        _client?.display ?? l.chooseClient,
                        style: TextStyle(fontWeight: FontWeight.w600, color: _client == null ? pal.textSoft : null),
                      ),
                      // 2026-10-26 (живой аудит, П4, С-4): у клиента виден его текущий долг
                      if (_client != null && _client!.debt > 0)
                        Text(
                          l.clientDebtLine(money(_client!.debt)),
                          style: TextStyle(fontSize: 12.5, color: pal.danger, fontWeight: FontWeight.w700),
                        ),
                      if (_client != null && _bonusBalance != null)
                        Text(
                          '${l.bonusBalance}: ${money(_bonusBalance!)}',
                          style: TextStyle(fontSize: 12.5, color: pal.success, fontWeight: FontWeight.w700),
                        ),
                    ],
                  ),
                ),
                if (_client != null)
                  IconButton(
                    tooltip: l.removeClient,
                    onPressed: () => setState(() {
                      _client = null;
                      _bonusBalance = null;
                      _redeem.clear();
                    }),
                    icon: const Icon(Icons.close_rounded),
                  )
                else
                  const Icon(Icons.chevron_right_rounded),
              ],
            ),
          ),
          // 2026-10-22 (мобилка, деньги): срок долга «Вернуть до» — печатается в чеке и записывается в сделку долга.
          if (_method == PayMethod.debt) ...[
            const SizedBox(height: 10),
            FieldLabel(l.debtDueLabel),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final days in const [7, 14, 30])
                  ChoiceChip(
                    label: Text(l.debtDueDays(days)),
                    selected: _debtDue == DateUtils.dateOnly(DateTime.now()).add(Duration(days: days)),
                    onSelected: (_) => setState(() => _debtDue = DateUtils.dateOnly(DateTime.now()).add(Duration(days: days))),
                  ),
                ActionChip(
                  avatar: const Icon(Icons.event_outlined, size: 18),
                  label: Text(
                    '${_debtDue.day.toString().padLeft(2, '0')}.${_debtDue.month.toString().padLeft(2, '0')}.${_debtDue.year}',
                  ),
                  onPressed: () async {
                    final today = DateUtils.dateOnly(DateTime.now());
                    final d = await showDatePicker(
                      context: context,
                      initialDate: _debtDue,
                      firstDate: today.add(const Duration(days: 1)),
                      lastDate: today.add(const Duration(days: 730)),
                    );
                    if (d != null && mounted) setState(() => _debtDue = DateUtils.dateOnly(d));
                  },
                ),
              ],
            ),
          ],
          // Бонусы: списать часть или максимум (не больше баланса и не больше чека), предпросмотр начисления.
          if (_client != null && _bonusEnabled && (_bonusBalance ?? 0) > 0) ...[
            const SizedBox(height: 10),
            FieldLabel(l.bonusRedeem),
            TextField(
              controller: _redeem,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(hintText: '0', suffixText: l.som),
            ),
            const SizedBox(height: 6),
            Wrap(
              spacing: 6,
              children: [
                ActionChip(label: Text(l.bonusNone), onPressed: () => setState(() => _redeem.clear())),
                ActionChip(
                  label: Text(l.bonusAll(money(_maxRedeem(baseCart)))),
                  onPressed: () => setState(() => _redeem.text = _maxRedeem(baseCart).toStringAsFixed(2)),
                ),
              ],
            ),
            if (redeem > 0)
              Text(
                l.bonusPaid(money(redeem)),
                style: TextStyle(color: pal.success, fontWeight: FontWeight.w700),
              ),
          ],
          if (_client != null && _bonusEnabled && _bonusPercent > 0)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(l.bonusEarn(money((total * _bonusPercent / 100 * 100).roundToDouble() / 100)), style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
            ),
          // 14.10, владелец: оплата переводом — QR банка с суммой чека (2–5 банков на выбор, какие назначил владелец).
          if (_method == PayMethod.card) PayQrPanel(amount: total),
          if (_method != PayMethod.card) ...[
            const SizedBox(height: 14),
            FieldLabel(_method == PayMethod.debt ? l.receivedNowDebt : l.received),
            TextField(
              controller: _received,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              onChanged: (_) => setState(() {}),
              style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800),
              decoration: InputDecoration(
                hintText: _method == PayMethod.debt ? '0' : money(total),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(14),
                  borderSide: BorderSide(color: pal.dark ? T.accent : T.navy, width: 2),
                ),
              ),
            ),
            // 2026-10-26 (живой аудит, П4, С-4): в долг — сразу видно, сколько останется должен клиент после внесённого
            if (_method == PayMethod.debt)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  l.payDebtLeft('${money(roundMoney((total - received).clamp(0, double.infinity).toDouble()))} ${l.som}'),
                  key: const ValueKey('debt-left'),
                  style: TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: pal.warn),
                ),
              ),
            if (_method == PayMethod.cash) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (final v in cashSuggestions(total))
                    ActionChip(
                      label: Text(v == total ? l.exact : money(v)),
                      onPressed: () => setState(() => _received.text = v.toStringAsFixed(v.truncateToDouble() == v ? 0 : 2)),
                    ),
                ],
              ),
              const SizedBox(height: 14),
              Row(
                children: [
                  Text(l.change, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                  const Spacer(),
                  Text(
                    '${money(change(received, total))} ${l.som}',
                    // 2026-10-26 (живой аудит, П1/П4): «0 сом» сдачи не зелёный (не «успех»); цвета — из палитры темы
                    style: TextStyle(fontSize: 24, fontWeight: FontWeight.w800, color: change(received, total) > 0 ? pal.success : pal.textSoft),
                  ),
                ],
              ),
              if (_received.text.isNotEmpty && received + 0.004 < total)
                Text(l.notEnough(money(total - received)), style: TextStyle(color: pal.danger, fontSize: 12.5)),
            ],
          ],
          const SizedBox(height: 16),
          FieldLabel(l.receiptToCustomer),
          Wrap(
            spacing: 6,
            children: [
              for (final (r, label) in [(ReceiptTo.whatsapp, 'PDF · WhatsApp'), (ReceiptTo.qr, l.receiptQr), (ReceiptTo.none, l.notNeeded)])
                ChoiceChip(
                  label: Text(label),
                  selected: _receipt == r,
                  labelStyle: TextStyle(color: _receipt == r ? Colors.white : pal.text, fontWeight: FontWeight.w600),
                  onSelected: (_) => setState(() => _receipt = r),
                ),
            ],
          ),
          if (compact) ...[
            const SizedBox(height: 16),
            payButton,
            SizedBox(height: MediaQuery.paddingOf(context).bottom),
          ],
        ],
      ),
      bottomNavigationBar: compact
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
                child: payButton,
              ),
            ),
    );
  }

  Future<void> _pickClient() async {
    final c = await showModalBottomSheet<Client>(context: context, isScrollControlled: true, useSafeArea: true, builder: (_) => const _ClientPicker());
    if (c != null) {
      setState(() {
        _client = c;
        _bonusBalance = null;
        _redeem.clear();
      });
      await _loadBonus();
    }
  }

  Future<void> _pay() async {
    // 2026-10-22 (мобилка, деньги, К-2): занятость — сразу, до первой паузы (проверка «Доступа к долгу» ждёт базу):
    // второе нажатие раньше успевало начать вторую оплату.
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await _payCore();
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// 2026-10-29 (аудит мобилки, п. 8): «Цена изменилась: A → B» — цены строк чека обновлены по серверу, продажа не проведена;
  /// кассир видит новую сумму и нажимает «Оплатить» ещё раз.
  Future<void> _showPriceChanged(List<PriceChange> changes) async {
    final l = context.l;
    ref.read(cartProvider.notifier).applyPriceChanges(changes);
    final lines = [for (final c in changes.take(5)) l.priceChangedLine(c.name, money(c.oldPrice), money(c.newPrice))];
    if (changes.length > 5) lines.add('+${changes.length - 5}');
    final text = [...lines, l.priceChangedNote].join('\n');
    _aiSink.report(HandleOutcome.error('${l.priceChangedTitle}: ${lines.join('; ')}'));
    if (!mounted) return;
    await showDialog<void>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.priceChangedTitle),
        content: Text(text),
        actions: [FilledButton(onPressed: () => Navigator.pop(c), child: Text(l.done))],
      ),
    );
  }

  Future<void> _payCore() async {
    final l = context.l;
    final messenger = ScaffoldMessenger.maybeOf(context);
    final baseCart = ref.read(cartProvider);
    final redeem = _redeemSum(baseCart);
    final cart = _payCart(baseCart);
    final shift = ref.read(shiftProvider).shift;
    if (shift == null) {
      toast(context, l.openShiftFirst, error: true);
      _aiSink.report(HandleOutcome.errorKey('noShift'));
      return;
    }
    final method = _method;
    final client = _client;
    final received = parseNum(_received.text);
    final debtDue = method == PayMethod.debt ? _debtDue : null;
    // 18.10: «Доступ к долгу» — запрет или лимит на сумму, которая уходит в долг (итог минус внесённое сейчас).
    final debtAmount = method == PayMethod.debt ? roundMoney(cart.total - (received ?? 0)) : 0.0;
    if (method == PayMethod.debt) {
      final repo = ref.read(debtAccessRepoProvider);
      final problem = await repo.check(l, ref.read(sessionProvider), _debtRules ?? await repo.load(), debtAmount);
      if (problem != null) {
        if (mounted) toast(context, problem, error: true);
        _aiSink.report(HandleOutcome(ToolStatus.denied, ask: problem));
        return;
      }
    }
    final sales = ref.read(salesRepoProvider);
    final payReceived = method == PayMethod.card ? null : (received ?? (method == PayMethod.cash ? cart.total : null));
    // 2026-10-29 (аудит мобилки, п. 8): перед оплатой сверяем цены чека с сервером, как касса («Цена изменилась: 205 → 195»).
    // Если прошлая попытка ЭТОГО чека без ответа (ключ жив) — не сверяем: повтор должен уйти тем же телом (п. 9).
    if (!await sales.hasLiveAttempt(cart: cart, shiftId: shift.id, method: method, received: payReceived, client: client)) {
      final check = await sales.checkPrices(baseCart);
      if (check.changes.isNotEmpty) {
        await _showPriceChanged(check.changes);
        return;
      }
      // Без связи сверить не с чем — продаём по цене телефона, но кассир предупреждён.
      if (check.offline && mounted) toast(context, l.priceMaybeStale);
    }
    final CheckoutResult result;
    try {
      result = await sales.checkout(
        cart: cart,
        shiftId: shift.id,
        method: method,
        received: payReceived,
        client: client,
        // Бонусы без сервера не списать — такая продажа только при связи (в очередь не встаёт).
        requireOnline: redeem > 0,
      );
    } on PriceChangedException catch (e) {
      // Сервер отказал: цена устарела, продажа НЕ проведена. Цены строк — из ответа сервера, нет их — запрашиваем карточки.
      final changes = e.changes.isNotEmpty ? e.changes : (await sales.checkPrices(baseCart)).changes;
      await _showPriceChanged(changes);
      return;
    } on CheckoutUnknownException {
      // Сервер не ответил — продажа могла пройти. Ключ сохранён: повтор той же оплаты второй раз не проведётся.
      if (mounted) toast(context, l.payOutcomeUnknown, error: true);
      _aiSink.report(const HandleOutcome(ToolStatus.error, unknown: true));
      return;
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      _aiSink.report(HandleOutcome.error(e.message));
      return;
    } catch (e) {
      if (mounted) toast(context, l.payOutcomeUnknown, error: true);
      _aiSink.report(const HandleOutcome(ToolStatus.error, unknown: true));
      return;
    }
    // Чек уже проведён (или в очереди) — корзину очищаем в любом случае, даже если экран успели закрыть;
    // дальше ошибки (бонусы, срок долга, журнал) не прячут экран «Оплачено».
    ref.read(cartProvider.notifier).clear();
    // 2026-10-29 (аудит мобилки, п. 1): сервер уже имел продажу с этим ключом — «проведено, сверьте», а не ошибка.
    if (result.reconcile && mounted) toast(context, ApiText.saleAlreadyPosted);
    final saleId = result.response == null ? null : str(result.response, 'id');
    try {
      // 14.10: журнал аудита.
      Audit.log(
        'sale',
        '${l.auditSale} ${result.number ?? ''} · ${method.api}${client == null ? '' : ' · ${client.display}'}${result.queued ? ' · offline' : ''}',
        amount: cart.total,
      );
      // 2026-10-22 (мобилка, деньги, К-3): как заплатили — для расчёта будущего возврата (доля скидки на чек, долг).
      if (saleId != null) {
        final prepaid = method == PayMethod.debt ? (received ?? 0) : cart.total;
        await ReturnLedger(ref.read(databaseProvider)).rememberPayment(
          saleId: saleId,
          total: result.total,
          method: method.api,
          paidCash: method == PayMethod.card ? 0 : (prepaid < result.total ? prepaid : result.total),
          paidNonCash: method == PayMethod.card ? result.total : 0,
          linesTotal: roundMoney(cart.subtotal - cart.lineDiscounts),
        );
      }
      // 13.10: бонусы клиента на сервере — сначала списание, потом начисление, с id продажи (как у кассы).
      // 2026-10-22: только у проведённой продажи (в очереди нет id продажи — начисление не к чему привязать).
      if (client != null && _bonusEnabled && !result.queued) {
        final earned = _bonusPercent > 0 ? (cart.total * _bonusPercent / 100 * 100).roundToDouble() / 100 : 0.0;
        final loyalty = ref.read(loyaltyRepoProvider);
        try {
          if (redeem > 0) await loyalty.post(client.id, -redeem, 'redeem', saleId);
          if (earned > 0) await loyalty.post(client.id, earned, 'earn', saleId);
        } on ApiException catch (e) {
          if (mounted) toast(context, e.message, error: true);
        }
      }
      if (method == PayMethod.debt) {
        await ref.read(debtAccessRepoProvider).recordSale(ref.read(sessionProvider)?.userId ?? '', debtAmount);
        // 2026-10-22 (мобилка, деньги): срок «Вернуть до» — в сделку долга (фоном, оплату не держит).
        if (client != null && saleId != null && debtDue != null && debtAmount > 0) {
          final sales = ref.read(salesRepoProvider);
          final warn = l.debtDueNotSaved;
          unawaited(
            sales.setDebtDue(clientId: client.id, saleId: saleId, due: debtDue).then((ok) {
              if (!ok) messenger?.showSnackBar(SnackBar(content: Text(warn)));
            }),
          );
        }
      }
    } catch (_) {
      // продажа проведена — второстепенное не мешает показать «Оплачено»
    }
    ref.read(shiftProvider.notifier).refresh();
    // 2026-10-26 (голос-управление, В3б): чек проведён (или в очереди) — докладываем итог ручке ДО перехода на «Оплачено»
    _aiSink.report(
      HandleOutcome.ok({
        'total': result.total,
        'change': method == PayMethod.cash ? change(received ?? cart.total, cart.total) : 0.0,
        'number': result.number ?? '',
        'sale_id': saleId ?? '',
        'queued': result.queued,
      }),
    );
    if (!mounted) return;
    final receipt = Receipt.fromCart(
      session: ref.read(sessionProvider),
      cart: cart,
      number: result.number,
      payment: method.api,
      received: method == PayMethod.card ? null : (method == PayMethod.cash ? (received ?? cart.total) : (received ?? 0)),
      client: client?.display ?? '',
      response: result.response,
      offline: result.queued,
      debtDue: debtDue,
    );
    final sharePdf = _receipt == ReceiptTo.whatsapp;
    // 2026-10-22: сначала «Оплачено», потом PDF — ошибка отправки PDF больше не прячет сдачу.
    context.pushReplacement(
      '/paid',
      extra: PaidInfo(result, method == PayMethod.cash ? change(received ?? cart.total, cart.total) : 0, receipt, showQr: _receipt == ReceiptTo.qr),
    );
    // 13.10, владелец: чек клиенту — PDF-файлом (WhatsApp, Telegram…), а не текстом.
    if (sharePdf) unawaited(shareReceiptPdf(receipt).catchError((_) {}));
  }
}

class PaidInfo {
  final CheckoutResult result;
  final double change;
  final Receipt receipt;

  /// Кассир выбрал «QR-код» — экран чека сразу показывает QR покупателю.
  final bool showQr;
  const PaidInfo(this.result, this.change, this.receipt, {this.showQr = false});
}

/// Чек проведён (или сохранён без связи — уйдёт на сервер сам).
class PaidScreen extends StatefulWidget {
  final PaidInfo info;
  const PaidScreen({super.key, required this.info});
  @override
  State<PaidScreen> createState() => _PaidScreenState();
}

class _PaidScreenState extends State<PaidScreen> {
  PaidInfo get info => widget.info;

  @override
  void initState() {
    super.initState();
    if (info.showQr) WidgetsBinding.instance.addPostFrameCallback((_) => _showQr());
    // 2026-10-20: печать чека сразу после оплаты, если включено в «Принтер чеков».
    WidgetsBinding.instance.addPostFrameCallback((_) => _autoPrint());
  }

  Future<void> _autoPrint() async {
    final service = ProviderScope.containerOf(context, listen: false).read(printerProvider);
    final c = await service.load();
    if (!c.ready || !mounted) return;
    // 2026-10-22 (мобилка, админ): П6 — после продажи за наличные ящик открывается (с чеком или без автопечати).
    if (c.autoPrint) {
      await printReceiptUi(context, info.receipt, afterSale: true);
    } else {
      try {
        await service.drawerAfterSale(info.receipt, c);
      } catch (_) {
        // ящик не открылся — продажа уже проведена
      }
    }
  }

  // 2026-10-26 (живой аудит, П4, Н-3): номер чека везде как на кассе — с нулями («№001601»), а не «№1601» в одном месте
  // и «№001601» в другом
  String get _checkTitle => '${context.l.check} №${info.receipt.numberText}';

  void _showQr() => ReceiptQrScreen.show(
    context,
    receipt: info.receipt,
    title: '$_checkTitle · ${money(info.result.total)} ${context.l.som}',
  );

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final queued = info.result.queued;
    // 2026-10-26 (живой аудит, П4, В-8): экран «Оплачено» прокручивается — в альбомной ориентации кнопки не уходят за край
    return Scaffold(
      body: SafeArea(
        child: CustomScrollView(
          slivers: [
            SliverFillRemaining(
              hasScrollBody: false,
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: Column(
                  children: [
                    const Spacer(),
                    TweenAnimationBuilder<double>(
                      tween: Tween(begin: 0, end: 1),
                      duration: const Duration(milliseconds: 700),
                      curve: Curves.elasticOut,
                      builder: (c, t, child) => Transform.scale(scale: t, child: child),
                      child: CircleAvatar(
                        radius: 44,
                        backgroundColor: queued ? pal.warnSoft : pal.successSoft,
                        child: Icon(queued ? Icons.cloud_upload_outlined : Icons.check_rounded, size: 48, color: queued ? pal.warn : pal.success),
                      ),
                    ),
                    const SizedBox(height: 18),
                    Text(
                      queued ? l.savedOffline : l.paidOk,
                      textAlign: TextAlign.center,
                      style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800),
                    ),
                    const SizedBox(height: 6),
                    Text(
                      queued ? l.savedOfflineHint : '$_checkTitle · ${money(info.result.total)} ${l.som}',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: pal.textSoft),
                    ),
                    if (info.change > 0) ...[
                      const SizedBox(height: 18),
                      Text(
                        '${l.change}: ${money(info.change)} ${l.som}',
                        textAlign: TextAlign.center,
                        style: TextStyle(fontSize: 26, fontWeight: FontWeight.w800, color: pal.success),
                      ),
                    ],
                    // 2026-10-27 (ревью М-6): остаток после продажи штуки из упаковки не сошёлся с сервером — жёлтая плашка
                    if (info.result.stockWarnings.isNotEmpty) ...[
                      const SizedBox(height: 14),
                      Container(
                        key: const Key('packStockWarn'),
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(12)),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Icon(Icons.warning_amber_rounded, color: pal.warn),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text(
                                l.pieceStockMismatch(info.result.stockWarnings.map((w) => '${w.name} (${qtyText(w.expected)} → ${qtyText(w.actual)})').join('; ')),
                                style: TextStyle(color: pal.warn, fontWeight: FontWeight.w700, fontSize: 13),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ],
                    const Spacer(),
                    const SizedBox(height: 16),
                    // 13.10: чек целиком — как бумажная лента (ReceiptScreen).
                    OutlinedButton.icon(
                      style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                      onPressed: () => ReceiptScreen.show(context, info.receipt),
                      icon: const Icon(Icons.receipt_long_outlined),
                      label: Text(l.showReceipt, maxLines: 1, overflow: TextOverflow.ellipsis),
                    ),
                    const SizedBox(height: 10),
                    // 2026-10-20: печать на принтер чеков (Bluetooth / сеть / USB); 2026-10-26: «Печать», «QR», «PDF» — короткие подписи
                    ReceiptActionBar(receipt: info.receipt, qrTitle: '$_checkTitle · ${money(info.result.total)} ${l.som}', afterSale: true),
                    const SizedBox(height: 10),
                    FilledButton(onPressed: () => context.go('/sale'), child: Text(l.newCheck)),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _Choice extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;
  const _Choice({required this.icon, required this.label, required this.selected, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
    // 14.10, обход мобилки: в тёмной теме выбранный способ (тёмно-синий на тёмном) почти не отличался — жёлтая рамка.
    color: selected ? (context.pal.dark ? context.pal.accentSoft : T.navy) : context.pal.panel,
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(12),
      side: BorderSide(color: selected ? (context.pal.dark ? T.accent : T.navy) : context.pal.border, width: selected ? 2 : 1),
    ),
    child: InkWell(
      borderRadius: BorderRadius.circular(12),
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Column(
          children: [
            Icon(icon, color: selected ? T.accent : null),
            const SizedBox(height: 4),
            Text(
              label,
              style: TextStyle(fontWeight: FontWeight.w700, fontSize: 12.5, color: selected ? Colors.white : null),
            ),
          ],
        ),
      ),
    ),
  );
}

/// Выбор клиента для продажи в долг: поиск по имени и телефону, новый клиент.
class _ClientPicker extends ConsumerStatefulWidget {
  /// 2026-10-21: для КП и счёта — в «Новом клиенте» ещё компания и ИНН.
  final bool requisites;
  const _ClientPicker({this.requisites = false});
  @override
  ConsumerState<_ClientPicker> createState() => _ClientPickerState();
}

class _ClientPickerState extends ConsumerState<_ClientPicker> {
  final _q = TextEditingController();
  List<Client> _list = [];
  bool _loading = false;
  String? _error;

  /// 2026-10-26 (живой аудит, П4, С-4): долги клиентов (clientId → сумма) — в списке видно, кто уже должен.
  /// Только для продажи; в КП и счетах долги не нужны. Не загрузились (нет связи/прав) — список без долгов.
  Map<String, double> _debts = const {};

  @override
  void initState() {
    super.initState();
    _search();
    if (!widget.requisites) _loadDebts();
  }

  Future<void> _loadDebts() async {
    try {
      final rows = await ref.read(opsRepoProvider).debtors();
      if (mounted) setState(() => _debts = {for (final d in rows) d.clientId: d.debt});
    } catch (_) {
      // долги не показываем
    }
  }

  Future<void> _search() async {
    setState(() => _loading = true);
    try {
      final r = await ref.read(salesRepoProvider).clients(_q.text);
      if (mounted) setState(() => _list = r);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _create() async {
    // 2026-10-21: общее окно «Новый клиент» (см. createClientDialog) — то же для продажи в долг и для КП.
    final c = await createClientDialog(context, initial: _q.text, requisites: widget.requisites);
    if (c != null && mounted) Navigator.pop(context, c);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final media = MediaQuery.of(context);
    // высота: три четверти экрана, но не выше свободного места над клавиатурой и под строкой состояния
    final maxH = media.size.height - media.padding.top - media.viewInsets.bottom - 8;
    final height = maxH < media.size.height * .75 ? maxH : media.size.height * .75;
    return Padding(
      padding: EdgeInsets.only(bottom: media.viewInsets.bottom),
      child: SizedBox(
        height: height,
        child: Column(
          children: [
            // 2026-10-26 (живой аудит, П4, С-4): заголовок «Клиент» и «Закрыть» — окно можно закрыть, не выбирая
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 8, 0),
              child: Row(
                children: [
                  Expanded(child: Text(l.client, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18))),
                  TextButton(
                    style: TextButton.styleFrom(minimumSize: const Size(64, 48), foregroundColor: pal.text),
                    onPressed: () => Navigator.pop(context),
                    child: Text(l.closeBtn),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _q,
                      autofocus: true,
                      onSubmitted: (_) => _search(),
                      decoration: InputDecoration(hintText: l.clientSearch, prefixIcon: const Icon(Icons.search_rounded)),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton.filled(
                    tooltip: l.newClient,
                    style: IconButton.styleFrom(backgroundColor: T.navy, fixedSize: const Size(48, 48)),
                    onPressed: _create,
                    icon: const Icon(Icons.person_add_alt_1, color: T.accent),
                  ),
                ],
              ),
            ),
            if (_loading) const LinearProgressIndicator(),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.all(12),
                child: Text(_error!, style: TextStyle(color: pal.danger)),
              ),
            // 2026-10-21, владелец: «КП — создание нового контакта» — кнопка «+» без подписи была незаметна. Если клиента нет
            // в списке — строка «Новый клиент «…»» прямо в результатах поиска.
            if (!_loading && _list.isEmpty)
              ListTile(
                leading: const Icon(Icons.person_add_alt_1),
                title: Text(_q.text.trim().isEmpty ? l.newClient : '${l.newClient} «${_q.text.trim()}»', style: const TextStyle(fontWeight: FontWeight.w700)),
                subtitle: Text(l.nothingFound),
                onTap: _create,
              ),
            Expanded(
              child: ListView.builder(
                itemCount: _list.length,
                itemBuilder: (c, i) {
                  final cl = _list[i];
                  final debt = _debts[cl.id] ?? 0;
                  final info = [cl.phone, cl.company, if (cl.inn.isNotEmpty) '${l.innLabel} ${cl.inn}'].where((x) => x.isNotEmpty).join(' · ');
                  return ListTile(
                    title: Text(cl.name, style: const TextStyle(fontWeight: FontWeight.w600)),
                    subtitle: info.isEmpty && debt <= 0
                        ? null
                        : Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              if (info.isNotEmpty) Text(info),
                              if (debt > 0) Text(l.clientDebtLine(money(debt)), style: TextStyle(color: pal.danger, fontWeight: FontWeight.w700)),
                            ],
                          ),
                    onTap: () => Navigator.pop(context, debt > 0 ? cl.withDebt(debt) : cl),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// 13.10: выбор клиента из NurCRM — тот же список, что при оплате (для КП и счетов).
Future<Client?> pickClient(BuildContext context, {bool requisites = false}) =>
    showModalBottomSheet<Client>(context: context, isScrollControlled: true, useSafeArea: true, builder: (_) => _ClientPicker(requisites: requisites));

/// 2026-10-21, владелец: «КП — создание нового контакта». Окно «Новый клиент»: имя, телефон, для КП/счёта — компания и ИНН.
/// Сохраняет в NurCRM (POST api/main/clients/) и возвращает клиента — экран выбирает его сразу. Клиент с тем же телефоном
/// уже есть — берём его, без дубля. Нет связи: при [requisites] клиент возвращается без id (записан только в документ),
/// иначе — ошибка и null. Повторное нажатие «Сохранить», пока идёт запрос, ничего не делает.
Future<Client?> createClientDialog(BuildContext context, {String initial = '', bool requisites = false}) =>
    showDialog<Client>(context: context, builder: (_) => _NewClientDialog(initial: initial, requisites: requisites));

String _digits(String s) => s.replaceAll(RegExp(r'\D'), '');

/// Тот же ли телефон: последние 9 цифр («0700 123 456» = «+996 700 123 456»).
bool samePhone(String a, String b) {
  final x = _digits(a), y = _digits(b);
  if (x.length < 6 || y.length < 6) return false;
  String tail(String s) => s.length > 9 ? s.substring(s.length - 9) : s;
  return tail(x) == tail(y);
}

class _NewClientDialog extends ConsumerStatefulWidget {
  final String initial;
  final bool requisites;
  const _NewClientDialog({this.initial = '', this.requisites = false});
  @override
  ConsumerState<_NewClientDialog> createState() => _NewClientDialogState();
}

class _NewClientDialogState extends ConsumerState<_NewClientDialog> {
  // В строку поиска часто вводят телефон — тогда он идёт в поле «Телефон», иначе в «Имя».
  late final bool _initialIsPhone = RegExp(r'^[\d\s+()-]{6,}$').hasMatch(widget.initial.trim());
  late final _name = TextEditingController(text: _initialIsPhone ? '' : widget.initial.trim());
  late final _phone = TextEditingController(text: _initialIsPhone ? widget.initial.trim() : '');
  final _company = TextEditingController();
  final _inn = TextEditingController();
  bool _busy = false;
  String? _error;

  Future<void> _save() async {
    if (_busy) return;
    final l = context.l;
    final name = _name.text.trim().isNotEmpty ? _name.text.trim() : _company.text.trim();
    if (name.isEmpty) {
      setState(() => _error = l.clientNameRequired);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    final repo = ref.read(salesRepoProvider);
    final phone = _phone.text.trim();
    try {
      final digits = _digits(phone);
      if (digits.length >= 6) {
        // Поиск по последним 9 цифрам: в NurCRM номер мог быть записан как «0555…» или «+996 555…».
        final tail = digits.length > 9 ? digits.substring(digits.length - 9) : digits;
        final same = (await repo.clients(tail)).where((c) => samePhone(c.phone, phone)).firstOrNull;
        if (same != null) {
          if (mounted) {
            toast(context, l.clientExists);
            Navigator.pop(context, same);
          }
          return;
        }
      }
      final c = await repo.createClient(name, phone, company: _company.text, inn: _inn.text);
      if (mounted) {
        toast(context, l.clientSaved);
        Navigator.pop(context, c);
      }
    } on ApiException catch (e) {
      if (!mounted) return;
      if (widget.requisites) {
        toast(context, l.clientLocalOnly, error: true);
        Navigator.pop(context, Client('', name, phone, company: _company.text.trim(), inn: _inn.text.trim()));
      } else {
        setState(() {
          _busy = false;
          _error = e.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final innBad = _inn.text.trim().isNotEmpty && !RegExp(r'^\d{10,14}$').hasMatch(_inn.text.trim());
    // Правило редизайна 4: жёлтый текст на светлом не читается — «Отмена» и подпись поля тёмные (только в этом окне).
    final pal = context.pal;
    final theme = Theme.of(context);
    return Theme(
      data: theme.copyWith(
        textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(foregroundColor: pal.text)),
        inputDecorationTheme: theme.inputDecorationTheme.copyWith(floatingLabelStyle: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w600)),
      ),
      child: _dialog(context, innBad),
    );
  }

  Widget _dialog(BuildContext context, bool innBad) {
    final l = context.l;
    return AlertDialog(
      title: Text(l.newClient),
      scrollable: true,
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TextField(
            controller: _name,
            autofocus: true,
            textCapitalization: TextCapitalization.words,
            textInputAction: TextInputAction.next,
            decoration: InputDecoration(labelText: l.clientName),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _phone,
            keyboardType: TextInputType.phone,
            textInputAction: widget.requisites ? TextInputAction.next : TextInputAction.done,
            onSubmitted: widget.requisites ? null : (_) => _save(),
            decoration: InputDecoration(labelText: l.phone, hintText: '0700 123 456'),
          ),
          if (widget.requisites) ...[
            const SizedBox(height: 8),
            TextField(
              controller: _company,
              textInputAction: TextInputAction.next,
              decoration: InputDecoration(labelText: l.companyLabel),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _inn,
              keyboardType: TextInputType.number,
              textInputAction: TextInputAction.done,
              onChanged: (_) => setState(() {}),
              onSubmitted: (_) => _save(),
              decoration: InputDecoration(labelText: l.innLabel, helperText: innBad ? l.innDigits : null),
            ),
          ],
          if (_error != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(_error!, style: TextStyle(color: context.pal.danger)),
            ),
        ],
      ),
      actions: [
        TextButton(onPressed: _busy ? null : () => Navigator.pop(context), child: Text(l.cancel)),
        FilledButton(
          style: FilledButton.styleFrom(minimumSize: const Size(110, 44)),
          onPressed: _busy ? null : _save,
          child: _busy ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) : Text(l.save),
        ),
      ],
    );
  }
}
