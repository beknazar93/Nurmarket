// 2026-10-27 (мобилка, видеозвонок, М2): рабочая реализация [WizardBackend] мастера «добавить товар» в звонке — те же
// репозитории и тот же порядок, что у экрана «Новый товар» (new_item.dart `_save` / `_afterCreate`): карточка через
// AdminReceiveRepository.createProduct, остаток PATCH quantity, фото в карточку (первое — главное), эталоны умной камеры
// (ProductRefs), обновление каталога телефона. Справочник NurCRM — тот же `globalInfo`, название по фото — тот же ИИ-распознаватель,
// что у «Что это?». Новых запросов к серверу нет. Не проверяется юнит-тестами сети (в тестах мастер идёт на моке [WizardBackend]).
import 'dart:typed_data';

import '../../core/ai/assistant/call_product_search.dart' show searchForCall;
import '../../core/ai/assistant/product_wizard.dart';
import '../../core/ai/product_refs.dart';
import '../../core/ai/vision.dart';
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../admin/admin_receive.dart' show AdminReceiveRepository, ReceiveLine, adminReceiveRepoProvider;
import '../scan/smart_camera.dart' show RefCache;
import '../../core/ai/assistant/read_tools.dart' show Reader;

class RepoWizardBackend implements WizardBackend {
  final Reader read;
  RepoWizardBackend(this.read);

  /// Прошлое создание этого же товара осталось без ответа (обрыв): перед повтором ищем его, а не создаём второй.
  String? _unknownSig;

  // 2026-10-27 (ревью М-9): товар создан, но мастер ещё не завершён (остаток / фото) — id для повторной попытки без второго создания
  String? _createdSig;
  String? _createdId;

  @override
  Future<({String name, String? category, String? brand})?> globalInfo(String code) => read(adminReceiveRepoProvider).globalInfo(code);

  @override
  Future<String?> nameFromPhoto(Uint8List jpeg) async {
    final seen = await read(visionProvider).recognize(jpeg, mode: 'sale');
    for (final s in seen) {
      // по-русски («Спрайт 1 л»), как назвал бы продавец; нет — как на упаковке
      final t = (s.titleRu.trim().isNotEmpty ? s.titleRu : s.title).trim();
      if (t.isNotEmpty) return t;
    }
    return null;
  }

  @override
  Future<String> create(WizardDraft d) async {
    final repo = read(adminReceiveRepoProvider);
    final api = read(apiProvider);
    final db = read(databaseProvider);
    final sig = '${d.name.toLowerCase()}|${d.barcode}';
    String? id;
    // 2026-10-27 (ревью М-9): товар уже создан в прошлой попытке, а остаток записать не вышло — второй раз не создаём
    if (_createdSig == sig) id = _createdId;
    if (id == null && _unknownSig == sig) {
      // повтор после обрыва: товар мог создаться — обновляем каталог и ищем по штрихкоду / точному названию
      try {
        await read(catalogRepoProvider).sync();
      } catch (_) {}
      if (d.barcode.isNotEmpty) id = (await db.productByScan(d.barcode, allowWeightLabel: false))?.product.id;
      id ??= (await searchForCall(db, d.name, top: 3)).where((h) => h.product.name.trim().toLowerCase() == d.name.toLowerCase()).firstOrNull?.product.id;
    }
    if (id == null) {
      final line = ReceiveLine(productId: '', name: d.name, barcode: d.barcode, unit: d.unit, cost: d.purchase, price: d.price, qty: 0, isNew: true);
      final weight = d.unit == 'кг';
      final plu = weight ? await repo.nextPluOr('') : null;
      try {
        id = await repo.createProduct(line, category: d.category, brand: d.brand, isWeight: weight, plu: plu);
      } on ApiException catch (e) {
        if (AdminReceiveRepository.outcomeUnknown(e)) _unknownSig = sig;
        rethrow;
      }
    }
    _unknownSig = null;
    _createdSig = sig;
    _createdId = id;
    if (d.qty > 0) await api.patch('api/main/products/$id/', body: {'quantity': d.qty});
    // фото: спереди — главное; остальные — галерея (по ним же другие телефоны узнают товар, RefCache.remote)
    for (var i = 0; i < d.shots.length; i++) {
      try {
        await repo.uploadImage(id, d.shots[i], primary: i == 0);
      } catch (_) {
        // фото не загрузилось — товар создан, фото добавят из карточки
      }
    }
    // эталоны умной камеры на этом телефоне (до 3 снимков)
    try {
      await ProductRefs.save(db, id, d.shots);
      RefCache.forget(id);
    } catch (_) {}
    try {
      await read(catalogRepoProvider).sync();
    } catch (_) {}
    _createdSig = null;
    _createdId = null;
    return id;
  }

  @override
  Future<int> teach(Product p, List<Uint8List> shots) async {
    final db = read(databaseProvider);
    final repo = read(adminReceiveRepoProvider);
    final used = shots.take(ProductRefs.maxPerProduct).toList();
    await ProductRefs.save(db, p.id, used);
    RefCache.forget(p.id);
    for (var i = 0; i < used.length; i++) {
      try {
        await repo.uploadImage(p.id, await ProductRefs.compact(used[i]), primary: i == 0 && p.imageUrl == null);
      } catch (_) {}
    }
    return used.length;
  }
}
