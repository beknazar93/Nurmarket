import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';

// 13.10: умная камера — ответ ИИ (JSON, иногда с текстом вокруг) превращается в список товаров.
void main() {
  test('json with surrounding text', () {
    final items = VisionService.parseSeen('Вот: [{"name":"Кока-Кола","brand":"Coca-Cola","size":"2 л","barcode":"5449 000 000996","count":2,"label_price":"98,5"}] готово');
    expect(items.length, 1);
    expect(items.first.barcode, '5449000000996');
    expect(items.first.count, 2);
    expect(items.first.labelPrice, 98.5);
    expect(items.first.title, 'Coca-Cola Кока-Кола 2 л');
  });
  test('skips empty names, count defaults to 1', () {
    final items = VisionService.parseSeen('[{"name":""},{"name":"Mars"}]');
    expect(items.single.name, 'Mars');
    expect(items.single.count, 1);
  });
}
