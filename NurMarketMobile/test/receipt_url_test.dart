import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/payment/receipt_qr.dart';

void main() {
  test('QR чека ведёт на PDF', () {
    expect(eReceiptUrl({'e_receipt_pdf_url': 'https://check.nurcrm.kg/abc.pdf'}), 'https://check.nurcrm.kg/abc.pdf');
    expect(eReceiptUrl({'e_receipt_url': 'https://check.nurcrm.kg/abc'}), 'https://check.nurcrm.kg/abc.pdf');
    expect(eReceiptUrl({'e_receipt_url': 'https://check.nurcrm.kg/abc/'}), 'https://check.nurcrm.kg/abc.pdf');
    expect(eReceiptUrl({'e_receipt': {'token': 'xyz'}}), 'https://check.nurcrm.kg/xyz.pdf');
    expect(eReceiptUrl({'e_receipt_token': 'xyz'}), 'https://check.nurcrm.kg/xyz.pdf');
    expect(eReceiptUrl({'id': '1'}), isNull);
    expect(eReceiptUrl(null), isNull);
  });
}
