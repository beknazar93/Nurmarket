import 'package:flutter/material.dart';

import '../../core/ui/infographic/infographic_data.dart';
import '../../core/ui/infographic/infographics.dart';
import '../../core/util/ui.dart';
import 'shift_repository.dart';

/// 2026-10-29 (владелец: «инфографику лучше», спека Fable, экран «Смена / Z-итог»): «Чем платили» — полоса и строки по способам
/// оплаты; «Деньги в ящике» — части «Должно быть в кассе» со знаками (+ зелёным, − красным). Числа те же, что в ленте X/Z:
/// [ShiftBreakdown] и число сервера; неизвестные части не показываются нулём (их пометки — в ленте ниже).
class ShiftInfographic extends StatelessWidget {
  final ShiftBreakdown bd;
  const ShiftInfographic(this.bd, {super.key});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final steps = [
      for (final s in drawerSteps(bd))
        if (s.known && (s.part == DrawerPart.opening || s.signed.abs() > 0.004)) s,
    ];
    String label(DrawerPart p) => switch (p) {
      DrawerPart.opening => l.openingCashLabel,
      DrawerPart.cashSales => l.cash,
      DrawerPart.debtPayments => l.shDebtPayments,
      DrawerPart.inflow => l.cashIn,
      DrawerPart.returnsCash => l.shReturnsCash,
      DrawerPart.outflow => l.cashOut,
    };
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        InfoCard(
          title: l.infPaidWith,
          subtitle: l.checksCount(bd.salesCount),
          child: PaymentSplit(cash: bd.cashSales, nonCash: bd.nonCashSales, debt: bd.debtSales, rows: true),
        ),
        const SizedBox(height: 12),
        InfoCard(
          title: l.shCashBlock,
          how: l.infHowShiftCash,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              for (final s in steps)
                ConstrainedBox(
                  constraints: const BoxConstraints(minHeight: 44),
                  child: Container(
                    decoration: BoxDecoration(border: Border(top: BorderSide(color: s == steps.first ? Colors.transparent : pal.border))),
                    child: Row(
                      children: [
                        Expanded(child: Text(label(s.part), maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 14))),
                        const SizedBox(width: 8),
                        Text(
                          s.part == DrawerPart.opening ? '${money(s.signed)} ${l.som}' : '${s.signed >= 0 ? '+' : '−'}${money(s.signed.abs())} ${l.som}',
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w700,
                            fontFeatures: const [FontFeature.tabularFigures()],
                            color: s.part == DrawerPart.opening ? null : (s.signed >= 0 ? pal.success : pal.danger),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              Container(
                margin: const EdgeInsets.only(top: 4),
                padding: const EdgeInsets.only(top: 8),
                decoration: BoxDecoration(border: Border(top: BorderSide(color: pal.borderStrong))),
                child: Row(
                  children: [
                    Expanded(child: Text(l.shouldBeInDrawer, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700))),
                    Text('${money(bd.serverExpected)} ${l.som}', style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800, fontFeatures: [FontFeature.tabularFigures()])),
                  ],
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
