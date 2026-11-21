import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/server_capabilities.dart';
import '../../core/mode/mode_provider.dart';
import '../../core/mode/plan_limit.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/util/ui.dart';

/// 2026-11-20 (Простой режим): вкладка «Ещё» — товары, долги, чеки, неотправленное, настройки; тариф и «обычный режим».
/// Здесь же обновляется тариф (при открытии, не чаще раза в 6 часов).
class SimpleMoreScreen extends ConsumerStatefulWidget {
  const SimpleMoreScreen({super.key});
  @override
  ConsumerState<SimpleMoreScreen> createState() => _SimpleMoreScreenState();
}

class _SimpleMoreScreenState extends ConsumerState<SimpleMoreScreen> {
  @override
  void initState() {
    super.initState();
    final s = ref.read(sessionProvider);
    if (s != null) unawaited(ServerCapabilities.refreshIfStale(ref.read(apiProvider), ref.read(databaseProvider), OutboxMeta.ownerOf(s)));
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final mode = ref.watch(modeProfileProvider);
    final lang = Localizations.localeOf(context).languageCode;
    final session = ref.watch(sessionProvider);
    Widget row(String key, IconData icon, String title, String? sub, VoidCallback onTap) => Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Material(
        color: pal.panel,
        borderRadius: BorderRadius.circular(16),
        child: InkWell(
          key: Key(key),
          borderRadius: BorderRadius.circular(16),
          onTap: onTap,
          child: Container(
            constraints: const BoxConstraints(minHeight: 64),
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(16),
              border: Border.all(color: pal.border),
            ),
            child: Row(
              children: [
                Icon(icon, size: 28),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
                      if (sub != null && sub.isNotEmpty) Text(sub, style: TextStyle(fontSize: 13, color: pal.textSoft)),
                    ],
                  ),
                ),
                Icon(Icons.chevron_right_rounded, color: pal.textSoft),
              ],
            ),
          ),
        ),
      ),
    );
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
      children: [
        Text(context.t5('Ещё', 'Дагы', 'Yana', 'More', 'Daha'), style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
        const SizedBox(height: 10),
        row(
          'moreStock',
          Icons.inventory_2_outlined,
          context.l.productsTitle,
          context.t5('Остатки, цены, фото', 'Калдыктар, баалар, сүрөттөр', 'Qoldiqlar, narxlar, rasmlar', 'Stock, prices, photos', 'Stok, fiyatlar, fotoğraflar'),
          () => context.push('/stock'),
        ),
        row(
          'moreDebts',
          Icons.person_outline_rounded,
          context.t5('Должны мне', 'Мага карыз', 'Menga qarzdor', 'Owed to me', 'Bana borçlu'),
          context.t5(
            'Список и напоминание в WhatsApp',
            'Тизме жана WhatsApp эскертүүсү',
            "Ro'yxat va WhatsApp eslatmasi",
            'List and a WhatsApp reminder',
            'Liste ve WhatsApp hatırlatması',
          ),
          () => context.push('/debts'),
        ),
        row('moreChecks', Icons.receipt_long_outlined, context.l.tabChecks, null, () => context.push('/checks')),
        row(
          'moreSync',
          Icons.cloud_upload_outlined,
          context.t5('Не отправлено', 'Жөнөтүлгөн жок', 'Yuborilmagan', 'Not sent', 'Gönderilmedi'),
          context.t5(
            'Что ещё не ушло на сервер',
            'Серверге эмне жөнөтүлө элек',
            'Serverga nima hali yuborilmagan',
            'What has not reached the server yet',
            'Sunucuya henüz ne gitmedi',
          ),
          () => context.push('/sync'),
        ),
        row('moreSettings', Icons.settings_outlined, context.l.profileTitle, session?.userName, () => context.push('/profile')),
        const SizedBox(height: 8),
        if (mode.plan.present && mode.plan.code.isNotEmpty)
          Container(
            key: const Key('morePlan'),
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: pal.soft,
              borderRadius: BorderRadius.circular(16),
              border: Border.all(color: pal.border),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('${context.t5('Тариф', 'Тариф', 'Tarif', 'Plan', 'Tarife')}: ${mode.plan.nameFor(lang)}', style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
                if (mode.plan.isMini) ...[
                  const SizedBox(height: 4),
                  Text(
                    context.t5(
                      'Нужен второй продавец или касса на компьютере?',
                      'Экинчи сатуучу же компьютерде касса керекпи?',
                      'Ikkinchi sotuvchi yoki kompyuterda kassa kerakmi?',
                      'Need a second seller or a computer register?',
                      'İkinci satıcı veya bilgisayarda kasa mı gerekli?',
                    ),
                    style: TextStyle(color: pal.textSoft, fontSize: 13.5),
                  ),
                  TextButton(
                    key: const Key('moreUpgrade'),
                    style: TextButton.styleFrom(minimumSize: const Size(64, 48), padding: EdgeInsets.zero, alignment: Alignment.centerLeft),
                    onPressed: () => showPlanLimitDialog(context, PlanLimitEvent(code: 'plan_limit', limit: 'max_staff', upgradePlan: mode.plan.upgradePlan)),
                    child: Text(
                      context.t5('Что даёт тариф выше', 'Жогорку тариф эмне берет', 'Yuqori tarif nima beradi', 'What a higher plan gives', 'Üst tarife ne sağlar'),
                      style: const TextStyle(fontWeight: FontWeight.w800),
                    ),
                  ),
                ],
              ],
            ),
          ),
        if (mode.canLeaveSimple) ...[
          const SizedBox(height: 8),
          SwitchListTile(
            key: const Key('moreLeaveSimple'),
            contentPadding: const EdgeInsets.symmetric(horizontal: 4),
            title: Text(context.t5('Простой режим', 'Жөнөкөй режим', 'Oddiy rejim', 'Simple mode', 'Basit mod'), style: const TextStyle(fontWeight: FontWeight.w800)),
            subtitle: Text(
              context.t5(
                'Выключите, чтобы вернуть обычный вид',
                'Кадимки түрүнө кайтуу үчүн өчүрүңүз',
                "Oddiy ko'rinishga qaytish uchun o'chiring",
                'Turn off to return to the usual view',
                'Olağan görünüme dönmek için kapatın',
              ),
            ),
            value: true,
            onChanged: (v) => ref.read(simpleModeProvider.notifier).set(v),
          ),
        ],
      ],
    );
  }
}
