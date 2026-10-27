// 2026-10-27 (мобилка, видеозвонок, М2): инструменты мастера «добавить товар» в звонке. Два инструмента (меньше объявлений):
//  - `product_wizard` — шаги: start / photo / set / teach / cancel (не деньги: черновик живёт только в памяти звонка);
//  - `wizard_create` — создать товар. Режим «Спрашивать перед деньгами» показывает карточку «Сделать»; всегда нужен «да» вслух
//    ПОСЛЕ того, как программа зачитала итог (проверяет сам мастер), и числа, названные вслух (ворота).
// Каждое создание — в журнал `ai_actions.jsonl` и в стек отмены («отмени» удаляет созданный товар, пока он не менялся).
import '../../api/api_client.dart' show ApiException;
import '../../util/audit.dart';
import '../live_tools.dart';
import 'action_tools.dart';
import 'intent_gate.dart';
import 'product_wizard.dart';
import 'tool_registry.dart';
import 'undo.dart';
import 'wizard_texts.dart';

num? _num(Object? v) => v is num ? v : (v is String ? num.tryParse(v.trim().replaceAll(',', '.').replaceAll(RegExp(r'\s'), '')) : null);
double? _dbl(Object? v) => _num(v)?.toDouble();
String _s(Object? v) => v == null ? '' : '$v'.trim();

/// Подключить инструменты мастера к реестру звонка.
void registerWizardTools(ToolRegistry registry, ActionKit kit, ProductWizard wizard) {
  for (final t in buildWizardTools(kit, wizard)) {
    registry.register(t);
  }
}

List<AssistantTool> buildWizardTools(ActionKit k, ProductWizard w) => [_productWizard(k, w), _wizardCreate(k, w)];

WizardSide? _sideOf(Object? v) => switch (_s(v).toLowerCase()) {
  'front' => WizardSide.front,
  'back' => WizardSide.back,
  'side' => WizardSide.side,
  'extra' || 'more' || 'fourth' => WizardSide.extra,
  'barcode' => WizardSide.barcode,
  _ => null,
};

ToolResult _res(WizardOut o, ToolEnv env) => ToolResult(o.status, o.say, data: o.data, screen: env.screen.isEmpty ? null : env.screen);

AssistantTool _productWizard(ActionKit k, ProductWizard w) => AssistantTool(
  decl: ToolDecl(
    'product_wizard',
    'Add a NEW product during the video call, step by step. op: start (user said add/new product: read its say aloud at once) | '
        'photo (user said ready/back/side/barcode: takes a camera frame; side front|back|side|extra|barcode, default = the next one) | '
        'set (ONLY what the user said aloud: name, price, purchase, qty, category, unit, barcode digits, no_barcode, finish) | '
        'teach (the barcode already exists: teach the camera with these shots) | cancel. Read the say text as is. '
        'ru: добавь товар, готово, сзади, цена 85, хватит, отмена; ky: товар кош, даяр; uz: tovar qo\'sh, tayyor.',
    parameters: sObj(
      {
        'op': sStr(values: ['start', 'photo', 'set', 'teach', 'cancel']),
        'side': sStr(values: ['front', 'back', 'side', 'extra', 'barcode']),
        'name': sStr(),
        'price': sNum(desc: 'Sale price, som'),
        'purchase': sNum(desc: 'Purchase price, som'),
        'qty': sNum(desc: 'Stock to put'),
        'category': sStr(),
        'unit': sStr(),
        'barcode': sStr(desc: 'Digits the user said aloud'),
        'no_barcode': sBool(),
        'finish': sBool(desc: 'User said enough / skip: stop asking, read the summary'),
      },
      required: ['op'],
    ),
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canRoute('/admin/new-product'),
  needs: 'Склад',
  perTurnDedupe: true,
  gateFor: (a) => switch (_s(a['op'])) {
    'start' => const [GateReq(words: Intents.productCreate)],
    'photo' => const [GateReq(words: Intents.wizardShot)],
    'teach' => const [GateReq(words: Intents.wizardTeach)],
    'cancel' => const [GateReq(words: Intents.wizardCancel)],
    'set' => _setGate(a),
    _ => const [GateReq()],
  },
  run: (r) => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final turn = env.speech.last;
    try {
      switch (_s(r.args['op'])) {
        case 'start':
          return _res(w.start(lang, turn: turn), env);
        case 'photo':
          return _res(await w.shoot(_sideOf(r.args['side']), lang, turn: turn), env);
        case 'cancel':
          return _res(w.cancel(lang), env);
        case 'teach':
          final o = await w.teach(lang);
          if (o.ok) {
            await k.commit(
              r,
              tool: 'product_wizard',
              say: o.say,
              label: (l) => wt('teachLabel', l),
              data: {'op': 'teach', 'id': '${o.data?['id'] ?? ''}', 'photos': o.data?['photos']},
              whyNot: (l) => wt('teachNone', l),
            );
          }
          return _res(o, env);
        case 'set':
          return _res(
            await w.setFields(
              lang,
              name: _s(r.args['name']),
              price: _dbl(r.args['price']),
              purchase: _dbl(r.args['purchase']),
              qty: _dbl(r.args['qty']),
              category: _s(r.args['category']),
              unit: _s(r.args['unit']),
              barcode: _s(r.args['barcode']),
              noBarcode: r.args['no_barcode'] == true,
              finish: r.args['finish'] == true,
            ),
            env,
          );
      }
    } on ApiException {
      return ToolResult(ToolStatus.offline, toolMsg('error', lang));
    }
    return ToolResult(ToolStatus.clarify, toolMsg('notRequested', lang));
  }),
);

List<GateReq> _setGate(Map<String, dynamic> a) {
  final numeric = [
    for (final key in const ['price', 'purchase', 'qty', 'barcode'])
      if (a[key] != null && _s(a[key]).isNotEmpty) key,
  ];
  final reqs = <GateReq>[
    if (_s(a['name']).isNotEmpty) const GateReq(objects: ['name']),
    if (_s(a['category']).isNotEmpty) const GateReq(objects: ['category']),
    if (_s(a['unit']).isNotEmpty) const GateReq(words: Intents.unit),
    if (numeric.isNotEmpty) GateReq(numeric: numeric),
    if (a['no_barcode'] == true) const GateReq(words: Intents.noBarcode),
    if (a['finish'] == true) const GateReq(words: Intents.wizardFinish),
  ];
  return reqs.isEmpty ? const [GateReq(words: Intents.wizardFinish)] : reqs;
}

AssistantTool _wizardCreate(ActionKit k, ProductWizard w) => AssistantTool(
  decl: const ToolDecl(
    'wizard_create',
    'Create the product from the add-product wizard. Call it ONLY after the app read the summary aloud AND the user said yes to it. ru: да, создавай; ky: ооба; uz: ha.',
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canRoute('/admin/new-product'),
  needs: 'Склад',
  confirmInAskMode: true,
  intentWords: Intents.wizardConfirm,
  run: (r) => k.serial(() => wizardCreateRun(k, w, r)),
);

/// Создать товар из черновика мастера: инструмент `wizard_create` и кнопка «Создать» на экране звонка ([viaUi]). Журнал и отмена
/// (удалить созданный товар, пока он не менялся) — в обоих случаях.
Future<ToolResult> wizardCreateRun(ActionKit k, ProductWizard w, ToolRequest r, {bool viaUi = false}) async {
  final env = r.env;
  final lang = env.lang;
  final out = await w.create(lang, turn: env.speech.last, viaUi: viaUi);
  if (!out.ok) {
    final e = out.error;
    if (e is ApiException && e.isTransient) {
      return ToolResult(ToolStatus.offline, wt('createUnknown', lang, {'name': w.name}));
    }
    return _res(out, env);
  }
  final d = out.data ?? const {};
  final id = _s(d['id']);
  final name = _s(d['name']);
  final qty = _dbl(d['qty']) ?? 0;
  final price = _dbl(d['price']) ?? 0;
  Audit.log('product', 'ИИ: создан товар $name', amount: price);
  await k.commit(
    r,
    tool: 'wizard_create',
    say: out.say,
    label: (l) => '+ ${name.length > 30 ? name.substring(0, 29) : name}',
    data: {'id': id, 'name': name, 'price': price, 'qty': qty, 'photos': d['photos']},
    undoFn: (l) async {
      final p = await k.host.productById(id);
      if (p == null) return UndoResult.ok;
      // товар уже менялся после создания (продали, поправили остаток) — не удаляем
      if ((p.quantity - qty).abs() > 0.0005 || (p.price - price).abs() > 0.004) return UndoResult(UndoStatus.conflict, wt('undoBlocked', l, {'name': name}));
      await k.host.deleteProduct(id);
      if (k.focus?.last?.id == id) k.focus!.last = null;
      return UndoResult(UndoStatus.ok, wt('undoDeleted', l, {'name': name}));
    },
    amount: price,
  );
  return ToolResult(ToolStatus.ok, out.say, undo: true, data: d, screen: env.screen.isEmpty ? null : env.screen);
}
