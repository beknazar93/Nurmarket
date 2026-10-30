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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n receipts',
      one: '$n receipt',
    );
    return '$_temp0';
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
      'No connection to the server — sales are saved on the phone. They will be sent as soon as there is a connection: right away if the app is open, in the background about every 15 minutes, or the next time you open it.';

  @override
  String runningOut(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n products running out',
      one: '$n product running out',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n items',
      one: '$n item',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n variants · $qty pcs',
      one: '$n variant · $qty pcs',
    );
    return '$_temp0';
  }

  @override
  String variantsSaved(int n) {
    return 'Variants saved: $n';
  }

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
  String get dayToday => 'Today';

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
  String get chartBestDay => 'Best day';

  @override
  String get chartWorstDay => 'Weakest day';

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
  String get advisorOnline => 'Answers from your shop data';

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
      'There is no AI key. Add it on this phone in “AI settings” → “Gemini keys” (or in the owner\'s program: Nur Advisor → ⚙ → AI models and keys)';

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
      'No microphone access. Allow the microphone for NurCRM in the phone settings.';

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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Upload $n products to stock',
      one: 'Upload $n product to stock',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'To pay · $n items',
      one: 'To pay · $n item',
    );
    return '$_temp0';
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
      'The receipt is saved on the phone. It will be sent when there is a connection: right away if the app is open, in the background in about 15 minutes, or the next time you open it. It will not be posted twice.';

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
      'Receipt saved and waiting to be sent: it goes out when there is a connection (app open — at once, closed — in the background or on the next open)';

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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n products · $qty pcs',
      one: '$n product · $qty pcs',
    );
    return '$_temp0';
  }

  @override
  String wasPieces(String qty) {
    return 'was $qty';
  }

  @override
  String get unknownBarcode => 'Unknown barcode';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · not in your catalog (checked on the server) or the NurCRM directory. Add the product — the code is not sent anywhere';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'since $time · $n receipts',
      one: 'since $time · $n receipt',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other:
          '$n receipts must be sent to the server first — a connection is needed.',
      one:
          '$n receipt must be sent to the server first — a connection is needed.',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other:
          'There are $n unsent records on the phone. They stay after signing out and will be sent at the next sign-in to this account. Sign out?',
      one:
          'There is $n unsent record on the phone. It stays after signing out and will be sent at the next sign-in to this account. Sign out?',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n items · time to order',
      one: '$n item · time to order',
    );
    return '$_temp0';
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
      'The stock count act is created but NOT posted (no connection). Count and post again — the act stays a draft.';

  @override
  String get auditOnlineOnly =>
      'A stock count is posted only with a server connection — the count is saved, try again later.';

  @override
  String auditContinue(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Continue the count ($n products)',
      one: 'Continue the count ($n product)',
    );
    return '$_temp0';
  }

  @override
  String get countedCash => 'Counted';

  @override
  String get openingCashLabel => 'At shift start';

  @override
  String closeShiftFailed(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other:
          'The server did not accept $n receipts — they will not be in the shift report. Send them again or remove them in “Sync”.',
      one:
          'The server did not accept $n receipt — it will not be in the shift report. Send it again or remove it in “Sync”.',
    );
    return '$_temp0';
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
      'The document is created but NOT posted (no connection). Count and post again.';

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
  String get bcGenTooltip => 'Generate barcode';

  @override
  String get bcGenReplace => 'Replace the current barcode with a new one?';

  @override
  String get bcGenDo => 'Generate';

  @override
  String pePackQty(String qty) {
    return '$qty per pack';
  }

  @override
  String get peNoAccess =>
      'Products can be edited by staff with the \"Stock\" permission or an administrator.';

  @override
  String get smartNoLink =>
      'No connection to the server — nothing was posted. The list stays on screen; retry when the internet is back.';

  @override
  String smartPartial(int done, int total, int left) {
    return 'Posted $done of $total, $left not posted — press Upload again: what was already posted will not be added twice.';
  }

  @override
  String get smartPendingReceipt =>
      'The previous supplier receipt got no reply — repeat it first via “Receive with supplier”, otherwise the stock may be added twice.';

  @override
  String get smartPendingStock =>
      'Some rows got no reply from the server — press Upload again first: that way the stock will not be doubled.';

  @override
  String get smartRowLocked =>
      'This row was already sent to the server — the product and name cannot be changed, retry the upload.';

  @override
  String smartRowRejected(String reason) {
    return 'Rejected: $reason';
  }

  @override
  String get smartRowUnknown =>
      'No reply from the server — retry: the stock will not be doubled.';

  @override
  String payDebtLeft(String sum) {
    return 'Debt remaining: $sum';
  }

  @override
  String get debtReceiptTitle => 'Debt payment receipt';

  @override
  String get returnsTitle => 'Returns';

  @override
  String get shDebtPayments => 'Debt payments';

  @override
  String get shDebtPrepay => 'Prepayment on debt';

  @override
  String get exitPressAgain => 'Press again to exit';

  @override
  String get createProductWithCode => 'Create a product with this code';

  @override
  String get printLabel => 'Print a label';

  @override
  String get openingCashField => 'Cash in the drawer at shift start';

  @override
  String get advisorGreeting =>
      'Hello! I am Nur Advisor. Ask about sales, debts or stock — I will answer from your store\'s data.';

  @override
  String get a11yBack => 'Back';

  @override
  String get a11yShowPassword => 'Show password';

  @override
  String get a11yHidePassword => 'Hide password';

  @override
  String get a11yScanner => 'Scanner';

  @override
  String get a11yCallAi => 'Call the AI';

  @override
  String get stockNone => 'none';

  @override
  String get cartEmptyHint => 'Add an item: search, scan or tap a tile';

  @override
  String get toProductsBtn => 'To products';

  @override
  String get closeBtn => 'Close';

  @override
  String clientDebtLine(String sum) {
    return 'Debt: $sum som';
  }

  @override
  String get apiNoToken => 'The server did not return a token.';

  @override
  String get apiSessionExpired => 'Session expired — sign in again.';

  @override
  String get apiTimeout => 'The server did not respond in time.';

  @override
  String get apiNoConnection => 'No connection to the server.';

  @override
  String apiServerError(String code) {
    return 'Server error $code.';
  }

  @override
  String get apiNetworkError => 'Network error.';

  @override
  String get adminNoLink => 'no connection';

  @override
  String printLabelAsk(String name, String code) {
    return 'Print a label for \"$name\"? Barcode $code';
  }

  @override
  String get notNow => 'Not now';

  @override
  String get bcGenTaken =>
      'That code is already taken on the server — a new one was issued.';

  @override
  String get tapePrint => 'Print';

  @override
  String get tapeSend => 'Send';

  @override
  String get zReport => 'Shift Z-report';

  @override
  String get xReportNote => 'X-report is interim: the shift is not closed.';

  @override
  String get shSalesBlock => 'Sales';

  @override
  String get shCashBlock => 'Cash in the drawer';

  @override
  String get shPrepayIncluded =>
      'included in cash/non-cash: prepayment on debt';

  @override
  String get shReturnsUnknown => 'Shift returns not loaded — no connection.';

  @override
  String shReturnsPartial(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n returns',
      one: '$n return',
    );
    return 'Partly: $_temp0 with no cashier in the server reply — not counted. Check on the site.';
  }

  @override
  String returnsCountN(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n returns',
      one: '$n return',
    );
    return '$_temp0';
  }

  @override
  String get shReturnsCash => 'Cash refunds';

  @override
  String get shCalcRow => 'Sum of the rows';

  @override
  String shMismatch(String sum) {
    return 'The row total differs from the server by $sum som — rely on the server figure.';
  }

  @override
  String get shNoSplit =>
      'Breakdown of returns and debt payments is unavailable — the expected cash is the server figure.';

  @override
  String get shPartial =>
      'Some data did not load (no connection) — refresh the screen.';

  @override
  String get debtReceiptBefore => 'Debt before payment';

  @override
  String get debtReceiptMethod => 'Payment method';

  @override
  String get debtReceiptAccepted => 'Accepted';

  @override
  String get debtReceiptCleared => 'The debt is fully paid.';

  @override
  String debtReceiptAcceptedLine(String sum, String left) {
    return 'Accepted $sum, remaining $left';
  }

  @override
  String get returnReasonTitle => 'Return reason (optional)';

  @override
  String get returnReasonLabelShort => 'Reason';

  @override
  String get returnReasonDefect => 'Defect';

  @override
  String get returnReasonNotFit => 'Did not fit';

  @override
  String get returnReasonCashier => 'Cashier error';

  @override
  String get listCachedNote => 'No connection — showing the last saved list.';

  @override
  String get checksLoadFailed => 'Could not load receipts';

  @override
  String get debtsLoadFailed => 'Could not load debts';

  @override
  String get callMinimize => 'Minimize';

  @override
  String get callAiListens => 'AI is listening';

  @override
  String get callInterrupted => 'Interrupted — listening';

  @override
  String get callExtend => '10 more minutes';

  @override
  String get callExtended => 'The call is extended by 10 minutes';

  @override
  String get callNoLink => 'No connection to AI';

  @override
  String get callNoLinkWork => 'No connection to AI — work on the screen';

  @override
  String callNoLinkEnds(int n) {
    return 'Ends in $n s if the connection does not return';
  }

  @override
  String get callNoLinkHelp => 'The cart, search and stock work without AI.';

  @override
  String get callSlowServer => 'The server is slow to answer…';

  @override
  String get callStillLoading =>
      'The data is still loading — I will tell you when it arrives';

  @override
  String get callSearching => 'Searching…';

  @override
  String get callSpeakingShort => 'Speaking…';

  @override
  String get callHush => 'Quiet';

  @override
  String get callOpenFull => 'Call with Nur Advisor — expand; hold to silence';

  @override
  String aiSays(String text) {
    return 'AI: $text';
  }

  @override
  String get aiUndo => 'Undo';

  @override
  String get aiCantUndo => 'cannot be undone';

  @override
  String get aiPickTitle => 'Which one?';

  @override
  String get aiAmbiguousHint => 'Say the number or tap';

  @override
  String get aiNotThat => 'None of these';

  @override
  String aiYesNumber(String num) {
    return 'Yes, $num';
  }

  @override
  String get aiNoRepeat => 'No, I\'ll say it again';

  @override
  String aiConfirmDo(String what) {
    return 'Do: $what';
  }

  @override
  String get aiDo => 'Do it';

  @override
  String get aiDont => 'Don\'t';

  @override
  String get aiOrSayYes => 'or say \"yes\"';

  @override
  String get aiModeAskMoney => 'Ask before money';

  @override
  String get aiWhatPay => 'payment';

  @override
  String get aiWhatReturn => 'refund';

  @override
  String get aiWhatReceive => 'posting a goods receipt';

  @override
  String get aiWhatWriteOff => 'write-off';

  @override
  String get aiWhatCount => 'stock count';

  @override
  String get aiWhatCloseShift => 'closing the shift';

  @override
  String get aiWhatSalary => 'payout to an employee';

  @override
  String get aiWhatDebt => 'debt payment';

  @override
  String get aiWhatDelete => 'deleting a product';

  @override
  String get aiWhatPrice => 'price change';

  @override
  String get aiWhatCash => 'cash in or out';

  @override
  String get aiWhatOther => 'action';

  @override
  String get tblBigger => 'Larger';

  @override
  String get tblSmaller => 'Smaller';

  @override
  String get tblToChat => 'To chat';

  @override
  String get tblPdf => 'PDF';

  @override
  String get tblShare => 'Share';

  @override
  String get tblPin => 'Pin';

  @override
  String get tblPinned => 'Pinned';

  @override
  String tblMore(int n) {
    return 'Show $n more';
  }

  @override
  String tblMoreLeft(int n) {
    return '$n left';
  }

  @override
  String get tblAllShown => 'All rows are shown';

  @override
  String tblSource(String source, String time) {
    return 'app data · $source · $time';
  }

  @override
  String get tblEmpty => 'Nothing here yet';

  @override
  String tblEmptyFilter(String q) {
    return 'Nothing found for \"$q\"';
  }

  @override
  String get tblClearFilter => 'Clear filter';

  @override
  String get tblOpenObject => 'Open';

  @override
  String tblRowsN(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n rows',
      one: '$n row',
    );
    return '$_temp0';
  }

  @override
  String get tblPrev => 'Previous table';

  @override
  String get tblNext => 'Next table';

  @override
  String get tblCollapse => 'Collapse the panel';

  @override
  String get tblClose => 'Close the table';

  @override
  String get tblSentChat => 'The table is added to the chat';

  @override
  String get tblPdfFailed => 'Could not make the PDF';

  @override
  String get vcCamera => 'Camera';

  @override
  String get vcCameraOn => 'Camera on';

  @override
  String get vcCameraOff => 'Camera off';

  @override
  String get vcSwitchCamera => 'Other camera';

  @override
  String get vcTorch => 'Flashlight';

  @override
  String get vcRecording => 'Camera';

  @override
  String get vcAiSees => 'AI sees';

  @override
  String get vcEconomy => 'Data saver';

  @override
  String get vcEconomyHint => '1 frame every 3 seconds, only while you speak';

  @override
  String get vcStarting => 'Turning the camera on…';

  @override
  String get vcSearching => 'Searching the catalog…';

  @override
  String get vcWhatIsIt => 'What is this?';

  @override
  String get vcInvoice => 'Invoice';

  @override
  String get vcShelf => 'Shelf';

  @override
  String get vcPriceTag => 'Price tag';

  @override
  String get wizAddProduct => 'Add a product';

  @override
  String get wizTitle => 'Adding a product';

  @override
  String get wizSlotFront => 'Front';

  @override
  String get wizSlotBack => 'Back';

  @override
  String get wizSlotSide => 'Side';

  @override
  String get wizSlotCode => 'Barcode';

  @override
  String wizShootSide(String side) {
    return 'Shoot: $side';
  }

  @override
  String get wizCreate => 'Create product';

  @override
  String wizDraft(String name, String price) {
    return '\"$name\" · $price som';
  }

  @override
  String get wizHint => 'Say \"ready\" or tap Shoot';

  @override
  String wizDuplicate(String name) {
    return 'Already exists: \"$name\"';
  }

  @override
  String wizPhotoAlt(String side) {
    return 'Shot: $side';
  }

  @override
  String get wizEnterCode => 'Enter barcode';

  @override
  String get wizEnterCodeHint => 'Scan it with a scanner or type the digits';

  @override
  String get vcCardProduct => 'Product';

  @override
  String get vcNotFound =>
      'Not found in the catalog. Move closer or show the barcode';

  @override
  String get vcExact => 'exact';

  @override
  String get vcByBarcode => 'barcode';

  @override
  String get vcByPhoto => 'by photo';

  @override
  String get vcLooksLike => 'Looks like:';

  @override
  String get vcPickHint => 'tap to choose';

  @override
  String vcPrice(String sum) {
    return 'Price: $sum som';
  }

  @override
  String vcStock(String qty) {
    return 'In stock: $qty';
  }

  @override
  String vcInvoiceSummary(int n, int known, int fresh) {
    return 'Rows: $n · in catalog: $known · new: $fresh';
  }

  @override
  String vcOpenReceive(int n) {
    return 'Open receiving ($n)';
  }

  @override
  String get vcReceiveEndsCall =>
      'The call will end, the conversation stays in the chat';

  @override
  String get vcNewItemsHint =>
      'New items are not in the catalog — add them via \"Stock → Invoice\"';

  @override
  String get vcNoProcure => 'The administrator posts the receipt';

  @override
  String get vcShelfHint =>
      'AI counts by eye — double-check before the stock count';

  @override
  String get vcShelfCols => 'seen · in the app';

  @override
  String get vcShelfThing => 'item';

  @override
  String get vcOnTag => 'On the tag';

  @override
  String get vcInProgram => 'In the app';

  @override
  String vcTagMismatch(String tag, String sum) {
    return 'The tag says $tag som, the app says $sum som — mismatch';
  }

  @override
  String get vcTagOk => 'The tag price matches the app';

  @override
  String get vcTagFixHint =>
      'We do not change the price here — use the product card';

  @override
  String vcMoreLines(int n) {
    return '$n more rows';
  }

  @override
  String get vcNewTag => 'new';

  @override
  String get vcConsentTitle => 'Turn the camera on in the call?';

  @override
  String get vcConsentBody =>
      'While the camera is on, frames (about 1 per second) go to Google Gemini using your store\'s AI key. They are not saved on the phone or in NurCRM — only the text of the conversation stays in the chat. On a free key Google may use the data to improve its models. Do not show customers\' faces, bank cards or other people\'s documents.';

  @override
  String get vcConsentPtSends =>
      'While the camera is on, frames (about 1 per second) go to Google Gemini using your store\'s AI key.';

  @override
  String get vcConsentPtSaved =>
      'They are not saved on the phone or in NurCRM — only the text of the conversation stays in the chat.';

  @override
  String get vcConsentPtFree =>
      'On a free key Google may use the data to improve its models.';

  @override
  String get vcConsentPtWarn =>
      'Do not show customers\' faces, bank cards or other people\'s documents.';

  @override
  String get vcConsentAgree => 'Turn the camera on';

  @override
  String get vcConsentLater => 'Not now';

  @override
  String vcCost(int n) {
    return 'A video call lasts up to $n min and uses the AI key limit several times faster than a voice call';
  }

  @override
  String get aiAccTitle => 'AI access';

  @override
  String get aiAccSection => 'Who may use AI';

  @override
  String aiAccLimitInfo(int n) {
    return 'The free AI limit (Google Gemini) is shared by the whole project: there are few requests per minute and per day. So AI — Nur Advisor, the call, the smart camera, invoice by photo, weighing by sight — can be used by at most $n store employees. Choose who is allowed.';
  }

  @override
  String aiAccCounter(int a, int b) {
    return '$a of $b';
  }

  @override
  String aiAccLimitReached(int n) {
    return 'You can choose at most $n employees. To give access to someone else, first untick one of the chosen.';
  }

  @override
  String aiAccDenied(int n, String names) {
    return 'AI is available only to $n store employees: $names. Please contact the owner.';
  }

  @override
  String get aiAccDeniedNone =>
      'AI is currently turned off for all store employees. Please contact the owner.';

  @override
  String get aiAccRecommend =>
      'We recommend limiting AI to two employees (shared free limit)';

  @override
  String get aiAccConfigure => 'Set up';

  @override
  String get aiAccSaved => 'AI access saved';

  @override
  String get aiAccOwnerTag => 'owner';

  @override
  String get aiAccYouTag => 'you';

  @override
  String get aiAccStaffNotLoaded =>
      'Employee list not loaded (no connection?). Only the already chosen are shown.';

  @override
  String get aiAccNobody =>
      'Nobody is chosen — AI will be turned off for all employees.';

  @override
  String get aiAccManagerOnly =>
      'Only the owner or an administrator can change the list.';

  @override
  String get aiAccNoticeTitle => 'AI — shared limit';

  @override
  String aiAccNoticeBody(int n) {
    return 'The store\'s AI runs on Google\'s shared free limit, so no more than $n employees have the right to it — you are one of them. Don\'t start long calls or unnecessary requests without need.';
  }

  @override
  String get aiAccGotIt => 'Got it';

  @override
  String get aiAccMineYes => 'You have access to AI';

  @override
  String get aiAccMineNo => 'AI is not available to you — contact the owner';

  @override
  String get aiAccMineOpen => 'AI is open to all employees (no limit set)';

  @override
  String get aiAccNotSet =>
      'Not set — AI is open to all employees (the owner and administrator always have it)';

  @override
  String get assistantTitle => 'Assistant';

  @override
  String get assistantSub => 'voice control mode';

  @override
  String get aiModeDo => 'Do on request';

  @override
  String get aiModeDoHint =>
      'Opens screens, reads, shows tables and does what you ask right away — says the result aloud and offers “Undo”.';

  @override
  String get aiModeAskHint =>
      'Same as on request, but before a payment, refund, goods receipt, write-off, stock count, closing the shift, payout, debt payment, deletion or price change there is a “Do it” card.';

  @override
  String get aiModeWatch => 'View only';

  @override
  String get aiModeWatchHint =>
      'Screens, reading and tables only. If you ask it to do something it answers: “In this mode I don\'t change data — do it on the screen or switch the mode.”';

  @override
  String get aiModeDefault => 'default';

  @override
  String get aiModeSofterLocked => 'Softer than the owner set — unavailable';

  @override
  String get aiModeOwnerSet => 'The owner set for everyone on this phone:';

  @override
  String get aiModeStricterSelf => 'For yourself you can choose a stricter one';

  @override
  String get aiModeNoVoice => 'The mode cannot be changed by voice — only here';

  @override
  String get aiModeAskWhatTitle => 'What it asks before';

  @override
  String get aiModeAskNote =>
      'A “Do: … amount …” card and one button — or say “yes”. No double confirmations.';

  @override
  String get aiModeFooter => 'The assistant\'s rights follow the user\'s role';

  @override
  String get aiModeSaved => 'Mode saved';

  @override
  String get aiLogTitle => 'AI actions';

  @override
  String get aiLogSubAll => 'all entries on this phone';

  @override
  String get aiLogMoneyOnly => 'Money only';

  @override
  String get aiLogUndone => 'Undone';

  @override
  String get aiLogByDate => 'Filter by date';

  @override
  String get dayYesterday => 'Yesterday';

  @override
  String aiLogUndoneAt(String time) {
    return 'undone $time';
  }

  @override
  String get aiLogCantUndoWriteOff => 'cannot be undone · you can write off';

  @override
  String get aiUndoRequest => 'cancellation — by request to the owner';

  @override
  String get aiLogUndoGone => 'undo was only in the call';

  @override
  String aiLogUndoneToast(String what) {
    return 'Undone: $what';
  }

  @override
  String get aiLogUndoConflict =>
      'Changed after the AI — please check on the screen';

  @override
  String aiLogUndoFailed(String what) {
    return 'Could not undo: $what. Check the connection and the screen.';
  }

  @override
  String get aiLogUndoShift =>
      'Cannot undo: the shift is closed or the drawer has less cash';

  @override
  String aiLogUndoPartial(int done, int total) {
    return 'Restored $done of $total; the rest changed after the AI or failed';
  }

  @override
  String get aiLogEmpty => 'The AI has not done anything on this phone yet';

  @override
  String get aiLogEmptyHint =>
      'Call Nur Advisor and say what to do — every action will appear here with an “Undo” button';

  @override
  String get aiLogCall => 'Call the advisor';

  @override
  String get aiLogMineOnly => 'Only your own actions are shown';

  @override
  String get aiLogFooter =>
      'Stored on this phone, up to 2000 entries. No AI keys, audio or customer phone numbers.';

  @override
  String get aiGroupNav => 'Screens';

  @override
  String get aiGroupCart => 'Receipt and orders';

  @override
  String get aiGroupMoney => 'Money';

  @override
  String get aiGroupProduct => 'Products';

  @override
  String get aiGroupTask => 'Tasks';

  @override
  String get aiGroupSalary => 'Salary';

  @override
  String get auditAi => 'AI';

  @override
  String get stfTasksTitle => 'Tasks';

  @override
  String stfTasksSub(int n, String late) {
    return 'Tasks: $n · overdue: $late';
  }

  @override
  String get stfMyTasksTitle => 'My tasks';

  @override
  String stfMySub(String name, String today, String late) {
    return '$name · for today: $today · overdue: $late';
  }

  @override
  String get stfFilterOverdue => 'Overdue';

  @override
  String get stfFilterDone => 'Done';

  @override
  String get stfStNew => 'new';

  @override
  String get stfStWork => 'in progress';

  @override
  String get stfStDone => 'done';

  @override
  String get stfOverdue => 'overdue';

  @override
  String get stfNoDue => 'no due date';

  @override
  String get stfDue => 'Due';

  @override
  String get stfRemind => 'Remind';

  @override
  String get stfUrgent => 'Urgent task';

  @override
  String get stfNewTask => 'New task';

  @override
  String get stfTaskField => 'What to do';

  @override
  String get stfTaskHint => 'For example: recount the water shelf';

  @override
  String get stfAssignee => 'Assignee';

  @override
  String get stfAssigneeHint => 'Choose an employee';

  @override
  String get stfCreate => 'Create task';

  @override
  String get stfNeedTitle => 'Write what needs to be done';

  @override
  String get stfNeedAssignee => 'Choose the assignee';

  @override
  String get stfTaskCreated => 'Task created';

  @override
  String get stfToWork => 'Start';

  @override
  String get stfMarkDone => 'Done';

  @override
  String get stfReopen => 'Reopen';

  @override
  String get stfEditDue => 'Change the due date';

  @override
  String get stfEditAssignee => 'Reassign';

  @override
  String get stfDelete => 'Delete';

  @override
  String stfDeleteAsk(String title) {
    return 'Delete the task \"$title\"?';
  }

  @override
  String get stfNoTasks => 'No tasks';

  @override
  String get stfLoadFail => 'Could not load the tasks';

  @override
  String get stfPriorityHint =>
      'Urgency and the reminder are only visible in the app for now; the task may show a service note on the site until the server is updated';

  @override
  String get stfNoBackground =>
      'There are no background notifications — the reminder shows when you open the app';

  @override
  String stfOverduePlate(int n, String title) {
    return 'Overdue: $n — \"$title\"';
  }

  @override
  String stfRemindPlate(String title) {
    return 'Reminder: \"$title\"';
  }

  @override
  String stfTodayPlate(String today, String late) {
    return 'Tasks for today: $today · overdue: $late';
  }

  @override
  String get stfSalaryTitle => 'Salary';

  @override
  String stfSalarySub(int n) {
    return 'employees: $n';
  }

  @override
  String get stfPeriodMonth => 'This month';

  @override
  String get stfPeriodLast => 'Last month';

  @override
  String get stfPeriodWeek => '7 days';

  @override
  String get stfAccrued => 'Accrued';

  @override
  String get stfPaid => 'Paid';

  @override
  String get stfLeft => 'Left';

  @override
  String get stfColEmployee => 'Employee';

  @override
  String get stfChecking => 'Checking payouts…';

  @override
  String get stfPartialPlate =>
      'Checked partially — the payout amount is not filled in automatically.';

  @override
  String get stfTrustTimeout =>
      'Checking past payouts did not finish in time — check on the site before paying.';

  @override
  String get stfTrustJournal =>
      'The payout log on the phone cannot be read — payouts are closed.';

  @override
  String get stfTrustNotChecked =>
      'Past payouts could not be checked — check on the site before paying.';

  @override
  String stfTrustUnassigned(String sum, int n) {
    return 'There are \"Salary\" expenses not tied to an employee: $sum som ($n) — check on the site.';
  }

  @override
  String get stfTrustPartial =>
      'Payouts without the \"Salary\" category or not all expenses are visible — check on the site before paying.';

  @override
  String get stfTrustPartialNotes =>
      'Some payouts only partly fall into the period — they are not subtracted, please check.';

  @override
  String get stfTrustByName =>
      'Some payouts were found only by the name in the expense title — check on the site before paying.';

  @override
  String stfPartialNote(String sum, String period) {
    return 'A payout of $sum som for $period only partly falls into this period — not subtracted';
  }

  @override
  String get stfSalaryEmpty =>
      'No accruals for the period: no sales or nobody has a pay scheme.';

  @override
  String get stfSalaryDenied =>
      'No access to salaries: this account does not have the employee analytics permission on the site.';

  @override
  String get stfSalaryFail => 'Could not load the salary';

  @override
  String get stfSalaryHint =>
      'Accrued is the server\'s calculation, as on the PC and the site. \"Paid\" is the \"Salary\" expenses for this period.';

  @override
  String get stfLockedTag => 'payout without reply';

  @override
  String get stfPayBtn => 'Pay out';

  @override
  String stfPayTitle(String name) {
    return 'Payout: $name';
  }

  @override
  String get stfKindSalary => 'Salary';

  @override
  String get stfKindAdvance => 'Advance';

  @override
  String get stfPayAmount => 'Amount';

  @override
  String get stfPayEnterAmount => 'Enter the amount yourself';

  @override
  String get stfPayBox => 'Cash register';

  @override
  String get stfPayComment => 'Comment (optional)';

  @override
  String get stfPayNeedAmount => 'Enter the payout amount';

  @override
  String get stfPayNoCash =>
      'No company cash register was found for the payout — pay on the site or in the owner program.';

  @override
  String get stfSalesBoxWarn =>
      'This is the branch sales cash register — the expense will reduce its balance.';

  @override
  String stfPayConfirm(String sum, String name, String box) {
    return 'Pay $sum som to $name from the \"$box\" cash register?';
  }

  @override
  String stfPayDone(String sum, String name) {
    return 'Paid $sum som: $name';
  }

  @override
  String get stfPayFailed => 'The payout failed.';

  @override
  String stfPayUnknown(String sum, String box) {
    return 'No reply from the server — the payout of $sum som (cash register \"$box\") may have gone through. Do not repeat it: open the employee and tap \"Check on the server\".';
  }

  @override
  String stfPayLock(String sum, String when, String box) {
    return 'The previous payout of $sum som ($when, cash register \"$box\") got no server reply. A new payout to this employee is closed until you check it.';
  }

  @override
  String get stfPayLockShort =>
      'The previous payout to this employee got no server reply — a new one is closed until you check it.';

  @override
  String get stfPayDup =>
      'The same payout has just gone through — it is not repeated.';

  @override
  String get stfPayJournal =>
      'The payout log on the phone cannot be read — without it there is no protection from a double payout, the payout was not sent.';

  @override
  String get stfLockCheck => 'Check on the server';

  @override
  String get stfLockCheckShort => 'Check';

  @override
  String get stfLockYes => 'The payout IS on the site';

  @override
  String get stfLockNo => 'The payout is NOT on the site';

  @override
  String get stfLockYesAsk => 'Is the payout on the site?';

  @override
  String get stfLockNoAsk => 'The payout is not on the site?';

  @override
  String get stfLockResolveHint =>
      'Check the \"Salary\" expense on the NurCRM site. If you choose wrongly, the payout will be counted wrongly or go through twice.';

  @override
  String get stfLockFound => 'The payout was found on the server and counted.';

  @override
  String get stfLockNotFound =>
      'The payout was not found on the server — the lock stays, decide yourself after checking the site.';

  @override
  String get stfConfirm => 'Confirm';

  @override
  String get stfRecentPayouts => 'Recent payouts from this phone';

  @override
  String get stfCancelBtn => 'Request cancellation';

  @override
  String stfCancelAsk(String sum) {
    return 'Request cancellation of the $sum som payout?';
  }

  @override
  String get stfCancelHint =>
      'Only the owner or the accountant can cancel a payout on the NurCRM site — the phone sends a request.';

  @override
  String get stfCancelSend => 'Send the request';

  @override
  String get stfCancelSent => 'The cancellation request was sent to the owner.';

  @override
  String get stfSchemeBtn => 'Pay scheme';

  @override
  String stfSchemeTitle(String name) {
    return 'Pay scheme: $name';
  }

  @override
  String get stfSchemeSaved => 'Pay scheme saved';

  @override
  String get stfSchemeSalary => 'Salary';

  @override
  String get stfSchemePercent => 'Percentage of sales';

  @override
  String get stfSchemeSalaryPercent => 'Salary + percentage of sales';

  @override
  String get stfSchemePerItem => 'Per item sold';

  @override
  String get stfSchemeSalaryItem => 'Salary + per item';

  @override
  String get stfSchemePercentItem => 'Percentage + per item';

  @override
  String get stfNoScheme => 'no scheme set up';

  @override
  String get stfMonthly => 'Monthly salary';

  @override
  String get stfPercent => 'Sales percentage';

  @override
  String get stfPerItem => 'Per item sold';

  @override
  String get stfBonus => 'Accrued for sales';

  @override
  String get gkOpen => 'Gemini keys (several accounts)';

  @override
  String get gkTitle => 'Gemini keys';

  @override
  String get gkHint =>
      'Keys are used in order: the first working one. When a key runs out of its limit, the AI takes the next one by itself. The list is the same as on the till and in the owner app.';

  @override
  String get gkWarn =>
      'Google\'s rules may forbid getting around limits with several accounts or projects — your keys could be blocked. Google\'s free tier may use your requests to improve its products. It is your decision.';

  @override
  String get gkViewOnly =>
      'The key list is changed by the owner and administrators. You see it without the full keys — the AI works from this list.';

  @override
  String gkWorkingCount(int a, int b) {
    return 'Working keys: $a of $b';
  }

  @override
  String get gkEmpty =>
      'No keys yet. Add the first one — Google AI Studio issues it.';

  @override
  String get gkAdd => 'Add a key';

  @override
  String get gkAddBtn => 'Add';

  @override
  String get gkPasteKey => 'Paste a key from Google AI Studio';

  @override
  String get gkLabelHint => 'Label (optional)';

  @override
  String gkLabelDefault(int n) {
    return 'Account $n';
  }

  @override
  String get gkMax => 'No more than 10 keys.';

  @override
  String get gkDuplicate => 'This key is already in the list.';

  @override
  String get gkShow => 'Show';

  @override
  String get gkHide => 'Hide';

  @override
  String get gkEnabled => 'On';

  @override
  String get gkMoveUp => 'Move up';

  @override
  String get gkMoveDown => 'Move down';

  @override
  String get gkDelete => 'Delete';

  @override
  String gkDeleteAsk(String name, String tail) {
    return 'Delete the key “$name” (…$tail)?';
  }

  @override
  String get gkStateWorking => 'working';

  @override
  String gkStateExhausted(String time) {
    return 'exhausted until $time';
  }

  @override
  String get gkStateInvalid => 'invalid';

  @override
  String get gkStateDisabled => 'off';

  @override
  String get gkStateUnchecked => 'not checked';

  @override
  String get gkCheckOk => 'The key works ✓';

  @override
  String gkCheckFail(String err) {
    return 'Could not check: $err';
  }

  @override
  String get aiKeysSavedPhoneOnly =>
      'Saved on the phone, but not sent to the cloud — the till and the owner app will not see it yet. Try again later.';

  @override
  String get klTitle => 'Till link';

  @override
  String get klProfileOff => 'Not connected';

  @override
  String klKassaName(String name) {
    return 'Till “$name”';
  }

  @override
  String get klScanQr => 'Scan the QR on the till';

  @override
  String get klScanQrHint =>
      'On the till computer open the “Connect phone” window and point the camera at the QR code. The phone and the till must be on the same Wi-Fi network, and the till must be running.';

  @override
  String get klScanQrTitle =>
      'Point the camera at the QR code on the till screen';

  @override
  String get klNotKassaQr => 'This is not a NurCRM till QR code';

  @override
  String get klBadQr =>
      'Could not read the QR code. Open the “Connect phone” window on the till and scan again.';

  @override
  String get klQrVersion =>
      'This till is newer than the app. Update NurCRM on the phone.';

  @override
  String get klManual => 'Enter the link manually';

  @override
  String get klManualHint => 'Paste the QR code text (nurmarket-link:…)';

  @override
  String get klConnect => 'Connect';

  @override
  String klConnected(String name) {
    return 'Till “$name” connected';
  }

  @override
  String get klConnectedPlain => 'Till connected';

  @override
  String get klStUnpaired => 'No till connected';

  @override
  String get klStUnknown => 'Link not checked';

  @override
  String get klStOnline => 'Connected';

  @override
  String get klStNotFound => 'Till not found on this network';

  @override
  String get klStRevoked => 'The phone was disconnected on the till';

  @override
  String get klStBadTime => 'Check the time on the phone';

  @override
  String get klStDisabled => 'Receiving from phones is turned off on the till';

  @override
  String get klStRePair =>
      'The link is outdated — scan the QR on the till again';

  @override
  String get klChecking => 'Checking the link…';

  @override
  String get klShiftOpen => 'Shift on the till: open';

  @override
  String get klShiftClosed => 'Shift on the till: closed';

  @override
  String get klCheckNow => 'Check the link';

  @override
  String get klDisconnect => 'Disconnect';

  @override
  String klDisconnectAsk(String name) {
    return 'Disconnect the phone from the till “$name”? You can connect again with a QR code. On the till itself the phone stays in the list — remove it there too.';
  }

  @override
  String get klDisconnected => 'The phone was disconnected from the till';

  @override
  String get klScannerMode => 'Scanner for the till';

  @override
  String get klScannerHint =>
      'Point at the barcode — the product goes straight into the receipt on the till';

  @override
  String get klSetupAsk => 'The till link is not set up';

  @override
  String get klSetup => 'Set up';

  @override
  String get klScSending => 'Sending…';

  @override
  String get klScAdded => 'Added';

  @override
  String get klScNotFound => 'Not found';

  @override
  String get klScTillInactive => 'The till window is not open';

  @override
  String get klScQueued => 'No connection — queued';

  @override
  String get klScQueueFull => 'The queue is full — code not accepted';

  @override
  String get klScExpired => 'Not sent: the till did not respond';

  @override
  String get klScFailed => 'The till did not accept the code';

  @override
  String get klLastCode => 'Last code';

  @override
  String klCounter(int n) {
    return 'Added to the receipt: $n';
  }

  @override
  String klQueueLeft(int n) {
    return 'Queued: $n';
  }

  @override
  String get globalSource => 'from the NurCRM catalog';

  @override
  String globalFoundAsk(String name) {
    return 'The NurCRM catalog has: $name';
  }

  @override
  String get globalCatalogTitle => 'Product catalog';

  @override
  String globalCatalogStatus(String count, String date, String added) {
    return 'Catalog: $count products, updated $date, added: $added';
  }

  @override
  String get globalCatalogOff => 'Catalog unavailable';

  @override
  String globalBulkFilled(int n) {
    return 'Names filled in from the NurCRM catalog: $n';
  }

  @override
  String advisorQuota(String time) {
    return 'AI limit used up until $time (shared with the till and the bot). You can add a second key from another Google account.';
  }

  @override
  String get advisorQuotaLater =>
      'AI limit used up (shared with the till and the bot). Try later or add a second key from another Google account.';

  @override
  String advisorKeyRejected(String time) {
    return 'Google rejected the key, retry after $time. Check the key in “AI settings” → “Gemini keys”.';
  }

  @override
  String get advisorTimeout =>
      'The AI is taking too long to answer. Check the internet and tap “Retry”.';

  @override
  String gkLastError(String text) {
    return 'Last error: $text';
  }

  @override
  String get aiAccOwnerNote =>
      'You are not in the list, but the owner and the administrator can always use AI.';

  @override
  String get pePriceCheckTitle => 'Check the price';

  @override
  String pePriceUpAsk(String now, String was, String times) {
    return 'Price $now instead of $was — save?\nThat is $times× the old price.';
  }

  @override
  String pePriceDownAsk(String now, String was, String times) {
    return 'Price $now instead of $was — save?\nThat is $times× lower than the old price.';
  }

  @override
  String peBelowCostAsk(String now, String cost) {
    return 'Price $now is below the purchase price $cost — selling at a loss. Save?';
  }

  @override
  String peMarkupAsk(String now, String cost, String pct) {
    return 'Price $now with purchase price $cost: markup $pct %. Is there a typo in the numbers? Save?';
  }

  @override
  String get peFixPrice => 'Fix it';

  @override
  String get peSaveAsIs => 'Save as is';

  @override
  String get klSearching => 'Looking for the till on Wi-Fi…';

  @override
  String get klHelpNotFound =>
      'The till does not answer. Check: 1) the till is running; 2) “Accept scans from phones” is turned on in the till settings (it is off by default); 3) the phone is on the same Wi-Fi network as the till computer, not on mobile data.';

  @override
  String get klHelpDisabled =>
      'Receiving from phones is off on the till. Turn on “Accept scans from phones” in the till settings and tap “Check the link”.';

  @override
  String get klHelpRePair =>
      'The till does not recognise this phone (it was disconnected or the QR is outdated). Tap “Scan the QR again” and point the camera at the QR in the “Connect a phone” window on the till.';

  @override
  String get klHelpBadTime =>
      'The time on the phone and on the till differs by more than 10 minutes. Turn on automatic date and time on the phone (and check the time on the till), then tap “Check the link”.';

  @override
  String get klRescanQr => 'Scan the QR again';

  @override
  String get klScRefused =>
      'Code not sent — restore the link with the till first';

  @override
  String get aiAccDraftNote =>
      'The list is not saved yet: right now AI is open to all employees. Tick up to 2 people and tap “Save” — AI will turn off for the rest. The owner and the administrator can always use AI.';

  @override
  String get aiAccAlways =>
      'The owner and the administrator can always use AI, whatever the list says.';

  @override
  String get invTitle => 'Upload an invoice';

  @override
  String get invSub => 'Excel, CSV or XML from the supplier';

  @override
  String get invHint =>
      'Choose the supplier\'s invoice file: Excel (.xlsx), CSV or XML (electronic invoice). The lines go into the receipt — you check quantities and prices and post it as usual.';

  @override
  String get invPick => 'Choose invoice file';

  @override
  String get invPhotoHint =>
      'PDF and photos of an invoice are read by the Smart camera (needs an AI key)';

  @override
  String get invPhotoBtn => 'Invoice from a photo';

  @override
  String get invReading => 'Reading the invoice…';

  @override
  String invFileName(String name) {
    return 'File: $name';
  }

  @override
  String get invOtherFile => 'Another file';

  @override
  String invDocLine(String num, String date) {
    return 'Invoice no. $num of $date';
  }

  @override
  String invDocNum(String num) {
    return 'Invoice no. $num';
  }

  @override
  String invDocDate(String date) {
    return 'Invoice of $date';
  }

  @override
  String get invDocNone => 'No number or date found in the file';

  @override
  String invSupplierFile(String name) {
    return 'In the file: $name';
  }

  @override
  String invSupplierFileInn(String name, String inn) {
    return 'In the file: $name, TIN $inn';
  }

  @override
  String invSupplierInnOnly(String inn) {
    return 'TIN in the file: $inn';
  }

  @override
  String get invSupplierNoneInFile => 'The file does not name a supplier';

  @override
  String get invSupByInn => 'Found by TIN';

  @override
  String get invSupByName => 'Found by name';

  @override
  String get invSupNotFound =>
      'No such supplier — pick one from the list or create it';

  @override
  String get invSupCreate => 'Create supplier';

  @override
  String invSupCreated(String name) {
    return 'Supplier “$name” created';
  }

  @override
  String invSupFail(String text) {
    return 'Could not create the supplier: $text';
  }

  @override
  String get invSupOffline => 'No connection: the supplier list was not loaded';

  @override
  String get invSupKept => 'The receipt kept its supplier';

  @override
  String get invDupPosted => 'This invoice was already loaded and posted';

  @override
  String get invDupPending =>
      'This invoice is already in a receipt that has not been posted yet';

  @override
  String get invDupAnyway => 'Add anyway';

  @override
  String invTotalLines(String sum) {
    return 'By lines: $sum';
  }

  @override
  String invTotalFile(String sum) {
    return 'In the file: $sum';
  }

  @override
  String get invTotalOk => 'The totals match';

  @override
  String invTotalBad(String diff) {
    return 'The totals differ by $diff. Check quantities and prices.';
  }

  @override
  String get invTotalNone => 'No total found in the file';

  @override
  String get invTotalVatHint =>
      'With VAT the totals match — turn on “Cost incl. VAT”';

  @override
  String get invVat => 'Cost incl. VAT';

  @override
  String get invVatSub => 'By default the cost is taken without VAT';

  @override
  String invCurrency(String cur) {
    return 'Invoice currency: $cur. You work in som — check the prices: the app does not convert them.';
  }

  @override
  String get invGenCodes => 'Give new items without a barcode our own code';

  @override
  String get invGenCodesSub => 'Otherwise they stay without a barcode';

  @override
  String invSummary(int n, String stock, String fresh, String bad) {
    return 'Lines: $n · in stock: $stock · new: $fresh · unrecognized: $bad';
  }

  @override
  String get invStBarcode => 'In stock · by barcode';

  @override
  String get invStArticle => 'In stock · by article';

  @override
  String get invStName => 'In stock · by name';

  @override
  String invStFuzzy(String name) {
    return 'Looks like: $name';
  }

  @override
  String get invStCatalog => 'New · in the NurCRM catalog';

  @override
  String get invStManual => 'New · fill in by hand';

  @override
  String get invStBad => 'Not recognized';

  @override
  String get invBadNoName => 'No name, barcode or article';

  @override
  String get invBadNoQty => 'No quantity';

  @override
  String invUseCandidate(String name) {
    return 'It is “$name”';
  }

  @override
  String get invAsNew => 'New product';

  @override
  String invSrcRow(int n) {
    return 'File row: $n';
  }

  @override
  String invArticle(String code) {
    return 'Article: $code';
  }

  @override
  String get invCostMissing => 'Enter the cost';

  @override
  String get invPriceMissing => 'Enter the selling price';

  @override
  String get invRemoveRow => 'Remove the row';

  @override
  String invAdd(int n) {
    return 'Add to receipt · $n';
  }

  @override
  String get invAddFix => 'Fix the marked rows';

  @override
  String get invAdding => 'Preparing the rows…';

  @override
  String invMerged(int n) {
    return 'Repeated items in the invoice were merged: $n';
  }

  @override
  String get invErrTooBig => 'The file is over 10 MB — it cannot be opened';

  @override
  String get invErrEmpty => 'The file is empty';

  @override
  String get invErrPdf =>
      'This is a PDF or a photo. The Smart camera reads those (button below)';

  @override
  String get invErrXls =>
      'The old Excel format (.xls) cannot be read — save the file as .xlsx or .csv';

  @override
  String get invErrBroken => 'The file is damaged or is not an invoice';

  @override
  String get invErrNoTable =>
      'No product table found: the columns “Name”, “Quantity” and “Price” are needed';

  @override
  String get invErrUnsafe =>
      'The XML contains a DTD or external entities — it is not opened for security reasons';

  @override
  String get invErrTooMany => 'Too many rows (over 5000)';

  @override
  String get invErrFormat =>
      'Unsupported file: .xlsx, .csv or .xml is required';

  @override
  String get tsTitle => 'Tester mode';

  @override
  String tsBuildLine(String version, String build) {
    return 'Version $version (build $build)';
  }

  @override
  String get tsTestBuild => 'test build';

  @override
  String get tsDevice => 'Device';

  @override
  String tsDeviceId(String id) {
    return 'Device ID: $id';
  }

  @override
  String get tsDeviceNameTitle => 'Device name';

  @override
  String get tsDeviceNameHint => 'e.g. the cashier\'s phone';

  @override
  String get tsRename => 'Name this device';

  @override
  String tsScreenLine(String w, String h, String scale, String lang) {
    return 'Screen $w×$h dp, font ×$scale, language $lang';
  }

  @override
  String tsSummary(String done, String total, String issues) {
    return 'Checked $done of $total, problems: $issues';
  }

  @override
  String get tsVersionFilter => 'Version';

  @override
  String tsVersionCurrent(String v) {
    return '$v (current)';
  }

  @override
  String tsAreaProgress(String done, String total) {
    return '$done of $total';
  }

  @override
  String get tsChanged => 'What changed';

  @override
  String get tsHowTo => 'How to check';

  @override
  String get tsSevMoney => 'Money';

  @override
  String get tsSevImportant => 'Important';

  @override
  String get tsSevNormal => 'Regular';

  @override
  String get tsMarkOk => 'Checked';

  @override
  String get tsMarkIssue => 'Problem';

  @override
  String get tsNotChecked => 'Not checked';

  @override
  String tsCheckedAt(String when) {
    return 'Checked $when';
  }

  @override
  String tsIssueAt(String when) {
    return 'Problem $when';
  }

  @override
  String get tsNoComment => 'No comment';

  @override
  String get tsUndoHint => 'Tap again to remove the mark';

  @override
  String get tsCountNone => 'Not checked on any device yet';

  @override
  String get tsCountLocalOk => 'Checked: 1 device (this one)';

  @override
  String get tsCountLocalIssue => 'Problem: 1 device (this one)';

  @override
  String tsCountShared(String ok, String issue) {
    return 'Checked on devices: $ok · problem on devices: $issue';
  }

  @override
  String get tsSyncOff =>
      'The shared counter is unavailable (rights or a server needed)';

  @override
  String get tsRefresh => 'Refresh counters';

  @override
  String get tsCopyReport => 'Copy report';

  @override
  String get tsShareReport => 'Send report';

  @override
  String get tsCopied => 'Report copied — paste it into a message';

  @override
  String get tsIssueTitle => 'What is wrong?';

  @override
  String get tsIssueHint => 'Describe the problem';

  @override
  String get tsAttachShot => 'Attach a screenshot';

  @override
  String get tsShotAttached => 'Screenshot attached';

  @override
  String get tsShotRemove => 'Remove the screenshot';

  @override
  String get tsEditComment => 'Edit the comment';

  @override
  String get tsEmpty => 'No items for this version';

  @override
  String get tsCodeTitle => 'Tester code';

  @override
  String get tsCodeHint => 'Enter the code';

  @override
  String get tsCodeOn => 'Tester mode is on: tap the logo 5 times';

  @override
  String get tsCodeBad => 'The code is not valid';

  @override
  String tsTapMore(int n) {
    return 'Tester mode: taps left — $n';
  }

  @override
  String get pdNoRights =>
      'Only staff with the Stock right or an administrator can delete products.';

  @override
  String get pdGone =>
      'The product is already gone from the server — removed it from the phone too.';

  @override
  String pdRefused(String msg) {
    return 'The server did not allow deleting the product: $msg. If it is already in sales or documents, write the stock off instead.';
  }

  @override
  String get pdOffline =>
      'No connection to the server — the product was NOT deleted. Try again when the network is back.';

  @override
  String pdFailed(String msg) {
    return 'The product was not deleted: $msg';
  }

  @override
  String pdQueueBlock(int n) {
    return 'This product has unsent operations: $n. Wait for them to be sent, then try again.';
  }

  @override
  String get pdAuditFail => 'Not deleted';

  @override
  String pdAsk(String name) {
    return 'Delete “$name”? The product disappears from the NurCRM catalog, the till and the app. This cannot be undone.';
  }

  @override
  String pdStockWarn(String qty, String unit) {
    return '$qty $unit in stock. The stock disappears with the product. If you no longer need it, write the stock off first.';
  }

  @override
  String get pdTypeName => 'To confirm, type the product name:';

  @override
  String get pdTypeHint => 'Letter case does not matter';

  @override
  String get ctTitle => 'Smart camera';

  @override
  String get ctAngleFront => 'front';

  @override
  String get ctAngleBack => 'back';

  @override
  String get ctAngleSide => 'side';

  @override
  String ctCount(int n, int a) {
    return 'References: $n of $a';
  }

  @override
  String get ctNone =>
      'Not trained: the product is not yet recognised exactly from a photo';

  @override
  String get ctTrained =>
      'Trained: the product is recognised from these shots when selling';

  @override
  String get ctTrain => 'Train the camera';

  @override
  String get ctReset => 'Clear references';

  @override
  String get ctResetAsk =>
      'Remove all reference shots of this product from the phone? Photos in NurCRM stay.';

  @override
  String get ctSaved => 'Reference saved';

  @override
  String get ctRemoved => 'Reference removed';

  @override
  String ctShotHint(String angle) {
    return 'Whole product in the frame, $angle';
  }

  @override
  String get ctBarcodeNone => 'No barcode — scan it';

  @override
  String ctBarcodeOk(String code) {
    return 'Barcode: $code';
  }

  @override
  String get ctNote =>
      'Shots are kept on this phone. “Train the camera” is the full course of up to 5 shots and a barcode; it also adds a product photo to NurCRM.';

  @override
  String ctSlot(int n) {
    return 'Shot $n';
  }

  @override
  String ctSlotAdd(int n, String angle) {
    return 'Shot $n: take $angle';
  }

  @override
  String ctSlotRetake(int n) {
    return 'Shot $n: retake';
  }

  @override
  String get ctRemove => 'Remove';

  @override
  String get camPickTitle => 'Looks like — pick it if that is the one';

  @override
  String get camFromGallery => 'From gallery';

  @override
  String get camFromFile => 'From file';

  @override
  String get camInvoiceSrcHint =>
      'Take a photo of the invoice or pick it from the gallery or a file: photo, PDF, Excel, CSV, XML.';

  @override
  String camPageOf(int n, int a) {
    return 'Photo $n of $a';
  }

  @override
  String get camFileNoProcure =>
      'Loading an invoice file needs the “Purchases” permission. Photograph the invoice — the lines will appear here.';

  @override
  String get camFileTooBig =>
      'The file is over 8 MB — pick a smaller one or photograph the invoice.';

  @override
  String get camFileBadType =>
      'This file type is not supported. Use Excel (xlsx), CSV, XML, TXT, PDF or a photo.';

  @override
  String get camFileReadFail => 'Could not read the file.';

  @override
  String recvFoundGlobal(String name) {
    return 'Found in the NurCRM directory: “$name”';
  }

  @override
  String get recvAddProduct => 'Add product';

  @override
  String recvLooksLike(String name) {
    return 'Looks like your “$name”';
  }

  @override
  String get recvBindCode => 'Link barcode';

  @override
  String recvTypo(String name, String stored, String code) {
    return 'Typo in the barcode of “$name”: stored $stored, correct $code';
  }

  @override
  String get recvFixCode => 'Fix';

  @override
  String recvBound(String name) {
    return 'Barcode linked to “$name”';
  }

  @override
  String get recvFixed => 'Barcode fixed';

  @override
  String get recvOffline => 'The server did not answer';

  @override
  String recvOfflineHint(String code) {
    return '$code · could not check: no connection to the server. This does not mean the product is missing — check again when the network is back';
  }

  @override
  String get recvRetry => 'Check again';

  @override
  String get recvNoRight =>
      'Adding, linking or fixing needs the “Stock” permission.';

  @override
  String recvSearching(String code) {
    return 'Looking up $code on the server and in the directory…';
  }

  @override
  String get pkTitle => 'Packs / single-piece sale';

  @override
  String get pkSwitch => 'Sell singles from a pack';

  @override
  String get pkSwitchHint =>
      'For example a pack of cigarettes or a 6-bottle water pack: sell it as a pack or by the piece. Stock is kept in packs.';

  @override
  String get pkName => 'Pack name';

  @override
  String get pkNameHint => 'Pack, block, box';

  @override
  String get pkQty => 'Pieces per pack';

  @override
  String pkPackPrice(String price) {
    return 'Pack price: $price som (the selling price above)';
  }

  @override
  String get pkPiecePrice => 'Price per piece';

  @override
  String get pkAuto => 'Auto';

  @override
  String get pkAutoTip => 'Recalculate: pack price ÷ pieces';

  @override
  String pkMarkup(int n) {
    return 'Piece markup: $n %';
  }

  @override
  String pkBelowCost(String cost) {
    return 'A piece costs less than the purchase ($cost som)';
  }

  @override
  String get pkQtyBad => 'Pieces per pack must be a whole number, at least 2';

  @override
  String get pkPieceBad => 'Enter the price per piece';

  @override
  String pkMismatch(String qty, String piece, String sum, String pack) {
    return '$qty pcs × $piece som = $sum som, but the pack costs $pack som — more than 5 % apart. Save anyway?';
  }

  @override
  String get pkMismatchTitle => 'Piece and pack prices differ';

  @override
  String get pkWeightBlocked =>
      'A weighed item (kg) cannot be sold by the piece from a pack.';

  @override
  String get pkMulti =>
      'This item has several packs — edit them on the website or the till.';

  @override
  String get pkQuick => 'Quick';

  @override
  String get pkWordPack => 'Pack';

  @override
  String get pkWordPackage => 'Package';

  @override
  String get pkWordCrate => 'Crate';

  @override
  String get pkWordBlock => 'Block';

  @override
  String get pkWordBox => 'Box';

  @override
  String get pkPcs => 'pcs';

  @override
  String get pkChoiceTitle => 'How to sell?';

  @override
  String get pkChoicePiece => 'Piece';

  @override
  String pkChoiceStock(String stock) {
    return 'In stock: $stock';
  }

  @override
  String pkLinePiece(String name) {
    return 'pcs from “$name”';
  }

  @override
  String get pkQtyInPacks =>
      'Quantity is entered in packs: 12 means 12 packs, pieces are sold from them.';

  @override
  String get pkPackShort => 'packs';

  @override
  String get pkNoPackId =>
      'The pack is not saved on the server yet — refresh the catalog first.';

  @override
  String get camNotThis => 'Not this product';

  @override
  String get camNotThisDone =>
      'The remembered name is forgotten — pick the product again.';

  @override
  String get camAliasesTitle => 'Remembered names:';

  @override
  String get camAliasForget => 'Forget this name';

  @override
  String get invDupPostedNow =>
      'This invoice was already posted earlier (on this phone or on the till). Posting the receipt again will double the stock and the supplier debt.';

  @override
  String get invSupOtherDoc =>
      'This receipt already holds an invoice from another supplier. Post it first, then load this one.';

  @override
  String get invSupConflictTitle => 'Whose receipt is this?';

  @override
  String invSupConflictBody(String doc, String keep) {
    return 'Invoice: $doc. Selected in the receipt: $keep. The receipt and the debt go to one supplier — choose.';
  }

  @override
  String invSupConflictKeep(String name) {
    return 'Keep: $name';
  }

  @override
  String invSupConflictDoc(String name) {
    return 'From the invoice: $name';
  }

  @override
  String recvCostAvg(int n) {
    return 'Purchase cost re-averaged by quantity on items: $n';
  }

  @override
  String invPackWarn(String unit, String name, String pu) {
    return 'In the invoice \"$unit\" is a pack, but the item \"$name\" is counted in \"$pu\". Choose how to receive it: otherwise packs go into stock as pieces at the pack price.';
  }

  @override
  String get invPackSize => 'Pieces per pack';

  @override
  String get invPackToPieces => 'Convert to pieces';

  @override
  String get invPackAsIs => 'Receive as is';

  @override
  String get invPackBad =>
      'Enter how many pieces are in a pack: a whole number from 2.';

  @override
  String invPackDonePieces(int n, String qty, String cost) {
    return '1 pack = $n pcs → $qty pcs at $cost som';
  }

  @override
  String invPackDoneAsIs(String qty, String unit, String cost) {
    return 'Received as is: stock grows by $qty $unit, cost $cost som per $unit';
  }

  @override
  String get invPackChange => 'Change';

  @override
  String get pieceSaleMode => 'Sell singles from a pack';

  @override
  String get pieceSaleModeSub =>
      'Turn off if the server writes off whole packs instead of pieces. Then such an item is sold by the pack only.';

  @override
  String get pieceSaleTesterWarn =>
      'Test: single-piece sale from a pack is not yet confirmed by the server. Check the item stock after payment.';

  @override
  String pieceStockMismatch(String list) {
    return 'The server stock did not match after selling a piece: $list. Check the item stock and tell the administrator.';
  }

  @override
  String get klScStale => 'Code expired — scan again';

  @override
  String get klScBusy => 'Payment in progress — scan after it';

  @override
  String invCurrencyOk(String cur) {
    return 'Prices are in $cur, but the receipt is kept in som. Add as is (1 $cur = 1 som) — I checked the amounts';
  }

  @override
  String get invGrossOnly =>
      'The file has prices with VAT only. The purchase cost was recalculated without VAT by the row\'s rate (no rate — the VAT price is used). Check the cost.';

  @override
  String invUnitCheck(String inv, String item) {
    return 'Check · unit: the invoice says \"$inv\", the item uses \"$item\"';
  }

  @override
  String get empAdd => 'New employee';

  @override
  String get empCard => 'Employee card';

  @override
  String get empSectionData => 'Employee details';

  @override
  String get empSectionRights => 'Access rights';

  @override
  String get empFirstName => 'First name';

  @override
  String get empLastName => 'Last name';

  @override
  String get empMiddleName => 'Middle name';

  @override
  String get empEmail => 'Email (sign-in login)';

  @override
  String get empPhone => 'Phone';

  @override
  String get empTrack => 'Employee number';

  @override
  String get empRole => 'Position';

  @override
  String get empBirth => 'Date of birth';

  @override
  String get empHire => 'Hire date';

  @override
  String get empAddress => 'Address';

  @override
  String get empComment => 'Comment';

  @override
  String get empClearDate => 'Clear date';

  @override
  String get empRequired => 'Fill in this field';

  @override
  String get empEmailBad => 'Enter the full email, e.g. aibek@mail.kg';

  @override
  String get empPhoneBad => 'Phone: 9 to 15 digits, may start with +';

  @override
  String get empSaved => 'Employee saved.';

  @override
  String get empCreated => 'Employee added.';

  @override
  String get empNothingChanged => 'Nothing changed.';

  @override
  String get empSearch => 'Search: name, email, phone';

  @override
  String get empFilterActive => 'Working';

  @override
  String get empFilterDismissed => 'Dismissed';

  @override
  String get empDismissedTag => 'dismissed';

  @override
  String empCount(int n) {
    return 'Employees: $n';
  }

  @override
  String get empNothingFound => 'Nobody found.';

  @override
  String get empDismiss => 'Dismiss';

  @override
  String get empRestore => 'Restore';

  @override
  String empDismissAsk(String name) {
    return 'Dismiss $name? They will no longer be able to sign in. Sales and shifts are kept.';
  }

  @override
  String empRestoreAsk(String name) {
    return 'Restore $name? They will be able to sign in again.';
  }

  @override
  String get empDismissedDone => 'Employee dismissed.';

  @override
  String get empRestoredDone => 'Employee restored.';

  @override
  String get empDismissStill =>
      'The server accepted the request, but the employee is still in the list. Remove them on app.nurcrm.kg.';

  @override
  String get empResetPwd => 'Reset password';

  @override
  String empResetAsk(String name) {
    return 'Reset the password of $name? The old one stops working; the new one is shown once.';
  }

  @override
  String get empPasswordTitle => 'Employee password';

  @override
  String empPasswordNote(String email) {
    return 'Login: $email. The password is shown once — hand it to the employee now.';
  }

  @override
  String get empCopy => 'Copy password';

  @override
  String get empCopied => 'Copied.';

  @override
  String get empFeatureMissing =>
      'The server does not support this action yet.';

  @override
  String get empListDenied =>
      'No \"Employees\" right — the list is unavailable. Ask the owner.';

  @override
  String get empErrBad =>
      'The server did not accept the data. Check the fields.';

  @override
  String get empErrEmail => 'This email is already taken or invalid.';

  @override
  String get empErr403 => 'You have no right for this action. Ask the owner.';

  @override
  String get empErr404 =>
      'Employee not found on the server — refresh the list.';

  @override
  String get empErr409 =>
      'This employee already exists or the data changed. Refresh the list.';

  @override
  String get empErr429 => 'Too many requests. Wait a minute and retry.';

  @override
  String get empErr5xx =>
      'Server error. Nothing was changed — try again later.';

  @override
  String get rightUsePos => 'Work at the till';

  @override
  String get rightSalaryView => 'Salary: view';

  @override
  String get rightSalaryEdit => 'Salary: pay out';

  @override
  String get rightEmployeesView => 'Employees: view';

  @override
  String get rightEmployeesEdit => 'Employees: edit';

  @override
  String get rightsReadOnly =>
      'Rights are view-only: the owner or someone with \"Settings\" edits them.';

  @override
  String get unitNamePcs => 'Piece';

  @override
  String get unitNameKg => 'Kilogram';

  @override
  String get unitNameG => 'Gram';

  @override
  String get unitNameL => 'Litre';

  @override
  String get unitNameMl => 'Millilitre';

  @override
  String get unitNameM => 'Metre';

  @override
  String get unitNamePack => 'Pack';

  @override
  String get unitNamePair => 'Pair';

  @override
  String get unitNameSet => 'Set';

  @override
  String get unitCustom => 'Custom';

  @override
  String get unitCustomTitle => 'Custom unit';

  @override
  String get unitCustomHint => 'e.g. roll, bottle';

  @override
  String unitHintFrac(String name) {
    return '$name: a fractional quantity is fine, e.g. 0.5';
  }

  @override
  String unitHintWhole(String name) {
    return '$name: whole quantity only';
  }

  @override
  String get unitHintCustom => 'Custom unit: the quantity may be fractional';

  @override
  String nuQtyWhole(String unit, String qty) {
    return 'For \"$unit\" the quantity must be a whole number (now $qty). Pick kg, l, m… or type a whole number.';
  }

  @override
  String get nuQtyNegative => 'The quantity cannot be below zero.';

  @override
  String get nuPriceNeed => 'Enter the sale price';

  @override
  String get bkHintUnits =>
      'Unit: шт, кг, г, л, мл, м, упак, пара, компл (also understood: piece, litre, pcs, kg, l, dona…). Price and quantity may be fractional: 12.75; 0.5.';

  @override
  String get bkErrName => 'no name';

  @override
  String get bkErrPrice => 'enter the sale price';

  @override
  String bkErrUnit(String unit) {
    return 'unit \"$unit\" not recognised — pick pcs, kg, l…';
  }

  @override
  String bkErrQtyWhole(String qty, String unit) {
    return 'quantity $qty must be whole for \"$unit\"';
  }

  @override
  String get bkErrQtyNeg => 'quantity below zero';

  @override
  String get bkWarnQty => 'no quantity — stock will be 0';

  @override
  String get bkSetUnit => 'Set unit';

  @override
  String get bkSetPrice => 'Set price';

  @override
  String get bkSetQty => 'Set quantity';

  @override
  String get bkSelectAll => 'Select all';

  @override
  String get bkSelectBad => 'With errors';

  @override
  String get bkSelectNone => 'Clear selection';

  @override
  String bkSelectedN(int n) {
    return 'Rows selected: $n';
  }

  @override
  String get bkSelectFirst => 'Tick rows to apply a change to many at once.';

  @override
  String bkApplyTo(int n) {
    return 'Apply to $n';
  }

  @override
  String get bkEditRow => 'Product row';

  @override
  String bkResult(int a, int b) {
    return 'Done $a, with errors $b';
  }

  @override
  String get barcodeDigitsHint => 'Digits only, 4 to 14';

  @override
  String get barcodeBadLen => 'The barcode is 4 to 14 digits, no letters';

  @override
  String get infK => 'K';

  @override
  String get infM => 'M';

  @override
  String get infB => 'B';

  @override
  String get infNoData => 'No data for this period';

  @override
  String get infNoDataHint => 'Pick another period or make some sales';

  @override
  String infUpdatedAt(String time) {
    return 'Updated $time';
  }

  @override
  String get infCompare => 'Compare with the previous period';

  @override
  String get infPeriodCustom => 'Custom';

  @override
  String get infVsYesterday => 'vs yesterday';

  @override
  String get infVsPrevWeek => 'vs last week';

  @override
  String get infVsPrevMonth => 'vs last month';

  @override
  String get infVsPrevPeriod => 'vs previous period';

  @override
  String infDeltaUpSem(String pct, String vs) {
    return 'Up $pct % $vs';
  }

  @override
  String infDeltaDownSem(String pct, String vs) {
    return 'Down $pct % $vs';
  }

  @override
  String infDeltaSameSem(String vs) {
    return 'No change $vs';
  }

  @override
  String get infDeltaNew => 'new';

  @override
  String get infOther => 'Other';

  @override
  String get infShare => 'Share';

  @override
  String infDescEmpty(String name) {
    return '$name: no data';
  }

  @override
  String infDescOne(String name, String label, String value, String unit) {
    return '$name: $label — $value $unit';
  }

  @override
  String infDescBars(
    String name,
    String max,
    String unit,
    String maxLabel,
    String min,
    String minLabel,
    String total,
  ) {
    return '$name: highest $max $unit ($maxLabel), lowest $min $unit ($minLabel), total $total $unit';
  }

  @override
  String get infHowCalc => 'How it is calculated';

  @override
  String get infHowRevenue =>
      'Revenue is the sum of receipts with status \"paid\" and \"partially returned\" for the period, same as in the desktop program and on the site. Debt and cancelled receipts are not included.';

  @override
  String get infHowChecks =>
      'Receipts — the number of paid receipts for the period (partially returned ones too).';

  @override
  String get infHowAvg =>
      'Average receipt = revenue ÷ number of receipts for the period.';

  @override
  String get infHowProfit =>
      'Profit = revenue − purchase cost of the goods sold (the server\'s gross profit). Store expenses are not included.';

  @override
  String get infHowShiftCash =>
      'Expected in the drawer = opening cash + cash sales + cash debt payments + cash in − cash refunds − cash out. The figure comes from the server.';

  @override
  String get infHowDebts =>
      'Debts — the amount customers currently owe the store (the server\'s debtor list).';

  @override
  String get infHowLow =>
      'Running out — goods (not services) with stock below 10 or not above the product\'s own minimum stock.';

  @override
  String get infHowPayments =>
      'Payment methods — how much of the revenue came as cash, card, transfer and debt; percentages are of the total of all methods.';

  @override
  String get infHowTop =>
      'Top products — the goods with the highest revenue for the period; the bar is the share of the best product.';

  @override
  String get scanModeLabel => 'Mode';

  @override
  String get scanModeTitle => 'Scanner mode';

  @override
  String scanModeA11y(String mode) {
    return 'Scanner mode: $mode. Tap to change';
  }

  @override
  String get scanModeSaleSub => 'Scan — the item goes straight to the receipt';

  @override
  String get scanModeLookSub => 'Recognise an item by photo, no barcode';

  @override
  String get scanModeWeighSub => 'Weighed item: weight from the scale display';

  @override
  String get scanModeReceiveSub => 'Receive goods into stock';

  @override
  String get scanModeKassaSub => 'Barcodes go to the till over Wi-Fi';

  @override
  String get infNoDelta => '— n/a';

  @override
  String get infNoCompare => '— no comparison';

  @override
  String get infCompareShort => 'Compare';

  @override
  String get infLoadFailedTitle => 'Could not load';

  @override
  String infLoadFailedAt(String time) {
    return 'No connection to the server. Showing data as of $time.';
  }

  @override
  String get infLoadFailedNoData => 'No connection to the server.';

  @override
  String get infTapHint => 'Tap a bar for details';

  @override
  String get infTapRingHint => 'Tap a segment for details';

  @override
  String get infPrevBar => 'Previous period';

  @override
  String infOfPeriod(String pct) {
    return '$pct % of the period';
  }

  @override
  String get infHoursNote =>
      'Revenue by hour: the server does not provide an hourly split yet — this section will appear later.';

  @override
  String get infExpensesNote =>
      'Store expenses are not in the server report — showing revenue, cost of goods and profit.';

  @override
  String get infCost => 'Cost of goods';

  @override
  String infMargin(String pct) {
    return 'Margin $pct %';
  }

  @override
  String get infRevenueCostProfit => 'Revenue, cost and profit';

  @override
  String get infDebtConcTitle => 'Who owes the most';

  @override
  String infDebtConcLine(String pct) {
    return 'The three largest debtors owe $pct % of the total';
  }

  @override
  String get infDebtAgeNote =>
      'Debt age: the server does not send debt dates — showing concentration by debtor.';

  @override
  String get infStockTitle => 'Stock levels';

  @override
  String get infStockOut => 'Out of stock';

  @override
  String get infStockLow => 'Running low';

  @override
  String get infStockOk => 'In stock';

  @override
  String get infPaidWith => 'Paid with';

  @override
  String get infTotalWord => 'Total';

  @override
  String get infShiftNoSales => 'No sales in this shift yet';

  @override
  String get infHowTitle => 'How this is calculated';

  @override
  String get infRangePick => 'Pick dates';

  @override
  String get apiSaleAlreadyPosted =>
      'The server has already posted this receipt (same key) — check it in “Receipts”.';

  @override
  String get apiAuditStale =>
      'The stock count was not posted in time — stock may have changed. Count again (the act stays a draft).';

  @override
  String get priceChangedTitle => 'Price changed';

  @override
  String priceChangedLine(String name, String oldPrice, String newPrice) {
    return '$name: $oldPrice → $newPrice som';
  }

  @override
  String get priceChangedNote =>
      'The sale was NOT posted. Prices in the receipt are updated — check the new total and press “Pay” again.';

  @override
  String get priceMaybeStale =>
      'No connection — the price in the receipt may be out of date.';

  @override
  String get apiWifiPortal =>
      'Open the Wi-Fi sign-in page (the shop network asks you to log in) and try again.';

  @override
  String get apiBadResponse =>
      'The server replied in an unexpected way. Try again later.';

  @override
  String get linkOffline => 'No connection to the server';

  @override
  String linkOfflineSince(String time) {
    return 'No connection · data as of $time';
  }

  @override
  String dataAsOf(String time) {
    return 'Data as of $time';
  }

  @override
  String get noDataOffline => 'No data — no connection to the server';

  @override
  String unsentOnPhone(int n) {
    return 'Not sent from this phone: $n';
  }

  @override
  String listCachedAt(String time) {
    return 'Showing the saved list from $time.';
  }

  @override
  String get auditError => 'Error';

  @override
  String stockAsOf(String time) {
    return 'Stock as of $time';
  }

  @override
  String accountLoading(int done, int total) {
    return 'Loading account data… $done/$total';
  }

  @override
  String get accountLoadFailed =>
      'Account data did not load completely (no connection or the server did not answer).';

  @override
  String get accountDataNotLoaded => 'This account\'s data is not loaded yet';

  @override
  String get logoutSendNow => 'Send now';

  @override
  String guardUnsent(int n, String sum) {
    return 'There are unsent sales and operations: $n worth $sum som. You cannot sign out until they are sent.';
  }

  @override
  String get guardSending => 'Sending…';

  @override
  String get guardWaitConnection =>
      'No connection to the server. Wait for the connection and tap Send now again, or export the sales to a file.';

  @override
  String guardSendLeft(int n, String error) {
    return 'Not everything was sent: $n left. $error';
  }

  @override
  String get guardExport => 'Export to a file and sign out';

  @override
  String get guardExportWarn =>
      'The file will contain receipt data (items, amounts). Keep it in a safe place. After signing out nothing will remain on the phone except this file.';

  @override
  String guardExported(String path) {
    return 'File saved: $path. Send it to the owner or administrator to restore the sales.';
  }

  @override
  String guardExportFailed(String error) {
    return 'Could not save the file: $error. Sign-out cancelled, the sales are still there.';
  }

  @override
  String get guardDiscard => 'Sign out WITHOUT sending';

  @override
  String get guardDiscardWord => 'DELETE';

  @override
  String guardDiscardWarn(String word) {
    return 'Unsent sales will be LOST and cannot be restored. To confirm, type the word \"$word\".';
  }

  @override
  String get guardDiscardField => 'Confirmation word';

  @override
  String guardForeign(String account, int n, String sum) {
    return 'Unsent sales of another account ($account) are left on the phone: $n worth $sum som. To sign in with a different account they must be exported to a file or deleted. Or sign in with the previous account and send them.';
  }

  @override
  String get close => 'Close';

  @override
  String revCostLine(String cost) {
    return 'Cost: $cost';
  }

  @override
  String get revNoCost => 'No cost price';

  @override
  String revDiffAmount(String amount) {
    return 'Diff. amount: $amount';
  }

  @override
  String get revStarNote =>
      '* — the product has no cost price, the amount is at the selling price.';

  @override
  String revShortageLine(String qty, String cost, String sale) {
    return 'Shortage: $qty pcs · −$cost som (at cost) · −$sale som (at sale price)';
  }

  @override
  String revSurplusLine(String qty, String cost, String sale) {
    return 'Surplus: $qty pcs · +$cost som (at cost) · +$sale som (at sale price)';
  }

  @override
  String revNetLine(String amount) {
    return 'Net result: $amount som (at cost)';
  }

  @override
  String revNoCostWarn(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '⚠ No cost price for $n products',
      one: '⚠ No cost price for $n product',
    );
    return '$_temp0';
  }

  @override
  String revNoCostTip(String short, String surplus) {
    return 'These products have no cost price, so they are not in the “at cost” totals. Their difference at the selling price: shortage −$short som, surplus +$surplus som. In the list their amount is marked “*”.';
  }

  @override
  String revTopShortages(int n) {
    return 'Top $n shortages';
  }

  @override
  String revAllDiffs(int n) {
    return 'All differences: $n';
  }

  @override
  String get pbTitle => 'Receipts and batches';

  @override
  String get pbTotalReceived => 'Total received';

  @override
  String get pbAvgCost => 'Average cost';

  @override
  String get pbLastCost => 'Last cost';

  @override
  String get pbLastSupplier => 'Last supplier';

  @override
  String pbCount(int n) {
    return 'Receipts: $n';
  }

  @override
  String get pbPartial =>
      'Totals cover the loaded receipts only. Scroll down to load the rest.';

  @override
  String get pbSearchHint => 'Supplier or receipt no.';

  @override
  String get pbSearchHintNo => 'Receipt no.';

  @override
  String get pbWeek => 'Week';

  @override
  String get pbMonth => 'Month';

  @override
  String get pbYear => 'Year';

  @override
  String get pbAllTime => 'All time';

  @override
  String get pbInvoice => 'Invoice';

  @override
  String pbNumber(String no) {
    return 'No. $no';
  }

  @override
  String pbCostLine(String cost) {
    return 'Cost: $cost som';
  }

  @override
  String pbSumLine(String sum) {
    return 'Total: $sum som';
  }

  @override
  String pbReceivedBy(String name) {
    return 'Received by: $name';
  }

  @override
  String pbPayLine(String method) {
    return 'Payment: $method';
  }

  @override
  String get pbPayCash => 'Cash';

  @override
  String get pbPayDebt => 'On credit';

  @override
  String get pbPayPrepay => 'Prepayment';

  @override
  String pbRemainLine(String qty, String unit) {
    return 'Batch left ≈ $qty $unit (estimate)';
  }

  @override
  String pbRemainTip(String stock, String unit) {
    return 'An estimate by the FIFO rule (first in, first out): the product\'s current stock ($stock $unit) is spread over the newest receipts, starting from the last one. The server does not keep the balance of each batch, so the figure is approximate.';
  }

  @override
  String pbReturned(String qty, String unit) {
    return 'Returned to supplier: $qty $unit';
  }

  @override
  String get pbEmpty => 'No receipts of this product found';

  @override
  String get pbEmptySearch => 'Nothing found for this search';

  @override
  String pbDataFrom(String time) {
    return 'No connection. Data from $time';
  }

  @override
  String get pbLoadingMore => 'Loading more…';

  @override
  String get pbEnd => 'No more receipts';

  @override
  String get pbToTop => 'To top';

  @override
  String pbLoadError(String error) {
    return 'Could not load: $error';
  }

  @override
  String get pbDocTitle => 'Receipt invoice';

  @override
  String get pbDocLines => 'Receipt items';

  @override
  String pbDocTotal(String sum) {
    return 'Total: $sum som';
  }

  @override
  String get pbThisProduct => 'this product';

  @override
  String get pbOpenFile => 'Open file';

  @override
  String get pbNoFile => 'The original file was not saved';

  @override
  String get pbFileError => 'Could not open the file';

  @override
  String get pbSupplier => 'Supplier';

  @override
  String get pbCall => 'Call';

  @override
  String get pbWhatsApp => 'WhatsApp';

  @override
  String get pbNoPhone => 'No phone number';
}
