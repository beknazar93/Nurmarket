import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/util/audit.dart';
import '../../core/util/barcode_generator.dart';
import '../../core/util/price_limits.dart';
import '../../core/util/ui.dart';
import '../admin/admin_receive.dart';
import '../admin/new_item.dart' show pickPhoto;
import '../sale/cart.dart' show roundMoney;
import 'simple_sheets.dart';

/// 2026-11-20 (Простой режим, М9/М22): «Новый товар» за ~10 секунд — фото (камера), название, цена, количество. Больше ничего.
/// Сервер принимает товар с одним названием и ценой (ответ Умара 20.11: `products/create-manual/`, штрихкод и категория пустые),
/// фото — отдельным запросом `products/{id}/images/`, остаток — как в обычной карточке (PATCH quantity). Штрихкод берётся свой внутренний
/// (тот же генератор, что у кнопки «Сгенерировать» в обычной карточке), чтобы товар можно было принять сканом; не вышло — без штрихкода.
/// true — товар создан.
Future<bool?> showQuickProduct(BuildContext context, {String? name, String? barcode}) => showModalBottomSheet<bool>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (_) => QuickProductSheet(initialName: name, barcode: barcode),
);

class QuickProductSheet extends ConsumerStatefulWidget {
  final String? initialName;

  /// Штрихкод, который отсканировали и не нашли («Приход»): товар заводится с ним, а не со своим.
  final String? barcode;

  /// Только для проверок без камеры: «снимок».
  @visibleForTesting
  final Future<Uint8List?> Function()? debugPhoto;
  const QuickProductSheet({super.key, this.initialName, this.barcode, this.debugPhoto});
  @override
  ConsumerState<QuickProductSheet> createState() => _QuickProductSheetState();
}

class _QuickProductSheetState extends ConsumerState<QuickProductSheet> {
  late final _name = TextEditingController(text: widget.initialName ?? '');
  final _price = TextEditingController();
  final _qty = TextEditingController(text: '1');
  Uint8List? _photo;
  bool _busy = false;
  bool _submitted = false;
  String? _createdId; // товар уже создан — повтор не создаёт второй, а доделывает фото и остаток
  bool _photoDone = false;
  bool _stockDone = false;

  @override
  void dispose() {
    _name.dispose();
    _price.dispose();
    _qty.dispose();
    super.dispose();
  }

  Future<void> _takePhoto() async {
    try {
      final b = await (widget.debugPhoto?.call() ?? pickPhoto(ImageSource.camera));
      if (b != null && mounted) setState(() => _photo = b);
    } catch (_) {
      if (mounted) toast(context, context.t5('Камера недоступна', 'Камера жеткиликсиз', 'Kamera mavjud emas', 'Camera unavailable', 'Kamera kullanılamıyor'), error: true);
    }
  }

  Future<void> _save() async {
    if (_busy) return;
    final l = context.l;
    final name = _name.text.trim();
    final price = parseNum(_price.text) ?? 0;
    final qty = parseNum(_qty.text) ?? 0;
    if (name.isEmpty || price <= 0) {
      setState(() => _submitted = true);
      toast(context, name.isEmpty ? l.peNameNeed : l.nuPriceNeed, error: true);
      return;
    }
    final limit = PriceLimits.problem(context, sale: price, wholesale: null, purchase: 0);
    if (limit != null) {
      toast(context, limit, error: true);
      return;
    }
    setState(() => _busy = true);
    final repo = ref.read(adminReceiveRepoProvider);
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    final lang = Localizations.localeOf(context).languageCode;
    final photoFailText = switch (lang) {
      'ky' => 'Товар сакталды, бирок сүрөт жүктөлгөн жок',
      'uz' => 'Mahsulot saqlandi, lekin rasm yuklanmadi',
      'en' => 'The product is saved, but the photo did not upload',
      'tr' => 'Ürün kaydedildi, ancak fotoğraf yüklenemedi',
      _ => 'Товар сохранён, но фото не загрузилось',
    };
    final navigator = Navigator.of(context);
    final messenger = ScaffoldMessenger.maybeOf(context);
    final savedText = l.productCreated;
    try {
      if (_createdId == null) {
        var code = (widget.barcode ?? '').trim();
        if (code.isEmpty) {
          try {
            code = await BarcodeGenerator.createFree((c) async => await db.productByBarcode(c) != null);
          } catch (_) {
            code = ''; // без штрихкода — сервер принимает
          }
        }
        final line = ReceiveLine(productId: '', name: name, barcode: code, unit: 'шт', cost: 0, price: roundMoney(price), qty: 0, isNew: true);
        _createdId = await repo.createProduct(line);
      }
      final id = _createdId!;
      final errors = <String>[];
      if (_photo != null && !_photoDone) {
        try {
          await repo.uploadImage(id, _photo!, primary: true);
          _photoDone = true;
        } on ApiException {
          errors.add(photoFailText);
        }
      }
      if (qty > 0 && !_stockDone) {
        try {
          await api.patch('api/main/products/$id/', body: {'quantity': qty});
          _stockDone = true;
        } on ApiException catch (e) {
          errors.add(e.message);
        }
      }
      try {
        await ref.read(catalogRepoProvider).sync();
      } catch (_) {
        // товар уже создан — сбой обновления каталога не ошибка сохранения
      }
      Audit.log('product', '${l.productCreated}: $name');
      messenger?.showSnackBar(SnackBar(content: Text(errors.isEmpty ? savedText : errors.first)));
      if (errors.isEmpty) {
        if (navigator.mounted) navigator.pop(true);
      } else if (mounted) {
        setState(() => _busy = false); // что-то не доехало — «Сохранить» повторит только недостающее
      }
    } on ApiException catch (e) {
      if (mounted) {
        setState(() => _busy = false);
        toast(
          context,
          AdminReceiveRepository.outcomeUnknown(e)
              ? '${e.message}\n${context.t5('Проверьте список товаров, прежде чем повторять', 'Кайталоодон мурун товарлар тизмесин текшериңиз', 'Takrorlashdan oldin mahsulotlar ro\'yxatini tekshiring', 'Check the product list before retrying', 'Tekrarlamadan önce ürün listesini kontrol edin')}'
              : e.message,
          error: true,
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final l = context.l;
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: PinnedSheet(
        footer: FilledButton(
          key: const Key('quickSave'),
          style: bigButton(min: const Size.fromHeight(60)),
          onPressed: _busy ? null : _save,
          child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.save),
        ),
        children: [
          Text(context.t5('Новый товар', 'Жаңы товар', 'Yangi mahsulot', 'New product', 'Yeni ürün'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
          const SizedBox(height: 10),
          InkWell(
            key: const Key('quickPhoto'),
            borderRadius: BorderRadius.circular(16),
            onTap: _busy ? null : _takePhoto,
            child: Container(
              height: 120,
              decoration: BoxDecoration(
                color: pal.soft,
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: pal.borderStrong),
              ),
              clipBehavior: Clip.antiAlias,
              child: _photo != null
                  ? Image.memory(_photo!, fit: BoxFit.cover, cacheWidth: 600)
                  : Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Icon(Icons.photo_camera_outlined, size: 40, color: pal.textSoft),
                        Text(
                          context.t5('Сфотографировать', 'Сүрөткө тартуу', 'Suratga olish', 'Take a photo', 'Fotoğraf çek'),
                          style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w700),
                        ),
                      ],
                    ),
            ),
          ),
          const SizedBox(height: 10),
          TextField(
            key: const Key('quickName'),
            controller: _name,
            enabled: _createdId == null,
            textCapitalization: TextCapitalization.sentences,
            textInputAction: TextInputAction.next,
            style: const TextStyle(fontSize: 17),
            decoration: InputDecoration(
              labelText: context.t5('Название', 'Аталышы', 'Nomi', 'Name', 'Ad'),
              errorText: _submitted && _name.text.trim().isEmpty ? l.peNameNeed : null,
            ),
          ),
          const SizedBox(height: 10),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                flex: 3,
                child: TextField(
                  key: const Key('quickPrice'),
                  controller: _price,
                  enabled: _createdId == null,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  inputFormatters: amountFormatters,
                  textInputAction: TextInputAction.next,
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800),
                  decoration: InputDecoration(labelText: l.price, suffixText: l.som, errorText: _submitted && (parseNum(_price.text) ?? 0) <= 0 ? l.nuPriceNeed : null),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                flex: 2,
                child: TextField(
                  key: const Key('quickQty'),
                  controller: _qty,
                  enabled: !_stockDone,
                  keyboardType: const TextInputType.numberWithOptions(decimal: false),
                  inputFormatters: amountFormatters,
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800),
                  decoration: InputDecoration(labelText: context.t5('Кол-во', 'Саны', 'Soni', 'Qty', 'Adet')),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
