import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/mode/mode_provider.dart';
import 'simple_more_screen.dart';
import 'simple_receive_screen.dart';
import 'simple_sale_screen.dart';
import 'simple_totals_screen.dart';

/// 2026-11-20 (тариф «Мини»): вкладки Простого режима внутри общей оболочки. В обычном виде эти адреса закрыты (ModeProfile.hidesRoute).
List<RouteBase> simpleShellRoutes() => [
  GoRoute(
    path: '/in',
    builder: (_, _) => const SafeArea(top: false, child: SimpleReceiveScreen()),
  ),
  GoRoute(
    path: '/totals',
    builder: (_, _) => const SafeArea(top: false, child: SimpleTotalsScreen()),
  ),
  GoRoute(
    path: '/more',
    builder: (_, _) => const SafeArea(top: false, child: SimpleMoreScreen()),
  ),
];

/// «Продажа»: в Простом режиме — [SimpleSaleScreen], иначе прежний экран.
class SaleEntry extends ConsumerWidget {
  final Widget classic;
  const SaleEntry({super.key, required this.classic});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final simple = ref.watch(modeProfileProvider.select((m) => m.simple));
    return simple ? const SimpleSaleScreen() : classic;
  }
}
