import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../providers.dart';
import 'ui.dart';

/// 13.10, владелец: «доступы в мобилке работают?» — действие, закрытое сотруднику на сайте (скидка, удаление строки,
/// возврат), не выполняется: сообщение и подсказка, где владелец включает доступ.
bool checkAccess(BuildContext context, WidgetRef ref, String permission) {
  final s = ref.read(sessionProvider);
  if (s == null || s.can(permission)) return true;
  toast(context, '${context.l.noAccess}. ${context.l.noAccessHint}', error: true);
  return false;
}

/// Раздел закрыт для сотрудника (маршрут не открывается и по ссылке).
class AccessDeniedScreen extends StatelessWidget {
  const AccessDeniedScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: AppBar(title: Text(l.noAccess)),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.lock_outline_rounded, size: 56, color: context.pal.textSoft),
              const SizedBox(height: 12),
              Text(l.noAccessHint, textAlign: TextAlign.center),
              const SizedBox(height: 20),
              FilledButton(
                onPressed: () => context.canPop() ? context.pop() : context.go('/home'),
                child: Text(MaterialLocalizations.of(context).backButtonTooltip),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
