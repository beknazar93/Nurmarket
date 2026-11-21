import 'package:flutter/material.dart';

import '../../features/admin/admin_screens.dart' show openWhatsApp;
import '../util/ui.dart';
import 'plan_limit_event.dart';

export 'plan_limit_event.dart';

/// 2026-11-20 (тариф «Мини», раздел 4 ТЗ): сервер отвечает 403 `plan_limit` {limit, max, current, upgrade_plan, detail} или
/// `plan_client_not_allowed` {allowed_clients} (и `plan_feature_required`). Приложение превращает это в окно «Доступно на тарифе «Старт»»
/// с кнопкой «Перейти на Старт» (WhatsApp поддержки). Введённое не стирается, продажа не блокируется — окно только сообщает.

String planDisplayName(BuildContext context, String code) => switch (code) {
  'mini' => context.t5('Мини', 'Мини', 'Mini', 'Mini', 'Mini'),
  'standard' => context.t5('Стандарт', 'Стандарт', 'Standart', 'Standard', 'Standart'),
  'custom' => context.t5('Своё', 'Өзүңүз', 'Maxsus', 'Custom', 'Özel'),
  _ => context.t5('Старт', 'Старт', 'Start', 'Start', 'Başlangıç'),
};

String _fallbackText(BuildContext c, PlanLimitEvent e) {
  if (e.code == 'plan_client_not_allowed') {
    return c.t5(
      'Ваш тариф работает только в телефоне. Для компьютера нужен более высокий тариф.',
      'Тарифиңиз телефондо гана иштейт. Компьютер үчүн жогорураак тариф керек.',
      'Sizning tarifingiz faqat telefonda ishlaydi. Kompyuter uchun yuqoriroq tarif kerak.',
      'Your plan works on the phone only. A computer needs a higher plan.',
      'Tarifiniz yalnızca telefonda çalışır. Bilgisayar için daha yüksek tarife gerekir.',
    );
  }
  if (e.limit == 'max_staff') {
    return c.t5(
      'На вашем тарифе один пользователь — владелец. Для сотрудников нужен более высокий тариф.',
      'Тарифиңизде бир колдонуучу — ээси. Кызматкерлер үчүн жогорураак тариф керек.',
      'Tarifingizda bitta foydalanuvchi — egasi. Xodimlar uchun yuqoriroq tarif kerak.',
      'Your plan has one user: the owner. Employees need a higher plan.',
      'Tarifinizde tek kullanıcı var: sahip. Çalışanlar için daha yüksek tarife gerekir.',
    );
  }
  if (e.limit == 'max_cashboxes') {
    return c.t5(
      'На вашем тарифе одна касса. Для второй кассы нужен более высокий тариф.',
      'Тарифиңизде бир касса. Экинчи касса үчүн жогорураак тариф керек.',
      'Tarifingizda bitta kassa. Ikkinchi kassa uchun yuqoriroq tarif kerak.',
      'Your plan has one cash register. A second one needs a higher plan.',
      'Tarifinizde tek kasa var. İkinci kasa için daha yüksek tarife gerekir.',
    );
  }
  return c.t5(
    'Эта возможность недоступна на вашем тарифе.',
    'Бул мүмкүнчүлүк тарифиңизде жок.',
    'Bu imkoniyat tarifingizda mavjud emas.',
    'This feature is not available on your plan.',
    'Bu özellik tarifenizde yok.',
  );
}

bool _open = false;

/// Окно «Доступно на тарифе «Старт»». Не показывается второй раз, пока открыто первое. Возвращает, нажали ли «Перейти».
Future<bool> showPlanLimitDialog(BuildContext context, PlanLimitEvent e) async {
  if (_open) return false;
  _open = true;
  try {
    final plan = planDisplayName(context, e.upgradePlan.isEmpty ? 'start' : e.upgradePlan);
    final text = e.detail.isNotEmpty ? e.detail : _fallbackText(context, e);
    final go = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        key: const Key('planLimitDialog'),
        title: Text(c.t5('Доступно на тарифе «$plan»', '«$plan» тарифинде жеткиликтүү', '«$plan» tarifida mavjud', 'Available on the “$plan” plan', '«$plan» tarifesinde mevcut')),
        scrollable: true,
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(text),
            const SizedBox(height: 10),
            Text(
              c.t5(
                'Поддержка в WhatsApp: $supportWhatsApp',
                'WhatsApp колдоо: $supportWhatsApp',
                'WhatsApp yordam: $supportWhatsApp',
                'WhatsApp support: $supportWhatsApp',
                'WhatsApp destek: $supportWhatsApp',
              ),
              style: TextStyle(color: c.pal.textSoft, fontSize: 13),
            ),
          ],
        ),
        actions: [
          TextButton(
            style: TextButton.styleFrom(minimumSize: const Size(64, 48)),
            onPressed: () => Navigator.pop(c, false),
            child: Text(c.t5('Позже', 'Кийинчерээк', 'Keyinroq', 'Later', 'Sonra')),
          ),
          FilledButton(
            key: const Key('planLimitGo'),
            style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
            onPressed: () => Navigator.pop(c, true),
            child: Text(c.t5('Перейти на «$plan»', '«$plan» тарифине өтүү', '«$plan» tarifiga o\'tish', 'Switch to “$plan”', '«$plan» tarifesine geç')),
          ),
        ],
      ),
    );
    if (go == true) {
      try {
        await openWhatsApp(supportWhatsApp, 'Здравствуйте! Хочу перейти на тариф «$plan» (NurCRM).');
      } catch (_) {
        // WhatsApp не открылся — номер виден в окне
      }
    }
    return go == true;
  } finally {
    _open = false;
  }
}
