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

- Изолированный сервер adb: флаги `-P` (порт сервера) и `kill-server` проверены
  в официальном AOSP `platform_system_core/adb/commandline.cpp`, ref android-8.1.0_r1.
  Lenscast использует 5038, не останавливая общий adb на 5037. PrepareToInstall/Exec
  сверены с официальной справкой Inno Setup `topic_scriptevents.htm` и
  `topic_isxfunc_exec.htm`. Остановка собственного adb до замены/удаления файлов
  проверяется установочным smoke-тестом.

## Исправления обнаружения 0.1.1

- UDP JoinMulticastGroup(IPAddress, IPAddress), ReceiveAsync(CancellationToken) и
  SendAsync(ReadOnlyMemory<byte>, IPEndPoint, CancellationToken) повторно сверены
  с официальным dotnet/runtime `System.Net.Sockets/.../UDPClient.cs`.
- MulticastInterface задаётся IPv4-адресом конкретного NIC. Запрос с 5353 содержит
  QU-бит класса; отдельный ephemeral-сокет принимает legacy unicast ответы.
  Основания: RFC 6762 §§5.4/6.7/15. Повтор запроса раз в секунду, не при каждом
  постороннем пакете. Пакеты PTR/SRV/A проверяются реальным UDP-тестом с потерей
  первого запроса и раздельными записями.
- `adb devices -l`, состояния device/unauthorized/offline, общий сервер 5037 и
  Windows native backend сверены с developer.android.com/tools/adb. Не добавляем
  случайные USB-драйверы и не убиваем общий сервер для «исправления» обнаружения.
- Inno: ExtractTemporaryFile, PrepareToInstall и Exec проверены в официальной
  справке jrsoftware.org. Windows Get-Process/Path, Stop-Process и Wait-Process
  используются только для adb по точному пути этой установки, с проверкой в CI
  сохранности процесса из другой папки.
- netsh advfirewall firewall add/delete rule сверены с Microsoft Learn;
  правило только для исполняемого файла Lenscast, UDP с remoteport=5353,
  remoteip=LocalSubnet, profile=private. Глобальные настройки firewall не меняются.
- Адрес телефона: ConnectivityManager.getAllNetworks/getNetworkCapabilities/
  getLinkProperties и LinkProperties.getLinkAddresses сверены с официальным AOSP
  platform_frameworks_base, ref android-11.0.0_r1, core/java/android/net.
- Реальный APK дополнительно проверяется в Android Emulator API 30/36. Входы
  api-level/target/arch/profile/script сверены с action.yml android-emulator-runner
  v2; action закреплён SHA. Скрипт проверяет FGS, KeyStore/TLS, код и токен без
  вывода секретов. Эмулятор не доказывает работу OEM USB-драйвера или реального mDNS.
