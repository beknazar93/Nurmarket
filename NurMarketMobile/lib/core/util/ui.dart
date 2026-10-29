import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show FilteringTextInputFormatter, LengthLimitingTextInputFormatter, SystemUiOverlayStyle, TextInputFormatter;
import 'package:intl/intl.dart';

import '../../l10n/app_localizations.dart';
import '../theme/tokens.dart';

extension L10nX on BuildContext {
  AppLocalizations get l => AppLocalizations.of(this)!;
  Palette get pal => Palette.of(this);
}

final _money = NumberFormat('#,##0.##', 'ru');
final _qty = NumberFormat('#,##0.###', 'ru');

String money(double v) => _money.format(v);
String qtyText(double v) => _qty.format(v);

// 2026-10-22 (аудит П5): и неразрывные пробелы (так форматирует intl: «1 250,50») — раньше число не читалось
// 2026-10-29: \u00AB1.250,50\u00BB \u0438 \u00AB1,250.50\u00BB (Excel \u0438 \u043A\u0430\u043B\u044C\u043A\u0443\u043B\u044F\u0442\u043E\u0440 \u043F\u0438\u0448\u0443\u0442 \u043F\u043E-\u0440\u0430\u0437\u043D\u043E\u043C\u0443) \u2014 \u043F\u043E\u0441\u043B\u0435\u0434\u043D\u0438\u0439 \u0437\u043D\u0430\u043A \u0438\u0437 \u00AB.\u00BB \u0438 \u00AB,\u00BB \u0441\u0447\u0438\u0442\u0430\u0435\u0442\u0441\u044F
// \u0434\u0435\u0441\u044F\u0442\u0438\u0447\u043D\u044B\u043C, \u0432\u0442\u043E\u0440\u043E\u0439 \u2014 \u0440\u0430\u0437\u0434\u0435\u043B\u0438\u0442\u0435\u043B\u0435\u043C \u0442\u044B\u0441\u044F\u0447. \u0420\u0430\u043D\u044C\u0448\u0435 \u0442\u0430\u043A\u0438\u0435 \u0447\u0438\u0441\u043B\u0430 \u043D\u0435 \u0447\u0438\u0442\u0430\u043B\u0438\u0441\u044C \u0441\u043E\u0432\u0441\u0435\u043C. \u00AB12,75\u00BB \u0438 \u00AB12.75\u00BB \u2014 \u043A\u0430\u043A \u043F\u0440\u0435\u0436\u0434\u0435.
double? parseNum(String? s) {
  final t = (s ?? '').replaceAll(RegExp('[ \u00A0\u202F]'), '');
  final dot = t.lastIndexOf('.'), comma = t.lastIndexOf(',');
  if (dot >= 0 && comma >= 0) {
    final decimal = dot > comma ? '.' : ',';
    final thousands = decimal == '.' ? ',' : '.';
    return double.tryParse(t.replaceAll(thousands, '').replaceAll(decimal, '.'));
  }
  return double.tryParse(t.replaceAll(',', '.'));
}

// 2026-10-29 (владелец: штрихкод — только цифры): поля «Штрихкод» и «Доп. штрихкод» отбрасывают всё, кроме цифр (буквы, пробелы,
// дефисы — и при наборе, и при вставке: «487 000-115 9955» → 4870001159955), длина 4–14. Поля поиска «название/код» не ограничиваются.
final barcodeFormatters = <TextInputFormatter>[FilteringTextInputFormatter.digitsOnly, LengthLimitingTextInputFormatter(14)];

/// Штрихкод пустой (не обязателен) или 4–14 цифр.
bool barcodeFieldOk(String s) {
  final t = s.trim();
  return t.isEmpty || (t.length >= 4 && t.length <= 14 && RegExp(r'^\d+$').hasMatch(t));
}

/// Подпись над полем — заглавными, мелко (доска 5).
/// 2026-10-21: [upper] = false — обычными буквами (правило редизайна «без ЗАГЛАВНЫХ», экраны КП и счетов).
class FieldLabel extends StatelessWidget {
  final String text;
  final bool upper;
  const FieldLabel(this.text, {super.key, this.upper = true});
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 6, top: 4),
    child: Text(
      upper ? text.toUpperCase() : text,
      style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, letterSpacing: .4, color: context.pal.textSoft),
    ),
  );
}

/// Белая карточка с рамкой.
class Panel extends StatelessWidget {
  final Widget child;
  final EdgeInsets padding;
  final VoidCallback? onTap;
  final Color? color;
  const Panel({super.key, required this.child, this.padding = const EdgeInsets.all(14), this.onTap, this.color});
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Material(
      color: color ?? p.panel,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(T.radiusCard),
        side: BorderSide(color: color == null ? p.border : Colors.transparent),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(padding: padding, child: child),
      ),
    );
  }
}

/// Квадратная кнопка «назад» как на макетах.
/// 2026-10-26 (живой аудит, П1, Н-2): касание 48 dp (было 42) и подпись «Назад» для TalkBack.
class BackSquare extends StatelessWidget {
  const BackSquare({super.key});
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(right: 12),
    child: Semantics(
      button: true,
      label: context.l.a11yBack,
      excludeSemantics: true,
      child: Material(
        color: context.pal.panel,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(12),
          side: BorderSide(color: context.pal.border),
        ),
        child: InkWell(
          borderRadius: BorderRadius.circular(12),
          onTap: () => Navigator.of(context).maybePop(),
          child: const SizedBox(width: 48, height: 48, child: Icon(Icons.chevron_left_rounded)),
        ),
      ),
    ),
  );
}

/// Заголовок экрана: «назад», название и подпись.
/// 2026-10-26 (живой аудит, П1, В-4): высота шапки считается от размера шрифта системы — при 1,3× шапка (фикс. 72 dp)
/// переполнялась на 4 px. Экраны ставят её в `appBar:`, а там высота известна заранее без контекста, поэтому масштаб
/// берётся из платформы (тот же, что и у MediaQuery.textScaler приложения).
class ScreenTitle extends StatelessWidget implements PreferredSizeWidget {
  final String title;
  final String? subtitle;
  final bool back;
  final List<Widget> actions;
  const ScreenTitle(this.title, {super.key, this.subtitle, this.back = true, this.actions = const []});

  /// Отступы шапки: сверху 12, снизу 8.
  static const _vPad = 20.0;

  @override
  Size get preferredSize {
    final s = WidgetsBinding.instance.platformDispatcher.textScaleFactor.clamp(1.0, 2.5);
    // строка названия 20 sp и подписи 12 sp с запасом на высоту строки Montserrat (по снимкам аудита ≈1,35)
    final text = 20 * 1.45 * s + (subtitle != null ? 12 * 1.45 * s : 0);
    final content = text > 48 ? text : 48.0;
    final h = _vPad + content;
    return Size.fromHeight(h > 72 ? h.ceilToDouble() : 72);
  }

  @override
  Widget build(BuildContext context) => SafeArea(
    bottom: false,
    child: Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
      child: Row(
        children: [
          if (back) const BackSquare(),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
                ),
                if (subtitle != null)
                  Text(
                    subtitle!,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(fontSize: 12, color: context.pal.textSoft),
                  ),
              ],
            ),
          ),
          ...actions,
        ],
      ),
    ),
  );
}

/// 2026-10-26 (живой аудит, П1, Н-1): экран с ТЁМНОЙ шапкой в светлой теме (вход, главная со сменой) — значки строки
/// состояния (время, сеть) на нём не видны. Оберните такой экран: значки станут светлыми. На светлой шапке в светлой теме
/// и во всей тёмной теме стиль выбирает само приложение (app.dart).
class LightStatusBar extends StatelessWidget {
  final Widget child;
  const LightStatusBar({super.key, required this.child});
  @override
  Widget build(BuildContext context) => AnnotatedRegion<SystemUiOverlayStyle>(value: statusBarStyle(dark: true), child: child);
}

/// Стиль строки состояния: [dark] — тёмный фон под ней (значки светлые).
SystemUiOverlayStyle statusBarStyle({required bool dark}) => SystemUiOverlayStyle(
  statusBarColor: Colors.transparent,
  statusBarIconBrightness: dark ? Brightness.light : Brightness.dark,
  statusBarBrightness: dark ? Brightness.dark : Brightness.light, // iOS: наоборот
);

/// Первые буквы названия — на плитке товара без фото.
String initials(String name) {
  final words = name.replaceAll(RegExp(r'[«»"()]'), ' ').split(RegExp(r'\s+')).where((w) => w.isNotEmpty).toList();
  if (words.isEmpty) return '?';
  final a = words.first.characters.first.toUpperCase();
  final b = words.length > 1 ? words[1].characters.first.toLowerCase() : (words.first.characters.length > 1 ? words.first.characters.elementAt(1) : '');
  return '$a$b';
}

/// 13.10, владелец: «почему в мобилке не отображаются фотки товаров?» — фото товара в списках (склад, поиск) и на плитке
/// продажи. Картинка декодируется в размер плитки (cacheWidth), а не целиком: на слабых телефонах сотни фото по 1000 px
/// забивали память. Нет фото или не загрузилось — буквы названия, как раньше.
class ProductThumb extends StatelessWidget {
  final String name;
  final String? url;
  final double? size;
  final double radius;
  final double fontSize;
  const ProductThumb({super.key, required this.name, this.url, this.size, this.radius = 10, this.fontSize = 13});

  @override
  Widget build(BuildContext context) {
    final letters = Container(
      color: tileColor(name),
      alignment: Alignment.center,
      child: Text(
        initials(name),
        style: TextStyle(fontSize: fontSize, fontWeight: FontWeight.w800, color: T.navy),
      ),
    );
    final px = ((size ?? 160) * MediaQuery.devicePixelRatioOf(context)).round().clamp(64, 480);
    final child = url == null || url!.isEmpty
        ? letters
        : Image.network(
            url!,
            fit: BoxFit.cover,
            cacheWidth: px,
            gaplessPlayback: true,
            errorBuilder: (_, _, _) => letters,
            loadingBuilder: (_, w, progress) => progress == null ? w : letters,
          );
    return ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      child: size == null ? child : SizedBox(width: size, height: size, child: child),
    );
  }
}

Color tileColor(String name) {
  const colors = [Color(0xFFFFF7CC), Color(0xFFFDE2E1), Color(0xFFEDE7FB), Color(0xFFE3F2FD), Color(0xFFE7F6EC), Color(0xFFFFEBD6)];
  return colors[name.hashCode.abs() % colors.length];
}

void toast(BuildContext context, String text, {bool error = false}) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(text), backgroundColor: error ? T.danger : T.navy));
}

/// Спросить число (сумма, количество, вес).
// 2026-10-26 (живой аудит, П2, Н-10): необязательные подпись поля [label] и единица [suffix] (сом) — поле открытия смены было без подписи.
Future<double?> askNumber(BuildContext context, {required String title, String? hint, double? initial, String? ok, String? label, String? suffix}) async {
  // 2026-10-21, владелец: «на Enter при добавлении товара в КП и счёт он удаляет или дублирует». Число в поле не было
  // выделено — «1» и набранное «2» давали 12 (количество «удваивалось»); Enter и «Готово» подряд закрывали окно дважды —
  // второе закрытие уводило с экрана документа. Теперь прежнее число выделено (набор заменяет его), окно закрывается один раз.
  final text = initial == null ? '' : qtyText(initial).replaceAll(' ', '');
  final ctrl = TextEditingController.fromValue(
    TextEditingValue(
      text: text,
      selection: TextSelection(baseOffset: 0, extentOffset: text.length),
    ),
  );
  var closed = false;
  void close(BuildContext c, [double? v]) {
    if (closed) return;
    closed = true;
    Navigator.pop(c, v);
  }

  final v = await showDialog<double>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
      content: TextField(
        controller: ctrl,
        autofocus: true,
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        textInputAction: TextInputAction.done,
        decoration: InputDecoration(hintText: hint, labelText: label, suffixText: suffix),
        onSubmitted: (s) => close(c, parseNum(s)),
      ),
      actions: [
        TextButton(onPressed: () => close(c), child: Text(context.l.cancel)),
        FilledButton(
          style: FilledButton.styleFrom(minimumSize: const Size(110, 48)), // 2026-10-26 (П1): касание 48 dp
          onPressed: () => close(c, parseNum(ctrl.text)),
          child: Text(ok ?? context.l.done),
        ),
      ],
    ),
  );
  return v;
}

/// 13.10, владелец: «в новом товаре при цене добавь процент и сумму на маржу». Наценка процентом и суммой рядом с ценами:
/// меняете процент или сумму — пересчитывается цена продажи; меняете закупку — цена считается от той же наценки;
/// меняете цену — пересчитываются процент и сумма.
class MarginFields extends StatefulWidget {
  final TextEditingController cost;
  final TextEditingController price;
  final double initialPercent;
  const MarginFields({super.key, required this.cost, required this.price, this.initialPercent = 30});
  @override
  State<MarginFields> createState() => _MarginFieldsState();
}

class _MarginFieldsState extends State<MarginFields> {
  final _pct = TextEditingController();
  final _sum = TextEditingController();
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    widget.cost.addListener(_costChanged);
    widget.price.addListener(_priceChanged);
    final c = parseNum(widget.cost.text) ?? 0;
    final p = parseNum(widget.price.text) ?? 0;
    if (c > 0 && p > 0) {
      _fromPrice();
    } else {
      _pct.text = qtyText(widget.initialPercent);
    }
  }

  @override
  void dispose() {
    widget.cost.removeListener(_costChanged);
    widget.price.removeListener(_priceChanged);
    super.dispose();
  }

  String _fmt(double v) => qtyText(v).replaceAll(' ', '').replaceAll(' ', '');

  void _set(TextEditingController c, String text) {
    if (c.text == text) return;
    c.value = TextEditingValue(
      text: text,
      selection: TextSelection.collapsed(offset: text.length),
    );
  }

  void _costChanged() {
    if (_busy) return;
    final c = parseNum(widget.cost.text) ?? 0;
    final pct = parseNum(_pct.text);
    if (c <= 0 || pct == null) return;
    _busy = true;
    final price = (c * (1 + pct / 100) * 100).round() / 100;
    _set(widget.price, _fmt(price));
    _set(_sum, _fmt(price - c));
    _busy = false;
  }

  void _priceChanged() {
    if (_busy) return;
    _busy = true;
    _fromPrice();
    _busy = false;
  }

  void _fromPrice() {
    final c = parseNum(widget.cost.text) ?? 0;
    final p = parseNum(widget.price.text) ?? 0;
    if (c <= 0 || p <= 0) return;
    _set(_sum, _fmt(p - c));
    _set(_pct, _fmt(((p - c) / c * 100 * 10).round() / 10));
  }

  void _pctChanged(String v) {
    final c = parseNum(widget.cost.text) ?? 0;
    final pct = parseNum(v);
    if (c <= 0 || pct == null) return;
    _busy = true;
    final price = (c * (1 + pct / 100) * 100).round() / 100;
    _set(widget.price, _fmt(price));
    _set(_sum, _fmt(price - c));
    _busy = false;
  }

  void _sumChanged(String v) {
    final c = parseNum(widget.cost.text) ?? 0;
    final sum = parseNum(v);
    if (c <= 0 || sum == null) return;
    _busy = true;
    _set(widget.price, _fmt(c + sum));
    _set(_pct, _fmt((sum / c * 100 * 10).round() / 10));
    _busy = false;
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final c = parseNum(widget.cost.text) ?? 0;
    final p = parseNum(widget.price.text) ?? 0;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: TextField(
                controller: _pct,
                keyboardType: const TextInputType.numberWithOptions(decimal: true, signed: true),
                decoration: InputDecoration(labelText: l.marginPercent, suffixText: '%'),
                onChanged: _pctChanged,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: TextField(
                controller: _sum,
                keyboardType: const TextInputType.numberWithOptions(decimal: true, signed: true),
                decoration: InputDecoration(labelText: l.marginSum, suffixText: l.som),
                onChanged: _sumChanged,
              ),
            ),
          ],
        ),
        if (c > 0 && p > 0 && p < c)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              l.belowCost,
              style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700, fontSize: 12),
            ),
          ),
      ],
    );
  }
}

/// 14.10, владелец: «если случайно нажать "назад" — спросить: "Вы точно хотите выйти?" — "Да, конечно" / "Нет, случайно
/// нажал"». Оборачивает экран с несохранёнными данными; [ask] — false, когда терять нечего (выходим сразу).
class LeaveGuard extends StatelessWidget {
  final bool Function() ask;
  final Widget child;
  const LeaveGuard({super.key, required this.ask, required this.child});

  @override
  Widget build(BuildContext context) => PopScope(
    canPop: false,
    onPopInvokedWithResult: (didPop, _) async {
      if (didPop) return;
      final nav = Navigator.of(context);
      if (!ask()) {
        nav.pop();
        return;
      }
      final l = context.l;
      final leave = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: Text(l.leaveAsk),
          content: Text(l.leaveAskSub),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.leaveNo)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.leaveYes)),
          ],
        ),
      );
      if (leave == true) nav.pop();
    },
    child: child,
  );
}
