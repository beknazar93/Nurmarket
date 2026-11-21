import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/util/ui.dart';
import '../ops/debt_whatsapp.dart';
import '../ops/ops_repository.dart';

/// 2026-11-20 (Простой режим, М13): «Итоги дня» крупно — «Выручка», «Наличных в кассе», «Должны мне», «Продаж N», кнопка «Закрыть день».
/// Данные — смена сервера (как на главной обычного вида) и список должников; без связи — последние известные (должники — из памяти телефона).
class SimpleTotalsScreen extends ConsumerStatefulWidget {
  const SimpleTotalsScreen({super.key});
  @override
  ConsumerState<SimpleTotalsScreen> createState() => _SimpleTotalsScreenState();
}

class _SimpleTotalsScreenState extends ConsumerState<SimpleTotalsScreen> {
  List<Debtor>? _debtors;
  bool _debtorsStale = false;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    unawaited(ref.read(shiftProvider.notifier).refresh());
    final repo = ref.read(opsRepoProvider);
    final owner = OutboxMeta.ownerOf(ref.read(sessionProvider));
    try {
      final rows = await repo.debtors();
      if (mounted) {
        setState(() {
          _debtors = rows;
          _debtorsStale = false;
        });
      }
      await repo.cacheDebtors(owner, rows);
    } on ApiException {
      final saved = await repo.cachedDebtors(owner);
      if (mounted && saved != null) {
        setState(() {
          _debtors = saved;
          _debtorsStale = true;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final l = context.l;
    final shift = ref.watch(shiftProvider);
    final info = shift.shift;
    final owed = _debtors?.fold<double>(0, (s, d) => s + d.debt);
    // «Должно быть в кассе»: ответ сервера; не прислал — начало + наличные продажи
    final cash = info == null ? 0.0 : (info.expectedCash > 0 ? info.expectedCash : info.openingCash + info.cashSales);
    Widget big(Key key, String label, String value, {Color? color, VoidCallback? onTap, String? hint}) => Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Material(
        color: pal.panel,
        borderRadius: BorderRadius.circular(18),
        child: InkWell(
          key: key,
          borderRadius: BorderRadius.circular(18),
          onTap: onTap,
          child: Container(
            constraints: const BoxConstraints(minHeight: 88),
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(18),
              border: Border.all(color: pal.border),
            ),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        label,
                        style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: pal.textSoft),
                      ),
                      FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: Alignment.centerLeft,
                        child: Text(
                          value,
                          maxLines: 1,
                          style: TextStyle(fontSize: 34, fontWeight: FontWeight.w800, color: color ?? pal.text),
                        ),
                      ),
                      if (hint != null) Text(hint, style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
                    ],
                  ),
                ),
                if (onTap != null) Icon(Icons.chevron_right_rounded, color: pal.textSoft),
              ],
            ),
          ),
        ),
      ),
    );
    final list = RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
        children: [
          Text(context.t5('Итоги дня', 'Күндүн жыйынтыгы', 'Kun natijalari', 'Day totals', 'Gün özeti'), style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
          const SizedBox(height: 10),
          if (info == null && !shift.loading)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Text(
                context.t5(
                  'Смена откроется сама при первой продаже',
                  'Смена биринчи сатууда өзү ачылат',
                  'Smena birinchi sotuvda o\'zi ochiladi',
                  'The shift opens by itself on the first sale',
                  'Vardiya ilk satışta kendiliğinden açılır',
                ),
                key: const Key('totalsNoShift'),
                style: TextStyle(color: pal.textSoft, fontSize: 14),
              ),
            ),
          big(const Key('totalsRevenue'), context.t5('Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro'), '${money(info?.salesTotal ?? 0)} ${l.som}'),
          big(const Key('totalsCash'), context.t5('Наличных в кассе', 'Кассадагы накталай', 'Kassadagi naqd', 'Cash in the drawer', 'Kasadaki nakit'), '${money(cash)} ${l.som}'),
          big(
            const Key('totalsDebts'),
            context.t5('Должны мне', 'Мага карыз', 'Menga qarzdor', 'Owed to me', 'Bana borçlu'),
            owed == null ? '—' : '${money(owed)} ${l.som}',
            color: (owed ?? 0) > 0 ? pal.warn : null,
            hint: _debtorsStale
                ? context.t5(
                    'Список из памяти телефона (нет связи)',
                    'Тизме телефондун эсинен (байланыш жок)',
                    'Ro\'yxat telefon xotirasidan (aloqa yo\'q)',
                    'List from the phone memory (offline)',
                    'Liste telefon belleğinden (çevrimdışı)',
                  )
                : null,
            onTap: () => context.push('/debts'),
          ),
          big(const Key('totalsCount'), context.t5('Продаж', 'Сатуу', 'Sotuvlar', 'Sales', 'Satış'), '${info?.salesCount ?? 0}'),
          if (_debtors != null && _debtors!.isNotEmpty) ...[
            const SizedBox(height: 4),
            Text(
              context.t5('Кто должен', 'Ким карыз', 'Kim qarzdor', 'Who owes', 'Kim borçlu'),
              style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w700),
            ),
            for (final d in _debtors!.take(5))
              Container(
                key: Key('debtor_${d.clientId}'),
                margin: const EdgeInsets.only(top: 6),
                padding: const EdgeInsets.fromLTRB(14, 6, 6, 6),
                decoration: BoxDecoration(
                  color: pal.panel,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: pal.border),
                ),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            d.name,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
                          ),
                          Text(
                            '${money(d.debt)} ${l.som}',
                            style: TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: pal.warn),
                          ),
                        ],
                      ),
                    ),
                    DebtWhatsAppButton(phone: d.phone, name: d.name, amount: d.debt, shop: ref.read(sessionProvider)?.companyName ?? ''),
                  ],
                ),
              ),
          ],
        ],
      ),
    );
    // «Закрыть день» закреплена внизу — достать одной рукой, не прокручивая
    return Column(
      children: [
        Expanded(child: list),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
          child: SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              key: const Key('totalsCloseDay'),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(64),
                textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
              ),
              onPressed: info == null ? null : () => context.push('/close-shift'),
              icon: const Icon(Icons.lock_clock_rounded),
              label: Text(context.t5('Закрыть день', 'Күндү жабуу', 'Kunni yopish', 'Close the day', 'Günü kapat')),
            ),
          ),
        ),
      ],
    );
  }
}
