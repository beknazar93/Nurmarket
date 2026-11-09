import 'package:flutter/material.dart';

import '../admin/admin_screens.dart' show openWhatsApp;

/// 2026-11-08 (владелец: «вернуть кнопку WA»): напоминание о долге в WhatsApp из списка «Долги». Как в кассе: номер → 996…,
/// готовый текст на языке приложения (ru/ky/en/tr/uz), чат открывается только по нажатию — «Отправить» жмёт человек.

/// Международные цифры для wa.me: «0700 123 456» / «+996 700 123 456» / «700123456» → 996700123456; нет номера — null.
String? debtWhatsAppDigits(String phone) {
  var d = phone.replaceAll(RegExp(r'\D'), '');
  if (d.startsWith('00')) d = d.substring(2);
  if (d.length == 10 && d.startsWith('0')) d = '996${d.substring(1)}';
  if (d.length == 9) d = '996$d';
  if (d.length == 13 && d.startsWith('9960')) d = '996${d.substring(4)}';
  if (d.length == 12 && d.startsWith('996')) return d;
  // Чужой номер — только если записан с «+» и длина разумная.
  if (phone.trim().startsWith('+') && d.length >= 10 && d.length <= 15 && !d.startsWith('0')) return d;
  return null;
}

String _amountText(double v) {
  final whole = (v - v.roundToDouble()).abs() < 0.005;
  final s = whole ? v.round().toString() : v.toStringAsFixed(2).replaceAll('.', ',');
  final parts = s.split(',');
  final digits = parts[0];
  final b = StringBuffer();
  for (var i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 == 0) b.write(' ');
    b.write(digits[i]);
  }
  return parts.length > 1 ? '$b,${parts[1]}' : b.toString();
}

/// Текст напоминания: имя, сумма, магазин (пусто — «наш магазин»), с какого числа (если известно).
String debtReminderText(String lang, {required String name, required double amount, String shop = '', DateTime? since}) {
  final n = name.trim();
  final sh = shop.trim();
  final a = _amountText(amount);
  String d2(int x) => x.toString().padLeft(2, '0');
  final s = since == null ? null : '${d2(since.day)}.${d2(since.month)}.${since.year}';
  switch (lang) {
    case 'ky':
      return '${n.isEmpty ? 'Саламатсызбы!' : 'Саламатсызбы, $n!'} ${sh.isEmpty ? 'Дүкөнүбүздөгү' : '«$sh» дүкөнүндөгү'} карызыңызды эскертебиз: $a сом.${s == null ? '' : ' Карыз $s баштап.'} Рахмат!';
    case 'en':
      return '${n.isEmpty ? 'Hello!' : 'Hello, $n!'} A reminder about your debt at ${sh.isEmpty ? 'our shop' : '“$sh”'}: $a som.${s == null ? '' : ' Owed since $s.'} Thank you!';
    case 'tr':
      return '${n.isEmpty ? 'Merhaba!' : 'Merhaba $n!'} ${sh.isEmpty ? 'Mağazamızdaki' : '«$sh» mağazasındaki'} borcunuzu hatırlatırız: $a som.${s == null ? '' : ' Borç $s tarihinden beri.'} Teşekkürler!';
    case 'uz':
      return '${n.isEmpty ? 'Assalomu alaykum!' : 'Assalomu alaykum, $n!'} ${sh.isEmpty ? 'Do\'konimizdagi' : '«$sh» do\'konidagi'} qarzingizni eslatamiz: $a so\'m.${s == null ? '' : ' Qarz $s dan beri.'} Rahmat!';
    default:
      return '${n.isEmpty ? 'Здравствуйте!' : 'Здравствуйте, $n!'} Напоминаем о долге ${sh.isEmpty ? 'в нашем магазине' : 'в магазине «$sh»'}: $a сом.${s == null ? '' : ' Долг с $s.'} Спасибо!';
  }
}

String _byLang(String lang, String ru, String ky, String en, String tr, String uz) => switch (lang) {
  'ky' => ky,
  'en' => en,
  'tr' => tr,
  'uz' => uz,
  _ => ru,
};

/// Кнопка «WA» в строке должника: нет номера — неактивна, причина в подсказке.
class DebtWhatsAppButton extends StatelessWidget {
  final String phone;
  final String name;
  final double amount;
  final String shop;
  const DebtWhatsAppButton({super.key, required this.phone, required this.name, required this.amount, this.shop = ''});

  @override
  Widget build(BuildContext context) {
    final lang = Localizations.localeOf(context).languageCode;
    final digits = debtWhatsAppDigits(phone);
    final tip = digits != null
        ? _byLang(lang, 'Напомнить о долге в WhatsApp', 'Карызды WhatsApp\'та эскертүү', 'Remind about the debt on WhatsApp', 'Borcu WhatsApp\'tan hatırlat', 'Qarzni WhatsApp\'da eslatish')
        : _byLang(lang, 'Нет номера телефона — WhatsApp недоступен', 'Телефон номери жок — WhatsApp жеткиликсиз', 'No phone number — WhatsApp unavailable', 'Telefon numarası yok — WhatsApp kullanılamaz', 'Telefon raqami yo\'q — WhatsApp mavjud emas');
    return Tooltip(
      message: tip,
      child: Semantics(
        button: true,
        label: 'WhatsApp — $name',
        child: OutlinedButton.icon(
          style: OutlinedButton.styleFrom(minimumSize: const Size(64, 48), padding: const EdgeInsets.symmetric(horizontal: 10)),
          onPressed: digits == null ? null : () => openWhatsApp(digits, debtReminderText(lang, name: name, amount: amount, shop: shop)),
          icon: const Icon(Icons.chat_rounded, size: 18, color: Color(0xFF25D366)),
          label: const Text('WA', style: TextStyle(fontWeight: FontWeight.w700)),
        ),
      ),
    );
  }
}
