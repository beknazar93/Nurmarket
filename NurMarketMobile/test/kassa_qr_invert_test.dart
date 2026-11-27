import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;
import 'package:nurmarket/features/profile/kassa_link_screen.dart';

// 2026-11-27 (жалоба «не работает подключение сканера с телефона»): касса до 1.17.97 в тёмной теме рисовала QR инверсным.
// «Сфотографировать QR» при неудаче пробует негатив снимка — проверяем, что негатив действительно светлое ↔ тёмное и файл создаётся.
void main() {
  test('invertedCopy: светлые модули на тёмном → тёмные на светлом', () async {
    final dir = await Directory.systemTemp.createTemp('kassa_qr_inv');
    addTearDown(() => dir.delete(recursive: true));
    final src = img.Image(width: 40, height: 40);
    img.fill(src, color: img.ColorRgb8(37, 37, 37)); // фон тёмной темы
    img.fillRect(src, x1: 10, y1: 10, x2: 29, y2: 29, color: img.ColorRgb8(255, 255, 255)); // «модуль» светлый
    final path = '${dir.path}/shot.png';
    await File(path).writeAsBytes(img.encodePng(src));

    final out = await invertedCopy(path);
    expect(out, isNotNull);
    final neg = img.decodeImage(await File(out!).readAsBytes())!;
    expect(neg.getPixel(20, 20).r, lessThan(40), reason: 'модуль стал тёмным');
    expect(neg.getPixel(2, 2).r, greaterThan(200), reason: 'фон стал светлым');
    await File(out).delete();

    expect(await invertedCopy('${dir.path}/нет-такого.jpg'), isNull);
  });
}
