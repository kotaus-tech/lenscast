plugins { id("com.android.application") }
android {
 namespace = "ru.lenscast.gesturefixture"
 compileSdk = 36
 buildToolsVersion = "36.0.0"
 defaultConfig { applicationId = "ru.lenscast.gesturefixture"; minSdk = 30; targetSdk = 36; versionCode = 1; versionName = "1" }
 signingConfigs { create("personal") { storeFile = file("../lenscast.keystore"); storePassword = "lenscast-personal"; keyAlias = "lenscast"; keyPassword = "lenscast-personal" } }
 buildTypes { getByName("release") { signingConfig = signingConfigs.getByName("personal") } }
 compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
}
