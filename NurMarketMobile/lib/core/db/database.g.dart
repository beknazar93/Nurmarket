// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'database.dart';

// ignore_for_file: type=lint
class $ProductsTable extends Products with TableInfo<$ProductsTable, Product> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ProductsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<String> id = GeneratedColumn<String>(
    'id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _nameMeta = const VerificationMeta('name');
  @override
  late final GeneratedColumn<String> name = GeneratedColumn<String>(
    'name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _priceMeta = const VerificationMeta('price');
  @override
  late final GeneratedColumn<double> price = GeneratedColumn<double>(
    'price',
    aliasedName,
    false,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _wholesalePriceMeta = const VerificationMeta(
    'wholesalePrice',
  );
  @override
  late final GeneratedColumn<double> wholesalePrice = GeneratedColumn<double>(
    'wholesale_price',
    aliasedName,
    false,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _barcodeMeta = const VerificationMeta(
    'barcode',
  );
  @override
  late final GeneratedColumn<String> barcode = GeneratedColumn<String>(
    'barcode',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _altBarcodesMeta = const VerificationMeta(
    'altBarcodes',
  );
  @override
  late final GeneratedColumn<String> altBarcodes = GeneratedColumn<String>(
    'alt_barcodes',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _skuMeta = const VerificationMeta('sku');
  @override
  late final GeneratedColumn<String> sku = GeneratedColumn<String>(
    'sku',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _pluMeta = const VerificationMeta('plu');
  @override
  late final GeneratedColumn<String> plu = GeneratedColumn<String>(
    'plu',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _unitMeta = const VerificationMeta('unit');
  @override
  late final GeneratedColumn<String> unit = GeneratedColumn<String>(
    'unit',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant('шт'),
  );
  static const VerificationMeta _isWeightMeta = const VerificationMeta(
    'isWeight',
  );
  @override
  late final GeneratedColumn<bool> isWeight = GeneratedColumn<bool>(
    'is_weight',
    aliasedName,
    false,
    type: DriftSqlType.bool,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'CHECK ("is_weight" IN (0, 1))',
    ),
    defaultValue: const Constant(false),
  );
  static const VerificationMeta _isServiceMeta = const VerificationMeta(
    'isService',
  );
  @override
  late final GeneratedColumn<bool> isService = GeneratedColumn<bool>(
    'is_service',
    aliasedName,
    false,
    type: DriftSqlType.bool,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'CHECK ("is_service" IN (0, 1))',
    ),
    defaultValue: const Constant(false),
  );
  static const VerificationMeta _isFavoriteMeta = const VerificationMeta(
    'isFavorite',
  );
  @override
  late final GeneratedColumn<bool> isFavorite = GeneratedColumn<bool>(
    'is_favorite',
    aliasedName,
    false,
    type: DriftSqlType.bool,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'CHECK ("is_favorite" IN (0, 1))',
    ),
    defaultValue: const Constant(false),
  );
  static const VerificationMeta _quantityMeta = const VerificationMeta(
    'quantity',
  );
  @override
  late final GeneratedColumn<double> quantity = GeneratedColumn<double>(
    'quantity',
    aliasedName,
    false,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _categoryMeta = const VerificationMeta(
    'category',
  );
  @override
  late final GeneratedColumn<String> category = GeneratedColumn<String>(
    'category',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _imageUrlMeta = const VerificationMeta(
    'imageUrl',
  );
  @override
  late final GeneratedColumn<String> imageUrl = GeneratedColumn<String>(
    'image_url',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _searchKeyMeta = const VerificationMeta(
    'searchKey',
  );
  @override
  late final GeneratedColumn<String> searchKey = GeneratedColumn<String>(
    'search_key',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _promoRulesMeta = const VerificationMeta(
    'promoRules',
  );
  @override
  late final GeneratedColumn<String> promoRules = GeneratedColumn<String>(
    'promo_rules',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _minQuantityMeta = const VerificationMeta(
    'minQuantity',
  );
  @override
  late final GeneratedColumn<double> minQuantity = GeneratedColumn<double>(
    'min_quantity',
    aliasedName,
    false,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _purchasePriceMeta = const VerificationMeta(
    'purchasePrice',
  );
  @override
  late final GeneratedColumn<double> purchasePrice = GeneratedColumn<double>(
    'purchase_price',
    aliasedName,
    true,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
  );
  @override
  List<GeneratedColumn> get $columns => [
    id,
    name,
    price,
    wholesalePrice,
    barcode,
    altBarcodes,
    sku,
    plu,
    unit,
    isWeight,
    isService,
    isFavorite,
    quantity,
    category,
    imageUrl,
    searchKey,
    promoRules,
    minQuantity,
    purchasePrice,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'products';
  @override
  VerificationContext validateIntegrity(
    Insertable<Product> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    } else if (isInserting) {
      context.missing(_idMeta);
    }
    if (data.containsKey('name')) {
      context.handle(
        _nameMeta,
        name.isAcceptableOrUnknown(data['name']!, _nameMeta),
      );
    } else if (isInserting) {
      context.missing(_nameMeta);
    }
    if (data.containsKey('price')) {
      context.handle(
        _priceMeta,
        price.isAcceptableOrUnknown(data['price']!, _priceMeta),
      );
    }
    if (data.containsKey('wholesale_price')) {
      context.handle(
        _wholesalePriceMeta,
        wholesalePrice.isAcceptableOrUnknown(
          data['wholesale_price']!,
          _wholesalePriceMeta,
        ),
      );
    }
    if (data.containsKey('barcode')) {
      context.handle(
        _barcodeMeta,
        barcode.isAcceptableOrUnknown(data['barcode']!, _barcodeMeta),
      );
    }
    if (data.containsKey('alt_barcodes')) {
      context.handle(
        _altBarcodesMeta,
        altBarcodes.isAcceptableOrUnknown(
          data['alt_barcodes']!,
          _altBarcodesMeta,
        ),
      );
    }
    if (data.containsKey('sku')) {
      context.handle(
        _skuMeta,
        sku.isAcceptableOrUnknown(data['sku']!, _skuMeta),
      );
    }
    if (data.containsKey('plu')) {
      context.handle(
        _pluMeta,
        plu.isAcceptableOrUnknown(data['plu']!, _pluMeta),
      );
    }
    if (data.containsKey('unit')) {
      context.handle(
        _unitMeta,
        unit.isAcceptableOrUnknown(data['unit']!, _unitMeta),
      );
    }
    if (data.containsKey('is_weight')) {
      context.handle(
        _isWeightMeta,
        isWeight.isAcceptableOrUnknown(data['is_weight']!, _isWeightMeta),
      );
    }
    if (data.containsKey('is_service')) {
      context.handle(
        _isServiceMeta,
        isService.isAcceptableOrUnknown(data['is_service']!, _isServiceMeta),
      );
    }
    if (data.containsKey('is_favorite')) {
      context.handle(
        _isFavoriteMeta,
        isFavorite.isAcceptableOrUnknown(data['is_favorite']!, _isFavoriteMeta),
      );
    }
    if (data.containsKey('quantity')) {
      context.handle(
        _quantityMeta,
        quantity.isAcceptableOrUnknown(data['quantity']!, _quantityMeta),
      );
    }
    if (data.containsKey('category')) {
      context.handle(
        _categoryMeta,
        category.isAcceptableOrUnknown(data['category']!, _categoryMeta),
      );
    }
    if (data.containsKey('image_url')) {
      context.handle(
        _imageUrlMeta,
        imageUrl.isAcceptableOrUnknown(data['image_url']!, _imageUrlMeta),
      );
    }
    if (data.containsKey('search_key')) {
      context.handle(
        _searchKeyMeta,
        searchKey.isAcceptableOrUnknown(data['search_key']!, _searchKeyMeta),
      );
    }
    if (data.containsKey('promo_rules')) {
      context.handle(
        _promoRulesMeta,
        promoRules.isAcceptableOrUnknown(data['promo_rules']!, _promoRulesMeta),
      );
    }
    if (data.containsKey('min_quantity')) {
      context.handle(
        _minQuantityMeta,
        minQuantity.isAcceptableOrUnknown(
          data['min_quantity']!,
          _minQuantityMeta,
        ),
      );
    }
    if (data.containsKey('purchase_price')) {
      context.handle(
        _purchasePriceMeta,
        purchasePrice.isAcceptableOrUnknown(
          data['purchase_price']!,
          _purchasePriceMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  Product map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return Product(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}id'],
      )!,
      name: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}name'],
      )!,
      price: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}price'],
      )!,
      wholesalePrice: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}wholesale_price'],
      )!,
      barcode: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}barcode'],
      ),
      altBarcodes: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}alt_barcodes'],
      )!,
      sku: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sku'],
      ),
      plu: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}plu'],
      ),
      unit: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}unit'],
      )!,
      isWeight: attachedDatabase.typeMapping.read(
        DriftSqlType.bool,
        data['${effectivePrefix}is_weight'],
      )!,
      isService: attachedDatabase.typeMapping.read(
        DriftSqlType.bool,
        data['${effectivePrefix}is_service'],
      )!,
      isFavorite: attachedDatabase.typeMapping.read(
        DriftSqlType.bool,
        data['${effectivePrefix}is_favorite'],
      )!,
      quantity: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}quantity'],
      )!,
      category: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}category'],
      )!,
      imageUrl: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}image_url'],
      ),
      searchKey: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}search_key'],
      )!,
      promoRules: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}promo_rules'],
      )!,
      minQuantity: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}min_quantity'],
      )!,
      purchasePrice: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}purchase_price'],
      ),
    );
  }

  @override
  $ProductsTable createAlias(String alias) {
    return $ProductsTable(attachedDatabase, alias);
  }
}

class Product extends DataClass implements Insertable<Product> {
  final String id;
  final String name;
  final double price;

  /// 13.10, владелец: «продажа оптом и в розницу» — оптовая цена товара (wholesale_price сервера); 0 — нет опта.
  final double wholesalePrice;
  final String? barcode;

  /// Доп. штрихкоды через перевод строки.
  final String altBarcodes;
  final String? sku;

  /// 13.10: PLU весов — по нему касса находит товар весовой этикетки (2X…).
  final String? plu;
  final String unit;
  final bool isWeight;
  final bool isService;
  final bool isFavorite;
  final double quantity;
  final String category;
  final String? imageUrl;

  /// 13.10, владелец: «в мобилке тормозит и не видит все данные». Название, артикул, штрихкод и категория в нижнем
  /// регистре — для поиска: lower() SQLite меняет только латиницу, и «мак» не находил «Макфа».
  final String searchKey;

  /// 18.10: акция товара (promotion_rules сервера) — JSON-массив ступеней; пусто — акции нет. Скидку по акции сервер
  /// считает сам и ручную скидку строки у такого товара не принимает — телефон считает так же (cart.dart, promoDiscount).
  final String promoRules;

  /// 2026-10-22 (мобилка, админ): минимальный остаток товара (minimum_quantity сервера). 0 — не задан: «заканчивается»
  /// по общему порогу < 10, как на ПК. Задан — товар заканчивается, когда остаток дошёл до минимума.
  final double minQuantity;

  /// 2026-10-30 (ревизия: потери в деньгах, как на кассе): закупочная цена товара (purchase_price сервера). null — неизвестна
  /// (сервер не прислал или у товара не задана): в ревизии сумма разницы тогда считается по цене продажи с пометкой «*».
  /// Показывается только владельцу и администратору (Session.canSeePurchase).
  final double? purchasePrice;
  const Product({
    required this.id,
    required this.name,
    required this.price,
    required this.wholesalePrice,
    this.barcode,
    required this.altBarcodes,
    this.sku,
    this.plu,
    required this.unit,
    required this.isWeight,
    required this.isService,
    required this.isFavorite,
    required this.quantity,
    required this.category,
    this.imageUrl,
    required this.searchKey,
    required this.promoRules,
    required this.minQuantity,
    this.purchasePrice,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<String>(id);
    map['name'] = Variable<String>(name);
    map['price'] = Variable<double>(price);
    map['wholesale_price'] = Variable<double>(wholesalePrice);
    if (!nullToAbsent || barcode != null) {
      map['barcode'] = Variable<String>(barcode);
    }
    map['alt_barcodes'] = Variable<String>(altBarcodes);
    if (!nullToAbsent || sku != null) {
      map['sku'] = Variable<String>(sku);
    }
    if (!nullToAbsent || plu != null) {
      map['plu'] = Variable<String>(plu);
    }
    map['unit'] = Variable<String>(unit);
    map['is_weight'] = Variable<bool>(isWeight);
    map['is_service'] = Variable<bool>(isService);
    map['is_favorite'] = Variable<bool>(isFavorite);
    map['quantity'] = Variable<double>(quantity);
    map['category'] = Variable<String>(category);
    if (!nullToAbsent || imageUrl != null) {
      map['image_url'] = Variable<String>(imageUrl);
    }
    map['search_key'] = Variable<String>(searchKey);
    map['promo_rules'] = Variable<String>(promoRules);
    map['min_quantity'] = Variable<double>(minQuantity);
    if (!nullToAbsent || purchasePrice != null) {
      map['purchase_price'] = Variable<double>(purchasePrice);
    }
    return map;
  }

  ProductsCompanion toCompanion(bool nullToAbsent) {
    return ProductsCompanion(
      id: Value(id),
      name: Value(name),
      price: Value(price),
      wholesalePrice: Value(wholesalePrice),
      barcode: barcode == null && nullToAbsent
          ? const Value.absent()
          : Value(barcode),
      altBarcodes: Value(altBarcodes),
      sku: sku == null && nullToAbsent ? const Value.absent() : Value(sku),
      plu: plu == null && nullToAbsent ? const Value.absent() : Value(plu),
      unit: Value(unit),
      isWeight: Value(isWeight),
      isService: Value(isService),
      isFavorite: Value(isFavorite),
      quantity: Value(quantity),
      category: Value(category),
      imageUrl: imageUrl == null && nullToAbsent
          ? const Value.absent()
          : Value(imageUrl),
      searchKey: Value(searchKey),
      promoRules: Value(promoRules),
      minQuantity: Value(minQuantity),
      purchasePrice: purchasePrice == null && nullToAbsent
          ? const Value.absent()
          : Value(purchasePrice),
    );
  }

  factory Product.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return Product(
      id: serializer.fromJson<String>(json['id']),
      name: serializer.fromJson<String>(json['name']),
      price: serializer.fromJson<double>(json['price']),
      wholesalePrice: serializer.fromJson<double>(json['wholesalePrice']),
      barcode: serializer.fromJson<String?>(json['barcode']),
      altBarcodes: serializer.fromJson<String>(json['altBarcodes']),
      sku: serializer.fromJson<String?>(json['sku']),
      plu: serializer.fromJson<String?>(json['plu']),
      unit: serializer.fromJson<String>(json['unit']),
      isWeight: serializer.fromJson<bool>(json['isWeight']),
      isService: serializer.fromJson<bool>(json['isService']),
      isFavorite: serializer.fromJson<bool>(json['isFavorite']),
      quantity: serializer.fromJson<double>(json['quantity']),
      category: serializer.fromJson<String>(json['category']),
      imageUrl: serializer.fromJson<String?>(json['imageUrl']),
      searchKey: serializer.fromJson<String>(json['searchKey']),
      promoRules: serializer.fromJson<String>(json['promoRules']),
      minQuantity: serializer.fromJson<double>(json['minQuantity']),
      purchasePrice: serializer.fromJson<double?>(json['purchasePrice']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<String>(id),
      'name': serializer.toJson<String>(name),
      'price': serializer.toJson<double>(price),
      'wholesalePrice': serializer.toJson<double>(wholesalePrice),
      'barcode': serializer.toJson<String?>(barcode),
      'altBarcodes': serializer.toJson<String>(altBarcodes),
      'sku': serializer.toJson<String?>(sku),
      'plu': serializer.toJson<String?>(plu),
      'unit': serializer.toJson<String>(unit),
      'isWeight': serializer.toJson<bool>(isWeight),
      'isService': serializer.toJson<bool>(isService),
      'isFavorite': serializer.toJson<bool>(isFavorite),
      'quantity': serializer.toJson<double>(quantity),
      'category': serializer.toJson<String>(category),
      'imageUrl': serializer.toJson<String?>(imageUrl),
      'searchKey': serializer.toJson<String>(searchKey),
      'promoRules': serializer.toJson<String>(promoRules),
      'minQuantity': serializer.toJson<double>(minQuantity),
      'purchasePrice': serializer.toJson<double?>(purchasePrice),
    };
  }

  Product copyWith({
    String? id,
    String? name,
    double? price,
    double? wholesalePrice,
    Value<String?> barcode = const Value.absent(),
    String? altBarcodes,
    Value<String?> sku = const Value.absent(),
    Value<String?> plu = const Value.absent(),
    String? unit,
    bool? isWeight,
    bool? isService,
    bool? isFavorite,
    double? quantity,
    String? category,
    Value<String?> imageUrl = const Value.absent(),
    String? searchKey,
    String? promoRules,
    double? minQuantity,
    Value<double?> purchasePrice = const Value.absent(),
  }) => Product(
    id: id ?? this.id,
    name: name ?? this.name,
    price: price ?? this.price,
    wholesalePrice: wholesalePrice ?? this.wholesalePrice,
    barcode: barcode.present ? barcode.value : this.barcode,
    altBarcodes: altBarcodes ?? this.altBarcodes,
    sku: sku.present ? sku.value : this.sku,
    plu: plu.present ? plu.value : this.plu,
    unit: unit ?? this.unit,
    isWeight: isWeight ?? this.isWeight,
    isService: isService ?? this.isService,
    isFavorite: isFavorite ?? this.isFavorite,
    quantity: quantity ?? this.quantity,
    category: category ?? this.category,
    imageUrl: imageUrl.present ? imageUrl.value : this.imageUrl,
    searchKey: searchKey ?? this.searchKey,
    promoRules: promoRules ?? this.promoRules,
    minQuantity: minQuantity ?? this.minQuantity,
    purchasePrice: purchasePrice.present
        ? purchasePrice.value
        : this.purchasePrice,
  );
  Product copyWithCompanion(ProductsCompanion data) {
    return Product(
      id: data.id.present ? data.id.value : this.id,
      name: data.name.present ? data.name.value : this.name,
      price: data.price.present ? data.price.value : this.price,
      wholesalePrice: data.wholesalePrice.present
          ? data.wholesalePrice.value
          : this.wholesalePrice,
      barcode: data.barcode.present ? data.barcode.value : this.barcode,
      altBarcodes: data.altBarcodes.present
          ? data.altBarcodes.value
          : this.altBarcodes,
      sku: data.sku.present ? data.sku.value : this.sku,
      plu: data.plu.present ? data.plu.value : this.plu,
      unit: data.unit.present ? data.unit.value : this.unit,
      isWeight: data.isWeight.present ? data.isWeight.value : this.isWeight,
      isService: data.isService.present ? data.isService.value : this.isService,
      isFavorite: data.isFavorite.present
          ? data.isFavorite.value
          : this.isFavorite,
      quantity: data.quantity.present ? data.quantity.value : this.quantity,
      category: data.category.present ? data.category.value : this.category,
      imageUrl: data.imageUrl.present ? data.imageUrl.value : this.imageUrl,
      searchKey: data.searchKey.present ? data.searchKey.value : this.searchKey,
      promoRules: data.promoRules.present
          ? data.promoRules.value
          : this.promoRules,
      minQuantity: data.minQuantity.present
          ? data.minQuantity.value
          : this.minQuantity,
      purchasePrice: data.purchasePrice.present
          ? data.purchasePrice.value
          : this.purchasePrice,
    );
  }

  @override
  String toString() {
    return (StringBuffer('Product(')
          ..write('id: $id, ')
          ..write('name: $name, ')
          ..write('price: $price, ')
          ..write('wholesalePrice: $wholesalePrice, ')
          ..write('barcode: $barcode, ')
          ..write('altBarcodes: $altBarcodes, ')
          ..write('sku: $sku, ')
          ..write('plu: $plu, ')
          ..write('unit: $unit, ')
          ..write('isWeight: $isWeight, ')
          ..write('isService: $isService, ')
          ..write('isFavorite: $isFavorite, ')
          ..write('quantity: $quantity, ')
          ..write('category: $category, ')
          ..write('imageUrl: $imageUrl, ')
          ..write('searchKey: $searchKey, ')
          ..write('promoRules: $promoRules, ')
          ..write('minQuantity: $minQuantity, ')
          ..write('purchasePrice: $purchasePrice')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    id,
    name,
    price,
    wholesalePrice,
    barcode,
    altBarcodes,
    sku,
    plu,
    unit,
    isWeight,
    isService,
    isFavorite,
    quantity,
    category,
    imageUrl,
    searchKey,
    promoRules,
    minQuantity,
    purchasePrice,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is Product &&
          other.id == this.id &&
          other.name == this.name &&
          other.price == this.price &&
          other.wholesalePrice == this.wholesalePrice &&
          other.barcode == this.barcode &&
          other.altBarcodes == this.altBarcodes &&
          other.sku == this.sku &&
          other.plu == this.plu &&
          other.unit == this.unit &&
          other.isWeight == this.isWeight &&
          other.isService == this.isService &&
          other.isFavorite == this.isFavorite &&
          other.quantity == this.quantity &&
          other.category == this.category &&
          other.imageUrl == this.imageUrl &&
          other.searchKey == this.searchKey &&
          other.promoRules == this.promoRules &&
          other.minQuantity == this.minQuantity &&
          other.purchasePrice == this.purchasePrice);
}

class ProductsCompanion extends UpdateCompanion<Product> {
  final Value<String> id;
  final Value<String> name;
  final Value<double> price;
  final Value<double> wholesalePrice;
  final Value<String?> barcode;
  final Value<String> altBarcodes;
  final Value<String?> sku;
  final Value<String?> plu;
  final Value<String> unit;
  final Value<bool> isWeight;
  final Value<bool> isService;
  final Value<bool> isFavorite;
  final Value<double> quantity;
  final Value<String> category;
  final Value<String?> imageUrl;
  final Value<String> searchKey;
  final Value<String> promoRules;
  final Value<double> minQuantity;
  final Value<double?> purchasePrice;
  final Value<int> rowid;
  const ProductsCompanion({
    this.id = const Value.absent(),
    this.name = const Value.absent(),
    this.price = const Value.absent(),
    this.wholesalePrice = const Value.absent(),
    this.barcode = const Value.absent(),
    this.altBarcodes = const Value.absent(),
    this.sku = const Value.absent(),
    this.plu = const Value.absent(),
    this.unit = const Value.absent(),
    this.isWeight = const Value.absent(),
    this.isService = const Value.absent(),
    this.isFavorite = const Value.absent(),
    this.quantity = const Value.absent(),
    this.category = const Value.absent(),
    this.imageUrl = const Value.absent(),
    this.searchKey = const Value.absent(),
    this.promoRules = const Value.absent(),
    this.minQuantity = const Value.absent(),
    this.purchasePrice = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  ProductsCompanion.insert({
    required String id,
    required String name,
    this.price = const Value.absent(),
    this.wholesalePrice = const Value.absent(),
    this.barcode = const Value.absent(),
    this.altBarcodes = const Value.absent(),
    this.sku = const Value.absent(),
    this.plu = const Value.absent(),
    this.unit = const Value.absent(),
    this.isWeight = const Value.absent(),
    this.isService = const Value.absent(),
    this.isFavorite = const Value.absent(),
    this.quantity = const Value.absent(),
    this.category = const Value.absent(),
    this.imageUrl = const Value.absent(),
    this.searchKey = const Value.absent(),
    this.promoRules = const Value.absent(),
    this.minQuantity = const Value.absent(),
    this.purchasePrice = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : id = Value(id),
       name = Value(name);
  static Insertable<Product> custom({
    Expression<String>? id,
    Expression<String>? name,
    Expression<double>? price,
    Expression<double>? wholesalePrice,
    Expression<String>? barcode,
    Expression<String>? altBarcodes,
    Expression<String>? sku,
    Expression<String>? plu,
    Expression<String>? unit,
    Expression<bool>? isWeight,
    Expression<bool>? isService,
    Expression<bool>? isFavorite,
    Expression<double>? quantity,
    Expression<String>? category,
    Expression<String>? imageUrl,
    Expression<String>? searchKey,
    Expression<String>? promoRules,
    Expression<double>? minQuantity,
    Expression<double>? purchasePrice,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (name != null) 'name': name,
      if (price != null) 'price': price,
      if (wholesalePrice != null) 'wholesale_price': wholesalePrice,
      if (barcode != null) 'barcode': barcode,
      if (altBarcodes != null) 'alt_barcodes': altBarcodes,
      if (sku != null) 'sku': sku,
      if (plu != null) 'plu': plu,
      if (unit != null) 'unit': unit,
      if (isWeight != null) 'is_weight': isWeight,
      if (isService != null) 'is_service': isService,
      if (isFavorite != null) 'is_favorite': isFavorite,
      if (quantity != null) 'quantity': quantity,
      if (category != null) 'category': category,
      if (imageUrl != null) 'image_url': imageUrl,
      if (searchKey != null) 'search_key': searchKey,
      if (promoRules != null) 'promo_rules': promoRules,
      if (minQuantity != null) 'min_quantity': minQuantity,
      if (purchasePrice != null) 'purchase_price': purchasePrice,
      if (rowid != null) 'rowid': rowid,
    });
  }

  ProductsCompanion copyWith({
    Value<String>? id,
    Value<String>? name,
    Value<double>? price,
    Value<double>? wholesalePrice,
    Value<String?>? barcode,
    Value<String>? altBarcodes,
    Value<String?>? sku,
    Value<String?>? plu,
    Value<String>? unit,
    Value<bool>? isWeight,
    Value<bool>? isService,
    Value<bool>? isFavorite,
    Value<double>? quantity,
    Value<String>? category,
    Value<String?>? imageUrl,
    Value<String>? searchKey,
    Value<String>? promoRules,
    Value<double>? minQuantity,
    Value<double?>? purchasePrice,
    Value<int>? rowid,
  }) {
    return ProductsCompanion(
      id: id ?? this.id,
      name: name ?? this.name,
      price: price ?? this.price,
      wholesalePrice: wholesalePrice ?? this.wholesalePrice,
      barcode: barcode ?? this.barcode,
      altBarcodes: altBarcodes ?? this.altBarcodes,
      sku: sku ?? this.sku,
      plu: plu ?? this.plu,
      unit: unit ?? this.unit,
      isWeight: isWeight ?? this.isWeight,
      isService: isService ?? this.isService,
      isFavorite: isFavorite ?? this.isFavorite,
      quantity: quantity ?? this.quantity,
      category: category ?? this.category,
      imageUrl: imageUrl ?? this.imageUrl,
      searchKey: searchKey ?? this.searchKey,
      promoRules: promoRules ?? this.promoRules,
      minQuantity: minQuantity ?? this.minQuantity,
      purchasePrice: purchasePrice ?? this.purchasePrice,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<String>(id.value);
    }
    if (name.present) {
      map['name'] = Variable<String>(name.value);
    }
    if (price.present) {
      map['price'] = Variable<double>(price.value);
    }
    if (wholesalePrice.present) {
      map['wholesale_price'] = Variable<double>(wholesalePrice.value);
    }
    if (barcode.present) {
      map['barcode'] = Variable<String>(barcode.value);
    }
    if (altBarcodes.present) {
      map['alt_barcodes'] = Variable<String>(altBarcodes.value);
    }
    if (sku.present) {
      map['sku'] = Variable<String>(sku.value);
    }
    if (plu.present) {
      map['plu'] = Variable<String>(plu.value);
    }
    if (unit.present) {
      map['unit'] = Variable<String>(unit.value);
    }
    if (isWeight.present) {
      map['is_weight'] = Variable<bool>(isWeight.value);
    }
    if (isService.present) {
      map['is_service'] = Variable<bool>(isService.value);
    }
    if (isFavorite.present) {
      map['is_favorite'] = Variable<bool>(isFavorite.value);
    }
    if (quantity.present) {
      map['quantity'] = Variable<double>(quantity.value);
    }
    if (category.present) {
      map['category'] = Variable<String>(category.value);
    }
    if (imageUrl.present) {
      map['image_url'] = Variable<String>(imageUrl.value);
    }
    if (searchKey.present) {
      map['search_key'] = Variable<String>(searchKey.value);
    }
    if (promoRules.present) {
      map['promo_rules'] = Variable<String>(promoRules.value);
    }
    if (minQuantity.present) {
      map['min_quantity'] = Variable<double>(minQuantity.value);
    }
    if (purchasePrice.present) {
      map['purchase_price'] = Variable<double>(purchasePrice.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ProductsCompanion(')
          ..write('id: $id, ')
          ..write('name: $name, ')
          ..write('price: $price, ')
          ..write('wholesalePrice: $wholesalePrice, ')
          ..write('barcode: $barcode, ')
          ..write('altBarcodes: $altBarcodes, ')
          ..write('sku: $sku, ')
          ..write('plu: $plu, ')
          ..write('unit: $unit, ')
          ..write('isWeight: $isWeight, ')
          ..write('isService: $isService, ')
          ..write('isFavorite: $isFavorite, ')
          ..write('quantity: $quantity, ')
          ..write('category: $category, ')
          ..write('imageUrl: $imageUrl, ')
          ..write('searchKey: $searchKey, ')
          ..write('promoRules: $promoRules, ')
          ..write('minQuantity: $minQuantity, ')
          ..write('purchasePrice: $purchasePrice, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $OutboxTable extends Outbox with TableInfo<$OutboxTable, OutboxData> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $OutboxTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<String> id = GeneratedColumn<String>(
    'id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _kindMeta = const VerificationMeta('kind');
  @override
  late final GeneratedColumn<String> kind = GeneratedColumn<String>(
    'kind',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _bodyMeta = const VerificationMeta('body');
  @override
  late final GeneratedColumn<String> body = GeneratedColumn<String>(
    'body',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _createdAtMeta = const VerificationMeta(
    'createdAt',
  );
  @override
  late final GeneratedColumn<DateTime> createdAt = GeneratedColumn<DateTime>(
    'created_at',
    aliasedName,
    false,
    type: DriftSqlType.dateTime,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _statusMeta = const VerificationMeta('status');
  @override
  late final GeneratedColumn<String> status = GeneratedColumn<String>(
    'status',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant('pending'),
  );
  static const VerificationMeta _attemptsMeta = const VerificationMeta(
    'attempts',
  );
  @override
  late final GeneratedColumn<int> attempts = GeneratedColumn<int>(
    'attempts',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _lastErrorMeta = const VerificationMeta(
    'lastError',
  );
  @override
  late final GeneratedColumn<String> lastError = GeneratedColumn<String>(
    'last_error',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _totalMeta = const VerificationMeta('total');
  @override
  late final GeneratedColumn<double> total = GeneratedColumn<double>(
    'total',
    aliasedName,
    false,
    type: DriftSqlType.double,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _summaryMeta = const VerificationMeta(
    'summary',
  );
  @override
  late final GeneratedColumn<String> summary = GeneratedColumn<String>(
    'summary',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _paymentMethodMeta = const VerificationMeta(
    'paymentMethod',
  );
  @override
  late final GeneratedColumn<String> paymentMethod = GeneratedColumn<String>(
    'payment_method',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant(''),
  );
  static const VerificationMeta _serverIdMeta = const VerificationMeta(
    'serverId',
  );
  @override
  late final GeneratedColumn<String> serverId = GeneratedColumn<String>(
    'server_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _serverNumberMeta = const VerificationMeta(
    'serverNumber',
  );
  @override
  late final GeneratedColumn<String> serverNumber = GeneratedColumn<String>(
    'server_number',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _shiftIdMeta = const VerificationMeta(
    'shiftId',
  );
  @override
  late final GeneratedColumn<String> shiftId = GeneratedColumn<String>(
    'shift_id',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _displayMeta = const VerificationMeta(
    'display',
  );
  @override
  late final GeneratedColumn<String> display = GeneratedColumn<String>(
    'display',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
    defaultValue: const Constant('[]'),
  );
  @override
  List<GeneratedColumn> get $columns => [
    id,
    kind,
    body,
    createdAt,
    status,
    attempts,
    lastError,
    total,
    summary,
    paymentMethod,
    serverId,
    serverNumber,
    shiftId,
    display,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'outbox';
  @override
  VerificationContext validateIntegrity(
    Insertable<OutboxData> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    } else if (isInserting) {
      context.missing(_idMeta);
    }
    if (data.containsKey('kind')) {
      context.handle(
        _kindMeta,
        kind.isAcceptableOrUnknown(data['kind']!, _kindMeta),
      );
    } else if (isInserting) {
      context.missing(_kindMeta);
    }
    if (data.containsKey('body')) {
      context.handle(
        _bodyMeta,
        body.isAcceptableOrUnknown(data['body']!, _bodyMeta),
      );
    } else if (isInserting) {
      context.missing(_bodyMeta);
    }
    if (data.containsKey('created_at')) {
      context.handle(
        _createdAtMeta,
        createdAt.isAcceptableOrUnknown(data['created_at']!, _createdAtMeta),
      );
    } else if (isInserting) {
      context.missing(_createdAtMeta);
    }
    if (data.containsKey('status')) {
      context.handle(
        _statusMeta,
        status.isAcceptableOrUnknown(data['status']!, _statusMeta),
      );
    }
    if (data.containsKey('attempts')) {
      context.handle(
        _attemptsMeta,
        attempts.isAcceptableOrUnknown(data['attempts']!, _attemptsMeta),
      );
    }
    if (data.containsKey('last_error')) {
      context.handle(
        _lastErrorMeta,
        lastError.isAcceptableOrUnknown(data['last_error']!, _lastErrorMeta),
      );
    }
    if (data.containsKey('total')) {
      context.handle(
        _totalMeta,
        total.isAcceptableOrUnknown(data['total']!, _totalMeta),
      );
    }
    if (data.containsKey('summary')) {
      context.handle(
        _summaryMeta,
        summary.isAcceptableOrUnknown(data['summary']!, _summaryMeta),
      );
    }
    if (data.containsKey('payment_method')) {
      context.handle(
        _paymentMethodMeta,
        paymentMethod.isAcceptableOrUnknown(
          data['payment_method']!,
          _paymentMethodMeta,
        ),
      );
    }
    if (data.containsKey('server_id')) {
      context.handle(
        _serverIdMeta,
        serverId.isAcceptableOrUnknown(data['server_id']!, _serverIdMeta),
      );
    }
    if (data.containsKey('server_number')) {
      context.handle(
        _serverNumberMeta,
        serverNumber.isAcceptableOrUnknown(
          data['server_number']!,
          _serverNumberMeta,
        ),
      );
    }
    if (data.containsKey('shift_id')) {
      context.handle(
        _shiftIdMeta,
        shiftId.isAcceptableOrUnknown(data['shift_id']!, _shiftIdMeta),
      );
    }
    if (data.containsKey('display')) {
      context.handle(
        _displayMeta,
        display.isAcceptableOrUnknown(data['display']!, _displayMeta),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  OutboxData map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return OutboxData(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}id'],
      )!,
      kind: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}kind'],
      )!,
      body: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}body'],
      )!,
      createdAt: attachedDatabase.typeMapping.read(
        DriftSqlType.dateTime,
        data['${effectivePrefix}created_at'],
      )!,
      status: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}status'],
      )!,
      attempts: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}attempts'],
      )!,
      lastError: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}last_error'],
      ),
      total: attachedDatabase.typeMapping.read(
        DriftSqlType.double,
        data['${effectivePrefix}total'],
      )!,
      summary: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}summary'],
      )!,
      paymentMethod: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}payment_method'],
      )!,
      serverId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_id'],
      ),
      serverNumber: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}server_number'],
      ),
      shiftId: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}shift_id'],
      ),
      display: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}display'],
      )!,
    );
  }

  @override
  $OutboxTable createAlias(String alias) {
    return $OutboxTable(attachedDatabase, alias);
  }
}

class OutboxData extends DataClass implements Insertable<OutboxData> {
  final String id;

  /// sale | receive | audit
  final String kind;
  final String body;
  final DateTime createdAt;

  /// pending | sent | failed
  final String status;
  final int attempts;
  final String? lastError;
  final double total;

  /// Короткое описание для списка чеков: «Mars, Кока-Кола…».
  final String summary;
  final String paymentMethod;
  final String? serverId;
  final String? serverNumber;
  final String? shiftId;

  /// Состав для показа на телефоне: [[название, кол-во, цена, сумма], …] — серверу не отправляется.
  final String display;
  const OutboxData({
    required this.id,
    required this.kind,
    required this.body,
    required this.createdAt,
    required this.status,
    required this.attempts,
    this.lastError,
    required this.total,
    required this.summary,
    required this.paymentMethod,
    this.serverId,
    this.serverNumber,
    this.shiftId,
    required this.display,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<String>(id);
    map['kind'] = Variable<String>(kind);
    map['body'] = Variable<String>(body);
    map['created_at'] = Variable<DateTime>(createdAt);
    map['status'] = Variable<String>(status);
    map['attempts'] = Variable<int>(attempts);
    if (!nullToAbsent || lastError != null) {
      map['last_error'] = Variable<String>(lastError);
    }
    map['total'] = Variable<double>(total);
    map['summary'] = Variable<String>(summary);
    map['payment_method'] = Variable<String>(paymentMethod);
    if (!nullToAbsent || serverId != null) {
      map['server_id'] = Variable<String>(serverId);
    }
    if (!nullToAbsent || serverNumber != null) {
      map['server_number'] = Variable<String>(serverNumber);
    }
    if (!nullToAbsent || shiftId != null) {
      map['shift_id'] = Variable<String>(shiftId);
    }
    map['display'] = Variable<String>(display);
    return map;
  }

  OutboxCompanion toCompanion(bool nullToAbsent) {
    return OutboxCompanion(
      id: Value(id),
      kind: Value(kind),
      body: Value(body),
      createdAt: Value(createdAt),
      status: Value(status),
      attempts: Value(attempts),
      lastError: lastError == null && nullToAbsent
          ? const Value.absent()
          : Value(lastError),
      total: Value(total),
      summary: Value(summary),
      paymentMethod: Value(paymentMethod),
      serverId: serverId == null && nullToAbsent
          ? const Value.absent()
          : Value(serverId),
      serverNumber: serverNumber == null && nullToAbsent
          ? const Value.absent()
          : Value(serverNumber),
      shiftId: shiftId == null && nullToAbsent
          ? const Value.absent()
          : Value(shiftId),
      display: Value(display),
    );
  }

  factory OutboxData.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return OutboxData(
      id: serializer.fromJson<String>(json['id']),
      kind: serializer.fromJson<String>(json['kind']),
      body: serializer.fromJson<String>(json['body']),
      createdAt: serializer.fromJson<DateTime>(json['createdAt']),
      status: serializer.fromJson<String>(json['status']),
      attempts: serializer.fromJson<int>(json['attempts']),
      lastError: serializer.fromJson<String?>(json['lastError']),
      total: serializer.fromJson<double>(json['total']),
      summary: serializer.fromJson<String>(json['summary']),
      paymentMethod: serializer.fromJson<String>(json['paymentMethod']),
      serverId: serializer.fromJson<String?>(json['serverId']),
      serverNumber: serializer.fromJson<String?>(json['serverNumber']),
      shiftId: serializer.fromJson<String?>(json['shiftId']),
      display: serializer.fromJson<String>(json['display']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<String>(id),
      'kind': serializer.toJson<String>(kind),
      'body': serializer.toJson<String>(body),
      'createdAt': serializer.toJson<DateTime>(createdAt),
      'status': serializer.toJson<String>(status),
      'attempts': serializer.toJson<int>(attempts),
      'lastError': serializer.toJson<String?>(lastError),
      'total': serializer.toJson<double>(total),
      'summary': serializer.toJson<String>(summary),
      'paymentMethod': serializer.toJson<String>(paymentMethod),
      'serverId': serializer.toJson<String?>(serverId),
      'serverNumber': serializer.toJson<String?>(serverNumber),
      'shiftId': serializer.toJson<String?>(shiftId),
      'display': serializer.toJson<String>(display),
    };
  }

  OutboxData copyWith({
    String? id,
    String? kind,
    String? body,
    DateTime? createdAt,
    String? status,
    int? attempts,
    Value<String?> lastError = const Value.absent(),
    double? total,
    String? summary,
    String? paymentMethod,
    Value<String?> serverId = const Value.absent(),
    Value<String?> serverNumber = const Value.absent(),
    Value<String?> shiftId = const Value.absent(),
    String? display,
  }) => OutboxData(
    id: id ?? this.id,
    kind: kind ?? this.kind,
    body: body ?? this.body,
    createdAt: createdAt ?? this.createdAt,
    status: status ?? this.status,
    attempts: attempts ?? this.attempts,
    lastError: lastError.present ? lastError.value : this.lastError,
    total: total ?? this.total,
    summary: summary ?? this.summary,
    paymentMethod: paymentMethod ?? this.paymentMethod,
    serverId: serverId.present ? serverId.value : this.serverId,
    serverNumber: serverNumber.present ? serverNumber.value : this.serverNumber,
    shiftId: shiftId.present ? shiftId.value : this.shiftId,
    display: display ?? this.display,
  );
  OutboxData copyWithCompanion(OutboxCompanion data) {
    return OutboxData(
      id: data.id.present ? data.id.value : this.id,
      kind: data.kind.present ? data.kind.value : this.kind,
      body: data.body.present ? data.body.value : this.body,
      createdAt: data.createdAt.present ? data.createdAt.value : this.createdAt,
      status: data.status.present ? data.status.value : this.status,
      attempts: data.attempts.present ? data.attempts.value : this.attempts,
      lastError: data.lastError.present ? data.lastError.value : this.lastError,
      total: data.total.present ? data.total.value : this.total,
      summary: data.summary.present ? data.summary.value : this.summary,
      paymentMethod: data.paymentMethod.present
          ? data.paymentMethod.value
          : this.paymentMethod,
      serverId: data.serverId.present ? data.serverId.value : this.serverId,
      serverNumber: data.serverNumber.present
          ? data.serverNumber.value
          : this.serverNumber,
      shiftId: data.shiftId.present ? data.shiftId.value : this.shiftId,
      display: data.display.present ? data.display.value : this.display,
    );
  }

  @override
  String toString() {
    return (StringBuffer('OutboxData(')
          ..write('id: $id, ')
          ..write('kind: $kind, ')
          ..write('body: $body, ')
          ..write('createdAt: $createdAt, ')
          ..write('status: $status, ')
          ..write('attempts: $attempts, ')
          ..write('lastError: $lastError, ')
          ..write('total: $total, ')
          ..write('summary: $summary, ')
          ..write('paymentMethod: $paymentMethod, ')
          ..write('serverId: $serverId, ')
          ..write('serverNumber: $serverNumber, ')
          ..write('shiftId: $shiftId, ')
          ..write('display: $display')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    id,
    kind,
    body,
    createdAt,
    status,
    attempts,
    lastError,
    total,
    summary,
    paymentMethod,
    serverId,
    serverNumber,
    shiftId,
    display,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is OutboxData &&
          other.id == this.id &&
          other.kind == this.kind &&
          other.body == this.body &&
          other.createdAt == this.createdAt &&
          other.status == this.status &&
          other.attempts == this.attempts &&
          other.lastError == this.lastError &&
          other.total == this.total &&
          other.summary == this.summary &&
          other.paymentMethod == this.paymentMethod &&
          other.serverId == this.serverId &&
          other.serverNumber == this.serverNumber &&
          other.shiftId == this.shiftId &&
          other.display == this.display);
}

class OutboxCompanion extends UpdateCompanion<OutboxData> {
  final Value<String> id;
  final Value<String> kind;
  final Value<String> body;
  final Value<DateTime> createdAt;
  final Value<String> status;
  final Value<int> attempts;
  final Value<String?> lastError;
  final Value<double> total;
  final Value<String> summary;
  final Value<String> paymentMethod;
  final Value<String?> serverId;
  final Value<String?> serverNumber;
  final Value<String?> shiftId;
  final Value<String> display;
  final Value<int> rowid;
  const OutboxCompanion({
    this.id = const Value.absent(),
    this.kind = const Value.absent(),
    this.body = const Value.absent(),
    this.createdAt = const Value.absent(),
    this.status = const Value.absent(),
    this.attempts = const Value.absent(),
    this.lastError = const Value.absent(),
    this.total = const Value.absent(),
    this.summary = const Value.absent(),
    this.paymentMethod = const Value.absent(),
    this.serverId = const Value.absent(),
    this.serverNumber = const Value.absent(),
    this.shiftId = const Value.absent(),
    this.display = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  OutboxCompanion.insert({
    required String id,
    required String kind,
    required String body,
    required DateTime createdAt,
    this.status = const Value.absent(),
    this.attempts = const Value.absent(),
    this.lastError = const Value.absent(),
    this.total = const Value.absent(),
    this.summary = const Value.absent(),
    this.paymentMethod = const Value.absent(),
    this.serverId = const Value.absent(),
    this.serverNumber = const Value.absent(),
    this.shiftId = const Value.absent(),
    this.display = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : id = Value(id),
       kind = Value(kind),
       body = Value(body),
       createdAt = Value(createdAt);
  static Insertable<OutboxData> custom({
    Expression<String>? id,
    Expression<String>? kind,
    Expression<String>? body,
    Expression<DateTime>? createdAt,
    Expression<String>? status,
    Expression<int>? attempts,
    Expression<String>? lastError,
    Expression<double>? total,
    Expression<String>? summary,
    Expression<String>? paymentMethod,
    Expression<String>? serverId,
    Expression<String>? serverNumber,
    Expression<String>? shiftId,
    Expression<String>? display,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (kind != null) 'kind': kind,
      if (body != null) 'body': body,
      if (createdAt != null) 'created_at': createdAt,
      if (status != null) 'status': status,
      if (attempts != null) 'attempts': attempts,
      if (lastError != null) 'last_error': lastError,
      if (total != null) 'total': total,
      if (summary != null) 'summary': summary,
      if (paymentMethod != null) 'payment_method': paymentMethod,
      if (serverId != null) 'server_id': serverId,
      if (serverNumber != null) 'server_number': serverNumber,
      if (shiftId != null) 'shift_id': shiftId,
      if (display != null) 'display': display,
      if (rowid != null) 'rowid': rowid,
    });
  }

  OutboxCompanion copyWith({
    Value<String>? id,
    Value<String>? kind,
    Value<String>? body,
    Value<DateTime>? createdAt,
    Value<String>? status,
    Value<int>? attempts,
    Value<String?>? lastError,
    Value<double>? total,
    Value<String>? summary,
    Value<String>? paymentMethod,
    Value<String?>? serverId,
    Value<String?>? serverNumber,
    Value<String?>? shiftId,
    Value<String>? display,
    Value<int>? rowid,
  }) {
    return OutboxCompanion(
      id: id ?? this.id,
      kind: kind ?? this.kind,
      body: body ?? this.body,
      createdAt: createdAt ?? this.createdAt,
      status: status ?? this.status,
      attempts: attempts ?? this.attempts,
      lastError: lastError ?? this.lastError,
      total: total ?? this.total,
      summary: summary ?? this.summary,
      paymentMethod: paymentMethod ?? this.paymentMethod,
      serverId: serverId ?? this.serverId,
      serverNumber: serverNumber ?? this.serverNumber,
      shiftId: shiftId ?? this.shiftId,
      display: display ?? this.display,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<String>(id.value);
    }
    if (kind.present) {
      map['kind'] = Variable<String>(kind.value);
    }
    if (body.present) {
      map['body'] = Variable<String>(body.value);
    }
    if (createdAt.present) {
      map['created_at'] = Variable<DateTime>(createdAt.value);
    }
    if (status.present) {
      map['status'] = Variable<String>(status.value);
    }
    if (attempts.present) {
      map['attempts'] = Variable<int>(attempts.value);
    }
    if (lastError.present) {
      map['last_error'] = Variable<String>(lastError.value);
    }
    if (total.present) {
      map['total'] = Variable<double>(total.value);
    }
    if (summary.present) {
      map['summary'] = Variable<String>(summary.value);
    }
    if (paymentMethod.present) {
      map['payment_method'] = Variable<String>(paymentMethod.value);
    }
    if (serverId.present) {
      map['server_id'] = Variable<String>(serverId.value);
    }
    if (serverNumber.present) {
      map['server_number'] = Variable<String>(serverNumber.value);
    }
    if (shiftId.present) {
      map['shift_id'] = Variable<String>(shiftId.value);
    }
    if (display.present) {
      map['display'] = Variable<String>(display.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('OutboxCompanion(')
          ..write('id: $id, ')
          ..write('kind: $kind, ')
          ..write('body: $body, ')
          ..write('createdAt: $createdAt, ')
          ..write('status: $status, ')
          ..write('attempts: $attempts, ')
          ..write('lastError: $lastError, ')
          ..write('total: $total, ')
          ..write('summary: $summary, ')
          ..write('paymentMethod: $paymentMethod, ')
          ..write('serverId: $serverId, ')
          ..write('serverNumber: $serverNumber, ')
          ..write('shiftId: $shiftId, ')
          ..write('display: $display, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $SettingsTable extends Settings with TableInfo<$SettingsTable, Setting> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $SettingsTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _keyMeta = const VerificationMeta('key');
  @override
  late final GeneratedColumn<String> key = GeneratedColumn<String>(
    'key',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _valueMeta = const VerificationMeta('value');
  @override
  late final GeneratedColumn<String> value = GeneratedColumn<String>(
    'value',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [key, value];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'settings';
  @override
  VerificationContext validateIntegrity(
    Insertable<Setting> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('key')) {
      context.handle(
        _keyMeta,
        key.isAcceptableOrUnknown(data['key']!, _keyMeta),
      );
    } else if (isInserting) {
      context.missing(_keyMeta);
    }
    if (data.containsKey('value')) {
      context.handle(
        _valueMeta,
        value.isAcceptableOrUnknown(data['value']!, _valueMeta),
      );
    } else if (isInserting) {
      context.missing(_valueMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {key};
  @override
  Setting map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return Setting(
      key: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}key'],
      )!,
      value: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}value'],
      )!,
    );
  }

  @override
  $SettingsTable createAlias(String alias) {
    return $SettingsTable(attachedDatabase, alias);
  }
}

class Setting extends DataClass implements Insertable<Setting> {
  final String key;
  final String value;
  const Setting({required this.key, required this.value});
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['key'] = Variable<String>(key);
    map['value'] = Variable<String>(value);
    return map;
  }

  SettingsCompanion toCompanion(bool nullToAbsent) {
    return SettingsCompanion(key: Value(key), value: Value(value));
  }

  factory Setting.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return Setting(
      key: serializer.fromJson<String>(json['key']),
      value: serializer.fromJson<String>(json['value']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'key': serializer.toJson<String>(key),
      'value': serializer.toJson<String>(value),
    };
  }

  Setting copyWith({String? key, String? value}) =>
      Setting(key: key ?? this.key, value: value ?? this.value);
  Setting copyWithCompanion(SettingsCompanion data) {
    return Setting(
      key: data.key.present ? data.key.value : this.key,
      value: data.value.present ? data.value.value : this.value,
    );
  }

  @override
  String toString() {
    return (StringBuffer('Setting(')
          ..write('key: $key, ')
          ..write('value: $value')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(key, value);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is Setting && other.key == this.key && other.value == this.value);
}

class SettingsCompanion extends UpdateCompanion<Setting> {
  final Value<String> key;
  final Value<String> value;
  final Value<int> rowid;
  const SettingsCompanion({
    this.key = const Value.absent(),
    this.value = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  SettingsCompanion.insert({
    required String key,
    required String value,
    this.rowid = const Value.absent(),
  }) : key = Value(key),
       value = Value(value);
  static Insertable<Setting> custom({
    Expression<String>? key,
    Expression<String>? value,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (key != null) 'key': key,
      if (value != null) 'value': value,
      if (rowid != null) 'rowid': rowid,
    });
  }

  SettingsCompanion copyWith({
    Value<String>? key,
    Value<String>? value,
    Value<int>? rowid,
  }) {
    return SettingsCompanion(
      key: key ?? this.key,
      value: value ?? this.value,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (key.present) {
      map['key'] = Variable<String>(key.value);
    }
    if (value.present) {
      map['value'] = Variable<String>(value.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('SettingsCompanion(')
          ..write('key: $key, ')
          ..write('value: $value, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

abstract class _$AppDatabase extends GeneratedDatabase {
  _$AppDatabase(QueryExecutor e) : super(e);
  $AppDatabaseManager get managers => $AppDatabaseManager(this);
  late final $ProductsTable products = $ProductsTable(this);
  late final $OutboxTable outbox = $OutboxTable(this);
  late final $SettingsTable settings = $SettingsTable(this);
  @override
  Iterable<TableInfo<Table, Object?>> get allTables =>
      allSchemaEntities.whereType<TableInfo<Table, Object?>>();
  @override
  List<DatabaseSchemaEntity> get allSchemaEntities => [
    products,
    outbox,
    settings,
  ];
}

typedef $$ProductsTableCreateCompanionBuilder = ProductsCompanion Function({
  required String id,
  required String name,
  Value<double> price,
  Value<double> wholesalePrice,
  Value<String?> barcode,
  Value<String> altBarcodes,
  Value<String?> sku,
  Value<String?> plu,
  Value<String> unit,
  Value<bool> isWeight,
  Value<bool> isService,
  Value<bool> isFavorite,
  Value<double> quantity,
  Value<String> category,
  Value<String?> imageUrl,
  Value<String> searchKey,
  Value<String> promoRules,
  Value<double> minQuantity,
  Value<double?> purchasePrice,
  Value<int> rowid,
});
typedef $$ProductsTableUpdateCompanionBuilder = ProductsCompanion Function({
  Value<String> id,
  Value<String> name,
  Value<double> price,
  Value<double> wholesalePrice,
  Value<String?> barcode,
  Value<String> altBarcodes,
  Value<String?> sku,
  Value<String?> plu,
  Value<String> unit,
  Value<bool> isWeight,
  Value<bool> isService,
  Value<bool> isFavorite,
  Value<double> quantity,
  Value<String> category,
  Value<String?> imageUrl,
  Value<String> searchKey,
  Value<String> promoRules,
  Value<double> minQuantity,
  Value<double?> purchasePrice,
  Value<int> rowid,
});

class $$ProductsTableFilterComposer
    extends Composer<_$AppDatabase, $ProductsTable> {
  $$ProductsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get name => $composableBuilder(
    column: $table.name,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get price => $composableBuilder(
    column: $table.price,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get wholesalePrice => $composableBuilder(
    column: $table.wholesalePrice,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get barcode => $composableBuilder(
    column: $table.barcode,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get altBarcodes => $composableBuilder(
    column: $table.altBarcodes,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get sku => $composableBuilder(
    column: $table.sku,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get plu => $composableBuilder(
    column: $table.plu,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get unit => $composableBuilder(
    column: $table.unit,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<bool> get isWeight => $composableBuilder(
    column: $table.isWeight,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<bool> get isService => $composableBuilder(
    column: $table.isService,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<bool> get isFavorite => $composableBuilder(
    column: $table.isFavorite,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get quantity => $composableBuilder(
    column: $table.quantity,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get category => $composableBuilder(
    column: $table.category,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get imageUrl => $composableBuilder(
    column: $table.imageUrl,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get searchKey => $composableBuilder(
    column: $table.searchKey,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get promoRules => $composableBuilder(
    column: $table.promoRules,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get minQuantity => $composableBuilder(
    column: $table.minQuantity,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get purchasePrice => $composableBuilder(
    column: $table.purchasePrice,
    builder: (column) => ColumnFilters(column),
  );
}

class $$ProductsTableOrderingComposer
    extends Composer<_$AppDatabase, $ProductsTable> {
  $$ProductsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get name => $composableBuilder(
    column: $table.name,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get price => $composableBuilder(
    column: $table.price,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get wholesalePrice => $composableBuilder(
    column: $table.wholesalePrice,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get barcode => $composableBuilder(
    column: $table.barcode,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get altBarcodes => $composableBuilder(
    column: $table.altBarcodes,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get sku => $composableBuilder(
    column: $table.sku,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get plu => $composableBuilder(
    column: $table.plu,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get unit => $composableBuilder(
    column: $table.unit,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<bool> get isWeight => $composableBuilder(
    column: $table.isWeight,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<bool> get isService => $composableBuilder(
    column: $table.isService,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<bool> get isFavorite => $composableBuilder(
    column: $table.isFavorite,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get quantity => $composableBuilder(
    column: $table.quantity,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get category => $composableBuilder(
    column: $table.category,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get imageUrl => $composableBuilder(
    column: $table.imageUrl,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get searchKey => $composableBuilder(
    column: $table.searchKey,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get promoRules => $composableBuilder(
    column: $table.promoRules,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get minQuantity => $composableBuilder(
    column: $table.minQuantity,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get purchasePrice => $composableBuilder(
    column: $table.purchasePrice,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ProductsTableAnnotationComposer
    extends Composer<_$AppDatabase, $ProductsTable> {
  $$ProductsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get name =>
      $composableBuilder(column: $table.name, builder: (column) => column);

  GeneratedColumn<double> get price =>
      $composableBuilder(column: $table.price, builder: (column) => column);

  GeneratedColumn<double> get wholesalePrice => $composableBuilder(
    column: $table.wholesalePrice,
    builder: (column) => column,
  );

  GeneratedColumn<String> get barcode =>
      $composableBuilder(column: $table.barcode, builder: (column) => column);

  GeneratedColumn<String> get altBarcodes => $composableBuilder(
    column: $table.altBarcodes,
    builder: (column) => column,
  );

  GeneratedColumn<String> get sku =>
      $composableBuilder(column: $table.sku, builder: (column) => column);

  GeneratedColumn<String> get plu =>
      $composableBuilder(column: $table.plu, builder: (column) => column);

  GeneratedColumn<String> get unit =>
      $composableBuilder(column: $table.unit, builder: (column) => column);

  GeneratedColumn<bool> get isWeight =>
      $composableBuilder(column: $table.isWeight, builder: (column) => column);

  GeneratedColumn<bool> get isService =>
      $composableBuilder(column: $table.isService, builder: (column) => column);

  GeneratedColumn<bool> get isFavorite => $composableBuilder(
    column: $table.isFavorite,
    builder: (column) => column,
  );

  GeneratedColumn<double> get quantity =>
      $composableBuilder(column: $table.quantity, builder: (column) => column);

  GeneratedColumn<String> get category =>
      $composableBuilder(column: $table.category, builder: (column) => column);

  GeneratedColumn<String> get imageUrl =>
      $composableBuilder(column: $table.imageUrl, builder: (column) => column);

  GeneratedColumn<String> get searchKey =>
      $composableBuilder(column: $table.searchKey, builder: (column) => column);

  GeneratedColumn<String> get promoRules => $composableBuilder(
    column: $table.promoRules,
    builder: (column) => column,
  );

  GeneratedColumn<double> get minQuantity => $composableBuilder(
    column: $table.minQuantity,
    builder: (column) => column,
  );

  GeneratedColumn<double> get purchasePrice => $composableBuilder(
    column: $table.purchasePrice,
    builder: (column) => column,
  );
}

class $$ProductsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ProductsTable,
          Product,
          $$ProductsTableFilterComposer,
          $$ProductsTableOrderingComposer,
          $$ProductsTableAnnotationComposer,
          $$ProductsTableCreateCompanionBuilder,
          $$ProductsTableUpdateCompanionBuilder,
          (Product, BaseReferences<_$AppDatabase, $ProductsTable, Product>),
          Product,
          PrefetchHooks Function()
        > {
  $$ProductsTableTableManager(_$AppDatabase db, $ProductsTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ProductsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$ProductsTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$ProductsTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> id = const Value.absent(),
                Value<String> name = const Value.absent(),
                Value<double> price = const Value.absent(),
                Value<double> wholesalePrice = const Value.absent(),
                Value<String?> barcode = const Value.absent(),
                Value<String> altBarcodes = const Value.absent(),
                Value<String?> sku = const Value.absent(),
                Value<String?> plu = const Value.absent(),
                Value<String> unit = const Value.absent(),
                Value<bool> isWeight = const Value.absent(),
                Value<bool> isService = const Value.absent(),
                Value<bool> isFavorite = const Value.absent(),
                Value<double> quantity = const Value.absent(),
                Value<String> category = const Value.absent(),
                Value<String?> imageUrl = const Value.absent(),
                Value<String> searchKey = const Value.absent(),
                Value<String> promoRules = const Value.absent(),
                Value<double> minQuantity = const Value.absent(),
                Value<double?> purchasePrice = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ProductsCompanion(
                id: id,
                name: name,
                price: price,
                wholesalePrice: wholesalePrice,
                barcode: barcode,
                altBarcodes: altBarcodes,
                sku: sku,
                plu: plu,
                unit: unit,
                isWeight: isWeight,
                isService: isService,
                isFavorite: isFavorite,
                quantity: quantity,
                category: category,
                imageUrl: imageUrl,
                searchKey: searchKey,
                promoRules: promoRules,
                minQuantity: minQuantity,
                purchasePrice: purchasePrice,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String id,
                required String name,
                Value<double> price = const Value.absent(),
                Value<double> wholesalePrice = const Value.absent(),
                Value<String?> barcode = const Value.absent(),
                Value<String> altBarcodes = const Value.absent(),
                Value<String?> sku = const Value.absent(),
                Value<String?> plu = const Value.absent(),
                Value<String> unit = const Value.absent(),
                Value<bool> isWeight = const Value.absent(),
                Value<bool> isService = const Value.absent(),
                Value<bool> isFavorite = const Value.absent(),
                Value<double> quantity = const Value.absent(),
                Value<String> category = const Value.absent(),
                Value<String?> imageUrl = const Value.absent(),
                Value<String> searchKey = const Value.absent(),
                Value<String> promoRules = const Value.absent(),
                Value<double> minQuantity = const Value.absent(),
                Value<double?> purchasePrice = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => ProductsCompanion.insert(
                id: id,
                name: name,
                price: price,
                wholesalePrice: wholesalePrice,
                barcode: barcode,
                altBarcodes: altBarcodes,
                sku: sku,
                plu: plu,
                unit: unit,
                isWeight: isWeight,
                isService: isService,
                isFavorite: isFavorite,
                quantity: quantity,
                category: category,
                imageUrl: imageUrl,
                searchKey: searchKey,
                promoRules: promoRules,
                minQuantity: minQuantity,
                purchasePrice: purchasePrice,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable<$ProductsTable, Product>(table),
                  BaseReferences<_$AppDatabase, $ProductsTable, Product>(
                    db,
                    table,
                    e,
                  ),
                ),
              )
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$ProductsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ProductsTable,
      Product,
      $$ProductsTableFilterComposer,
      $$ProductsTableOrderingComposer,
      $$ProductsTableAnnotationComposer,
      $$ProductsTableCreateCompanionBuilder,
      $$ProductsTableUpdateCompanionBuilder,
      (Product, BaseReferences<_$AppDatabase, $ProductsTable, Product>),
      Product,
      PrefetchHooks Function()
    >;
typedef $$OutboxTableCreateCompanionBuilder = OutboxCompanion Function({
  required String id,
  required String kind,
  required String body,
  required DateTime createdAt,
  Value<String> status,
  Value<int> attempts,
  Value<String?> lastError,
  Value<double> total,
  Value<String> summary,
  Value<String> paymentMethod,
  Value<String?> serverId,
  Value<String?> serverNumber,
  Value<String?> shiftId,
  Value<String> display,
  Value<int> rowid,
});
typedef $$OutboxTableUpdateCompanionBuilder = OutboxCompanion Function({
  Value<String> id,
  Value<String> kind,
  Value<String> body,
  Value<DateTime> createdAt,
  Value<String> status,
  Value<int> attempts,
  Value<String?> lastError,
  Value<double> total,
  Value<String> summary,
  Value<String> paymentMethod,
  Value<String?> serverId,
  Value<String?> serverNumber,
  Value<String?> shiftId,
  Value<String> display,
  Value<int> rowid,
});

class $$OutboxTableFilterComposer
    extends Composer<_$AppDatabase, $OutboxTable> {
  $$OutboxTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get kind => $composableBuilder(
    column: $table.kind,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get body => $composableBuilder(
    column: $table.body,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get status => $composableBuilder(
    column: $table.status,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get attempts => $composableBuilder(
    column: $table.attempts,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get lastError => $composableBuilder(
    column: $table.lastError,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<double> get total => $composableBuilder(
    column: $table.total,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get summary => $composableBuilder(
    column: $table.summary,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get paymentMethod => $composableBuilder(
    column: $table.paymentMethod,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get serverId => $composableBuilder(
    column: $table.serverId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get serverNumber => $composableBuilder(
    column: $table.serverNumber,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get shiftId => $composableBuilder(
    column: $table.shiftId,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get display => $composableBuilder(
    column: $table.display,
    builder: (column) => ColumnFilters(column),
  );
}

class $$OutboxTableOrderingComposer
    extends Composer<_$AppDatabase, $OutboxTable> {
  $$OutboxTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get kind => $composableBuilder(
    column: $table.kind,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get body => $composableBuilder(
    column: $table.body,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<DateTime> get createdAt => $composableBuilder(
    column: $table.createdAt,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get status => $composableBuilder(
    column: $table.status,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get attempts => $composableBuilder(
    column: $table.attempts,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get lastError => $composableBuilder(
    column: $table.lastError,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<double> get total => $composableBuilder(
    column: $table.total,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get summary => $composableBuilder(
    column: $table.summary,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get paymentMethod => $composableBuilder(
    column: $table.paymentMethod,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get serverId => $composableBuilder(
    column: $table.serverId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get serverNumber => $composableBuilder(
    column: $table.serverNumber,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get shiftId => $composableBuilder(
    column: $table.shiftId,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get display => $composableBuilder(
    column: $table.display,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$OutboxTableAnnotationComposer
    extends Composer<_$AppDatabase, $OutboxTable> {
  $$OutboxTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get kind =>
      $composableBuilder(column: $table.kind, builder: (column) => column);

  GeneratedColumn<String> get body =>
      $composableBuilder(column: $table.body, builder: (column) => column);

  GeneratedColumn<DateTime> get createdAt =>
      $composableBuilder(column: $table.createdAt, builder: (column) => column);

  GeneratedColumn<String> get status =>
      $composableBuilder(column: $table.status, builder: (column) => column);

  GeneratedColumn<int> get attempts =>
      $composableBuilder(column: $table.attempts, builder: (column) => column);

  GeneratedColumn<String> get lastError =>
      $composableBuilder(column: $table.lastError, builder: (column) => column);

  GeneratedColumn<double> get total =>
      $composableBuilder(column: $table.total, builder: (column) => column);

  GeneratedColumn<String> get summary =>
      $composableBuilder(column: $table.summary, builder: (column) => column);

  GeneratedColumn<String> get paymentMethod => $composableBuilder(
    column: $table.paymentMethod,
    builder: (column) => column,
  );

  GeneratedColumn<String> get serverId =>
      $composableBuilder(column: $table.serverId, builder: (column) => column);

  GeneratedColumn<String> get serverNumber => $composableBuilder(
    column: $table.serverNumber,
    builder: (column) => column,
  );

  GeneratedColumn<String> get shiftId =>
      $composableBuilder(column: $table.shiftId, builder: (column) => column);

  GeneratedColumn<String> get display =>
      $composableBuilder(column: $table.display, builder: (column) => column);
}

class $$OutboxTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $OutboxTable,
          OutboxData,
          $$OutboxTableFilterComposer,
          $$OutboxTableOrderingComposer,
          $$OutboxTableAnnotationComposer,
          $$OutboxTableCreateCompanionBuilder,
          $$OutboxTableUpdateCompanionBuilder,
          (OutboxData, BaseReferences<_$AppDatabase, $OutboxTable, OutboxData>),
          OutboxData,
          PrefetchHooks Function()
        > {
  $$OutboxTableTableManager(_$AppDatabase db, $OutboxTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$OutboxTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$OutboxTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$OutboxTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> id = const Value.absent(),
                Value<String> kind = const Value.absent(),
                Value<String> body = const Value.absent(),
                Value<DateTime> createdAt = const Value.absent(),
                Value<String> status = const Value.absent(),
                Value<int> attempts = const Value.absent(),
                Value<String?> lastError = const Value.absent(),
                Value<double> total = const Value.absent(),
                Value<String> summary = const Value.absent(),
                Value<String> paymentMethod = const Value.absent(),
                Value<String?> serverId = const Value.absent(),
                Value<String?> serverNumber = const Value.absent(),
                Value<String?> shiftId = const Value.absent(),
                Value<String> display = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => OutboxCompanion(
                id: id,
                kind: kind,
                body: body,
                createdAt: createdAt,
                status: status,
                attempts: attempts,
                lastError: lastError,
                total: total,
                summary: summary,
                paymentMethod: paymentMethod,
                serverId: serverId,
                serverNumber: serverNumber,
                shiftId: shiftId,
                display: display,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String id,
                required String kind,
                required String body,
                required DateTime createdAt,
                Value<String> status = const Value.absent(),
                Value<int> attempts = const Value.absent(),
                Value<String?> lastError = const Value.absent(),
                Value<double> total = const Value.absent(),
                Value<String> summary = const Value.absent(),
                Value<String> paymentMethod = const Value.absent(),
                Value<String?> serverId = const Value.absent(),
                Value<String?> serverNumber = const Value.absent(),
                Value<String?> shiftId = const Value.absent(),
                Value<String> display = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => OutboxCompanion.insert(
                id: id,
                kind: kind,
                body: body,
                createdAt: createdAt,
                status: status,
                attempts: attempts,
                lastError: lastError,
                total: total,
                summary: summary,
                paymentMethod: paymentMethod,
                serverId: serverId,
                serverNumber: serverNumber,
                shiftId: shiftId,
                display: display,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable<$OutboxTable, OutboxData>(table),
                  BaseReferences<_$AppDatabase, $OutboxTable, OutboxData>(
                    db,
                    table,
                    e,
                  ),
                ),
              )
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$OutboxTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $OutboxTable,
      OutboxData,
      $$OutboxTableFilterComposer,
      $$OutboxTableOrderingComposer,
      $$OutboxTableAnnotationComposer,
      $$OutboxTableCreateCompanionBuilder,
      $$OutboxTableUpdateCompanionBuilder,
      (OutboxData, BaseReferences<_$AppDatabase, $OutboxTable, OutboxData>),
      OutboxData,
      PrefetchHooks Function()
    >;
typedef $$SettingsTableCreateCompanionBuilder = SettingsCompanion Function({
  required String key,
  required String value,
  Value<int> rowid,
});
typedef $$SettingsTableUpdateCompanionBuilder = SettingsCompanion Function({
  Value<String> key,
  Value<String> value,
  Value<int> rowid,
});

class $$SettingsTableFilterComposer
    extends Composer<_$AppDatabase, $SettingsTable> {
  $$SettingsTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get key => $composableBuilder(
    column: $table.key,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get value => $composableBuilder(
    column: $table.value,
    builder: (column) => ColumnFilters(column),
  );
}

class $$SettingsTableOrderingComposer
    extends Composer<_$AppDatabase, $SettingsTable> {
  $$SettingsTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get key => $composableBuilder(
    column: $table.key,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get value => $composableBuilder(
    column: $table.value,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$SettingsTableAnnotationComposer
    extends Composer<_$AppDatabase, $SettingsTable> {
  $$SettingsTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get key =>
      $composableBuilder(column: $table.key, builder: (column) => column);

  GeneratedColumn<String> get value =>
      $composableBuilder(column: $table.value, builder: (column) => column);
}

class $$SettingsTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $SettingsTable,
          Setting,
          $$SettingsTableFilterComposer,
          $$SettingsTableOrderingComposer,
          $$SettingsTableAnnotationComposer,
          $$SettingsTableCreateCompanionBuilder,
          $$SettingsTableUpdateCompanionBuilder,
          (Setting, BaseReferences<_$AppDatabase, $SettingsTable, Setting>),
          Setting,
          PrefetchHooks Function()
        > {
  $$SettingsTableTableManager(_$AppDatabase db, $SettingsTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$SettingsTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$SettingsTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$SettingsTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback: ({
            Value<String> key = const Value.absent(),
            Value<String> value = const Value.absent(),
            Value<int> rowid = const Value.absent(),
          }) => SettingsCompanion(key: key, value: value, rowid: rowid),
          createCompanionCallback: ({
            required String key,
            required String value,
            Value<int> rowid = const Value.absent(),
          }) => SettingsCompanion.insert(key: key, value: value, rowid: rowid),
          withReferenceMapper: (p0) => p0
              .map(
                (e) => (
                  e.readTable<$SettingsTable, Setting>(table),
                  BaseReferences<_$AppDatabase, $SettingsTable, Setting>(
                    db,
                    table,
                    e,
                  ),
                ),
              )
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$SettingsTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $SettingsTable,
      Setting,
      $$SettingsTableFilterComposer,
      $$SettingsTableOrderingComposer,
      $$SettingsTableAnnotationComposer,
      $$SettingsTableCreateCompanionBuilder,
      $$SettingsTableUpdateCompanionBuilder,
      (Setting, BaseReferences<_$AppDatabase, $SettingsTable, Setting>),
      Setting,
      PrefetchHooks Function()
    >;

class $AppDatabaseManager {
  final _$AppDatabase _db;
  $AppDatabaseManager(this._db);
  $$ProductsTableTableManager get products =>
      $$ProductsTableTableManager(_db, _db.products);
  $$OutboxTableTableManager get outbox =>
      $$OutboxTableTableManager(_db, _db.outbox);
  $$SettingsTableTableManager get settings =>
      $$SettingsTableTableManager(_db, _db.settings);
}
