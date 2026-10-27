/* NurMarket — темы сайта для магазина одежды. Общий скрипт макетов SiteThemes-Clothing-*.html (27.10.2026).
   Данные 10 тем — ТЗ «Сайт — темы для магазина одежды…» (26.10), разделы 3.2–3.4. Это макет: витрину рисует
   фронт market.nurcrm.kg по `theme.*`, здесь — как она должна выглядеть. Фото товаров — силуэты кодом (прав на фото нет).
   Панель сверху — только для просмотра макета. Значки — MDI (как MarketplaceIcons). */
(function(){
  /* ---------- 17 цветов в порядке theme.colors (ТЗ 3.3) ---------- */
  const KEYS = "background surface text text_muted accent accent_text border header_bg header_text footer_bg footer_text price old_price badge_new_bg badge_new_text badge_sale_bg badge_sale_text".split(" ");
  const P = s => { const o = {}; s.split(" ").forEach((h, i) => o[KEYS[i]] = "#" + h); return o; };

  /* name — 5 языков (ТЗ 3.2), for — одна строка «для кого» (ru/ky), font/radius/shadow/btn/header/hero/cats/cols/card (ТЗ 3.4) */
  const THEMES = [
    { code:"cl_minimal", name:{ru:"Минимал",ky:"Минимал",en:"Minimal",tr:"Minimal",uz:"Minimal"}, mode:"light",
      for:{ru:"Светлый, тонкие линии, кнопки контуром. Базовая одежда, капсулы.", ky:"Жарык, ичке сызыктар, контур баскычтар. Базалык кийим, капсулалар."},
      colors:P("FAFAF8 FFFFFF 1A1A1A 5F5F5F 1A1A1A FFFFFF E7E5E0 FAFAF8 1A1A1A F0EEE9 1A1A1A 1A1A1A 6B6B6B 1A1A1A FFFFFF B42318 FFFFFF"),
      font:"Inter", base:15, hw:600, radius:0, shadow:"none", btn:"outline", header:"logo_left", hero:{style:"split",overlay:0,align:"left"}, cats:"chips",
      cols:[4,3,2], card:"compact", cardShadow:false, cardBorder:false, stock:"low_only", category:false,
      shop:"MONO", tagline:"Базовый гардероб", heroT:"Капсула осень", heroP:"12 вещей, которые сочетаются между собой. Хлопок, шерсть, лён.",
      heroBg:"linear-gradient(135deg,#D9D6CE,#B8B4A9 60%,#E9E6DF)", products:"basic" },
    { code:"cl_boutique", name:{ru:"Бутик Крем",ky:"Бутик Крем",en:"Boutique Cream",tr:"Butik Krem",uz:"Butik Krem"}, mode:"light",
      for:{ru:"Крем и бронза, логотип по центру, крупные карточки. Женская одежда, бутик.", ky:"Каймак түс жана коло, логотип ортодо, чоң карточкалар. Аялдар кийими, бутик."},
      colors:P("F7F2EA FFFDF9 2B2118 6B5D50 6B4F2A FFFFFF E6DCCD FFFDF9 2B2118 2B2118 F3EBDD 2B2118 75675A 6B4F2A FFFFFF 9F1239 FFFFFF"),
      font:"Montserrat", base:15, hw:600, radius:4, shadow:"soft", btn:"filled", header:"logo_center", hero:{style:"cover",overlay:0.25,align:"center"}, cats:"list",
      cols:[3,2,2], card:"large", cardShadow:false, cardBorder:false, stock:"low_only", category:false,
      shop:"Aiperi Boutique", tagline:"Женская одежда", heroT:"Новая коллекция", heroP:"Платья, костюмы и пальто — примерка в бутике на Киевской.",
      heroBg:"linear-gradient(135deg,#8C6A4A,#5A4330 55%,#A98B6B)", products:"women" },
    { code:"cl_street", name:{ru:"Streetwear",ky:"Streetwear",en:"Streetwear",tr:"Streetwear",uz:"Streetwear"}, mode:"dark",
      for:{ru:"Чёрный и кислотный лайм, узкий жирный шрифт, прямые углы. Уличная одежда, кроссовки.", ky:"Кара жана ачык лайм, ичке калың шрифт, түз бурчтар. Көчө кийими, кроссовкалар."},
      colors:P("111111 1B1B1B F5F5F5 A3A3A3 C6F432 111111 2E2E2E 000000 FFFFFF 000000 D4D4D4 C6F432 9A9A9A C6F432 111111 F43F5E 111111"),
      font:"Roboto Condensed", base:16, hw:800, radius:0, shadow:"none", btn:"filled", header:"logo_left", hero:{style:"cover",overlay:0.45,align:"left"}, cats:"chips",
      cols:[4,3,2], card:"standard", cardShadow:false, cardBorder:true, stock:"low_only", category:false,
      shop:"BLOCK 7", tagline:"Streetwear · Bishkek", heroT:"DROP 03 // ОСЕНЬ", heroP:"Худи, карго и кроссовки. Новая поставка каждую пятницу.",
      heroBg:"linear-gradient(120deg,#2A2A2A,#4A4F3A 50%,#1A1A1A)", products:"street" },
    { code:"cl_kids", name:{ru:"Детская одежда",ky:"Балдар кийими",en:"Kids",tr:"Çocuk giyim",uz:"Bolalar kiyimi"}, mode:"light",
      for:{ru:"Тёплый жёлтый, скругление 20, округлый шрифт, плитки категорий. Детская одежда.", ky:"Жылуу сары, 20 тегеректөө, тегерек шрифт, категория плиткалары. Балдар кийими."},
      colors:P("FFF9EC FFFFFF 1F2A44 56607A FFC83D 1F2A44 F1E3C2 FFFFFF 1F2A44 1F2A44 FFF4D6 1F2A44 6B7280 2563EB FFFFFF DC2626 FFFFFF"),
      font:"Nunito", base:16, hw:800, radius:20, shadow:"soft", btn:"filled", header:"logo_left", hero:{style:"split",overlay:0,align:"left"}, cats:"tiles",
      cols:[4,3,2], card:"standard", cardShadow:true, cardBorder:false, stock:"low_only", category:false,
      shop:"Балапан", tagline:"Детская одежда 0–14", heroT:"В школу и в садик", heroP:"Тёплые комбинезоны, куртки и школьная форма. Размеры 86–164.",
      heroBg:"linear-gradient(135deg,#FFD27A,#7FB3E8 60%,#F9A8D4)", products:"kids" },
    { code:"cl_menswear", name:{ru:"Мужская классика",ky:"Эркектер классикасы",en:"Menswear Classic",tr:"Erkek Klasik",uz:"Erkaklar klassikasi"}, mode:"light",
      for:{ru:"Тёмно-синяя шапка, PT Sans, рамки у карточек. Костюмы, рубашки, обувь.", ky:"Кочкул көк баш жагы, PT Sans, карточкаларда рамка. Костюмдар, көйнөктөр, бут кийим."},
      colors:P("F4F5F7 FFFFFF 14213D 4B5563 14213D FFFFFF DDE1E7 14213D FFFFFF 0E1730 E5E7EB 14213D 6B7280 0F766E FFFFFF B91C1C FFFFFF"),
      font:"PT Sans", base:16, hw:700, radius:4, shadow:"soft", btn:"filled", header:"logo_left", hero:{style:"split",overlay:0,align:"left"}, cats:"list",
      cols:[4,3,2], card:"standard", cardShadow:false, cardBorder:true, stock:"low_only", category:true,
      shop:"Султан", tagline:"Мужская одежда", heroT:"Костюмы к сезону", heroP:"Классика и slim fit, рубашки и обувь. Подгонка по фигуре в день покупки.",
      heroBg:"linear-gradient(135deg,#2B3A5C,#14213D 55%,#6B7A99)", products:"men" },
    { code:"cl_sport", name:{ru:"Спорт",ky:"Спорт",en:"Sport",tr:"Spor",uz:"Sport"}, mode:"light",
      for:{ru:"Белый, чёрный и красный, узкий шрифт. Спортивная одежда, форма.", ky:"Ак, кара жана кызыл, ичке шрифт. Спорт кийими, форма."},
      colors:P("FFFFFF F6F7F9 0B0F19 4B5563 E11D48 FFFFFF E5E7EB 0B0F19 FFFFFF 0B0F19 E5E7EB 0B0F19 6B7280 15803D FFFFFF E11D48 FFFFFF"),
      font:"Roboto Condensed", base:16, hw:800, radius:8, shadow:"soft", btn:"filled", header:"logo_left", hero:{style:"cover",overlay:0.35,align:"left"}, cats:"chips",
      cols:[4,3,2], card:"standard", cardShadow:false, cardBorder:false, stock:"low_only", category:true,
      shop:"ARENA", tagline:"Спортивная одежда", heroT:"ФОРМА ДЛЯ КОМАНД", heroP:"Футбол, борьба, бег. Нанесение номеров и логотипа за 2 дня.",
      heroBg:"linear-gradient(120deg,#3A3F4B,#7A1F33 55%,#1A1E29)", products:"sport" },
    { code:"cl_modest", name:{ru:"Хиджаб и этно",ky:"Хиджаб жана этно",en:"Modest & Ethnic",tr:"Tesettür ve Etnik",uz:"Hijob va etno"}, mode:"light",
      for:{ru:"Изумруд и песок, крупные фото. Хиджабы, платья в пол, национальная одежда.", ky:"Зымырыт жана кум, чоң сүрөттөр. Хиджабдар, узун көйнөктөр, улуттук кийим."},
      colors:P("F6F3EC FFFFFF 1E2B26 55625C 0F5B4A FFFFFF E3DCCB 0F5B4A FFFFFF 0B3F33 F1E9D2 0F5B4A 6B6F6A A16207 FFFFFF 9F1239 FFFFFF"),
      font:"Noto Sans", base:16, hw:700, radius:14, shadow:"soft", btn:"filled", header:"logo_center", hero:{style:"cover",overlay:0.3,align:"center"}, cats:"tiles",
      cols:[3,2,2], card:"large", cardShadow:true, cardBorder:false, stock:"low_only", category:false,
      shop:"Нурия", tagline:"Хиджабы и платья", heroT:"Платья в пол и хиджабы", heroP:"Повседневные и праздничные. Ткани из Турции и Узбекистана.",
      heroBg:"linear-gradient(135deg,#1F6B58,#0F5B4A 50%,#C9B27C)", products:"modest" },
    { code:"cl_evening", name:{ru:"Вечерний и свадебный",ky:"Кечки жана той кийими",en:"Evening & Bridal",tr:"Abiye ve Gelinlik",uz:"Kechki va to'y kiyimi"}, mode:"light",
      for:{ru:"Слоновая кость и винный, одна колонка на телефоне, остаток скрыт. Вечерние и свадебные платья.", ky:"Пил сөөгү жана шарап түсү, телефондо бир тилке, калдык жашырылган. Кечки жана той көйнөктөрү."},
      colors:P("FBF7F4 FFFFFF 2A1E22 6E5A60 7F1D3A FFFFFF EFE3DE FFFFFF 2A1E22 F3E8E4 2A1E22 7F1D3A 75666A 2A1E22 FFFFFF 7F1D3A FFFFFF"),
      font:"Montserrat", base:15, hw:500, radius:6, shadow:"soft", btn:"outline", header:"logo_center", hero:{style:"cover",overlay:0.2,align:"center"}, cats:"tiles",
      cols:[3,2,1], card:"large", cardShadow:false, cardBorder:false, stock:"hidden", category:false,
      shop:"Atelier Rosa", tagline:"Вечерние и свадебные платья", heroT:"Свадебный сезон 2027", heroP:"Платья по записи. Примерка в салоне, подгонка включена.",
      heroBg:"linear-gradient(135deg,#5A2A3A,#7F1D3A 50%,#C9A9B2)", products:"evening" },
    { code:"cl_dark_luxe", name:{ru:"Тёмный люкс",ky:"Караңгы люкс",en:"Dark Luxe",tr:"Koyu Lüks",uz:"Qorong'i lyuks"}, mode:"dark",
      for:{ru:"Чёрный и золото, без теней, кнопки контуром. Премиум, кожа, аксессуары.", ky:"Кара жана алтын, көлөкөсүз, контур баскычтар. Премиум, булгаары, аксессуарлар."},
      colors:P("0D0D0D 171717 F5F1E8 B5AE9F D4AF37 0D0D0D 2A2722 0D0D0D F5F1E8 000000 CFC8B8 E6C766 9A9384 D4AF37 0D0D0D B91C1C FFFFFF"),
      font:"Montserrat", base:15, hw:600, radius:2, shadow:"none", btn:"outline", header:"logo_center", hero:{style:"cover",overlay:0.35,align:"center"}, cats:"list",
      cols:[3,2,2], card:"large", cardShadow:false, cardBorder:true, stock:"low_only", category:false,
      shop:"NOIR", tagline:"Кожа и аксессуары", heroT:"Кожа ручной работы", heroP:"Куртки, сумки, ремни. Гравировка инициалов в подарок.",
      heroBg:"linear-gradient(135deg,#2B2520,#0D0D0D 55%,#6B5A2E)", products:"luxe" },
    { code:"cl_bazaar", name:{ru:"Яркий базар",ky:"Жаркын базар",en:"Bright Bazaar",tr:"Renkli Pazar",uz:"Yorqin bozor"}, mode:"light",
      for:{ru:"Оранжевый, 5 колонок, остаток виден всегда. Рынки (Дордой, Ош-базар), опт.", ky:"Кызгылт сары, 5 тилке, калдык ар дайым көрүнөт. Базарлар (Дордой, Ош базары), дүң."},
      colors:P("FFF7ED FFFFFF 1C1917 57534E C2410C FFFFFF FED7AA C2410C FFFFFF 1C1917 FFEDD5 B91C1C 78716C 15803D FFFFFF B91C1C FFFFFF"),
      font:"Nunito", base:16, hw:800, radius:12, shadow:"soft", btn:"filled", header:"logo_left", hero:{style:"card",overlay:0,align:"left"}, cats:"chips",
      cols:[5,3,2], card:"standard", cardShadow:true, cardBorder:false, stock:"always", category:true,
      shop:"Дордой Текстиль", tagline:"Опт и розница · контейнер 12-34", heroT:"Опт от 10 штук — минус 15 %", heroP:"Футболки, спортивки, детское. Отправка в регионы каждый день.",
      heroBg:"linear-gradient(135deg,#F59E0B,#C2410C 55%,#FDE68A)", products:"bazaar" },
  ];
  const byCode = c => THEMES.find(t => t.code === c);

  /* ---------- товары (пример; размеры по сфере clothing, цвета — hex как colors[].hex) ---------- */
  const CLR = { white:"#F4F4F2", black:"#151515", grey:"#8A8F98", navy:"#1F2A5A", beige:"#D9C7A7", olive:"#6B7A3A", red:"#B91C1C", pink:"#F2A6C0", blue:"#3B82F6", yellow:"#F6C343", green:"#2E7D4F", wine:"#7F1D3A", ivory:"#F3EBDD", emerald:"#0F5B4A", sand:"#D8C8A4", gold:"#C9A24A", brown:"#6B4F2A", lime:"#C6F432", orange:"#F97316" };
  const SETS = {
    basic:[ ["Футболка хлопок oversize",1290,null,["white","black","grey"],"S · M · L · XL","tshirt",true,0],
            ["Брюки прямые шерсть",4900,5900,["black","beige"],"44 · 46 · 48 · 50","pants",false,2],
            ["Рубашка лён",3400,null,["white","beige"],"S · M · L","jacket",false,0],
            ["Свитшот базовый",2600,null,["grey","black","navy"],"XS · S · M · L · XL","hoodie",false,0],
            ["Пальто-кокон",12900,null,["beige","black"],"42 · 44 · 46","jacket",true,1],
            ["Юбка миди",3100,3900,["black"],"42 · 44 · 46 · 48","dress",false,0],
            ["Лонгслив",1690,null,["white","black"],"S · M · L · XL","tshirt",false,0],
            ["Джинсы прямые",4200,null,["navy","black"],"W27 · W28 · W29 · W30","pants",false,0] ],
    women:[ ["Платье миди с поясом",6900,8400,["wine","ivory","black"],"42 · 44 · 46 · 48","dress",true,0],
            ["Костюм двойка твид",14900,null,["beige","black"],"42 · 44 · 46","jacket",true,2],
            ["Блуза шёлк",5400,null,["ivory","pink"],"42 · 44 · 46 · 48","tshirt",false,0],
            ["Пальто кашемир",24900,null,["beige","grey"],"42 · 44 · 46","jacket",false,1],
            ["Юбка плиссе",4600,5900,["ivory","black"],"42 · 44 · 46","dress",false,0],
            ["Сумка кожаная",8900,null,["brown","black"],"один размер","bag",false,0] ],
    street:[ ["Худи oversize BLOCK 7",3900,null,["black","lime","grey"],"S · M · L · XL · XXL","hoodie",true,0],
            ["Карго широкие",4400,5200,["black","olive","beige"],"S · M · L · XL","pants",false,2],
            ["Кроссовки Runner 2",7900,null,["white","black"],"39 · 40 · 41 · 42 · 43 · 44","shoe",true,0],
            ["Футболка принт DROP 03",1900,null,["black","white"],"S · M · L · XL","tshirt",false,0],
            ["Ветровка нейлон",5600,6900,["black","lime"],"M · L · XL","jacket",false,0],
            ["Шапка бини",900,null,["black","lime","grey"],"один размер","bag",false,0],
            ["Джоггеры флис",3300,null,["grey","black"],"S · M · L · XL","pants",false,1],
            ["Бомбер стёганый",6900,null,["black","olive"],"M · L · XL","jacket",false,0] ],
    kids:[ ["Комбинезон зимний",4900,5900,["blue","pink","yellow"],"86 · 92 · 98 · 104 · 110","hoodie",true,0],
            ["Куртка демисезон",3600,null,["red","navy","green"],"110 · 116 · 122 · 128","jacket",false,2],
            ["Школьная форма (жилет)",1900,null,["navy","grey"],"122 · 128 · 134 · 140 · 146","jacket",false,0],
            ["Футболка с принтом",690,890,["white","yellow","blue"],"98 · 104 · 110 · 116","tshirt",false,0],
            ["Платье нарядное",2900,null,["pink","ivory"],"104 · 110 · 116 · 122","dress",true,0],
            ["Кроссовки на липучке",2200,null,["white","blue","pink"],"26 · 27 · 28 · 29 · 30 · 31","shoe",false,1],
            ["Джинсы с резинкой",1700,null,["navy"],"98 · 104 · 110 · 116 · 122","pants",false,0],
            ["Шапка с помпоном",590,null,["yellow","pink","blue"],"50 · 52 · 54","bag",false,0] ],
    men:[ ["Костюм классический slim",18900,21900,["navy","black","grey"],"46 · 48 · 50 · 52 · 54","jacket",true,0],
            ["Рубашка хлопок non-iron",2900,null,["white","blue"],"39 · 40 · 41 · 42 · 43","tshirt",false,0],
            ["Туфли дерби кожа",8900,null,["black","brown"],"40 · 41 · 42 · 43 · 44","shoe",false,2],
            ["Брюки чинос",3900,null,["beige","navy","olive"],"46 · 48 · 50 · 52","pants",false,0],
            ["Пальто шерсть",16900,null,["grey","navy"],"48 · 50 · 52","jacket",true,1],
            ["Ремень кожаный",1900,null,["black","brown"],"95 · 100 · 105 · 110","bag",false,0],
            ["Поло пике",2200,2700,["white","navy","green"],"M · L · XL · XXL","tshirt",false,0],
            ["Джемпер мериносовый",5400,null,["grey","navy","wine"],"M · L · XL","hoodie",false,0] ],
    sport:[ ["Футбольная форма (комплект)",2900,null,["red","white","blue"],"S · M · L · XL · XXL","tshirt",true,0],
            ["Кроссовки для бега",6900,8400,["black","white","red"],"38 · 39 · 40 · 41 · 42 · 43 · 44","shoe",false,2],
            ["Костюм спортивный",4900,null,["black","navy"],"S · M · L · XL","hoodie",false,0],
            ["Леггинсы компрессионные",2400,null,["black","grey"],"XS · S · M · L","pants",false,0],
            ["Ветровка беговая",3900,null,["red","black"],"S · M · L · XL","jacket",true,0],
            ["Борцовское трико",2600,null,["red","blue"],"S · M · L","tshirt",false,1],
            ["Шорты баскетбольные",1700,2100,["black","white","red"],"S · M · L · XL · XXL","pants",false,0],
            ["Сумка спортивная 40 л",2900,null,["black","red"],"один размер","bag",false,0] ],
    modest:[ ["Платье в пол с вышивкой",7900,null,["emerald","sand","wine"],"42 · 44 · 46 · 48 · 50","dress",true,0],
            ["Хиджаб шифон",690,890,["emerald","sand","black","ivory"],"один размер","scarf",false,0],
            ["Абая классическая",6400,null,["black","emerald"],"S · M · L · XL","dress",false,2],
            ["Платок жоргу (шёлк)",1900,null,["ivory","gold","wine"],"90 × 90","scarf",false,0],
            ["Чапан женский",9900,null,["wine","emerald"],"44 · 46 · 48","jacket",true,0],
            ["Туника с брюками",4400,5200,["sand","emerald"],"42 · 44 · 46 · 48","tshirt",false,1] ],
    evening:[ ["Платье вечернее атлас",18900,null,["wine","ivory","black"],"42 · 44 · 46","dress",true,0],
            ["Свадебное платье А-силуэт",49000,59000,["ivory","white"],"42 · 44 · 46 · 48","dress",true,0],
            ["Платье-футляр с кружевом",14900,null,["wine","black"],"42 · 44 · 46","dress",false,0],
            ["Накидка из органзы",6900,null,["ivory","wine"],"один размер","scarf",false,0],
            ["Платье выпускное",22900,26900,["pink","ivory"],"40 · 42 · 44","dress",false,0],
            ["Клатч атласный",3900,null,["ivory","wine","gold"],"один размер","bag",false,0] ],
    luxe:[ ["Куртка кожаная косуха",34900,null,["black","brown"],"46 · 48 · 50 · 52","jacket",true,0],
            ["Сумка кожа crazy horse",18900,22900,["brown","black"],"один размер","bag",false,2],
            ["Ремень с латунной пряжкой",4900,null,["black","brown"],"95 · 100 · 105 · 110","bag",false,0],
            ["Перчатки кожа и кашемир",5900,null,["black","brown"],"8 · 8,5 · 9 · 9,5","scarf",false,0],
            ["Ботинки челси",21900,null,["black","brown"],"40 · 41 · 42 · 43 · 44","shoe",true,1],
            ["Портфель кожаный",27900,null,["brown"],"один размер","bag",false,0] ],
    bazaar:[ ["Футболка хлопок (упак. 10)",3500,4200,["white","black","grey","navy","red"],"S–XXL","tshirt",true,0],
            ["Спортивный костюм",1890,null,["black","navy","grey"],"46 · 48 · 50 · 52 · 54","hoodie",false,0],
            ["Детская футболка (упак. 12)",2400,null,["yellow","pink","blue","white"],"98–128","tshirt",false,0],
            ["Джинсы мужские",1490,1790,["navy","black"],"29 · 30 · 31 · 32 · 33 · 34","pants",false,2],
            ["Носки (упак. 24 пары)",960,null,["black","white","grey"],"41–45","bag",false,0],
            ["Куртка демисезон",2900,null,["black","olive","navy"],"48 · 50 · 52 · 54","jacket",true,0],
            ["Кроссовки (упак. 6 пар)",8900,9900,["white","black"],"40–45","shoe",false,0],
            ["Платье летнее",990,null,["red","blue","green","yellow"],"44 · 46 · 48","dress",false,1],
            ["Шапка трикотаж",190,null,["black","grey","navy"],"один размер","bag",false,0],
            ["Полотенце махровое",350,null,["white","blue","pink"],"70 × 140","scarf",false,0] ],
  };
  const LONG = ["Платье миди из вискозы с цветочным принтом и съёмным поясом, рукав три четверти, коллекция весна–лето 2026",
                "Куртка демисезонная утеплённая с капюшоном на кнопках и двумя боковыми карманами, водоотталкивающая ткань",
                "Комплект из трёх базовых футболок из органического хлопка повышенной плотности 220 г/м² без принта",
                "Костюм классический двойка из шерсти с добавлением эластана, приталенный силуэт, брюки со стрелками"];

  /* ---------- силуэты (viewBox 0 0 120 160), заглушка «вешалка» для «нет фото» (ТЗ 3.6) ---------- */
  const SIL = {
    tshirt:"M38 22 L22 30 L8 52 L24 62 L32 52 L32 128 L88 128 L88 52 L96 62 L112 52 L98 30 L82 22 C78 32 70 36 60 36 C50 36 42 32 38 22 Z",
    dress:"M44 18 L36 42 L30 60 L20 148 L100 148 L90 60 L84 42 L76 18 C72 28 66 32 60 32 C54 32 48 28 44 18 Z",
    hoodie:"M40 30 L20 38 L8 74 L26 80 L30 68 L30 140 L90 140 L90 68 L94 80 L112 74 L100 38 L80 30 C80 18 70 10 60 10 C50 10 40 18 40 30 Z",
    pants:"M34 14 L86 14 L92 150 L66 150 L60 70 L54 150 L28 150 Z",
    jacket:"M36 22 L18 32 L8 66 L26 72 L30 62 L30 144 L57 144 L60 50 L63 144 L90 144 L90 62 L94 72 L112 66 L102 32 L84 22 L60 42 Z",
    shoe:"M12 112 C12 96 30 92 44 84 L62 60 C68 56 74 60 80 66 L106 90 C114 96 118 108 112 120 L12 120 Z",
    scarf:"M60 14 C44 14 36 30 36 48 C36 62 42 70 50 74 L22 148 L98 148 L70 74 C78 70 84 62 84 48 C84 30 76 14 60 14 Z",
    bag:"M28 62 L92 62 L100 146 L20 146 Z M44 62 C44 34 76 34 76 62",
    hanger:"M60 24 a9 9 0 1 1 9 9 L60 42 L10 106 L110 106 Z" };
  function photo(kind, hex, ph){
    const stroke = "rgba(127,127,127,.5)";
    const d = SIL[kind] || SIL.tshirt;
    return '<svg viewBox="0 0 120 160" aria-hidden="true"><path d="' + d + '" fill="' + hex + '" stroke="' + stroke + '" stroke-width="1.5" fill-rule="evenodd"/></svg>';
  }
  function placeholder(t){ /* заглушка «нет фото»: вешалка в цветах темы */
    return '<svg viewBox="0 0 120 160" aria-hidden="true"><path d="' + SIL.hanger + '" fill="none" stroke="' + t.colors.text_muted + '" stroke-width="3" stroke-linejoin="round" opacity=".7"/></svg>';
  }

  const ICONS = {
    magnify:"M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z",
    cart:"M17,18A2,2 0 0,1 19,20A2,2 0 0,1 17,22C15.89,22 15,21.1 15,20C15,18.89 15.89,18 17,18M1,2H4.27L5.21,4H20A1,1 0 0,1 21,5C21,5.17 20.95,5.34 20.88,5.5L17.3,11.97C16.96,12.58 16.3,13 15.55,13H8.1L7.2,14.63L7.17,14.75A0.25,0.25 0 0,0 7.42,15H19V17H7C5.89,17 5,16.1 5,15C5,14.65 5.09,14.32 5.24,14.04L6.6,11.59L3,4H1V2M7,18A2,2 0 0,1 9,20A2,2 0 0,1 7,22C5.89,22 5,21.1 5,20C5,18.89 5.89,18 7,18M16,11L18.78,6H6.14L8.5,11H16Z",
    heart:"M12.1,18.55L12,18.65L11.89,18.55C7.14,14.24 4,11.39 4,8.5C4,6.5 5.5,5 7.5,5C9.04,5 10.54,6 11.07,7.36H12.93C13.46,6 14.96,5 16.5,5C18.5,5 20,6.5 20,8.5C20,11.39 16.86,14.24 12.1,18.55M16.5,3C14.76,3 13.09,3.81 12,5.08C10.91,3.81 9.24,3 7.5,3C4.42,3 2,5.41 2,8.5C2,12.27 5.4,15.36 10.55,20.03L12,21.35L13.45,20.03C18.6,15.36 22,12.27 22,8.5C22,5.41 19.58,3 16.5,3Z",
    account:"M12,4A4,4 0 0,1 16,8A4,4 0 0,1 12,12A4,4 0 0,1 8,8A4,4 0 0,1 12,4M12,6A2,2 0 0,0 10,8A2,2 0 0,0 12,10A2,2 0 0,0 14,8A2,2 0 0,0 12,6M12,13C14.67,13 20,14.33 20,17V20H4V17C4,14.33 9.33,13 12,13M12,14.9C9.03,14.9 5.9,16.36 5.9,17V18.1H18.1V17C18.1,16.36 14.97,14.9 12,14.9Z",
    menu:"M3,6H21V8H3V6M3,11H21V13H3V11M3,16H21V18H3V16Z",
    close:"M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z",
    check:"M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z",
    info:"M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z",
    web:"M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
    palette:"M17.5,12A1.5,1.5 0 0,1 16,10.5A1.5,1.5 0 0,1 17.5,9A1.5,1.5 0 0,1 19,10.5A1.5,1.5 0 0,1 17.5,12M14.5,8A1.5,1.5 0 0,1 13,6.5A1.5,1.5 0 0,1 14.5,5A1.5,1.5 0 0,1 16,6.5A1.5,1.5 0 0,1 14.5,8M9.5,8A1.5,1.5 0 0,1 8,6.5A1.5,1.5 0 0,1 9.5,5A1.5,1.5 0 0,1 11,6.5A1.5,1.5 0 0,1 9.5,8M6.5,12A1.5,1.5 0 0,1 5,10.5A1.5,1.5 0 0,1 6.5,9A1.5,1.5 0 0,1 8,10.5A1.5,1.5 0 0,1 6.5,12M12,3A9,9 0 0,0 3,12A9,9 0 0,0 12,21A2.5,2.5 0 0,0 14.5,18.5C14.5,17.88 14.27,17.32 13.88,16.88C13.5,16.44 13.25,15.88 13.25,15.25A2.5,2.5 0 0,1 15.75,12.75H17.5A5,5 0 0,0 22.5,7.75C22.5,5.1 20.4,3 17.75,3H12Z",
    monitor:"M21,16H3V4H21M21,2H3C1.89,2 1,2.89 1,4V16A2,2 0 0,0 3,18H10V20H8V22H16V20H14V18H21A2,2 0 0,0 23,16V4C23,2.89 22.1,2 21,2Z",
    cellphone:"M17,19H7V5H17M17,1H7C5.89,1 5,1.89 5,3V21A2,2 0 0,0 7,23H17A2,2 0 0,0 19,21V3C19,1.89 18.1,1 17,1Z",
    eye:"M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z",
    history:"M13.5,8H12V13L16.28,15.54L17,14.33L13.5,12.25V8M13,3A9,9 0 0,0 4,12H1L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3",
    wifioff:"M2.28,3L1,4.27L2.47,5.74C2.04,6 1.61,6.29 1.2,6.6L3,9C3.53,8.6 4.08,8.25 4.66,7.93L6.89,10.16C6.15,10.5 5.44,10.91 4.8,11.4L6.6,13.8C7.38,13.22 8.26,12.77 9.2,12.47L11.75,15C10.5,15.07 9.34,15.5 8.4,16.2L12,21L14.46,17.73L17.74,21L19,19.72M12,3C9.85,3 7.8,3.38 5.9,4.07L8.29,6.47C9.5,6.16 10.72,6 12,6C15.38,6 18.5,7.11 21,9L22.8,6.6C19.79,4.34 16.06,3 12,3M12,9C11.62,9 11.25,9 10.88,9.05L14.07,12.25C15.29,12.53 16.43,13.07 17.4,13.8L19.2,11.4C17.2,9.89 14.7,9 12,9Z",
    lock:"M12,17A2,2 0 0,0 14,15C14,13.89 13.1,13 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z",
    image:"M19,19H5V5H19M19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M13.96,12.29L11.21,15.83L9.25,13.47L6.5,17H17.5L13.96,12.29Z",
    sort:"M3,13H15V11H3M3,6V8H21V6M3,18H9V16H3V18Z",
    store:"M18.36 9L18.96 12H5.04L5.64 9H18.36M20 4H4V6H20V4M20 7H4L3 12V14H4V20H14V14H18V20H20V14H21V12L20 7M6 18V14H12V18H6Z",
    tshirt:"M16,21H8A1,1 0 0,1 7,20V12.07L5.7,13.07C5.31,13.46 4.68,13.46 4.29,13.07L1.46,10.29C1.07,9.9 1.07,9.27 1.46,8.88L7.34,3H9C9,4.1 10.34,5 12,5C13.66,5 15,4.1 15,3H16.66L22.54,8.88C22.93,9.27 22.93,9.9 22.54,10.29L19.71,13.07C19.32,13.46 18.69,13.46 18.3,13.07L17,12.07V20A1,1 0 0,1 16,21",
    chevronDown:"M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z",
    send:"M2,21L23,12L2,3V10L17,12L2,14V21Z",
    phone:"M6.62,10.79C8.06,13.62 10.38,15.94 13.21,17.38L15.41,15.18C15.69,14.9 16.08,14.82 16.43,14.93C17.55,15.3 18.75,15.5 20,15.5A1,1 0 0,1 21,16.5V20A1,1 0 0,1 20,21A17,17 0 0,1 3,4A1,1 0 0,1 4,3H7.5A1,1 0 0,1 8.5,4C8.5,5.25 8.7,6.45 9.07,7.57C9.18,7.92 9.1,8.31 8.82,8.59L6.62,10.79Z",
    whatsapp:"M12.04 2C6.58 2 2.13 6.45 2.13 11.91C2.13 13.66 2.59 15.36 3.45 16.86L2.05 22L7.3 20.62C8.75 21.41 10.38 21.83 12.04 21.83C17.5 21.83 21.95 17.38 21.95 11.92C21.95 9.27 20.92 6.78 19.05 4.91C17.18 3.03 14.69 2 12.04 2M12.05 3.67C14.25 3.67 16.31 4.53 17.87 6.09C19.42 7.65 20.28 9.72 20.28 11.92C20.28 16.46 16.58 20.15 12.04 20.15C10.56 20.15 9.11 19.76 7.85 19L7.55 18.83L4.43 19.65L5.26 16.61L5.06 16.29C4.24 15 3.8 13.47 3.8 11.91C3.81 7.37 7.5 3.67 12.05 3.67M8.53 7.33C8.37 7.33 8.1 7.39 7.87 7.64C7.65 7.89 7 8.5 7 9.71C7 10.93 7.89 12.1 8 12.27C8.14 12.44 9.76 14.94 12.25 16C12.84 16.27 13.3 16.42 13.66 16.53C14.25 16.72 14.79 16.69 15.22 16.63C15.7 16.56 16.68 16.03 16.89 15.45C17.1 14.87 17.1 14.38 17.04 14.27C16.97 14.17 16.81 14.11 16.56 14C16.31 13.86 15.09 13.26 14.87 13.18C14.64 13.1 14.5 13.06 14.31 13.3C14.15 13.55 13.67 14.11 13.53 14.27C13.38 14.44 13.24 14.46 13 14.34C12.74 14.21 11.94 13.95 11 13.11C10.26 12.45 9.77 11.64 9.62 11.39C9.5 11.15 9.64 11 9.73 10.89C9.84 10.78 10 10.6 10.1 10.45C10.23 10.31 10.27 10.2 10.35 10.04C10.43 9.87 10.39 9.73 10.33 9.61C10.27 9.5 9.77 8.26 9.56 7.77C9.36 7.29 9.16 7.35 9 7.34C8.86 7.34 8.7 7.33 8.53 7.33Z",
    telegram:"M9.78,18.65L10.06,14.42L17.74,7.5C18.08,7.19 17.67,7.04 17.22,7.31L7.74,13.3L3.64,12C2.76,11.75 2.75,11.14 3.84,10.7L19.81,4.54C20.54,4.21 21.24,4.72 20.96,5.84L18.24,18.65C18.05,19.56 17.5,19.78 16.74,19.36L12.6,16.3L10.61,18.23C10.38,18.46 10.19,18.65 9.78,18.65Z",
    instagram:"M7.8,2H16.2C19.4,2 22,4.6 22,7.8V16.2A5.8,5.8 0 0,1 16.2,22H7.8C4.6,22 2,19.4 2,16.2V7.8A5.8,5.8 0 0,1 7.8,2M7.6,4A3.6,3.6 0 0,0 4,7.6V16.4C4,18.39 5.61,20 7.6,20H16.4A3.6,3.6 0 0,0 20,16.4V7.6C20,5.61 18.39,4 16.4,4H7.6M17.25,5.5A1.25,1.25 0 0,1 18.5,6.75A1.25,1.25 0 0,1 17.25,8A1.25,1.25 0 0,1 16,6.75A1.25,1.25 0 0,1 17.25,5.5M12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z",
    hanger:"M12,2A3,3 0 0,1 15,5C15,6.55 13.86,7.81 12.5,7.97V9.2L21.5,14.5C22.9,15.3 22.4,17 20.8,17H3.2C1.6,17 1.1,15.3 2.5,14.5L11.5,9.2V7.5C11.5,7.5 11.5,7 12,7A2,2 0 1,0 10,5H8A4,4 0 0,1 12,2M12,10.5L4.5,15H19.5L12,10.5Z",
    arrowLeft:"M20,11V13H8L13.5,18.5L12.08,19.92L4.16,12L12.08,4.08L13.5,5.5L8,11H20Z",
    openIn:"M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z",
    play:"M8,5.14V19.14L19,12.14L8,5.14Z",
    sparkle:"M19,1L17.74,3.75L15,5L17.74,6.26L19,9L20.25,6.26L23,5L20.25,3.75M9,4L6.5,9.5L1,12L6.5,14.5L9,20L11.5,14.5L17,12L11.5,9.5M19,15L17.74,17.74L15,19L17.74,20.25L19,23L20.25,20.25L23,19L20.25,17.74Z",
    alert:"M13,13H11V7H13M13,17H11V15H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
    list:"M3,4H7V8H3V4M9,5V7H21V5H9M3,10H7V14H3V10M9,11V13H21V11H9M3,16H7V20H3V16M9,17V19H21V17H9Z",
  };
  const icon = (n, s) => { s = s || 18; return '<svg class="ic" width="'+s+'" height="'+s+'" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="'+(ICONS[n]||ICONS.info)+'"/></svg>'; };

  /* ---------- кыргызский — проверка длинных подписей (переводы для кода делает Sonnet, проверяет носитель) ---------- */
  const KY = {
    /* витрина */ "В корзину":"Корзинага", "Корзина":"Корзина", "Поиск":"Издөө", "Найти: платье, худи, размер…":"Издөө: көйнөк, худи, өлчөм…", "Все товары":"Бардык товарлар",
    "Новинки":"Жаңылыктар", "Скидки":"Арзандатуулар", "Категории":"Категориялар", "Все":"Баары", "Смотреть все":"Баарын көрүү", "Показать ещё":"Дагы көрсөтүү",
    "Размер":"Өлчөм", "Цвет":"Түс", "Цена":"Баасы", "до 3 000":"3 000гө чейин", "Бренд":"Бренд", "Сезон":"Мезгил", "Сбросить":"Баштапкы абалга",
    "Новинка":"Жаңы", "Осталось 2":"2 калды", "Осталось 1":"1 калды", "В наличии":"Бар", "Нет в наличии":"Жок", "Смотреть каталог":"Каталогду көрүү", "Подобрать размер":"Өлчөм тандоо",
    "Товары скоро появятся":"Товарлар жакында чыгат", "Магазин наполняет витрину. Напишите нам — подберём и отложим вещь.":"Дүкөн витринаны толтуруп жатат. Бизге жазыңыз — буюмду тандап, калтырып коёбуз.",
    "Написать в WhatsApp":"WhatsApp'ка жазуу", "Написать в Telegram":"Telegram'га жазуу", "Пока всё — новые поступления на этой неделе":"Азырынча ушул — жаңы товарлар ушул жумада",
    "Контакты":"Байланыш", "Покупателям":"Сатып алуучуларга", "Доставка и самовывоз":"Жеткирүү жана өзү алып кетүү", "Обмен и возврат 14 дней":"14 күн алмаштыруу жана кайтаруу",
    "Таблица размеров":"Өлчөмдөр таблицасы", "Мы в соцсетях":"Биз социалдык тармактарда", "Работает на NurCRM":"NurCRM менен иштейт", "Женское":"Аялдарга", "Мужское":"Эркектерге", "Детское":"Балдарга",
    "Обувь":"Бут кийим", "Аксессуары":"Аксессуарлар", "Платья":"Көйнөктөр", "Верхняя одежда":"Сырт кийим", "Костюмы":"Костюмдар", "Рубашки":"Көйнөктөр", "Хиджабы":"Хиджабдар", "Национальное":"Улуттук",
    "Худи":"Худи", "Кроссовки":"Кроссовкалар", "Штаны":"Шымдар", "Форма":"Форма", "Бег":"Чуркоо", "Сумки":"Сумкалар", "Куртки":"Курткалар", "Опт":"Дүң", "Свадебные":"Той көйнөктөрү", "Вечерние":"Кечки",
    /* редактор */ "Редактор сайта":"Сайт редактору", "Тема":"Тема", "Темы сайта":"Сайттын темалары", "Готовые цвета":"Даяр түстөр", "Цвета":"Түстөр", "Шрифт и форма":"Шрифт жана форма", "Шапка":"Баш жагы",
    "Главный блок":"Башкы блок", "Разделы и порядок":"Бөлүмдөр жана тартип", "Товары":"Товарлар", "Карточка товара":"Товар карточкасы", "Корзина и заказ":"Корзина жана буйрутма", "Подвал":"Төмөнкү бөлүк",
    "Google и соцсети":"Google жана соцтармактар", "Telegram":"Telegram", "Чат на сайте":"Сайттагы чат", "Смотреть сайт":"Сайтты көрүү", "История версий":"Версиялар тарыхы", "Опубликовать":"Жарыялоо",
    "Всё опубликовано":"Баары жарыяланды", "Для вашего магазина":"Сиздин дүкөн үчүн", "Одежда и обувь":"Кийим жана бут кийим", "Продукты":"Азык-түлүк", "Для любого":"Ар кандай дүкөнгө", "Одежда":"Кийим",
    "Посмотреть":"Көрүү", "Применить к сайту":"Сайтка колдонуу", "Применена":"Колдонулду", "В черновике":"Долбоордо", "Порядок разделов для одежды":"Кийим үчүн бөлүмдөрдүн тартиби",
    "Предпросмотр черновика":"Долбоорду алдын ала көрүү", "Компьютер":"Компьютер", "Телефон":"Телефон", "Закрыть":"Жабуу", "Отмена":"Жокко чыгаруу", "Оставить ваш баннер?":"Баннериңизди калтырабызбы?",
    "Оставить мой баннер":"Менин баннеримди калтыруу", "Заменить на баннер темы":"Теманын баннерине алмаштыруу", "Применяю тему…":"Тема колдонулуп жатат…", "Повторить":"Кайталоо",
    "Опубликовать вместе":"Чогуу жарыялоо", "Только в черновик":"Долбоорго гана", "Тема в черновике":"Тема долбоордо", "Покупатели увидят после «Опубликовать»":"Сатып алуучулар «Жарыялоо» баскандан кийин көрөт",
    "Онлайн-витрина не подключена":"Онлайн-витрина туташтырылган эмес", "Редактор только для просмотра":"Редактор көрүү үчүн гана", "Подключить витрину":"Витринаны туташтыруу",
    /* маркетплейс */ "Маркетплейс":"Маркетплейс", "Продажи":"Сатуулар", "Склад":"Кампа", "Владелец":"Ээси", "Наборы":"Топтомдор", "Виды кассы":"Касса түрлөрү", "Темы":"Темалар", "Скоро":"Жакында",
    "Темы кассы":"Касса темалары", "Сайт":"Сайт", "Бесплатно":"Акысыз", "Куплено":"Сатылып алынды", "Открыть в редакторе сайта":"Сайт редакторунда ачуу", "Открыть в программе владельца":"Ээсинин программасында ачуу",
    "Для кого":"Кимге", "Как включить":"Кантип күйгүзүү", "Тема сайта":"Сайттын темасы", "Ищите темы кассы? Они во вкладке «Темы кассы»":"Касса темаларын издеп жатасызбы? Алар «Касса темалары» өтмөгүндө",
    "Применяется только в программе владельца при подключённой витрине":"Витрина туташканда ээсинин программасында гана колдонулат"
  };
  const Q = (s, r) => (r || document).querySelector(s);
  const QA = (s, r) => Array.from((r || document).querySelectorAll(s));
  function applyKy(on){
    QA('[data-ky], [data-key]').forEach(el => {
      if (el.children.length && !el.classList.contains('kyok')) return;
      const ru = el.dataset.ru != null ? el.dataset.ru : el.textContent.trim();
      if (el.dataset.ru == null) el.dataset.ru = ru;
      const m = ru.match(/^(.*?)(\s*[·—(].*)?$/); const base = m[1].trim(), tail = m[2] || '';
      const ky = KY[base] || KY[ru]; if (!ky) return;
      el.textContent = on ? (KY[ru] ? KY[ru] : ky + tail) : ru;
    });
    QA('input[placeholder]').forEach(i => { const ru = i.dataset.ru || i.placeholder; i.dataset.ru = ru; if (KY[ru]) i.placeholder = on ? KY[ru] : ru; });
  }
  const t = (label, key) => '<span'+(key?' data-key="'+key+'"':' data-ky')+'>'+label+'</span>';
  const fmt = n => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const money = n => fmt(n) + ' сом';

  /* ---------- контраст WCAG (для подписи под макетом) ---------- */
  function lum(h){ const c = h.replace('#',''); const f = x => { x /= 255; return x <= 0.03928 ? x/12.92 : Math.pow((x+0.055)/1.055, 2.4); };
    return 0.2126*f(parseInt(c.slice(0,2),16)) + 0.7152*f(parseInt(c.slice(2,4),16)) + 0.0722*f(parseInt(c.slice(4,6),16)); }
  const cr = (a, b) => { const x = lum(a), y = lum(b); return ((Math.max(x,y)+0.05)/(Math.min(x,y)+0.05)); };
  function contrastRows(th){ const c = th.colors; const out = (th.btn === 'outline');
    const rows = [["текст / фон", c.text, c.background], ["вторичный / фон", c.text_muted, c.background], ["вторичный / карточка", c.text_muted, c.surface],
      ["текст кнопки / кнопка", c.accent_text, c.accent], ["шапка", c.header_text, c.header_bg], ["подвал", c.footer_text, c.footer_bg],
      ["цена / карточка", c.price, c.surface], ["старая цена / карточка", c.old_price, c.surface], ["«Новинка»", c.badge_new_text, c.badge_new_bg], ["«−20 %»", c.badge_sale_text, c.badge_sale_bg]];
    if (out) rows.push(["контур кнопки / фон", c.accent, c.background]);
    return rows.map(r => ({ name:r[0], a:r[1], b:r[2], v:cr(r[1], r[2]) }));
  }

  /* ---------- CSS-переменные темы ---------- */
  function vars(th){
    let s = ''; KEYS.forEach(k => s += '--c-'+k+':'+th.colors[k]+';');
    s += "--font:'"+th.font+"',system-ui,sans-serif;--base:"+th.base+'px;--hw:'+th.hw+';--radius:'+th.radius+'px;--cols:'+th.cols[0]+';--cols-m:'+th.cols[2]+';';
    s += '--hero-bg:'+th.heroBg+';--hero-ov:'+th.hero.overlay+';--hero-tx:'+(th.hero.style==='cover' ? '#FFFFFF' : th.colors.text)+';';
    s += '--ph-bg:'+mix(th.colors.surface, th.colors.text_muted, 0.10)+';--ph-fg:'+th.colors.text_muted+';--mini-r:'+Math.min(th.radius/4, 4)+'px;--mini-cols:'+(th.cols[0] >= 4 ? 4 : 3)+';';
    return s;
  }
  function mix(a, b, k){ const p = h => [1,3,5].map(i => parseInt(h.slice(i,i+2),16)); const x = p(a), y = p(b);
    return '#' + x.map((v,i) => Math.round(v*(1-k) + y[i]*k).toString(16).padStart(2,'0')).join(''); }

  /* ---------- миниатюра 16:10 (превью в редакторе и Маркетплейсе; без текста) ---------- */
  function mini(th, extra){
    return '<div class="mini'+(th.header==='logo_center'?' center':'')+(extra?' '+extra:'')+'" style="'+vars(th)+'" aria-label="Превью темы">'+
      '<div class="mh"><i class="l"></i><i class="s"></i><i class="s"></i></div>'+
      '<div class="hero '+th.hero.style+'"><div class="img"></div><div class="tx"><i class="b"></i><i></i><i class="k'+(th.btn==='outline'?' outline':'')+'"></i></div></div>'+
      '<div class="cards">'+[0,1,2,3].slice(0, th.cols[0] >= 4 ? 4 : 3).map(()=> '<div><div class="ph"></div><i></i><i class="p"></i></div>').join('')+'</div></div>';
  }

  /* ---------- витрина: главная страница ----------
     opts: { phone, state: normal|empty|nophoto|long|few, cls } */
  function storefront(th, opts){
    opts = opts || {}; const ph = !!opts.phone; const st = opts.state || 'normal';
    const cats = catsOf(th);
    let items = SETS[th.products].slice();
    if (st === 'few') items = items.slice(0, 2);
    if (st === 'long') items = items.map((p, i) => i < 4 ? [LONG[i]].concat(p.slice(1)) : p);
    const header = '<div class="ann">'+t('Доставка по Бишкеку бесплатно от 3 000 сом · обмен 14 дней')+'</div>'+
      '<header class="hdr'+(th.header==='logo_center'?' center':'')+'"><div class="wrap">'+
      (ph ? '<button class="hbtn" aria-label="Меню">'+icon('menu',22)+'</button>' : '')+
      '<div class="logo"><i></i>'+th.shop+'</div>'+
      (ph ? '' : '<div class="search">'+icon('magnify',18)+'<span data-ky>Найти: платье, худи, размер…</span></div>')+
      '<div class="hacts">'+(ph?'<button class="hbtn" aria-label="Поиск">'+icon('magnify',22)+'</button>':'<button class="hbtn" aria-label="Избранное">'+icon('heart',20)+'</button><button class="hbtn" aria-label="Войти">'+icon('account',20)+'</button>')+
      '<button class="hbtn" aria-label="Корзина">'+icon('cart',20)+(ph?'':t('Корзина'))+'<span class="cnt">2</span></button></div></div>'+
      (th.cats === 'list' && !ph ? '<nav class="menu"><div class="wrap">'+cats.map((c,i)=>'<a'+(i===0?' class="on"':'')+'>'+t(c)+'</a>').join('')+'</div></nav>' : '')+
      '</header>';
    const hero = '<section class="hero wrap h-'+th.hero.style+(th.hero.align==='center'?' c':'')+(th.shadow!=='none'&&th.hero.style!=='split'?' sh-'+th.shadow:'')+'">'+
      '<div class="img"></div><div class="tx"><h1>'+th.heroT+'</h1><p>'+th.heroP+'</p><div style="display:flex;gap:10px;flex-wrap:wrap"><button class="sbtn p">'+t('Смотреть каталог')+'</button>'+(ph?'':'<button class="sbtn s">'+t('Подобрать размер')+'</button>')+'</div></div></section>';
    const catsHtml = th.cats === 'list' && !ph ? '' :
      '<section class="sec wrap"><div class="sec-h"><h2>'+t('Категории')+'</h2>'+(th.cats==='chips'?'':'<a>'+t('Смотреть все')+'</a>')+'</div>'+
      '<div class="cats '+th.cats+'">'+cats.map((c,i)=>'<a'+(i===0?' class="on"':'')+'>'+(th.cats==='tiles'?'<i></i>':'')+t(c)+'</a>').join('')+'</div></section>';
    const filters = '<div class="filt"><span class="lb">'+t('Размер')+'</span><button>S</button><button class="on">M</button><button>L</button><button>XL</button>'+
      '<span class="lb" style="margin-left:6px">'+t('Цвет')+'</span><button><span class="dot" style="background:#151515"></span>'+(ph?'':t('Чёрный'))+'</button><button><span class="dot" style="background:#F4F4F2"></span>'+(ph?'':t('Белый'))+'</button>'+
      '<button>'+t('Цена')+' · '+t('до 3 000')+'</button>'+(ph?'':'<button>'+t('Бренд')+' '+icon('chevronDown',16)+'</button><button>'+t('Сезон')+' '+icon('chevronDown',16)+'</button>')+
      '<span class="cnt">'+(st==='empty'?'0':st==='few'?'2':fmt(th.products==='bazaar'?1240:184))+' товаров · '+t('Новинки')+' ↓</span></div>';
    let gridHtml;
    if (st === 'empty'){
      gridHtml = '<div class="empty">'+icon('hanger',72)+'<h3>'+t('Товары скоро появятся')+'</h3><p>'+t('Магазин наполняет витрину. Напишите нам — подберём и отложим вещь.')+'</p>'+
        '<div class="acts"><button class="sbtn p">'+icon('whatsapp',18)+t('Написать в WhatsApp')+'</button><button class="sbtn s">'+icon('telegram',18)+t('Написать в Telegram')+'</button></div></div>';
    } else {
      gridHtml = '<div class="grid">'+items.map((p,i)=>card(th, p, i, st==='nophoto', ph)).join('')+'</div>'+
        (st === 'few' ? '<div class="few">'+icon('info',18)+t('Пока всё — новые поступления на этой неделе')+'</div>' :
         '<div class="more"><button class="sbtn s">'+t('Показать ещё')+' · 48</button></div>');
    }
    const products = '<section class="sec wrap"><div class="sec-h"><h2>'+t('Все товары')+'</h2>'+(ph?'':'<a>'+icon('sort',16)+' '+t('Новинки')+'</a>')+'</div>'+filters+gridHtml+'</section>';
    const footer = '<footer class="ftr"><div class="wrap"><div><div class="logo" style="margin-bottom:10px"><i style="background:var(--c-accent)"></i>'+th.shop+'</div><span>'+th.tagline+'</span><span>Бишкек, ул. Киевская 77 · 10:00–20:00</span><span>+996 555 12-34-56</span></div>'+
      '<div><h3>'+t('Покупателям')+'</h3><a>'+t('Доставка и самовывоз')+'</a><a>'+t('Обмен и возврат 14 дней')+'</a><a>'+t('Таблица размеров')+'</a></div>'+
      '<div><h3>'+t('Категории')+'</h3>'+cats.slice(0,4).map(c=>'<a>'+t(c)+'</a>').join('')+'</div>'+
      '<div><h3>'+t('Мы в соцсетях')+'</h3><div class="soc"><a aria-label="Instagram">'+icon('instagram',20)+'</a><a aria-label="Telegram">'+icon('telegram',20)+'</a><a aria-label="WhatsApp">'+icon('whatsapp',20)+'</a></div></div></div>'+
      '<div class="bot"><div class="wrap"><span>© 2026 '+th.shop+'</span><span>'+t('Работает на NurCRM')+'</span></div></div></footer>';
    return '<div class="site'+(ph?' phone':'')+(opts.cls?' '+opts.cls:'')+'" style="'+vars(th)+(opts.style||'')+'" data-btn="'+th.btn+'" data-card-border="'+(th.cardBorder?1:0)+'" data-card-shadow="'+(th.cardShadow?1:0)+'" lang="ru">'+
      header+'<div class="scroll">'+hero+catsHtml+products+footer+'</div></div>';
  }
  function catsOf(th){
    const m = { basic:["Все","Женское","Мужское","Верхняя одежда","Аксессуары"], women:["Все","Платья","Костюмы","Верхняя одежда","Сумки","Аксессуары"], street:["Все","Худи","Штаны","Кроссовки","Куртки","Аксессуары"],
      kids:["Все","Детское","Куртки","Обувь","Форма","Аксессуары","Новинки"], men:["Все","Костюмы","Рубашки","Верхняя одежда","Обувь","Аксессуары"], sport:["Все","Форма","Бег","Обувь","Сумки","Скидки"],
      modest:["Все","Платья","Хиджабы","Национальное","Верхняя одежда","Аксессуары"], evening:["Все","Вечерние","Свадебные","Аксессуары"], luxe:["Все","Куртки","Сумки","Обувь","Аксессуары"], bazaar:["Все","Опт","Мужское","Женское","Детское","Обувь","Аксессуары"] };
    return m[th.products];
  }
  function card(th, p, i, nophoto, ph){
    const [name, price, old, colors, sizes, kind, isNew, low] = p;
    const pct = old ? Math.round((1 - price/old)*100) : 0;
    const showStock = th.stock === 'always' || (th.stock === 'low_only' && low > 0);
    const stock = !showStock ? '' : (low > 0 ? '<div class="stock"><i></i>'+t('Осталось '+low)+'</div>' : '<div class="stock ok"><i></i>'+t('В наличии')+(th.stock==='always'?' · '+(40+i*17)+' шт':'')+'</div>');
    return '<article class="card '+th.card+'">'+
      '<div class="ph">'+(nophoto ? placeholder(th) : photo(kind, CLR[colors[0]]))+
      '<div class="badges">'+(isNew?'<span class="bdg new">'+t('Новинка')+'</span>':'')+(pct?'<span class="bdg sale">−'+pct+' %</span>':'')+'</div>'+
      (th.card==='compact'||ph?'':'<span class="fav" aria-label="В избранное">'+icon('heart',20)+'</span>')+'</div>'+
      '<div class="bd">'+(th.category?'<div class="cat">'+t(catsOf(th)[1 + (i % (catsOf(th).length-1))])+'</div>':'')+
      '<div class="nm">'+name+'</div>'+
      (th.card==='compact' ? '' : '<div class="dots">'+colors.slice(0,5).map(c=>'<i style="background:'+CLR[c]+'"></i>').join('')+(colors.length>5?'<small>+'+(colors.length-5)+'</small>':'')+'</div><div class="sz">'+sizes+'</div>')+
      '<div class="pr"><b>'+money(price)+'</b>'+(old?'<s>'+money(old)+'</s>':'')+'</div>'+stock+
      (th.card==='compact' && !ph ? '' : '<button class="sbtn p add">'+t('В корзину')+'</button>')+
      '</div></article>';
  }

  /* ---------- панель макета ---------- */
  function toolbar(frame, opts){
    const bar = document.createElement('div'); bar.className = 'tb';
    const states = opts.states || []; const sizes = opts.sizes || ['1296','1024'];
    const sizeLabel = { '1296':'1296×879', '1024':'1024×600 сенсор', 'phone':'390×844 телефон', '1440':'1440×900' };
    bar.innerHTML = '<a href="SiteThemes-Clothing-Index.html">← Макеты</a><b>'+opts.title+'</b>'+
      (opts.themes ? '<div class="grp"><span>Тема сайта</span>'+THEMES.map(th=>'<button data-th="'+th.code+'"'+(th.code===opts.theme?' class="on"':'')+'><i class="sw" style="background:'+th.colors.accent+'"></i>'+th.name.ru+'</button>').join('')+'</div>' : '')+
      (opts.appTheme === false ? '' : '<div class="grp"><span>Программа</span><button data-t="light" class="on">Светлая</button><button data-t="dark">Тёмная</button></div>')+
      (sizes.length > 1 ? '<div class="grp"><span>Окно</span>'+sizes.map((s,i)=>'<button data-s="'+s+'"'+(i===0?' class="on"':'')+'>'+sizeLabel[s]+'</button>').join('')+'</div>' : '')+
      '<div class="grp"><button data-ky>ky — длинные подписи</button>'+(opts.keys===false?'':'<button data-keys>ключи Tr.T</button>')+'</div>'+
      (states.length ? '<div class="grp"><span>Состояние</span>'+states.map((s,i)=>'<button data-st="'+s.id+'"'+(i===0?' class="on"':'')+'>'+s.label+'</button>').join('')+'</div>' : '');
    document.body.insertBefore(bar, document.body.firstChild);
    const sw = (sel, btn) => QA(sel, bar).forEach(b => b.classList.toggle('on', b === btn));
    const setSize = s => { frame.classList.toggle('s1024', s==='1024'); frame.classList.toggle('phone', s==='phone'); frame.classList.toggle('w1440', s==='1440'); frame.dataset.size = s; };
    const rerender = () => { if (opts.onState) opts.onState(frame.dataset.state || (states[0] && states[0].id)); if (frame.dataset.ky) applyKy(true); };
    bar.addEventListener('click', e => {
      const b = e.target.closest('button'); if (!b) return;
      if (b.dataset.t){ frame.dataset.theme = b.dataset.t; sw('[data-t]', b); }
      else if (b.dataset.th){ frame.dataset.site = b.dataset.th; sw('[data-th]', b); rerender(); }
      else if (b.dataset.s){ setSize(b.dataset.s); sw('[data-s]', b); rerender(); }
      else if ('ky' in b.dataset){ b.classList.toggle('on'); frame.dataset.ky = b.classList.contains('on') ? '1' : ''; applyKy(!!frame.dataset.ky); }
      else if ('keys' in b.dataset){ b.classList.toggle('on'); document.body.classList.toggle('show-keys', b.classList.contains('on')); }
      else if (b.dataset.st){ sw('[data-st]', b); frame.dataset.state = b.dataset.st; rerender(); }
    });
    const qs = new URLSearchParams(location.search);   /* ?theme=dark&size=phone&ky=1&site=cl_street#state — для снимков */
    if (qs.get('theme') === 'dark'){ frame.dataset.theme = 'dark'; sw('[data-t]', Q('[data-t="dark"]', bar)); }
    const sz = qs.get('size'); if (sz && sizes.includes(sz)){ setSize(sz); sw('[data-s]', Q('[data-s="'+sz+'"]', bar)); } else setSize(sizes[0]);
    if (qs.get('site') && byCode(qs.get('site'))){ frame.dataset.site = qs.get('site'); sw('[data-th]', Q('[data-th="'+qs.get('site')+'"]', bar)); } else if (opts.theme) frame.dataset.site = opts.theme;
    if (qs.get('ky')){ frame.dataset.ky = '1'; Q('[data-ky]', bar).classList.add('on'); }
    if (qs.get('keys') && Q('[data-keys]', bar)){ document.body.classList.add('show-keys'); Q('[data-keys]', bar).classList.add('on'); }
    const first = (location.hash || '').slice(1);
    const init = states.find(s => s.id === first) ? first : (states[0] && states[0].id);
    if (init){ QA('[data-st]', bar).forEach(x => x.classList.toggle('on', x.dataset.st === init)); frame.dataset.state = init; }
    rerender();
  }

  /* масштабированная витрина внутри контейнера (предпросмотр в редакторе, слайды Маркетплейса) */
  function scaled(th, w, h, opts){
    opts = opts || {}; const ph = !!opts.phone; const sw = ph ? 390 : (opts.w || 1296), sh = ph ? 844 : (opts.h || 879);
    const k = Math.min(w / sw, h / sh);
    return '<div class="scaled-wrap" style="width:'+w+'px;height:'+h+'px;display:flex;justify-content:center;background:'+(opts.bg||'transparent')+'">'+
      '<div style="position:relative;width:'+Math.round(sw*k)+'px;height:'+Math.round(sh*k)+'px;overflow:hidden">'+
      storefront(th, { phone: ph, state: opts.state, style:'transform:scale('+k.toFixed(4)+');' })+
      '</div></div>';
  }

  window.addEventListener('error', e => { const f = document.getElementById('frame');
    if (f) f.insertAdjacentHTML('afterbegin', '<pre style="color:#B91C1C;padding:20px;white-space:pre-wrap;font:13px monospace">Ошибка макета: ' + e.message + ' (' + (e.filename || '').split('/').pop() + ':' + e.lineno + ')</pre>'); });
  window.ST = { THEMES, KEYS, byCode, icon, t, fmt, money, KY, applyKy, toolbar, storefront, mini, scaled, vars, contrastRows, cr, photo, placeholder, CLR, Q, QA };
})();
