import 'dart:typed_data';

/// 2026-10-22, владелец: «умный сканер при нажатии сам сразу сканирует — добавь кнопку» и «при обучении фоткает только
/// 1 фото и потом чёрный экран». Состояния видоискателя умной камеры отдельно от камеры (их проверяют тесты):
/// «Наведите и нажмите» → «Сканирую…» → результат; снимки обучения копятся на одной странице с одной камерой.
enum ShotPhase { aim, scanning, result }

class ShotFlow {
  /// Сколько снимков можно сделать (0 — без ограничения: продажа «По виду», скан за сканом).
  final int maxShots;

  /// Сколько снимков нужно, чтобы стала доступна «Готово».
  final int minShots;

  /// Хранить снимки (обучение) или только передавать дальше (продажа).
  final bool keep;

  final List<Uint8List> shots = [];
  ShotPhase phase = ShotPhase.aim;

  ShotFlow({this.maxShots = 1, this.minShots = 1, this.keep = true});

  bool get busy => phase == ShotPhase.scanning;
  bool get full => maxShots > 0 && shots.length >= maxShots;
  bool get canShoot => !busy && !full;
  bool get canFinish => !busy && shots.length >= minShots;

  /// Номер снимка, который будет сделан сейчас: «Фото 2 из 5».
  int get current => maxShots > 0 ? (shots.length + 1).clamp(1, maxShots) : shots.length + 1;

  /// Нажали «Сканировать»: false — уже идёт снимок или снимков хватает (двойное нажатие не делает два кадра).
  bool begin() {
    if (!canShoot) return false;
    phase = ShotPhase.scanning;
    return true;
  }

  /// Кадр получен. [waitResult] — дальше распознавание (продажа): «Сканирую…» остаётся до [showResult].
  void taken(Uint8List shot, {bool waitResult = false}) {
    if (keep) shots.add(shot);
    phase = waitResult ? ShotPhase.scanning : ShotPhase.aim;
  }

  /// Распознавание закончилось: [hasResult] — показан список, иначе снова «Наведите и нажмите».
  void showResult({bool hasResult = true}) => phase = hasResult ? ShotPhase.result : ShotPhase.aim;

  /// Снимок не получился (ошибка камеры или ИИ) — можно снять снова.
  void failed() => phase = ShotPhase.aim;

  /// «Переснять»: убрать последний снимок.
  void removeLast() {
    if (busy || shots.isEmpty) return;
    shots.removeLast();
    phase = ShotPhase.aim;
  }
}

/// 2026-10-22 (аудит С-1): новый ли это скан. Код → когда его последний раз видели в кадре и когда засчитали (раньше
/// помнился один последний код: два штрихкода в кадре — A, B, A, B… — добавлялись оба по кругу, а в приёмке одинаковые
/// товары подряд чаще раза в 1,5 с терялись). Код, ушедший из кадра (не виден [gone]), — новый; в продаже тот же код,
/// не уходивший из кадра, — снова через [again] (две одинаковые пачки подряд); в приёмке ([receiving]) — только
/// «ушёл и вернулся».
class ScanRepeat {
  bool receiving;
  final Duration gone;
  final Duration again;
  final _seenAt = <String, DateTime>{};
  final _countedAt = <String, DateTime>{};

  ScanRepeat({this.receiving = false, this.gone = const Duration(milliseconds: 500), this.again = const Duration(milliseconds: 1500)});

  bool isNew(String code, DateTime now) {
    final seen = _seenAt[code];
    _seenAt[code] = now;
    if (_seenAt.length > 50) _seenAt.removeWhere((_, t) => now.difference(t) > const Duration(seconds: 10));
    if (seen == null || now.difference(seen) >= gone) return true;
    if (receiving) return false;
    final counted = _countedAt[code];
    return counted == null || now.difference(counted) >= again;
  }

  void counted(String code, DateTime now) => _countedAt[code] = now;

  /// Код был в кадре (разбор шёл долго) — «ушёл из кадра» считать от этого момента.
  void seen(String code, DateTime now) => _seenAt[code] = now;
}
