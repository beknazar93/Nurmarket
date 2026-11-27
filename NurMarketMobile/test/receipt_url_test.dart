import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/payment/receipt_qr.dart';

void main() {
  // 2026-11-26: QR ведёт на красивую страницу чека (share_url), а не на старый PDF check.nurcrm.kg (подробнее — receipt_share_link_test.dart)
  test('QR чека ведёт на страницу share_url', () {
    expect(eReceiptUrl({'share_url': 'https://app.nurcrm.kg/r/abc'}), 'https://app.nurcrm.kg/r/abc');
    expect(eReceiptUrl({'e_receipt_url': 'https://check.nurcrm.kg/abc'}), isNull);
    expect(eReceiptUrl({'e_receipt': {'token': 'xyz'}}), isNull);
    expect(eReceiptUrl({'id': '1'}), isNull);
    expect(eReceiptUrl(null), isNull);
  });
}
