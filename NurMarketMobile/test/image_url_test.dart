import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';

// 13.10: фото товара — из списка images (как отдаёт NurCRM), вложенного объекта и относительного адреса.
void main() {
  test('images list of objects', () {
    final url = CatalogRepository.productImageUrl({
      'images': [
        {'image': 'https://app.nurcrm.kg/media/products/a/b.webp'},
      ],
    });
    expect(url, 'https://app.nurcrm.kg/media/products/a/b.webp');
  });
  test('relative path and jpg preferred over webp', () {
    final url = CatalogRepository.productImageUrl({
      'image': '/media/x.webp',
      'primary_image': {'url': 'media/y.jpg'},
    });
    expect(url, 'https://app.nurcrm.kg/media/y.jpg');
  });
  test('no image', () {
    expect(CatalogRepository.productImageUrl({'name': 'x'}), isNull);
  });
}
