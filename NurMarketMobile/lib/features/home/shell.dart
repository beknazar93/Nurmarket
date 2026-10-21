import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

/// Нижняя панель кассира: Главная · Продажа · Сканер · Чеки · Склад (макеты Т1–Т7).
/// 13.10, владелец: «в мобилке нет возврата в главное меню» — первой кнопкой «Главная».
class CashierShell extends StatelessWidget {
  final Widget child;
  final String location;
  const CashierShell({super.key, required this.child, required this.location});

  static const _tabs = ['/home', '/sale', '/scan', '/checks', '/stock'];

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final selected = _tabs.indexWhere((t) => location.startsWith(t));
    final items = [
      (Icons.home_outlined, l.tabHome),
      (Icons.shopping_cart_outlined, l.tabSale),
      (Icons.qr_code_scanner_rounded, l.tabScan),
      (Icons.receipt_long_outlined, l.tabChecks),
      (Icons.inventory_2_outlined, l.tabStock),
    ];
    final p = context.pal;
    return Scaffold(
      body: child,
      bottomNavigationBar: Container(
        decoration: BoxDecoration(
          color: p.panel,
          border: Border(top: BorderSide(color: p.border)),
        ),
        child: SafeArea(
          top: false,
          child: SizedBox(
            height: 64,
            child: Row(
              children: [
                for (var i = 0; i < items.length; i++)
                  Expanded(
                    child: InkWell(
                      onTap: () => context.go(_tabs[i]),
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          AnimatedContainer(
                            duration: const Duration(milliseconds: 150),
                            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                            decoration: BoxDecoration(color: i == selected ? T.accent : Colors.transparent, borderRadius: BorderRadius.circular(16)),
                            child: Icon(items[i].$1, size: 22, color: i == selected ? T.navy : p.textSoft),
                          ),
                          const SizedBox(height: 3),
                          Text(
                            items[i].$2,
                            style: TextStyle(
                              fontSize: 11.5,
                              fontWeight: i == selected ? FontWeight.w800 : FontWeight.w500,
                              color: i == selected ? p.text : p.textSoft,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
