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
SDK Android зафиксирован на 36; основание выбора проверено повторно ниже.

## Повторный аудит 18 октября

- Официальная страница SDK Platform release notes проверена напрямую:
  https://developer.android.com/tools/releases/platforms — верхний стабильный
  раздел в полученном документе Android 16/API 36. compileSdk/targetSdk 36
  оставлены; по случайному номеру из стороннего репозитория SDK не повышался.
- NSD registerService и четыре RegistrationListener callbacks сверены с
  официальным примером `use-nsd` (раздел Register your service).
- Для connectedDevice сверены FOREGROUND_SERVICE_CONNECTED_DEVICE и разрешённая
  предпосылка CHANGE_NETWORK_STATE в официальной таблице FGS types.
- .NET UDP SendAsync(ReadOnlyMemory<byte>, IPEndPoint, CancellationToken) сверено
  с dotnet/runtime `src/libraries/System.Net.Sockets/src/System/Net/Sockets/UDPClient.cs`.
- Одноразовые mDNS-запросы с ephemeral-порта — RFC 6762 §6.7, Legacy Unicast
  Responses. Не требуется занимать UDP 5353 рядом с системным mDNS Windows.
- Android-приём намеренно **не использует available()**: это не индикатор
  наличия зашифрованных данных. Idle timeout допустим до первого байта заголовка;
  после первого байта любой тайм-аут закрывает сеанс. Добавлены JVM unit-тесты.

- Причина ошибки Mica подтверждена **исходником версии пакета 4.3.0**:
  https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/FluentWindow/FluentWindow.cs
  (`OnBackdropTypeChanged`, проверка `!ExtendsContentIntoTitleBar`). ui:TitleBar
  и ExtendsContentIntoTitleBar взяты из Demo.Simple этой же версии.

- Фиолетовый акцент Windows задаётся через ApplicationAccentColorManager.Apply
  (Color, ApplicationTheme), сигнатура проверена в исходнике версии 4.3.0.
