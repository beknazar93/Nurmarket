import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// 2026-10-21: ключ выпуска мобилки (nurmarket-mobile.keystore у владельца). Путь, alias и пароли —
// в android/key.properties, он не коммитится. Нет файла — release подписывается отладочным ключом,
// чтобы сборка у других не ломалась.
val keystorePropertiesFile = rootProject.file("key.properties")
val keystoreProperties = Properties().apply {
    if (keystorePropertiesFile.exists()) keystorePropertiesFile.inputStream().use { load(it) }
}

// 2026-11-05 (аудит №3, М4-08): сборка выпуска (assemble/bundle … Release) без key.properties раньше молча подписывалась отладочным ключом —
// такой APK выкладывали, и Android отказывал в установке поверх выпускного. Теперь сборка останавливается с понятным сообщением.
// Отладочные сборки (debug, flutter run) не затрагиваются. Сами ключи и пароли здесь не читаются и не печатаются.
gradle.taskGraph.whenReady {
    val releaseBuild = allTasks.any { it.name.matches(Regex("^(assemble|bundle|package)\\w*Release$")) }
    if (releaseBuild && !keystorePropertiesFile.exists()) {
        throw GradleException(
            "Нет android/key.properties — выпускную сборку нельзя подписать ключом выпуска (отладочным ключом не подписываем). " +
                "Положите key.properties (ключ nurmarket-mobile.keystore, файл у владельца) и повторите.",
        )
    }
}

android {
    namespace = "kg.nurcrm.nurmarket"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "kg.nurcrm.nurmarket"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        // ТЗ мобильного приложения, раздел 1: минимальный Android — 8.0 (API 26).
        minSdk = 26
        targetSdk = flutter.targetSdkVersion
        // Uses the version code from pubspec.yaml. When using split APKs, 1000 * ABI_VERSION
        // is added automatically by Flutter. (https://developer.android.com/studio/build/configure-apk-splits#configure-APK-versions)
        // You can force using the value of versionCode by specifying the `-P force-version-code-ignoring-abi=true`
        // flag during build.
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        if (keystorePropertiesFile.exists()) {
            create("release") {
                keyAlias = keystoreProperties["keyAlias"] as String
                keyPassword = keystoreProperties["keyPassword"] as String
                storeFile = file(keystoreProperties["storeFile"] as String)
                storePassword = keystoreProperties["storePassword"] as String
            }
        }
    }

    buildTypes {
        release {
            // 2026-10-21: ключ выпуска из key.properties, иначе отладочный (см. выше).
            signingConfig = if (keystorePropertiesFile.exists()) signingConfigs.getByName("release")
                else signingConfigs.getByName("debug")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
