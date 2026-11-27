import 'package:flutter/material.dart';

/// 2026-11-27 (Маркетплейс мобилки; правило владельца 22.11: у каждой функции своя иконка, запасной «пазл» недопустим): значки модулей.
/// Касса хранит ключи значков Material Design Icons (MarketplaceIcons.cs) — здесь им подобраны аналоги из `Icons` Flutter. Новый ключ в тексте кассы = строка тут;
/// ключ не найден — «плюс в квадрате» (как у кассы), не пазл. Тест marketplace_test.dart проверяет, что у каждого ключа из content.json есть значок.
const mdiToMaterial = <String, IconData>{
  'account-clock-outline': Icons.history_toggle_off,
  'account-group-outline': Icons.groups_outlined,
  'account-voice': Icons.record_voice_over_outlined,
  'barcode': Icons.view_week_outlined,
  'barcode-scan': Icons.qr_code_scanner,
  'bell-outline': Icons.notifications_none_rounded,
  'book-account-outline': Icons.contacts_outlined,
  'calculator-variant-outline': Icons.calculate_outlined,
  'calendar-clock-outline': Icons.calendar_month_outlined,
  'calendar-range': Icons.date_range_outlined,
  'cart-arrow-down': Icons.add_shopping_cart,
  'cash': Icons.payments_outlined,
  'cash-multiple': Icons.currency_exchange,
  'cellphone-message': Icons.sms_outlined,
  'chart-bar': Icons.bar_chart_rounded,
  'chart-box-outline': Icons.insert_chart_outlined,
  'chart-line': Icons.show_chart,
  'check-circle-outline': Icons.check_circle_outline,
  'clipboard-check-outline': Icons.fact_check_outlined,
  'clock-outline': Icons.schedule,
  'cog-outline': Icons.build_outlined,
  'counter': Icons.pin_outlined,
  'currency-usd': Icons.attach_money,
  'eye-check-outline': Icons.visibility_outlined,
  'eye-outline': Icons.visibility_outlined,
  'file-document-outline': Icons.description_outlined,
  'flash-outline': Icons.bolt_outlined,
  'format-list-numbered': Icons.format_list_numbered,
  'gesture-tap': Icons.touch_app_outlined,
  'history': Icons.history,
  'image-outline': Icons.image_outlined,
  'layers-outline': Icons.layers_outlined,
  'lifebuoy': Icons.support_outlined,
  'lock-outline': Icons.lock_outline,
  'magnify': Icons.search,
  'meat-cleaver': Icons.set_meal_outlined,
  'message-text-outline': Icons.chat_bubble_outline,
  'package-variant-closed': Icons.inventory_2_outlined,
  'percent': Icons.percent,
  'pound': Icons.tag,
  'printer-outline': Icons.print_outlined,
  'receipt-text-outline': Icons.receipt_long_outlined,
  'robot-outline': Icons.smart_toy_outlined,
  'ruler-square': Icons.square_foot,
  'sale': Icons.local_offer_outlined,
  'scale-balance': Icons.balance,
  'send-outline': Icons.send_outlined,
  'shape-outline': Icons.category_outlined,
  'shield-check-outline': Icons.verified_user_outlined,
  'shield-lock-outline': Icons.shield_outlined,
  'star-half-full': Icons.star_half,
  'store-outline': Icons.storefront_outlined,
  'swap-horizontal': Icons.swap_horiz,
  'sync': Icons.sync,
  'tag-multiple-outline': Icons.sell_outlined,
  'tag-text-outline': Icons.label_outline,
  'undo': Icons.undo,
  'vector-square': Icons.aspect_ratio_outlined,
  'view-dashboard-outline': Icons.dashboard_outlined,
  'view-grid-outline': Icons.grid_view,
  'qrcode': Icons.qr_code_2,
  'qrcode-scan': Icons.qr_code_scanner,
  'file-check-outline': Icons.task_outlined,
  'file-excel-outline': Icons.table_chart_outlined,
  'printer-pos': Icons.point_of_sale,
  'database-arrow-right-outline': Icons.move_to_inbox_outlined,
  'file-export-outline': Icons.drive_folder_upload_outlined,
  'plus-box-outline': Icons.add_box_outlined,
};

/// Значок для модулей, которых в кассе ещё нет (или у которых нет ключа в тексте): по коду модуля. Порядок важен: частные раньше общих.
const _byIdPart = <(String, IconData)>[
  ('repair_status', Icons.track_changes_outlined),
  ('seller_bot', Icons.support_agent_outlined),
  ('haggle_bot', Icons.forum_outlined),
  ('bot', Icons.smart_toy_outlined),
  ('esf', Icons.account_balance_outlined),
  ('ekassa', Icons.point_of_sale),
  ('kkm', Icons.point_of_sale),
  ('mailing', Icons.campaign_outlined),
  ('broadcast', Icons.campaign_outlined),
  ('1c', Icons.drive_folder_upload_outlined),
  ('export', Icons.drive_folder_upload_outlined),
  ('qr', Icons.qr_code_2),
  ('pretty', Icons.receipt_long_outlined),
  ('receipt', Icons.receipt_long_outlined),
  ('photo', Icons.photo_camera_outlined),
  ('scan', Icons.photo_camera_outlined),
  ('repair', Icons.build_outlined),
];

/// Значок модуля: ключ из текста кассы → значок по коду → «плюс в квадрате».
IconData moduleIcon(String id, {String key = ''}) {
  final byKey = mdiToMaterial[key];
  if (byKey != null) return byKey;
  final low = id.toLowerCase();
  for (final (part, icon) in _byIdPart) {
    if (low.contains(part)) return icon;
  }
  return Icons.add_box_outlined;
}

/// Цвет плитки значка из «#RRGGBB»; нет — по умолчанию [fallback].
Color moduleColor(String hex, Color fallback) {
  final m = RegExp(r'^#([0-9a-fA-F]{6})$').firstMatch(hex.trim());
  if (m == null) return fallback;
  return Color(0xFF000000 | int.parse(m.group(1)!, radix: 16));
}

/// Цвет значка на цветной плитке: белый или чёрный — тот, что контрастнее (как GlyphColorOn кассы).
Color onTile(Color bg) => ThemeData.estimateBrightnessForColor(bg) == Brightness.dark ? Colors.white : const Color(0xFF191919);
