import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/admin/waybill_pdf.dart';

/// Сумма прописью в накладной (макет Д5) — как на десктопе (CrmDocuments.AmountInWords).
void main() {
  test('сумма прописью — целые сомы и тыйыны', () {
    expect(amountInWords(7886), 'Семь тысяч восемьсот восемьдесят шесть сом 00 тыйын');
    expect(amountInWords(21.5), 'Двадцать один сом 50 тыйын');
    expect(amountInWords(1002000), 'Один миллион две тысячи сом 00 тыйын');
    expect(amountInWords(0), 'Ноль сом 00 тыйын');
    expect(amountInWords(11), 'Одиннадцать сом 00 тыйын');
  });
}
