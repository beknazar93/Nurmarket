import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show SystemNavigator;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/mode/login_expiry.dart';
import '../../core/mode/mode_provider.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../home/shell.dart';

/// 2026-11-20 (тариф «Мини», E1/E2): оболочка выбирается профилем режима. Простой режим — [SimpleShell], иначе прежняя [CashierShell].
class AdaptiveShell extends ConsumerWidget {
  final Widget child;
  final String location;
  const AdaptiveShell({super.key, required this.child, required this.location});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final simple = ref.watch(modeProfileProvider.select((m) => m.simple));
    return simple ? SimpleShell(location: location, child: child) : CashierShell(location: location, child: child);
  }
}

/// Даты неотправленных ЧЕКОВ этого аккаунта (для предупреждения о чеках старше 5–7 суток).
final pendingSaleDatesProvider = StreamProvider<List<DateTime>>((ref) {
  final db = ref.watch(databaseProvider);
  final me = OutboxMeta.ownerOf(ref.watch(sessionProvider));
  return db.watchPendingRows().map(
    (rows) => [
      for (final r in rows)
        if (r.kind == 'sale' && (OutboxMeta.ownerOfRow(r) ?? me) == me) r.createdAt,
    ],
  );
});

/// Нижняя панель Простого режима: Продажа · Приход · Итоги · Ещё. Кнопки не ниже 64 dp, подписи крупные; всё главное — внизу
/// (режим «одной рукой»). Сверху — полоса «Не отправлено N» и предупреждения (срок входа, старые чеки).
class SimpleShell extends ConsumerStatefulWidget {
  final Widget child;
  final String location;
  const SimpleShell({super.key, required this.child, required this.location});

  static const tabs = ['/sale', '/in', '/totals', '/more'];

  @override
  ConsumerState<SimpleShell> createState() => _SimpleShellState();
}

class _SimpleShellState extends ConsumerState<SimpleShell> {
  DateTime? _lastBack;

  void _onBack(bool didPop, Object? result) {
    if (didPop) return;
    if (!widget.location.startsWith('/sale')) {
      context.go('/sale');
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
    final p = context.pal;
    final items = [
      (Icons.shopping_cart_outlined, context.t5('Продажа', 'Сатуу', 'Sotuv', 'Sale', 'Satış')),
      (Icons.move_to_inbox_outlined, context.t5('Приход', 'Киреше', 'Kirim', 'Receive', 'Giriş')),
      (Icons.bar_chart_rounded, context.t5('Итоги', 'Жыйынтык', 'Natijalar', 'Totals', 'Özet')),
      (Icons.more_horiz_rounded, context.t5('Ещё', 'Дагы', 'Yana', 'More', 'Daha')),
    ];
    final selected = SimpleShell.tabs.indexWhere((t) => widget.location.startsWith(t));
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: _onBack,
      child: Scaffold(
        body: Column(
          children: [
            SimpleStatusBar(topInset: MediaQuery.viewPaddingOf(context).top),
            // страницы вкладок не добавляют верхний отступ второй раз
            Expanded(
              child: MediaQuery.removePadding(context: context, removeTop: true, child: widget.child),
            ),
          ],
        ),
        bottomNavigationBar: Container(
          decoration: BoxDecoration(
            color: p.panel,
            border: Border(top: BorderSide(color: p.border)),
          ),
          child: Material(
            type: MaterialType.transparency,
            child: SafeArea(
              top: false,
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
                              key: Key('simpleTab$i'),
                              onTap: () => context.go(SimpleShell.tabs[i]),
                              child: Padding(
                                padding: const EdgeInsets.symmetric(horizontal: 2, vertical: 6),
                                child: Column(
                                  mainAxisAlignment: MainAxisAlignment.center,
                                  children: [
                                    AnimatedContainer(
                                      duration: const Duration(milliseconds: 150),
                                      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                                      decoration: BoxDecoration(color: i == selected ? T.accent : Colors.transparent, borderRadius: BorderRadius.circular(16)),
                                      child: Icon(items[i].$1, size: 28, color: i == selected ? T.navy : p.textSoft),
                                    ),
                                    const SizedBox(height: 2),
                                    FittedBox(
                                      fit: BoxFit.scaleDown,
                                      child: Text(
                                        items[i].$2,
                                        maxLines: 1,
                                        style: TextStyle(fontSize: 14, fontWeight: i == selected ? FontWeight.w800 : FontWeight.w600, color: i == selected ? p.text : p.textSoft),
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
      ),
    );
  }
}

/// Полоса сверху: «Не отправлено N» всегда видна (зелёное «Всё отправлено», когда очередь пуста) и предупреждения «не потерять продажу».
class SimpleStatusBar extends ConsumerWidget {
  final double topInset;
  const SimpleStatusBar({super.key, this.topInset = 0});

  static String _dt(DateTime d) {
    String t(int x) => x.toString().padLeft(2, '0');
    return '${t(d.day)}.${t(d.month)} ${t(d.hour)}:${t(d.minute)}';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pal = context.pal;
    final pending = ref.watch(pendingCountProvider).value ?? 0;
    final session = ref.watch(sessionProvider);
    final dates = ref.watch(pendingSaleDatesProvider).value ?? const <DateTime>[];
    final warnings = simpleWarnings(now: DateTime.now(), loginExpiry: jwtExpiry(session?.refresh), pendingSales: dates);
    final unsent = pending > 0;
    String warnText(SimpleWarning w) => switch (w.kind) {
      SimpleWarningKind.loginExpires => context.t5(
        'Подключитесь к интернету: вход истечёт ${_dt(w.at!)}',
        'Интернетке туташыңыз: кирүү ${_dt(w.at!)} бүтөт',
        'Internetga ulaning: kirish ${_dt(w.at!)} da tugaydi',
        'Connect to the internet: your login expires ${_dt(w.at!)}',
        'İnternete bağlanın: oturum ${_dt(w.at!)} tarihinde sona erer',
      ),
      SimpleWarningKind.loginExpired => context.t5(
        'Вход истёк. Подключитесь к интернету и войдите заново — неотправленные продажи не потеряются',
        'Кирүү мөөнөтү бүттү. Интернетке туташып, кайра кириңиз — жөнөтүлбөгөн сатуулар жоголбойт',
        'Kirish muddati tugadi. Internetga ulanib, qayta kiring — yuborilmagan sotuvlar yo\'qolmaydi',
        'Your login expired. Connect to the internet and sign in again — unsent sales are kept',
        'Oturum süresi doldu. İnternete bağlanıp yeniden giriş yapın — gönderilmemiş satışlar kaybolmaz',
      ),
      SimpleWarningKind.oldSalesSoon => context.t5(
        'Чеков без отправки: ${w.count}, самому старому ${w.days} дн. Через 7 суток их примут только с PIN владельца — подключитесь к интернету',
        'Жөнөтүлбөгөн чектер: ${w.count}, эң эскиси ${w.days} күн. 7 күндөн кийин ээсинин PIN кодусуз кабыл алынбайт — интернетке туташыңыз',
        'Yuborilmagan cheklar: ${w.count}, eng eskisi ${w.days} kun. 7 kundan keyin faqat egasining PIN kodi bilan qabul qilinadi — internetga ulaning',
        'Unsent receipts: ${w.count}, the oldest is ${w.days} days old. After 7 days they need the owner PIN — connect to the internet',
        'Gönderilmemiş fişler: ${w.count}, en eskisi ${w.days} günlük. 7 günden sonra yalnızca sahip PIN koduyla kabul edilir — internete bağlanın',
      ),
      SimpleWarningKind.oldSalesLate => context.t5(
        'Чеки старше 7 суток: ${w.count}. Сервер примет их только с PIN владельца. Не удаляйте приложение, сообщите владельцу',
        '7 күндөн эски чектер: ${w.count}. Сервер аларды ээсинин PIN коду менен гана кабыл алат. Колдонмону өчүрбөңүз, ээсине айтыңыз',
        '7 kundan eski cheklar: ${w.count}. Server ularni faqat egasining PIN kodi bilan qabul qiladi. Ilovani o\'chirmang, egasiga ayting',
        'Receipts older than 7 days: ${w.count}. The server accepts them only with the owner PIN. Do not delete the app, tell the owner',
        '7 günden eski fişler: ${w.count}. Sunucu bunları yalnızca sahip PIN koduyla kabul eder. Uygulamayı silmeyin, sahibine söyleyin',
      ),
    };
    return Container(
      key: const Key('simpleStatusBar'),
      color: pal.window,
      padding: EdgeInsets.fromLTRB(12, topInset + 4, 12, 4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            key: const Key('simpleUnsent'),
            borderRadius: BorderRadius.circular(10),
            onTap: () => context.push('/sync'),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 36),
              child: Row(
                children: [
                  Icon(unsent ? Icons.cloud_upload_outlined : Icons.cloud_done_outlined, size: 20, color: unsent ? pal.warn : pal.success),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      unsent
                          ? context.t5('Не отправлено: $pending', 'Жөнөтүлгөн жок: $pending', 'Yuborilmagan: $pending', 'Not sent: $pending', 'Gönderilmedi: $pending')
                          : context.t5('Всё отправлено', 'Баары жөнөтүлдү', 'Hammasi yuborildi', 'Everything sent', 'Hepsi gönderildi'),
                      key: const Key('simpleUnsentText'),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(fontSize: 14, fontWeight: FontWeight.w800, color: unsent ? pal.warn : pal.success),
                    ),
                  ),
                  if (unsent) Icon(Icons.chevron_right_rounded, color: pal.warn),
                ],
              ),
            ),
          ),
          for (final w in warnings)
            Container(
              key: Key('simpleWarn_${w.kind.name}'),
              margin: const EdgeInsets.only(top: 4),
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(12)),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.warning_amber_rounded, size: 20, color: pal.warn),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      warnText(w),
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: pal.warn),
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}
