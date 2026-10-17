# Зависимости

- WPF-UI 4.3.0 — MIT, https://github.com/lepoco/wpfui
- .NET 8 / System.Security.Cryptography.ProtectedData 8.0.0 — MIT, https://github.com/dotnet/runtime
- AndroidX / Compose — Apache-2.0, https://github.com/androidx/androidx
- Kotlin 2.2.20 — Apache-2.0, https://github.com/JetBrains/kotlin
- Android platform-tools 36.0.2 — распространяется по условиям Android SDK;
  NOTICE.txt кладётся рядом с adb. https://developer.android.com/tools/releases/platform-tools
- Inno Setup 6.4.3 — собственная лицензия, https://github.com/jrsoftware/issrc

Версии Gradle, плагинов, библиотек и platform-tools зафиксированы. Транзитивные
зависимости приложений сохранены в Gradle/NuGet lockfiles. Actions закреплены SHA,
.NET SDK — global.json. Inno Setup 6.4.3 загружается из официального Release с
проверкой SHA-256 (`f3c42116542c4cc57263c5ba6c4feabfc49fe771f2f98a79d2f7628b8762723b`).
Hosted runner остаётся обновляемым образом, а подпись APK содержит временные
данные: побитовая идентичность артефактов не заявляется.
Установщик не имеет подписи Authenticode: Windows может показать SmartScreen.
FFmpeg не распространяется: выбран Media Foundation Windows, декодер пока не реализован.
