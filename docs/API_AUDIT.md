# Основания для API и проверки

Никакие системные функции блоков Б–Г пока не реализованы. Ниже источники для
реализованного кода; реальные сигнатуры дополнительно проверяются компиляцией
Android SDK и .NET SDK в Actions. Загруженные исходники не включаются в продукт.

| Узел | Официальный источник / проверенный пример |
|---|---|
| TLS .NET | dotnet/runtime: `src/libraries/System.Net.Security/src/System/Net/Security/SslStream.cs`, blob `db932eba59193af6ce4afeda7b5a3d42fb98ff9b`; `SslClientAuthenticationOptions.cs`, blob `16700e0598dad18d39e2194becf8193ecba6bd9d` |
| Ключ Android | aosp-mirror/platform_frameworks_base: `keystore/java/android/security/keystore/KeyGenParameterSpec.java`, blob `3cff9159793916c3c67fe608b41f7bbfce7e6973` |
| Аккумулятор | тот же репозиторий: `core/java/android/os/BatteryManager.java`, blob `b63ad5f0148ee8acb90ccef1cb7f788b9e41d123` |
| WPF-UI | lepoco/wpfui: `samples/Wpf.Ui.Demo.Simple/MainWindow.xaml`, blob `ba09c4e6e358451b80cf9ec0994bef51b9987378`; пакет 4.3.0 |
| NSD | https://developer.android.com/develop/connectivity/wifi/use-nsd ; сигнатуры проверяет компилятор SDK |
| connectedDevice FGS | https://developer.android.com/develop/background-work/services/fgs/service-types#connected-device |
| DPAPI | https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata |
| DNS-SD | RFC 6762 / 6763: PTR запрос, SRV/A ответы; ограниченный парсер с защитой циклов и границ |
| USB | https://developer.android.com/tools/adb#forwardports |

Выбор Windows-декодера: Media Foundation (блок Б), чтобы не добавлять FFmpeg и
вторую нативную DLL. Нативная DLL будет только у виртуальной камеры (блок Г).
Для блока Г обязательны Windows-Camera/Samples/VirtualCamera, smourier/VCamSample
и заголовки установленного Windows SDK; их исследование ещё впереди.

Среда агента — Linux без Java и .NET. Локальная сборка невозможна; единственный
источник утверждения «собрано» — успешные jobs Actions, не наличие исходников.
SDK Android пока зафиксирован на 36; проверка более нового стабильного SDK и
соответствующее обновление нужны до объявления блока А завершённым.
