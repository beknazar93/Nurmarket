import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_en.dart';
import 'app_localizations_ky.dart';
import 'app_localizations_ru.dart';
import 'app_localizations_tr.dart';
import 'app_localizations_uz.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
    : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations? of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations);
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
        delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
      ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('en'),
    Locale('ky'),
    Locale('ru'),
    Locale('tr'),
    Locale('uz'),
  ];

  /// No description provided for @som.
  ///
  /// In ru, this message translates to:
  /// **'сом'**
  String get som;

  /// No description provided for @cancel.
  ///
  /// In ru, this message translates to:
  /// **'Отмена'**
  String get cancel;

  /// No description provided for @done.
  ///
  /// In ru, this message translates to:
  /// **'Готово'**
  String get done;

  /// No description provided for @save.
  ///
  /// In ru, this message translates to:
  /// **'Сохранить'**
  String get save;

  /// No description provided for @retry.
  ///
  /// In ru, this message translates to:
  /// **'Повторить'**
  String get retry;

  /// No description provided for @all.
  ///
  /// In ru, this message translates to:
  /// **'Все'**
  String get all;

  /// No description provided for @loginTagline.
  ///
  /// In ru, this message translates to:
  /// **'Тот же вход, что на кассе и в вебе'**
  String get loginTagline;

  /// No description provided for @loginLabel.
  ///
  /// In ru, this message translates to:
  /// **'Логин или телефон'**
  String get loginLabel;

  /// No description provided for @loginHint.
  ///
  /// In ru, this message translates to:
  /// **'почта или телефон'**
  String get loginHint;

  /// No description provided for @passwordLabel.
  ///
  /// In ru, this message translates to:
  /// **'Пароль'**
  String get passwordLabel;

  /// No description provided for @signIn.
  ///
  /// In ru, this message translates to:
  /// **'Войти'**
  String get signIn;

  /// No description provided for @loginEmpty.
  ///
  /// In ru, this message translates to:
  /// **'Введите логин и пароль.'**
  String get loginEmpty;

  /// No description provided for @loginWrong.
  ///
  /// In ru, this message translates to:
  /// **'Неверный логин или пароль.'**
  String get loginWrong;

  /// No description provided for @chooseCashbox.
  ///
  /// In ru, this message translates to:
  /// **'Выберите кассу'**
  String get chooseCashbox;

  /// No description provided for @cashbox.
  ///
  /// In ru, this message translates to:
  /// **'Касса'**
  String get cashbox;

  /// No description provided for @tabHome.
  ///
  /// In ru, this message translates to:
  /// **'Главная'**
  String get tabHome;

  /// No description provided for @tabSale.
  ///
  /// In ru, this message translates to:
  /// **'Продажа'**
  String get tabSale;

  /// No description provided for @tabScan.
  ///
  /// In ru, this message translates to:
  /// **'Сканер'**
  String get tabScan;

  /// No description provided for @tabChecks.
  ///
  /// In ru, this message translates to:
  /// **'Чеки'**
  String get tabChecks;

  /// No description provided for @tabStock.
  ///
  /// In ru, this message translates to:
  /// **'Склад'**
  String get tabStock;

  /// No description provided for @newCheck.
  ///
  /// In ru, this message translates to:
  /// **'Новый чек'**
  String get newCheck;

  /// No description provided for @scanTileSub.
  ///
  /// In ru, this message translates to:
  /// **'Цена и остаток по штрихкоду'**
  String get scanTileSub;

  /// No description provided for @checksTileSub.
  ///
  /// In ru, this message translates to:
  /// **'Отправить, напечатать'**
  String get checksTileSub;

  /// No description provided for @stockTileSub.
  ///
  /// In ru, this message translates to:
  /// **'Остатки и приёмка'**
  String get stockTileSub;

  /// No description provided for @shiftOpenSince.
  ///
  /// In ru, this message translates to:
  /// **'Смена открыта · {time}'**
  String shiftOpenSince(String time);

  /// No description provided for @shiftClosed.
  ///
  /// In ru, this message translates to:
  /// **'Смена закрыта'**
  String get shiftClosed;

  /// No description provided for @mySalesForShift.
  ///
  /// In ru, this message translates to:
  /// **'Мои продажи за смену'**
  String get mySalesForShift;

  /// No description provided for @checksCount.
  ///
  /// In ru, this message translates to:
  /// **'{n} чеков'**
  String checksCount(int n);

  /// No description provided for @openShift.
  ///
  /// In ru, this message translates to:
  /// **'Открыть смену'**
  String get openShift;

  /// No description provided for @closeShift.
  ///
  /// In ru, this message translates to:
  /// **'Закрыть смену'**
  String get closeShift;

  /// No description provided for @openingCashHint.
  ///
  /// In ru, this message translates to:
  /// **'Сколько денег в кассе на начало'**
  String get openingCashHint;

  /// No description provided for @openShiftFirst.
  ///
  /// In ru, this message translates to:
  /// **'Сначала откройте смену на главной.'**
  String get openShiftFirst;

  /// No description provided for @offlineWorking.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи с сервером — продажи сохраняются на телефоне и уйдут сами.'**
  String get offlineWorking;

  /// No description provided for @runningOut.
  ///
  /// In ru, this message translates to:
  /// **'Заканчиваются {n} товаров'**
  String runningOut(int n);

  /// No description provided for @runningOutShort.
  ///
  /// In ru, this message translates to:
  /// **'Заканчиваются'**
  String get runningOutShort;

  /// No description provided for @outOfStock.
  ///
  /// In ru, this message translates to:
  /// **'Нет в наличии'**
  String get outOfStock;

  /// No description provided for @tellAdmin.
  ///
  /// In ru, this message translates to:
  /// **'Сообщите администратору'**
  String get tellAdmin;

  /// No description provided for @searchProduct.
  ///
  /// In ru, this message translates to:
  /// **'Товар или штрихкод'**
  String get searchProduct;

  /// No description provided for @favorites.
  ///
  /// In ru, this message translates to:
  /// **'Избранное'**
  String get favorites;

  /// No description provided for @nothingFound.
  ///
  /// In ru, this message translates to:
  /// **'Ничего не найдено'**
  String get nothingFound;

  /// No description provided for @inCheck.
  ///
  /// In ru, this message translates to:
  /// **'В чеке'**
  String get inCheck;

  /// No description provided for @positions.
  ///
  /// In ru, this message translates to:
  /// **'{n} позиций'**
  String positions(int n);

  /// No description provided for @swipeToRemove.
  ///
  /// In ru, this message translates to:
  /// **'Смахните строку влево, чтобы убрать'**
  String get swipeToRemove;

  /// No description provided for @payAmount.
  ///
  /// In ru, this message translates to:
  /// **'Оплатить {sum} сом'**
  String payAmount(String sum);

  /// No description provided for @orderDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Скидка на чек'**
  String get orderDiscount;

  /// No description provided for @discountPercentHint.
  ///
  /// In ru, this message translates to:
  /// **'Процент, например 10'**
  String get discountPercentHint;

  /// No description provided for @extraService.
  ///
  /// In ru, this message translates to:
  /// **'Доп. услуга'**
  String get extraService;

  /// No description provided for @clearCheck.
  ///
  /// In ru, this message translates to:
  /// **'Очистить чек'**
  String get clearCheck;

  /// No description provided for @price.
  ///
  /// In ru, this message translates to:
  /// **'Цена'**
  String get price;

  /// No description provided for @weightOf.
  ///
  /// In ru, this message translates to:
  /// **'Вес: {name}'**
  String weightOf(String name);

  /// No description provided for @kg.
  ///
  /// In ru, this message translates to:
  /// **'килограммы, например 0,5'**
  String get kg;

  /// No description provided for @lineDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Скидка на товар'**
  String get lineDiscount;

  /// No description provided for @sumHint.
  ///
  /// In ru, this message translates to:
  /// **'Сумма в сомах'**
  String get sumHint;

  /// No description provided for @discount.
  ///
  /// In ru, this message translates to:
  /// **'Скидка'**
  String get discount;

  /// No description provided for @barcodeNotFound.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} не найден в каталоге.'**
  String barcodeNotFound(String code);

  /// No description provided for @cameraUnavailable.
  ///
  /// In ru, this message translates to:
  /// **'Камера недоступна. Разрешите доступ к камере в настройках телефона или введите штрихкод вручную.'**
  String get cameraUnavailable;

  /// No description provided for @scanHint.
  ///
  /// In ru, this message translates to:
  /// **'Наведите на штрихкод — товар добавится сам'**
  String get scanHint;

  /// No description provided for @scanReceiveHint.
  ///
  /// In ru, this message translates to:
  /// **'Отсканировано: {n}'**
  String scanReceiveHint(int n);

  /// No description provided for @toCheck.
  ///
  /// In ru, this message translates to:
  /// **'В чек'**
  String get toCheck;

  /// No description provided for @checkPrice.
  ///
  /// In ru, this message translates to:
  /// **'Узнать цену'**
  String get checkPrice;

  /// No description provided for @receiving.
  ///
  /// In ru, this message translates to:
  /// **'Приёмка'**
  String get receiving;

  /// No description provided for @doneCount.
  ///
  /// In ru, this message translates to:
  /// **'Готово · {n}'**
  String doneCount(int n);

  /// No description provided for @inStock.
  ///
  /// In ru, this message translates to:
  /// **'на складе {qty} {unit}'**
  String inStock(String qty, String unit);

  /// No description provided for @scanContinuous.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать подряд, без подтверждения'**
  String get scanContinuous;

  /// No description provided for @checkN.
  ///
  /// In ru, this message translates to:
  /// **'Чек · {n}'**
  String checkN(int n);

  /// No description provided for @addToCheck.
  ///
  /// In ru, this message translates to:
  /// **'Добавить в чек'**
  String get addToCheck;

  /// No description provided for @addedAutomatically.
  ///
  /// In ru, this message translates to:
  /// **'Добавлено в чек'**
  String get addedAutomatically;

  /// No description provided for @profileTitle.
  ///
  /// In ru, this message translates to:
  /// **'Профиль'**
  String get profileTitle;

  /// No description provided for @discountInPercent.
  ///
  /// In ru, this message translates to:
  /// **'Процент'**
  String get discountInPercent;

  /// No description provided for @discountInSom.
  ///
  /// In ru, this message translates to:
  /// **'Сумма, сом'**
  String get discountInSom;

  /// No description provided for @discountResult.
  ///
  /// In ru, this message translates to:
  /// **'Итого со скидкой: {sum} сом'**
  String discountResult(String sum);

  /// No description provided for @clothingMode.
  ///
  /// In ru, this message translates to:
  /// **'Размеры и цвета при продаже'**
  String get clothingMode;

  /// No description provided for @clothingModeSub.
  ///
  /// In ru, this message translates to:
  /// **'Для магазина одежды и обуви'**
  String get clothingModeSub;

  /// No description provided for @chooseSize.
  ///
  /// In ru, this message translates to:
  /// **'Размер и цвет'**
  String get chooseSize;

  /// No description provided for @colorLabel.
  ///
  /// In ru, this message translates to:
  /// **'Цвет'**
  String get colorLabel;

  /// No description provided for @sizeLabel.
  ///
  /// In ru, this message translates to:
  /// **'Размер'**
  String get sizeLabel;

  /// No description provided for @toCheckSize.
  ///
  /// In ru, this message translates to:
  /// **'В чек · {size}'**
  String toCheckSize(String size);

  /// No description provided for @sizeLowStock.
  ///
  /// In ru, this message translates to:
  /// **'Остаток этого размера — {qty} шт. Всё равно добавить?'**
  String sizeLowStock(String qty);

  /// No description provided for @rentals.
  ///
  /// In ru, this message translates to:
  /// **'Прокат'**
  String get rentals;

  /// No description provided for @rentalNew.
  ///
  /// In ru, this message translates to:
  /// **'Новый прокат'**
  String get rentalNew;

  /// No description provided for @rentalActive.
  ///
  /// In ru, this message translates to:
  /// **'На руках'**
  String get rentalActive;

  /// No description provided for @rentalOverdue.
  ///
  /// In ru, this message translates to:
  /// **'Просрочен'**
  String get rentalOverdue;

  /// No description provided for @rentalReturn.
  ///
  /// In ru, this message translates to:
  /// **'Принять возврат'**
  String get rentalReturn;

  /// No description provided for @rentalReturned.
  ///
  /// In ru, this message translates to:
  /// **'Возврат принят.'**
  String get rentalReturned;

  /// No description provided for @rentalCondOk.
  ///
  /// In ru, this message translates to:
  /// **'Целое'**
  String get rentalCondOk;

  /// No description provided for @rentalCondDamaged.
  ///
  /// In ru, this message translates to:
  /// **'Повреждено'**
  String get rentalCondDamaged;

  /// No description provided for @rentalPenalty.
  ///
  /// In ru, this message translates to:
  /// **'Штраф из залога'**
  String get rentalPenalty;

  /// No description provided for @rentalClient.
  ///
  /// In ru, this message translates to:
  /// **'Клиент'**
  String get rentalClient;

  /// No description provided for @rentalItem.
  ///
  /// In ru, this message translates to:
  /// **'Вещь'**
  String get rentalItem;

  /// No description provided for @rentalUntil.
  ///
  /// In ru, this message translates to:
  /// **'До даты'**
  String get rentalUntil;

  /// No description provided for @rentalPerDay.
  ///
  /// In ru, this message translates to:
  /// **'Цена за сутки'**
  String get rentalPerDay;

  /// No description provided for @rentalDays.
  ///
  /// In ru, this message translates to:
  /// **'{n} сут.'**
  String rentalDays(int n);

  /// No description provided for @rentalDeposit.
  ///
  /// In ru, this message translates to:
  /// **'Залог деньгами'**
  String get rentalDeposit;

  /// No description provided for @rentalDocument.
  ///
  /// In ru, this message translates to:
  /// **'Залог документом'**
  String get rentalDocument;

  /// No description provided for @rentalDocumentHint.
  ///
  /// In ru, this message translates to:
  /// **'Паспорт ID…, ФИО'**
  String get rentalDocumentHint;

  /// No description provided for @rentalCreate.
  ///
  /// In ru, this message translates to:
  /// **'Выдать и в чек'**
  String get rentalCreate;

  /// No description provided for @rentalCreated.
  ///
  /// In ru, this message translates to:
  /// **'Прокат №{n} оформлен — стоимость в чеке.'**
  String rentalCreated(int n);

  /// No description provided for @rentalLine.
  ///
  /// In ru, this message translates to:
  /// **'Прокат №{n} · {days} сут.'**
  String rentalLine(int n, String days);

  /// No description provided for @chooseClient.
  ///
  /// In ru, this message translates to:
  /// **'Выбрать клиента'**
  String get chooseClient;

  /// No description provided for @chooseItem.
  ///
  /// In ru, this message translates to:
  /// **'Выберите вещь'**
  String get chooseItem;

  /// No description provided for @marginPercent.
  ///
  /// In ru, this message translates to:
  /// **'Наценка, %'**
  String get marginPercent;

  /// No description provided for @marginSum.
  ///
  /// In ru, this message translates to:
  /// **'Наценка, сом'**
  String get marginSum;

  /// No description provided for @kindProduct.
  ///
  /// In ru, this message translates to:
  /// **'Товар'**
  String get kindProduct;

  /// No description provided for @kindClothes.
  ///
  /// In ru, this message translates to:
  /// **'Одежда'**
  String get kindClothes;

  /// No description provided for @kindService.
  ///
  /// In ru, this message translates to:
  /// **'Услуга'**
  String get kindService;

  /// No description provided for @sizesLabel.
  ///
  /// In ru, this message translates to:
  /// **'Размеры'**
  String get sizesLabel;

  /// No description provided for @colorsLabel.
  ///
  /// In ru, this message translates to:
  /// **'Цвета'**
  String get colorsLabel;

  /// No description provided for @addColor.
  ///
  /// In ru, this message translates to:
  /// **'Добавить цвет'**
  String get addColor;

  /// No description provided for @colorName.
  ///
  /// In ru, this message translates to:
  /// **'Название цвета'**
  String get colorName;

  /// No description provided for @colorSizeGrid.
  ///
  /// In ru, this message translates to:
  /// **'Цвет × размер — сколько штук'**
  String get colorSizeGrid;

  /// No description provided for @variantsSummary.
  ///
  /// In ru, this message translates to:
  /// **'{n} вариантов · {qty} шт'**
  String variantsSummary(int n, String qty);

  /// No description provided for @variantsSaved.
  ///
  /// In ru, this message translates to:
  /// **'Сохранено вариантов: {n}'**
  String variantsSaved(int n);

  /// No description provided for @durationLabel.
  ///
  /// In ru, this message translates to:
  /// **'Длительность'**
  String get durationLabel;

  /// No description provided for @mastersLabel.
  ///
  /// In ru, this message translates to:
  /// **'Мастера · своя цена и процент'**
  String get mastersLabel;

  /// No description provided for @masterAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить мастера'**
  String get masterAdd;

  /// No description provided for @masterName.
  ///
  /// In ru, this message translates to:
  /// **'Имя мастера'**
  String get masterName;

  /// No description provided for @masterPercent.
  ///
  /// In ru, this message translates to:
  /// **'% мастеру'**
  String get masterPercent;

  /// No description provided for @consumablesLabel.
  ///
  /// In ru, this message translates to:
  /// **'Расходники со склада'**
  String get consumablesLabel;

  /// No description provided for @consumableAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить расходник'**
  String get consumableAdd;

  /// No description provided for @onlineBooking.
  ///
  /// In ru, this message translates to:
  /// **'Онлайн-запись на сайте и в боте'**
  String get onlineBooking;

  /// No description provided for @serviceLocalNote.
  ///
  /// In ru, this message translates to:
  /// **'Мастера, длительность и расходники хранятся на этом телефоне, пока у сервера нет API услуг. Сама услуга — в каталоге NurCRM.'**
  String get serviceLocalNote;

  /// No description provided for @saveService.
  ///
  /// In ru, this message translates to:
  /// **'Сохранить услугу'**
  String get saveService;

  /// No description provided for @findInternet.
  ///
  /// In ru, this message translates to:
  /// **'Найти в интернете'**
  String get findInternet;

  /// No description provided for @internetFound.
  ///
  /// In ru, this message translates to:
  /// **'Найдено в базе NurCRM'**
  String get internetFound;

  /// No description provided for @barcodeMatched.
  ///
  /// In ru, this message translates to:
  /// **'штрихкод совпал · точно'**
  String get barcodeMatched;

  /// No description provided for @takeLabel.
  ///
  /// In ru, this message translates to:
  /// **'Взять'**
  String get takeLabel;

  /// No description provided for @categoryLabel.
  ///
  /// In ru, this message translates to:
  /// **'Категория'**
  String get categoryLabel;

  /// No description provided for @brandLabel.
  ///
  /// In ru, this message translates to:
  /// **'Бренд'**
  String get brandLabel;

  /// No description provided for @fillProduct.
  ///
  /// In ru, this message translates to:
  /// **'Заполнить товар'**
  String get fillProduct;

  /// No description provided for @searchInBrowser.
  ///
  /// In ru, this message translates to:
  /// **'Искать в браузере'**
  String get searchInBrowser;

  /// No description provided for @priceNotTaken.
  ///
  /// In ru, this message translates to:
  /// **'Цена из интернета не берётся — укажите свою.'**
  String get priceNotTaken;

  /// No description provided for @bookingsTitle.
  ///
  /// In ru, this message translates to:
  /// **'Услуги · запись'**
  String get bookingsTitle;

  /// No description provided for @dayToday.
  ///
  /// In ru, this message translates to:
  /// **'Сегодня'**
  String get dayToday;

  /// No description provided for @dayTomorrow.
  ///
  /// In ru, this message translates to:
  /// **'Завтра'**
  String get dayTomorrow;

  /// No description provided for @allMasters.
  ///
  /// In ru, this message translates to:
  /// **'Все мастера'**
  String get allMasters;

  /// No description provided for @bookNew.
  ///
  /// In ru, this message translates to:
  /// **'Записать'**
  String get bookNew;

  /// No description provided for @bookCheckout.
  ///
  /// In ru, this message translates to:
  /// **'Рассчитать'**
  String get bookCheckout;

  /// No description provided for @bookArrived.
  ///
  /// In ru, this message translates to:
  /// **'пришёл'**
  String get bookArrived;

  /// No description provided for @bookBooked.
  ///
  /// In ru, this message translates to:
  /// **'записан'**
  String get bookBooked;

  /// No description provided for @bookPaid.
  ///
  /// In ru, this message translates to:
  /// **'оплачено'**
  String get bookPaid;

  /// No description provided for @bookFree.
  ///
  /// In ru, this message translates to:
  /// **'свободно'**
  String get bookFree;

  /// No description provided for @bookMarkArrived.
  ///
  /// In ru, this message translates to:
  /// **'Отметить «пришёл»'**
  String get bookMarkArrived;

  /// No description provided for @bookRemind.
  ///
  /// In ru, this message translates to:
  /// **'Напомнить в WhatsApp'**
  String get bookRemind;

  /// No description provided for @bookCancel.
  ///
  /// In ru, this message translates to:
  /// **'Отменить запись'**
  String get bookCancel;

  /// No description provided for @bookTime.
  ///
  /// In ru, this message translates to:
  /// **'Время'**
  String get bookTime;

  /// No description provided for @bookClash.
  ///
  /// In ru, this message translates to:
  /// **'У мастера в это время уже есть запись.'**
  String get bookClash;

  /// No description provided for @bookNoMasters.
  ///
  /// In ru, this message translates to:
  /// **'Мастеров пока нет — добавьте их при создании услуги («Новая позиция» → «Услуга»).'**
  String get bookNoMasters;

  /// No description provided for @bookLocalNote.
  ///
  /// In ru, this message translates to:
  /// **'Записи хранятся на этом телефоне, пока у сервера нет API записи. Оплата услуг — обычный чек NurCRM.'**
  String get bookLocalNote;

  /// No description provided for @bookReminder.
  ///
  /// In ru, this message translates to:
  /// **'Здравствуйте, {name}! Напоминаем о записи {when} к мастеру {master}.'**
  String bookReminder(String name, String when, String master);

  /// No description provided for @pricesTitle.
  ///
  /// In ru, this message translates to:
  /// **'Цены продажи'**
  String get pricesTitle;

  /// No description provided for @pricesSub.
  ///
  /// In ru, this message translates to:
  /// **'закупка изменилась: {a} · новых: {b}'**
  String pricesSub(int a, int b);

  /// No description provided for @ruleLabel.
  ///
  /// In ru, this message translates to:
  /// **'Правило'**
  String get ruleLabel;

  /// No description provided for @roundTo.
  ///
  /// In ru, this message translates to:
  /// **'Округлять до'**
  String get roundTo;

  /// No description provided for @applyToAll.
  ///
  /// In ru, this message translates to:
  /// **'Ко всем строкам, не только изменённым'**
  String get applyToAll;

  /// No description provided for @costWas.
  ///
  /// In ru, this message translates to:
  /// **'закупка {was} → {now}'**
  String costWas(String was, String now);

  /// No description provided for @newItemShort.
  ///
  /// In ru, this message translates to:
  /// **'новый'**
  String get newItemShort;

  /// No description provided for @applyPrices.
  ///
  /// In ru, this message translates to:
  /// **'Применить цены'**
  String get applyPrices;

  /// No description provided for @postSummary.
  ///
  /// In ru, this message translates to:
  /// **'Позиций: {a} · штук: {qty}\nНовых товаров: {b}\nЦен продажи изменится: {n}\nЗакупка: {sum}'**
  String postSummary(int a, String qty, int b, int n, String sum);

  /// No description provided for @quotesTitle.
  ///
  /// In ru, this message translates to:
  /// **'КП и счета'**
  String get quotesTitle;

  /// No description provided for @quoteNew.
  ///
  /// In ru, this message translates to:
  /// **'Коммерческое предложение'**
  String get quoteNew;

  /// No description provided for @invoiceNew.
  ///
  /// In ru, this message translates to:
  /// **'Счёт на оплату'**
  String get invoiceNew;

  /// No description provided for @quotesWaiting.
  ///
  /// In ru, this message translates to:
  /// **'Ждут ответа'**
  String get quotesWaiting;

  /// No description provided for @invoicesUnpaid.
  ///
  /// In ru, this message translates to:
  /// **'Не оплачены'**
  String get invoicesUnpaid;

  /// No description provided for @invoicesOverdue.
  ///
  /// In ru, this message translates to:
  /// **'Просрочено'**
  String get invoicesOverdue;

  /// No description provided for @filterAll.
  ///
  /// In ru, this message translates to:
  /// **'Все'**
  String get filterAll;

  /// No description provided for @filterQuotes.
  ///
  /// In ru, this message translates to:
  /// **'КП'**
  String get filterQuotes;

  /// No description provided for @filterInvoices.
  ///
  /// In ru, this message translates to:
  /// **'Счета'**
  String get filterInvoices;

  /// No description provided for @stDraft.
  ///
  /// In ru, this message translates to:
  /// **'Черновик'**
  String get stDraft;

  /// No description provided for @stSent.
  ///
  /// In ru, this message translates to:
  /// **'Отправлено'**
  String get stSent;

  /// No description provided for @stAccepted.
  ///
  /// In ru, this message translates to:
  /// **'Принято → счёт'**
  String get stAccepted;

  /// No description provided for @stDeclined.
  ///
  /// In ru, this message translates to:
  /// **'Отклонено'**
  String get stDeclined;

  /// No description provided for @stIssued.
  ///
  /// In ru, this message translates to:
  /// **'Ждёт оплаты'**
  String get stIssued;

  /// No description provided for @stPartial.
  ///
  /// In ru, this message translates to:
  /// **'Оплачен частично'**
  String get stPartial;

  /// No description provided for @stPaid.
  ///
  /// In ru, this message translates to:
  /// **'Оплачен'**
  String get stPaid;

  /// No description provided for @validUntil.
  ///
  /// In ru, this message translates to:
  /// **'Действует до {date}'**
  String validUntil(String date);

  /// No description provided for @payUntil.
  ///
  /// In ru, this message translates to:
  /// **'Оплатить до {date}'**
  String payUntil(String date);

  /// No description provided for @fromCart.
  ///
  /// In ru, this message translates to:
  /// **'Из текущего чека'**
  String get fromCart;

  /// No description provided for @useWholesale.
  ///
  /// In ru, this message translates to:
  /// **'Оптовые цены'**
  String get useWholesale;

  /// No description provided for @innLabel.
  ///
  /// In ru, this message translates to:
  /// **'ИНН'**
  String get innLabel;

  /// No description provided for @docSendPdf.
  ///
  /// In ru, this message translates to:
  /// **'Отправить PDF'**
  String get docSendPdf;

  /// No description provided for @invoiceFromQuote.
  ///
  /// In ru, this message translates to:
  /// **'Выставить счёт по КП'**
  String get invoiceFromQuote;

  /// No description provided for @takePayment.
  ///
  /// In ru, this message translates to:
  /// **'Принять оплату'**
  String get takePayment;

  /// No description provided for @sellFromDoc.
  ///
  /// In ru, this message translates to:
  /// **'Провести продажу'**
  String get sellFromDoc;

  /// No description provided for @markDeclined.
  ///
  /// In ru, this message translates to:
  /// **'Клиент отказался'**
  String get markDeclined;

  /// No description provided for @editDoc.
  ///
  /// In ru, this message translates to:
  /// **'Изменить'**
  String get editDoc;

  /// No description provided for @deleteDoc.
  ///
  /// In ru, this message translates to:
  /// **'Удалить документ'**
  String get deleteDoc;

  /// No description provided for @paidOf.
  ///
  /// In ru, this message translates to:
  /// **'оплачено {paid} из {total}'**
  String paidOf(String paid, String total);

  /// No description provided for @basedOnQuote.
  ///
  /// In ru, this message translates to:
  /// **'по {number}'**
  String basedOnQuote(String number);

  /// No description provided for @quotesLocalNote.
  ///
  /// In ru, this message translates to:
  /// **'КП и счета общие с программой владельца на ПК и другими телефонами компании. Продажа по счёту — обычный чек NurCRM.'**
  String get quotesLocalNote;

  /// No description provided for @addLine.
  ///
  /// In ru, this message translates to:
  /// **'Добавить товар'**
  String get addLine;

  /// No description provided for @docDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Скидка, сом'**
  String get docDiscount;

  /// No description provided for @esfTitle.
  ///
  /// In ru, this message translates to:
  /// **'ККМ и ЭСФ'**
  String get esfTitle;

  /// No description provided for @esfQueue.
  ///
  /// In ru, this message translates to:
  /// **'Чеков в очереди: {n} — уйдут, когда появится связь'**
  String esfQueue(int n);

  /// No description provided for @esfOnline.
  ///
  /// In ru, this message translates to:
  /// **'Касса на связи, очередь пуста'**
  String get esfOnline;

  /// No description provided for @esfIssue.
  ///
  /// In ru, this message translates to:
  /// **'Выписать ЭСФ'**
  String get esfIssue;

  /// No description provided for @esfBuyerInn.
  ///
  /// In ru, this message translates to:
  /// **'ИНН покупателя'**
  String get esfBuyerInn;

  /// No description provided for @esfBuyerName.
  ///
  /// In ru, this message translates to:
  /// **'Покупатель'**
  String get esfBuyerName;

  /// No description provided for @esfPayForm.
  ///
  /// In ru, this message translates to:
  /// **'Форма оплаты'**
  String get esfPayForm;

  /// No description provided for @esfCash.
  ///
  /// In ru, this message translates to:
  /// **'Наличная'**
  String get esfCash;

  /// No description provided for @esfNonCash.
  ///
  /// In ru, this message translates to:
  /// **'Безналичная'**
  String get esfNonCash;

  /// No description provided for @esfContract.
  ///
  /// In ru, this message translates to:
  /// **'Договор № и дата'**
  String get esfContract;

  /// No description provided for @esfSum.
  ///
  /// In ru, this message translates to:
  /// **'Сумма, сом'**
  String get esfSum;

  /// No description provided for @esfDraftSaved.
  ///
  /// In ru, this message translates to:
  /// **'Черновик ЭСФ сохранён'**
  String get esfDraftSaved;

  /// No description provided for @esfSendTax.
  ///
  /// In ru, this message translates to:
  /// **'Отправить в налоговую'**
  String get esfSendTax;

  /// No description provided for @esfDrafts.
  ///
  /// In ru, this message translates to:
  /// **'Черновики ЭСФ'**
  String get esfDrafts;

  /// No description provided for @esfIncoming.
  ///
  /// In ru, this message translates to:
  /// **'Входящие ЭСФ от поставщиков'**
  String get esfIncoming;

  /// No description provided for @esfStub.
  ///
  /// In ru, this message translates to:
  /// **'Отправка в налоговую (ЭСФ ГНС КР) и входящие ЭСФ заработают, когда сервер NurCRM подключит налоговую. Сейчас можно готовить черновики — данные покупателя и суммы сохранятся.'**
  String get esfStub;

  /// No description provided for @smartCam.
  ///
  /// In ru, this message translates to:
  /// **'Умная камера'**
  String get smartCam;

  /// No description provided for @byLook.
  ///
  /// In ru, this message translates to:
  /// **'По виду'**
  String get byLook;

  /// No description provided for @byLookHint.
  ///
  /// In ru, this message translates to:
  /// **'Снимите товар без штрихкода — ИИ узнает его и найдёт в каталоге'**
  String get byLookHint;

  /// No description provided for @recognizing.
  ///
  /// In ru, this message translates to:
  /// **'Узнаю товар…'**
  String get recognizing;

  /// No description provided for @recognizedNone.
  ///
  /// In ru, this message translates to:
  /// **'Не узнал товар — снимите ближе и при свете'**
  String get recognizedNone;

  /// No description provided for @seenOnPhoto.
  ///
  /// In ru, this message translates to:
  /// **'На снимке'**
  String get seenOnPhoto;

  /// No description provided for @notInCatalog.
  ///
  /// In ru, this message translates to:
  /// **'нет в каталоге'**
  String get notInCatalog;

  /// No description provided for @aiKeyNeeded.
  ///
  /// In ru, this message translates to:
  /// **'Для умной камеры нужен ключ ИИ (Gemini) — тот же, что у Нур Советника на кассе. Его можно вставить здесь.'**
  String get aiKeyNeeded;

  /// No description provided for @aiKeyLabel.
  ///
  /// In ru, this message translates to:
  /// **'Ключ Gemini'**
  String get aiKeyLabel;

  /// No description provided for @smartShelf.
  ///
  /// In ru, this message translates to:
  /// **'Полка'**
  String get smartShelf;

  /// No description provided for @smartInvoice.
  ///
  /// In ru, this message translates to:
  /// **'Накладная'**
  String get smartInvoice;

  /// No description provided for @smartLabels.
  ///
  /// In ru, this message translates to:
  /// **'Этикетки'**
  String get smartLabels;

  /// No description provided for @smartHint.
  ///
  /// In ru, this message translates to:
  /// **'Держите телефон ровно — камера ищет товары и ценники'**
  String get smartHint;

  /// No description provided for @takePhoto.
  ///
  /// In ru, this message translates to:
  /// **'Сделать снимок'**
  String get takePhoto;

  /// No description provided for @morePhoto.
  ///
  /// In ru, this message translates to:
  /// **'Ещё снимок'**
  String get morePhoto;

  /// No description provided for @seenInBase.
  ///
  /// In ru, this message translates to:
  /// **'Есть в базе · {n}'**
  String seenInBase(int n);

  /// No description provided for @seenNew.
  ///
  /// In ru, this message translates to:
  /// **'Новых · {n}'**
  String seenNew(int n);

  /// No description provided for @smartNext.
  ///
  /// In ru, this message translates to:
  /// **'Далее — в приёмку · {n}'**
  String smartNext(int n);

  /// No description provided for @bulkImport.
  ///
  /// In ru, this message translates to:
  /// **'Массовая загрузка'**
  String get bulkImport;

  /// No description provided for @bulkPickFile.
  ///
  /// In ru, this message translates to:
  /// **'Выбрать файл Excel или CSV'**
  String get bulkPickFile;

  /// No description provided for @bulkPaste.
  ///
  /// In ru, this message translates to:
  /// **'Вставить скопированное из Excel'**
  String get bulkPaste;

  /// No description provided for @bulkTemplate.
  ///
  /// In ru, this message translates to:
  /// **'Шаблон файла'**
  String get bulkTemplate;

  /// No description provided for @bulkHint.
  ///
  /// In ru, this message translates to:
  /// **'Столбцы: Название · Штрихкод · Цена продажи · Закупка · Количество · Единица · Категория. Первая строка — заголовки (можно в любом порядке). Товар с тем же штрихкодом не дублируется: прибавится количество и обновятся цены.'**
  String get bulkHint;

  /// No description provided for @bulkSummary.
  ///
  /// In ru, this message translates to:
  /// **'Строк: {n} · новых {a} · уже есть {b}'**
  String bulkSummary(int n, int a, int b);

  /// No description provided for @bulkBad.
  ///
  /// In ru, this message translates to:
  /// **'пропущено: нет названия или цены'**
  String get bulkBad;

  /// No description provided for @bulkExists.
  ///
  /// In ru, this message translates to:
  /// **'уже есть — прибавится'**
  String get bulkExists;

  /// No description provided for @bulkUpload.
  ///
  /// In ru, this message translates to:
  /// **'Загрузить {n}'**
  String bulkUpload(int n);

  /// No description provided for @bulkDone.
  ///
  /// In ru, this message translates to:
  /// **'Загружено: {n}'**
  String bulkDone(int n);

  /// No description provided for @bulkNothing.
  ///
  /// In ru, this message translates to:
  /// **'В файле не нашлось строк с товарами'**
  String get bulkNothing;

  /// No description provided for @notFoundTitle.
  ///
  /// In ru, this message translates to:
  /// **'Товара нет в каталоге'**
  String get notFoundTitle;

  /// No description provided for @notFoundAsk.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code}. Добавить новый товар с этим штрихкодом?'**
  String notFoundAsk(String code);

  /// No description provided for @addNewProduct.
  ///
  /// In ru, this message translates to:
  /// **'Добавить товар'**
  String get addNewProduct;

  /// No description provided for @supplierLabel.
  ///
  /// In ru, this message translates to:
  /// **'Поставщик'**
  String get supplierLabel;

  /// No description provided for @supplierDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Скидка поставщика, %'**
  String get supplierDiscount;

  /// No description provided for @costWithDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Закупка со скидкой: {sum}'**
  String costWithDiscount(String sum);

  /// No description provided for @supplierReceiptNote.
  ///
  /// In ru, this message translates to:
  /// **'Количество придёт приходом от поставщика — в NurCRM видно, у кого и почём купили.'**
  String get supplierReceiptNote;

  /// No description provided for @retailPrice.
  ///
  /// In ru, this message translates to:
  /// **'Розничная цена'**
  String get retailPrice;

  /// No description provided for @altBarcodesLabel.
  ///
  /// In ru, this message translates to:
  /// **'Доп. штрихкоды'**
  String get altBarcodesLabel;

  /// No description provided for @altBarcodesHint.
  ///
  /// In ru, this message translates to:
  /// **'Другой штрихкод того же товара или упаковки: у коробки укажите, сколько в ней штук — скан коробки добавит их все.'**
  String get altBarcodesHint;

  /// No description provided for @altBarcodeAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить штрихкод'**
  String get altBarcodeAdd;

  /// No description provided for @packQty.
  ///
  /// In ru, this message translates to:
  /// **'Штук'**
  String get packQty;

  /// No description provided for @packName.
  ///
  /// In ru, this message translates to:
  /// **'Упаковка'**
  String get packName;

  /// No description provided for @photosLabel.
  ///
  /// In ru, this message translates to:
  /// **'Фото товара'**
  String get photosLabel;

  /// No description provided for @photoCamera.
  ///
  /// In ru, this message translates to:
  /// **'Камера'**
  String get photoCamera;

  /// No description provided for @photoGallery.
  ///
  /// In ru, this message translates to:
  /// **'Галерея'**
  String get photoGallery;

  /// No description provided for @photoInternet.
  ///
  /// In ru, this message translates to:
  /// **'Из интернета'**
  String get photoInternet;

  /// No description provided for @photosSearching.
  ///
  /// In ru, this message translates to:
  /// **'Ищу фото…'**
  String get photosSearching;

  /// No description provided for @photoFirstMain.
  ///
  /// In ru, this message translates to:
  /// **'Первое фото — главное'**
  String get photoFirstMain;

  /// No description provided for @descriptionLabel.
  ///
  /// In ru, this message translates to:
  /// **'Описание — для сайта и бота'**
  String get descriptionLabel;

  /// No description provided for @descriptionFind.
  ///
  /// In ru, this message translates to:
  /// **'Найти описание в интернете'**
  String get descriptionFind;

  /// No description provided for @descriptionWriting.
  ///
  /// In ru, this message translates to:
  /// **'Пишу описание…'**
  String get descriptionWriting;

  /// No description provided for @lowStockPull.
  ///
  /// In ru, this message translates to:
  /// **'↑ Потяните вверх — что заканчивается'**
  String get lowStockPull;

  /// No description provided for @lowStockTitle.
  ///
  /// In ru, this message translates to:
  /// **'Заканчивается · {n}'**
  String lowStockTitle(int n);

  /// No description provided for @lowStockEmpty.
  ///
  /// In ru, this message translates to:
  /// **'Всего хватает — остатки в норме'**
  String get lowStockEmpty;

  /// No description provided for @summaryTitle.
  ///
  /// In ru, this message translates to:
  /// **'Сводка'**
  String get summaryTitle;

  /// No description provided for @periodToday.
  ///
  /// In ru, this message translates to:
  /// **'Сегодня'**
  String get periodToday;

  /// No description provided for @periodWeek.
  ///
  /// In ru, this message translates to:
  /// **'Неделя'**
  String get periodWeek;

  /// No description provided for @periodMonth.
  ///
  /// In ru, this message translates to:
  /// **'Месяц'**
  String get periodMonth;

  /// No description provided for @kpiRevenue.
  ///
  /// In ru, this message translates to:
  /// **'Выручка'**
  String get kpiRevenue;

  /// No description provided for @kpiChecks.
  ///
  /// In ru, this message translates to:
  /// **'Чеков'**
  String get kpiChecks;

  /// No description provided for @kpiAvg.
  ///
  /// In ru, this message translates to:
  /// **'Средний чек'**
  String get kpiAvg;

  /// No description provided for @kpiProfit.
  ///
  /// In ru, this message translates to:
  /// **'Прибыль'**
  String get kpiProfit;

  /// No description provided for @revenueByDay.
  ///
  /// In ru, this message translates to:
  /// **'Выручка по дням'**
  String get revenueByDay;

  /// No description provided for @paymentMethods.
  ///
  /// In ru, this message translates to:
  /// **'Способы оплаты'**
  String get paymentMethods;

  /// No description provided for @topProducts.
  ///
  /// In ru, this message translates to:
  /// **'Лучшие товары'**
  String get topProducts;

  /// No description provided for @noSalesPeriod.
  ///
  /// In ru, this message translates to:
  /// **'За этот период продаж нет'**
  String get noSalesPeriod;

  /// No description provided for @abcTitle.
  ///
  /// In ru, this message translates to:
  /// **'ABC-анализ'**
  String get abcTitle;

  /// No description provided for @abcByRevenue.
  ///
  /// In ru, this message translates to:
  /// **'По выручке'**
  String get abcByRevenue;

  /// No description provided for @abcByProfit.
  ///
  /// In ru, this message translates to:
  /// **'По прибыли'**
  String get abcByProfit;

  /// No description provided for @abcByQty.
  ///
  /// In ru, this message translates to:
  /// **'По штукам'**
  String get abcByQty;

  /// No description provided for @abcA.
  ///
  /// In ru, this message translates to:
  /// **'Кормят магазин'**
  String get abcA;

  /// No description provided for @abcB.
  ///
  /// In ru, this message translates to:
  /// **'Середина'**
  String get abcB;

  /// No description provided for @abcC.
  ///
  /// In ru, this message translates to:
  /// **'Хвост'**
  String get abcC;

  /// No description provided for @abcGroupNote.
  ///
  /// In ru, this message translates to:
  /// **'{n} поз. · {share} %'**
  String abcGroupNote(int n, String share);

  /// No description provided for @paretoTitle.
  ///
  /// In ru, this message translates to:
  /// **'Диаграмма Парето'**
  String get paretoTitle;

  /// No description provided for @paretoHint.
  ///
  /// In ru, this message translates to:
  /// **'Столбцы — вклад товара, линия — накопленная доля. Товары левее отметки 80 % — группа A.'**
  String get paretoHint;

  /// No description provided for @advisorTitle.
  ///
  /// In ru, this message translates to:
  /// **'Нур Советник'**
  String get advisorTitle;

  /// No description provided for @advisorOnline.
  ///
  /// In ru, this message translates to:
  /// **'Данные магазина подключены'**
  String get advisorOnline;

  /// No description provided for @advisorAsk.
  ///
  /// In ru, this message translates to:
  /// **'Спросите о своём магазине…'**
  String get advisorAsk;

  /// No description provided for @advisorThinking.
  ///
  /// In ru, this message translates to:
  /// **'Советник думает…'**
  String get advisorThinking;

  /// No description provided for @advisorQ1.
  ///
  /// In ru, this message translates to:
  /// **'Сколько продали вчера?'**
  String get advisorQ1;

  /// No description provided for @advisorQ2.
  ///
  /// In ru, this message translates to:
  /// **'Кто должен?'**
  String get advisorQ2;

  /// No description provided for @advisorQ3.
  ///
  /// In ru, this message translates to:
  /// **'Что заказать?'**
  String get advisorQ3;

  /// No description provided for @advisorQ4.
  ///
  /// In ru, this message translates to:
  /// **'Как прошла неделя?'**
  String get advisorQ4;

  /// No description provided for @advisorNew.
  ///
  /// In ru, this message translates to:
  /// **'Новый разговор'**
  String get advisorNew;

  /// No description provided for @advisorNoKey.
  ///
  /// In ru, this message translates to:
  /// **'Добавьте ключ ИИ в программе владельца: Нур Советник → ⚙ → Модели ИИ и ключи'**
  String get advisorNoKey;

  /// No description provided for @advisorStageData.
  ///
  /// In ru, this message translates to:
  /// **'Собираю данные магазина…'**
  String get advisorStageData;

  /// No description provided for @advisorStageAsk.
  ///
  /// In ru, this message translates to:
  /// **'Спрашиваю ИИ…'**
  String get advisorStageAsk;

  /// No description provided for @advisorError.
  ///
  /// In ru, this message translates to:
  /// **'Советник не ответил: {error}'**
  String advisorError(String error);

  /// No description provided for @callTitle.
  ///
  /// In ru, this message translates to:
  /// **'Разговор'**
  String get callTitle;

  /// No description provided for @callConnecting.
  ///
  /// In ru, this message translates to:
  /// **'Подключаюсь…'**
  String get callConnecting;

  /// No description provided for @callPreparing.
  ///
  /// In ru, this message translates to:
  /// **'Готовлю данные магазина…'**
  String get callPreparing;

  /// No description provided for @callListening.
  ///
  /// In ru, this message translates to:
  /// **'Слушаю…'**
  String get callListening;

  /// No description provided for @callThinking.
  ///
  /// In ru, this message translates to:
  /// **'Думаю…'**
  String get callThinking;

  /// No description provided for @callSpeaking.
  ///
  /// In ru, this message translates to:
  /// **'Говорю… (нажмите, чтобы перебить)'**
  String get callSpeaking;

  /// No description provided for @callEnded.
  ///
  /// In ru, this message translates to:
  /// **'Звонок завершён'**
  String get callEnded;

  /// No description provided for @callEnd.
  ///
  /// In ru, this message translates to:
  /// **'Завершить'**
  String get callEnd;

  /// No description provided for @callMicOff.
  ///
  /// In ru, this message translates to:
  /// **'Микрофон выкл.'**
  String get callMicOff;

  /// No description provided for @callMicOn.
  ///
  /// In ru, this message translates to:
  /// **'Микрофон вкл.'**
  String get callMicOn;

  /// No description provided for @callSpeakerOn.
  ///
  /// In ru, this message translates to:
  /// **'Громкая связь'**
  String get callSpeakerOn;

  /// No description provided for @callSpeakerOff.
  ///
  /// In ru, this message translates to:
  /// **'Без громкой связи'**
  String get callSpeakerOff;

  /// No description provided for @callCost.
  ///
  /// In ru, this message translates to:
  /// **'Звонок длится до {n} мин и расходует лимит ключа ИИ быстрее, чем чат.'**
  String callCost(int n);

  /// No description provided for @callLimitSoon.
  ///
  /// In ru, this message translates to:
  /// **'Через минуту звонок завершится'**
  String get callLimitSoon;

  /// No description provided for @callEndedLimit.
  ///
  /// In ru, this message translates to:
  /// **'Звонок завершён: прошло {n} мин. Можно позвонить снова.'**
  String callEndedLimit(int n);

  /// No description provided for @callEndedFocus.
  ///
  /// In ru, this message translates to:
  /// **'Звонок прерван: входящий звонок или приложение свёрнуто.'**
  String get callEndedFocus;

  /// No description provided for @callFailed.
  ///
  /// In ru, this message translates to:
  /// **'Звонок не удался: {error}'**
  String callFailed(String error);

  /// No description provided for @callNoMic.
  ///
  /// In ru, this message translates to:
  /// **'Нет доступа к микрофону. Разрешите микрофон для NurMarket в настройках телефона.'**
  String get callNoMic;

  /// No description provided for @callOpenSettings.
  ///
  /// In ru, this message translates to:
  /// **'Открыть настройки'**
  String get callOpenSettings;

  /// No description provided for @callUnsupported.
  ///
  /// In ru, this message translates to:
  /// **'Голосовой звонок работает на телефоне Android.'**
  String get callUnsupported;

  /// No description provided for @callTableReady.
  ///
  /// In ru, this message translates to:
  /// **'Таблица с цифрами будет в чате после звонка'**
  String get callTableReady;

  /// No description provided for @callClose.
  ///
  /// In ru, this message translates to:
  /// **'Закрыть'**
  String get callClose;

  /// No description provided for @callYou.
  ///
  /// In ru, this message translates to:
  /// **'Вы'**
  String get callYou;

  /// No description provided for @checkTab.
  ///
  /// In ru, this message translates to:
  /// **'Чек · {n}'**
  String checkTab(int n);

  /// No description provided for @deleteProduct.
  ///
  /// In ru, this message translates to:
  /// **'Удалить товар'**
  String get deleteProduct;

  /// No description provided for @deleteProductAsk.
  ///
  /// In ru, this message translates to:
  /// **'Удалить «{name}»? Продажи и отчёты по нему останутся.'**
  String deleteProductAsk(String name);

  /// No description provided for @productDeleted.
  ///
  /// In ru, this message translates to:
  /// **'Товар удалён'**
  String get productDeleted;

  /// No description provided for @smartUpload.
  ///
  /// In ru, this message translates to:
  /// **'Загрузить {n} товаров на склад'**
  String smartUpload(int n);

  /// No description provided for @smartToReceive.
  ///
  /// In ru, this message translates to:
  /// **'В приёмку с поставщиком'**
  String get smartToReceive;

  /// No description provided for @smartShots.
  ///
  /// In ru, this message translates to:
  /// **'Снимков: {n}'**
  String smartShots(int n);

  /// No description provided for @smartPriceRule.
  ///
  /// In ru, this message translates to:
  /// **'Цена продажи новых: +30 % к закупке, до 5 сом'**
  String get smartPriceRule;

  /// No description provided for @smartUploaded.
  ///
  /// In ru, this message translates to:
  /// **'Загружено на склад: {n}'**
  String smartUploaded(int n);

  /// No description provided for @sellingPrice.
  ///
  /// In ru, this message translates to:
  /// **'Продажная цена'**
  String get sellingPrice;

  /// No description provided for @durationCustom.
  ///
  /// In ru, this message translates to:
  /// **'Своя…'**
  String get durationCustom;

  /// No description provided for @minutesHint.
  ///
  /// In ru, this message translates to:
  /// **'минут'**
  String get minutesHint;

  /// No description provided for @photoLabel.
  ///
  /// In ru, this message translates to:
  /// **'Фото'**
  String get photoLabel;

  /// No description provided for @shiftDetails.
  ///
  /// In ru, this message translates to:
  /// **'Отчёт смены'**
  String get shiftDetails;

  /// No description provided for @shOpened.
  ///
  /// In ru, this message translates to:
  /// **'Открыта'**
  String get shOpened;

  /// No description provided for @shClosed.
  ///
  /// In ru, this message translates to:
  /// **'Закрыта'**
  String get shClosed;

  /// No description provided for @shOpeningCash.
  ///
  /// In ru, this message translates to:
  /// **'В кассе на начало'**
  String get shOpeningCash;

  /// No description provided for @shNonCash.
  ///
  /// In ru, this message translates to:
  /// **'Безнал'**
  String get shNonCash;

  /// No description provided for @shDebt.
  ///
  /// In ru, this message translates to:
  /// **'В долг'**
  String get shDebt;

  /// No description provided for @shReturns.
  ///
  /// In ru, this message translates to:
  /// **'Возвраты'**
  String get shReturns;

  /// No description provided for @shIncome.
  ///
  /// In ru, this message translates to:
  /// **'Прочий приход'**
  String get shIncome;

  /// No description provided for @shExpected.
  ///
  /// In ru, this message translates to:
  /// **'Должно быть в кассе'**
  String get shExpected;

  /// No description provided for @shCounted.
  ///
  /// In ru, this message translates to:
  /// **'Пересчитано'**
  String get shCounted;

  /// No description provided for @shDifference.
  ///
  /// In ru, this message translates to:
  /// **'Разница'**
  String get shDifference;

  /// No description provided for @orderReadyNotify.
  ///
  /// In ru, this message translates to:
  /// **'Здравствуйте, {name}! Ваш заказ №{num} готов к выдаче. К оплате {sum} сом. Ждём вас!'**
  String orderReadyNotify(String name, String num, String sum);

  /// No description provided for @customSize.
  ///
  /// In ru, this message translates to:
  /// **'Свой размер'**
  String get customSize;

  /// No description provided for @pickSupplier.
  ///
  /// In ru, this message translates to:
  /// **'Выбрать поставщика'**
  String get pickSupplier;

  /// No description provided for @addProductsHint.
  ///
  /// In ru, this message translates to:
  /// **'Найдите товар и нажмите, чтобы добавить'**
  String get addProductsHint;

  /// No description provided for @removeAction.
  ///
  /// In ru, this message translates to:
  /// **'Убрать'**
  String get removeAction;

  /// No description provided for @esfExportCsv.
  ///
  /// In ru, this message translates to:
  /// **'Выгрузить в Excel (CSV)'**
  String get esfExportCsv;

  /// No description provided for @esfExportAll.
  ///
  /// In ru, this message translates to:
  /// **'Все черновики в Excel (CSV)'**
  String get esfExportAll;

  /// No description provided for @esfTaxSoon.
  ///
  /// In ru, this message translates to:
  /// **'Отправка в налоговую заработает, когда сервер NurCRM подключит ЭСФ ГНС. Пока выгрузите в Excel и загрузите на сайте ГНС.'**
  String get esfTaxSoon;

  /// No description provided for @dateLabel.
  ///
  /// In ru, this message translates to:
  /// **'Дата'**
  String get dateLabel;

  /// No description provided for @supplierNoLow.
  ///
  /// In ru, this message translates to:
  /// **'У этого поставщика нет заканчивающихся товаров'**
  String get supplierNoLow;

  /// No description provided for @supplierLowAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить заканчивающиеся товары поставщика'**
  String get supplierLowAdd;

  /// No description provided for @inStockOnly.
  ///
  /// In ru, this message translates to:
  /// **'В наличии'**
  String get inStockOnly;

  /// No description provided for @allCategories.
  ///
  /// In ru, this message translates to:
  /// **'Все категории'**
  String get allCategories;

  /// No description provided for @catalogPick.
  ///
  /// In ru, this message translates to:
  /// **'Из каталога'**
  String get catalogPick;

  /// No description provided for @themeAuto.
  ///
  /// In ru, this message translates to:
  /// **'По времени'**
  String get themeAuto;

  /// No description provided for @darkFrom.
  ///
  /// In ru, this message translates to:
  /// **'Тёмная с'**
  String get darkFrom;

  /// No description provided for @darkTo.
  ///
  /// In ru, this message translates to:
  /// **'до'**
  String get darkTo;

  /// No description provided for @leaveAsk.
  ///
  /// In ru, this message translates to:
  /// **'Вы точно хотите выйти?'**
  String get leaveAsk;

  /// No description provided for @leaveAskSub.
  ///
  /// In ru, this message translates to:
  /// **'Несохранённые изменения пропадут.'**
  String get leaveAskSub;

  /// No description provided for @leaveYes.
  ///
  /// In ru, this message translates to:
  /// **'Да, конечно'**
  String get leaveYes;

  /// No description provided for @leaveNo.
  ///
  /// In ru, this message translates to:
  /// **'Нет, случайно нажал'**
  String get leaveNo;

  /// No description provided for @aiSettings.
  ///
  /// In ru, this message translates to:
  /// **'Настройка ИИ'**
  String get aiSettings;

  /// No description provided for @aiSettingsHint.
  ///
  /// In ru, this message translates to:
  /// **'Нур Советник отвечает через Gemini. Если у Gemini кончился бесплатный лимит — через Groq, затем OpenRouter. Ключи бесплатные; сохраняются в NurCRM и работают на телефоне, кассе и в программе владельца.'**
  String get aiSettingsHint;

  /// No description provided for @aiConnected.
  ///
  /// In ru, this message translates to:
  /// **'подключён'**
  String get aiConnected;

  /// No description provided for @aiNoKeyShort.
  ///
  /// In ru, this message translates to:
  /// **'нет ключа'**
  String get aiNoKeyShort;

  /// No description provided for @aiGetKey.
  ///
  /// In ru, this message translates to:
  /// **'Получить ключ'**
  String get aiGetKey;

  /// No description provided for @aiTest.
  ///
  /// In ru, this message translates to:
  /// **'Проверить'**
  String get aiTest;

  /// No description provided for @aiTestOk.
  ///
  /// In ru, this message translates to:
  /// **'Работает ✓'**
  String get aiTestOk;

  /// No description provided for @aiKeysSaved.
  ///
  /// In ru, this message translates to:
  /// **'Ключи сохранены'**
  String get aiKeysSaved;

  /// No description provided for @aiPasteKey.
  ///
  /// In ru, this message translates to:
  /// **'Вставьте новый ключ'**
  String get aiPasteKey;

  /// No description provided for @aiRemoveKey.
  ///
  /// In ru, this message translates to:
  /// **'Убрать ключ'**
  String get aiRemoveKey;

  /// No description provided for @payQrTitle.
  ///
  /// In ru, this message translates to:
  /// **'QR для оплаты'**
  String get payQrTitle;

  /// No description provided for @payQrHint.
  ///
  /// In ru, this message translates to:
  /// **'Добавьте QR своих банков (MBank, Optima и др.). Покупатель сканирует QR с суммой чека и платит в приложении банка. Укажите, каким кассирам какой QR показывать — у кассира на выбор до 5 банков. Список общий для всех телефонов магазина.'**
  String get payQrHint;

  /// No description provided for @payQrAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить QR'**
  String get payQrAdd;

  /// No description provided for @payQrBank.
  ///
  /// In ru, this message translates to:
  /// **'Банк'**
  String get payQrBank;

  /// No description provided for @payQrCode.
  ///
  /// In ru, this message translates to:
  /// **'QR банка'**
  String get payQrCode;

  /// No description provided for @payQrScan.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать'**
  String get payQrScan;

  /// No description provided for @payQrGallery.
  ///
  /// In ru, this message translates to:
  /// **'Из галереи (скриншот)'**
  String get payQrGallery;

  /// No description provided for @payQrPaste.
  ///
  /// In ru, this message translates to:
  /// **'Вставить ссылку'**
  String get payQrPaste;

  /// No description provided for @payQrDynamic.
  ///
  /// In ru, this message translates to:
  /// **'Сумма чека в QR'**
  String get payQrDynamic;

  /// No description provided for @payQrDynamicSub.
  ///
  /// In ru, this message translates to:
  /// **'Покупателю не нужно набирать сумму (стандарт ELQR, как на кассе; проверено на MBank). Если банк покупателя не примет — выключите.'**
  String get payQrDynamicSub;

  /// No description provided for @payQrStaticOnly.
  ///
  /// In ru, this message translates to:
  /// **'Этот QR без стандарта ELQR — сумма не вписывается, покажется как есть'**
  String get payQrStaticOnly;

  /// No description provided for @payQrCashiers.
  ///
  /// In ru, this message translates to:
  /// **'Кому показывать'**
  String get payQrCashiers;

  /// No description provided for @payQrAllCashiers.
  ///
  /// In ru, this message translates to:
  /// **'Всем кассирам'**
  String get payQrAllCashiers;

  /// No description provided for @payQrNotRecognized.
  ///
  /// In ru, this message translates to:
  /// **'QR не распознан'**
  String get payQrNotRecognized;

  /// No description provided for @payQrShowCustomer.
  ///
  /// In ru, this message translates to:
  /// **'Покажите покупателю (нажмите — крупно). После оплаты нажмите «Оплатить».'**
  String get payQrShowCustomer;

  /// No description provided for @payQrNone.
  ///
  /// In ru, this message translates to:
  /// **'QR для оплаты не добавлены — владелец добавляет в «Управление → QR для оплаты».'**
  String get payQrNone;

  /// No description provided for @payQrNoneOwner.
  ///
  /// In ru, this message translates to:
  /// **'Пока ни одного QR. Нажмите «Добавить QR».'**
  String get payQrNoneOwner;

  /// No description provided for @payQrSaved.
  ///
  /// In ru, this message translates to:
  /// **'QR сохранены — у кассиров появятся сразу'**
  String get payQrSaved;

  /// No description provided for @payQrDelete.
  ///
  /// In ru, this message translates to:
  /// **'Удалить QR'**
  String get payQrDelete;

  /// No description provided for @grpAi.
  ///
  /// In ru, this message translates to:
  /// **'ИИ и аналитика'**
  String get grpAi;

  /// No description provided for @grpStock.
  ///
  /// In ru, this message translates to:
  /// **'Товары и склад'**
  String get grpStock;

  /// No description provided for @grpMoney.
  ///
  /// In ru, this message translates to:
  /// **'Продажи и деньги'**
  String get grpMoney;

  /// No description provided for @grpClients.
  ///
  /// In ru, this message translates to:
  /// **'Клиенты и документы'**
  String get grpClients;

  /// No description provided for @grpTeam.
  ///
  /// In ru, this message translates to:
  /// **'Сотрудники'**
  String get grpTeam;

  /// No description provided for @findSection.
  ///
  /// In ru, this message translates to:
  /// **'Найти раздел'**
  String get findSection;

  /// No description provided for @liveAuto.
  ///
  /// In ru, this message translates to:
  /// **'Авто'**
  String get liveAuto;

  /// No description provided for @liveHintSale.
  ///
  /// In ru, this message translates to:
  /// **'Наведите на товар и нажмите «Сканировать»'**
  String get liveHintSale;

  /// No description provided for @liveHintShelf.
  ///
  /// In ru, this message translates to:
  /// **'Наведите на полку с товарами'**
  String get liveHintShelf;

  /// No description provided for @liveHintInvoice.
  ///
  /// In ru, this message translates to:
  /// **'Наведите на накладную целиком'**
  String get liveHintInvoice;

  /// No description provided for @liveHintLabels.
  ///
  /// In ru, this message translates to:
  /// **'Наведите на ценники и этикетки'**
  String get liveHintLabels;

  /// No description provided for @auditTitle.
  ///
  /// In ru, this message translates to:
  /// **'Журнал аудита'**
  String get auditTitle;

  /// No description provided for @auditSub.
  ///
  /// In ru, this message translates to:
  /// **'Все действия на этом телефоне'**
  String get auditSub;

  /// No description provided for @auditSearch.
  ///
  /// In ru, this message translates to:
  /// **'Сотрудник, товар, номер чека'**
  String get auditSearch;

  /// No description provided for @auditKind.
  ///
  /// In ru, this message translates to:
  /// **'Действие'**
  String get auditKind;

  /// No description provided for @auditWhat.
  ///
  /// In ru, this message translates to:
  /// **'Что сделано'**
  String get auditWhat;

  /// No description provided for @auditSale.
  ///
  /// In ru, this message translates to:
  /// **'Продажа'**
  String get auditSale;

  /// No description provided for @auditCart.
  ///
  /// In ru, this message translates to:
  /// **'В чек'**
  String get auditCart;

  /// No description provided for @auditReturn.
  ///
  /// In ru, this message translates to:
  /// **'Возврат'**
  String get auditReturn;

  /// No description provided for @auditPrice.
  ///
  /// In ru, this message translates to:
  /// **'Цены и остаток'**
  String get auditPrice;

  /// No description provided for @auditProduct.
  ///
  /// In ru, this message translates to:
  /// **'Товары'**
  String get auditProduct;

  /// No description provided for @auditSettings.
  ///
  /// In ru, this message translates to:
  /// **'Настройки'**
  String get auditSettings;

  /// No description provided for @auditLogin.
  ///
  /// In ru, this message translates to:
  /// **'Вход и выход'**
  String get auditLogin;

  /// No description provided for @auditOpen.
  ///
  /// In ru, this message translates to:
  /// **'Открыл экран'**
  String get auditOpen;

  /// No description provided for @teachCamera.
  ///
  /// In ru, this message translates to:
  /// **'Научить умную камеру: 3–5 фото + штрихкод'**
  String get teachCamera;

  /// No description provided for @teachRight.
  ///
  /// In ru, this message translates to:
  /// **'1 из 3 · товар справа'**
  String get teachRight;

  /// No description provided for @teachLeft.
  ///
  /// In ru, this message translates to:
  /// **'2 из 3 · товар слева'**
  String get teachLeft;

  /// No description provided for @teachTop.
  ///
  /// In ru, this message translates to:
  /// **'3 из 3 · товар сверху'**
  String get teachTop;

  /// No description provided for @teachHint.
  ///
  /// In ru, this message translates to:
  /// **'Товар целиком в рамке, на светлом фоне'**
  String get teachHint;

  /// No description provided for @teachDone.
  ///
  /// In ru, this message translates to:
  /// **'Камера обучена — при продаже найдёт этот товар точно'**
  String get teachDone;

  /// No description provided for @teachShots.
  ///
  /// In ru, this message translates to:
  /// **'Для умной камеры: {n} фото'**
  String teachShots(int n);

  /// No description provided for @scanShoot.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать'**
  String get scanShoot;

  /// No description provided for @scanBusy.
  ///
  /// In ru, this message translates to:
  /// **'Сканирую…'**
  String get scanBusy;

  /// No description provided for @scanNext.
  ///
  /// In ru, this message translates to:
  /// **'Следующий товар — снова «Сканировать»'**
  String get scanNext;

  /// No description provided for @scanChecking.
  ///
  /// In ru, this message translates to:
  /// **'Сверяю с обученными товарами…'**
  String get scanChecking;

  /// No description provided for @scanTook.
  ///
  /// In ru, this message translates to:
  /// **'за {sec} с'**
  String scanTook(String sec);

  /// No description provided for @teachPhotoOf.
  ///
  /// In ru, this message translates to:
  /// **'Фото {a} из {b}'**
  String teachPhotoOf(int a, int b);

  /// No description provided for @teachAngleRight.
  ///
  /// In ru, this message translates to:
  /// **'товар справа'**
  String get teachAngleRight;

  /// No description provided for @teachAngleLeft.
  ///
  /// In ru, this message translates to:
  /// **'товар слева'**
  String get teachAngleLeft;

  /// No description provided for @teachAngleTop.
  ///
  /// In ru, this message translates to:
  /// **'товар сверху'**
  String get teachAngleTop;

  /// No description provided for @teachAngleFront.
  ///
  /// In ru, this message translates to:
  /// **'этикетка спереди'**
  String get teachAngleFront;

  /// No description provided for @teachAngleBack.
  ///
  /// In ru, this message translates to:
  /// **'товар сзади'**
  String get teachAngleBack;

  /// No description provided for @teachCanFinish.
  ///
  /// In ru, this message translates to:
  /// **'Можно нажать «Готово» или снять ещё ракурс'**
  String get teachCanFinish;

  /// No description provided for @teachRetake.
  ///
  /// In ru, this message translates to:
  /// **'Переснять'**
  String get teachRetake;

  /// No description provided for @weighAim.
  ///
  /// In ru, this message translates to:
  /// **'Положите товар на весы, наведите на табло и нажмите «Сканировать»'**
  String get weighAim;

  /// No description provided for @weighAimDisplay.
  ///
  /// In ru, this message translates to:
  /// **'Табло весов не видно — наведите камеру на окно веса, выключите фонарик и нажмите «Сканировать»'**
  String get weighAimDisplay;

  /// No description provided for @aiErrNoNet.
  ///
  /// In ru, this message translates to:
  /// **'Нет интернета — проверьте связь и нажмите «Сканировать» ещё раз'**
  String get aiErrNoNet;

  /// No description provided for @aiErrSlow.
  ///
  /// In ru, this message translates to:
  /// **'ИИ не ответил вовремя (медленная связь) — попробуйте ещё раз'**
  String get aiErrSlow;

  /// No description provided for @aiErrLimit.
  ///
  /// In ru, this message translates to:
  /// **'Кончился бесплатный лимит запросов ИИ — попробуйте через минуту'**
  String get aiErrLimit;

  /// No description provided for @aiErrBusy.
  ///
  /// In ru, this message translates to:
  /// **'ИИ сейчас перегружен — попробуйте через минуту'**
  String get aiErrBusy;

  /// No description provided for @aiErrOther.
  ///
  /// In ru, this message translates to:
  /// **'ИИ не смог распознать снимок'**
  String get aiErrOther;

  /// No description provided for @addAgain.
  ///
  /// In ru, this message translates to:
  /// **'«{name}» уже в чеке. Добавить ещё?'**
  String addAgain(String name);

  /// No description provided for @yesAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить'**
  String get yesAdd;

  /// No description provided for @shelfAddAsk.
  ///
  /// In ru, this message translates to:
  /// **'Снимок полки — это товар, который уже стоит на полке. Прибавить эти количества к остатку как поставку?'**
  String get shelfAddAsk;

  /// No description provided for @isItProduct.
  ///
  /// In ru, this message translates to:
  /// **'Это «{name}»?'**
  String isItProduct(String name);

  /// No description provided for @notFoundOffline.
  ///
  /// In ru, this message translates to:
  /// **'Код {code} не найден на телефоне, а сервер не отвечает. Проверьте связь — товар может быть уже заведён на ПК, не создавайте второй.'**
  String notFoundOffline(String code);

  /// No description provided for @scaleLabelsTitle.
  ///
  /// In ru, this message translates to:
  /// **'Этикетки весов'**
  String get scaleLabelsTitle;

  /// No description provided for @scaleLabelsHint.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод этикетки весов начинается с префикса (20–29). Укажите, какие префиксы несут СУММУ, а какие — ВЕС. Для всех телефонов компании.'**
  String get scaleLabelsHint;

  /// No description provided for @scaleAmountPrefixes.
  ///
  /// In ru, this message translates to:
  /// **'Префиксы «сумма»'**
  String get scaleAmountPrefixes;

  /// No description provided for @scaleWeightPrefixes.
  ///
  /// In ru, this message translates to:
  /// **'Префиксы «вес»'**
  String get scaleWeightPrefixes;

  /// No description provided for @cameraShotFailed.
  ///
  /// In ru, this message translates to:
  /// **'Камера не сделала снимок — нажмите «Сканировать» ещё раз'**
  String get cameraShotFailed;

  /// No description provided for @saved.
  ///
  /// In ru, this message translates to:
  /// **'Сохранено'**
  String get saved;

  /// No description provided for @exactMatch.
  ///
  /// In ru, this message translates to:
  /// **'точно'**
  String get exactMatch;

  /// No description provided for @noAccess.
  ///
  /// In ru, this message translates to:
  /// **'Нет доступа'**
  String get noAccess;

  /// No description provided for @noAccessHint.
  ///
  /// In ru, this message translates to:
  /// **'Этот раздел или действие закрыты для вашей учётной записи. Доступ включает владелец: NurCRM → Сотрудники → доступы.'**
  String get noAccessHint;

  /// No description provided for @clientOptional.
  ///
  /// In ru, this message translates to:
  /// **'Клиент — для бонусов и истории покупок'**
  String get clientOptional;

  /// No description provided for @bonusRedeem.
  ///
  /// In ru, this message translates to:
  /// **'Списать бонусами'**
  String get bonusRedeem;

  /// No description provided for @bonusNone.
  ///
  /// In ru, this message translates to:
  /// **'Не списывать'**
  String get bonusNone;

  /// No description provided for @bonusAll.
  ///
  /// In ru, this message translates to:
  /// **'Максимум · {sum}'**
  String bonusAll(String sum);

  /// No description provided for @bonusEarn.
  ///
  /// In ru, this message translates to:
  /// **'Начислится бонусов: {sum}'**
  String bonusEarn(String sum);

  /// No description provided for @bonusPaid.
  ///
  /// In ru, this message translates to:
  /// **'Бонусами: −{sum} сом'**
  String bonusPaid(String sum);

  /// No description provided for @removeClient.
  ///
  /// In ru, this message translates to:
  /// **'Убрать клиента'**
  String get removeClient;

  /// No description provided for @discountRemove.
  ///
  /// In ru, this message translates to:
  /// **'Убрать скидку'**
  String get discountRemove;

  /// No description provided for @addedToCheck.
  ///
  /// In ru, this message translates to:
  /// **'Добавлено в чек'**
  String get addedToCheck;

  /// No description provided for @payment.
  ///
  /// In ru, this message translates to:
  /// **'Оплата'**
  String get payment;

  /// No description provided for @toPayPositions.
  ///
  /// In ru, this message translates to:
  /// **'К оплате · {n} позиций'**
  String toPayPositions(int n);

  /// No description provided for @payMethod.
  ///
  /// In ru, this message translates to:
  /// **'Способ оплаты'**
  String get payMethod;

  /// No description provided for @cash.
  ///
  /// In ru, this message translates to:
  /// **'Наличные'**
  String get cash;

  /// No description provided for @card.
  ///
  /// In ru, this message translates to:
  /// **'Карта'**
  String get card;

  /// No description provided for @debt.
  ///
  /// In ru, this message translates to:
  /// **'В долг'**
  String get debt;

  /// No description provided for @mixed.
  ///
  /// In ru, this message translates to:
  /// **'Смешанная'**
  String get mixed;

  /// No description provided for @client.
  ///
  /// In ru, this message translates to:
  /// **'Клиент'**
  String get client;

  /// No description provided for @received.
  ///
  /// In ru, this message translates to:
  /// **'Получено'**
  String get received;

  /// No description provided for @receivedNowDebt.
  ///
  /// In ru, this message translates to:
  /// **'Получено сейчас (0 — весь долг)'**
  String get receivedNowDebt;

  /// No description provided for @exact.
  ///
  /// In ru, this message translates to:
  /// **'Без сдачи'**
  String get exact;

  /// No description provided for @change.
  ///
  /// In ru, this message translates to:
  /// **'Сдача'**
  String get change;

  /// No description provided for @notEnough.
  ///
  /// In ru, this message translates to:
  /// **'Не хватает {sum} сом'**
  String notEnough(String sum);

  /// No description provided for @receiptToCustomer.
  ///
  /// In ru, this message translates to:
  /// **'Чек покупателю'**
  String get receiptToCustomer;

  /// No description provided for @notNeeded.
  ///
  /// In ru, this message translates to:
  /// **'Не нужен'**
  String get notNeeded;

  /// No description provided for @doPay.
  ///
  /// In ru, this message translates to:
  /// **'Провести оплату'**
  String get doPay;

  /// No description provided for @check.
  ///
  /// In ru, this message translates to:
  /// **'Чек'**
  String get check;

  /// No description provided for @total.
  ///
  /// In ru, this message translates to:
  /// **'Итого'**
  String get total;

  /// No description provided for @paidOk.
  ///
  /// In ru, this message translates to:
  /// **'Оплата проведена'**
  String get paidOk;

  /// No description provided for @savedOffline.
  ///
  /// In ru, this message translates to:
  /// **'Чек сохранён на телефоне'**
  String get savedOffline;

  /// No description provided for @savedOfflineHint.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи — чек уйдёт на сервер сам, второй раз не проведётся.'**
  String get savedOfflineHint;

  /// No description provided for @receiptQr.
  ///
  /// In ru, this message translates to:
  /// **'QR-код'**
  String get receiptQr;

  /// No description provided for @showQr.
  ///
  /// In ru, this message translates to:
  /// **'Показать QR покупателю'**
  String get showQr;

  /// No description provided for @qrPointCamera.
  ///
  /// In ru, this message translates to:
  /// **'Покупатель наводит камеру телефона'**
  String get qrPointCamera;

  /// No description provided for @qrDownloadHint.
  ///
  /// In ru, this message translates to:
  /// **'Камера покупателя сразу скачает чек PDF-файлом'**
  String get qrDownloadHint;

  /// No description provided for @qrTextHint.
  ///
  /// In ru, this message translates to:
  /// **'Пока сервер не выдаёт ссылку на чек, камера покажет его текстом. PDF-файл клиенту — кнопкой ниже.'**
  String get qrTextHint;

  /// No description provided for @cashInOut.
  ///
  /// In ru, this message translates to:
  /// **'Внесение / изъятие'**
  String get cashInOut;

  /// No description provided for @cashIn.
  ///
  /// In ru, this message translates to:
  /// **'Внесение'**
  String get cashIn;

  /// No description provided for @cashOut.
  ///
  /// In ru, this message translates to:
  /// **'Изъятие'**
  String get cashOut;

  /// No description provided for @amount.
  ///
  /// In ru, this message translates to:
  /// **'Сумма'**
  String get amount;

  /// No description provided for @reason.
  ///
  /// In ru, this message translates to:
  /// **'Причина'**
  String get reason;

  /// No description provided for @reasonChange.
  ///
  /// In ru, this message translates to:
  /// **'Размен'**
  String get reasonChange;

  /// No description provided for @reasonCollection.
  ///
  /// In ru, this message translates to:
  /// **'Инкассация'**
  String get reasonCollection;

  /// No description provided for @reasonExpense.
  ///
  /// In ru, this message translates to:
  /// **'Расходы'**
  String get reasonExpense;

  /// No description provided for @reasonOther.
  ///
  /// In ru, this message translates to:
  /// **'Другое'**
  String get reasonOther;

  /// No description provided for @cashOpDone.
  ///
  /// In ru, this message translates to:
  /// **'Записано в смену'**
  String get cashOpDone;

  /// No description provided for @debts.
  ///
  /// In ru, this message translates to:
  /// **'Долги клиентов'**
  String get debts;

  /// No description provided for @debtorsTotal.
  ///
  /// In ru, this message translates to:
  /// **'Всего долгов'**
  String get debtorsTotal;

  /// No description provided for @payDebt.
  ///
  /// In ru, this message translates to:
  /// **'Принять оплату долга'**
  String get payDebt;

  /// No description provided for @payDebtDone.
  ///
  /// In ru, this message translates to:
  /// **'Оплата долга проведена'**
  String get payDebtDone;

  /// No description provided for @noDebts.
  ///
  /// In ru, this message translates to:
  /// **'Должников нет'**
  String get noDebts;

  /// No description provided for @heldChecks.
  ///
  /// In ru, this message translates to:
  /// **'Отложенные чеки'**
  String get heldChecks;

  /// No description provided for @holdCheck.
  ///
  /// In ru, this message translates to:
  /// **'Отложить чек'**
  String get holdCheck;

  /// No description provided for @holdNote.
  ///
  /// In ru, this message translates to:
  /// **'Для кого или заметка'**
  String get holdNote;

  /// No description provided for @heldEmpty.
  ///
  /// In ru, this message translates to:
  /// **'Отложенных чеков нет'**
  String get heldEmpty;

  /// No description provided for @restoreCheck.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть в чек'**
  String get restoreCheck;

  /// No description provided for @delete.
  ///
  /// In ru, this message translates to:
  /// **'Удалить'**
  String get delete;

  /// No description provided for @checkBusy.
  ///
  /// In ru, this message translates to:
  /// **'В чеке уже есть товары — сначала отложите или оплатите его'**
  String get checkBusy;

  /// No description provided for @returnTitle.
  ///
  /// In ru, this message translates to:
  /// **'Возврат по чеку'**
  String get returnTitle;

  /// No description provided for @returnAll.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть весь чек'**
  String get returnAll;

  /// No description provided for @returnSelected.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть выбранное'**
  String get returnSelected;

  /// No description provided for @returnDone.
  ///
  /// In ru, this message translates to:
  /// **'Возврат проведён'**
  String get returnDone;

  /// No description provided for @returnToCustomer.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть покупателю'**
  String get returnToCustomer;

  /// No description provided for @showReceipt.
  ///
  /// In ru, this message translates to:
  /// **'Посмотреть чек'**
  String get showReceipt;

  /// No description provided for @sendPdf.
  ///
  /// In ru, this message translates to:
  /// **'Отправить PDF'**
  String get sendPdf;

  /// No description provided for @sendReceipt.
  ///
  /// In ru, this message translates to:
  /// **'Отправить чек'**
  String get sendReceipt;

  /// No description provided for @newClient.
  ///
  /// In ru, this message translates to:
  /// **'Новый клиент'**
  String get newClient;

  /// No description provided for @clientName.
  ///
  /// In ru, this message translates to:
  /// **'Имя'**
  String get clientName;

  /// No description provided for @phone.
  ///
  /// In ru, this message translates to:
  /// **'Телефон'**
  String get phone;

  /// No description provided for @clientSearch.
  ///
  /// In ru, this message translates to:
  /// **'Имя или телефон'**
  String get clientSearch;

  /// No description provided for @myChecks.
  ///
  /// In ru, this message translates to:
  /// **'Мои чеки'**
  String get myChecks;

  /// No description provided for @thisShift.
  ///
  /// In ru, this message translates to:
  /// **'Эта смена'**
  String get thisShift;

  /// No description provided for @unsentN.
  ///
  /// In ru, this message translates to:
  /// **'Не отправленные · {n}'**
  String unsentN(int n);

  /// No description provided for @checkSearch.
  ///
  /// In ru, this message translates to:
  /// **'Номер чека или товар'**
  String get checkSearch;

  /// No description provided for @noChecks.
  ///
  /// In ru, this message translates to:
  /// **'Чеков пока нет'**
  String get noChecks;

  /// No description provided for @checkNotSent.
  ///
  /// In ru, this message translates to:
  /// **'Чек не отправлен'**
  String get checkNotSent;

  /// No description provided for @notAccepted.
  ///
  /// In ru, this message translates to:
  /// **'не принят'**
  String get notAccepted;

  /// No description provided for @waitsForNetwork.
  ///
  /// In ru, this message translates to:
  /// **'ждёт связи'**
  String get waitsForNetwork;

  /// No description provided for @returned.
  ///
  /// In ru, this message translates to:
  /// **'возврат'**
  String get returned;

  /// No description provided for @offlineCheckHint.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи — чек сохранён и уйдёт на сервер сам'**
  String get offlineCheckHint;

  /// No description provided for @sendToCustomer.
  ///
  /// In ru, this message translates to:
  /// **'Отправить покупателю'**
  String get sendToCustomer;

  /// No description provided for @sendAgain.
  ///
  /// In ru, this message translates to:
  /// **'Отправить ещё раз'**
  String get sendAgain;

  /// No description provided for @returnByAdmin.
  ///
  /// In ru, this message translates to:
  /// **'Возврат — с разрешения администратора'**
  String get returnByAdmin;

  /// No description provided for @receiveByScanner.
  ///
  /// In ru, this message translates to:
  /// **'Приёмка сканером'**
  String get receiveByScanner;

  /// No description provided for @audit.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия'**
  String get audit;

  /// No description provided for @findProduct.
  ///
  /// In ru, this message translates to:
  /// **'Найти товар'**
  String get findProduct;

  /// No description provided for @pricesByAdmin.
  ///
  /// In ru, this message translates to:
  /// **'Цены и новые товары меняет администратор'**
  String get pricesByAdmin;

  /// No description provided for @receiveNoSupplier.
  ///
  /// In ru, this message translates to:
  /// **'Без поставщика и без цен'**
  String get receiveNoSupplier;

  /// No description provided for @scanProduct.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать товар'**
  String get scanProduct;

  /// No description provided for @eachScanPlusOne.
  ///
  /// In ru, this message translates to:
  /// **'Каждый скан — +1 штука'**
  String get eachScanPlusOne;

  /// No description provided for @accepted.
  ///
  /// In ru, this message translates to:
  /// **'Принято'**
  String get accepted;

  /// No description provided for @goodsPieces.
  ///
  /// In ru, this message translates to:
  /// **'{n} товаров · {qty} шт'**
  String goodsPieces(int n, String qty);

  /// No description provided for @wasPieces.
  ///
  /// In ru, this message translates to:
  /// **'было {qty}'**
  String wasPieces(String qty);

  /// No description provided for @unknownBarcode.
  ///
  /// In ru, this message translates to:
  /// **'Неизвестный штрихкод'**
  String get unknownBarcode;

  /// No description provided for @unknownBarcodeHint.
  ///
  /// In ru, this message translates to:
  /// **'{code} · администратор получит его и заведёт товар'**
  String unknownBarcodeHint(String code);

  /// No description provided for @postpone.
  ///
  /// In ru, this message translates to:
  /// **'Отложить'**
  String get postpone;

  /// No description provided for @postPieces.
  ///
  /// In ru, this message translates to:
  /// **'Провести · {qty} шт'**
  String postPieces(String qty);

  /// No description provided for @receivedOk.
  ///
  /// In ru, this message translates to:
  /// **'Приёмка проведена.'**
  String get receivedOk;

  /// No description provided for @receivedQueued.
  ///
  /// In ru, this message translates to:
  /// **'Приёмка сохранена — уйдёт при связи.'**
  String get receivedQueued;

  /// No description provided for @allStock.
  ///
  /// In ru, this message translates to:
  /// **'Весь склад'**
  String get allStock;

  /// No description provided for @chooseWhatToCount.
  ///
  /// In ru, this message translates to:
  /// **'Что пересчитать'**
  String get chooseWhatToCount;

  /// No description provided for @checkedOf.
  ///
  /// In ru, this message translates to:
  /// **'Проверено {a} из {b}'**
  String checkedOf(int a, int b);

  /// No description provided for @notChecked.
  ///
  /// In ru, this message translates to:
  /// **'Не проверены'**
  String get notChecked;

  /// No description provided for @differences.
  ///
  /// In ru, this message translates to:
  /// **'Расхождения'**
  String get differences;

  /// No description provided for @byProgram.
  ///
  /// In ru, this message translates to:
  /// **'по программе {qty} {unit}'**
  String byProgram(String qty, String unit);

  /// No description provided for @matches.
  ///
  /// In ru, this message translates to:
  /// **'совпало'**
  String get matches;

  /// No description provided for @shortBy.
  ///
  /// In ru, this message translates to:
  /// **'не хватает {qty}'**
  String shortBy(String qty);

  /// No description provided for @extraBy.
  ///
  /// In ru, this message translates to:
  /// **'лишние {qty}'**
  String extraBy(String qty);

  /// No description provided for @auditAdminApplies.
  ///
  /// In ru, this message translates to:
  /// **'Остатки поменяет администратор после проверки'**
  String get auditAdminApplies;

  /// No description provided for @sendToAdmin.
  ///
  /// In ru, this message translates to:
  /// **'Отправить администратору'**
  String get sendToAdmin;

  /// No description provided for @auditSent.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия отправлена администратору.'**
  String get auditSent;

  /// No description provided for @auditQueued.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия сохранена — уйдёт при связи.'**
  String get auditQueued;

  /// No description provided for @sinceChecks.
  ///
  /// In ru, this message translates to:
  /// **'с {time} · {n} чеков'**
  String sinceChecks(String time, int n);

  /// No description provided for @revenue.
  ///
  /// In ru, this message translates to:
  /// **'Выручка'**
  String get revenue;

  /// No description provided for @shouldBeInDrawer.
  ///
  /// In ru, this message translates to:
  /// **'Должно быть в кассе'**
  String get shouldBeInDrawer;

  /// No description provided for @recountCash.
  ///
  /// In ru, this message translates to:
  /// **'Пересчитайте наличные'**
  String get recountCash;

  /// No description provided for @howMuchInDrawer.
  ///
  /// In ru, this message translates to:
  /// **'Сколько денег в кассе'**
  String get howMuchInDrawer;

  /// No description provided for @cashDiff.
  ///
  /// In ru, this message translates to:
  /// **'Разница с программой: {sum} сом'**
  String cashDiff(String sum);

  /// No description provided for @cashDiffHint.
  ///
  /// In ru, this message translates to:
  /// **'Если сумма не сходится, администратор увидит разницу в отчёте'**
  String get cashDiffHint;

  /// No description provided for @closeShiftPending.
  ///
  /// In ru, this message translates to:
  /// **'Сначала должны уйти на сервер {n} чеков — нужна связь.'**
  String closeShiftPending(int n);

  /// No description provided for @shiftClosedOk.
  ///
  /// In ru, this message translates to:
  /// **'Смена закрыта'**
  String get shiftClosedOk;

  /// No description provided for @receiptPrinter.
  ///
  /// In ru, this message translates to:
  /// **'Принтер чеков'**
  String get receiptPrinter;

  /// No description provided for @prnSub.
  ///
  /// In ru, this message translates to:
  /// **'Bluetooth, Wi-Fi/LAN или USB · 58/80 мм'**
  String get prnSub;

  /// No description provided for @prnNotSet.
  ///
  /// In ru, this message translates to:
  /// **'Не настроен'**
  String get prnNotSet;

  /// No description provided for @prnConnection.
  ///
  /// In ru, this message translates to:
  /// **'Подключение'**
  String get prnConnection;

  /// No description provided for @prnOff.
  ///
  /// In ru, this message translates to:
  /// **'Нет'**
  String get prnOff;

  /// No description provided for @prnNetwork.
  ///
  /// In ru, this message translates to:
  /// **'Сеть (Wi-Fi/LAN)'**
  String get prnNetwork;

  /// No description provided for @prnUsb.
  ///
  /// In ru, this message translates to:
  /// **'USB (OTG)'**
  String get prnUsb;

  /// No description provided for @prnSearch.
  ///
  /// In ru, this message translates to:
  /// **'Найти принтеры'**
  String get prnSearch;

  /// No description provided for @prnSearching.
  ///
  /// In ru, this message translates to:
  /// **'Ищу…'**
  String get prnSearching;

  /// No description provided for @prnNothingFound.
  ///
  /// In ru, this message translates to:
  /// **'Ничего не найдено. Принтер включён и рядом?'**
  String get prnNothingFound;

  /// No description provided for @prnPairHint.
  ///
  /// In ru, this message translates to:
  /// **'Принтера нет в списке? Включите его и нажмите «Найти» — или сопрягите в настройках Bluetooth телефона (PIN обычно 0000 или 1234).'**
  String get prnPairHint;

  /// No description provided for @prnUsbHint.
  ///
  /// In ru, this message translates to:
  /// **'Подключите принтер кабелем OTG и включите его. Android спросит разрешение — нажмите «Разрешить».'**
  String get prnUsbHint;

  /// No description provided for @prnBtSettings.
  ///
  /// In ru, this message translates to:
  /// **'Настройки Bluetooth'**
  String get prnBtSettings;

  /// No description provided for @prnPaired.
  ///
  /// In ru, this message translates to:
  /// **'сопряжён'**
  String get prnPaired;

  /// No description provided for @prnNotPaired.
  ///
  /// In ru, this message translates to:
  /// **'рядом, не сопряжён'**
  String get prnNotPaired;

  /// No description provided for @prnIp.
  ///
  /// In ru, this message translates to:
  /// **'IP-адрес принтера'**
  String get prnIp;

  /// No description provided for @prnPort.
  ///
  /// In ru, this message translates to:
  /// **'Порт'**
  String get prnPort;

  /// No description provided for @prnPaper.
  ///
  /// In ru, this message translates to:
  /// **'Ширина ленты'**
  String get prnPaper;

  /// No description provided for @prnPaperMm.
  ///
  /// In ru, this message translates to:
  /// **'{a} мм · {b} знаков'**
  String prnPaperMm(int a, int b);

  /// No description provided for @prnCodepage.
  ///
  /// In ru, this message translates to:
  /// **'Кодировка кириллицы'**
  String get prnCodepage;

  /// No description provided for @prnCodeTable.
  ///
  /// In ru, this message translates to:
  /// **'Таблица ESC t'**
  String get prnCodeTable;

  /// No description provided for @prnCodepageHint.
  ///
  /// In ru, this message translates to:
  /// **'Вместо русских букв иероглифы или «?» — смените кодировку или номер таблицы (CP866 — 17, у некоторых 6 или 7; CP1251 — 46, у некоторых 73) и проверьте ещё раз.'**
  String get prnCodepageHint;

  /// No description provided for @prnAuto.
  ///
  /// In ru, this message translates to:
  /// **'Печатать чек сразу после оплаты'**
  String get prnAuto;

  /// No description provided for @prnTest.
  ///
  /// In ru, this message translates to:
  /// **'Проверить печать'**
  String get prnTest;

  /// No description provided for @prnTestSent.
  ///
  /// In ru, this message translates to:
  /// **'Пробная страница отправлена на принтер'**
  String get prnTestSent;

  /// No description provided for @prnTestHint.
  ///
  /// In ru, this message translates to:
  /// **'Посмотрите пробную страницу: строка цифр должна занять всю ширину, русские буквы — читаться.'**
  String get prnTestHint;

  /// No description provided for @prnPrint.
  ///
  /// In ru, this message translates to:
  /// **'Печать чека'**
  String get prnPrint;

  /// No description provided for @prnSent.
  ///
  /// In ru, this message translates to:
  /// **'Чек отправлен на принтер'**
  String get prnSent;

  /// No description provided for @prnErrNotConfigured.
  ///
  /// In ru, this message translates to:
  /// **'Принтер не настроен: Профиль → Принтер чеков.'**
  String get prnErrNotConfigured;

  /// No description provided for @prnErrUnsupported.
  ///
  /// In ru, this message translates to:
  /// **'На этом устройстве такой способ печати недоступен.'**
  String get prnErrUnsupported;

  /// No description provided for @prnErrBtOff.
  ///
  /// In ru, this message translates to:
  /// **'Bluetooth выключен — включите его и повторите.'**
  String get prnErrBtOff;

  /// No description provided for @prnErrPermission.
  ///
  /// In ru, this message translates to:
  /// **'Нет разрешения «Устройства поблизости» (Bluetooth). Разрешите в окне Android или в настройках приложения.'**
  String get prnErrPermission;

  /// No description provided for @prnErrNotFound.
  ///
  /// In ru, this message translates to:
  /// **'Принтер не найден — проверьте, что он включён, и выберите его заново.'**
  String get prnErrNotFound;

  /// No description provided for @prnErrBleOnly.
  ///
  /// In ru, this message translates to:
  /// **'Этот принтер работает только через BLE — нужен принтер с обычным Bluetooth (SPP).'**
  String get prnErrBleOnly;

  /// No description provided for @prnErrTimeout.
  ///
  /// In ru, this message translates to:
  /// **'Принтер не отвечает. Он включён, рядом, есть бумага? Повторите.'**
  String get prnErrTimeout;

  /// No description provided for @prnErrRefused.
  ///
  /// In ru, this message translates to:
  /// **'Принтер в сети не принимает печать — проверьте IP-адрес и порт (обычно 9100).'**
  String get prnErrRefused;

  /// No description provided for @prnErrUsbDenied.
  ///
  /// In ru, this message translates to:
  /// **'Нет доступа к USB-принтеру — нажмите «Разрешить» в окне Android.'**
  String get prnErrUsbDenied;

  /// No description provided for @prnErrUsbNotFound.
  ///
  /// In ru, this message translates to:
  /// **'USB-принтер не найден — проверьте кабель OTG и питание принтера.'**
  String get prnErrUsbNotFound;

  /// No description provided for @prnErrIo.
  ///
  /// In ru, this message translates to:
  /// **'Ошибка печати: {text}'**
  String prnErrIo(String text);

  /// No description provided for @kgUnit.
  ///
  /// In ru, this message translates to:
  /// **'кг'**
  String get kgUnit;

  /// No description provided for @weighShort.
  ///
  /// In ru, this message translates to:
  /// **'Весы'**
  String get weighShort;

  /// No description provided for @weighTitle.
  ///
  /// In ru, this message translates to:
  /// **'Весовой товар'**
  String get weighTitle;

  /// No description provided for @weighHint.
  ///
  /// In ru, this message translates to:
  /// **'Положите товар на весы. Наведите камеру, чтобы были видны товар и табло весов.'**
  String get weighHint;

  /// No description provided for @weighReading.
  ///
  /// In ru, this message translates to:
  /// **'Узнаю товар и читаю табло весов…'**
  String get weighReading;

  /// No description provided for @weighWaiting.
  ///
  /// In ru, this message translates to:
  /// **'Жду, пока вес установится…'**
  String get weighWaiting;

  /// No description provided for @weighZero.
  ///
  /// In ru, this message translates to:
  /// **'На весах 0 — положите товар.'**
  String get weighZero;

  /// No description provided for @weighNoDisplay.
  ///
  /// In ru, this message translates to:
  /// **'Табло весов не видно — наведите камеру на цифры веса.'**
  String get weighNoDisplay;

  /// No description provided for @weighNoAi.
  ///
  /// In ru, this message translates to:
  /// **'Умная камера недоступна (нет связи или ключа ИИ) — выберите товар и введите вес вручную.'**
  String get weighNoAi;

  /// No description provided for @weighConfirm.
  ///
  /// In ru, this message translates to:
  /// **'Проверьте товар и вес'**
  String get weighConfirm;

  /// No description provided for @weighRead.
  ///
  /// In ru, this message translates to:
  /// **'Камера прочитала: {name} · {kg} кг'**
  String weighRead(String name, String kg);

  /// No description provided for @weighLine.
  ///
  /// In ru, this message translates to:
  /// **'{name} {kg} кг × {price} = {sum} сом'**
  String weighLine(String name, String kg, String price, String sum);

  /// No description provided for @weighTooHeavy.
  ///
  /// In ru, this message translates to:
  /// **'Вес {kg} кг — больше 50 кг. Точно добавить?'**
  String weighTooHeavy(String kg);

  /// No description provided for @weighNotStable.
  ///
  /// In ru, this message translates to:
  /// **'Вес не установился — проверьте показание.'**
  String get weighNotStable;

  /// No description provided for @weighEditKg.
  ///
  /// In ru, this message translates to:
  /// **'Изменить вес'**
  String get weighEditKg;

  /// No description provided for @weighOther.
  ///
  /// In ru, this message translates to:
  /// **'Другой товар — выбрать из списка'**
  String get weighOther;

  /// No description provided for @linkWithKassa.
  ///
  /// In ru, this message translates to:
  /// **'Связь с кассой'**
  String get linkWithKassa;

  /// No description provided for @linkWithKassaSub.
  ///
  /// In ru, this message translates to:
  /// **'По Wi-Fi магазина — в следующем обновлении'**
  String get linkWithKassaSub;

  /// No description provided for @language.
  ///
  /// In ru, this message translates to:
  /// **'Язык'**
  String get language;

  /// No description provided for @darkTheme.
  ///
  /// In ru, this message translates to:
  /// **'Тёмная тема'**
  String get darkTheme;

  /// No description provided for @offlineMode.
  ///
  /// In ru, this message translates to:
  /// **'Работа без интернета'**
  String get offlineMode;

  /// No description provided for @waitingToSend.
  ///
  /// In ru, this message translates to:
  /// **'Ждут отправки: {n}'**
  String waitingToSend(int n);

  /// No description provided for @logout.
  ///
  /// In ru, this message translates to:
  /// **'Выйти из аккаунта'**
  String get logout;

  /// No description provided for @logoutPending.
  ///
  /// In ru, this message translates to:
  /// **'На телефоне {n} неотправленных записей. После выхода они останутся и уйдут при следующем входе этого же аккаунта. Выйти?'**
  String logoutPending(int n);

  /// No description provided for @sync.
  ///
  /// In ru, this message translates to:
  /// **'Синхронизация'**
  String get sync;

  /// No description provided for @catalogUpdated.
  ///
  /// In ru, this message translates to:
  /// **'Каталог обновлён {time}'**
  String catalogUpdated(String time);

  /// No description provided for @nothingToSend.
  ///
  /// In ru, this message translates to:
  /// **'Всё отправлено'**
  String get nothingToSend;

  /// No description provided for @sent.
  ///
  /// In ru, this message translates to:
  /// **'отправлено'**
  String get sent;

  /// No description provided for @sendNow.
  ///
  /// In ru, this message translates to:
  /// **'Отправить сейчас'**
  String get sendNow;

  /// No description provided for @kindSale.
  ///
  /// In ru, this message translates to:
  /// **'Продажа'**
  String get kindSale;

  /// No description provided for @admin.
  ///
  /// In ru, this message translates to:
  /// **'Управление'**
  String get admin;

  /// No description provided for @adminSub.
  ///
  /// In ru, this message translates to:
  /// **'Цены, смены, заказы, клиенты'**
  String get adminSub;

  /// No description provided for @revenueToday.
  ///
  /// In ru, this message translates to:
  /// **'Выручка сегодня'**
  String get revenueToday;

  /// No description provided for @checksToday.
  ///
  /// In ru, this message translates to:
  /// **'Чеков сегодня'**
  String get checksToday;

  /// No description provided for @clientsOwe.
  ///
  /// In ru, this message translates to:
  /// **'Клиенты должны'**
  String get clientsOwe;

  /// No description provided for @productPrice.
  ///
  /// In ru, this message translates to:
  /// **'Товар — цена и остаток'**
  String get productPrice;

  /// No description provided for @shifts.
  ///
  /// In ru, this message translates to:
  /// **'Смены и отчёт'**
  String get shifts;

  /// No description provided for @siteOrders.
  ///
  /// In ru, this message translates to:
  /// **'Заказы с сайта'**
  String get siteOrders;

  /// No description provided for @clientsBonuses.
  ///
  /// In ru, this message translates to:
  /// **'Клиенты и бонусы'**
  String get clientsBonuses;

  /// No description provided for @supplierOrder.
  ///
  /// In ru, this message translates to:
  /// **'Заказ поставщику'**
  String get supplierOrder;

  /// No description provided for @deletedFromCheck.
  ///
  /// In ru, this message translates to:
  /// **'Удалено из чека'**
  String get deletedFromCheck;

  /// No description provided for @nameOrBarcode.
  ///
  /// In ru, this message translates to:
  /// **'Название или штрихкод'**
  String get nameOrBarcode;

  /// No description provided for @salePrice.
  ///
  /// In ru, this message translates to:
  /// **'Цена продажи'**
  String get salePrice;

  /// No description provided for @purchasePrice.
  ///
  /// In ru, this message translates to:
  /// **'Закупка'**
  String get purchasePrice;

  /// No description provided for @wholesalePrice.
  ///
  /// In ru, this message translates to:
  /// **'Оптовая цена'**
  String get wholesalePrice;

  /// No description provided for @onStockLabel.
  ///
  /// In ru, this message translates to:
  /// **'На складе'**
  String get onStockLabel;

  /// No description provided for @priceSaved.
  ///
  /// In ru, this message translates to:
  /// **'Цена сохранена.'**
  String get priceSaved;

  /// No description provided for @markup.
  ///
  /// In ru, this message translates to:
  /// **'Наценка {pct} %'**
  String markup(String pct);

  /// No description provided for @belowCost.
  ///
  /// In ru, this message translates to:
  /// **'Цена ниже закупки — продажа в убыток'**
  String get belowCost;

  /// No description provided for @shiftOpenNow.
  ///
  /// In ru, this message translates to:
  /// **'открыта'**
  String get shiftOpenNow;

  /// No description provided for @shiftClosedAt.
  ///
  /// In ru, this message translates to:
  /// **'закрыта {time}'**
  String shiftClosedAt(String time);

  /// No description provided for @cashierLabel.
  ///
  /// In ru, this message translates to:
  /// **'Кассир'**
  String get cashierLabel;

  /// No description provided for @orderNew.
  ///
  /// In ru, this message translates to:
  /// **'Новый'**
  String get orderNew;

  /// No description provided for @orderPicking.
  ///
  /// In ru, this message translates to:
  /// **'Собираем'**
  String get orderPicking;

  /// No description provided for @orderReady.
  ///
  /// In ru, this message translates to:
  /// **'Готов к выдаче'**
  String get orderReady;

  /// No description provided for @orderDone.
  ///
  /// In ru, this message translates to:
  /// **'Выдан'**
  String get orderDone;

  /// No description provided for @orderCanceled.
  ///
  /// In ru, this message translates to:
  /// **'Отменён'**
  String get orderCanceled;

  /// No description provided for @pickOrder.
  ///
  /// In ru, this message translates to:
  /// **'Собрать заказ'**
  String get pickOrder;

  /// No description provided for @pickScanHint.
  ///
  /// In ru, this message translates to:
  /// **'Сканируйте товар — он сам отметится'**
  String get pickScanHint;

  /// No description provided for @pickedOf.
  ///
  /// In ru, this message translates to:
  /// **'Собрано {a} из {b}'**
  String pickedOf(int a, int b);

  /// No description provided for @orderPicked.
  ///
  /// In ru, this message translates to:
  /// **'Заказ собран'**
  String get orderPicked;

  /// No description provided for @notInOrder.
  ///
  /// In ru, this message translates to:
  /// **'Этого товара нет в заказе'**
  String get notInOrder;

  /// No description provided for @notifyCustomer.
  ///
  /// In ru, this message translates to:
  /// **'Сообщить покупателю'**
  String get notifyCustomer;

  /// No description provided for @handOver.
  ///
  /// In ru, this message translates to:
  /// **'Выдать'**
  String get handOver;

  /// No description provided for @orderReadyMsg.
  ///
  /// In ru, this message translates to:
  /// **'Заказ {num} готов! К оплате {sum} сом. Код выдачи {code}.'**
  String orderReadyMsg(String num, String sum, String code);

  /// No description provided for @statusSaved.
  ///
  /// In ru, this message translates to:
  /// **'Статус сохранён.'**
  String get statusSaved;

  /// No description provided for @bonusBalance.
  ///
  /// In ru, this message translates to:
  /// **'Бонусов на счёте'**
  String get bonusBalance;

  /// No description provided for @addBonus.
  ///
  /// In ru, this message translates to:
  /// **'Начислить бонусы'**
  String get addBonus;

  /// No description provided for @bonusAdded.
  ///
  /// In ru, this message translates to:
  /// **'Бонусы начислены.'**
  String get bonusAdded;

  /// No description provided for @noPhone.
  ///
  /// In ru, this message translates to:
  /// **'нет номера'**
  String get noPhone;

  /// No description provided for @orderToSupplier.
  ///
  /// In ru, this message translates to:
  /// **'Отправить в WhatsApp'**
  String get orderToSupplier;

  /// No description provided for @supplierName.
  ///
  /// In ru, this message translates to:
  /// **'Поставщик'**
  String get supplierName;

  /// No description provided for @supplierPhone.
  ///
  /// In ru, this message translates to:
  /// **'Телефон поставщика'**
  String get supplierPhone;

  /// No description provided for @runningLowAdd.
  ///
  /// In ru, this message translates to:
  /// **'Добавить всё, что заканчивается'**
  String get runningLowAdd;

  /// No description provided for @orderQty.
  ///
  /// In ru, this message translates to:
  /// **'Сколько заказать'**
  String get orderQty;

  /// No description provided for @deleteReason.
  ///
  /// In ru, this message translates to:
  /// **'Почему убираете?'**
  String get deleteReason;

  /// No description provided for @reasonChanged.
  ///
  /// In ru, this message translates to:
  /// **'Покупатель передумал'**
  String get reasonChanged;

  /// No description provided for @reasonScanError.
  ///
  /// In ru, this message translates to:
  /// **'Ошибка сканирования'**
  String get reasonScanError;

  /// No description provided for @reasonNoMoney.
  ///
  /// In ru, this message translates to:
  /// **'Не хватило денег'**
  String get reasonNoMoney;

  /// No description provided for @reasonDefect.
  ///
  /// In ru, this message translates to:
  /// **'Брак или просрочка'**
  String get reasonDefect;

  /// No description provided for @remove.
  ///
  /// In ru, this message translates to:
  /// **'Убрать'**
  String get remove;

  /// No description provided for @noDeletions.
  ///
  /// In ru, this message translates to:
  /// **'Удалений не было.'**
  String get noDeletions;

  /// No description provided for @loading.
  ///
  /// In ru, this message translates to:
  /// **'Загрузка…'**
  String get loading;

  /// No description provided for @empty.
  ///
  /// In ru, this message translates to:
  /// **'Здесь пусто.'**
  String get empty;

  /// No description provided for @share.
  ///
  /// In ru, this message translates to:
  /// **'Поделиться'**
  String get share;

  /// No description provided for @documentPdf.
  ///
  /// In ru, this message translates to:
  /// **'Накладная, товарный чек, счёт'**
  String get documentPdf;

  /// No description provided for @docSalesReceipt.
  ///
  /// In ru, this message translates to:
  /// **'Товарный чек'**
  String get docSalesReceipt;

  /// No description provided for @docWaybill.
  ///
  /// In ru, this message translates to:
  /// **'Накладная'**
  String get docWaybill;

  /// No description provided for @docInvoice.
  ///
  /// In ru, this message translates to:
  /// **'Счёт на оплату'**
  String get docInvoice;

  /// No description provided for @staff.
  ///
  /// In ru, this message translates to:
  /// **'Сотрудники и права'**
  String get staff;

  /// No description provided for @rightSale.
  ///
  /// In ru, this message translates to:
  /// **'Продажа и оплата'**
  String get rightSale;

  /// No description provided for @rightReturn.
  ///
  /// In ru, this message translates to:
  /// **'Возврат и обмен'**
  String get rightReturn;

  /// No description provided for @rightDiscount.
  ///
  /// In ru, this message translates to:
  /// **'Скидки'**
  String get rightDiscount;

  /// No description provided for @rightEditPrice.
  ///
  /// In ru, this message translates to:
  /// **'Менять цену в чеке'**
  String get rightEditPrice;

  /// No description provided for @rightDeleteLine.
  ///
  /// In ru, this message translates to:
  /// **'Удалять из чека'**
  String get rightDeleteLine;

  /// No description provided for @rightStock.
  ///
  /// In ru, this message translates to:
  /// **'Склад, приёмка и ревизия'**
  String get rightStock;

  /// No description provided for @rightReports.
  ///
  /// In ru, this message translates to:
  /// **'Отчёты и аналитика'**
  String get rightReports;

  /// No description provided for @rightShifts.
  ///
  /// In ru, this message translates to:
  /// **'Смены'**
  String get rightShifts;

  /// No description provided for @rightClients.
  ///
  /// In ru, this message translates to:
  /// **'Клиенты'**
  String get rightClients;

  /// No description provided for @rightSettings.
  ///
  /// In ru, this message translates to:
  /// **'Настройки'**
  String get rightSettings;

  /// No description provided for @rightsSaved.
  ///
  /// In ru, this message translates to:
  /// **'Права сохранены.'**
  String get rightsSaved;

  /// No description provided for @notifications.
  ///
  /// In ru, this message translates to:
  /// **'Уведомления'**
  String get notifications;

  /// No description provided for @notifyNewOrder.
  ///
  /// In ru, this message translates to:
  /// **'Новый заказ с сайта'**
  String get notifyNewOrder;

  /// No description provided for @notifyRunningOut.
  ///
  /// In ru, this message translates to:
  /// **'Заканчиваются товары'**
  String get notifyRunningOut;

  /// No description provided for @notifyRunningOutSub.
  ///
  /// In ru, this message translates to:
  /// **'{n} позиций · пора заказать'**
  String notifyRunningOutSub(int n);

  /// No description provided for @notifyBigDebt.
  ///
  /// In ru, this message translates to:
  /// **'Большой долг клиента'**
  String get notifyBigDebt;

  /// No description provided for @notifyNotSent.
  ///
  /// In ru, this message translates to:
  /// **'Не отправлены на сервер'**
  String get notifyNotSent;

  /// No description provided for @noNotifications.
  ///
  /// In ru, this message translates to:
  /// **'Новых уведомлений нет.'**
  String get noNotifications;

  /// No description provided for @newProductSub.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод, название, цены'**
  String get newProductSub;

  /// No description provided for @findByBarcode.
  ///
  /// In ru, this message translates to:
  /// **'Найти по штрихкоду'**
  String get findByBarcode;

  /// No description provided for @alreadyInStock.
  ///
  /// In ru, this message translates to:
  /// **'Такой товар уже есть: {name}'**
  String alreadyInStock(String name);

  /// No description provided for @productCreated.
  ///
  /// In ru, this message translates to:
  /// **'Товар создан.'**
  String get productCreated;

  /// No description provided for @quantityLabel.
  ///
  /// In ru, this message translates to:
  /// **'Количество'**
  String get quantityLabel;

  /// No description provided for @barcodeLabel.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод'**
  String get barcodeLabel;

  /// No description provided for @retail.
  ///
  /// In ru, this message translates to:
  /// **'Розница'**
  String get retail;

  /// No description provided for @wholesale.
  ///
  /// In ru, this message translates to:
  /// **'Опт'**
  String get wholesale;

  /// No description provided for @wholesaleOn.
  ///
  /// In ru, this message translates to:
  /// **'Оптовая продажа: товары с оптовой ценой — по опту.'**
  String get wholesaleOn;

  /// No description provided for @wholesaleNone.
  ///
  /// In ru, this message translates to:
  /// **'У товаров этого чека нет оптовой цены — задайте её в «Товар — цена и остаток».'**
  String get wholesaleNone;

  /// No description provided for @wholesaleTag.
  ///
  /// In ru, this message translates to:
  /// **'опт'**
  String get wholesaleTag;

  /// No description provided for @cashJournal.
  ///
  /// In ru, this message translates to:
  /// **'Внесения и изъятия за смену'**
  String get cashJournal;

  /// No description provided for @noCashOps.
  ///
  /// In ru, this message translates to:
  /// **'Внесений и изъятий не было.'**
  String get noCashOps;

  /// No description provided for @themeLight.
  ///
  /// In ru, this message translates to:
  /// **'Светлая'**
  String get themeLight;

  /// No description provided for @themeDark.
  ///
  /// In ru, this message translates to:
  /// **'Тёмная'**
  String get themeDark;

  /// No description provided for @themeSystem.
  ///
  /// In ru, this message translates to:
  /// **'Как в системе'**
  String get themeSystem;

  /// No description provided for @appearance.
  ///
  /// In ru, this message translates to:
  /// **'Оформление'**
  String get appearance;

  /// No description provided for @searchAll.
  ///
  /// In ru, this message translates to:
  /// **'Товар, клиент или раздел'**
  String get searchAll;

  /// No description provided for @sectionsTitle.
  ///
  /// In ru, this message translates to:
  /// **'Разделы'**
  String get sectionsTitle;

  /// No description provided for @productsTitle.
  ///
  /// In ru, this message translates to:
  /// **'Товары'**
  String get productsTitle;

  /// No description provided for @clientsTitle.
  ///
  /// In ru, this message translates to:
  /// **'Клиенты'**
  String get clientsTitle;

  /// No description provided for @openAction.
  ///
  /// In ru, this message translates to:
  /// **'Открыть'**
  String get openAction;

  /// No description provided for @returnAction.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть'**
  String get returnAction;

  /// No description provided for @addedToCheckName.
  ///
  /// In ru, this message translates to:
  /// **'Добавлено в чек: {name}'**
  String addedToCheckName(String name);

  /// No description provided for @receiveWithPrices.
  ///
  /// In ru, this message translates to:
  /// **'Приёмка с ценами'**
  String get receiveWithPrices;

  /// No description provided for @receiveWithPricesSub.
  ///
  /// In ru, this message translates to:
  /// **'Поставщик, закупка, продажа, новые товары'**
  String get receiveWithPricesSub;

  /// No description provided for @noSupplier.
  ///
  /// In ru, this message translates to:
  /// **'Без поставщика'**
  String get noSupplier;

  /// No description provided for @newProduct.
  ///
  /// In ru, this message translates to:
  /// **'Новый товар'**
  String get newProduct;

  /// No description provided for @barcodeNotInStock.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} не найден на складе'**
  String barcodeNotInStock(String code);

  /// No description provided for @foundInBase.
  ///
  /// In ru, this message translates to:
  /// **'Найдено в общей базе по штрихкоду — проверьте'**
  String get foundInBase;

  /// No description provided for @unitLabel.
  ///
  /// In ru, this message translates to:
  /// **'Единица'**
  String get unitLabel;

  /// No description provided for @createAndAdd.
  ///
  /// In ru, this message translates to:
  /// **'Создать и добавить'**
  String get createAndAdd;

  /// No description provided for @markupRule.
  ///
  /// In ru, this message translates to:
  /// **'Цены продажи: +{pct} % к закупке, до 5 сом'**
  String markupRule(String pct);

  /// No description provided for @payNowCash.
  ///
  /// In ru, this message translates to:
  /// **'Оплачено сразу'**
  String get payNowCash;

  /// No description provided for @onCredit.
  ///
  /// In ru, this message translates to:
  /// **'В долг поставщику'**
  String get onCredit;

  /// No description provided for @purchaseSum.
  ///
  /// In ru, this message translates to:
  /// **'Закупка {sum} сом'**
  String purchaseSum(String sum);

  /// No description provided for @postReceipt.
  ///
  /// In ru, this message translates to:
  /// **'Провести приход'**
  String get postReceipt;

  /// No description provided for @receiptPosted.
  ///
  /// In ru, this message translates to:
  /// **'Приход проведён: {n} поз.'**
  String receiptPosted(int n);

  /// No description provided for @partlyFailed.
  ///
  /// In ru, this message translates to:
  /// **'Не прошло: {text}'**
  String partlyFailed(String text);

  /// No description provided for @shiftsOpen.
  ///
  /// In ru, this message translates to:
  /// **'Открытые'**
  String get shiftsOpen;

  /// No description provided for @shiftsClosed.
  ///
  /// In ru, this message translates to:
  /// **'Закрытые'**
  String get shiftsClosed;

  /// No description provided for @debtAccessTitle.
  ///
  /// In ru, this message translates to:
  /// **'Доступ к долгу'**
  String get debtAccessTitle;

  /// No description provided for @debtAccessHint.
  ///
  /// In ru, this message translates to:
  /// **'Кому из сотрудников можно продавать в долг: можно, нельзя или с лимитом — сколько чеков и на какую сумму в день. Правила общие для всех касс и телефонов компании. Владелец и администратор ограничений не имеют.'**
  String get debtAccessHint;

  /// No description provided for @debtAllStaff.
  ///
  /// In ru, this message translates to:
  /// **'Все сотрудники'**
  String get debtAllStaff;

  /// No description provided for @debtSameAsAll.
  ///
  /// In ru, this message translates to:
  /// **'Как у всех'**
  String get debtSameAsAll;

  /// No description provided for @debtAllow.
  ///
  /// In ru, this message translates to:
  /// **'Можно'**
  String get debtAllow;

  /// No description provided for @debtDeny.
  ///
  /// In ru, this message translates to:
  /// **'Нельзя'**
  String get debtDeny;

  /// No description provided for @debtLimit.
  ///
  /// In ru, this message translates to:
  /// **'С лимитом'**
  String get debtLimit;

  /// No description provided for @debtReceiptsPerDay.
  ///
  /// In ru, this message translates to:
  /// **'Чеков в долг в день'**
  String get debtReceiptsPerDay;

  /// No description provided for @debtSumPerDay.
  ///
  /// In ru, this message translates to:
  /// **'Сумма долга в день, сом'**
  String get debtSumPerDay;

  /// No description provided for @debtNoLimit.
  ///
  /// In ru, this message translates to:
  /// **'без огр.'**
  String get debtNoLimit;

  /// No description provided for @debtAccessSaved.
  ///
  /// In ru, this message translates to:
  /// **'Сохранено — правила уже действуют на всех кассах и телефонах.'**
  String get debtAccessSaved;

  /// No description provided for @debtStaffNotLoaded.
  ///
  /// In ru, this message translates to:
  /// **'Список сотрудников не получен (нет связи?) — можно задать правило для всех.'**
  String get debtStaffNotLoaded;

  /// No description provided for @debtDenied.
  ///
  /// In ru, this message translates to:
  /// **'Владелец запретил вам продажу в долг. Выберите другой способ оплаты или позовите владельца.'**
  String get debtDenied;

  /// No description provided for @debtLimitReceipts.
  ///
  /// In ru, this message translates to:
  /// **'Лимит продаж в долг на сегодня исчерпан: {a} из {b} чек. Обратитесь к владельцу.'**
  String debtLimitReceipts(int a, int b);

  /// No description provided for @debtLimitSum.
  ///
  /// In ru, this message translates to:
  /// **'Превышен лимит долга на сегодня: можно ещё {left} сом из {max} сом, а в долг {sum} сом. Возьмите часть оплаты сейчас или позовите владельца.'**
  String debtLimitSum(String left, String max, String sum);

  /// No description provided for @debtReceiptsOf.
  ///
  /// In ru, this message translates to:
  /// **'чеков {a} из {b}'**
  String debtReceiptsOf(int a, int b);

  /// No description provided for @debtSumOf.
  ///
  /// In ru, this message translates to:
  /// **'долг {used} из {max} сом'**
  String debtSumOf(String used, String max);

  /// No description provided for @debtYourLimit.
  ///
  /// In ru, this message translates to:
  /// **'Ваш лимит в долг на сегодня: {text}'**
  String debtYourLimit(String text);

  /// No description provided for @promoTag.
  ///
  /// In ru, this message translates to:
  /// **'акция'**
  String get promoTag;

  /// No description provided for @promoNoManualDiscount.
  ///
  /// In ru, this message translates to:
  /// **'На товар действует акция — скидку по ней считает сервер, ручную скидку на эту строку дать нельзя.'**
  String get promoNoManualDiscount;

  /// No description provided for @quoteSearch.
  ///
  /// In ru, this message translates to:
  /// **'Номер, клиент или телефон'**
  String get quoteSearch;

  /// No description provided for @quotesEmpty.
  ///
  /// In ru, this message translates to:
  /// **'Пока нет КП и счетов'**
  String get quotesEmpty;

  /// No description provided for @quotesEmptyHint.
  ///
  /// In ru, this message translates to:
  /// **'Нажмите «Создать»: выберите клиента и товары — PDF отправится в WhatsApp или на почту.'**
  String get quotesEmptyHint;

  /// No description provided for @quotesNoMatch.
  ///
  /// In ru, this message translates to:
  /// **'Ничего не найдено — измените поиск или фильтр.'**
  String get quotesNoMatch;

  /// No description provided for @createDoc.
  ///
  /// In ru, this message translates to:
  /// **'Создать'**
  String get createDoc;

  /// No description provided for @makeInvoice.
  ///
  /// In ru, this message translates to:
  /// **'Сделать счёт'**
  String get makeInvoice;

  /// No description provided for @sendToClient.
  ///
  /// In ru, this message translates to:
  /// **'Отправить'**
  String get sendToClient;

  /// No description provided for @docWaText.
  ///
  /// In ru, this message translates to:
  /// **'{doc} № {number} на сумму {sum} сом. {until}.'**
  String docWaText(String doc, String number, String sum, String until);

  /// No description provided for @docSubtotal.
  ///
  /// In ru, this message translates to:
  /// **'Сумма без скидки'**
  String get docSubtotal;

  /// No description provided for @discountShort.
  ///
  /// In ru, this message translates to:
  /// **'Скидка'**
  String get discountShort;

  /// No description provided for @paidLabel.
  ///
  /// In ru, this message translates to:
  /// **'Оплачено'**
  String get paidLabel;

  /// No description provided for @leftToPay.
  ///
  /// In ru, this message translates to:
  /// **'Осталось оплатить'**
  String get leftToPay;

  /// No description provided for @findClient.
  ///
  /// In ru, this message translates to:
  /// **'Найти клиента'**
  String get findClient;

  /// No description provided for @companyLabel.
  ///
  /// In ru, this message translates to:
  /// **'Компания (ОсОО, ИП)'**
  String get companyLabel;

  /// No description provided for @clientExists.
  ///
  /// In ru, this message translates to:
  /// **'Клиент с этим телефоном уже есть в NurCRM — выбран он.'**
  String get clientExists;

  /// No description provided for @clientSaved.
  ///
  /// In ru, this message translates to:
  /// **'Клиент сохранён в NurCRM'**
  String get clientSaved;

  /// No description provided for @clientLocalOnly.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи с NurCRM — клиент записан только в этот документ.'**
  String get clientLocalOnly;

  /// No description provided for @clientNameRequired.
  ///
  /// In ru, this message translates to:
  /// **'Введите имя или компанию'**
  String get clientNameRequired;

  /// No description provided for @innDigits.
  ///
  /// In ru, this message translates to:
  /// **'ИНН — только цифры, обычно 14'**
  String get innDigits;

  /// No description provided for @discountHint.
  ///
  /// In ru, this message translates to:
  /// **'Сумма или процент, например 10%'**
  String get discountHint;

  /// No description provided for @stExpired.
  ///
  /// In ru, this message translates to:
  /// **'Срок истёк'**
  String get stExpired;

  /// No description provided for @deleteDocAsk.
  ///
  /// In ru, this message translates to:
  /// **'Удалить {number}?'**
  String deleteDocAsk(String number);

  /// No description provided for @deleteDocAskSub.
  ///
  /// In ru, this message translates to:
  /// **'Документ удалится и в программе владельца, без возврата.'**
  String get deleteDocAskSub;

  /// No description provided for @requisitesTitle.
  ///
  /// In ru, this message translates to:
  /// **'Реквизиты для счёта'**
  String get requisitesTitle;

  /// No description provided for @requisitesHint.
  ///
  /// In ru, this message translates to:
  /// **'Банк, расчётный счёт, БИК, телефон — печатаются в PDF под названием магазина.'**
  String get requisitesHint;

  /// No description provided for @scannedAdded.
  ///
  /// In ru, this message translates to:
  /// **'Добавлено: {name}'**
  String scannedAdded(String name);

  /// No description provided for @invoiceCreated.
  ///
  /// In ru, this message translates to:
  /// **'Создан счёт {number}'**
  String invoiceCreated(String number);

  /// No description provided for @historyLabel.
  ///
  /// In ru, this message translates to:
  /// **'История'**
  String get historyLabel;

  /// No description provided for @docLines.
  ///
  /// In ru, this message translates to:
  /// **'Товары и услуги'**
  String get docLines;

  /// No description provided for @docFrom.
  ///
  /// In ru, this message translates to:
  /// **'от {date}'**
  String docFrom(String date);

  /// No description provided for @clientNotChosen.
  ///
  /// In ru, this message translates to:
  /// **'Клиент не выбран'**
  String get clientNotChosen;

  /// No description provided for @linesEmptyHint.
  ///
  /// In ru, this message translates to:
  /// **'Добавьте товары: поиск, сканер или из текущего чека.'**
  String get linesEmptyHint;

  /// No description provided for @quotesSyncing.
  ///
  /// In ru, this message translates to:
  /// **'Обновление общего списка…'**
  String get quotesSyncing;

  /// No description provided for @quotesShared.
  ///
  /// In ru, this message translates to:
  /// **'Общий список с программой владельца'**
  String get quotesShared;

  /// No description provided for @quotesSharedAt.
  ///
  /// In ru, this message translates to:
  /// **'Общий список обновлён: {time}'**
  String quotesSharedAt(String time);

  /// No description provided for @quotesOffline.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи с сервером — показана копия телефона'**
  String get quotesOffline;

  /// No description provided for @quotesNotSent.
  ///
  /// In ru, this message translates to:
  /// **'Не отправлено: {n}. Уйдёт в программу владельца при связи — нажмите, чтобы повторить.'**
  String quotesNotSent(int n);

  /// No description provided for @quotesForbidden.
  ///
  /// In ru, this message translates to:
  /// **'Нет прав на общие настройки компании — изменения только на этом телефоне. Нужен вход владельца или администратора.'**
  String get quotesForbidden;

  /// No description provided for @notSentMark.
  ///
  /// In ru, this message translates to:
  /// **'не отправлено'**
  String get notSentMark;

  /// No description provided for @quoteChangedElsewhere.
  ///
  /// In ru, this message translates to:
  /// **'{number} позже изменили в другом месте — показана последняя версия'**
  String quoteChangedElsewhere(String number);

  /// No description provided for @quoteRenumbered.
  ///
  /// In ru, this message translates to:
  /// **'Номер {from} уже занят — документ теперь {to}'**
  String quoteRenumbered(String from, String to);

  /// No description provided for @payOutcomeUnknown.
  ///
  /// In ru, this message translates to:
  /// **'Сервер не ответил — продажа могла пройти. Нажмите «Оплатить» ещё раз, ничего не меняя: повтор второй раз не проведётся. Без связи долг и списание бонусов не оформляются.'**
  String get payOutcomeUnknown;

  /// No description provided for @debtDueLabel.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть до'**
  String get debtDueLabel;

  /// No description provided for @debtDueDays.
  ///
  /// In ru, this message translates to:
  /// **'{n} дн.'**
  String debtDueDays(int n);

  /// No description provided for @debtDueNotSaved.
  ///
  /// In ru, this message translates to:
  /// **'Срок долга не записан на сервере — поправьте его на сайте (по умолчанию 30 дней).'**
  String get debtDueNotSaved;

  /// No description provided for @returnAmount.
  ///
  /// In ru, this message translates to:
  /// **'Сумма возврата: {sum} сом'**
  String returnAmount(String sum);

  /// No description provided for @returnGive.
  ///
  /// In ru, this message translates to:
  /// **'Выдать покупателю: {what}'**
  String returnGive(String what);

  /// No description provided for @returnInCash.
  ///
  /// In ru, this message translates to:
  /// **'наличными {sum} сом'**
  String returnInCash(String sum);

  /// No description provided for @returnToCard.
  ///
  /// In ru, this message translates to:
  /// **'на карту/перевод {sum} сом'**
  String returnToCard(String sum);

  /// No description provided for @returnDebtReduced.
  ///
  /// In ru, this message translates to:
  /// **'Долг клиента уменьшен на {sum} сом — эти деньги не выдаются.'**
  String returnDebtReduced(String sum);

  /// No description provided for @returnDebtUnknown.
  ///
  /// In ru, this message translates to:
  /// **'Внимание: остаток долга клиента с сервера не получен. По расчёту долг уменьшен на {sum} сом, но если клиент уже погасил долг по этому чеку — ему нужно вернуть до {sum} сом. Проверьте долг клиента перед выдачей.'**
  String returnDebtUnknown(String sum);

  /// No description provided for @returnViaBank.
  ///
  /// In ru, this message translates to:
  /// **'Часть на карту/перевод — вернуть через банк, не из кассы.'**
  String get returnViaBank;

  /// No description provided for @returnPaidInfo.
  ///
  /// In ru, this message translates to:
  /// **'Оплачено по чеку: {paid} сом · уже возвращено: {done} сом'**
  String returnPaidInfo(String paid, String done);

  /// No description provided for @returnedQty.
  ///
  /// In ru, this message translates to:
  /// **'уже возвращено: {qty}'**
  String returnedQty(String qty);

  /// No description provided for @returnNothingLeft.
  ///
  /// In ru, this message translates to:
  /// **'По этому чеку всё уже возвращено.'**
  String get returnNothingLeft;

  /// No description provided for @cashOutTooMuch.
  ///
  /// In ru, this message translates to:
  /// **'Изъятие больше, чем в кассе ({sum} сом).'**
  String cashOutTooMuch(String sum);

  /// No description provided for @payDebtToDebt.
  ///
  /// In ru, this message translates to:
  /// **'В счёт долга: {sum} сом'**
  String payDebtToDebt(String sum);

  /// No description provided for @payDebtOverCard.
  ///
  /// In ru, this message translates to:
  /// **'Переводом — не больше долга.'**
  String get payDebtOverCard;

  /// No description provided for @receiveNotAccepted.
  ///
  /// In ru, this message translates to:
  /// **'Приход не принят сервером: {reason}. Запись — в «Синхронизации».'**
  String receiveNotAccepted(String reason);

  /// No description provided for @auditApply.
  ///
  /// In ru, this message translates to:
  /// **'Провести ревизию'**
  String get auditApply;

  /// No description provided for @auditApplyHint.
  ///
  /// In ru, this message translates to:
  /// **'Остатки станут равны пересчитанным — как ревизия на кассе ПК. Нужна связь.'**
  String get auditApplyHint;

  /// No description provided for @auditApplyAsk.
  ///
  /// In ru, this message translates to:
  /// **'Проверено товаров: {n}, расхождений: {a}. Остатки станут равны пересчитанным. Провести?'**
  String auditApplyAsk(int n, int a);

  /// No description provided for @auditApplied.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия проведена — остатки обновлены.'**
  String get auditApplied;

  /// No description provided for @auditApplyQueued.
  ///
  /// In ru, this message translates to:
  /// **'Акт ревизии создан — проведение уйдёт при связи.'**
  String get auditApplyQueued;

  /// No description provided for @auditOnlineOnly.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия проводится только при связи с сервером — пересчёт сохранён, повторите позже.'**
  String get auditOnlineOnly;

  /// No description provided for @auditContinue.
  ///
  /// In ru, this message translates to:
  /// **'Продолжить пересчёт ({n} товаров)'**
  String auditContinue(int n);

  /// No description provided for @countedCash.
  ///
  /// In ru, this message translates to:
  /// **'Пересчитано'**
  String get countedCash;

  /// No description provided for @openingCashLabel.
  ///
  /// In ru, this message translates to:
  /// **'На начало смены'**
  String get openingCashLabel;

  /// No description provided for @closeShiftFailed.
  ///
  /// In ru, this message translates to:
  /// **'Сервер не принял {n} чеков — они не попадут в отчёт смены. Отправьте их снова или снимите в «Синхронизации».'**
  String closeShiftFailed(int n);

  /// No description provided for @closeShiftAsk.
  ///
  /// In ru, this message translates to:
  /// **'Закрыть смену?'**
  String get closeShiftAsk;

  /// No description provided for @closeShiftAskSub.
  ///
  /// In ru, this message translates to:
  /// **'После закрытия продавать в эту смену нельзя.'**
  String get closeShiftAskSub;

  /// No description provided for @xReport.
  ///
  /// In ru, this message translates to:
  /// **'X-отчёт'**
  String get xReport;

  /// No description provided for @foreignShiftHint.
  ///
  /// In ru, this message translates to:
  /// **'На этой кассе открыта смена другого кассира — продавать в неё нельзя. Откройте свою смену.'**
  String get foreignShiftHint;

  /// No description provided for @loadMore.
  ///
  /// In ru, this message translates to:
  /// **'Ещё'**
  String get loadMore;

  /// No description provided for @dropRow.
  ///
  /// In ru, this message translates to:
  /// **'Снять'**
  String get dropRow;

  /// No description provided for @dropRowAsk.
  ///
  /// In ru, this message translates to:
  /// **'Снять чек из очереди? Он не уйдёт на сервер, товар вернётся в остаток телефона. Если деньги взяты — верните их покупателю или пробейте чек заново.'**
  String get dropRowAsk;

  /// No description provided for @dropRowAskStock.
  ///
  /// In ru, this message translates to:
  /// **'Снять запись из очереди? Она не уйдёт на сервер — проверьте остатки товара вручную.'**
  String get dropRowAskStock;

  /// No description provided for @otherAccount.
  ///
  /// In ru, this message translates to:
  /// **'другой аккаунт'**
  String get otherAccount;

  /// No description provided for @otherAccountHint.
  ///
  /// In ru, this message translates to:
  /// **'Записано под входом {email} — уйдёт после входа этим логином.'**
  String otherAccountHint(String email);

  /// No description provided for @pAStockUnclear.
  ///
  /// In ru, this message translates to:
  /// **'{name}: прошлая попытка осталась без ответа сервера, а остаток с тех пор изменился (сейчас {now}, до прибавки было {before}). Повторно НЕ прибавлено, чтобы не задвоить. Проверьте остаток ревизией: если прибавилось — удалите строку, если нет — удалите её и добавьте товар заново.'**
  String pAStockUnclear(String name, String now, String before);

  /// No description provided for @pAWeightLabel.
  ///
  /// In ru, this message translates to:
  /// **'Это этикетка весов (вес одной покупки) — в приёмку её не берём. Отсканируйте штрихкод товара или найдите его по названию.'**
  String get pAWeightLabel;

  /// No description provided for @pANoServerCheck.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи с сервером — не проверить, есть ли уже товар с этим штрихкодом. Повторите, когда появится связь.'**
  String get pANoServerCheck;

  /// No description provided for @pALinesLeft.
  ///
  /// In ru, this message translates to:
  /// **'Не принято строк: {n} — они остались в списке, поправьте и проведите снова.'**
  String pALinesLeft(int n);

  /// No description provided for @pAPluLabel.
  ///
  /// In ru, this message translates to:
  /// **'PLU для весов'**
  String get pAPluLabel;

  /// No description provided for @pAPluHint.
  ///
  /// In ru, this message translates to:
  /// **'пусто — назначится сам'**
  String get pAPluHint;

  /// No description provided for @pAStockAdd.
  ///
  /// In ru, this message translates to:
  /// **'Прибавить к остатку'**
  String get pAStockAdd;

  /// No description provided for @pAStockSet.
  ///
  /// In ru, this message translates to:
  /// **'Задать остаток'**
  String get pAStockSet;

  /// No description provided for @pAStockAddHint.
  ///
  /// In ru, this message translates to:
  /// **'Количество из файла прибавится к остатку, как приход.'**
  String get pAStockAddHint;

  /// No description provided for @pAStockSetHint.
  ///
  /// In ru, this message translates to:
  /// **'Остаток станет равен количеству из файла (замена, не приход).'**
  String get pAStockSetHint;

  /// No description provided for @pADupRow.
  ///
  /// In ru, this message translates to:
  /// **'Повтор строки {n} — пропускается'**
  String pADupRow(int n);

  /// No description provided for @pAUploaded.
  ///
  /// In ru, this message translates to:
  /// **'загружено'**
  String get pAUploaded;

  /// No description provided for @rightCashier.
  ///
  /// In ru, this message translates to:
  /// **'Интерфейс кассира'**
  String get rightCashier;

  /// No description provided for @rightOrders.
  ///
  /// In ru, this message translates to:
  /// **'Заказы с сайта'**
  String get rightOrders;

  /// No description provided for @rightProcurement.
  ///
  /// In ru, this message translates to:
  /// **'Закупки'**
  String get rightProcurement;

  /// No description provided for @rightSuppliers.
  ///
  /// In ru, this message translates to:
  /// **'Поставщики'**
  String get rightSuppliers;

  /// No description provided for @rightSelfLocked.
  ///
  /// In ru, this message translates to:
  /// **'Свои права выключить нельзя — это делает владелец или другой администратор.'**
  String get rightSelfLocked;

  /// No description provided for @staffYou.
  ///
  /// In ru, this message translates to:
  /// **'это вы'**
  String get staffYou;

  /// No description provided for @screenUnavailable.
  ///
  /// In ru, this message translates to:
  /// **'Экран открылся заново, и его данные не сохранились. Вернитесь назад и откройте его ещё раз.'**
  String get screenUnavailable;

  /// No description provided for @cancelOrder.
  ///
  /// In ru, this message translates to:
  /// **'Отменить заказ'**
  String get cancelOrder;

  /// No description provided for @cancelOrderAsk.
  ///
  /// In ru, this message translates to:
  /// **'Отменить заказ №{number}? Покупатель его не получит.'**
  String cancelOrderAsk(String number);

  /// No description provided for @pickShortAsk.
  ///
  /// In ru, this message translates to:
  /// **'Собрано {got} из {need}. Отметить заказ собранным с недобором?'**
  String pickShortAsk(String got, String need);

  /// No description provided for @pickupCodeLabel.
  ///
  /// In ru, this message translates to:
  /// **'Код выдачи: {code}'**
  String pickupCodeLabel(String code);

  /// No description provided for @torchToggle.
  ///
  /// In ru, this message translates to:
  /// **'Фонарик'**
  String get torchToggle;

  /// No description provided for @payTransferQr.
  ///
  /// In ru, this message translates to:
  /// **'Перевод / QR'**
  String get payTransferQr;

  /// No description provided for @reportLoadFailed.
  ///
  /// In ru, this message translates to:
  /// **'Отчёт не загрузился. Проверьте связь и нажмите «Повторить».'**
  String get reportLoadFailed;

  /// No description provided for @rentalLatePerDay.
  ///
  /// In ru, this message translates to:
  /// **'Пеня за сутки просрочки'**
  String get rentalLatePerDay;

  /// No description provided for @rentalLateHint.
  ///
  /// In ru, this message translates to:
  /// **'Просрочка {n} сут. × {rate} = {sum} сом (по условиям проката)'**
  String rentalLateHint(int n, String rate, String sum);

  /// No description provided for @rentalBackDeposit.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть клиенту залог: {sum} сом'**
  String rentalBackDeposit(String sum);

  /// No description provided for @rentalWithheld.
  ///
  /// In ru, this message translates to:
  /// **'удержано {sum} сом'**
  String rentalWithheld(String sum);

  /// No description provided for @rentalBackDocument.
  ///
  /// In ru, this message translates to:
  /// **'Вернуть клиенту документ: {doc}'**
  String rentalBackDocument(String doc);

  /// No description provided for @rentalExcess.
  ///
  /// In ru, this message translates to:
  /// **'Штраф сверх залога: {sum} сом — добавится в чек'**
  String rentalExcess(String sum);

  /// No description provided for @rentalPenaltyLine.
  ///
  /// In ru, this message translates to:
  /// **'Штраф по прокату №{n}'**
  String rentalPenaltyLine(int n);

  /// No description provided for @rentalPenaltyInCart.
  ///
  /// In ru, this message translates to:
  /// **'Возврат принят. Штраф {sum} сом добавлен в чек — проведите оплату.'**
  String rentalPenaltyInCart(String sum);

  /// No description provided for @waybillDoc.
  ///
  /// In ru, this message translates to:
  /// **'Накладная'**
  String get waybillDoc;

  /// No description provided for @waybillsFilter.
  ///
  /// In ru, this message translates to:
  /// **'Накладные'**
  String get waybillsFilter;

  /// No description provided for @docPayMarkOnly.
  ///
  /// In ru, this message translates to:
  /// **'Это только отметка в документе: деньги в кассу смены и долг клиента в NurCRM не записываются. Чтобы провести деньги через кассу, используйте «Провести продажу».'**
  String get docPayMarkOnly;

  /// No description provided for @docPayMarkOk.
  ///
  /// In ru, this message translates to:
  /// **'Отметить оплату'**
  String get docPayMarkOk;

  /// No description provided for @docSellCartBusy.
  ///
  /// In ru, this message translates to:
  /// **'В чеке уже есть товары. Заменить их товарами документа?'**
  String get docSellCartBusy;

  /// No description provided for @docSellReplace.
  ///
  /// In ru, this message translates to:
  /// **'Заменить'**
  String get docSellReplace;

  /// No description provided for @docSentToSale.
  ///
  /// In ru, this message translates to:
  /// **'Передан в продажу'**
  String get docSentToSale;

  /// No description provided for @docSellMissing.
  ///
  /// In ru, this message translates to:
  /// **'Нет в каталоге телефона — добавьте вручную: {names}'**
  String docSellMissing(String names);

  /// No description provided for @docVatIncl.
  ///
  /// In ru, this message translates to:
  /// **'В т.ч. НДС {p}%'**
  String docVatIncl(String p);

  /// No description provided for @prnQr.
  ///
  /// In ru, this message translates to:
  /// **'QR-код на чеке'**
  String get prnQr;

  /// No description provided for @prnQrHint.
  ///
  /// In ru, this message translates to:
  /// **'Проверка фискального чека или электронный чек. Если принтер печатает вместо QR лишние знаки — выключите.'**
  String get prnQrHint;

  /// No description provided for @prnDrawer.
  ///
  /// In ru, this message translates to:
  /// **'Денежный ящик через принтер'**
  String get prnDrawer;

  /// No description provided for @prnDrawerHint.
  ///
  /// In ru, this message translates to:
  /// **'Открывается после продажи за наличные (ящик подключён к принтеру кабелем).'**
  String get prnDrawerHint;

  /// No description provided for @prnDrawerPin.
  ///
  /// In ru, this message translates to:
  /// **'Контакт {n}'**
  String prnDrawerPin(int n);

  /// No description provided for @prnDrawerTest.
  ///
  /// In ru, this message translates to:
  /// **'Открыть ящик'**
  String get prnDrawerTest;

  /// No description provided for @prnDrawerSent.
  ///
  /// In ru, this message translates to:
  /// **'Команда ящику отправлена. Не открылся — выберите другой контакт.'**
  String get prnDrawerSent;

  /// No description provided for @stepPhoto.
  ///
  /// In ru, this message translates to:
  /// **'Фото'**
  String get stepPhoto;

  /// No description provided for @stepBarcode.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод'**
  String get stepBarcode;

  /// No description provided for @bindTitle.
  ///
  /// In ru, this message translates to:
  /// **'Фото сохранены ✓ — теперь отсканируйте штрихкод товара'**
  String get bindTitle;

  /// No description provided for @bindSub.
  ///
  /// In ru, this message translates to:
  /// **'Если на товаре нет штрихкода — нажмите «Пропустить»'**
  String get bindSub;

  /// No description provided for @bindSkip.
  ///
  /// In ru, this message translates to:
  /// **'Пропустить'**
  String get bindSkip;

  /// No description provided for @bindManual.
  ///
  /// In ru, this message translates to:
  /// **'Ввести вручную'**
  String get bindManual;

  /// No description provided for @bindManualHint.
  ///
  /// In ru, this message translates to:
  /// **'Цифры под штрихкодом'**
  String get bindManualHint;

  /// No description provided for @bindChecking.
  ///
  /// In ru, this message translates to:
  /// **'Проверяю штрихкод…'**
  String get bindChecking;

  /// No description provided for @bindWillLink.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} будет привязан к «{name}»'**
  String bindWillLink(String code, String name);

  /// No description provided for @bindWillLinkNew.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} будет привязан к новому товару'**
  String bindWillLinkNew(String code);

  /// No description provided for @bindAlreadyOwn.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} уже привязан к «{name}» — менять ничего не нужно'**
  String bindAlreadyOwn(String code, String name);

  /// No description provided for @bindTakenTitle.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод уже у другого товара'**
  String get bindTakenTitle;

  /// No description provided for @bindTakenAsk.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} уже привязан к «{name}».\n«Заменить» — штрихкод перейдёт к этому товару.\n«Оставить» — останется у «{name}», этот товар будет без штрихкода.'**
  String bindTakenAsk(String code, String name);

  /// No description provided for @bindTakenAskNew.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} уже привязан к «{name}» — возможно, этот товар уже есть на складе.\n«Оставить» — штрихкод останется у «{name}», новый товар будет без штрихкода.'**
  String bindTakenAskNew(String code, String name);

  /// No description provided for @bindReplace.
  ///
  /// In ru, this message translates to:
  /// **'Заменить'**
  String get bindReplace;

  /// No description provided for @bindKeep.
  ///
  /// In ru, this message translates to:
  /// **'Оставить'**
  String get bindKeep;

  /// No description provided for @bindRescan.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать заново'**
  String get bindRescan;

  /// No description provided for @bindLinked.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} привязан к «{name}»'**
  String bindLinked(String code, String name);

  /// No description provided for @teachNext.
  ///
  /// In ru, this message translates to:
  /// **'Снимите товар с 3–5 сторон, затем «Готово» — следующим шагом штрихкод'**
  String get teachNext;

  /// No description provided for @teachNextNoCode.
  ///
  /// In ru, this message translates to:
  /// **'Снимите товар с 3–5 сторон, затем нажмите «Готово»'**
  String get teachNextNoCode;

  /// No description provided for @liveNextSale.
  ///
  /// In ru, this message translates to:
  /// **'Найденные товары появятся ниже — нажмите товар, и он ляжет в чек'**
  String get liveNextSale;

  /// No description provided for @liveNextList.
  ///
  /// In ru, this message translates to:
  /// **'После снимка камера покажет список найденного — его можно поправить'**
  String get liveNextList;

  /// No description provided for @scanCodesHint.
  ///
  /// In ru, this message translates to:
  /// **'Наведите рамку на штрихкод — код запишется сам. Затем нажмите «Готово»'**
  String get scanCodesHint;

  /// No description provided for @weighNext.
  ///
  /// In ru, this message translates to:
  /// **'Камера узнает товар, прочитает вес и добавит в чек'**
  String get weighNext;

  /// No description provided for @weighManualBtn.
  ///
  /// In ru, this message translates to:
  /// **'Без камеры — выбрать товар и ввести вес'**
  String get weighManualBtn;

  /// No description provided for @peEdit.
  ///
  /// In ru, this message translates to:
  /// **'Редактировать товар'**
  String get peEdit;

  /// No description provided for @peName.
  ///
  /// In ru, this message translates to:
  /// **'Название'**
  String get peName;

  /// No description provided for @peMinQty.
  ///
  /// In ru, this message translates to:
  /// **'Минимальный остаток'**
  String get peMinQty;

  /// No description provided for @peMinQtyHint.
  ///
  /// In ru, this message translates to:
  /// **'Меньше — товар попадёт в «Заканчивается»'**
  String get peMinQtyHint;

  /// No description provided for @peStock.
  ///
  /// In ru, this message translates to:
  /// **'Остаток'**
  String get peStock;

  /// No description provided for @peStockHint.
  ///
  /// In ru, this message translates to:
  /// **'Остаток меняется только документом — приходом, списанием или ревизией с причиной, как на ПК. Так не бывает расхождений.'**
  String get peStockHint;

  /// No description provided for @peIncome.
  ///
  /// In ru, this message translates to:
  /// **'Приход'**
  String get peIncome;

  /// No description provided for @peWriteOff.
  ///
  /// In ru, this message translates to:
  /// **'Списание'**
  String get peWriteOff;

  /// No description provided for @peRecount.
  ///
  /// In ru, this message translates to:
  /// **'Ревизия'**
  String get peRecount;

  /// No description provided for @peQtyIn.
  ///
  /// In ru, this message translates to:
  /// **'Сколько пришло'**
  String get peQtyIn;

  /// No description provided for @peQtyOut.
  ///
  /// In ru, this message translates to:
  /// **'Сколько списать'**
  String get peQtyOut;

  /// No description provided for @peQtyFact.
  ///
  /// In ru, this message translates to:
  /// **'Сколько на самом деле'**
  String get peQtyFact;

  /// No description provided for @peReason.
  ///
  /// In ru, this message translates to:
  /// **'Причина'**
  String get peReason;

  /// No description provided for @peReasonNeed.
  ///
  /// In ru, this message translates to:
  /// **'Укажите причину.'**
  String get peReasonNeed;

  /// No description provided for @peReasonExpired.
  ///
  /// In ru, this message translates to:
  /// **'Просрочка'**
  String get peReasonExpired;

  /// No description provided for @peReasonDefect.
  ///
  /// In ru, this message translates to:
  /// **'Брак'**
  String get peReasonDefect;

  /// No description provided for @peReasonSpoiled.
  ///
  /// In ru, this message translates to:
  /// **'Порча'**
  String get peReasonSpoiled;

  /// No description provided for @peReasonTheft.
  ///
  /// In ru, this message translates to:
  /// **'Кража'**
  String get peReasonTheft;

  /// No description provided for @peReasonOther.
  ///
  /// In ru, this message translates to:
  /// **'Другое'**
  String get peReasonOther;

  /// No description provided for @peReasonSupplier.
  ///
  /// In ru, this message translates to:
  /// **'Поставка'**
  String get peReasonSupplier;

  /// No description provided for @peReasonCount.
  ///
  /// In ru, this message translates to:
  /// **'Пересчёт'**
  String get peReasonCount;

  /// No description provided for @peApply.
  ///
  /// In ru, this message translates to:
  /// **'Провести'**
  String get peApply;

  /// No description provided for @peStockDone.
  ///
  /// In ru, this message translates to:
  /// **'Остаток: {from} → {to}'**
  String peStockDone(String from, String to);

  /// No description provided for @peStockSame.
  ///
  /// In ru, this message translates to:
  /// **'Остаток уже {qty} — ничего не изменено.'**
  String peStockSame(String qty);

  /// No description provided for @peStockQueued.
  ///
  /// In ru, this message translates to:
  /// **'Документ создан — проведётся сам при связи.'**
  String get peStockQueued;

  /// No description provided for @peStockOffline.
  ///
  /// In ru, this message translates to:
  /// **'Нет связи с сервером — остаток не получен, ничего не изменено.'**
  String get peStockOffline;

  /// No description provided for @peStockBelowZero.
  ///
  /// In ru, this message translates to:
  /// **'Списать больше остатка нельзя (есть {qty}).'**
  String peStockBelowZero(String qty);

  /// No description provided for @peSaved.
  ///
  /// In ru, this message translates to:
  /// **'Товар сохранён.'**
  String get peSaved;

  /// No description provided for @peNoChanges.
  ///
  /// In ru, this message translates to:
  /// **'Изменений нет.'**
  String get peNoChanges;

  /// No description provided for @pePriceRestored.
  ///
  /// In ru, this message translates to:
  /// **'Сервер пересчитал цену продажи по наценке — введённая цена {price} возвращена.'**
  String pePriceRestored(String price);

  /// No description provided for @pePriceLocked.
  ///
  /// In ru, this message translates to:
  /// **'Цены меняет сотрудник с правом «Изменение цены».'**
  String get pePriceLocked;

  /// No description provided for @peLoadFailed.
  ///
  /// In ru, this message translates to:
  /// **'Карточка товара не загружена с сервера — править можно при связи.'**
  String get peLoadFailed;

  /// No description provided for @peNameNeed.
  ///
  /// In ru, this message translates to:
  /// **'Введите название.'**
  String get peNameNeed;

  /// No description provided for @peBarcodeTaken.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} уже у товара «{name}».'**
  String peBarcodeTaken(String code, String name);

  /// No description provided for @peBarcodeTwice.
  ///
  /// In ru, this message translates to:
  /// **'Штрихкод {code} указан дважды.'**
  String peBarcodeTwice(String code);

  /// No description provided for @peScan.
  ///
  /// In ru, this message translates to:
  /// **'Сканировать'**
  String get peScan;

  /// No description provided for @pePackQty.
  ///
  /// In ru, this message translates to:
  /// **'в упаковке {qty}'**
  String pePackQty(String qty);

  /// No description provided for @peNoAccess.
  ///
  /// In ru, this message translates to:
  /// **'Править товар может сотрудник с правом «Склад» или администратор.'**
  String get peNoAccess;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['en', 'ky', 'ru', 'tr', 'uz'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'en':
      return AppLocalizationsEn();
    case 'ky':
      return AppLocalizationsKy();
    case 'ru':
      return AppLocalizationsRu();
    case 'tr':
      return AppLocalizationsTr();
    case 'uz':
      return AppLocalizationsUz();
  }

  throw FlutterError(
    'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
    'an issue with the localizations generation tool. Please file an issue '
    'on GitHub with a reproducible sample app and the gen-l10n configuration '
    'that was used.',
  );
}
