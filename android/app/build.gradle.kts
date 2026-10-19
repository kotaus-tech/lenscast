plugins { id("com.android.application"); id("org.jetbrains.kotlin.android"); id("org.jetbrains.kotlin.plugin.compose") }
android {
 namespace = "ru.lenscast"
 compileSdk = 36
 buildToolsVersion = "36.0.0"
 defaultConfig { applicationId = "ru.lenscast"; minSdk = 30; targetSdk = 36; versionCode = 6; versionName = "0.3.0" }
 signingConfigs { create("personal") { storeFile = file("../lenscast.keystore"); storePassword = "lenscast-personal"; keyAlias = "lenscast"; keyPassword = "lenscast-personal" } }
 buildTypes { getByName("release") { signingConfig = signingConfigs.getByName("personal"); isMinifyEnabled = false } }
 buildFeatures { compose = true }
 compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
 kotlinOptions { jvmTarget = "17" }
}
dependencies {
 testImplementation("junit:junit:4.13.2")
 implementation(platform("androidx.compose:compose-bom:2025.09.00"))
 implementation("androidx.activity:activity-compose:1.11.0")
 implementation("androidx.compose.material3:material3")
 implementation("androidx.compose.ui:ui")
 implementation("androidx.core:core-ktx:1.17.0")
}

dependencyLocking { lockAllConfigurations() }
