# Зависимости

- WPF-UI 4.3.0 — MIT, https://github.com/lepoco/wpfui
- .NET 8 / System.Security.Cryptography.ProtectedData 8.0.0 — MIT, https://github.com/dotnet/runtime
- AndroidX / Compose — Apache-2.0, https://github.com/androidx/androidx
- Kotlin 2.2.20 — Apache-2.0, https://github.com/JetBrains/kotlin
- Android platform-tools 36.0.2 — распространяется по условиям Android SDK;
  NOTICE.txt кладётся рядом с adb. https://developer.android.com/tools/releases/platform-tools
- Inno Setup 6 — собственная лицензия, https://github.com/jrsoftware/issrc

Прямые версии Gradle, плагинов, библиотек и platform-tools зафиксированы.
Runner и Actions пока обновляются по major-тегам; для строгой побитовой
воспроизводимости ещё нужны SHA Actions, lockfiles и контрольные суммы загрузок.
Установщик не имеет подписи Authenticode: Windows может показать SmartScreen.
FFmpeg не распространяется: выбран Media Foundation Windows.
