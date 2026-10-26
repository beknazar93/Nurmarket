import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show SystemNavigator;
import 'package:go_router/go_router.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

/// Нижняя панель кассира: Главная · Продажа · Сканер · Чеки · Склад (макеты Т1–Т7).
/// 13.10, владелец: «в мобилке нет возврата в главное меню» — первой кнопкой «Главная».
class CashierShell extends StatefulWidget {
  final Widget child;
  final String location;
  const CashierShell({super.key, required this.child, required this.location});

  static const _tabs = ['/home', '/sale', '/scan', '/checks', '/stock'];

  @override
  State<CashierShell> createState() => _CashierShellState();
}

class _CashierShellState extends State<CashierShell> {
  DateTime? _lastBack;

  /// 2026-10-26 (живой аудит, П1, С-9): «Назад» на вкладке закрывал приложение. Теперь: вкладка → «Главная»;
  /// на «Главной» — «Нажмите ещё раз, чтобы выйти» (второе нажатие в течение 2 секунд выходит).
  void _onBack(bool didPop, Object? result) {
    if (didPop) return;
    if (!widget.location.startsWith('/home')) {
      context.go('/home');
      return;
    }
    final now = DateTime.now();
    final last = _lastBack;
    if (last != null && now.difference(last) < const Duration(seconds: 2)) {
      SystemNavigator.pop();
      return;
    }
    _lastBack = now;
    toast(context, context.l.exitPressAgain);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final selected = CashierShell._tabs.indexWhere((t) => widget.location.startsWith(t));
    final items = [
      (Icons.home_outlined, l.tabHome),
      (Icons.shopping_cart_outlined, l.tabSale),
      (Icons.qr_code_scanner_rounded, l.tabScan),
      (Icons.receipt_long_outlined, l.tabChecks),
      (Icons.inventory_2_outlined, l.tabStock),
    ];
    final p = context.pal;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: _onBack,
      child: Scaffold(
        body: widget.child,
        bottomNavigationBar: Container(
          decoration: BoxDecoration(
            color: p.panel,
            border: Border(top: BorderSide(color: p.border)),
          ),
          child: SafeArea(
            top: false,
            // 2026-10-26 (живой аудит, П1, В-4): высота панели не фиксированная (была 64 dp — при шрифте 1,3× переполнялась
            // на 11 px): минимум 64, дальше растёт с текстом. Подпись в одну строку ужимается по ширине вкладки.
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 64),
              child: IntrinsicHeight(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    for (var i = 0; i < items.length; i++)
                      Expanded(
                        child: Semantics(
                          selected: i == selected,
                          button: true,
                          child: InkWell(
                            onTap: () => context.go(CashierShell._tabs[i]),
                            child: Padding(
                              padding: const EdgeInsets.symmetric(horizontal: 2, vertical: 6),
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
                                  FittedBox(
                                    fit: BoxFit.scaleDown,
                                    child: Text(
                                      items[i].$2,
                                      maxLines: 1,
                                      style: TextStyle(
                                        fontSize: 11.5,
                                        fontWeight: i == selected ? FontWeight.w800 : FontWeight.w500,
                                        color: i == selected ? p.text : p.textSoft,
                                      ),
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
