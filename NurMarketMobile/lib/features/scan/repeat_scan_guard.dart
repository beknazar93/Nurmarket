/// 2026-11-07, владелец: «в мобилке сканер: если товар смотрит долго, то подряд добавляет. Сделай так, чтобы при повторном скане
/// останавливать сканер и спрашивать количество». Чистая логика без камеры и экрана (проверяется тестами): по каждому кадру с кодом
/// говорит, что это — новый скан, тот же скан (кадры одной и той же пачки) или ПОВТОР (тот же код считан снова — пора остановить
/// камеру и спросить «Сколько штук?»).
enum ScanVerdict {
  /// Новый скан: добавить как обычно (дальше работают прежние проверки экрана).
  fresh,

  /// Тот же скан: камера ещё видит только что засчитанный код (или код заблокирован после окна количества) — ничего не делать.
  same,

  /// Повтор: тот же код считан снова сразу после добавления, камеру не отводили — остановить сканер и спросить количество.
  repeat,
}

class RepeatScanGuard {
  /// Сколько камера не видит код, чтобы он снова считался новым («камеру отвели»).
  final Duration gone;

  /// Короткое пропадание кода (автофокус, дрожание руки) внутри [settle] после добавления — ещё тот же скан, а не повтор.
  final Duration flicker;

  /// Сколько после добавления кадры того же кода — просто та же пачка в кадре (кассир ещё не успел убрать товар).
  final Duration settle;

  final DateTime Function() _clock;

  String? _code;
  DateTime _seenAt = DateTime(2000);
  DateTime _countedAt = DateTime(2000);
  bool _locked = false;

  RepeatScanGuard({
    this.gone = const Duration(milliseconds: 1500),
    this.flicker = const Duration(milliseconds: 700),
    this.settle = const Duration(milliseconds: 1500),
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  /// Код, по которому идёт отсчёт (последний засчитанный), или null.
  String? get code => _code;

  /// Заблокирован ли код после окна количества (до тех пор, пока камеру не отведут на [gone]).
  bool get locked => _locked;

  /// Вызывать на КАЖДЫЙ кадр с кодом — время «последний раз видели» обновляется всегда.
  ScanVerdict check(String code) {
    final now = _clock();
    final mine = _code;
    if (mine == null) return ScanVerdict.fresh;
    if (code != mine) {
      // камера увидела другой код — отсчёт по прежнему сброшен
      _reset();
      return ScanVerdict.fresh;
    }
    final gap = now.difference(_seenAt);
    _seenAt = now;
    if (gap >= gone) {
      // камеру отводили (или окно количества стояло долго и товар убрали) — код снова новый
      _reset();
      return ScanVerdict.fresh;
    }
    if (_locked) return ScanVerdict.same;
    if (now.difference(_countedAt) < settle && gap < flicker) return ScanVerdict.same;
    return ScanVerdict.repeat;
  }

  /// Код засчитан (добавлен в чек / приёмку / на кассу) — с этого момента его повтор считается повтором.
  void counted(String code) {
    final now = _clock();
    _code = code;
    _seenAt = now;
    _countedAt = now;
    _locked = false;
  }

  /// Окно «Сколько штук?» закрыто (любой исход). Камера включается снова, но этот код новым станет, только когда камеру отведут
  /// на [gone] — иначе товар, всё ещё лежащий перед камерой, снова вызвал бы окно или добавился.
  void dialogClosed(String code) {
    _code = code;
    _seenAt = _clock();
    _locked = true;
  }

  /// Сбросить всё (смена режима, возврат на экран, кассир нажал кнопку): следующий код — новый.
  void reset() => _reset();

  void _reset() {
    _code = null;
    _locked = false;
  }
}
