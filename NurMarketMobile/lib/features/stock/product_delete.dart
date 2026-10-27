import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/cam_aliases.dart';
import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';
import '../admin/admin_repository.dart';

// 2026-10-27 (мобилка, М3): владелец — «в мобилке нет возможности удаления!». Удаление товара было, но пряталось в «⋮».
// Теперь: видимая кнопка в карточке, право, проверка неотправленных операций, остаток → подтверждение вводом названия,
// понятные ответы сервера (400/403/404/409), запись в журнал действий телефона. Новых запросов к серверу нет — тот же
// DELETE products/{id}/, что у кассы.

/// Удалять товары: то же право, что открывает «Склад → товар» и совпадает с правом сервера (`can_view_products`;
/// у кассира без флага сервер ответит 403). Без сессии — нельзя.
bool canDeleteProducts(Session? s) => s != null && s.canRoute('/admin/new-product');

/// Чем кончилась попытка удаления.
enum ProductDeleteOutcome {
  /// Сервер удалил, товар убран и с телефона.
  deleted,

  /// Сервер ответил 404 — товара там уже нет; убран только с телефона.
  alreadyGone,

  /// 403 — у сотрудника нет права.
  forbidden,

  /// 400/409/422 — сервер не разрешил (например, товар в документах).
  refused,

  /// Нет связи, таймаут или ошибка сервера 5xx/429 — запрос мог не дойти, товар НЕ удалён.
  offline,

  /// Любая другая ошибка.
  failed,
}

class ProductDeleteResult {
  final ProductDeleteOutcome outcome;
  final String message;
  const ProductDeleteResult(this.outcome, [this.message = '']);
  bool get removed => outcome == ProductDeleteOutcome.deleted || outcome == ProductDeleteOutcome.alreadyGone;
}

/// Сколько неотправленных операций (продажи, приёмки, ревизии) ещё ссылаются на товар. Пока они в очереди, удалять
/// нельзя: при отправке сервер не найдёт товар, и продажа зависнет.
Future<int> pendingOpsFor(AppDatabase db, String productId) async {
  var n = 0;
  final rows = await db.select(db.outbox).get();
  for (final r in rows) {
    // 2026-10-27 (ревью М-11): «снятые» (dropped) больше не отправляются — они удалению товара не мешают
    if (r.status == 'sent' || r.status == 'dropped') continue;
    try {
      if (_mentions(jsonDecode(r.body), productId)) n++;
    } catch (_) {
      // не разобрали запись — считаем, что товар в ней может быть (безопаснее не удалять)
      if (r.body.contains(productId)) n++;
    }
  }
  return n;
}

const _idKeys = {'product', 'product_id', 'productId', 'id'};

bool _mentions(Object? node, String id, {String? key}) {
  if (node is Map) {
    for (final e in node.entries) {
      if (_mentions(e.value, id, key: '${e.key}')) return true;
    }
    return false;
  }
  if (node is List) return node.any((x) => _mentions(x, id, key: key));
  // ключ «id» у самой операции — это ключ идемпотентности, а не товар; товар ищем только по product*
  return key != null && key != 'id' && _idKeys.contains(key) && '$node' == id;
}

/// Удаление товара на сервере и на телефоне, разбор ответов, запись в журнал. Диалогов нет — их показывает
/// [confirmAndDeleteProduct]; здесь логика, которую можно проверить без экрана.
class ProductDeleter {
  final AdminRepository admin;
  ProductDeleter(this.admin);

  /// [logLabel] — начало записи журнала («Удалить товар»), [stock] — остаток на момент подтверждения.
  Future<ProductDeleteResult> run(Product p, {required String logLabel, required String failLabel, double? stock}) async {
    final qty = stock ?? p.quantity;
    final what = '$logLabel: ${p.name} · ${qtyText(qty)} ${p.unit} · #${p.id}';
    try {
      await admin.deleteProduct(p.id);
      unawaited(CamAliases.forget(admin.db, p.id).catchError((_) {})); // 2026-10-27 (ревью М-3): названия камеры удалённого товара не нужны
      unawaited(Audit.log('delete', what)); // 2026-10-27: журнал пишется пачкой, экран его не ждёт
      return const ProductDeleteResult(ProductDeleteOutcome.deleted);
    } on ApiException catch (e) {
      final status = e.status;
      ProductDeleteResult res;
      if (status == 404) {
        // admin.deleteProduct пробует два адреса и при 404 на обоих бросает последнюю ошибку: товара на сервере нет
        await admin.db.deleteProduct(p.id);
        res = const ProductDeleteResult(ProductDeleteOutcome.alreadyGone);
      } else if (status == 403 || status == 401) {
        res = ProductDeleteResult(ProductDeleteOutcome.forbidden, e.message);
      } else if (status == 400 || status == 409 || status == 422) {
        // 409 попадает в isTransient у ApiClient (для денег), но для удаления это отказ сервера, а не обрыв
        res = ProductDeleteResult(ProductDeleteOutcome.refused, e.message);
      } else if (e.isTransient) {
        res = ProductDeleteResult(ProductDeleteOutcome.offline, e.message);
      } else {
        res = ProductDeleteResult(ProductDeleteOutcome.failed, e.message);
      }
      unawaited(Audit.log('delete', res.outcome == ProductDeleteOutcome.alreadyGone ? what : '$failLabel: $what · ${status ?? '—'}'));
      return res;
    }
  }
}

/// Текст для кассира по итогу удаления; null — всё прошло, отдельного сообщения не нужно (показываем «Товар удалён»).
String deleteOutcomeText(BuildContext context, ProductDeleteResult r) {
  final l = context.l;
  switch (r.outcome) {
    case ProductDeleteOutcome.deleted:
      return l.productDeleted;
    case ProductDeleteOutcome.alreadyGone:
      return l.pdGone;
    case ProductDeleteOutcome.forbidden:
      return l.pdNoRights;
    case ProductDeleteOutcome.refused:
      return l.pdRefused(r.message);
    case ProductDeleteOutcome.offline:
      return l.pdOffline;
    case ProductDeleteOutcome.failed:
      return l.pdFailed(r.message);
  }
}

/// Название введено верно: без учёта регистра и пробелов по краям.
bool deleteNameMatches(String typed, String name) => typed.trim().toLowerCase() == name.trim().toLowerCase() && name.trim().isNotEmpty;

/// Подтверждение и удаление товара. true — товар удалён (или его уже не было на сервере).
/// [stock] — остаток из карточки (свежий с сервера); без него берётся остаток с телефона.
/// Остаток > 0, а также любой сотрудник без прав администратора — подтверждение вводом названия товара.
Future<bool> confirmAndDeleteProduct(BuildContext context, WidgetRef ref, Product p, {double? stock}) async {
  final l = context.l;
  final session = ref.read(sessionProvider);
  if (!canDeleteProducts(session)) {
    toast(context, l.pdNoRights, error: true);
    return false;
  }
  final db = ref.read(databaseProvider);
  final queued = await pendingOpsFor(db, p.id);
  if (!context.mounted) return false;
  if (queued > 0) {
    toast(context, l.pdQueueBlock(queued), error: true);
    return false;
  }
  final qty = stock ?? p.quantity;
  final needName = (!p.isService && qty > 0) || !(session?.isAdmin ?? false);
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => _DeleteDialog(product: p, stock: qty, needName: needName),
  );
  if (ok != true || !context.mounted) return false;
  final deleter = ProductDeleter(AdminRepository(ref.read(apiProvider), db));
  final res = await deleter.run(p, logLabel: l.deleteProduct, failLabel: l.pdAuditFail, stock: qty);
  if (context.mounted) toast(context, deleteOutcomeText(context, res), error: !res.removed);
  return res.removed;
}

class _DeleteDialog extends StatefulWidget {
  final Product product;
  final double stock;
  final bool needName;
  const _DeleteDialog({required this.product, required this.stock, required this.needName});
  @override
  State<_DeleteDialog> createState() => _DeleteDialogState();
}

class _DeleteDialogState extends State<_DeleteDialog> {
  final _typed = TextEditingController();

  @override
  void dispose() {
    _typed.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = widget.product;
    final ready = !widget.needName || deleteNameMatches(_typed.text, p.name);
    const btn = Size(64, 48); // касание не меньше 48 dp
    return AlertDialog(
      key: const Key('deleteProductDialog'),
      title: Text(l.deleteProduct),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l.pdAsk(p.name)),
            if (widget.stock > 0 && !p.isService) ...[
              const SizedBox(height: 10),
              Semantics(
                liveRegion: true,
                child: Text(
                  l.pdStockWarn(qtyText(widget.stock), p.unit),
                  key: const Key('deleteStockWarn'),
                  style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700),
                ),
              ),
            ],
            if (widget.needName) ...[
              const SizedBox(height: 12),
              Text(l.pdTypeName),
              const SizedBox(height: 4),
              // название видно целиком (в подсказке поля оно обрезалось бы), его и вводим
              Semantics(label: p.name, child: Text(p.name, key: const Key('deleteNameShown'), style: const TextStyle(fontWeight: FontWeight.w800))),
              const SizedBox(height: 6),
              TextField(
                key: const Key('deleteNameField'),
                controller: _typed,
                autofocus: true,
                onChanged: (_) => setState(() {}),
                decoration: InputDecoration(helperText: l.pdTypeHint, helperMaxLines: 2),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(key: const Key('deleteCancel'), style: TextButton.styleFrom(minimumSize: btn), onPressed: () => Navigator.pop(context, false), child: Text(l.cancel)),
        FilledButton(
          key: const Key('deleteConfirm'),
          style: FilledButton.styleFrom(backgroundColor: T.danger, foregroundColor: Colors.white, minimumSize: btn),
          onPressed: ready ? () => Navigator.pop(context, true) : null,
          child: Text(l.deleteProduct),
        ),
      ],
    );
  }
}

/// Видимая кнопка «Удалить товар» внизу карточки. Не показывается, если права нет.
class DeleteProductButton extends StatelessWidget {
  final VoidCallback onPressed;
  const DeleteProductButton({super.key, required this.onPressed});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Semantics(
      button: true,
      label: l.deleteProduct,
      excludeSemantics: true,
      child: OutlinedButton.icon(
        key: const Key('deleteProductButton'),
        style: OutlinedButton.styleFrom(
          minimumSize: const Size.fromHeight(52),
          foregroundColor: T.danger,
          side: const BorderSide(color: T.danger),
        ),
        onPressed: onPressed,
        icon: const Icon(Icons.delete_outline_rounded),
        label: Text(l.deleteProduct),
      ),
    );
  }
}
