import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../providers.dart';
import '../util/ui.dart';

/// 2026-10-29 (полная изоляция аккаунтов, уточнение владельца): «Загружаем данные аккаунта… 3/6» пока идёт загрузка и
/// «Данные загрузились не полностью — Повторить», если нет связи или сервер не ответил. Ничего от прошлого аккаунта не показывает.
class AccountLoadBanner extends ConsumerWidget {
  final EdgeInsetsGeometry margin;
  const AccountLoadBanner({super.key, this.margin = const EdgeInsets.only(bottom: 12)});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final st = ref.watch(accountLoadProvider);
    if (!st.loading && !st.failed) return const SizedBox.shrink();
    final pal = context.pal;
    final l = context.l;
    return Container(
      width: double.infinity,
      margin: margin,
      padding: const EdgeInsets.fromLTRB(12, 8, 4, 8),
      decoration: BoxDecoration(color: st.loading ? pal.warnSoft : pal.dangerSoft, borderRadius: BorderRadius.circular(12)),
      child: Row(
        children: [
          if (st.loading)
            const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
          else
            Icon(Icons.sync_problem_rounded, size: 20, color: pal.danger),
          const SizedBox(width: 10),
          Expanded(child: Text(st.loading ? l.accountLoading(st.done, st.total) : l.accountLoadFailed, style: const TextStyle(fontSize: 13))),
          if (!st.loading) TextButton(onPressed: () => ref.read(sessionProvider.notifier).loadAccountData(), child: Text(l.retry)),
        ],
      ),
    );
  }
}
