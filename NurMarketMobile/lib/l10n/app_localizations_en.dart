// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String get som => 'som';

  @override
  String get cancel => 'Cancel';

  @override
  String get done => 'Done';

  @override
  String get save => 'Save';

  @override
  String get retry => 'Retry';

  @override
  String get all => 'All';

  @override
  String get loginTagline => 'Same sign-in as on the till and the web';

  @override
  String get loginLabel => 'Login or phone';

  @override
  String get loginHint => 'email or phone';

  @override
  String get passwordLabel => 'Password';

  @override
  String get signIn => 'Sign in';

  @override
  String get loginEmpty => 'Enter the login and password.';

  @override
  String get loginWrong => 'Wrong login or password.';

  @override
  String get chooseCashbox => 'Choose a till';

  @override
  String get cashbox => 'Till';

  @override
  String get tabHome => 'Home';

  @override
  String get tabSale => 'Sale';

  @override
  String get tabScan => 'Scanner';

  @override
  String get tabChecks => 'Receipts';

  @override
  String get tabStock => 'Stock';

  @override
  String get newCheck => 'New receipt';

  @override
  String get scanTileSub => 'Price and stock by barcode';

  @override
  String get checksTileSub => 'Send, print';

  @override
  String get stockTileSub => 'Stock and receiving';

  @override
  String shiftOpenSince(String time) {
    return 'Shift open · $time';
  }

  @override
  String get shiftClosed => 'Shift closed';

  @override
  String get mySalesForShift => 'My sales this shift';

  @override
  String checksCount(int n) {
    return '$n receipts';
  }

  @override
  String get openShift => 'Open shift';

  @override
  String get closeShift => 'Close shift';

  @override
  String get openingCashHint => 'Cash in the drawer at start';

  @override
  String get openShiftFirst => 'Open the shift on the home screen first.';

  @override
  String get offlineWorking =>
      'No connection — sales are saved on the phone and will be sent automatically.';

  @override
  String runningOut(int n) {
    return '$n products running out';
  }

  @override
  String get runningOutShort => 'Running out';

  @override
  String get outOfStock => 'Out of stock';

  @override
  String get tellAdmin => 'Tell the administrator';

  @override
  String get searchProduct => 'Product or barcode';

  @override
  String get favorites => 'Favorites';

  @override
  String get nothingFound => 'Nothing found';

  @override
  String get inCheck => 'In receipt';

  @override
  String positions(int n) {
    return '$n items';
  }

  @override
  String get swipeToRemove => 'Swipe a line left to remove it';

  @override
  String payAmount(String sum) {
    return 'Pay $sum som';
  }

  @override
  String get orderDiscount => 'Receipt discount';

  @override
  String get discountPercentHint => 'Percent, e.g. 10';

  @override
  String get extraService => 'Extra service';

  @override
  String get clearCheck => 'Clear receipt';

  @override
  String get price => 'Price';

  @override
  String weightOf(String name) {
    return 'Weight: $name';
  }

  @override
  String get kg => 'kilograms, e.g. 0.5';

  @override
  String get lineDiscount => 'Item discount';

  @override
  String get sumHint => 'Amount in som';

  @override
  String get discount => 'Discount';

  @override
  String barcodeNotFound(String code) {
    return 'Barcode $code is not in the catalog.';
  }

  @override
  String get cameraUnavailable =>
      'The camera is unavailable. Allow camera access in the phone settings or type the barcode.';

  @override
  String get scanHint =>
      'Point at the barcode — the product is added automatically';

  @override
  String scanReceiveHint(int n) {
    return 'Scanned: $n';
  }

  @override
  String get toCheck => 'To receipt';

  @override
  String get checkPrice => 'Check price';

  @override
  String get receiving => 'Receiving';

  @override
  String doneCount(int n) {
    return 'Done · $n';
  }

  @override
  String inStock(String qty, String unit) {
    return '$qty $unit in stock';
  }

  @override
  String get scanContinuous => 'Scan in a row without confirming';

  @override
  String checkN(int n) {
    return 'Receipt · $n';
  }

  @override
  String get addToCheck => 'Add to receipt';

  @override
  String get addedAutomatically => 'Added to receipt';

  @override
  String get profileTitle => 'Profile';

  @override
  String get discountInPercent => 'Percent';

  @override
  String get discountInSom => 'Amount, som';

  @override
  String discountResult(String sum) {
    return 'Total with discount: $sum som';
  }

  @override
  String get clothingMode => 'Sizes and colors at sale';

  @override
  String get clothingModeSub => 'For clothing and shoe shops';

  @override
  String get chooseSize => 'Size and color';

  @override
  String get colorLabel => 'Color';

  @override
  String get sizeLabel => 'Size';

  @override
  String toCheckSize(String size) {
    return 'To receipt · $size';
  }

  @override
  String sizeLowStock(String qty) {
    return 'Only $qty pcs of this size left. Add anyway?';
  }

  @override
  String get rentals => 'Rentals';

  @override
  String get rentalNew => 'New rental';

  @override
  String get rentalActive => 'Out';

  @override
  String get rentalOverdue => 'Overdue';

  @override
  String get rentalReturn => 'Take back';

  @override
  String get rentalReturned => 'Return accepted.';

  @override
  String get rentalCondOk => 'Intact';

  @override
  String get rentalCondDamaged => 'Damaged';

  @override
  String get rentalPenalty => 'Penalty from deposit';

  @override
  String get rentalClient => 'Customer';

  @override
  String get rentalItem => 'Item';

  @override
  String get rentalUntil => 'Until';

  @override
  String get rentalPerDay => 'Price per day';

  @override
  String rentalDays(int n) {
    return '$n days';
  }

  @override
  String get rentalDeposit => 'Cash deposit';

  @override
  String get rentalDocument => 'Document deposit';

  @override
  String get rentalDocumentHint => 'Passport ID…, name';

  @override
  String get rentalCreate => 'Hand out and to receipt';

  @override
  String rentalCreated(int n) {
    return 'Rental #$n created — the cost is in the receipt.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Rental #$n · $days d';
  }

  @override
  String get chooseClient => 'Choose customer';

  @override
  String get chooseItem => 'Choose an item';

  @override
  String get marginPercent => 'Markup, %';

  @override
  String get marginSum => 'Markup, som';

  @override
  String get kindProduct => 'Product';

  @override
  String get kindClothes => 'Clothing';

  @override
  String get kindService => 'Service';

  @override
  String get sizesLabel => 'Sizes';

  @override
  String get colorsLabel => 'Colors';

  @override
  String get addColor => 'Add color';

  @override
  String get colorName => 'Color name';

  @override
  String get colorSizeGrid => 'Color × size — how many';

  @override
  String variantsSummary(int n, String qty) {
    return '$n variants · $qty pcs';
  }

  @override
  String variantsSaved(int n) {
    return 'Variants saved: $n';
  }

  @override
  String get durationLabel => 'Duration';

  @override
  String get mastersLabel => 'Masters · own price and percent';

  @override
  String get masterAdd => 'Add master';

  @override
  String get masterName => 'Master\'s name';

  @override
  String get masterPercent => '% to master';

  @override
  String get consumablesLabel => 'Consumables from stock';

  @override
  String get consumableAdd => 'Add consumable';

  @override
  String get onlineBooking => 'Online booking on site and bot';

  @override
  String get serviceLocalNote =>
      'Masters, duration and consumables are kept on this phone until the server has a services API. The service itself is in the NurCRM catalog.';

  @override
  String get saveService => 'Save service';

  @override
  String get findInternet => 'Find on the internet';

  @override
  String get internetFound => 'Found in the NurCRM base';

  @override
  String get barcodeMatched => 'barcode matched · exact';

  @override
  String get takeLabel => 'Take';

  @override
  String get categoryLabel => 'Category';

  @override
  String get brandLabel => 'Brand';

  @override
  String get fillProduct => 'Fill in the product';

  @override
  String get searchInBrowser => 'Search in browser';

  @override
  String get priceNotTaken =>
      'Prices from the internet are not used — enter your own.';

  @override
  String get bookingsTitle => 'Services · bookings';

  @override
  String get dayToday => 'Today';

  @override
  String get dayTomorrow => 'Tomorrow';

  @override
  String get allMasters => 'All masters';

  @override
  String get bookNew => 'Book';

  @override
  String get bookCheckout => 'Check out';

  @override
  String get bookArrived => 'arrived';

  @override
  String get bookBooked => 'booked';

  @override
  String get bookPaid => 'paid';

  @override
  String get bookFree => 'free';

  @override
  String get bookMarkArrived => 'Mark as arrived';

  @override
  String get bookRemind => 'Remind on WhatsApp';

  @override
  String get bookCancel => 'Cancel booking';

  @override
  String get bookTime => 'Time';

  @override
  String get bookClash => 'The master already has a booking then.';

  @override
  String get bookNoMasters =>
      'No masters yet — add them when creating a service.';

  @override
  String get bookLocalNote =>
      'Bookings are kept on this phone until the server has a bookings API. Payment is a normal NurCRM receipt.';

  @override
  String bookReminder(String name, String when, String master) {
    return 'Hello, $name! A reminder of your booking $when with $master.';
  }

  @override
  String get pricesTitle => 'Sale prices';

  @override
  String pricesSub(int a, int b) {
    return 'cost changed: $a · new: $b';
  }

  @override
  String get ruleLabel => 'Rule';

  @override
  String get roundTo => 'Round to';

  @override
  String get applyToAll => 'To all lines, not only changed';

  @override
  String costWas(String was, String now) {
    return 'cost $was → $now';
  }

  @override
  String get newItemShort => 'new';

  @override
  String get applyPrices => 'Apply prices';

  @override
  String postSummary(int a, String qty, int b, int n, String sum) {
    return 'Lines: $a · pcs: $qty\nNew products: $b\nSale prices changing: $n\nPurchase: $sum';
  }

  @override
  String get quotesTitle => 'Quotes and invoices';

  @override
  String get quoteNew => 'Commercial offer';

  @override
  String get invoiceNew => 'Invoice';

  @override
  String get quotesWaiting => 'Awaiting reply';

  @override
  String get invoicesUnpaid => 'Unpaid';

  @override
  String get invoicesOverdue => 'Overdue';

  @override
  String get filterAll => 'All';

  @override
  String get filterQuotes => 'Offers';

  @override
  String get filterInvoices => 'Invoices';

  @override
  String get stDraft => 'Draft';

  @override
  String get stSent => 'Sent';

  @override
  String get stAccepted => 'Accepted → invoice';

  @override
  String get stDeclined => 'Declined';

  @override
  String get stIssued => 'Awaiting payment';

  @override
  String get stPartial => 'Partly paid';

  @override
  String get stPaid => 'Paid';

  @override
  String validUntil(String date) {
    return 'Valid until $date';
  }

  @override
  String payUntil(String date) {
    return 'Pay by $date';
  }

  @override
  String get fromCart => 'From current receipt';

  @override
  String get useWholesale => 'Wholesale prices';

  @override
  String get innLabel => 'Tax ID';

  @override
  String get docSendPdf => 'Send PDF';

  @override
  String get invoiceFromQuote => 'Invoice from offer';

  @override
  String get takePayment => 'Take payment';

  @override
  String get sellFromDoc => 'Make the sale';

  @override
  String get markDeclined => 'Client declined';

  @override
  String get editDoc => 'Edit';

  @override
  String get deleteDoc => 'Delete document';

  @override
  String paidOf(String paid, String total) {
    return 'paid $paid of $total';
  }

  @override
  String basedOnQuote(String number) {
    return 'from $number';
  }

  @override
  String get quotesLocalNote =>
      'Offers and invoices are shared with the owner\'s program on the PC and the company\'s other phones. A sale from an invoice is a normal NurCRM receipt.';

  @override
  String get addLine => 'Add product';

  @override
  String get docDiscount => 'Discount, som';

  @override
  String get esfTitle => 'Fiscal and e-invoices';

  @override
  String esfQueue(int n) {
    return '$n receipts queued — they go out once online';
  }

  @override
  String get esfOnline => 'Till online, queue empty';

  @override
  String get esfIssue => 'Issue e-invoice';

  @override
  String get esfBuyerInn => 'Buyer\'s tax ID';

  @override
  String get esfBuyerName => 'Buyer';

  @override
  String get esfPayForm => 'Payment form';

  @override
  String get esfCash => 'Cash';

  @override
  String get esfNonCash => 'Non-cash';

  @override
  String get esfContract => 'Contract no. and date';

  @override
  String get esfSum => 'Amount, som';

  @override
  String get esfDraftSaved => 'E-invoice draft saved';

  @override
  String get esfSendTax => 'Send to the tax office';

  @override
  String get esfDrafts => 'E-invoice drafts';

  @override
  String get esfIncoming => 'Incoming e-invoices';

  @override
  String get esfStub =>
      'Sending to the tax office and incoming e-invoices will work once the NurCRM server connects the tax service. For now you can prepare drafts.';

  @override
  String get smartCam => 'Smart camera';

  @override
  String get byLook => 'By look';

  @override
  String get byLookHint =>
      'Photograph a product without a barcode — AI recognizes it and finds it in the catalog';

  @override
  String get recognizing => 'Recognizing…';

  @override
  String get recognizedNone =>
      'Not recognized — shoot closer and in good light';

  @override
  String get seenOnPhoto => 'On the photo';

  @override
  String get notInCatalog => 'not in catalog';

  @override
  String get aiKeyNeeded =>
      'The smart camera needs an AI key (Gemini) — the same as Nur Advisor on the till. You can paste it here.';

  @override
  String get aiKeyLabel => 'Gemini key';

  @override
  String get smartShelf => 'Shelf';

  @override
  String get smartInvoice => 'Invoice';

  @override
  String get smartLabels => 'Labels';

  @override
  String get smartHint =>
      'Hold the phone steady — the camera looks for products and price tags';

  @override
  String get takePhoto => 'Take a photo';

  @override
  String get morePhoto => 'Another photo';

  @override
  String seenInBase(int n) {
    return 'In catalog · $n';
  }

  @override
  String seenNew(int n) {
    return 'New · $n';
  }

  @override
  String smartNext(int n) {
    return 'Next — to receiving · $n';
  }

  @override
  String get bulkImport => 'Bulk import';

  @override
  String get bulkPickFile => 'Choose an Excel or CSV file';

  @override
  String get bulkPaste => 'Paste copied from Excel';

  @override
  String get bulkTemplate => 'File template';

  @override
  String get bulkHint =>
      'Columns: Name · Barcode · Sale price · Cost · Quantity · Unit · Category. The first row is headers (any order). A product with the same barcode is not duplicated: quantity is added and prices updated.';

  @override
  String bulkSummary(int n, int a, int b) {
    return 'Rows: $n · new $a · existing $b';
  }

  @override
  String get bulkBad => 'skipped: no name or price';

  @override
  String get bulkExists => 'exists — will be added';

  @override
  String bulkUpload(int n) {
    return 'Upload $n';
  }

  @override
  String bulkDone(int n) {
    return 'Uploaded: $n';
  }

  @override
  String get bulkNothing => 'No product rows found in the file';

  @override
  String get notFoundTitle => 'Product not in catalog';

  @override
  String notFoundAsk(String code) {
    return 'Barcode $code. Add a new product with this barcode?';
  }

  @override
  String get addNewProduct => 'Add product';

  @override
  String get supplierLabel => 'Supplier';

  @override
  String get supplierDiscount => 'Supplier discount, %';

  @override
  String costWithDiscount(String sum) {
    return 'Cost after discount: $sum';
  }

  @override
  String get supplierReceiptNote =>
      'The quantity arrives as a receipt from the supplier — NurCRM shows who you bought from and at what price.';

  @override
  String get retailPrice => 'Retail price';

  @override
  String get altBarcodesLabel => 'Extra barcodes';

  @override
  String get altBarcodesHint =>
      'Another barcode of the same product or its pack: for a box, set how many pieces it holds — scanning the box adds them all.';

  @override
  String get altBarcodeAdd => 'Add barcode';

  @override
  String get packQty => 'Pcs';

  @override
  String get packName => 'Pack';

  @override
  String get photosLabel => 'Product photos';

  @override
  String get photoCamera => 'Camera';

  @override
  String get photoGallery => 'Gallery';

  @override
  String get photoInternet => 'From the internet';

  @override
  String get photosSearching => 'Searching photos…';

  @override
  String get photoFirstMain => 'The first photo is the main one';

  @override
  String get descriptionLabel => 'Description — for the site and bot';

  @override
  String get descriptionFind => 'Find a description online';

  @override
  String get descriptionWriting => 'Writing a description…';

  @override
  String get lowStockPull => '↑ Pull up — what is running out';

  @override
  String lowStockTitle(int n) {
    return 'Running out · $n';
  }

  @override
  String get lowStockEmpty => 'Everything is in stock';

  @override
  String get summaryTitle => 'Summary';

  @override
  String get periodToday => 'Today';

  @override
  String get periodWeek => 'Week';

  @override
  String get periodMonth => 'Month';

  @override
  String get kpiRevenue => 'Revenue';

  @override
  String get kpiChecks => 'Receipts';

  @override
  String get kpiAvg => 'Average receipt';

  @override
  String get kpiProfit => 'Profit';

  @override
  String get revenueByDay => 'Revenue by day';

  @override
  String get paymentMethods => 'Payment methods';

  @override
  String get topProducts => 'Top products';

  @override
  String get noSalesPeriod => 'No sales in this period';

  @override
  String get abcTitle => 'ABC analysis';

  @override
  String get abcByRevenue => 'By revenue';

  @override
  String get abcByProfit => 'By profit';

  @override
  String get abcByQty => 'By units';

  @override
  String get abcA => 'Feed the shop';

  @override
  String get abcB => 'Middle';

  @override
  String get abcC => 'Tail';

  @override
  String abcGroupNote(int n, String share) {
    return '$n items · $share %';
  }

  @override
  String get paretoTitle => 'Pareto chart';

  @override
  String get paretoHint =>
      'Bars — each product\'s contribution, line — cumulative share. Products left of the 80 % mark are group A.';

  @override
  String get advisorTitle => 'Nur Advisor';

  @override
  String get advisorOnline => 'Shop data connected';

  @override
  String get advisorAsk => 'Ask about your shop…';

  @override
  String get advisorThinking => 'The advisor is thinking…';

  @override
  String get advisorQ1 => 'How much did we sell yesterday?';

  @override
  String get advisorQ2 => 'Who owes us?';

  @override
  String get advisorQ3 => 'What should I order?';

  @override
  String get advisorQ4 => 'How was the week?';

  @override
  String get advisorNew => 'New conversation';

  @override
  String get advisorNoKey =>
      'Add an AI key in the owner\'s program: Nur Advisor → ⚙ → AI models and keys';

  @override
  String get advisorStageData => 'Gathering shop data…';

  @override
  String get advisorStageAsk => 'Asking the AI…';

  @override
  String advisorError(String error) {
    return 'The advisor did not answer: $error';
  }

  @override
  String get callTitle => 'Voice call';

  @override
  String get callConnecting => 'Connecting…';

  @override
  String get callPreparing => 'Preparing shop data…';

  @override
  String get callListening => 'Listening…';

  @override
  String get callThinking => 'Thinking…';

  @override
  String get callSpeaking => 'Speaking… (tap to interrupt)';

  @override
  String get callEnded => 'Call ended';

  @override
  String get callEnd => 'End';

  @override
  String get callMicOff => 'Mic off';

  @override
  String get callMicOn => 'Mic on';

  @override
  String get callSpeakerOn => 'Speaker';

  @override
  String get callSpeakerOff => 'Earpiece';

  @override
  String callCost(int n) {
    return 'The call lasts up to $n min and uses the AI key\'s quota faster than chat.';
  }

  @override
  String get callLimitSoon => 'The call will end in a minute';

  @override
  String callEndedLimit(int n) {
    return 'The call ended: $n min passed. You can call again.';
  }

  @override
  String get callEndedFocus =>
      'The call was interrupted: an incoming call or the app was minimised.';

  @override
  String callFailed(String error) {
    return 'The call failed: $error';
  }

  @override
  String get callNoMic =>
      'No microphone access. Allow the microphone for NurMarket in the phone settings.';

  @override
  String get callOpenSettings => 'Open settings';

  @override
  String get callUnsupported => 'Voice calls work on an Android phone.';

  @override
  String get callTableReady =>
      'A table with the figures will be in the chat after the call';

  @override
  String get callClose => 'Close';

  @override
  String get callYou => 'You';

  @override
  String checkTab(int n) {
    return 'Receipt · $n';
  }

  @override
  String get deleteProduct => 'Delete product';

  @override
  String deleteProductAsk(String name) {
    return 'Delete “$name”? Its sales and reports stay.';
  }

  @override
  String get productDeleted => 'Product deleted';

  @override
  String smartUpload(int n) {
    return 'Upload $n products to stock';
  }

  @override
  String get smartToReceive => 'To receiving with a supplier';

  @override
  String smartShots(int n) {
    return 'Shots: $n';
  }

  @override
  String get smartPriceRule => 'New items\' price: cost +30 %, rounded to 5';

  @override
  String smartUploaded(int n) {
    return 'Uploaded to stock: $n';
  }

  @override
  String get sellingPrice => 'Selling price';

  @override
  String get durationCustom => 'Custom…';

  @override
  String get minutesHint => 'minutes';

  @override
  String get photoLabel => 'Photo';

  @override
  String get shiftDetails => 'Shift report';

  @override
  String get shOpened => 'Opened';

  @override
  String get shClosed => 'Closed';

  @override
  String get shOpeningCash => 'Opening cash';

  @override
  String get shNonCash => 'Non-cash';

  @override
  String get shDebt => 'On credit';

  @override
  String get shReturns => 'Returns';

  @override
  String get shIncome => 'Other income';

  @override
  String get shExpected => 'Expected in drawer';

  @override
  String get shCounted => 'Counted';

  @override
  String get shDifference => 'Difference';

  @override
  String orderReadyNotify(String name, String num, String sum) {
    return 'Hello, $name! Your order #$num is ready for pickup. To pay: $sum som. See you soon!';
  }

  @override
  String get customSize => 'Custom size';

  @override
  String get pickSupplier => 'Choose supplier';

  @override
  String get addProductsHint => 'Find a product and tap to add';

  @override
  String get removeAction => 'Remove';

  @override
  String get esfExportCsv => 'Export to Excel (CSV)';

  @override
  String get esfExportAll => 'All drafts to Excel (CSV)';

  @override
  String get esfTaxSoon =>
      'Sending to the tax service will work once the NurCRM server connects ESF. For now export to Excel and upload on the tax website.';

  @override
  String get dateLabel => 'Date';

  @override
  String get supplierNoLow => 'This supplier has no products running low';

  @override
  String get supplierLowAdd => 'Add this supplier\'s low-stock products';

  @override
  String get inStockOnly => 'In stock';

  @override
  String get allCategories => 'All categories';

  @override
  String get catalogPick => 'From catalog';

  @override
  String get themeAuto => 'By time';

  @override
  String get darkFrom => 'Dark from';

  @override
  String get darkTo => 'until';

  @override
  String get leaveAsk => 'Are you sure you want to leave?';

  @override
  String get leaveAskSub => 'Unsaved changes will be lost.';

  @override
  String get leaveYes => 'Yes, leave';

  @override
  String get leaveNo => 'No, pressed by accident';

  @override
  String get aiSettings => 'AI settings';

  @override
  String get aiSettingsHint =>
      'Nur Advisor answers via Gemini. If Gemini\'s free limit runs out — via Groq, then OpenRouter. Keys are free; saved in NurCRM and work on the phone, till and owner app.';

  @override
  String get aiConnected => 'connected';

  @override
  String get aiNoKeyShort => 'no key';

  @override
  String get aiGetKey => 'Get a key';

  @override
  String get aiTest => 'Test';

  @override
  String get aiTestOk => 'Works ✓';

  @override
  String get aiKeysSaved => 'Keys saved';

  @override
  String get aiPasteKey => 'Paste a new key';

  @override
  String get aiRemoveKey => 'Remove key';

  @override
  String get payQrTitle => 'Payment QR';

  @override
  String get payQrHint =>
      'Add your banks\' QR codes. The customer scans a QR with the receipt amount and pays in their bank app. Choose which cashier sees which QR — up to 5 banks to choose from.';

  @override
  String get payQrAdd => 'Add QR';

  @override
  String get payQrBank => 'Bank';

  @override
  String get payQrCode => 'Bank QR';

  @override
  String get payQrScan => 'Scan';

  @override
  String get payQrGallery => 'From gallery (screenshot)';

  @override
  String get payQrPaste => 'Paste link';

  @override
  String get payQrDynamic => 'Receipt amount in QR';

  @override
  String get payQrDynamicSub =>
      'The customer doesn\'t type the amount (ELQR, as on the till; tested with MBank). Turn off if a bank rejects it.';

  @override
  String get payQrStaticOnly =>
      'This QR is not ELQR — shown as is, without the amount';

  @override
  String get payQrCashiers => 'Show to';

  @override
  String get payQrAllCashiers => 'All cashiers';

  @override
  String get payQrNotRecognized => 'QR not recognized';

  @override
  String get payQrShowCustomer =>
      'Show it to the customer (tap to enlarge). After payment press Pay.';

  @override
  String get payQrNone =>
      'No payment QR yet — the owner adds them in Management → Payment QR.';

  @override
  String get payQrNoneOwner => 'No QR yet. Tap Add QR.';

  @override
  String get payQrSaved => 'QR codes saved';

  @override
  String get payQrDelete => 'Delete QR';

  @override
  String get grpAi => 'AI & analytics';

  @override
  String get grpStock => 'Products & stock';

  @override
  String get grpMoney => 'Sales & money';

  @override
  String get grpClients => 'Customers & documents';

  @override
  String get grpTeam => 'Team';

  @override
  String get findSection => 'Find a section';

  @override
  String get liveAuto => 'Auto';

  @override
  String get liveHintSale => 'Point at the product and tap «Scan»';

  @override
  String get liveHintShelf => 'Point at the shelf';

  @override
  String get liveHintInvoice => 'Fit the whole invoice in the frame';

  @override
  String get liveHintLabels => 'Point at price tags and labels';

  @override
  String get auditTitle => 'Audit log';

  @override
  String get auditSub => 'All actions on this phone';

  @override
  String get auditSearch => 'Employee, product, receipt number';

  @override
  String get auditKind => 'Action';

  @override
  String get auditWhat => 'What was done';

  @override
  String get auditSale => 'Sale';

  @override
  String get auditCart => 'To receipt';

  @override
  String get auditReturn => 'Return';

  @override
  String get auditPrice => 'Prices and stock';

  @override
  String get auditProduct => 'Products';

  @override
  String get auditSettings => 'Settings';

  @override
  String get auditLogin => 'Sign in/out';

  @override
  String get auditOpen => 'Opened screen';

  @override
  String get teachCamera => 'Teach the smart camera: 3–5 photos + barcode';

  @override
  String get teachRight => '1 of 3 · product from the right';

  @override
  String get teachLeft => '2 of 3 · product from the left';

  @override
  String get teachTop => '3 of 3 · product from the top';

  @override
  String get teachHint => 'Whole product in the frame, light background';

  @override
  String get teachDone => 'Camera trained — it will find this product exactly';

  @override
  String teachShots(int n) {
    return 'For the smart camera: $n photos';
  }

  @override
  String get scanShoot => 'Scan';

  @override
  String get scanBusy => 'Scanning…';

  @override
  String get scanNext => 'Next product — tap «Scan» again';

  @override
  String get scanChecking => 'Checking against trained products…';

  @override
  String scanTook(String sec) {
    return 'in $sec s';
  }

  @override
  String teachPhotoOf(int a, int b) {
    return 'Photo $a of $b';
  }

  @override
  String get teachAngleRight => 'product from the right';

  @override
  String get teachAngleLeft => 'product from the left';

  @override
  String get teachAngleTop => 'product from the top';

  @override
  String get teachAngleFront => 'label from the front';

  @override
  String get teachAngleBack => 'product from the back';

  @override
  String get teachCanFinish => 'Tap «Done» or shoot one more angle';

  @override
  String get teachRetake => 'Retake';

  @override
  String get weighAim =>
      'Put the item on the scale, point at the display and tap «Scan»';

  @override
  String get weighAimDisplay =>
      'The scale display is not visible — point at the weight window, turn off the flashlight and tap «Scan»';

  @override
  String get aiErrNoNet =>
      'No internet — check the connection and tap «Scan» again';

  @override
  String get aiErrSlow =>
      'The AI did not answer in time (slow connection) — try again';

  @override
  String get aiErrLimit =>
      'The free AI request limit is used up — try again in a minute';

  @override
  String get aiErrBusy =>
      'The AI is overloaded right now — try again in a minute';

  @override
  String get aiErrOther => 'The AI could not recognize the photo';

  @override
  String addAgain(String name) {
    return '«$name» is already in the receipt. Add another?';
  }

  @override
  String get yesAdd => 'Add';

  @override
  String get shelfAddAsk =>
      'A shelf photo shows goods already on the shelf. Add these quantities to stock as a delivery?';

  @override
  String isItProduct(String name) {
    return 'Is it «$name»?';
  }

  @override
  String notFoundOffline(String code) {
    return 'Code $code is not on the phone and the server is not answering. Check the connection — the product may already exist on the PC, don\'t create a duplicate.';
  }

  @override
  String get scaleLabelsTitle => 'Scale labels';

  @override
  String get scaleLabelsHint =>
      'A scale label barcode starts with a prefix (20–29). Set which prefixes carry the AMOUNT and which carry the WEIGHT. For all company phones.';

  @override
  String get scaleAmountPrefixes => '«Amount» prefixes';

  @override
  String get scaleWeightPrefixes => '«Weight» prefixes';

  @override
  String get cameraShotFailed =>
      'The camera did not take the photo — tap «Scan» again';

  @override
  String get saved => 'Saved';

  @override
  String get exactMatch => 'exact';

  @override
  String get noAccess => 'No access';

  @override
  String get noAccessHint =>
      'This section or action is closed for your account. The owner can enable it: NurCRM → Employees → access.';

  @override
  String get clientOptional => 'Customer — for bonuses and purchase history';

  @override
  String get bonusRedeem => 'Pay with bonuses';

  @override
  String get bonusNone => 'Don\'t use';

  @override
  String bonusAll(String sum) {
    return 'Maximum · $sum';
  }

  @override
  String bonusEarn(String sum) {
    return 'Bonuses to earn: $sum';
  }

  @override
  String bonusPaid(String sum) {
    return 'By bonuses: −$sum som';
  }

  @override
  String get removeClient => 'Remove customer';

  @override
  String get discountRemove => 'Remove discount';

  @override
  String get addedToCheck => 'Added to receipt';

  @override
  String get payment => 'Payment';

  @override
  String toPayPositions(int n) {
    return 'To pay · $n items';
  }

  @override
  String get payMethod => 'Payment method';

  @override
  String get cash => 'Cash';

  @override
  String get card => 'Card';

  @override
  String get debt => 'On credit';

  @override
  String get mixed => 'Mixed';

  @override
  String get client => 'Customer';

  @override
  String get received => 'Received';

  @override
  String get receivedNowDebt => 'Received now (0 — all on credit)';

  @override
  String get exact => 'Exact';

  @override
  String get change => 'Change';

  @override
  String notEnough(String sum) {
    return '$sum som short';
  }

  @override
  String get receiptToCustomer => 'Receipt for customer';

  @override
  String get notNeeded => 'Not needed';

  @override
  String get doPay => 'Complete payment';

  @override
  String get check => 'Receipt';

  @override
  String get total => 'Total';

  @override
  String get paidOk => 'Payment completed';

  @override
  String get savedOffline => 'Receipt saved on the phone';

  @override
  String get savedOfflineHint =>
      'No connection — the receipt will be sent automatically and won\'t be recorded twice.';

  @override
  String get receiptQr => 'QR code';

  @override
  String get showQr => 'Show QR to customer';

  @override
  String get qrPointCamera => 'The customer points their phone camera';

  @override
  String get qrDownloadHint =>
      'The customer\'s camera downloads the receipt as a PDF right away';

  @override
  String get qrTextHint =>
      'Until the server issues a receipt link, the camera shows it as text. Send the PDF with the button below.';

  @override
  String get cashInOut => 'Cash in / out';

  @override
  String get cashIn => 'Cash in';

  @override
  String get cashOut => 'Cash out';

  @override
  String get amount => 'Amount';

  @override
  String get reason => 'Reason';

  @override
  String get reasonChange => 'Change float';

  @override
  String get reasonCollection => 'Collection';

  @override
  String get reasonExpense => 'Expenses';

  @override
  String get reasonOther => 'Other';

  @override
  String get cashOpDone => 'Recorded in the shift';

  @override
  String get debts => 'Customer debts';

  @override
  String get debtorsTotal => 'Total debts';

  @override
  String get payDebt => 'Take debt payment';

  @override
  String get payDebtDone => 'Debt payment recorded';

  @override
  String get noDebts => 'No debtors';

  @override
  String get heldChecks => 'Held receipts';

  @override
  String get holdCheck => 'Hold receipt';

  @override
  String get holdNote => 'For whom or a note';

  @override
  String get heldEmpty => 'No held receipts';

  @override
  String get restoreCheck => 'Restore';

  @override
  String get delete => 'Delete';

  @override
  String get checkBusy => 'The receipt has items — hold or pay it first';

  @override
  String get returnTitle => 'Return by receipt';

  @override
  String get returnAll => 'Return the whole receipt';

  @override
  String get returnSelected => 'Return selected';

  @override
  String get returnDone => 'Return completed';

  @override
  String get returnToCustomer => 'Refund to customer';

  @override
  String get showReceipt => 'View receipt';

  @override
  String get sendPdf => 'Send PDF';

  @override
  String get sendReceipt => 'Send receipt';

  @override
  String get newClient => 'New customer';

  @override
  String get clientName => 'Name';

  @override
  String get phone => 'Phone';

  @override
  String get clientSearch => 'Name or phone';

  @override
  String get myChecks => 'My receipts';

  @override
  String get thisShift => 'This shift';

  @override
  String unsentN(int n) {
    return 'Not sent · $n';
  }

  @override
  String get checkSearch => 'Receipt number or product';

  @override
  String get noChecks => 'No receipts yet';

  @override
  String get checkNotSent => 'Receipt not sent';

  @override
  String get notAccepted => 'not accepted';

  @override
  String get waitsForNetwork => 'waiting for network';

  @override
  String get returned => 'returned';

  @override
  String get offlineCheckHint =>
      'No connection — the receipt is saved and will be sent automatically';

  @override
  String get sendToCustomer => 'Send to customer';

  @override
  String get sendAgain => 'Send again';

  @override
  String get returnByAdmin => 'Returns — with the administrator\'s permission';

  @override
  String get receiveByScanner => 'Receive by scanner';

  @override
  String get audit => 'Stock count';

  @override
  String get findProduct => 'Find product';

  @override
  String get pricesByAdmin =>
      'Prices and new products are changed by the administrator';

  @override
  String get receiveNoSupplier => 'No supplier, no prices';

  @override
  String get scanProduct => 'Scan product';

  @override
  String get eachScanPlusOne => 'Each scan — +1 piece';

  @override
  String get accepted => 'Received';

  @override
  String goodsPieces(int n, String qty) {
    return '$n products · $qty pcs';
  }

  @override
  String wasPieces(String qty) {
    return 'was $qty';
  }

  @override
  String get unknownBarcode => 'Unknown barcode';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · the administrator will get it and add the product';
  }

  @override
  String get postpone => 'Postpone';

  @override
  String postPieces(String qty) {
    return 'Post · $qty pcs';
  }

  @override
  String get receivedOk => 'Receiving posted.';

  @override
  String get receivedQueued => 'Receiving saved — will be sent when online.';

  @override
  String get allStock => 'Whole stock';

  @override
  String get chooseWhatToCount => 'What to count';

  @override
  String checkedOf(int a, int b) {
    return 'Checked $a of $b';
  }

  @override
  String get notChecked => 'Not checked';

  @override
  String get differences => 'Differences';

  @override
  String byProgram(String qty, String unit) {
    return '$qty $unit in the system';
  }

  @override
  String get matches => 'matches';

  @override
  String shortBy(String qty) {
    return 'short by $qty';
  }

  @override
  String extraBy(String qty) {
    return 'extra $qty';
  }

  @override
  String get auditAdminApplies =>
      'The administrator will update stock after review';

  @override
  String get sendToAdmin => 'Send to administrator';

  @override
  String get auditSent => 'Stock count sent to the administrator.';

  @override
  String get auditQueued => 'Stock count saved — will be sent when online.';

  @override
  String sinceChecks(String time, int n) {
    return 'since $time · $n receipts';
  }

  @override
  String get revenue => 'Revenue';

  @override
  String get shouldBeInDrawer => 'Should be in the drawer';

  @override
  String get recountCash => 'Count the cash';

  @override
  String get howMuchInDrawer => 'How much cash is in the drawer';

  @override
  String cashDiff(String sum) {
    return 'Difference from the system: $sum som';
  }

  @override
  String get cashDiffHint =>
      'If the amount doesn\'t match, the administrator will see the difference in the report';

  @override
  String closeShiftPending(int n) {
    return '$n receipts must be sent to the server first — a connection is needed.';
  }

  @override
  String get shiftClosedOk => 'Shift closed';

  @override
  String get receiptPrinter => 'Receipt printer';

  @override
  String get prnSub => 'Bluetooth, Wi-Fi/LAN or USB · 58/80 mm';

  @override
  String get prnNotSet => 'Not set up';

  @override
  String get prnConnection => 'Connection';

  @override
  String get prnOff => 'None';

  @override
  String get prnNetwork => 'Network (Wi-Fi/LAN)';

  @override
  String get prnUsb => 'USB (OTG)';

  @override
  String get prnSearch => 'Find printers';

  @override
  String get prnSearching => 'Searching…';

  @override
  String get prnNothingFound => 'Nothing found. Is the printer on and nearby?';

  @override
  String get prnPairHint =>
      'Printer not listed? Turn it on and tap Find — or pair it in the phone\'s Bluetooth settings (PIN is usually 0000 or 1234).';

  @override
  String get prnUsbHint =>
      'Connect the printer with an OTG cable and turn it on. Android will ask for access — tap Allow.';

  @override
  String get prnBtSettings => 'Bluetooth settings';

  @override
  String get prnPaired => 'paired';

  @override
  String get prnNotPaired => 'nearby, not paired';

  @override
  String get prnIp => 'Printer IP address';

  @override
  String get prnPort => 'Port';

  @override
  String get prnPaper => 'Paper width';

  @override
  String prnPaperMm(int a, int b) {
    return '$a mm · $b characters';
  }

  @override
  String get prnCodepage => 'Cyrillic encoding';

  @override
  String get prnCodeTable => 'ESC t table';

  @override
  String get prnCodepageHint =>
      'Hieroglyphs or «?» instead of Cyrillic — change the encoding or table number (CP866 — 17, sometimes 6 or 7; CP1251 — 46, sometimes 73) and test again.';

  @override
  String get prnAuto => 'Print the receipt right after payment';

  @override
  String get prnTest => 'Test print';

  @override
  String get prnTestSent => 'Test page sent to the printer';

  @override
  String get prnTestHint =>
      'Check the test page: the row of digits should fill the width and Cyrillic should be readable.';

  @override
  String get prnPrint => 'Print receipt';

  @override
  String get prnSent => 'Receipt sent to the printer';

  @override
  String get prnErrNotConfigured =>
      'Printer is not set up: Profile → Receipt printer.';

  @override
  String get prnErrUnsupported =>
      'This print method is not available on this device.';

  @override
  String get prnErrBtOff => 'Bluetooth is off — turn it on and try again.';

  @override
  String get prnErrPermission =>
      'No “Nearby devices” (Bluetooth) permission. Allow it in the Android prompt or in the app settings.';

  @override
  String get prnErrNotFound =>
      'Printer not found — check it is on and choose it again.';

  @override
  String get prnErrBleOnly =>
      'This printer only works over BLE — a classic Bluetooth (SPP) printer is needed.';

  @override
  String get prnErrTimeout =>
      'The printer does not respond. Is it on, nearby, with paper? Try again.';

  @override
  String get prnErrRefused =>
      'The network printer refuses the job — check the IP address and port (usually 9100).';

  @override
  String get prnErrUsbDenied =>
      'No access to the USB printer — tap Allow in the Android prompt.';

  @override
  String get prnErrUsbNotFound =>
      'USB printer not found — check the OTG cable and printer power.';

  @override
  String prnErrIo(String text) {
    return 'Print error: $text';
  }

  @override
  String get kgUnit => 'kg';

  @override
  String get weighShort => 'Scale';

  @override
  String get weighTitle => 'Weighed item';

  @override
  String get weighHint =>
      'Put the item on the scale. Point the camera so the item and the scale display are visible.';

  @override
  String get weighReading => 'Recognising the item and reading the scale…';

  @override
  String get weighWaiting => 'Waiting for the weight to settle…';

  @override
  String get weighZero => 'The scale shows 0 — put the item on it.';

  @override
  String get weighNoDisplay =>
      'The scale display is not visible — point the camera at the weight digits.';

  @override
  String get weighNoAi =>
      'Smart camera unavailable (no connection or AI key) — choose the item and enter the weight manually.';

  @override
  String get weighConfirm => 'Check the item and weight';

  @override
  String weighRead(String name, String kg) {
    return 'Camera read: $name · $kg kg';
  }

  @override
  String weighLine(String name, String kg, String price, String sum) {
    return '$name $kg kg × $price = $sum som';
  }

  @override
  String weighTooHeavy(String kg) {
    return 'Weight $kg kg is over 50 kg. Add anyway?';
  }

  @override
  String get weighNotStable => 'The weight did not settle — check the reading.';

  @override
  String get weighEditKg => 'Change weight';

  @override
  String get weighOther => 'Another item — choose from the list';

  @override
  String get linkWithKassa => 'Link with the till';

  @override
  String get linkWithKassaSub => 'Over the shop Wi-Fi — in the next update';

  @override
  String get language => 'Language';

  @override
  String get darkTheme => 'Dark theme';

  @override
  String get offlineMode => 'Offline work';

  @override
  String waitingToSend(int n) {
    return 'Waiting to send: $n';
  }

  @override
  String get logout => 'Sign out';

  @override
  String logoutPending(int n) {
    return 'There are $n unsent records on the phone. They stay after signing out and will be sent at the next sign-in to this account. Sign out?';
  }

  @override
  String get sync => 'Sync';

  @override
  String catalogUpdated(String time) {
    return 'Catalog updated $time';
  }

  @override
  String get nothingToSend => 'Everything is sent';

  @override
  String get sent => 'sent';

  @override
  String get sendNow => 'Send now';

  @override
  String get kindSale => 'Sale';

  @override
  String get admin => 'Management';

  @override
  String get adminSub => 'Prices, shifts, orders, customers';

  @override
  String get revenueToday => 'Revenue today';

  @override
  String get checksToday => 'Receipts today';

  @override
  String get clientsOwe => 'Customers owe';

  @override
  String get productPrice => 'Product — price and stock';

  @override
  String get shifts => 'Shifts and report';

  @override
  String get siteOrders => 'Website orders';

  @override
  String get clientsBonuses => 'Customers and bonuses';

  @override
  String get supplierOrder => 'Supplier order';

  @override
  String get deletedFromCheck => 'Removed from receipts';

  @override
  String get nameOrBarcode => 'Name or barcode';

  @override
  String get salePrice => 'Sale price';

  @override
  String get purchasePrice => 'Purchase price';

  @override
  String get wholesalePrice => 'Wholesale price';

  @override
  String get onStockLabel => 'In stock';

  @override
  String get priceSaved => 'Price saved.';

  @override
  String markup(String pct) {
    return 'Markup $pct %';
  }

  @override
  String get belowCost => 'Price below cost — selling at a loss';

  @override
  String get shiftOpenNow => 'open';

  @override
  String shiftClosedAt(String time) {
    return 'closed $time';
  }

  @override
  String get cashierLabel => 'Cashier';

  @override
  String get orderNew => 'New';

  @override
  String get orderPicking => 'Picking';

  @override
  String get orderReady => 'Ready';

  @override
  String get orderDone => 'Handed over';

  @override
  String get orderCanceled => 'Canceled';

  @override
  String get pickOrder => 'Pick order';

  @override
  String get pickScanHint => 'Scan an item — it marks itself';

  @override
  String pickedOf(int a, int b) {
    return 'Picked $a of $b';
  }

  @override
  String get orderPicked => 'Order picked';

  @override
  String get notInOrder => 'This item is not in the order';

  @override
  String get notifyCustomer => 'Tell the customer';

  @override
  String get handOver => 'Hand over';

  @override
  String orderReadyMsg(String num, String sum, String code) {
    return 'Order $num is ready! To pay $sum som. Pickup code $code.';
  }

  @override
  String get statusSaved => 'Status saved.';

  @override
  String get bonusBalance => 'Bonus balance';

  @override
  String get addBonus => 'Add bonuses';

  @override
  String get bonusAdded => 'Bonuses added.';

  @override
  String get noPhone => 'no number';

  @override
  String get orderToSupplier => 'Send via WhatsApp';

  @override
  String get supplierName => 'Supplier';

  @override
  String get supplierPhone => 'Supplier phone';

  @override
  String get runningLowAdd => 'Add everything running low';

  @override
  String get orderQty => 'Quantity to order';

  @override
  String get deleteReason => 'Why remove it?';

  @override
  String get reasonChanged => 'Customer changed their mind';

  @override
  String get reasonScanError => 'Scanning error';

  @override
  String get reasonNoMoney => 'Not enough money';

  @override
  String get reasonDefect => 'Defect or expired';

  @override
  String get remove => 'Remove';

  @override
  String get noDeletions => 'No removals.';

  @override
  String get loading => 'Loading…';

  @override
  String get empty => 'Nothing here.';

  @override
  String get share => 'Share';

  @override
  String get documentPdf => 'Waybill, sales receipt, invoice';

  @override
  String get docSalesReceipt => 'Sales receipt';

  @override
  String get docWaybill => 'Waybill';

  @override
  String get docInvoice => 'Invoice';

  @override
  String get staff => 'Staff and rights';

  @override
  String get rightSale => 'Sale and payment';

  @override
  String get rightReturn => 'Returns and exchanges';

  @override
  String get rightDiscount => 'Discounts';

  @override
  String get rightEditPrice => 'Change price in receipt';

  @override
  String get rightDeleteLine => 'Remove from receipt';

  @override
  String get rightStock => 'Stock, receiving, audit';

  @override
  String get rightReports => 'Reports and analytics';

  @override
  String get rightShifts => 'Shifts';

  @override
  String get rightClients => 'Customers';

  @override
  String get rightSettings => 'Settings';

  @override
  String get rightsSaved => 'Rights saved.';

  @override
  String get notifications => 'Notifications';

  @override
  String get notifyNewOrder => 'New website order';

  @override
  String get notifyRunningOut => 'Items running low';

  @override
  String notifyRunningOutSub(int n) {
    return '$n items · time to order';
  }

  @override
  String get notifyBigDebt => 'Large customer debt';

  @override
  String get notifyNotSent => 'Not sent to the server';

  @override
  String get noNotifications => 'No new notifications.';

  @override
  String get newProductSub => 'Barcode, name, prices';

  @override
  String get findByBarcode => 'Find by barcode';

  @override
  String alreadyInStock(String name) {
    return 'This product already exists: $name';
  }

  @override
  String get productCreated => 'Product created.';

  @override
  String get quantityLabel => 'Quantity';

  @override
  String get barcodeLabel => 'Barcode';

  @override
  String get retail => 'Retail';

  @override
  String get wholesale => 'Wholesale';

  @override
  String get wholesaleOn =>
      'Wholesale sale: items with a wholesale price at wholesale.';

  @override
  String get wholesaleNone =>
      'Items in this receipt have no wholesale price — set it in “Product — price and stock”.';

  @override
  String get wholesaleTag => 'wholesale';

  @override
  String get cashJournal => 'Cash in and out this shift';

  @override
  String get noCashOps => 'No cash in or out yet.';

  @override
  String get themeLight => 'Light';

  @override
  String get themeDark => 'Dark';

  @override
  String get themeSystem => 'As in the system';

  @override
  String get appearance => 'Appearance';

  @override
  String get searchAll => 'Product, customer or section';

  @override
  String get sectionsTitle => 'Sections';

  @override
  String get productsTitle => 'Products';

  @override
  String get clientsTitle => 'Customers';

  @override
  String get openAction => 'Open';

  @override
  String get returnAction => 'Restore';

  @override
  String addedToCheckName(String name) {
    return 'Added to receipt: $name';
  }

  @override
  String get receiveWithPrices => 'Receiving with prices';

  @override
  String get receiveWithPricesSub => 'Supplier, cost, price, new items';

  @override
  String get noSupplier => 'No supplier';

  @override
  String get newProduct => 'New product';

  @override
  String barcodeNotInStock(String code) {
    return 'Barcode $code is not in stock';
  }

  @override
  String get foundInBase => 'Found in the shared base by barcode — check it';

  @override
  String get unitLabel => 'Unit';

  @override
  String get createAndAdd => 'Create and add';

  @override
  String markupRule(String pct) {
    return 'Sale prices: cost +$pct %, round to 5';
  }

  @override
  String get payNowCash => 'Paid now';

  @override
  String get onCredit => 'On credit';

  @override
  String purchaseSum(String sum) {
    return 'Cost $sum som';
  }

  @override
  String get postReceipt => 'Post the receipt';

  @override
  String receiptPosted(int n) {
    return 'Receipt posted: $n items';
  }

  @override
  String partlyFailed(String text) {
    return 'Failed: $text';
  }

  @override
  String get shiftsOpen => 'Open';

  @override
  String get shiftsClosed => 'Closed';

  @override
  String get debtAccessTitle => 'Credit sale access';

  @override
  String get debtAccessHint =>
      'Which employees may sell on credit: allowed, not allowed or with a limit — receipts and amount per day. Rules are shared by all tills and phones of the company. The owner and administrators have no limits.';

  @override
  String get debtAllStaff => 'All employees';

  @override
  String get debtSameAsAll => 'Same as all';

  @override
  String get debtAllow => 'Allowed';

  @override
  String get debtDeny => 'Not allowed';

  @override
  String get debtLimit => 'With a limit';

  @override
  String get debtReceiptsPerDay => 'Credit receipts per day';

  @override
  String get debtSumPerDay => 'Credit amount per day, som';

  @override
  String get debtNoLimit => 'no limit';

  @override
  String get debtAccessSaved =>
      'Saved — the rules apply on all tills and phones now.';

  @override
  String get debtStaffNotLoaded =>
      'Employee list not loaded — you can set the rule for everyone.';

  @override
  String get debtDenied =>
      'The owner has not allowed you to sell on credit. Choose another payment method or call the owner.';

  @override
  String debtLimitReceipts(int a, int b) {
    return 'Today\'s credit sales limit is used up: $a of $b receipts. Contact the owner.';
  }

  @override
  String debtLimitSum(String left, String max, String sum) {
    return 'Today\'s credit limit exceeded: $left som left of $max som, this debt is $sum som. Take part of the payment now or call the owner.';
  }

  @override
  String debtReceiptsOf(int a, int b) {
    return 'receipts $a of $b';
  }

  @override
  String debtSumOf(String used, String max) {
    return 'debt $used of $max som';
  }

  @override
  String debtYourLimit(String text) {
    return 'Your credit limit today: $text';
  }

  @override
  String get promoTag => 'promo';

  @override
  String get promoNoManualDiscount =>
      'This product is on promotion — the server applies its discount; a manual discount cannot be given on this line.';

  @override
  String get quoteSearch => 'Number, customer or phone';

  @override
  String get quotesEmpty => 'No offers or invoices yet';

  @override
  String get quotesEmptyHint =>
      'Tap “Create”: choose a customer and products — the PDF goes to WhatsApp or email.';

  @override
  String get quotesNoMatch => 'Nothing found — change the search or filter.';

  @override
  String get createDoc => 'Create';

  @override
  String get makeInvoice => 'Make invoice';

  @override
  String get sendToClient => 'Send';

  @override
  String docWaText(String doc, String number, String sum, String until) {
    return '$doc No. $number for $sum som. $until.';
  }

  @override
  String get docSubtotal => 'Before discount';

  @override
  String get discountShort => 'Discount';

  @override
  String get paidLabel => 'Paid';

  @override
  String get leftToPay => 'Left to pay';

  @override
  String get findClient => 'Find customer';

  @override
  String get companyLabel => 'Company (LLC, sole trader)';

  @override
  String get clientExists =>
      'A customer with this phone is already in NurCRM — selected.';

  @override
  String get clientSaved => 'Customer saved to NurCRM';

  @override
  String get clientLocalOnly =>
      'No connection to NurCRM — the customer is saved only in this document.';

  @override
  String get clientNameRequired => 'Enter a name or company';

  @override
  String get innDigits => 'Tax ID — digits only, usually 14';

  @override
  String get discountHint => 'Amount or percent, e.g. 10%';

  @override
  String get stExpired => 'Expired';

  @override
  String deleteDocAsk(String number) {
    return 'Delete $number?';
  }

  @override
  String get deleteDocAskSub =>
      'The document will also be removed from the owner\'s program, for good.';

  @override
  String get requisitesTitle => 'Invoice details';

  @override
  String get requisitesHint =>
      'Bank, account, BIC, phone — printed in the PDF under the shop name.';

  @override
  String scannedAdded(String name) {
    return 'Added: $name';
  }

  @override
  String invoiceCreated(String number) {
    return 'Invoice $number created';
  }

  @override
  String get historyLabel => 'History';

  @override
  String get docLines => 'Products and services';

  @override
  String docFrom(String date) {
    return 'of $date';
  }

  @override
  String get clientNotChosen => 'No customer chosen';

  @override
  String get linesEmptyHint =>
      'Add products: search, scanner or from the current receipt.';

  @override
  String get quotesSyncing => 'Updating the shared list…';

  @override
  String get quotesShared => 'Shared list with the owner\'s program';

  @override
  String quotesSharedAt(String time) {
    return 'Shared list updated: $time';
  }

  @override
  String get quotesOffline =>
      'No connection to the server — showing the phone\'s copy';

  @override
  String quotesNotSent(int n) {
    return 'Not sent: $n. Will reach the owner\'s program when online — tap to retry.';
  }

  @override
  String get quotesForbidden =>
      'No rights to the company\'s shared settings — changes stay on this phone. Sign in as the owner or an administrator.';

  @override
  String get notSentMark => 'not sent';

  @override
  String quoteChangedElsewhere(String number) {
    return '$number was changed later elsewhere — showing the latest version';
  }

  @override
  String quoteRenumbered(String from, String to) {
    return 'Number $from is taken — the document is now $to';
  }

  @override
  String get payOutcomeUnknown =>
      'The server did not reply — the sale may have gone through. Tap “Pay” again without changing anything: a repeat will not be posted twice. Debt and bonus redemption need a connection.';

  @override
  String get debtDueLabel => 'Repay by';

  @override
  String debtDueDays(int n) {
    return '$n days';
  }

  @override
  String get debtDueNotSaved =>
      'The debt due date was not saved on the server — fix it on the website (default is 30 days).';

  @override
  String returnAmount(String sum) {
    return 'Refund amount: $sum som';
  }

  @override
  String returnGive(String what) {
    return 'Give the customer: $what';
  }

  @override
  String returnInCash(String sum) {
    return 'in cash $sum som';
  }

  @override
  String returnToCard(String sum) {
    return 'to the card/transfer $sum som';
  }

  @override
  String returnDebtReduced(String sum) {
    return 'The client\'s debt is reduced by $sum som — this money is not paid out.';
  }

  @override
  String returnDebtUnknown(String sum) {
    return 'Warning: the client\'s remaining debt was not received from the server. By calculation the debt is reduced by $sum som, but if the client has already paid off this receipt\'s debt, up to $sum som must be refunded. Check the client\'s debt before paying out.';
  }

  @override
  String get returnViaBank =>
      'Card/transfer part — refund through the bank, not from the till.';

  @override
  String returnPaidInfo(String paid, String done) {
    return 'Paid for the receipt: $paid som · already refunded: $done som';
  }

  @override
  String returnedQty(String qty) {
    return 'already returned: $qty';
  }

  @override
  String get returnNothingLeft =>
      'Everything on this receipt has already been returned.';

  @override
  String cashOutTooMuch(String sum) {
    return 'The withdrawal is more than the drawer holds ($sum som).';
  }

  @override
  String payDebtToDebt(String sum) {
    return 'Towards the debt: $sum som';
  }

  @override
  String get payDebtOverCard => 'By transfer — no more than the debt.';

  @override
  String receiveNotAccepted(String reason) {
    return 'The receipt of goods was not accepted by the server: $reason. The record is in “Sync”.';
  }

  @override
  String get auditApply => 'Post the stock count';

  @override
  String get auditApplyHint =>
      'Stock will be set to the counted quantities — like a stock count on the PC till. A connection is needed.';

  @override
  String auditApplyAsk(int n, int a) {
    return 'Products checked: $n, differences: $a. Stock will be set to the counted quantities. Post?';
  }

  @override
  String get auditApplied => 'Stock count posted — stock updated.';

  @override
  String get auditApplyQueued =>
      'The stock count act is created — posting will be sent when online.';

  @override
  String get auditOnlineOnly =>
      'A stock count is posted only with a server connection — the count is saved, try again later.';

  @override
  String auditContinue(int n) {
    return 'Continue the count ($n products)';
  }

  @override
  String get countedCash => 'Counted';

  @override
  String get openingCashLabel => 'At shift start';

  @override
  String closeShiftFailed(int n) {
    return 'The server did not accept $n receipts — they will not be in the shift report. Send them again or remove them in “Sync”.';
  }

  @override
  String get closeShiftAsk => 'Close the shift?';

  @override
  String get closeShiftAskSub =>
      'After closing, no sales can be made in this shift.';

  @override
  String get xReport => 'X report';

  @override
  String get foreignShiftHint =>
      'Another cashier\'s shift is open on this till — you cannot sell in it. Open your own shift.';

  @override
  String get loadMore => 'More';

  @override
  String get dropRow => 'Remove';

  @override
  String get dropRowAsk =>
      'Remove the receipt from the queue? It will not reach the server, the goods return to the phone\'s stock. If money was taken, give it back or ring the sale up again.';

  @override
  String get dropRowAskStock =>
      'Remove the record from the queue? It will not reach the server — check the product stock manually.';

  @override
  String get otherAccount => 'other account';

  @override
  String otherAccountHint(String email) {
    return 'Recorded under the sign-in $email — will be sent after signing in with that login.';
  }

  @override
  String pAStockUnclear(String name, String now, String before) {
    return '$name: the previous attempt got no reply from the server, and the stock has changed since (now $now, before adding it was $before). It was NOT added again to avoid doubling. Check the stock with a stock count: if it was added, remove the line; if not, remove it and add the product again.';
  }

  @override
  String get pAWeightLabel =>
      'This is a scale label (the weight of one purchase) — it is not used in receiving. Scan the product barcode or find it by name.';

  @override
  String get pANoServerCheck =>
      'No connection to the server — cannot check whether a product with this barcode already exists. Try again when online.';

  @override
  String pALinesLeft(int n) {
    return 'Lines not received: $n — they stay in the list, fix them and post again.';
  }

  @override
  String get pAPluLabel => 'PLU for the scale';

  @override
  String get pAPluHint => 'empty — assigned automatically';

  @override
  String get pAStockAdd => 'Add to stock';

  @override
  String get pAStockSet => 'Set stock';

  @override
  String get pAStockAddHint =>
      'The quantity from the file is added to the stock, like a delivery.';

  @override
  String get pAStockSetHint =>
      'The stock becomes equal to the quantity in the file (replaced, not added).';

  @override
  String pADupRow(int n) {
    return 'Repeats row $n — skipped';
  }

  @override
  String get pAUploaded => 'uploaded';

  @override
  String get rightCashier => 'Cashier interface';

  @override
  String get rightOrders => 'Website orders';

  @override
  String get rightProcurement => 'Purchasing';

  @override
  String get rightSuppliers => 'Suppliers';

  @override
  String get rightSelfLocked =>
      'You cannot turn off your own rights — the owner or another administrator does it.';

  @override
  String get staffYou => 'this is you';

  @override
  String get screenUnavailable =>
      'The screen was reopened and its data was lost. Go back and open it again.';

  @override
  String get cancelOrder => 'Cancel order';

  @override
  String cancelOrderAsk(String number) {
    return 'Cancel order #$number? The customer will not receive it.';
  }

  @override
  String pickShortAsk(String got, String need) {
    return '$got of $need picked. Mark the order as picked with items missing?';
  }

  @override
  String pickupCodeLabel(String code) {
    return 'Pickup code: $code';
  }

  @override
  String get torchToggle => 'Flashlight';

  @override
  String get payTransferQr => 'Transfer / QR';

  @override
  String get reportLoadFailed =>
      'The report did not load. Check the connection and tap “Retry”.';

  @override
  String get rentalLatePerDay => 'Late fee per day';

  @override
  String rentalLateHint(int n, String rate, String sum) {
    return 'Overdue $n d × $rate = $sum som (per the rental terms)';
  }

  @override
  String rentalBackDeposit(String sum) {
    return 'Give back the deposit: $sum som';
  }

  @override
  String rentalWithheld(String sum) {
    return 'withheld $sum som';
  }

  @override
  String rentalBackDocument(String doc) {
    return 'Give back the document: $doc';
  }

  @override
  String rentalExcess(String sum) {
    return 'Penalty above the deposit: $sum som — will be added to the receipt';
  }

  @override
  String rentalPenaltyLine(int n) {
    return 'Rental #$n penalty';
  }

  @override
  String rentalPenaltyInCart(String sum) {
    return 'Return accepted. The $sum som penalty is in the receipt — take the payment.';
  }

  @override
  String get waybillDoc => 'Waybill';

  @override
  String get waybillsFilter => 'Waybills';

  @override
  String get docPayMarkOnly =>
      'This is only a mark in the document: money is not recorded in the shift till and the customer\'s debt in NurCRM is not changed. To put money through the till, use \"Make the sale\".';

  @override
  String get docPayMarkOk => 'Mark as paid';

  @override
  String get docSellCartBusy =>
      'The receipt already has items. Replace them with the document\'s items?';

  @override
  String get docSellReplace => 'Replace';

  @override
  String get docSentToSale => 'Sent to the sale';

  @override
  String docSellMissing(String names) {
    return 'Not in the phone\'s catalogue — add manually: $names';
  }

  @override
  String docVatIncl(String p) {
    return 'Incl. VAT $p%';
  }

  @override
  String get prnQr => 'QR code on the receipt';

  @override
  String get prnQrHint =>
      'Fiscal check or e-receipt link. If the printer prints stray characters instead of the QR, turn it off.';

  @override
  String get prnDrawer => 'Cash drawer via the printer';

  @override
  String get prnDrawerHint =>
      'Opens after a cash sale (the drawer is cabled to the printer).';

  @override
  String prnDrawerPin(int n) {
    return 'Pin $n';
  }

  @override
  String get prnDrawerTest => 'Open the drawer';

  @override
  String get prnDrawerSent =>
      'Drawer command sent. If it did not open, choose the other pin.';

  @override
  String get stepPhoto => 'Photos';

  @override
  String get stepBarcode => 'Barcode';

  @override
  String get bindTitle => 'Photos saved ✓ — now scan the product barcode';

  @override
  String get bindSub => 'If the product has no barcode, tap “Skip”';

  @override
  String get bindSkip => 'Skip';

  @override
  String get bindManual => 'Enter manually';

  @override
  String get bindManualHint => 'Digits under the barcode';

  @override
  String get bindChecking => 'Checking the barcode…';

  @override
  String bindWillLink(String code, String name) {
    return 'Barcode $code will be linked to “$name”';
  }

  @override
  String bindWillLinkNew(String code) {
    return 'Barcode $code will be linked to the new product';
  }

  @override
  String bindAlreadyOwn(String code, String name) {
    return 'Barcode $code is already linked to “$name” — nothing to change';
  }

  @override
  String get bindTakenTitle => 'The barcode belongs to another product';

  @override
  String bindTakenAsk(String code, String name) {
    return 'Barcode $code is already linked to “$name”.\n“Replace” — the barcode moves to this product.\n“Keep” — it stays with “$name”, this product gets no barcode.';
  }

  @override
  String bindTakenAskNew(String code, String name) {
    return 'Barcode $code is already linked to “$name” — maybe this product is already in stock.\n“Keep” — the barcode stays with “$name”, the new product gets no barcode.';
  }

  @override
  String get bindReplace => 'Replace';

  @override
  String get bindKeep => 'Keep';

  @override
  String get bindRescan => 'Scan again';

  @override
  String bindLinked(String code, String name) {
    return 'Barcode $code linked to “$name”';
  }

  @override
  String get teachNext =>
      'Shoot the product from 3–5 sides, then “Done” — the barcode comes next';

  @override
  String get teachNextNoCode =>
      'Shoot the product from 3–5 sides, then tap “Done”';

  @override
  String get liveNextSale =>
      'Found products appear below — tap one to add it to the receipt';

  @override
  String get liveNextList =>
      'After the shot the camera shows what it found — you can correct the list';

  @override
  String get scanCodesHint =>
      'Point the frame at a barcode — the code is saved automatically. Then tap “Done”';

  @override
  String get weighNext =>
      'The camera recognises the item, reads the weight and adds it to the receipt';

  @override
  String get weighManualBtn =>
      'Without the camera — choose the item and enter the weight';

  @override
  String get peEdit => 'Edit product';

  @override
  String get peName => 'Name';

  @override
  String get peMinQty => 'Minimum stock';

  @override
  String get peMinQtyHint => 'Below this the product shows in \"Running low\"';

  @override
  String get peStock => 'Stock';

  @override
  String get peStockHint =>
      'Stock changes only by a document — receipt, write-off or stock count with a reason, as on the PC. This keeps the numbers consistent.';

  @override
  String get peIncome => 'Receipt';

  @override
  String get peWriteOff => 'Write-off';

  @override
  String get peRecount => 'Stock count';

  @override
  String get peQtyIn => 'How many arrived';

  @override
  String get peQtyOut => 'How many to write off';

  @override
  String get peQtyFact => 'Actual quantity';

  @override
  String get peReason => 'Reason';

  @override
  String get peReasonNeed => 'Enter the reason.';

  @override
  String get peReasonExpired => 'Expired';

  @override
  String get peReasonDefect => 'Defect';

  @override
  String get peReasonSpoiled => 'Spoiled';

  @override
  String get peReasonTheft => 'Theft';

  @override
  String get peReasonOther => 'Other';

  @override
  String get peReasonSupplier => 'Delivery';

  @override
  String get peReasonCount => 'Recount';

  @override
  String get peApply => 'Post';

  @override
  String peStockDone(String from, String to) {
    return 'Stock: $from → $to';
  }

  @override
  String peStockSame(String qty) {
    return 'Stock is already $qty — nothing changed.';
  }

  @override
  String get peStockQueued =>
      'The document is created — it will be posted automatically when online.';

  @override
  String get peStockOffline =>
      'No connection to the server — stock not received, nothing changed.';

  @override
  String peStockBelowZero(String qty) {
    return 'You can\'t write off more than the stock ($qty).';
  }

  @override
  String get peSaved => 'Product saved.';

  @override
  String get peNoChanges => 'No changes.';

  @override
  String pePriceRestored(String price) {
    return 'The server recalculated the selling price from the markup — your price $price was restored.';
  }

  @override
  String get pePriceLocked =>
      'Prices are changed by staff with the \"Price change\" permission.';

  @override
  String get peLoadFailed =>
      'The product card did not load from the server — editing works when online.';

  @override
  String get peNameNeed => 'Enter the name.';

  @override
  String peBarcodeTaken(String code, String name) {
    return 'Barcode $code already belongs to \"$name\".';
  }

  @override
  String peBarcodeTwice(String code) {
    return 'Barcode $code is entered twice.';
  }

  @override
  String get peScan => 'Scan';

  @override
  String pePackQty(String qty) {
    return '$qty per pack';
  }

  @override
  String get peNoAccess =>
      'Products can be edited by staff with the \"Stock\" permission or an administrator.';
}
