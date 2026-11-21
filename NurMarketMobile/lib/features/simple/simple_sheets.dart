import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show FilteringTextInputFormatter, TextInputFormatter;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart';
import '../payment/payment_screen.dart' show pickClient, samePhone;
import '../payment/sales_repository.dart';
import '../sale/cart.dart' show roundMoney;
import 'simple_math.dart';

/// 2026-11-20 (Простой режим): окна продажи — «Получено» со сдачей, «Торг», «В долг» (клиент на лету), «Другая оплата».
/// Суммы вводятся системной цифровой клавиатурой (TextInputType.number) и кнопками быстрых сумм; своей экранной клавиатуры нет.

/// Поле суммы: цифры и одна запятая/точка, не больше 10 цифр до запятой (как поле «Получено» оплаты).
final List<TextInputFormatter> amountFormatters = [TextInputFormatter.withFunction((old, nw) => RegExp(r'^\d{0,10}([.,]\d{0,2})?$').hasMatch(nw.text) ? nw : old)];

String _plain(double v) => v.toStringAsFixed(v.truncateToDouble() == v ? 0 : 2);

/// Кнопка не ниже 56 dp.
ButtonStyle bigButton({Size min = const Size(0, 56)}) => FilledButton.styleFrom(
  minimumSize: min,
  textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
);

Future<R?> _sheet<R>(BuildContext context, WidgetBuilder b) =>
    showModalBottomSheet<R>(context: context, isScrollControlled: true, showDragHandle: true, useSafeArea: true, builder: b);

// ------------------------------------------------------------------ «Получено» и сдача

/// Окно «Получено»: быстрые суммы («Без сдачи» и ближайшие 100 / 500 / 1000 / 5000), своя сумма, СДАЧА крупно. Возвращает, сколько дали.
Future<double?> askReceived(BuildContext context, {required double total}) => _sheet<double>(context, (c) => _ReceivedSheet(total: total));

class _ReceivedSheet extends StatefulWidget {
  final double total;
  const _ReceivedSheet({required this.total});
  @override
  State<_ReceivedSheet> createState() => _ReceivedSheetState();
}

class _ReceivedSheetState extends State<_ReceivedSheet> {
  final _ctl = TextEditingController();
  @override
  void dispose() {
    _ctl.dispose();
    super.dispose();
  }

  double? get _got => parseNum(_ctl.text);

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final total = widget.total;
    final got = _got;
    final ok = got != null && got + 0.004 >= total;
    final change = ok ? roundMoney(got - total) : 0.0;
    final quick = receivedQuickAmounts(total);
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: PinnedSheet(
        footer: FilledButton(
          key: const Key('recvOk'),
          style: bigButton(min: const Size.fromHeight(60)),
          onPressed: ok ? () => Navigator.pop(context, got) : null,
          child: Text(context.t5('Принять оплату', 'Төлөмдү кабыл алуу', 'To\'lovni qabul qilish', 'Accept payment', 'Ödemeyi al')),
        ),
        children: [
          Text(context.t5('К оплате', 'Төлөнүүчү', 'To\'lov', 'To pay', 'Ödenecek'), style: TextStyle(color: pal.textSoft, fontSize: 14)),
          Text('${money(total)} ${context.l.som}', style: const TextStyle(fontSize: 28, fontWeight: FontWeight.w800)),
          const SizedBox(height: 8),
          Container(
            key: const Key('recvChange'),
            padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 14),
            decoration: BoxDecoration(color: ok && change > 0 ? pal.successSoft : pal.soft, borderRadius: BorderRadius.circular(14)),
            child: Text(
              got == null
                  ? context.t5('Сдача: —', 'Кайтарым: —', 'Qaytim: —', 'Change: —', 'Para üstü: —')
                  : ok
                  ? '${context.l.change}: ${money(change)} ${context.l.som}'
                  : context.t5(
                      'Не хватает ${money(total - got)} ${context.l.som}',
                      '${money(total - got)} ${context.l.som} жетишпейт',
                      '${money(total - got)} ${context.l.som} yetishmaydi',
                      'Short by ${money(total - got)} ${context.l.som}',
                      '${money(total - got)} ${context.l.som} eksik',
                    ),
              textAlign: TextAlign.center,
              style: TextStyle(fontSize: 34, fontWeight: FontWeight.w800, color: !ok && got != null ? pal.danger : (change > 0 ? pal.success : pal.textSoft)),
            ),
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final v in quick)
                SizedBox(
                  height: 56,
                  child: OutlinedButton(
                    key: Key('recvQuick_${v.round()}'),
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size(92, 56),
                      textStyle: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800),
                    ),
                    onPressed: () => setState(() => _ctl.text = _plain(v)),
                    child: Text(v == total ? context.t5('Без сдачи', 'Кайтарымсыз', 'Qaytimsiz', 'No change', 'Para üstü yok') : money(v)),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 12),
          TextField(
            key: const Key('recvField'),
            controller: _ctl,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: amountFormatters,
            onChanged: (_) => setState(() {}),
            style: const TextStyle(fontSize: 26, fontWeight: FontWeight.w800),
            decoration: InputDecoration(labelText: context.t5('Получено', 'Алынды', 'Olindi', 'Received', 'Alındı'), suffixText: context.l.som),
          ),
        ],
      ),
    );
  }
}

// ------------------------------------------------------------------ торг

/// Результат окна «Торг»: новая сумма скидки (null — «убрать торг» передаётся как 0).
class HaggleResult {
  final double discountSum;
  const HaggleResult(this.discountSum);
}

/// «Торг»: «До 10 / 50 / 100» (округление итога вниз) и «Договорились за …» (скидка = разница). [base] — сумма до торга.
Future<HaggleResult?> askHaggle(BuildContext context, {required double base, required double currentDiscount}) =>
    _sheet<HaggleResult>(context, (c) => _HaggleSheet(base: base, current: currentDiscount));

class _HaggleSheet extends StatefulWidget {
  final double base;
  final double current;
  const _HaggleSheet({required this.base, required this.current});
  @override
  State<_HaggleSheet> createState() => _HaggleSheetState();
}

class _HaggleSheetState extends State<_HaggleSheet> {
  final _ctl = TextEditingController();
  @override
  void dispose() {
    _ctl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final base = widget.base;
    final agreed = parseNum(_ctl.text);
    final disc = agreed == null ? null : agreedDiscount(base, agreed);
    final bad = agreed != null && disc == null;
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: PinnedSheet(
        footer: FilledButton(
          key: const Key('haggleOk'),
          style: bigButton(min: const Size.fromHeight(60)),
          onPressed: disc == null ? null : () => Navigator.pop(context, HaggleResult(disc)),
          child: Text(context.t5('Применить', 'Колдонуу', 'Qo\'llash', 'Apply', 'Uygula')),
        ),
        children: [
          Text(context.t5('Торг', 'Соодалашуу', 'Savdolashish', 'Haggle', 'Pazarlık'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
          Text(
            '${context.t5('Без скидки', 'Арзандатуусуз', 'Chegirmasiz', 'Without discount', 'İndirimsiz')}: ${money(base)} ${context.l.som}',
            style: TextStyle(color: pal.textSoft),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              for (final step in const [10, 50, 100]) ...[
                Expanded(
                  child: Builder(
                    builder: (c) {
                      final v = roundDownTotal(base, step);
                      return SizedBox(
                        height: 64,
                        child: FilledButton.tonal(
                          key: Key('haggleStep_$step'),
                          style: FilledButton.styleFrom(padding: const EdgeInsets.symmetric(horizontal: 4)),
                          onPressed: v == null ? null : () => Navigator.pop(context, HaggleResult(haggleSumFor(base, v))),
                          child: FittedBox(
                            fit: BoxFit.scaleDown,
                            child: Column(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Text('${context.t5('До', 'Чейин', 'Gacha', 'To', 'Kadar')} $step', style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
                                if (v != null) Text(money(v), style: const TextStyle(fontSize: 12)),
                              ],
                            ),
                          ),
                        ),
                      );
                    },
                  ),
                ),
                if (step != 100) const SizedBox(width: 8),
              ],
            ],
          ),
          const SizedBox(height: 14),
          TextField(
            key: const Key('haggleField'),
            controller: _ctl,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: amountFormatters,
            onChanged: (_) => setState(() {}),
            style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800),
            decoration: InputDecoration(
              labelText: context.t5('Договорились за', 'Макулдашылды', 'Kelishildi', 'Agreed for', 'Anlaşılan'),
              suffixText: context.l.som,
              errorText: bad
                  ? context.t5(
                      'Сумма должна быть меньше ${money(base)}',
                      'Сумма ${money(base)} дан аз болушу керек',
                      'Summa ${money(base)} dan kam bo\'lishi kerak',
                      'The amount must be below ${money(base)}',
                      'Tutar ${money(base)} altında olmalı',
                    )
                  : null,
            ),
          ),
          if (disc != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                '${context.t5('Скидка', 'Арзандатуу', 'Chegirma', 'Discount', 'İndirim')}: ${money(disc)} ${context.l.som} (${qtyText(roundMoney(discountPercentOf(base, disc)))} %)',
                key: const Key('haggleDiscount'),
                style: TextStyle(fontSize: 16, fontWeight: FontWeight.w800, color: pal.success),
              ),
            ),
          if (widget.current > 0) ...[
            const SizedBox(height: 8),
            OutlinedButton(
              key: const Key('haggleClear'),
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(56)),
              onPressed: () => Navigator.pop(context, const HaggleResult(0)),
              child: Text(context.t5('Убрать торг', 'Соодалашууну алып салуу', 'Savdolashishni olib tashlash', 'Remove haggle', 'Pazarlığı kaldır')),
            ),
          ],
        ],
      ),
    );
  }
}

/// Крупная скидка (> [haggleWarnPercent] %): в мобилке PIN нет — спрашиваем подтверждение у владельца вместо него.
Future<bool> confirmBigDiscount(BuildContext context, double percent) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(c.t5('Большая скидка', 'Чоң арзандатуу', 'Katta chegirma', 'Large discount', 'Büyük indirim')),
      content: Text(
        c.t5(
          'Скидка ${qtyText(roundMoney(percent))} % — больше обычной. Продолжить?',
          'Арзандатуу ${qtyText(roundMoney(percent))} % — адаттагыдан көп. Улантабызбы?',
          'Chegirma ${qtyText(roundMoney(percent))} % — odatdagidan ko\'p. Davom etamizmi?',
          'The discount is ${qtyText(roundMoney(percent))} % — more than usual. Continue?',
          'İndirim ${qtyText(roundMoney(percent))} % — olağandan fazla. Devam edilsin mi?',
        ),
      ),
      actions: [
        TextButton(
          style: TextButton.styleFrom(minimumSize: const Size(64, 48)),
          onPressed: () => Navigator.pop(c, false),
          child: Text(c.l.cancel),
        ),
        FilledButton(
          style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
          onPressed: () => Navigator.pop(c, true),
          child: Text(c.t5('Продолжить', 'Улантуу', 'Davom etish', 'Continue', 'Devam')),
        ),
      ],
    ),
  );
  return ok == true;
}

// ------------------------------------------------------------------ в долг

/// Клиент и срок долга.
class DebtChoice {
  final Client client;
  final DateTime due;
  const DebtChoice(this.client, this.due);
}

/// «В долг»: имя + телефон (клиент создаётся здесь же; тот же телефон — берётся уже существующий) → срок 7 / 14 / 30 дней → готово.
Future<DebtChoice?> askDebt(BuildContext context) => _sheet<DebtChoice>(context, (c) => const _DebtSheet());

class _DebtSheet extends ConsumerStatefulWidget {
  const _DebtSheet();
  @override
  ConsumerState<_DebtSheet> createState() => _DebtSheetState();
}

class _DebtSheetState extends ConsumerState<_DebtSheet> {
  final _name = TextEditingController();
  final _phone = TextEditingController();
  int _days = 14;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _name.dispose();
    _phone.dispose();
    super.dispose();
  }

  DateTime get _due => DateUtils.dateOnly(DateTime.now()).add(Duration(days: _days));

  Future<void> _pick() async {
    final c = await pickClient(context);
    if (c != null && mounted) Navigator.pop(context, DebtChoice(c, _due));
  }

  Future<void> _done() async {
    if (_busy) return;
    final name = _name.text.trim();
    final phone = _phone.text.trim();
    final digits = phone.replaceAll(RegExp(r'\D'), '');
    if (name.isEmpty || digits.length < 9) {
      setState(
        () => _error = context.t5(
          'Введите имя и телефон (нужен для напоминания в WhatsApp)',
          'Атын жана телефонду жазыңыз (WhatsApp эскертүүсү үчүн керек)',
          'Ism va telefonni kiriting (WhatsApp eslatmasi uchun kerak)',
          'Enter the name and phone (needed for the WhatsApp reminder)',
          'Ad ve telefon girin (WhatsApp hatırlatması için gerekli)',
        ),
      );
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    final repo = ref.read(salesRepoProvider);
    try {
      final tail = digits.length > 9 ? digits.substring(digits.length - 9) : digits;
      final same = (await repo.clients(tail)).where((c) => samePhone(c.phone, phone)).firstOrNull;
      final client = same ?? await repo.createClient(name, phone);
      if (!mounted) return;
      Navigator.pop(context, DebtChoice(client, _due));
    } on ApiException catch (e) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = e.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: PinnedSheet(
        footer: FilledButton(
          key: const Key('debtDone'),
          style: bigButton(min: const Size.fromHeight(60)),
          onPressed: _busy ? null : _done,
          child: _busy
              ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5))
              : Text(context.t5('Готово — в долг', 'Даяр — насыяга', 'Tayyor — qarzga', 'Done — on credit', 'Tamam — veresiye')),
        ),
        children: [
          Text(context.t5('В долг', 'Насыяга', 'Qarzga', 'On credit', 'Veresiye'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
          const SizedBox(height: 10),
          TextField(
            key: const Key('debtName'),
            controller: _name,
            textCapitalization: TextCapitalization.words,
            textInputAction: TextInputAction.next,
            decoration: InputDecoration(labelText: context.l.clientName),
          ),
          const SizedBox(height: 10),
          TextField(
            key: const Key('debtPhone'),
            controller: _phone,
            keyboardType: TextInputType.phone,
            inputFormatters: [FilteringTextInputFormatter.allow(RegExp(r'[0-9+\s()-]'))],
            decoration: InputDecoration(labelText: context.l.phone, hintText: '0700 123 456'),
          ),
          const SizedBox(height: 12),
          Text(context.t5('Вернуть через', 'Кайтаруу мөөнөтү', 'Qaytarish muddati', 'Return in', 'Geri ödeme'), style: TextStyle(color: pal.textSoft, fontSize: 13)),
          const SizedBox(height: 6),
          Row(
            children: [
              for (final d in const [7, 14, 30]) ...[
                Expanded(
                  child: SizedBox(
                    height: 56,
                    child: ChoiceChip(
                      key: Key('debtDays_$d'),
                      showCheckmark: false,
                      label: SizedBox(
                        width: double.infinity,
                        child: Text(context.l.debtDueDays(d), textAlign: TextAlign.center),
                      ),
                      selected: _days == d,
                      labelStyle: TextStyle(color: _days == d ? pal.onAction : pal.text, fontWeight: FontWeight.w800, fontSize: 15),
                      onSelected: (_) => setState(() => _days = d),
                    ),
                  ),
                ),
                if (d != 30) const SizedBox(width: 8),
              ],
            ],
          ),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                _error!,
                key: const Key('debtError'),
                style: TextStyle(color: pal.danger, fontWeight: FontWeight.w700),
              ),
            ),
          TextButton(
            key: const Key('debtPick'),
            style: TextButton.styleFrom(minimumSize: const Size.fromHeight(52)),
            onPressed: _busy ? null : _pick,
            child: Text(
              context.t5('Выбрать из списка клиентов', 'Кардарлар тизмесинен тандоо', 'Mijozlar ro\'yxatidan tanlash', 'Pick from the client list', 'Müşteri listesinden seç'),
            ),
          ),
        ],
      ),
    );
  }
}

// ------------------------------------------------------------------ другая оплата

enum OtherPay { card, debt, cashAmount }

/// «Другая оплата»: карта / QR банка, в долг, наличные с суммой.
Future<OtherPay?> askOtherPay(BuildContext context) => _sheet<OtherPay>(
  context,
  (c) => SafeArea(
    child: Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(c.t5('Другая оплата', 'Башка төлөм', 'Boshqa to\'lov', 'Other payment', 'Diğer ödeme'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
          const SizedBox(height: 12),
          for (final (key, v, icon, label) in [
            ('otherCash', OtherPay.cashAmount, Icons.payments_outlined, c.t5('Наличные — получено', 'Накталай — алынды', 'Naqd — olindi', 'Cash — received', 'Nakit — alındı')),
            ('otherCard', OtherPay.card, Icons.qr_code_2_rounded, c.t5('Карта / QR банка', 'Карта / банк QR', 'Karta / bank QR', 'Card / bank QR', 'Kart / banka QR')),
            ('otherDebt', OtherPay.debt, Icons.person_outline_rounded, c.t5('В долг', 'Насыяга', 'Qarzga', 'On credit', 'Veresiye')),
          ]) ...[
            OutlinedButton.icon(
              key: Key(key),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(60),
                alignment: Alignment.centerLeft,
                textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
              ),
              onPressed: () => Navigator.pop(c, v),
              icon: Icon(icon),
              label: Text(label),
            ),
            const SizedBox(height: 8),
          ],
        ],
      ),
    ),
  ),
);

/// Нижнее окно: содержимое прокручивается, главная кнопка ([footer]) закреплена внизу и не уходит под край на низких экранах и под клавиатуру.
class PinnedSheet extends StatelessWidget {
  final List<Widget> children;
  final Widget footer;
  const PinnedSheet({super.key, required this.children, required this.footer});

  @override
  Widget build(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Flexible(
        child: SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: children),
        ),
      ),
      const SizedBox(height: 10),
      footer,
    ],
  );
}
