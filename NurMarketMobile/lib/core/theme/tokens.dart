import 'package:flutter/material.dart';

/// Токены темы NurMarket (ТЗ мобильного приложения, раздел 2; доска «Элементы интерфейса»).
/// 2026-10-22, брендбук NUR CRM: Golden Light #FFB208 (акцент), Deep Night #191919 (чёрный; имя `navy` оставлено, чтобы не править
/// десятки экранов — теперь это тёмно-серый брендбука), серая шкала #3D3D3D … #EEE. Текст на жёлтом — только `navy` (9,7:1);
/// белый на жёлтом — 1,8:1, жёлтый текст на белом запрещён. Оранжевый Orange Flame #F77B14 — [brandOrange]; Wood Brown — [brandBrown].
class T {
  static const window = Color(0xFFF4F4F4);
  static const panel = Color(0xFFFFFFFF);
  static const soft = Color(0xFFF9FAFB);
  static const border = Color(0xFFE2E2E2);
  static const borderStrong = Color(0xFFCCCCCC);
  static const text = Color(0xFF191919);
  static const textSoft = Color(0xFF666666);
  static const accent = Color(0xFFFFB208);
  static const accentSoft = Color(0xFFFFF1DB);
  static const navy = Color(0xFF191919);
  static const brandOrange = Color(0xFFF77B14);
  static const brandYellow2 = Color(0xFFF9D919);
  static const brandBrown = Color(0xFF7C460B);
  static const success = Color(0xFF0E7A3A);
  static const successSoft = Color(0xFFE7F6EC);
  static const danger = Color(0xFFC0362C);
  static const dangerSoft = Color(0xFFFDECEA);
  static const warn = Color(0xFFB54708);
  static const warnSoft = Color(0xFFFEF3E2);

  // Тёмная тема
  static const darkWindow = Color(0xFF191919);
  // 2026-10-26 (живой аудит, П1, С-7): карточки и рамки в тёмной теме сливались с фоном — панель и рамка чуть светлее.
  static const darkPanel = Color(0xFF2A2A2A);
  static const darkBorder = Color(0xFF4A4A4A);
  static const darkText = Color(0xFFF5F5F5);
  static const darkTextSoft = Color(0xFFAAAAAA);

  /// 2026-10-26, владелец: «в аналитике, сводке используй РАЗНЫЕ ЦВЕТА в диаграммах». Единая палитра диаграмм (та же, что в
  /// кассе: BarChartRenderer.Palette). 12 цветов по кругу, порядок — от самых несхожих: первые 5–6 различимы даже рядом.
  /// Светлота подобрана так, что каждый цвет держит контраст не ниже 3:1 (AA для графики) и на белом, и на тёмной панели
  /// #2A2A2A; чёрный или белый текст поверх цвета — [ChartColors.onColor] — не ниже 4,5:1. Различимы при дальтонизме:
  /// наименьшая разница пар (ΔE2000) — 12 в обычном зрении, 6 при протанопии/дейтеранопии/тританопии. Не красный/зелёный рядом.
  /// Золотой — тёмный брендовый жёлтый (чистый #FFB208 на белом — 1,8:1, его оставили для акцента «лучший день»).
  static const List<Color> chartColors = [
    Color(0xFFB8860B), // золотой
    Color(0xFF5470E1), // синий
    Color(0xFFEE3083), // розовый
    Color(0xFF3E9157), // зелёный
    Color(0xFF9F81DA), // фиолетовый
    Color(0xFFD16B60), // коралловый (красный)
    Color(0xFFC740B1), // пурпурный
    Color(0xFF78859A), // серый
    Color(0xFF6B8137), // оливковый
    Color(0xFFC95523), // оранжевый
    Color(0xFF0DA38A), // бирюзовый
    Color(0xFF1F80B1), // голубой
  ];

  static const radiusCard = 16.0;
  static const radiusButton = 12.0;
  static const radiusCta = 16.0;
  static const ctaHeight = 56.0;
  static const fieldHeight = 48.0;
}

/// Цвета, которые зависят от светлой или тёмной темы.
class Palette {
  final bool dark;
  const Palette(this.dark);

  Color get window => dark ? T.darkWindow : T.window;
  Color get panel => dark ? T.darkPanel : T.panel;
  Color get border => dark ? T.darkBorder : T.border;
  Color get text => dark ? T.darkText : T.text;
  Color get textSoft => dark ? T.darkTextSoft : T.textSoft;
  Color get price => dark ? T.accent : T.navy;

  /// 2026-10-29 (инфографика, спека Fable): подложка «soft» и сильная рамка — по спеке 03-mobile-infographics.
  Color get soft => dark ? const Color(0xFF333333) : T.soft;
  Color get borderStrong => dark ? const Color(0xFF5A5A5A) : T.borderStrong;

  /// 13.10, владелец: «кнопка плюс сливается с фоном» — главные кнопки (+, сканер) в тёмной теме жёлтые с тёмным значком.
  Color get action => dark ? T.accent : T.navy;
  Color get onAction => dark ? T.navy : Colors.white;

  /// 13.10, владелец (снимок тёмной темы: «текст просроченного не отображается»): светлые подложки предупреждений
  /// в тёмной теме — приглушённые, иначе светлый текст на них не читается.
  Color get dangerSoft => dark ? const Color(0x40F04438) : T.dangerSoft;
  Color get warnSoft => dark ? const Color(0x40F79009) : T.warnSoft;
  Color get successSoft => dark ? const Color(0x4012B76A) : T.successSoft;
  Color get accentSoft => dark ? const Color(0x33FFB208) : T.accentSoft;

  /// 2026-10-26 (живой аудит, П1, С-7): «успех», «ошибка», «внимание» как цвет ТЕКСТА и значков. В светлой теме — прежние
  /// (контраст с белым ≥ 4,5:1), в тёмной — светлее (на #191919/#2A2A2A контраст ≥ 4,5:1; тёмно-зелёный 0E7A3A там ~3:1).
  /// `T.success`/`T.danger` как цвет текста в тёмной теме не использовать — брать отсюда.
  Color get success => dark ? const Color(0xFF4ADE80) : T.success;
  Color get danger => dark ? const Color(0xFFFF8A80) : T.danger;
  Color get warn => dark ? const Color(0xFFFFB74D) : T.warn;

  /// Тёмная «героевая» карточка (смена на главной, итог оплаты): в светлой теме — navy; в тёмной navy = фон окна и карточка
  /// терялась, поэтому — светлее фона и с рамкой.
  Color get heroBg => dark ? const Color(0xFF333333) : T.navy;
  Color get heroBorder => dark ? const Color(0xFF5A5A5A) : Colors.transparent;

  /// Аватар (инициалы): в тёмной теме navy сливался с фоном — серая заливка и жёлтая рамка; буквы всегда светлые.
  Color get avatarBg => dark ? const Color(0xFF3D3D3D) : T.navy;
  Color get avatarBorder => dark ? T.accent : Colors.transparent;
  Color get onAvatar => Colors.white;

  static Palette of(BuildContext context) => Palette(Theme.of(context).brightness == Brightness.dark);
}

/// 2026-10-26, владелец: «в аналитике, сводке используй РАЗНЫЕ ЦВЕТА в диаграммах». Раздача цветов [T.chartColors].
/// Цвет категории (товар, способ оплаты, филиал…) берётся по хэшу ключа, а не по порядку в списке: при смене сортировки
/// и обновлении данных цвета не «прыгают», а тот же товар в кассе и в телефоне — того же цвета (хэш и палитра общие).
class ChartColors {
  ChartColors._();

  /// FNV-1a 32 бит по кодам UTF-16 строки в нижнем регистре без пробелов по краям (в кассе — тот же алгоритм).
  static int hashOf(String key) {
    var h = 0x811C9DC5;
    for (final unit in key.trim().toLowerCase().codeUnits) {
      h = ((h ^ unit) * 0x01000193) & 0xFFFFFFFF;
    }
    return h;
  }

  static int slotOf(String key) => hashOf(key) % T.chartColors.length;

  static Color forKey(String key) => T.chartColors[slotOf(key)];

  /// Цвета для набора ключей одной диаграммы: у каждого свой «любимый» цвет по хэшу; если двое попали в один, второй
  /// (по порядку хэша, не показа) берёт следующий свободный. Результат не зависит от порядка строк. Ключей больше
  /// цветов — палитра идёт по кругу.
  static List<Color> assign(List<String> keys) {
    final n = T.chartColors.length;
    final order = List<int>.generate(keys.length, (i) => i)
      ..sort((a, b) {
        final c = hashOf(keys[a]).compareTo(hashOf(keys[b]));
        return c != 0 ? c : keys[a].compareTo(keys[b]);
      });
    final slotByKey = <String, int>{};
    var used = <int>{};
    final result = List<Color>.filled(keys.length, T.chartColors[0]);
    for (final i in order) {
      final norm = keys[i].trim().toLowerCase();
      var slot = slotByKey[norm] ?? -1;
      if (slot < 0) {
        if (used.length >= n) used = <int>{};
        slot = slotOf(keys[i]);
        while (used.contains(slot)) {
          slot = (slot + 1) % n;
        }
        used.add(slot);
        slotByKey[norm] = slot;
      }
      result[i] = T.chartColors[slot];
    }
    return result;
  }

  /// Чёрный или белый — что контрастнее на этом цвете (порог 0,179: у любого цвета палитры не ниже 4,5:1).
  static Color onColor(Color c) => c.computeLuminance() > 0.179 ? Colors.black : Colors.white;

  /// Контраст двух цветов по WCAG (1…21).
  static double contrast(Color a, Color b) {
    final la = a.computeLuminance();
    final lb = b.computeLuminance();
    final hi = la > lb ? la : lb;
    final lo = la > lb ? lb : la;
    return (hi + 0.05) / (lo + 0.05);
  }
}
